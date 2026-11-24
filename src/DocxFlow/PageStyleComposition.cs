using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal static partial class PageComposer
{
    static bool IsStyleComparisonIssue(FlowIssue issue) => issue.Code is
        "PAGE_STYLES_EFFECTS_DIVERGENT" or "PAGE_STYLES_EFFECTS_DEFAULTS" or
        "PAGE_STYLES_EFFECTS_MISSING_STYLE" or "PAGE_STYLES_EFFECTS_ONLY_STYLE";

    static PageCompositionLease? CompareStyleCompositions(InspectionResult inspection, PageCompositionSnapshot initial,
        CancellationToken token, bool prepareReadingTables, PreparedReadingModel? prepared,
        IReadOnlyList<HistoricalFontChoice>? historicalChoices)
    {
        if (initial.StyleDecision is not { Selection: PageStyleSelection.Unresolved, CanCompareEffective: true } decision ||
            initial.Coverage != PageCoverage.Partial || initial.Pages.Length > 12 ||
            initial.Composition.Issues.Any(i => i.BlocksRendering && !IsStyleComparisonIssue(i)))
            return null;

        long started = Stopwatch.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        PageCompositionLease? effects = null;
        try
        {
            using var ordinary = ComposeWithStyles(inspection, budget.Token, prepareReadingTables, prepared,
                styleComparison: true,historicalChoices:historicalChoices);
            if (ordinary.Snapshot.Coverage != PageCoverage.Complete) return null;
            effects = ComposeWithStyles(inspection, budget.Token, prepareReadingTables, null,
                StyleCollectionKind.Effects, styleComparison: true,historicalChoices:historicalChoices);
            if (!SameStyleComposition(ordinary.Snapshot, effects.Snapshot, budget.Token)) return null;

            var chosen = effects.Snapshot.StyleDecision! with
            {
                ComparisonMs = decision.ComparisonMs + Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
            var evidence = initial.Composition.Issues.Where(IsStyleComparisonIssue)
                .Select(i => i with { BlocksRendering = false })
                .Append(new FlowIssue("PAGE_STYLES_EFFECTS_COMPOSITION_EQUIVALENT", chosen.SelectedRoot?.Location,
                    "Les deux collections donnent la même composition qualifiée. La collection auxiliaire représente ce résultat commun.", false));
            effects.PublishStyleComparison(chosen, evidence);
            var accepted = effects;
            effects = null;
            return accepted;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (InvalidDataException e) when (e.Message == "PAGE_RASTER_LIMIT")
        {
            return null;
        }
        finally
        {
            effects?.Dispose();
        }
    }

    static bool SameStyleComposition(PageCompositionSnapshot left, PageCompositionSnapshot right, CancellationToken token)
    {
        if (right.Coverage != PageCoverage.Complete || left.Pages.Length != right.Pages.Length ||
            left.Pages.Length > 12 || left.FieldStability != right.FieldStability ||
            !left.FieldBookmarks.OrderBy(p => p.Key, StringComparer.Ordinal)
                .SequenceEqual(right.FieldBookmarks.OrderBy(p => p.Key, StringComparer.Ordinal)) ||
            !left.PageLinkTargets.OrderBy(p => p.Key, StringComparer.Ordinal)
                .SequenceEqual(right.PageLinkTargets.OrderBy(p => p.Key, StringComparer.Ordinal)))
            return false;

        var first = left.Typography!.Paragraphs;
        var second = right.Typography!.Paragraphs;
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var a = first[i];
            var b = second[i];
            if (a.Id != b.Id || a.Context != b.Context || a.RightToLeft != b.RightToLeft ||
                Style(a.MarkStyle) != Style(b.MarkStyle) || a.Fragments.Length != b.Fragments.Length)
                return false;
            for (int j = 0; j < a.Fragments.Length; j++)
            {
                var fa = a.Fragments[j];
                var fb = b.Fragments[j];
                if (fa.ContextOffset != fb.ContextOffset || fa.Text != fb.Text || fa.Script != fb.Script ||
                    fa.Language != fb.Language || fa.BidiLevel != fb.BidiLevel || fa.FontSlot != fb.FontSlot ||
                    fa.RequestedFamily != fb.RequestedFamily || fa.PointSize != fb.PointSize ||
                    fa.Face?.Sha256 != fb.Face?.Sha256 || fa.Face?.FaceIndex != fb.Face?.FaceIndex ||
                    fa.Measure?.Metrics != fb.Measure?.Metrics || Style(fa.Style) != Style(fb.Style))
                    return false;
            }
        }

        double rasterPixels = 0;
        for (int i = 0; i < left.Pages.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var a = left.Pages[i];
            var b = right.Pages[i];
            if (a.Geometry.Geometry != b.Geometry.Geometry || !a.Sections.SequenceEqual(b.Sections) ||
                !SamePage(a.Geometry, b.Geometry)) return false;
            rasterPixels += Math.Ceiling(a.Geometry.Geometry.Width) * Math.Ceiling(a.Geometry.Geometry.Height);
            if (rasterPixels > 10_000_000 ||
                !Composer.PagePng(a.Geometry).AsSpan().SequenceEqual(Composer.PagePng(b.Geometry))) return false;
        }
        token.ThrowIfCancellationRequested();
        return true;
    }

    static TextStyle Style(TextStyle value) => value with
    {
        Origins = ImmutableDictionary<string, SemanticSource>.Empty,
        Slots = new(WithoutSource(value.Slots.Ascii), WithoutSource(value.Slots.HAnsi), WithoutSource(value.Slots.EastAsia),
            WithoutSource(value.Slots.Cs), value.Slots.Hint)
    };

    static FontRequest? WithoutSource(FontRequest? font) => font is null ? null : font with { Source = default! };

    static bool SamePage(ComposedPage left, ComposedPage right) =>
        left.PrintedPageNumber == right.PrintedPageNumber &&
        JsonSerializer.SerializeToUtf8Bytes(PlacedContent(left)).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(PlacedContent(right)));

    static object PlacedContent(ComposedPage page) => new
    {
        Lines = page.Lines.Select(PlacedLine),
        Cells = page.DisplayCells.Select(c =>
            new { c.TableId, c.CellId, c.X, c.Y, c.Width, c.Height, c.Fill, c.IsRepeated, c.MergedSourceIds, c.RowFragment, c.CellFragment }),
        Stories = page.RunningStories.Select(s => new { s.Selection, s.Region, Lines = s.Lines.Select(PlacedLine) }),
        Notes = page.Footnotes.Select(n => new { n.Source, n.Call, n.Region, n.Continued, Lines = n.Lines.Select(PlacedLine) }),
        Pictures = page.FloatingPictures.Select(p => new { p.ParagraphId, p.X, p.Y }),
        Frames = page.FloatingTextFrames.Select(f => new {f.Frame.X,f.Frame.Y,f.Frame.Width,f.Frame.Height,
            f.Frame.Rotation,f.Frame.Fill,f.Frame.Stroke,f.Frame.Order,f.Frame.BehindText,Lines=f.Lines.Select(PlacedLine)})
    };

    static object PlacedLine(PageLine line) => new
    {
        line.ParagraphId, line.Section, line.CellId, line.IsRepeated, line.X, line.Baseline, line.Height, line.Text,
        line.SourceBreak, line.FlowRegion, line.SideExclusion, line.ClipToLineBox, line.NativeOffsetX, line.NativeTop,
        Shading = line.ParagraphShading?.Shading.Color,
        Range = line.Native?.ContextRange, Cut = line.Native?.Cut,
        Parts = line.Native?.Parts.Select(p => new { p.ContextRange, p.Text, Sources = p.Fragment?.Sources.Select(s => s.Source) }),
        Tabs = line.Tabs,
        Images = line.Images.Select(i => new { i.RunId, i.X, i.Y, i.IsRepeated })
    };
}
