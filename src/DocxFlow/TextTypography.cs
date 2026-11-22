using System.Collections.Immutable;
using System.Diagnostics;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

internal enum TextRole { Source, StoredField, FieldInstruction, Deleted, Inactive, Unsupported, Control }
internal sealed record TextToken(SemanticSource Source, string? RunId, string? ParagraphId, string? StoryId,
    TextRole Role, string? Original, string? Control, TextStyle? Style);
internal readonly record struct TextSlice(SemanticSource Source, string? RunId, TextStyle Style, Utf16Span FragmentRange)
{
    internal TextRole Role { get; init; }
}
internal readonly record struct TextGlyph(ushort Id, int Cluster, long X, long Y, long AdvanceX, long AdvanceY)
{
    internal int HarfBuzzOffset { get; init; }
    internal TextInk? Ink {get;init;}
}
internal readonly record struct TextCluster(Utf16Span Range, int FirstGlyph, int GlyphCount, bool MissingGlyph, bool UnsafeBefore);
internal readonly record struct TextBreak(int Offset, bool Mandatory);
internal readonly record struct TextControl(SemanticSource Source, int Offset, string Kind)
{
    internal TextStyle? Style { get; init; }
    internal TextFragment? Symbol { get; init; }
}
internal readonly record struct TextInk(long Left, long Top, long Right, long Bottom);
internal sealed record TextMetrics(long Width, long Ascent, long Descent, long LineGap, long Baseline, TextInk? Ink)
{
    internal long Height => Ascent + Descent + LineGap;
}
internal sealed record TextMeasure(ImmutableArray<TextGlyph> Glyphs, ImmutableArray<TextCluster> Clusters, TextMetrics Metrics);
internal sealed record TextFragment(string ParagraphId, int ContextOffset, string Text, ImmutableArray<TextSlice> Sources,
    string Script, string Language, byte BidiLevel, TextStyle Style, string FontSlot, string RequestedFamily,
    FontRequest? Request, SourceLocation? ThemeSource, FontResolution? Face, float PointSize, ImmutableArray<string> Features,
    TextMeasure? Measure, ImmutableArray<TextBreak> Breaks)
{
    internal ImmutableArray<int> GraphemeStarts { get; init; } = [];
    internal string? MeasurementIssue { get; init; }
    internal LegacySymbolDecision? LegacySymbol { get; init; }
}
internal sealed record TextParagraph(string Id, string StoryId, bool RightToLeft, TextStyle MarkStyle,
    ImmutableArray<TextFragment> Fragments, ImmutableArray<int> VisualFragmentOrder)
{
    internal int ContextLength { get; init; }
    internal string Context { get; init; } = "";
    internal SemanticSource? DirectionOrigin { get; init; }
    internal ImmutableArray<TextBreak> Breaks { get; init; } = [];
    internal ImmutableArray<TextControl> Controls { get; init; } = [];
}
internal sealed record TextBudget(int MaxTokens=100_000, int MaxChars=500_000, int MaxParagraphChars=32768,
    int MaxGlyphs=500_000, int Ms=15000);
internal sealed record TextProfileTimes(double StyleMs, double SegmentMs, double FontMs, double ShapeMs,
    int StyleCacheHits, int ShapeCacheHits, int ShapeCacheEntries, int OpenFaces, long CachedGlyphs, long SnapshotBytes);
internal sealed record TypographyDocument(string SnapshotId, string IcuVersion, string UnicodeVersion,
    ImmutableArray<TextToken> Tokens, ImmutableArray<TextParagraph> Paragraphs, ImmutableArray<FlowIssue> Diagnostics,
    TextProfileTimes Timings)
{
    internal object? Owner { get; init; }
}

/// <summary>A document-owned measurement and painting session. Active callers serialize access.</summary>
internal sealed partial class TypographySession : IDisposable
{
    internal const int EmuPerPoint = 12_700;
    const string W = SemanticIndex.W;
    readonly SemanticDocument model;
    DocumentFonts fonts;
    readonly TextBudget limits;
    readonly TextUnicode unicode;
    readonly TextStyleResolver styles;
    readonly object owner = new();
    readonly FontResolver.FaceSelection selection;
    readonly Dictionary<(string Code,string Id), FlowIssue> issues = [];
    readonly Dictionary<(string Hash,int Index), ShapingFace> faces = [];
    readonly Dictionary<(string Paragraph,int Start,int Length,string Hash,int Index), TextMeasure> shapes = [];
    readonly Dictionary<int, string> scripts = [];
    readonly Dictionary<int, bool> ignorables = [];
    long cachedGlyphs;
    bool disposed, diagnosticOverflow;
    Stopwatch? watch;
    CancellationToken token;
    int glyphCount, shapeHits;
    long styleTicks, segmentTicks, fontTicks, shapeTicks;

    internal TypographySession(InspectionResult inspected, SemanticDocument model, TextBudget? limits = null, CancellationToken cancellation = default,
        TableStyleCascade? tableStyles=null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!inspected.Manifest.InspectionComplete || inspected.Manifest.Status==Coverage.Refused ||
            inspected.Manifest.SourceSha256 != model.SnapshotId)
            throw new InvalidDataException("TEXT_SNAPSHOT_MISMATCH");
        this.model = model;
        this.limits = limits ?? new();
        if (this.limits is not { MaxTokens: >0 and <=100_000, MaxChars: >0 and <=500_000,
            MaxParagraphChars: >0 and <=32768, MaxGlyphs: >0 and <=500_000, Ms: >0 and <=15000 })
            throw new ArgumentOutOfRangeException(nameof(limits));
        unicode = TextUnicode.Instance;
        fonts = DocumentFonts.Read(inspected, collectRequests: false);
        cancellation.ThrowIfCancellationRequested();
        styles = new(model, Issue,tableStyles);
        selection = new(fonts, Check);
    }

    void Check()
    {
        token.ThrowIfCancellationRequested();
        if (watch?.ElapsedMilliseconds >= limits.Ms) throw new InvalidDataException("TEXT_TIME_LIMIT");
    }

    void Issue(string code,string id,string message)
    {
        if (issues.Count < 4096) issues.TryAdd((code,id),new(code,model.Source(id).Location,message,false));
        else diagnosticOverflow = true;
    }

    internal TypographyDocument Measure(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token = cancellation;
        watch = Stopwatch.StartNew();
        glyphCount = shapeHits = 0;
        styleTicks = segmentTicks = fontTicks = shapeTicks = 0;
        int styleHits = styles.CacheHits;
        try
        {
            var tokens = CollectTokens();
            var groups = tokens.Where(t => t.ParagraphId is not null && t.StoryId is not null)
                .ToLookup(t => t.ParagraphId!);
            var paragraphs = ImmutableArray.CreateBuilder<TextParagraph>();
            foreach (var paragraph in model.Elements)
            {
                Check();
                if (paragraph.Kind!=SemanticKind.Paragraph || paragraph.StoryId is null ||
                    paragraph.Coverage==SemanticCoverage.InactiveAlternative || model.Revision(paragraph.Id)=="deleted") continue;
                paragraphs.Add(BuildParagraph(paragraph, groups[paragraph.Id]));
            }
            var diagnostics = issues.Values.ToImmutableArray();
            if (diagnosticOverflow)
                diagnostics=diagnostics.Add(new("TEXT_DIAGNOSTIC_LIMIT",null,"Diagnostic détaillé borné. Sources et résultats conservés.",false));
            Check();
            return new(model.SnapshotId, unicode.Version,unicode.UnicodeVersion,tokens,paragraphs.ToImmutable(),diagnostics,
                new(Ms(styleTicks),Ms(segmentTicks),Ms(fontTicks),Ms(shapeTicks),styles.CacheHits-styleHits,shapeHits,shapes.Count,faces.Count,cachedGlyphs,
                    faces.Values.Sum(x=>(long)x.Bytes))) { Owner=owner };
        }
        finally { watch = null; token = default; }
    }

    internal void CheckOwner(TypographyDocument document, SemanticDocument semantic)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!ReferenceEquals(document.Owner,owner)||!ReferenceEquals(semantic,model))
            throw new InvalidDataException("LINE_SNAPSHOT_MISMATCH");
    }

    static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    bool Word(string id, string name) => model.Name(id)==SemanticIndex.QName(W,name);

    ImmutableArray<TextToken> CollectTokens()
    {
        var result = ImmutableArray.CreateBuilder<TextToken>();
        var visibleFields=new HashSet<string>(StringComparer.Ordinal);
        var stored=new HashSet<string>(StringComparer.Ordinal);
        var operands=new HashSet<string>(StringComparer.Ordinal);
        var fields=model.Fields.ToDictionary(f=>f.Id,StringComparer.Ordinal);
        foreach(var field in model.Fields)
        {
            Check();
            bool visible=field.StructureVerified&&field.Command!="TC"&&
                (field.ParentFieldId is not {} id||visibleFields.Contains(id)&&fields.TryGetValue(id,out var parent)&&
                    (parent.InstructionAttribute is not null||parent.SeparatorId is {} separator&&
                        model.Element(separator).Order<model.Element(field.Id).Order));
            if(visible)visibleFields.Add(field.Id);
            (visible?stored:operands).UnionWith(field.StoredResultIds);
        }
        long chars=0;
        foreach (var e in model.Elements)
        {
            Check();
            bool text = e.Kind is SemanticKind.Text or SemanticKind.DeletedText or SemanticKind.FieldInstruction;
            bool control = e.Kind is SemanticKind.Tab or SemanticKind.Break or SemanticKind.Symbol or SemanticKind.NoteCall
                or SemanticKind.Drawing or SemanticKind.SoftHyphen or SemanticKind.NoBreakHyphen;
            if (!text && !control) continue;
            string? paragraph=null, run=null;
            for (string? id=model.ParentId(e.Id);id is not null && model.Element(id).StoryId==e.StoryId;id=model.ParentId(id))
            {
                if (run is null && Word(id,"r")) run=id;
                if (Word(id,"p")) { paragraph=id; break; }
            }

            string? original = text ? model.RawText(e.Id) : e.Kind==SemanticKind.Symbol?model.Attribute(e.Id,"char"):null;
            chars += original?.Length ?? 0;
            if (chars>limits.MaxChars || result.Count>=limits.MaxTokens) throw new InvalidDataException("TEXT_TOKEN_LIMIT");
            TextRole role;
            if (e.Coverage==SemanticCoverage.InactiveAlternative) role=TextRole.Inactive;
            else if (e.Kind==SemanticKind.DeletedText || model.Revision(e.Id)=="deleted") role=TextRole.Deleted;
            else if (e.Kind==SemanticKind.FieldInstruction || operands.Contains(e.Id) || model.FieldRole(e.Id)=="instruction" && !stored.Contains(e.Id))
                role=TextRole.FieldInstruction;
            else if (control) role=TextRole.Control;
            else if (paragraph is null || run is null || model.ParentId(e.Id) is not {} owner || !Word(owner,"t"))
                role=TextRole.Unsupported;
            else role=stored.Contains(e.Id)?TextRole.StoredField:TextRole.Source;

            TextStyle? style=null;
            if (run is not null && role is TextRole.Source or TextRole.StoredField or TextRole.Control)
            {
                long start=Stopwatch.GetTimestamp();
                style=styles.Resolve(run,false);
                styleTicks+=Stopwatch.GetTimestamp()-start;
            }
            if (role==TextRole.Unsupported)
                Issue("TEXT_STORY_UNQUALIFIED",e.Id,"Texte conservé hors paragraphe Word qualifié.");
            result.Add(new(text ? model.TextSource(e.Id) : model.Source(e.Id),run,paragraph,e.StoryId,role,original,
                control?e.Kind.ToString():null,style));
        }
        return result.ToImmutable();
    }

    string Script(int scalar)
    {
        if (scripts.TryGetValue(scalar,out string? script)) return script;
        script=unicode.Script(scalar);
        if (scripts.Count<8192) scripts.Add(scalar,script);
        return script;
    }

    bool Ignorable(int scalar)
    {
        if (ignorables.TryGetValue(scalar,out bool value)) return value;
        value=unicode.IsIgnorable(scalar);
        if (ignorables.Count<8192) ignorables.Add(scalar,value);
        return value;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed=true;
        foreach (var face in faces.Values) face.Dispose();
        faces.Clear();
        shapes.Clear();
        fontUses.Clear();
        styles.Clear();
        selection.Dispose();
        fonts = new();
        scripts.Clear();
        ignorables.Clear();
        issues.Clear();
        cachedGlyphs=0;
    }
}
