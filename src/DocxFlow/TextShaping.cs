using System.Collections.Immutable;
using System.Diagnostics;
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
        using var buffer=new HbBuffer
        {
            Direction=(level&1)==0?Direction.LeftToRight:Direction.RightToLeft,
            Language=new Language(language),
            Script=HarfBuzzSharp.Script.Parse(script),
            ClusterLevel=ClusterLevel.MonotoneGraphemes,
            Flags=(offset==0?BufferFlags.BeginningOfText:0)|(end==context.Length?BufferFlags.EndOfText:0)
        };
        buffer.AddUtf16(context,offset,end-offset);
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
            double px=x+position.XOffset*scale,py=y-position.YOffset*scale-style.BaselineShift;
            double ax=position.XAdvance*scale,ay=-position.YAdvance*scale;
            // Tracking applies at a drawable cluster boundary. Combining and ligature interiors stay intact.
            if(i+1==infos.Length || ClusterAt(i+1)!=cluster)ax+=style.Tracking;
            TextInk? glyphInk=GlyphInk(face,info.Codepoint,scale,px,py);
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

    static TextInk? GlyphInk(ShapingFace face,uint glyph,double scale,double x,double y)
    {
        if(!face.Font.TryGetGlyphExtents(glyph,out var bounds))return null;
        long left=Emu(x+bounds.XBearing*scale),right=Emu(x+(bounds.XBearing+bounds.Width)*scale);
        long top=Emu(y-bounds.YBearing*scale),bottom=Emu(y-(bounds.YBearing+bounds.Height)*scale);
        return left==right||top==bottom?null:new(Math.Min(left,right),Math.Min(top,bottom),Math.Max(left,right),Math.Max(top,bottom));
    }

    static TextMetrics Metrics(ShapingFace face,double scale,double width,TextInk? ink)
    {
        if(!face.Font.TryGetHorizontalFontExtents(out var metrics))throw new InvalidDataException("TEXT_FACE_EXTENTS");
        long ascent=Emu(Math.Max(0,metrics.Ascender*scale)),descent=Emu(Math.Max(0,-metrics.Descender*scale));
        return new(Emu(width),ascent,descent,Emu(Math.Max(0,metrics.LineGap*scale)),ascent,ink);
    }

    TextFragment MeasureSymbol(string paragraph,int offset,string original,int code,SegmentKey key,Action check,Action<int> count)
    {
        FontResolution? chosen=null;
        TextMeasure? measure=null;
        var decision=new LegacySymbolDecision(key.Family,code,null,"unresolved","Aucune face ou correspondance vérifiée.");
        string? rejected=null;
        if(key.CanShape&&!(key.Request?.Theme==true&&key.Theme is null))
        {
            foreach(var resolution in selection.ExactFaces(key.Family,key.Bold,key.Italic))
            {
                check();
                ShapingFace face;
                try{face=GetFace(resolution);}
                catch(InvalidDataException error) when(error.Message is "TEXT_FACE_METRICS" or "LAYOUT_FONT_INVALID" or "LAYOUT_FONT_IDENTITY_MISMATCH")
                {rejected="Face demandée inexploitable. "+error.Message;continue;}
                if(face.SkiaFace.GetTableSize(0x66766172)>0)continue;
                var cmap=SymbolCmap.Lookup(face.Cmap,code,face.GlyphCount,check);
                if(cmap.Status==SymbolCmap.Status.Invalid)
                {rejected="Table cmap symbolique invalide.";continue;}
                uint glyph=cmap.Glyph;
                if(cmap.Status==SymbolCmap.Status.Absent&&!face.Font.TryGetGlyph(code,out glyph))glyph=0;
                if(glyph==0||glyph>=face.GlyphCount||glyph>=face.SkiaFace.GlyphCount)continue;
                chosen=resolution;
                measure=MeasureGlyph(face,glyph);
                decision=new(key.Family,code,null,resolution.Status,
                    cmap.Status==SymbolCmap.Status.Present?"cmap OpenType 3/0, format 4.":"cmap Unicode de la face demandée.");
                break;
            }
            if(measure is null&&LegacySymbols.Mapping(key.Family,code) is {} mapping)
            {
                var fallback=mapping.Face=="DejaVu Serif"?selection.Bundled(key.Bold,key.Italic):
                    selection.ExactInstalled(mapping.Face,key.Bold,key.Italic);
                bool regularFallback=fallback is null&&(key.Bold||key.Italic);
                if(regularFallback)fallback=selection.ExactInstalled(mapping.Face,false,false);
                if(fallback is not null&&GetFace(fallback) is {} face&&face.SkiaFace.GetTableSize(0x66766172)==0&&
                    face.Font.TryGetGlyph(mapping.Code,out uint glyph)&&glyph>0&&glyph<face.GlyphCount&&glyph<face.SkiaFace.GlyphCount)
                {
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
            double width=face.Font.GetHorizontalGlyphAdvance(glyph)*scale+key.Style.Tracking;
            double y=-key.Style.BaselineShift;
            var ink=GlyphInk(face,glyph,scale,0,y);
            return new([new((ushort)glyph,0,0,Emu(y),Emu(width),0){Ink=ink}],
                [new(new(0,original.Length),0,1,false,false)],Metrics(face,scale,width,ink));
        }
    }

    internal long? SymbolSpaceAdvance(TextFragment fragment,Action check,Action<int> count)
    {
        check();
        if(fragment.Face is not {} resolution||!faces.TryGetValue((resolution.Sha256,resolution.FaceIndex),out var face))
            return null;
        var cmap=SymbolCmap.Lookup(face.Cmap,' ',face.GlyphCount,check);
        uint glyph=cmap.Glyph;
        if(cmap.Status==SymbolCmap.Status.Invalid||
            cmap.Status==SymbolCmap.Status.Absent&&!face.Font.TryGetGlyph(' ',out glyph)||glyph==0||glyph>=face.GlyphCount)
            return null;
        count(1);
        return Emu(face.Font.GetHorizontalGlyphAdvance(glyph)*(double)fragment.PointSize/face.UnitsPerEm+fragment.Style.Tracking);
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
        if(value.EnumerateRunes().Any(r=>LegacySymbols.Private(r.Value)))
        {
            if(!LegacySymbols.SingleScalar(value,out int code))return null;
            var symbol=MeasureSymbol(paragraph,0,value,code,key,check,count);
            return symbol.Measure is null?null:symbol with
            {Sources=[new(source with {Span=null},null,style,new(0,value.Length)){Role=TextRole.Control}]};
        }
        var resolution=Select(value,key);
        if(resolution is null)return null;
        var features=Features(key);
        var measure=ShapeCore(GetFace(resolution),value,0,value.Length,level,key.Language,key.Script,key.Size,
            style,features,[0],check,count,()=>{});
        if(measure.Clusters.Any(c=>c.MissingGlyph))return null;
        return new(paragraph,0,value,[new(source with {Span=null},null,style,new(0,value.Length)){Role=TextRole.Control}],
            key.Script,key.Language,level,style,key.Slot,key.Family,key.Request,key.Theme?.Source,resolution with {Data=null},
            key.Size,features,measure,[]);
    }

    internal TextStyle ListMarkerStyle(TextParagraph paragraph,string levelId) =>
        model.ChildId(levelId,"rPr") is {} properties?styles.Resolve(paragraph.Id,true,properties):paragraph.MarkStyle;

    void CountGlyphs(int count)
    {
        if(count<0||count>limits.MaxGlyphs-glyphCount)throw new InvalidDataException("TEXT_GLYPH_LIMIT");
        glyphCount+=count;
    }

    static long Emu(double value) => checked((long)Math.Round(value*EmuPerPoint,MidpointRounding.ToEven));
    static TextInk Union(TextInk? previous,TextInk next) => previous is not {} bounds?next:
        new(Math.Min(bounds.Left,next.Left),Math.Min(bounds.Top,next.Top),Math.Max(bounds.Right,next.Right),Math.Max(bounds.Bottom,next.Bottom));

    static ImmutableArray<string> Features(SegmentKey key)
    {
        var result=ImmutableArray.CreateBuilder<string>();
        result.Add(key.Style.KernAt is {} threshold && key.Size>=threshold?"kern=1":"kern=0");
        string ligatures=key.Style.Ligatures;
        if(ligatures!="default")
        {
            Set("liga","standard");Set("clig","contextual");Set("hlig","historical");Set("dlig","discretionary");
        }
        return result.ToImmutable();

        void Set(string tag,string name) => result.Add(tag+"="+(ligatures=="all"||ligatures.Contains(name,StringComparison.OrdinalIgnoreCase)?"1":"0"));
    }

    internal SKTextBlob? TextBlob(TextFragment fragment)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(fragment.Sources.IsEmpty||fragment.Sources.Any(s=>s.Source.SnapshotId!=model.SnapshotId))
            throw new InvalidDataException("TEXT_PAINT_SOURCE");
        if(fragment.Face is not {} identity||fragment.Measure is not {} measured||measured.Glyphs.IsEmpty)return null;
        if(!faces.TryGetValue((identity.Sha256,identity.FaceIndex),out var face))
            throw new InvalidDataException("TEXT_PAINT_FACE");
        using var font=new SKFont(face.SkiaFace,fragment.PointSize) { Subpixel=true, Hinting=SKFontHinting.None, LinearMetrics=true };
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

    internal void Paint(SKCanvas canvas,TextFragment fragment,float x,float baseline,SKColor automaticColor)
    {
        using var blob=TextBlob(fragment);
        if(blob is null)return;
        using var paint=new SKPaint
        {
            IsAntialias=true,
            Color=fragment.Style.Color=="auto"?automaticColor:SKColor.Parse(fragment.Style.Color)
        };
        canvas.DrawText(blob,x,baseline,paint);
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
