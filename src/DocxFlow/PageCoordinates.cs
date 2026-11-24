using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal readonly record struct PageRect(float Left,float Top,float Width,float Height)
{
    internal bool Contains(double x,double y)=>x>=Left&&x<Left+Width&&y>=Top&&y<Top+Height;
    internal PageRect Intersection(PageRect other)
    {
        float left=Math.Max(Left,other.Left),top=Math.Max(Top,other.Top);
        return new(left,top,Math.Max(0,Math.Min(Left+Width,other.Left+other.Width)-left),
            Math.Max(0,Math.Min(Top+Height,other.Top+other.Height)-top));
    }
    internal bool Empty=>Width<=0||Height<=0;
}
internal sealed record PageCluster(Utf16Span ContextRange,PageRect Rect,ImmutableArray<SemanticSource> Sources,string Text);

internal static class PageCoordinates
{
    internal static float LineTop(PageLine line)=>line.FrameTransform is null?line.NativeTop:
        Clusters(line).Select(c=>c.Rect.Top).Concat(Images(line).Select(c=>c.Rect.Top)).DefaultIfEmpty(line.FrameTransform.Value.Y).Min();

    internal static IEnumerable<(string Id,PageRect Rect)> FieldLabels(PageLine line)
    {
        if(line.Native is not {} native||native.Ascent is not {} ascent||native.Descent is not {} descent)yield break;
        foreach(var field in native.Generated)
        {
            if(field.Kind!="pageField"||field.Fragment.Measure is not {} measure)continue;
            var rect=Clip(line,new(line.NativeOffsetX+PageComposer.Points(field.X),
                line.Baseline-PageComposer.Points(ascent),PageComposer.Points(measure.Metrics.Width),PageComposer.Points(ascent+descent)));
            if(!rect.Empty)yield return (field.ResultId??field.Cause.NodeId,rect);
        }
    }

    internal static IEnumerable<PageCluster> Clusters(PageLine line)
    {
        if(line.Native is not {} native||native.Ascent is not {} ascent||native.Descent is not {} descent)yield break;
        foreach(var slot in native.Visual)
        {
            if(slot.AdvanceX+slot.ExtraAfter<=0)continue;
            var rect=Clip(line,new(line.NativeOffsetX+PageComposer.Points(slot.X),line.Baseline-PageComposer.Points(ascent),
                PageComposer.Points(slot.AdvanceX+slot.ExtraAfter),PageComposer.Points(ascent+descent)));
            if(rect.Empty)continue;
            if(slot.Cluster<0)
            {
                var part=native.Parts[slot.Part];
                if(part.Control is {Symbol.Measure:not null} symbol)
                    yield return new(part.ContextRange,rect,[symbol.Source],"");
                continue;
            }
            var f=native.Parts[slot.Part].Fragment!;
            var c=f.Measure!.Clusters[slot.Cluster].Range;
            var sources=ImmutableArray.CreateBuilder<SemanticSource>();
            foreach(var s in f.Sources)
            {
                int a=Math.Max(c.Start,s.FragmentRange.Start),b=Math.Min(c.EndExclusive,s.FragmentRange.EndExclusive);
                if(a>=b||s.Source.Span is not {} original)continue;
                int offset=original.Start-s.FragmentRange.Start;
                sources.Add(s.Source with {Span=new(offset+a,offset+b)});
            }
            yield return new(new(f.ContextOffset+c.Start,f.ContextOffset+c.EndExclusive),
                rect,sources.ToImmutable(),f.Text[c.Start..c.EndExclusive]);
        }
    }

    internal static IEnumerable<PageCluster> Images(PageLine line)
    {
        if(line.Native is not {} native||line.Images.Count==0)yield break;
        var bySource=native.Parts.Where(p=>p.Control is {Kind:"Drawing"})
            .ToDictionary(p=>p.Control!.Value.Source.NodeId,StringComparer.Ordinal);
        foreach(var image in line.Images)
        {
            if(!bySource.TryGetValue(image.RunId,out var part))continue;
            var rect=Clip(line,new(image.X,image.Y,image.Image.Width,image.Image.Height));
            if(!rect.Empty)yield return new(part.ContextRange,rect,[part.Control!.Value.Source],"");
        }
    }

    static PageRect Clip(PageLine line,PageRect rect)
    {
        if(line.ClipToLineBox)
        {
            float top=Math.Max(rect.Top,line.NativeTop);
            rect=rect with {Top=top,Height=Math.Max(0,Math.Min(rect.Top+rect.Height,line.NativeTop+line.Height)-top)};
        }
        if(line.FrameTransform is {} rotation)rect=rotation.Bounds(rect);
        return line.SheetClip is {} sheet?rect.Intersection(sheet):rect;
    }
}
