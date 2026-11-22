using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal readonly record struct PageRect(float Left,float Top,float Width,float Height)
{
    internal bool Contains(double x,double y)=>x>=Left&&x<Left+Width&&y>=Top&&y<Top+Height;
}
internal sealed record PageCluster(Utf16Span ContextRange,PageRect Rect,ImmutableArray<SemanticSource> Sources,string Text);

internal static class PageCoordinates
{
    internal static IEnumerable<PageCluster> Clusters(PageLine line)
    {
        if(line.Native is not {} native||native.Ascent is not {} ascent||native.Descent is not {} descent)yield break;
        foreach(var slot in native.Visual)
        {
            if(slot.AdvanceX+slot.ExtraAfter<=0)continue;
            var rect=new PageRect(line.NativeOffsetX+PageComposer.Points(slot.X),line.Baseline-PageComposer.Points(ascent),
                PageComposer.Points(slot.AdvanceX+slot.ExtraAfter),PageComposer.Points(ascent+descent));
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
}
