using System.Collections.Immutable;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

public static partial class Composer
{
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
        IReadOnlyDictionary<string,ImmutableArray<ComposedLine>> source,LineComposer painter,Action check,Action firstPage)
    {
        document=ApplyContextualSpacing(document);
        var measured=new List<MeasuredParagraph>();
        try
        {
            foreach(var p in document.Paragraphs)
            {
                check();
                if(p.TableId is not null){measured.Add(new(p,[]));continue;}
                if(!source.TryGetValue(p.Id,out var lines))throw new LayoutFailure("PAGE_LINE_MISSING",p.Source);
                measured.Add(new(p,lines.Select(l=>MeasureNative(p,l)).ToImmutableArray()));
            }
            using var fonts=new LayoutFonts();
            var placed=PlaceMeasured(document,measured,fonts,check,native:true,firstPage:firstPage);
            return placed with {Pages=placed.Pages.Select(p=>p with {NativePainter=painter}).ToImmutableArray()};
        }
        catch(LayoutFailure failure)
        {
            return new([], [new(failure.Message,failure.SourceLocation,"Contraintes de placement incompatibles. Provenance et texte conservés.",true)]);
        }
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
        if(line.Height is not {} heightEmu||line.Ascent is not {} ascent||line.Occupied is not {} width)
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
        if(!float.IsFinite(height)||height<natural-.01f)
            throw new LayoutFailure("PAGE_LINE_SPACING_CLIPS",paragraph.Source);
        string text=string.Concat(line.Parts.Select(part=>part.Text??(part.Control?.Kind switch
            {"Tab"=>"\t","NoBreakHyphen"=>"\u2011","SoftHyphen"=>"\u00ad",_=>""})));
        string sourceBreak=line.Cut is LineCutKind.Line or LineCutKind.Mandatory?"\n":"";
        return new([],text,PageComposer.Points(width),height,PageComposer.Points(ascent)+(height-natural)/2,sourceBreak,line.Cut==LineCutKind.Page)
        {Native=line};
    }

    static PageLine PositionNative(MeasuredLine line,LayoutParagraph paragraph,float origin,float top,
        float left,float upper,float right,float bottom,string? cell=null,bool repeated=false)
    {
        var native=line.Native!;
        float baseline=top+line.Ascent;
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
        foreach(var box in native.Boxes)
        {
            float x=origin+PageComposer.Points(box.X),y=baseline-PageComposer.Points(box.Height);
            if(x<left-.01f||y<upper-.01f||x+PageComposer.Points(box.Width)>right+.01f||baseline>bottom+.01f)
                throw new LayoutFailure("PAGE_INLINE_OUTSIDE",box.Source.Location);
        }
        return new(paragraph.Id,paragraph.Source,line.Text,origin+PageComposer.Points(native.ContentLeft),baseline)
        {
            Native=native,NativeOffsetX=origin,NativeTop=top,Height=line.Height,SourceBreak=line.SourceBreak,
            HasExplicitPageBreak=line.PageBreak,Section=paragraph.Section,CellId=cell,IsRepeated=repeated,
            Images=native.Boxes.Select(b=>new PageImage(b.Source.NodeId,b.Image,origin+PageComposer.Points(b.X),
                baseline-PageComposer.Points(b.Height)){IsRepeated=repeated}).ToImmutableArray(),
            Tabs=native.Tabs.Select(t=>new PageTab(t.Source.NodeId,t.Source.Location,origin+PageComposer.Points(t.From),
                origin+PageComposer.Points(t.To),t.Stop.Kind.ToString().ToLowerInvariant())).ToImmutableArray()
        };

        void Check(TextInk ink,long x,long y)
        {
            if(origin+PageComposer.Points(x+ink.Left)<left-.01f||origin+PageComposer.Points(x+ink.Right)>right+.01f||
               baseline+PageComposer.Points(y+ink.Top)<upper-.01f||baseline+PageComposer.Points(y+ink.Bottom)>bottom+.01f)
                throw new LayoutFailure("PAGE_GLYPH_OUTSIDE",paragraph.Source);
        }
    }
}
