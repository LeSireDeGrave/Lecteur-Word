namespace LecteurWord.Flow;

internal static class LegacySinglePage
{
    internal static Composition? TryCompose(PageCompositionSnapshot? native,PaginatedDocument legacy,CancellationToken token)
    {
        if(native is not {Coverage:PageCoverage.Fallback,Semantic:{} semantic,Sections.Length:1}||
            !legacy.Available||legacy.Tables.Count!=0||legacy.Paragraphs.Count>64||
            !legacy.Paragraphs.Select(p=>p.Id).SequenceEqual(semantic.BodyBlocks)||!semantic.Fields.IsEmpty||
            semantic.Sections.Any(s=>!s.RunningStories.IsEmpty))
            return null;

        // The legacy reader verifies that these dependencies contain only note separators.
        var emptyNotes=legacy.Issues.Where(i=>i.Code=="LAYOUT_EMPTY_NOTE_PART").Select(i=>i.Source?.PartUri).ToHashSet();
        if(native.Gaps.Any(g=>g.Reason switch
            {
                "PAGE_STORY_PENDING"=>!emptyNotes.Contains(g.Source.Location.PartUri),
                "LINE_TAB_ALIGN_PARTIAL" or "PAGE_INLINE_DECORATION_PENDING" or "PAGE_AFTER_UNPLACED"=>false,
                _=>true
            })||
            native.Composition.Issues.Any(i=>i.BlocksRendering&&i.Code is not
                ("LINE_TAB_ALIGN_PARTIAL" or "PAGE_INLINE_DECORATION_PENDING" or "PAGE_NO_PLACED_BODY"))||
            !native.Composition.Issues.Any(i=>i.Code is "LINE_TAB_ALIGN_PARTIAL" or "PAGE_INLINE_DECORATION_PENDING"))
            return null;

        var page=Composer.Compose(legacy,token);
        if(!page.Available||page.Pages.Count!=1)return null;
        var rendered=page.Pages[0].Lines.Select(l=>l.ParagraphId).Distinct(StringComparer.Ordinal);
        return rendered.SequenceEqual(semantic.BodyBlocks)?page:null;
    }
}
