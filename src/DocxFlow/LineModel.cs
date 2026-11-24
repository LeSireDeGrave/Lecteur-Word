using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal enum LineAlignment { Left, Right, Center, Justify }
internal enum LineCutKind { End, Allowed, Mandatory, Line, Page, Column, SoftHyphen, AutomaticHyphen }
internal enum LineCoverage { Complete, Partial }
internal enum LineTabKind { Left, Right, Center, Decimal, Numbering }

internal readonly record struct LineRegion(string Id,long Left,long Width)
{
    internal long RightMargin {get;init;}
    internal long? TabOrigin {get;init;}
}
internal readonly record struct LineRequest(string ParagraphId,string StoryId,int Index,int ContextStart);
internal readonly record struct LineResume(int ContextStart,int Index,int ConsecutiveHyphens,bool TerminalMark);
internal readonly record struct LineTabStop(long Position,LineTabKind Kind,string Leader,SemanticSource? Source);
internal sealed record LineProperties(LineAlignment Alignment,long Left,long Right,long First,
    ImmutableArray<LineTabStop> Tabs,long DefaultTab,SemanticSource? DefaultTabSource,
    bool KeepNext,bool KeepLines,bool WidowControl,bool PageBefore,bool RightToLeft,
    ImmutableDictionary<string,SemanticSource> Origins,ImmutableArray<FlowIssue> Issues)
{
    internal LineHyphenation? Hyphenation {get;init;}
    internal bool ForgetLastTabAlignment {get;init;}
}

internal sealed record LineHyphenation(SemanticSource Source,long Zone,int ConsecutiveLimit,bool SuppressCaps,
    string? CompatibilityLimitation);

internal sealed record LinePart(Utf16Span ContextRange,TextFragment? Fragment,TextControl? Control,string? Limitation)
{
    internal string? Text=>Fragment?.Text;
}
internal readonly record struct LinePlacement(int Part,int Cluster,long X,long GlyphOriginX,long GlyphOriginY,
    long AdvanceX,long AdvanceY,long ExtraAfter);
internal sealed record LineDecoration(string Kind,TextFragment Fragment,long X,long Step,int Count,SemanticSource Cause)
{
    internal string? ResultId {get;init;}
}
internal sealed record LineTabPlacement(SemanticSource Source,LineTabStop Stop,long From,long To);
internal sealed record MeasuredListMarker(ListLabel Label,LineProperties Properties,TextStyle Style,
    LineDecoration? Decoration,long? X,long? TextStart,LineTabStop? Tab,ImmutableArray<FlowIssue> Issues);
internal readonly record struct LineInlineBox(SemanticSource Source,long X,long Width,long Height,InlineImage Image);
internal readonly record struct LineUnderline(SemanticSource Source,TextInk Ink,string Color);
internal sealed record ComposedLine(string ParagraphId,string StoryId,SemanticSource ParagraphSource,int Index,
    LineRegion Region,long ContentLeft,long ContentRight,Utf16Span ContextRange,LineCutKind Cut,
    LineProperties Properties,ImmutableArray<LinePart> Parts,ImmutableArray<LinePlacement> Visual,
    ImmutableArray<LineTabPlacement> Tabs,ImmutableArray<LineDecoration> Generated,
    long? Occupied,long? Advance,long? Ascent,long? Descent,long? Baseline,long? Height,
    LineCoverage Coverage,ImmutableArray<FlowIssue> Diagnostics)
{
    internal LineResume Resume {get;init;}
    internal ImmutableArray<LineInlineBox> Boxes {get;init;}=[];
    internal ImmutableArray<LineUnderline> Underlines {get;init;}=[];
    internal bool HasUnsupportedUnderline=>Parts.Any(p=>p.Fragment is {} text&&text.Style.Underline is not ("none" or "single" or "double")||
        p.Control is {Style.Underline:not "none" and not null} control&&!Generated.Any(d=>
            d.Kind=="noteNumber"&&d.Cause==control.Source&&d.Fragment.Style.Underline is "single" or "double"))||
        Generated.Any(d=>d.Kind!="listMarker"&&d.Fragment.Style.Underline!="none"&&
            (d.Kind!="noteNumber"||d.Fragment.Style.Underline is not ("single" or "double")));
    internal MeasuredListMarker? ListMarker {get;init;}
    internal GeneratedTocRow? TocRow {get;init;}
}
internal sealed record ComposedLines(TypographyDocument Typography,ImmutableArray<ComposedLine> Lines,LinePerformance Performance);
internal sealed record LinePerformance(double SegmentationMs,double CompositionMs,double ShapeMs,int Reshaped,int ShapeHits,
    int CachedShapes,long CachedGlyphs,int Work,int ShapedGlyphs)
{
    internal double HyphenationMs {get;init;}
    internal double HyphenationLanguageMs {get;init;}
}
internal sealed record LineBudget(int MaxLines=30_000,int MaxWork=5_000_000,int MaxGlyphs=500_000,int Ms=15_000);

internal sealed record GeneratedPageField(SemanticSource Source,string Value,TextStyle? Style=null)
{
    internal Utf16Span? StoredRange {get;init;}
    internal TextFragment? Measured {get;init;}
    internal string? ResultId {get;init;}
}
