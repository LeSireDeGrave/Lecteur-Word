using System.Collections.Immutable;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

public static partial class Composer
{
    internal static PageLine? PlaceFloatingTextLine(FloatingTextFrame frame,LayoutParagraph paragraph,ComposedLine source)
    {
        try
        {
            var spacing=paragraph.Properties;
            var line=MeasureNative(paragraph,source);
            float height=line.Height+spacing.Before+spacing.After;
            float available=frame.Height-frame.Top-frame.Bottom;
            if(height>available+.01f)return null;
            float top=frame.Top+spacing.Before+(frame.Center?(available-height)/2:0);
            return PositionNative(line,paragraph,frame.Left,top,frame.Left,frame.Top,frame.Width-frame.Right,frame.Height-frame.Bottom);
        }
        catch(LayoutFailure){return null;}
    }

    internal static LayoutTable FitPageTable(LayoutTable table,Dictionary<string,LayoutParagraph> paragraphs,Action check,
        Func<LayoutParagraph,(float Minimum,float Preferred)> intrinsic)
    {
        var document=new PaginatedDocument(table.Cells.SelectMany(c=>c.ParagraphIds).Distinct().Select(id=>paragraphs[id]).ToArray(),[])
        {Tables=[table]};
        using var fonts=new LayoutFonts();
        try
        {
            var fit=FitTables(document,fonts,check,intrinsic);
            foreach(var p in fit.Paragraphs)paragraphs[p.Id]=p;
            return fit.Tables[0];
        }
        catch(LayoutFailure failure){throw new TableWidthFailure(failure.Message,failure.SourceLocation,failure.Detail);}
    }

    internal static Composition PlaceNativeLayout(PaginatedDocument document,
        IReadOnlyDictionary<string,ImmutableArray<ComposedLine>> source,LineComposer painter,Action check,Action firstPage,
        IReadOnlyList<PageSection>? paper=null,Func<LayoutParagraph,LineRegion,LineResume,ImmutableArray<ComposedLine>>? reflow=null,
        FootnoteLayout? footnotes=null,PageRegionReplay? replay=null,List<PageRegionCheckpoint>? checkpoints=null,
        IReadOnlySet<string>? regionalTables=null,Func<string,long,RegionalTable>? fitRegion=null,
        IReadOnlyDictionary<int,EndnoteGroup>? endnotes=null,bool completeBody=false,bool textBoxBorders=false)
    {
        document=ApplyContextualSpacing(document);
        var measured=new List<MeasuredParagraph>();
        try
        {
            if(textBoxBorders)document=JoinTextBoxBorders(document);
            foreach(var p in document.Paragraphs)
            {
                check();
                if(p.TableId is not null){measured.Add(new(p,[]));continue;}
                if(!source.TryGetValue(p.Id,out var lines))throw new LayoutFailure("PAGE_LINE_MISSING",p.Source);
                measured.Add(new(p,lines.Select(l=>MeasureNative(p,l)).ToImmutableArray()));
            }
            using var fonts=new LayoutFonts();
            var placed=PlaceMeasured(document,measured,fonts,check,native:true,firstPage:firstPage,paper:paper,
                reflow:reflow is null?null:(p,region,start)=>reflow(p,region,start).Select(l=>MeasureNative(p,l)).ToArray(),
                footnotes:footnotes,replay:replay,checkpoints:checkpoints,regionalTables:regionalTables,tableAt:fitRegion is null?null:TableAt,
                endnotes:endnotes,completeBody:completeBody,textBoxBorders:textBoxBorders);
            return placed with {Pages=placed.Pages.Select(p=>p.NativePainter==painter?p:p with {NativePainter=painter}).ToImmutableArray()};

            MeasuredTable TableAt(string id,long width)
            {
                var fitted=fitRegion!(id,width);
                var region=ApplyContextualSpacing(fitted.Layout);
                var cells=new Dictionary<string,MeasuredParagraph>(StringComparer.Ordinal);
                foreach(var paragraph in region.Paragraphs)
                {
                    check();
                    var start=source[paragraph.Id][0];
                    var bounds=new LineRegion(paragraph.CellOwner!,0,PageComposer.Emu(paragraph.Geometry.Width)-
                        PageComposer.Emu(paragraph.Geometry.Left)-PageComposer.Emu(paragraph.Geometry.Right))
                        {CenteredImageOverhang=PageComposer.Emu(paragraph.CenteredImageOverhang)};
                    var lines=reflow!(paragraph,bounds,start.Resume);
                    if(lines.Any(l=>l.Diagnostics.Any(issue=>issue.Code=="LINE_OVERFLOW")))
                        throw new LayoutFailure("LINE_OVERFLOW",paragraph.Source);
                    cells.Add(paragraph.Id,new(paragraph,lines.Select(l=>MeasureNative(paragraph,l)).ToImmutableArray()));
                }
                return MeasureTable(fitted.Table,cells,check);
            }
        }
        catch(LayoutFailure failure)
        {
            return new([], [new(failure.Message,failure.SourceLocation,"Contraintes de placement incompatibles. Provenance et texte conservés.",true)]);
        }
        catch(TableWidthFailure failure)
        {
            return new([], [new(failure.Message,failure.Location,failure.Detail,true)]);
        }
    }

    // ISO/IEC 29500-1 §17.3.1.4: compare the whole set on adjoining paragraphs.
    // The auxiliary compositor subsequently requires the whole story on one sheet.
    static PaginatedDocument JoinTextBoxBorders(PaginatedDocument document)
    {
        if(!document.Paragraphs.Any(p=>p.Properties.HasBorders))return document;
        var original=document.Paragraphs;
        var paragraphs=original.ToArray();
        for(int i=0;i<paragraphs.Length;i++)
        {
            var p=original[i];
            var props=p.Properties;
            if(!props.HasBorders||p.TableId is not null||p.CellOwner is not null)continue;
            bool fromPrevious=i>0&&Join(original[i-1],p);
            bool toNext=i+1<original.Count&&Join(p,original[i+1]);
            paragraphs[i]=p with{Properties=props with
            {
                TopBorder=fromPrevious?null:props.TopBorder,
                BottomBorder=toNext?props.BetweenBorder is {} between?between with{Space=props.After}:null:props.BottomBorder,
                ContinuesSideBorders=fromPrevious
            }};
        }
        return document with{Paragraphs=paragraphs};

        static bool Join(LayoutParagraph previous,LayoutParagraph next)
        {
            if(previous.TableId is not null||previous.CellOwner is not null||next.Properties.PageBreakBefore)return false;
            var a=previous.Properties;var b=next.Properties;
            if(!Same(a.TopBorder,b.TopBorder)||!Same(a.BottomBorder,b.BottomBorder)||
                !Same(a.LeftBorder,b.LeftBorder)||!Same(a.RightBorder,b.RightBorder)||!Same(a.BetweenBorder,b.BetweenBorder))
                return false;
            if(a.Left!=b.Left||a.Right!=b.Right||Math.Min(0,a.FirstLine)!=Math.Min(0,b.FirstLine))
                throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_GROUP_INDENT_UNQUALIFIED",next.Source);
            return true;
        }

        static bool Same(ParagraphBorderStroke? a,ParagraphBorderStroke? b)=>
            a is null?b is null:b is not null&&a==(b with{Source=a.Source});
    }

    internal static ComposedPage PlaceContinuousTable(LayoutTable table,
        IReadOnlyDictionary<string,LayoutParagraph> paragraphs,
        IReadOnlyDictionary<string,ImmutableArray<ComposedLine>> source,LineComposer painter,Action check)
    {
        var document=ApplyContextualSpacing(new PaginatedDocument(table.Cells.Where(c=>c.MergeOrigin is null)
            .SelectMany(c=>c.ParagraphIds).Select(id=>paragraphs[id]).ToArray(),[]));
        try
        {
            var measured=document.Paragraphs.ToDictionary(p=>p.Id,p=>
            {
                check();
                return new MeasuredParagraph(p,source[p.Id].Select(line=>MeasureNative(p,line)).ToImmutableArray());
            },StringComparer.Ordinal);
            var rows=MeasureTable(table,measured,check);
            var strokes=table.Cells.Where(c=>c.MergeOrigin is null).Select(c=>EffectiveBorders(table,c));
            float inset=strokes.Select(b=>Math.Max(Math.Max(b.Left?.Width??0,b.Right?.Width??0),
                Math.Max(b.Top?.Width??0,b.Bottom?.Width??0))).DefaultIfEmpty().Max()/2;
            var lines=new List<PageLine>();
            var cells=new List<PageCell>();
            using var fonts=new LayoutFonts();
            PlaceTableRows(rows,0,table.Rows.Count-1,inset,inset,false,lines,cells,fonts,check);
            ValidateCellBorders(cells,check);
            float width=table.Columns.Sum()+2*inset,height=rows.Heights.Sum()+2*inset;
            return new(0,lines){Cells=cells,Geometry=new(width,height,0,0,0,0),NativePainter=painter};
        }
        catch(LayoutFailure failure)
        {
            throw new InvalidDataException(failure.Message,failure);
        }
    }

    internal static SKPicture RecordContinuousTable(ComposedPage table)
    {
        using var recorder=new SKPictureRecorder();
        DrawStyledPage(recorder.BeginRecording(SKRect.Create(table.Geometry.Width,table.Geometry.Height)),table);
        return recorder.EndRecording();
    }

    static MeasuredLine MeasureNative(LayoutParagraph paragraph,ComposedLine line)
    {
        if(line.Height is not {} heightEmu||line.Ascent is not {} ascent||line.Descent is not {} descent||line.Occupied is not {} width)
            throw new LayoutFailure("PAGE_LINE_WITHOUT_MEASUREMENT",paragraph.Source);
        float natural=PageComposer.Points(heightEmu);
        var p=paragraph.Properties;
        float height=p.LineRule switch
        {
            "exact"=>p.Line/20,
            "atLeast"=>Math.Max(natural,p.Line/20),
            "auto"=>natural*p.Line/240,
            _=>throw new LayoutFailure("PAGE_LINE_SPACING_UNQUALIFIED",paragraph.Source)
        };
        bool clipped=p.LineRule=="exact"&&height<natural;
        if(!float.IsFinite(height)||height<=0||!clipped&&height<natural-.01f)
            throw new LayoutFailure("PAGE_LINE_SPACING_CLIPS",paragraph.Source);
        string text=string.Concat(line.Parts.Select(part=>part.Text??(part.Control?.Kind switch
            {"Tab"=>"\t","NoBreakHyphen"=>"\u2011","SoftHyphen"=>"\u00ad",_=>""})));
        string sourceBreak=line.Cut is LineCutKind.Line or LineCutKind.Mandatory?"\n":"";
        float baseline=clipped?height-PageComposer.Points(descent):PageComposer.Points(ascent)+(height-natural)/2;
        if(line.Index==0&&p.TopBorder is {} top)
        {
            if(clipped)throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_REGION_UNQUALIFIED",top.Source);
            height+=top.Height;
            baseline+=top.Height;
        }
        return new([],text,PageComposer.Points(width),height,baseline,sourceBreak,line.Cut==LineCutKind.Page)
        {Native=line,ClipToLineBox=clipped};
    }

    static PageLine PositionNative(MeasuredLine line,LayoutParagraph paragraph,float origin,float top,
        float left,float upper,float right,float bottom,string? cell=null,bool repeated=false)
    {
        var native=line.Native!;
        float baseline=top+line.Ascent;
        if(line.ClipToLineBox&&(top<upper-.01f||top+line.Height>bottom+.01f))
            throw new LayoutFailure("PAGE_LINE_BOX_OUTSIDE",paragraph.Source);
        foreach(var placement in native.Visual)
        {
            if(placement.Cluster<0)continue;
            var shape=native.Parts[placement.Part].Fragment!.Measure!;
            var cluster=shape.Clusters[placement.Cluster];
            for(int i=0;i<cluster.GlyphCount;i++)
                if(shape.Glyphs[cluster.FirstGlyph+i].Ink is {} ink)
                    Check(ink,placement.GlyphOriginX,placement.GlyphOriginY);
        }
        foreach(var decoration in native.Generated)
            if(decoration.Fragment.Measure?.Metrics.Ink is {} ink)
            {
                Check(ink,decoration.X,0);
                Check(ink,decoration.X+(decoration.Count-1)*decoration.Step,0);
            }
        foreach(var underline in native.Underlines)Check(underline.Ink,0,0);
        foreach(var box in native.Boxes)
        {
            float x=origin+PageComposer.Points(box.X),y=baseline-PageComposer.Points(box.Height);
            if(!native.Region.AllowHorizontalOverflow&&(x-box.Image.Effects.Left<left-.01f||
                x+PageComposer.Points(box.Width)+box.Image.Effects.Right>right+.01f)||
                !line.ClipToLineBox&&(y<upper-.01f||baseline>bottom+.01f))
                throw new LayoutFailure("PAGE_INLINE_OUTSIDE",box.Source.Location);
        }
        PlacedParagraphShading? shading=null;
        if(paragraph.Properties.Shading is {} shade)
        {
            float start=Math.Max(left,origin+PageComposer.Points(native.Region.Left+native.Properties.Left));
            float end=Math.Min(right,origin+PageComposer.Points(native.Region.Left+native.Region.Width-native.Properties.Right));
            if(end<=start)throw new LayoutFailure("PAGE_PARAGRAPH_SHADING_REGION_UNQUALIFIED",shade.Source);
            shading=new(shade,start-origin,end-origin);
        }
        return new(paragraph.Id,paragraph.Source,line.Text,origin+PageComposer.Points(native.ContentLeft),baseline)
        {
            Native=native,NativeOffsetX=origin,NativeTop=top,Height=line.Height,ClipToLineBox=line.ClipToLineBox,SourceBreak=line.SourceBreak,
            HasExplicitPageBreak=line.PageBreak,Section=paragraph.Section,CellId=cell,IsRepeated=repeated,
            ParagraphShading=shading,
            Images=native.Boxes.Select(b=>new PageImage(b.Source.NodeId,b.Image,
                origin+PageComposer.Points(b.X),
                baseline-PageComposer.Points(b.Height)+b.Image.Effects.Top){IsRepeated=repeated}).ToImmutableArray(),
            Tabs=native.Tabs.Select(t=>new PageTab(t.Source.NodeId,t.Source.Location,origin+PageComposer.Points(t.From),
                origin+PageComposer.Points(t.To),t.Stop.Kind.ToString().ToLowerInvariant())).ToImmutableArray()
        };

        void Check(TextInk ink,long x,long y)
        {
            if(!native.Region.AllowHorizontalOverflow&&(origin+PageComposer.Points(x+ink.Left)<left-.01f||
                origin+PageComposer.Points(x+ink.Right)>right+.01f)||
               !line.ClipToLineBox&&(baseline+PageComposer.Points(y+ink.Top)<upper-.01f||baseline+PageComposer.Points(y+ink.Bottom)>bottom+.01f))
                throw new LayoutFailure("PAGE_GLYPH_OUTSIDE",paragraph.Source);
        }
    }
}
