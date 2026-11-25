using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace LecteurWord.Flow;

internal sealed partial class TypographySession
{
    sealed class ShapingFace : IDisposable
    {
        readonly Blob blob;
        readonly Face nativeFace;
        internal HbFont Font { get; }
        internal SKTypeface SkiaFace { get; }
        internal int UnitsPerEm { get; }
        internal int GlyphCount => nativeFace.GlyphCount;
        internal int Bytes { get; }
        byte[]? cmap;
        internal byte[] Cmap => cmap??=SkiaFace.GetTableSize(0x636d6170) is >0 and <=1_048_576?
            SkiaFace.GetTableData(0x636d6170)??[]:[];
        byte[]? os2;
        internal byte[] Os2 => os2??=SkiaFace.GetTableSize(0x4f532f32) is >=26 and <=4096?
            SkiaFace.GetTableData(0x4f532f32)??[]:[];

        internal int FirstSymbol
        {
            get
            {
                // In Windows symbol fonts this field is the code corresponding to byte 0x20.
                int first=Os2.Length>=66?BinaryPrimitives.ReadUInt16BigEndian(Os2.AsSpan(64)):0;
                return first is >=0xe000 and <=0xf820?first:0xf020;
            }
        }

        internal unsafe ShapingFace(FontResolution resolution)
        {
            var data=resolution.Data??throw new InvalidDataException("TEXT_FACE_WITHOUT_SNAPSHOT");
            Bytes=data.Bytes.Length;
            fixed(byte* bytes=data.Bytes)
                blob=new((nint)bytes,data.Bytes.Length,MemoryMode.Duplicate);
            try
            {
                nativeFace=new(blob,data.Index);
                try
                {
                    UnitsPerEm=nativeFace.UnitsPerEm;
                    if(UnitsPerEm is <16 or >16384||nativeFace.GlyphCount is <1 or >65535)
                        throw new InvalidDataException("TEXT_FACE_METRICS");
                    Font=new(nativeFace);
                    try
                    {
                        Font.SetFunctionsOpenType();
                        Font.SetScale(UnitsPerEm,UnitsPerEm);
                        SkiaFace=FontResolver.Open(resolution);
                    }
                    catch { Font.Dispose(); throw; }
                }
                catch { nativeFace.Dispose(); throw; }
            }
            catch { blob.Dispose(); throw; }
        }

        public void Dispose()
        {
            SkiaFace.Dispose();
            Font.Dispose();
            nativeFace.Dispose();
            blob.Dispose();
        }
    }

    static SegmentKey? FitScript(SegmentKey key,ShapingFace face)
    {
        if(key.Style.VerticalAlignment=="baseline")return key;
        if(face.Os2 is not {Length:>=26} metrics)return null;
        bool superscript=key.Style.VerticalAlignment=="superscript";
        // The OS/2 table expresses vertical size and displacement in design units.
        int size=BinaryPrimitives.ReadInt16BigEndian(metrics.AsSpan(superscript?20:12));
        int offset=BinaryPrimitives.ReadInt16BigEndian(metrics.AsSpan(superscript?24:16));
        if(size<=0||size>face.UnitsPerEm||offset<0||offset>face.UnitsPerEm)return null;
        float scale=key.Size/face.UnitsPerEm;
        return key with{FittedSize=size*scale,
            Style=key.Style with{BaselineShift=key.Style.BaselineShift+(superscript?offset:-offset)*scale}};
    }

    ShapingFace GetFace(FontResolution font)
    {
        var key=(font.Sha256,font.FaceIndex);
        if(faces.TryGetValue(key,out var cached))return cached;
        Check();
        if(faces.Count>=64||faces.Values.Sum(f=>(long)f.Bytes)+(font.Data?.Bytes.Length??0)>32*1024*1024)
            throw new InvalidDataException("TEXT_NATIVE_FACE_LIMIT");
        var created=new ShapingFace(font);
        faces.Add(key,created);
        return created;
    }

    TextMeasure Shape(string paragraph,string context,int offset,int end,SegmentKey key,FontResolution font,
        ImmutableArray<string> features,string origin,int[] graphemeStarts)
    {
        var cacheKey=(paragraph,offset,end-offset,font.Sha256,font.FaceIndex);
        if(shapes.TryGetValue(cacheKey,out var cached))
        {
            shapeHits++;
            CountGlyphs(cached.Glyphs.Length);
            return cached;
        }

        long timestamp=Stopwatch.GetTimestamp();
        try
        {
            var result=ShapeCore(GetFace(font),context,offset,end,key.Level,key.Language,key.Script,key.Size,
                key.Style,features,graphemeStarts,Check,CountGlyphs,
                ()=>Issue("TEXT_NOTDEF",origin,"HarfBuzz a produit un glyphe zéro. La couverture n’est pas complète."));
            if(shapes.Count<512&&cachedGlyphs+result.Glyphs.Length<=64000)
            {
                shapes.Add(cacheKey,result);
                cachedGlyphs+=result.Glyphs.Length;
            }
            return result;
        }
        finally { shapeTicks+=Stopwatch.GetTimestamp()-timestamp; }
    }

    TextMeasure ShapeCore(ShapingFace face,string context,int offset,int end,byte level,string language,string script,
        float size,TextStyle style,ImmutableArray<string> features,int[] graphemeStarts,Action check,Action<int> count,Action missing)
    {
        string displayContext=context;
        if(style.Caps||style.SmallCaps)
        {
            string original=context[offset..end];
            string display=CapitalDisplay(original,language)??throw new InvalidDataException("TEXT_CAPITAL_MAPPING_UNQUALIFIED");
            if(display!=original)
                displayContext=string.Concat(context.AsSpan(0,offset),display,context.AsSpan(end));
        }
        using var buffer=new HbBuffer
        {
            Direction=(level&1)==0?Direction.LeftToRight:Direction.RightToLeft,
            Language=new Language(language),
            Script=HarfBuzzSharp.Script.Parse(script),
            ClusterLevel=ClusterLevel.MonotoneGraphemes,
            Flags=(offset==0?BufferFlags.BeginningOfText:0)|(end==context.Length?BufferFlags.EndOfText:0)
        };
        buffer.AddUtf16(displayContext,offset,end-offset);
        face.Font.Shape(buffer,[..features.Select(Feature.Parse)]);
        check();
        count(buffer.Length);
        var infos=buffer.GlyphInfos;
        var positions=buffer.GlyphPositions;
        if(infos.Length!=positions.Length||infos.Length!=buffer.Length)
            throw new InvalidDataException("TEXT_SHAPING_RESULT");
        var glyphs=ImmutableArray.CreateBuilder<TextGlyph>(infos.Length);
        var clusterData=new SortedDictionary<int,(int First,int Count,bool Missing,bool Unsafe)>();
        double scale=(double)size/face.UnitsPerEm;
        double scaleX=scale*(style.HorizontalScale/100d);
        double x=0,y=0;
        TextInk? ink=null;
        for(int i=0;i<infos.Length;i++)
        {
            check();
            var info=infos[i];
            var position=positions[i];
            int raw=checked((int)info.Cluster)-offset;
            if(raw<0||raw>=end-offset||raw>0&&char.IsLowSurrogate(context[offset+raw])||info.Codepoint>ushort.MaxValue)
                throw new InvalidDataException("TEXT_CLUSTER_SOURCE");
            int found=Array.BinarySearch(graphemeStarts,raw);
            int cluster=graphemeStarts[found<0?~found-1:found];
            var prior=clusterData.GetValueOrDefault(cluster,(First:i,Count:0,Missing:false,Unsafe:false));
            if(prior.First+prior.Count!=i)throw new InvalidDataException("TEXT_CLUSTER_ORDER");
            clusterData[cluster]=(prior.First,prior.Count+1,prior.Missing||info.Codepoint==0,
                prior.Unsafe||(info.GlyphFlags&GlyphFlags.UnsafeToBreak)!=0);
            double px=x+position.XOffset*scaleX,py=y-position.YOffset*scale-style.BaselineShift;
            double ax=position.XAdvance*scaleX,ay=-position.YAdvance*scale;
            // Tracking applies at a drawable cluster boundary. Combining and ligature interiors stay intact.
            if(i+1==infos.Length || ClusterAt(i+1)!=cluster)ax+=style.Tracking;
            TextInk? glyphInk=GlyphInk(face,info.Codepoint,scaleX,scale,px,py);
            if(glyphInk is {} box)ink=Union(ink,box);
            glyphs.Add(new((ushort)info.Codepoint,cluster,Emu(px),Emu(py),Emu(x+ax)-Emu(x),Emu(y+ay)-Emu(y)) {HarfBuzzOffset=raw,Ink=glyphInk});
            x+=ax;
            y+=ay;

            int ClusterAt(int index)
            {
                int local=checked((int)infos[index].Cluster)-offset;
                if(local<0||local>=end-offset)throw new InvalidDataException("TEXT_CLUSTER_SOURCE");
                int search=Array.BinarySearch(graphemeStarts,local);
                return graphemeStarts[search<0?~search-1:search];
            }
        }

        if(!clusterData.ContainsKey(0))
        {
            int until=clusterData.Count==0?end-offset:clusterData.First().Key;
            foreach(var scalar in context.AsSpan(offset,until).EnumerateRunes())
                if(!Ignorable(scalar.Value))throw new InvalidDataException("TEXT_UNMAPPED_CLUSTER");
            clusterData.Add(0,(-1,0,false,false));
        }
        var clusters=ImmutableArray.CreateBuilder<TextCluster>(clusterData.Count);
        var boundaries=clusterData.Keys.Append(end-offset).ToArray();
        int boundaryIndex=0;
        foreach(var cluster in clusterData)
        {
            int finish=boundaries[++boundaryIndex];
            var data=cluster.Value;
            clusters.Add(new(new(cluster.Key,finish),data.First,data.Count,data.Missing,data.Unsafe));
            if(data.Missing)missing();
        }

        return new TextMeasure(glyphs.MoveToImmutable(),clusters.MoveToImmutable(),
            Metrics(face,scale,x,ink));
    }

    static TextInk? GlyphInk(ShapingFace face,uint glyph,double scaleX,double scaleY,double x,double y)
    {
        if(!face.Font.TryGetGlyphExtents(glyph,out var bounds))return null;
        long left=Emu(x+bounds.XBearing*scaleX),right=Emu(x+(bounds.XBearing+bounds.Width)*scaleX);
        long top=Emu(y-bounds.YBearing*scaleY),bottom=Emu(y-(bounds.YBearing+bounds.Height)*scaleY);
        return left==right||top==bottom?null:new(Math.Min(left,right),Math.Min(top,bottom),Math.Max(left,right),Math.Max(top,bottom));
    }

    static TextMetrics Metrics(ShapingFace face,double scale,double width,TextInk? ink)
    {
        if(!face.Font.TryGetHorizontalFontExtents(out var metrics))throw new InvalidDataException("TEXT_FACE_EXTENTS");
        long ascent=Emu(Math.Max(0,metrics.Ascender*scale)),descent=Emu(Math.Max(0,-metrics.Descender*scale));
        return new(Emu(width),ascent,descent,Emu(Math.Max(0,metrics.LineGap*scale)),ascent,ink);
    }

    TextFragment MeasureSymbol(string paragraph,int offset,string original,int code,SegmentKey key,Action check,Action<int> count,
        bool packedWordSymbol=false,bool sourceGlyph=false)
    {
        FontResolution? chosen=null;
        TextMeasure? measure=null;
        var decision=new LegacySymbolDecision(key.Family,code,null,"unresolved","Aucune face ou correspondance vérifiée.");
        string? rejected=null;
        bool ambiguousEncoding=false;
        bool invalidEncoding=false;
        if(key.CanShape&&!(key.Request?.Theme==true&&key.Theme is null))
        {
            foreach(var resolution in selection.SymbolFaces(key.Family,key.Bold,key.Italic))
            {
                check();
                ShapingFace face;
                try{face=GetFace(resolution);}
                catch(InvalidDataException error) when(error.Message is "TEXT_FACE_METRICS" or "LAYOUT_FONT_INVALID" or "LAYOUT_FONT_IDENTITY_MISMATCH")
                {rejected="Face demandée inexploitable. "+error.Message;continue;}
                if(face.SkiaFace.GetTableSize(0x66766172)>0)continue;
                uint glyph=HistoricalGlyph(face,code,packedWordSymbol,check,out var encodingStatus);
                if(encodingStatus==SymbolCmap.Status.Invalid)
                {rejected="Table cmap symbolique invalide.";invalidEncoding=true;continue;}
                if(encodingStatus==SymbolCmap.Status.Ambiguous)
                {
                    rejected="Table cmap symbolique avec deux glyphes pour le même octet.";
                    ambiguousEncoding=true;
                    break;
                }
                if(glyph==0)continue;
                if(FitScript(key,face) is not {} fitted)
                {
                    rejected="Métriques d’indice ou d’exposant de la face hors profil.";
                    continue;
                }
                key=fitted;
                chosen=resolution;
                measure=MeasureGlyph(face,glyph);
                string encoding=encodingStatus==SymbolCmap.Status.Present?"cmap OpenType 3/0, format 4.":"cmap Unicode de la face utilisée.";
                decision=new(key.Family,code,null,resolution.Status=="substituted"?"declared-alternate":resolution.Status,
                    resolution.Status=="substituted"?resolution.Reason+" "+encoding:encoding);
                break;
            }
            if(measure is null&&!ambiguousEncoding&&LegacySymbols.Mapping(key.Family,code) is {} mapping)
            {
                var fallback=mapping.Face=="DejaVu Serif"?selection.Bundled(key.Bold,key.Italic):
                    selection.ExactInstalled(mapping.Face,key.Bold,key.Italic);
                bool regularFallback=fallback is null&&(key.Bold||key.Italic);
                if(regularFallback)fallback=selection.ExactInstalled(mapping.Face,false,false);
                if(fallback is not null&&GetFace(fallback) is {} face&&face.SkiaFace.GetTableSize(0x66766172)==0&&
                    face.Font.TryGetGlyph(mapping.Code,out uint glyph)&&glyph>0&&glyph<face.GlyphCount&&glyph<face.SkiaFace.GlyphCount&&
                    FitScript(key,face) is {} fitted)
                {
                    key=fitted;
                    chosen=fallback with {RequestedFamily=key.Family,Status="substituted",Installed=FontResolver.IsInstalled(key.Family),
                        Reason=(rejected is null?"":rejected+" ")+$"Correspondance documentée {key.Family} U+{code:X4} → U+{mapping.Code:X4}."+
                            (regularFallback?" Variante demandée indisponible dans la face libre, glyphe régulier utilisé sans synthèse.":"")};
                    measure=MeasureGlyph(face,glyph);
                    decision=new(key.Family,code,mapping.Code,"documented",mapping.Source);
                }
                else decision=decision with {Evidence="Correspondance publiée, face libre couvrante indisponible. "+mapping.Source};
            }
        }
        if(measure is null&&rejected is not null)decision=decision with {Evidence=rejected+" "+decision.Evidence};
        bool choiceUnavailable=false;
        if(measure is null&&sourceGlyph&&key.CanShape&&historicalChoices is not null)
        {
            var choice=historicalChoices.FirstOrDefault(c=>c.Valid&&c.Code==code&&c.Packed==packedWordSymbol&&
                c.Family.Equals(key.Family,StringComparison.OrdinalIgnoreCase));
            if(choice is not null)
            {
                choiceUnavailable=true;
                foreach(var substitute in selection.ExactFaces(choice.SelectedFamily,key.Bold,key.Italic))
                {
                    check();
                    var face=GetFace(substitute);
                    uint glyph=HistoricalGlyph(face,choice.SelectedCode,false,check,out _);
                    if(glyph==0||FitScript(key,face) is not {} fitted)continue;
                    key=fitted;
                    chosen=substitute with{RequestedFamily=key.Family,Status="user-substitution",
                        Reason="Substitution choisie par l’utilisateur pour ce document."};
                    measure=MeasureGlyph(face,glyph);
                    decision=new(key.Family,code,choice.SelectedCode,"user",chosen.Reason);
                    break;
                }
            }
        }
        if(measure is null&&reserveMissingSymbols&&sourceGlyph&&key.CanShape&&!invalidEncoding&&LegacySymbols.Private(code))
        {
            var fallback=selection.Bundled(key.Bold,key.Italic);
            var face=GetFace(fallback);
            if(face.Font.TryGetGlyph(0x25a1,out uint glyph)&&glyph>0&&FitScript(key,face) is {} fitted)
            {
                key=fitted;
                string detail=decision.Evidence+(choiceUnavailable?" La substitution sélectionnée ne couvre pas cette occurrence.":"");
                chosen=fallback with{RequestedFamily=key.Family,Status="missing-glyph-marker",
                    Reason="Réservation d’un carré pour un glyphe absent."};
                measure=MeasureGlyph(face,glyph);
                decision=new(key.Family,code,null,"missing-marker",detail+" Un carré signale la position du caractère source.");
            }
        }
        return new(paragraph,offset,original,[],key.Script,key.Language,key.Level,key.Style,key.Slot,key.Family,
            key.Request,key.Theme?.Source,chosen is null?null:chosen with {Data=null},key.Size,[],measure,[])
        {
            GraphemeStarts=[0],LegacySymbol=decision,
            MeasurementIssue=measure is null?"TEXT_MISSING_FACE":null
        };

        TextMeasure MeasureGlyph(ShapingFace face,uint glyph)
        {
            check();count(1);
            double scale=(double)key.Size/face.UnitsPerEm;
            double scaleX=scale*(key.Style.HorizontalScale/100d);
            double width=face.Font.GetHorizontalGlyphAdvance(glyph)*scaleX+key.Style.Tracking;
            double y=-key.Style.BaselineShift;
            var ink=GlyphInk(face,glyph,scaleX,scale,0,y);
            return new([new((ushort)glyph,0,0,Emu(y),Emu(width),0){Ink=ink}],
                [new(new(0,original.Length),0,1,false,false)],Metrics(face,scale,width,ink));
        }
    }

    static uint HistoricalGlyph(ShapingFace face,int scalar,bool packed,Action check,out SymbolCmap.Status status)
    {
        status=SymbolCmap.Status.Invalid;
        if(face.SkiaFace.GetTableSize(0x66766172)>0)return 0;
        var mapping=SymbolCmap.Lookup(face.Cmap,scalar,face.GlyphCount,check,face.FirstSymbol);
        status=mapping.Status;
        uint glyph=mapping.Glyph;
        if(status==SymbolCmap.Status.Absent&&!face.Font.TryGetGlyph(scalar,out glyph))return 0;
        return glyph<face.GlyphCount&&glyph<face.SkiaFace.GlyphCount?glyph:0;
    }

    internal static byte[]? PreviewHistoricalGlyph(string family,int scalar,bool bold,bool italic)
    {
        using var selection=new FontResolver.FaceSelection(new DocumentFonts(),()=>{});
        if(!Rune.IsValid(scalar)||selection.ExactInstalled(family,bold,italic) is not {} resolution)return null;
        try
        {
            using var face=new ShapingFace(resolution);
            uint glyph=HistoricalGlyph(face,scalar,false,()=>{},out _);
            if(glyph==0)return null;
            using var font=new SKFont(face.SkiaFace,48);
            using var path=font.GetGlyphPath((ushort)glyph);
            using var bitmap=new SKBitmap(120,76);
            using var canvas=new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            if(path is {IsEmpty:false})
            {
                var bounds=path.TightBounds;
                float scale=Math.Min(1,Math.Min(100/bounds.Width,56/bounds.Height));
                canvas.Translate(60,38);
                canvas.Scale(scale);
                canvas.Translate(-bounds.MidX,-bounds.MidY);
                using var paint=new SKPaint{Color=SKColors.Black,IsAntialias=true};
                canvas.DrawPath(path,paint);
            }
            using var encoded=bitmap.Encode(SKEncodedImageFormat.Png,100);
            return encoded.ToArray();
        }
        catch(InvalidDataException){return null;}
    }

    internal long? SymbolSpaceAdvance(TextFragment fragment,Action check,Action<int> count)
    {
        check();
        if(fragment.Face is not {} resolution||!faces.TryGetValue((resolution.Sha256,resolution.FaceIndex),out var face))
            return null;
        var cmap=SymbolCmap.Lookup(face.Cmap,' ',face.GlyphCount,check,face.FirstSymbol);
        uint glyph=cmap.Glyph;
        if(cmap.Status is SymbolCmap.Status.Invalid or SymbolCmap.Status.Ambiguous||
            cmap.Status==SymbolCmap.Status.Absent&&!face.Font.TryGetGlyph(' ',out glyph)||glyph==0||glyph>=face.GlyphCount)
            return null;
        count(1);
        return Emu(face.Font.GetHorizontalGlyphAdvance(glyph)*(double)fragment.PointSize/face.UnitsPerEm*
            (fragment.Style.HorizontalScale/100d)+fragment.Style.Tracking);
    }

    internal TextMeasure ReshapeLine(TextFragment fragment,string line,int offset,int length,byte level,
        int[] graphemes,Action check,Action<int> count)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var identity=fragment.Face??throw new InvalidDataException("LINE_NO_FACE");
        if(!faces.TryGetValue((identity.Sha256,identity.FaceIndex),out var face))
            throw new InvalidDataException("LINE_FACE_LEASE");
        return ShapeCore(face,line,offset,offset+length,level,fragment.Language,fragment.Script,fragment.PointSize,
            fragment.Style,fragment.Features,graphemes,check,count,()=>{});
    }

    internal TextFragment? ShapeLineDecoration(string paragraph,string value,TextStyle style,SemanticSource source,
        byte level,Action check,Action<int> count)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        check();
        var token=new TextToken(source,null,paragraph,null,TextRole.Control,null,null,style);
        var key=Key(value,new Graph(0,value.Length,new ContextSlice(0,value.Length,token),level,"Latn",true),style);
        if(style.SmallCaps&&value.EnumerateRunes().Any(Rune.IsLower)&&
            value.EnumerateRunes().Any(r=>!Rune.IsLower(r)))
            return null;
        if(value.EnumerateRunes().Any(r=>LegacySymbols.Private(r.Value)))
        {
            if(!LegacySymbols.SingleScalar(value,out int code))return null;
            var symbol=MeasureSymbol(paragraph,0,value,code,key,check,count);
            return symbol.Measure is null?null:symbol with
            {Sources=[new(source with {Span=null},null,style,new(0,value.Length)){Role=TextRole.Control}]};
        }
        var resolution=Select(value,key);
        if(resolution is null)return null;
        var face=GetFace(resolution);
        if(FitScript(key,face) is not {} fitted)return null;
        key=fitted;
        var features=Features(key);
        var measure=ShapeCore(face,value,0,value.Length,level,key.Language,key.Script,key.Size,
            key.Style,features,[0],check,count,()=>{});
        if(measure.Clusters.Any(c=>c.MissingGlyph))return null;
        return new(paragraph,0,value,[new(source with {Span=null},null,style,new(0,value.Length)){Role=TextRole.Control}],
            key.Script,key.Language,level,key.Style,key.Slot,key.Family,key.Request,key.Theme?.Source,resolution with {Data=null},
            key.Size,features,measure,[]);
    }

    internal TextStyle ListMarkerStyle(TextParagraph paragraph,string levelId) =>
        model.ChildId(levelId,"rPr") is {} properties?styles.Resolve(paragraph.Id,true,properties):paragraph.MarkStyle;

    // Display-only substitution. HarfBuzz clusters continue to address the unchanged source.
    static string? CapitalDisplay(string text,string language)
    {
        CultureInfo culture;
        try { culture=CultureInfo.GetCultureInfo(language=="und"?"":language); }
        catch(CultureNotFoundException) { return null; }
        var display=new StringBuilder(text.Length);
        foreach(var rune in text.EnumerateRunes())
        {
            var upper=Rune.IsLower(rune)?Rune.ToUpper(rune,culture):rune;
            if(upper.Utf16SequenceLength!=rune.Utf16SequenceLength||Rune.IsLower(upper))
                return null;
            display.Append(upper.ToString());
        }
        return display.ToString();
    }

    void CountGlyphs(int count)
    {
        if(count<0||count>limits.MaxGlyphs-glyphCount)throw new InvalidDataException("TEXT_GLYPH_LIMIT");
        glyphCount+=count;
    }

    static long Emu(double value) => checked((long)Math.Round(value*EmuPerPoint,MidpointRounding.ToEven));
    static TextInk Union(TextInk? previous,TextInk next) => previous is not {} bounds?next:
        new(Math.Min(bounds.Left,next.Left),Math.Min(bounds.Top,next.Top),Math.Max(bounds.Right,next.Right),Math.Max(bounds.Bottom,next.Bottom));

    ImmutableArray<string> Features(SegmentKey key)
    {
        var result=ImmutableArray.CreateBuilder<string>();
        result.Add(key.Style.KernAt is {} threshold && key.Size>=threshold?"kern=1":"kern=0");
        string ligatures=key.Style.Ligatures;
        // Apply the MS-DOCX §2.6.1.17 default in the qualified Latin mode-14
        // profile. Required substitutions (rlig) remain part of shaping.
        if(ligatures!="default"||key.Script=="Latn"&&model.StyleCompatibility.Mode==14)
        {
            Set("liga","standard");Set("clig","contextual");Set("hlig","historical");Set("dlig","discretionary");
        }
        return result.ToImmutable();

        void Set(string tag,string name) => result.Add(tag+"="+(ligatures=="all"||ligatures.Contains(name,StringComparison.OrdinalIgnoreCase)?"1":"0"));
    }

    internal bool PermitsPdfEmbedding(FontResolution identity)
    {
        if(!faces.TryGetValue((identity.Sha256,identity.FaceIndex),out var face)||face.Os2.Length<10)return false;
        ushort rights=BinaryPrimitives.ReadUInt16BigEndian(face.Os2.AsSpan(8,2));
        return rights is 0 or 4 or 8;
    }

    internal SKTextBlob? TextBlob(TextFragment fragment)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(fragment.Sources.IsEmpty||fragment.Sources.Any(s=>s.Source.SnapshotId!=model.SnapshotId))
            throw new InvalidDataException("TEXT_PAINT_SOURCE");
        if(fragment.Face is not {} identity||fragment.Measure is not {} measured||measured.Glyphs.IsEmpty)return null;
        if(!faces.TryGetValue((identity.Sha256,identity.FaceIndex),out var face))
            throw new InvalidDataException("TEXT_PAINT_FACE");
        using var font=new SKFont(face.SkiaFace,fragment.PointSize) { Subpixel=true, Hinting=SKFontHinting.None, LinearMetrics=true,
            ScaleX=fragment.Style.HorizontalScale/100f };
        using var builder=new SKTextBlobBuilder();
        var ids=new ushort[measured.Glyphs.Length];
        var points=new SKPoint[ids.Length];
        for(int i=0;i<ids.Length;i++)
        {
            var glyph=measured.Glyphs[i];
            ids[i]=glyph.Id;
            points[i]=new((float)glyph.X/EmuPerPoint,(float)glyph.Y/EmuPerPoint);
        }
        builder.AddPositionedRun(ids,font,points);
        return builder.Build();
    }

    internal void Paint(SKCanvas canvas,TextFragment fragment,float x,float baseline,SKColor automaticColor,bool shadowOnly=false,SKColor? background=null,Func<SKColor,SKColor,SKColor>? ink=null)
    {
        using var blob=TextBlob(fragment);
        if(blob is null)return;
        if(shadowOnly)
        {
            if(fragment.Style.Shadow is not {Alpha:>0} shadow)return;
            using var mask=shadow.Blur>0?SKMaskFilter.CreateBlur(SKBlurStyle.Normal,shadow.Blur/MathF.Sqrt(3)+.5f):null;
            using var shadowPaint=new SKPaint{Color=SKColor.Parse(shadow.Color).WithAlpha(shadow.Alpha),IsAntialias=true,MaskFilter=mask};
            canvas.DrawText(blob,x+shadow.OffsetX,baseline+shadow.OffsetY,shadowPaint);
            return;
        }
        using var paint=new SKPaint
        {
            IsAntialias=true,
            Color=fragment.Style.Color=="auto"?automaticColor:SKColor.Parse(fragment.Style.Color)
        };
        if(ink is not null)paint.Color=ink(paint.Color,fragment.Style.Background is {} fill ? SKColor.Parse(fill) : background??SKColors.White);
        canvas.DrawText(blob,x,baseline,paint);
    }

    internal (long Top,long Bottom)? UnderlineInk(TextFragment fragment,bool useRaisedBaseline=false)
    {
        if(fragment.Face is not {} identity||!useRaisedBaseline&&fragment.Style.BaselineShift!=0||
            !faces.TryGetValue((identity.Sha256,identity.FaceIndex),out var face))return null;
        using var font=new SKFont(face.SkiaFace,fragment.PointSize);
        var metrics=font.Metrics;
        if(metrics.UnderlinePosition is not {} y||metrics.UnderlineThickness is not {} thickness||
            !float.IsFinite(y)||!float.IsFinite(thickness)||thickness<=0)return null;
        return (Emu(y-fragment.Style.BaselineShift-thickness/2),Emu(y-fragment.Style.BaselineShift+thickness/2));
    }

    // Diagnostic comparison on the identical face. It does not participate in shaping or painting.
    internal float LegacyAdvance(TextFragment fragment)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var identity=fragment.Face??throw new InvalidDataException("TEXT_PAINT_FACE");
        using var font=new SKFont(faces[(identity.Sha256,identity.FaceIndex)].SkiaFace,fragment.PointSize);
        return font.MeasureText(fragment.Text);
    }
}
