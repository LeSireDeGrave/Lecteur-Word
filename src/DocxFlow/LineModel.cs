using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal enum LineAlignment { Left, Right, Center, Justify }
internal enum LineCutKind { End, Allowed, Mandatory, Line, Page, Column, SoftHyphen }
internal enum LineCoverage { Complete, Partial }
internal enum LineTabKind { Left, Right, Center, Decimal, Numbering }

internal readonly record struct LineRegion(string Id,long Left,long Width);
internal readonly record struct LineRequest(string ParagraphId,string StoryId,int Index,int ContextStart);
internal readonly record struct LineTabStop(long Position,LineTabKind Kind,string Leader,SemanticSource? Source);
internal sealed record LineProperties(LineAlignment Alignment,long Left,long Right,long First,
    ImmutableArray<LineTabStop> Tabs,long DefaultTab,SemanticSource? DefaultTabSource,
    bool KeepNext,bool KeepLines,bool WidowControl,bool PageBefore,bool RightToLeft,
    ImmutableDictionary<string,SemanticSource> Origins,ImmutableArray<FlowIssue> Issues);

internal sealed record LinePart(Utf16Span ContextRange,TextFragment? Fragment,TextControl? Control,string? Limitation)
{
    internal string? Text=>Fragment?.Text;
}
internal readonly record struct LinePlacement(int Part,int Cluster,long X,long GlyphOriginX,long GlyphOriginY,
    long AdvanceX,long AdvanceY,long ExtraAfter);
internal sealed record LineDecoration(string Kind,TextFragment Fragment,long X,long Step,int Count,SemanticSource Cause);
internal sealed record LineTabPlacement(SemanticSource Source,LineTabStop Stop,long From,long To);
internal sealed record MeasuredListMarker(ListLabel Label,LineProperties Properties,TextStyle Style,
    LineDecoration? Decoration,long? X,long? TextStart,LineTabStop? Tab,ImmutableArray<FlowIssue> Issues);
internal readonly record struct LineInlineBox(SemanticSource Source,long X,long Width,long Height,InlineImage Image);
internal sealed record ComposedLine(string ParagraphId,string StoryId,SemanticSource ParagraphSource,int Index,
    LineRegion Region,long ContentLeft,long ContentRight,Utf16Span ContextRange,LineCutKind Cut,
    LineProperties Properties,ImmutableArray<LinePart> Parts,ImmutableArray<LinePlacement> Visual,
    ImmutableArray<LineTabPlacement> Tabs,ImmutableArray<LineDecoration> Generated,
    long? Occupied,long? Advance,long? Ascent,long? Descent,long? Baseline,long? Height,
    LineCoverage Coverage,ImmutableArray<FlowIssue> Diagnostics)
{
    internal ImmutableArray<LineInlineBox> Boxes {get;init;}=[];
    internal MeasuredListMarker? ListMarker {get;init;}
}
internal sealed record ComposedLines(TypographyDocument Typography,ImmutableArray<ComposedLine> Lines,LinePerformance Performance);
internal sealed record LinePerformance(double SegmentationMs,double CompositionMs,double ShapeMs,int Reshaped,int ShapeHits,
    int CachedShapes,long CachedGlyphs,int Work,int ShapedGlyphs);
internal sealed record LineBudget(int MaxLines=30_000,int MaxWork=5_000_000,int MaxGlyphs=500_000,int Ms=15_000);
