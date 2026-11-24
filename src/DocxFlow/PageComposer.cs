using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal enum PageCoverage { Complete, Partial, Fallback }
internal sealed record PageRenderingStatus(PageCoverage Pagination,bool ContentComplete,int MissingGlyphs,int UserGlyphChoices)
{
    internal string VisualComparison=>"NotCompared";
}

internal readonly record struct PageRegion(long Left,long Top,long Width,long Height);
internal sealed record PageSection(SemanticSection Semantic,SemanticSource? Source,PageGeometry Format,PageRegion Body,
    long PhysicalWidth,long PhysicalHeight,string Start,string? Orientation,long? HeaderDistance,long? FooterDistance,long? Gutter,int? Columns,long? ColumnSpace,
    bool MirrorMargins,ImmutableArray<FlowIssue> Diagnostics)
{
    internal bool TitlePage {get;init;}
    internal bool EvenAndOdd {get;init;}
    internal int? PageNumberStart {get;init;}
    internal string PageNumberFormat {get;init;}="decimal";
    internal ImmutableArray<FlowIssue> RunningIssues {get;init;}=[];
    internal FlowColumns? ColumnLayout {get;init;}
    internal ImmutableDictionary<string,StoryMargins> RunningMargins {get;init;}=ImmutableDictionary<string,StoryMargins>.Empty;

    internal string RunningVariant(bool first,int? printed)=>TitlePage&&first?"first":EvenAndOdd&&printed%2==0?"even":"default";

    internal PageGeometry GeometryOn(int physicalPage,bool first,int? printed)
    {
        var geometry=MirrorMargins&&physicalPage%2==0?Format with {Left=Format.Right,Right=Format.Left}:Format;
        return RunningMargins.TryGetValue(RunningVariant(first,printed),out var margins)?
            geometry with{Top=PageComposer.Points(margins.Top),Bottom=PageComposer.Points(margins.Bottom)}:geometry;
    }
}
internal readonly record struct StoryMargins(long Top,long Bottom);
internal readonly record struct CalculatedPageId(string? SnapshotId,int PhysicalIndex);
internal sealed record NativePage(CalculatedPageId Id,PageSection Origin,ImmutableArray<int> Sections,ComposedPage Geometry)
{
    internal int? SectionPageNumber=>Geometry.PrintedPageNumber;
}
internal sealed record PagePlacementGap(SemanticSource Source,string Reason,Utf16Span? ContextRange=null);
internal sealed record PageCompositionTimes(double SemanticMs,double TypographyMs,double RegionsMs,double LinesMs,double AssemblyMs,
    double? FirstPageMs)
{
    internal double FieldResolutionMs {get;init;}
    internal double FieldRecompositionMs {get;init;}
    internal double FieldAnalysisMs {get;init;}
    internal double BookmarkIndexMs {get;init;}
    internal double FieldStabilizationMs {get;init;}
    internal double InitialBodyPlacementMs {get;init;}
    internal double RunningStoriesMs {get;init;}
    internal double RunningFieldResolutionMs {get;init;}
    internal double RunningFieldMeasurementMs {get;init;}
    internal double FloatingPlacementMs {get;init;}
    internal double FloatingTextMeasurementMs {get;init;}
    internal double PublicationMs {get;init;}
}
internal sealed record PageCompositionSnapshot(string? SnapshotId,SemanticDocument? Semantic,TypographyDocument? Typography,ComposedLines? Lines,
    ImmutableArray<PageSection> Sections,ImmutableArray<NativePage> Pages,ImmutableArray<PagePlacementGap> Gaps,Composition Composition,PageCoverage Coverage,
    PageCompositionTimes Times,PaginatedDocument? RulerModel)
{
    internal IReadOnlyDictionary<string,MeasuredListMarker> ListMarkers {get;init;}=ImmutableDictionary<string,MeasuredListMarker>.Empty;
    internal ImmutableArray<PageStoryRegion> StoryRegions {get;init;}=[];
    internal PageStyleDecision? StyleDecision {get;init;}
    internal IReadOnlyDictionary<string,ComposedPage> ReadingTables {get;init;}=ImmutableDictionary<string,ComposedPage>.Empty;
    internal IReadOnlyDictionary<string,LayoutTable> ReadingGrids {get;init;}=ImmutableDictionary<string,LayoutTable>.Empty;
    internal ImmutableArray<PageFieldPass> FieldPasses {get;init;}=[];
    internal ImmutableArray<int> ReplayedFieldSheets {get;init;}=[];
    internal string? FieldStability {get;init;}
    internal IReadOnlyDictionary<string,PlacedPageBookmark> FieldBookmarks {get;init;}=ImmutableDictionary<string,PlacedPageBookmark>.Empty;
    internal IReadOnlyDictionary<string,string> PageLinkTargets {get;init;}=ImmutableDictionary<string,string>.Empty;
    internal PageRenderingStatus Rendering {get;init;}=new(PageCoverage.Fallback,false,0,0);
}

/// <summary>Owns the faces used to draw one immutable composition. The caller releases the lease on document close.</summary>
internal sealed class PageCompositionLease(PageCompositionSnapshot snapshot,TypographySession? typography=null,LineComposer? painter=null) : IDisposable
{
    internal PageCompositionSnapshot Snapshot {get;private set;}=snapshot;
    internal void PublishStyleComparison(PageStyleDecision decision,IEnumerable<FlowIssue> evidence)
    {
        Snapshot=Snapshot with { StyleDecision=decision,
            Composition=Snapshot.Composition with { Issues=Snapshot.Composition.Issues.Concat(evidence).ToImmutableArray() } };
    }
    public void Dispose()
    {
        painter?.Dispose();
        typography?.Dispose();
    }
}

internal static partial class PageComposer
{
    internal const string Profile="docx-flow-native-pages-v1";
    const int MaxDiagnostics=4096;
    internal static PageCompositionLease Compose(InspectionResult inspection,CancellationToken token=default,bool prepareReadingTables=false)=>
        Compose(inspection,token,prepareReadingTables,null);

    internal static PageCompositionLease Compose(InspectionResult inspection,CancellationToken token,bool prepareReadingTables,
        PreparedReadingModel? preparedModel,IReadOnlyList<HistoricalFontChoice>? historicalChoices=null)
    {
        var initial=ComposeWithStyles(inspection,token,prepareReadingTables,preparedModel,historicalChoices:historicalChoices);
        try
        {
            var equivalent=CompareStyleCompositions(inspection,initial.Snapshot,token,prepareReadingTables,preparedModel,historicalChoices);
            if(equivalent is null)return initial;
            initial.Dispose();
            return equivalent;
        }
        catch
        {
            initial.Dispose();
            throw;
        }
    }

    static PageCompositionLease ComposeWithStyles(InspectionResult inspection,CancellationToken token,bool prepareReadingTables,
        PreparedReadingModel? preparedModel,StyleCollectionKind styles=StyleCollectionKind.Ordinary,bool styleComparison=false,
        IReadOnlyList<HistoricalFontChoice>? historicalChoices=null)
    {
        token.ThrowIfCancellationRequested();
        var timer=Stopwatch.StartNew();
        double semanticMs=0,typographyMs=0,regionsMs=0,linesMs=0,assemblyMs=0,fieldResolutionMs=0,fieldRecompositionMs=0;
        double fieldAnalysisMs=0,bookmarkIndexMs=0,stabilizationMs=0,initialBodyPlacementMs=0;
        double? firstPageMs=null;
        double runningStoriesMs=0,runningFieldResolutionMs=0,runningFieldMeasurementMs=0,floatingPlacementMs=0,floatingTextMeasurementMs=0;
        SemanticDocument? model=null;
        TypographySession? typography=null;
        LineComposer? painter=null;
        TypographyDocument? textSnapshot=null;
        ComposedLines? lines=null;
        PaginatedDocument? layout=null;
        PageStyleDecision? styleDecision=null;
        IReadOnlyDictionary<string,MeasuredListMarker> markers=ImmutableDictionary<string,MeasuredListMarker>.Empty;
        ImmutableArray<PageSection> sections=[];
        ImmutableArray<PageStoryRegion> storyRegions=[];
        var gaps=ImmutableArray.CreateBuilder<PagePlacementGap>();
        var issues=new List<FlowIssue>();
        var keys=new Dictionary<(string,SourceLocation?),int>();
        int informational=0,blocking=0;
        bool informationLimited=false,blockingLimited=false;
        ImmutableArray<FlowIssue> semanticDiagnostics=[];
        Composition pages=new([],[]);
        var readingTables=new Dictionary<string,ComposedPage>(StringComparer.Ordinal);
        var readingGrids=new Dictionary<string,LayoutTable>(StringComparer.Ordinal);
        var fieldPasses=ImmutableArray.CreateBuilder<PageFieldPass>();
        var replayedFieldSheets=ImmutableArray.CreateBuilder<int>();
        string? fieldStability=null;
        IReadOnlyDictionary<string,PlacedPageBookmark> fieldBookmarks=ImmutableDictionary<string,PlacedPageBookmark>.Empty;
        IReadOnlyDictionary<string,string> pageLinkTargets=ImmutableDictionary<string,string>.Empty;
        bool ownsLease=false;
        try
        {
            bool reuse=styles==StyleCollectionKind.Ordinary&&preparedModel?.Result.Document is {} prepared&&
                prepared.SnapshotId==inspection.Manifest.SourceSha256;
            var semantic=reuse?preparedModel!.Result:SemanticReader.Build(inspection,token,styles);
            semanticMs=reuse?preparedModel!.Milliseconds:timer.Elapsed.TotalMilliseconds;
            model=semantic.Document;
            semanticDiagnostics=semantic.Issues;
            if(model is null)return Result();
            long start=Stopwatch.GetTimestamp();
            var tableStyles=new TableStyleCascade(model,token);
            typography=new(inspection,model,cancellation:token,tableStyles:tableStyles,
                historicalChoices:historicalChoices,reserveMissingSymbols:true);
            var measured=typography.Measure(token);
            textSnapshot=measured;
            typographyMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start=Stopwatch.GetTimestamp();
            long? regionCpuStart=ThreadCpuClock.Nanoseconds();
            int regionChecks=0;
            void Check()
            {
                token.ThrowIfCancellationRequested();
                double elapsed=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if(regionCpuStart is not {} initial)
                {
                    if(elapsed>15_000)throw new InvalidDataException("PAGE_REGION_TIME_LIMIT");
                    return;
                }
                if(elapsed>60_000)throw new InvalidDataException("PAGE_REGION_WALL_LIMIT");
                if(++regionChecks%64==0&&((ThreadCpuClock.Nanoseconds()??initial+(long)(elapsed*1_000_000))-initial)>15_000_000_000L)
                    throw new InvalidDataException("PAGE_REGION_TIME_LIMIT");
            }
            var bodyFields=ReadBodyFields(model,measured,typography,Check);
            fieldAnalysisMs=model.FieldAnalysisMs+Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var textParagraphs=measured.Paragraphs.ToDictionary(p=>p.Id,StringComparer.Ordinal);
            var tocPlans=ReadEmptyTocs(model,textParagraphs,Check,out var tocContinuations);
            var tocRows=tocPlans.Values.SelectMany(rows=>rows).ToDictionary(row=>row.Id,StringComparer.Ordinal);
            var flowBlocks=tocRows.Count==0?model.BodyBlocks:
                [..model.BodyBlocks.Where(id=>!tocContinuations.ContainsKey(id))
                    .SelectMany(id=>tocPlans.TryGetValue(id,out var rows)?rows.Select(r=>r.Id):[id])];
            var footnoteRequests=ReadFootnoteRequests(model,textParagraphs);
            var endnoteRequests=ReadFootnoteRequests(model,textParagraphs,endnote:true);
            var noteCalls=new HashSet<string>(StringComparer.Ordinal);
            var noteNumbers=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var requests in new[]{footnoteRequests,endnoteRequests}.OfType<FootnoteRequests>())
            {
                noteCalls.UnionWith(requests.Notes.Select(n=>n.Call.NodeId));
                if(requests.ContinuationRule is {} rule)noteCalls.Add(rule.NodeId);
                if(requests.SeparatorRule is {} separatorRule)noteCalls.Add(separatorRule.NodeId);
                foreach(var (id,value) in requests.Numbers)noteNumbers.Add(id,value);
            }
            var noteControls=noteCalls.Count==0?null:noteCalls.Concat(noteNumbers.Keys).ToHashSet(StringComparer.Ordinal);
            long phase=Stopwatch.GetTimestamp();
            var bookmarks=IndexPageBookmarks(model,textParagraphs,bodyFields.Values,Check);
            var tocFields=model.Fields.Where(f=>f.Command=="TOC").ToDictionary(f=>f.Id,StringComparer.Ordinal);
            foreach(var row in tocRows.Values)
            {
                Check();
                var heading=textParagraphs[row.Destination.NodeId];
                var text=typography.ShapeTocRow(row,heading);
                if(text.Context!=heading.Context+"\t"||text.Fragments.Any(f=>f.BidiLevel%2!=0||f.Measure is null))
                    throw new InvalidDataException("PAGE_TOC_ENTRY_TEXT_UNQUALIFIED");
                textParagraphs.Add(row.Id,text);
                bodyFields.Add(row.Id,new(tocFields[row.Field.NodeId],new(null,row.Hyperlink,false,null,0),row.Id,text.MarkStyle,null,null)
                {TocRow=row});
                bookmarks.TryAdd(heading.Id,new(heading.Id,heading.Id,0,heading.ContextLength));
            }
            if(bodyFields.Count>MaxBodyPageFields)throw new InvalidDataException("PAGE_BODY_FIELD_LIMIT");
            bookmarkIndexMs=Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
            var reader=new LayoutReader.Reader(inspection,token,model,tableStyles,nativeComposition:true,
                calculatedBodyFields:bodyFields.Values.Select(f=>f.Field.Id).ToHashSet(StringComparer.Ordinal),placedFootnoteCalls:noteControls,
                generatedTocs:tocPlans,tocContinuations:tocContinuations,endnotesPlanned:endnoteRequests is not null,
                styleCompositionComparison:styleComparison);
            reader.UseDiagramTypography(typography,Check);
            layout=reader.Read();
            styleDecision=reader.StyleDecision;
            sections=ReadSections(model,layout,token);
            var runningParts=model.Stories.Where(s=>s.Kind is "hdr" or "ftr")
                .Select(s=>model.Source(s.RootId).Location.PartUri).ToHashSet(StringComparer.Ordinal);
            var body=flowBlocks.ToHashSet(StringComparer.Ordinal);
            SemanticSource FlowSource(string id)=>tocRows.TryGetValue(id,out var row)?row.Paragraph:model.Source(id);
            var owner=new Dictionary<string,string?>(StringComparer.Ordinal);
            string? Owner(string node)
            {
                if(body.Contains(node))return node;
                if(tocContinuations.TryGetValue(node,out var host))node=host;
                if(tocPlans.TryGetValue(node,out var rows))return rows[0].Id;
                if(owner.TryGetValue(node,out var known))return known;
                string? parent=model.ParentId(node);
                return owner[node]=parent is null?null:Owner(parent);
            }

            var locations=new Dictionary<SourceLocation,string>();
            foreach(var element in model.Elements)
            {
                Check();
                locations.TryAdd(model.Source(element.Id).Location,element.Id);
            }
            var blocked=new Dictionary<string,string>(StringComparer.Ordinal);
            var noSectionGeometry=new HashSet<int>();
            var pendingSections=new HashSet<int>();
            var sectionIds=sections.Where(s=>s.Source is not null).ToDictionary(s=>s.Source!.Value.NodeId,s=>s.Semantic.Number);
            foreach(var issue in semanticDiagnostics.Where(i=>i.Code is "SEMANTIC_MUST_UNDERSTAND" or "SEMANTIC_ALTERNATIVE_UNRESOLVED"
                or "SEMANTIC_SECTION_REFERENCE" or "SEMANTIC_STORY_UNRESOLVED"))
                Register(issue with {BlocksRendering=true});
            foreach(var issue in measured.Diagnostics.Where(i=>i.Code is "TEXT_WORD_RTL_PARTIAL" or "TEXT_HIDDEN_PARAGRAPH_MARK" or
                "TEXT_HIDDEN_CONTROL_UNQUALIFIED" or "TEXT_VISIBILITY_UNRESOLVED"))
                Register(issue with {BlocksRendering=true});
            foreach(var section in sections)
            {
                foreach(var issue in section.Diagnostics)Add(issue);
                foreach(var issue in section.RunningIssues)Add(issue);
                if(section.Diagnostics.Any(i=>i.BlocksRendering))pendingSections.Add(section.Semantic.Number);
                if(section.Diagnostics.Any(i=>i.BlocksRendering&&i.Code is not ("PAGE_SECTION_TRANSITION_PENDING" or "PAGE_CONTINUOUS_INCOMPATIBLE")))
                    noSectionGeometry.Add(section.Semantic.Number);
            }
            foreach(var issue in layout.Issues)Register(issue);
            foreach(var diagnostic in tableStyles.Issues)
            {
                Register(diagnostic.Issue);
                if(diagnostic.Issue.BlocksRendering)blocked.TryAdd(diagnostic.TableId,diagnostic.Issue.Code);
            }

            void Register(FlowIssue issue)
            {
                Check();
                if(issue.Source is {} part&&runningParts.Contains(part.PartUri))return;
                string? node=issue.Source is {} location?locations.GetValueOrDefault(location):null;
                Add(issue);
                if(!issue.BlocksRendering||node is null)return;
                if(issue.Code=="PAGE_FIELD_STORED_PENDING")
                {
                    // The field parser verified its structure and retained a visible source result.
                    // Use that result for candidate measurement while withholding final qualification.
                    gaps.Add(new(model.Source(node),issue.Code));
                    return;
                }
                if(issue.Code is "LAYOUT_PROPERTY_RANGE" or "LAYOUT_ORIENTATION_UNQUALIFIED" or "LAYOUT_PAGE_BODY_INVALID"
                    or "LAYOUT_COLUMNS_UNQUALIFIED" or "LAYOUT_GUTTER_UNQUALIFIED")
                    for(string? id=node;id is not null;id=model.ParentId(id))
                        if(sectionIds.TryGetValue(id,out int number)){noSectionGeometry.Add(number);break;}
                var block=Owner(node);
                if(block is not null)blocked.TryAdd(block,issue.Code);
            }
            // LayoutReader validates document and table structure even on incomplete documents. Only
            // globally fatal limits preclude probing regions; other unresolved claims stay in Issues.
            if(layout.SectionFormats.Count!=model.Sections.Length)
            {
                Add(new("PAGE_SECTION_LAYOUT_UNAVAILABLE",null,"Les régions déclarées n’ont pas pu être résolues.",true));
                return Result();
            }

            var imageRuns=layout.Paragraphs.SelectMany(p=>p.Runs).Where(r=>r.Image is not null)
                .ToDictionary(r=>r.Id,r=>r.Image!,StringComparer.Ordinal);
            var floatingPictures=layout.Paragraphs.SelectMany(p=>p.Runs.Where(r=>r.FloatingPicture is not null)
                .Select(r=>(Picture:r.FloatingPicture!,Paragraph:p.Id))).ToDictionary(p=>p.Picture.Id,StringComparer.Ordinal);
            var floatingFrames=layout.Paragraphs.SelectMany(p=>p.Runs.Where(r=>r.FloatingTextFrame is not null)
                .Select(r=>(Frame:r.FloatingTextFrame!,Paragraph:p.Id))).ToDictionary(p=>p.Frame.Id,StringComparer.Ordinal);
            if(floatingPictures.Count+floatingFrames.Count>512)throw new InvalidDataException("PAGE_FLOATING_PICTURE_LIMIT");
            var floatingIds=floatingPictures.Keys.Concat(floatingFrames.Keys).ToHashSet(StringComparer.Ordinal);
            var floatingStoryIds=floatingFrames.Values.Select(p=>p.Frame.StoryId).ToHashSet(StringComparer.Ordinal);
            var floatingStoryParagraphs=textParagraphs.Values.Where(p=>floatingStoryIds.Contains(p.StoryId))
                .Select(p=>p.Id).ToHashSet(StringComparer.Ordinal);
            if(floatingIds.Count>0)
            {
                var anchorParagraphs=floatingPictures.Values.Select(p=>p.Paragraph).Concat(floatingFrames.Values.Select(p=>p.Paragraph));
                foreach(string id in anchorParagraphs.Distinct(StringComparer.Ordinal))
                {
                    Check();
                    if(!textParagraphs.TryGetValue(id,out var anchorText))continue;
                    int leading=0;
                    foreach(var control in anchorText.Controls)
                    {
                        if(control.Kind!="Drawing"||!floatingIds.Contains(control.Source.NodeId))continue;
                        if(control.Offset==leading){leading++;continue;}
                        blocked.TryAdd(id,"PAGE_FLOATING_ANCHOR_UNQUALIFIED");
                        Add(new("PAGE_FLOATING_ANCHOR_UNQUALIFIED",control.Source.Location,
                            "L’ancre suit un contenu visible ou un contrôle. L’effet sur les coupures reste hors profil.",true));
                    }
                }
            }
            painter=new(model,measured,typography,images:imageRuns,tableStyles:tableStyles,
                floatingPictures:floatingIds.Count==0?null:floatingIds,footnoteCalls:noteCalls.Count==0?null:noteCalls,noteNumbers:noteNumbers);
            sections=MeasureRunningMargins(sections,model,reader,painter,typography,measured,
                textParagraphs,imageRuns,semanticDiagnostics,tableStyles,Check,token);
            FootnoteLayout? footnoteLayout=null;
            var endnoteGroups=new Dictionary<int,EndnoteGroup>();
            var placedNoteStories=new HashSet<string>(StringComparer.Ordinal);
            if(footnoteRequests is {} requested)
            {
                // A reservation at page level must be shared by every body region on the page.
                // Empty proofs never suppress the source notes and their diagnostics.
                var noteSections=sections.Where(s=>requested.Sections.Contains(s.Semantic.Number)).ToArray();
                var noteSection=noteSections[0];
                var noteParagraphs=requested.Notes.Select(n=>Ancestor(model,n.Call.NodeId,SemanticKind.Paragraph))
                    .OfType<string>().ToHashSet(StringComparer.Ordinal);
                var bodyLayout=layout.Paragraphs.Where(p=>p.CellOwner is null).ToArray();
                bool noteInKeep=bodyLayout.Where((p,i)=>noteParagraphs.Contains(p.Id)&&
                    (p.Properties.KeepNext||p.Properties.KeepLines||i>0&&bodyLayout[i-1].Properties.KeepNext&&
                        Composer.CanJoinNative(bodyLayout[i-1],p))).Any();
                if(noteSections.Any(s=>s.ColumnLayout is not null||s.Body.Width!=noteSection.Body.Width)||
                    sections.Skip(1).Any(s=>s.Start is "continuous" or "nextColumn")||
                    floatingFrames.Values.Any(p=>p.Frame.Square is null)||floatingPictures.Values.Any(p=>p.Picture.SideExclusion is null)||
                    noteInKeep&&(floatingPictures.Count>0||floatingFrames.Count>0))
                {
                    BlockNoteCalls("PAGE_FOOTNOTE_DEPENDENCIES_UNQUALIFIED",
                        "La réserve de note doit être partagée avec les autres régions et contraintes de la feuille.");
                }
                else
                {
                    try
                    {
                        footnoteLayout=PrepareFootnoteLayout(model,requested,noteSection,reader,painter,textParagraphs,Check,token,diagnose:Add);
                        placedNoteStories.Add(requested.Separator.RootId);
                        if(requested.Continuation is {} continuation)placedNoteStories.Add(continuation.RootId);
                        foreach(var request in requested.Notes)placedNoteStories.Add(request.Story.RootId);
                    }
                    catch(InvalidDataException error) when(error.Message is "PAGE_FOOTNOTE_CONTENT_UNQUALIFIED" or "PAGE_FOOTNOTE_RULE_UNQUALIFIED")
                    {
                        BlockNoteCalls(error.Message,"Le contenu de la note ou de son séparateur reste hors profil de composition.");
                    }
                }

                void BlockNoteCalls(string code,string message)
                {
                    foreach(var request in requested.Notes)
                    {
                        Check();
                        Register(new(code,request.Call.Location,message,true));
                        gaps.Add(new(request.Call,code));
                    }
                }
            }
            if(endnoteRequests is {} tail)
            {
                foreach(var group in tail.Notes.GroupBy(n=>n.DestinationSection))
                {
                    Check();
                    var requests=group.ToImmutableArray();
                    var section=sections[group.Key-1];
                    try
                    {
                        if(section.ColumnLayout is not null||floatingFrames.Count>0||
                            floatingPictures.Values.Any(p=>p.Picture.SideExclusion is null))
                            throw new InvalidDataException("PAGE_ENDNOTE_REGION_UNQUALIFIED");
                        var preparedTail=PrepareFootnoteLayout(model,tail with{Notes=requests},section,reader,painter,textParagraphs,Check,token,
                            endnote:true,diagnose:Add);
                        endnoteGroups.Add(group.Key,new(preparedTail,requests));
                        placedNoteStories.Add(tail.Separator.RootId);
                        if(tail.Continuation is {} continuation)placedNoteStories.Add(continuation.RootId);
                        foreach(var request in requests)placedNoteStories.Add(request.Story.RootId);
                    }
                    catch(InvalidDataException error) when(error.Message is "PAGE_FOOTNOTE_CONTENT_UNQUALIFIED" or "PAGE_FOOTNOTE_RULE_UNQUALIFIED"
                        or "PAGE_ENDNOTE_REGION_UNQUALIFIED")
                    {
                        foreach(var request in requests)
                            Register(new("PAGE_ENDNOTE_REGION_UNQUALIFIED",request.Call.Location,
                                "La composition de la note de fin ou de ses séparateurs est hors profil ("+error.Message+").",true));
                    }
                }
            }
            var paintedFrames=new Dictionary<string,(FloatingTextFrame Frame,ComposedPage Page)>(StringComparer.Ordinal);
            if(floatingFrames.Count>0)
            {
                long frameStart=Stopwatch.GetTimestamp();
                var frameStories=model.Stories.Where(s=>floatingStoryIds.Contains(s.RootId))
                    .ToDictionary(s=>s.RootId,StringComparer.Ordinal);
                var frameText=measured.Paragraphs.Where(p=>floatingStoryIds.Contains(p.StoryId)).ToLookup(p=>p.StoryId);
                var frameFields=model.Fields.Where(f=>floatingStoryIds.Contains(f.StoryId)).ToLookup(f=>f.StoryId);
                foreach(var item in floatingFrames.Values)
                {
                    Check();
                    var frame=item.Frame;
                    if(!blocked.ContainsKey(item.Paragraph)&&frameStories.TryGetValue(frame.StoryId,out var story)&&
                        !frameFields[frame.StoryId].Any())
                    {
                        if(PrepareFrame(frame,story,reader,painter,textParagraphs,imageRuns,frameText[frame.StoryId],Check,Add,token) is {} content)
                        {paintedFrames.Add(frame.Id,content);continue;}
                    }
                    blocked.TryAdd(item.Paragraph,"PAGE_FLOATING_TEXTBOX_LAYOUT_UNQUALIFIED");
                    Add(new("PAGE_FLOATING_TEXTBOX_LAYOUT_UNQUALIFIED",frame.Drawing,
                        "Texte de la zone flottante hors des contraintes mesurées.",true));
                }
                floatingTextMeasurementMs=Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;


            }
            markers=painter.MeasureListMarkers(layout.Paragraphs,textParagraphs,token);
            foreach(var marker in markers.Values)
                foreach(var issue in marker.Issues)
                    Add(issue with{BlocksRendering=BlocksLine(issue)});
            var validTables=new List<LayoutTable>();
            var regionalDrafts=new Dictionary<string,LayoutTable>(StringComparer.Ordinal);
            var regionalFits=new Dictionary<(string,long),RegionalTable>();
            int intrinsicTabWork=0;
            var adjusted=layout.Paragraphs.ToDictionary(p=>p.Id,p=>p.CellOwner is null?
                p with{Geometry=sections[p.Section-1].Format,FlowColumns=sections[p.Section-1].ColumnLayout}:p,StringComparer.Ordinal);
            foreach(var group in floatingPictures.Values.Where(x=>x.Picture.Exclusion is not null).GroupBy(x=>x.Paragraph))
            {
                var anchors=group.Take(2).ToArray();
                if(anchors.Length==1&&adjusted.TryGetValue(group.Key,out var p)&&p.CellOwner is null&&
                    textParagraphs.TryGetValue(p.Id,out var paragraph)&&paragraph.Controls.Count(c=>c.Kind=="Drawing")==1)
                    adjusted[p.Id]=p with{FlowExclusion=anchors[0].Picture.Exclusion};
                else
                {
                    blocked.TryAdd(group.Key,"PAGE_FLOW_EXCLUSION_UNQUALIFIED");
                    Add(new("PAGE_FLOW_EXCLUSION_UNQUALIFIED",model.Source(anchors[0].Picture.Id).Location,
                        "Les exclusions multiples ou rattachées à une cellule demandent une composition couplée.",true));
                }
            }
            var sideAnchors=floatingPictures.Values.Select(x=>(Id:x.Picture.Id,x.Paragraph,Exclusion:x.Picture.SideExclusion))
                .Concat(floatingFrames.Values.Where(x=>paintedFrames.ContainsKey(x.Frame.Id)).Select(x=>
                    (x.Frame.Id,x.Paragraph,paintedFrames[x.Frame.Id].Frame.Exclusion)))
                .Where(x=>x.Exclusion is not null).ToArray();
            bool hasThrough=sideAnchors.Any(x=>!x.Exclusion!.ThroughPolygon.IsEmpty);
            bool sideProfile=sideAnchors.Length>0&&sections.Skip(1).All(s=>s.Start is not ("continuous" or "nextColumn"))&&
                sideAnchors.All(a=>adjusted.TryGetValue(a.Paragraph,out var p)&&sections[p.Section-1].ColumnLayout is not {Balance:true})&&
                (floatingIds.Count==sideAnchors.Length||!hasThrough&&
                    floatingPictures.Values.All(p=>p.Picture.SideExclusion is not null||p.Picture.Exclusion is null))&&
                (!hasThrough||adjusted.Values.Where(p=>p.CellOwner is null&&p.TableId is null)
                    .All(p=>!p.Properties.KeepNext&&!p.Properties.KeepLines&&!p.Properties.Widow&&
                        p.Properties.LineRule=="exact"));
            foreach(var anchor in sideAnchors)
            {
                Check();
                if(sideProfile&&adjusted.TryGetValue(anchor.Paragraph,out var p)&&
                    (p.CellOwner is null||paintedFrames.ContainsKey(anchor.Id))&&
                    textParagraphs.TryGetValue(p.Id,out var paragraph)&&paragraph.Controls.Count(c=>c.Kind=="Drawing")==1)
                    adjusted[p.Id]=p with{SideExclusion=anchor.Exclusion};
                else
                {
                    blocked.TryAdd(anchor.Paragraph,"PAGE_FLOW_SIDE_PROFILE_UNQUALIFIED");
                    Add(new("PAGE_FLOW_SIDE_PROFILE_UNQUALIFIED",model.Source(anchor.Id).Location,
                        "Habillage latéral sans combinaison qualifiée avec les autres contraintes du document.",true));
                }
            }
            var intrinsicNoteWidths=new Dictionary<string,long>(StringComparer.Ordinal);
            int intrinsicNoteGlyphs=0;
            foreach(var table in layout.Tables)
            {
                Check();
                if(prepareReadingTables)readingGrids.Add(table.Id,table);
                if(!adjusted.TryGetValue(table.Id,out var sentinel)||noSectionGeometry.Contains(sentinel.Section))continue;
                try
                {
                    var paperGeometry=sentinel.Geometry;
                    var section=sections[sentinel.Section-1];
                    if(section.ColumnLayout is {} columns)
                    {
                        if(table.AutoFit&&columns.Columns.Any(c=>c.Width!=columns.Narrowest))
                            regionalDrafts.Add(table.Id,table);
                        paperGeometry=paperGeometry with
                        {
                            Right=paperGeometry.Width-paperGeometry.Left-Points(columns.Narrowest)
                        };
                    }
                    var tableLayout=Composer.FitPageTable(table with
                    {
                        PhysicalPage=paperGeometry,AvailableWidth=paperGeometry.Width-paperGeometry.Left-paperGeometry.Right-table.Indent
                    },adjusted,Check,MeasureCell);
                    validTables.Add(tableLayout);
                    if(prepareReadingTables)readingGrids[table.Id]=tableLayout;
                    if(tableLayout.WidthResult is {} geometry)
                    {
                        if(geometry.Overhang)
                            Add(new("PAGE_TABLE_MARGIN_OVERHANG",table.Source,"Les dimensions du tableau dépassent la marge tout en restant dans le papier.",false));
                        if(!geometry.ContentVerified)
                        {
                            blocked.TryAdd(table.Id,"PAGE_TABLE_CONTENT_UNMEASURED");
                            Add(new("PAGE_TABLE_CONTENT_UNMEASURED",table.Source,
                                "Grille résolue. Au moins une cellule n’a pas de mesure intrinsèque qualifiée.",true));
                        }
                    }
                }
                catch(TableWidthFailure error)
                {
                    Add(new(error.Message,error.Location,error.Detail.Length==0?"Les contraintes de largeur du tableau ne sont pas résolues.":error.Detail,true));
                    blocked.TryAdd(table.Id,error.Message);
                }
            }
            layout=layout with {Tables=validTables.ToArray(),Paragraphs=layout.Paragraphs.Select(p=>adjusted[p.Id]).ToArray()};
            var paragraphRegions=new Dictionary<string,LineRegion>(StringComparer.Ordinal);
            var usableCellIds=validTables.SelectMany(t=>t.Cells).Select(c=>c.Id).ToHashSet(StringComparer.Ordinal);
            var aliases=validTables.SelectMany(t=>t.Cells).Where(c=>c.MergeOrigin is not null).SelectMany(c=>c.ParagraphIds)
                .Where(id=>textParagraphs.TryGetValue(id,out var p)&&p.ContextLength==0).ToHashSet(StringComparer.Ordinal);
            foreach(var p in layout.Paragraphs)
            {
                Check();
                if(p.TableId is not null||noSectionGeometry.Contains(p.Section)||!textParagraphs.ContainsKey(p.Id)||aliases.Contains(p.Id)||
                    p.CellOwner is {} cell&&!usableCellIds.Contains(cell))continue;
                var section=sections[p.Section-1];
                long width=p.CellOwner is null?section.ColumnLayout?.Narrowest??section.Body.Width:
                    Emu(p.Geometry.Width)-Emu(p.Geometry.Left)-Emu(p.Geometry.Right);
                if(width>0)paragraphRegions.Add(p.Id,new(p.CellOwner??$"section-{p.Section}",0,width)
                {
                    CenteredImageOverhang=Emu(p.CenteredImageOverhang),
                    PaperRight=p.CellOwner is null&&section.ColumnLayout is null?
                        width+Emu(section.MirrorMargins?Math.Max(section.Format.Left,section.Format.Right):section.Format.Right):null,
                    RightMargin=p.CellOwner is null&&section.ColumnLayout is null?
                        Emu(section.MirrorMargins?Math.Min(section.Format.Left,section.Format.Right):section.Format.Right):0
                });
            }
            regionsMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start=Stopwatch.GetTimestamp();
            lines=painter.Compose(r=>paragraphRegions[r.ParagraphId],token,paragraphRegions.Keys.ToHashSet(StringComparer.Ordinal),markers,
                orderedParagraphs:tocRows.Count==0?null:textParagraphs.Values);
            linesMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start=Stopwatch.GetTimestamp();
            regionCpuStart=ThreadCpuClock.Nanoseconds();
            regionChecks=0;
            var baseLines=lines;
            string? verifiedReplayState=null;
            int columnWork=0,columnGlyphs=0;
            bool variableWidths=sideProfile||sections.Any(s=>s.ColumnLayout is {} columns&&columns.Columns.Any(c=>c.Width!=columns.Narrowest));
            var baseBlocked=new Dictionary<string,string>(blocked,StringComparer.Ordinal);
            var cellsByTable=validTables.ToDictionary(t=>t.Id,t=>t.Cells,StringComparer.Ordinal);
            var included=bodyFields.Where(entry=>paragraphRegions.ContainsKey(entry.Key)).ToDictionary(StringComparer.Ordinal);
            if(bodyFields.Count>0)
            {
                try
                {
                    lines=StabilizeFields(included);
                }
                catch(InvalidDataException error) when(error.Message.EndsWith("_LIMIT",StringComparison.Ordinal))
                {
                    fieldStability=error.Message;
                    lines=baseLines;
                }
                if(fieldStability!="Stable")
                    foreach(var field in bodyFields.Values)
                    {
                        token.ThrowIfCancellationRequested();
                        const string unresolved="PAGE_FIELD_BODY_UNSTABLE";
                        string reason=fieldPasses.Count==0?fieldStability??"PAGE_FIELD_UNPLACED":
                            fieldPasses[^1].Fields.FirstOrDefault(f=>f.FieldId==field.ResultId)?.Diagnostic??fieldStability??unresolved;
                        if(!reason.StartsWith("PAGE_",StringComparison.Ordinal)&&!reason.StartsWith("LINE_",StringComparison.Ordinal))
                            reason=unresolved;
                        var source=model.Source(field.Field.Id);
                        // The final candidate uses the measured source lines, including an empty source
                        // result where the document supplies no cache. Trial values never leave the passes.
                        gaps.Add(new(source,reason,field.StoredRange));
                        Add(new(reason,source.Location,field.StoredRange is null?
                            "Champ sans résultat source. La composition candidate reste à stabiliser.":
                            "Le résultat stocké reste dans la composition candidate. Le champ reste à stabiliser.",true));
                    }
            }
            var byParagraph=GroupLines(lines);
            RegisterLines(byParagraph,blocked,Add);
            foreach(var p in measured.Paragraphs)
            {
                Check();
                if(!paragraphRegions.ContainsKey(p.Id)&&!tocPlans.ContainsKey(p.Id)&&!tocContinuations.ContainsKey(p.Id)&&
                    !aliases.Contains(p.Id)&&!floatingStoryParagraphs.Contains(p.Id)&&
                    !runningParts.Contains(model.Source(p.Id).Location.PartUri)&&!placedNoteStories.Contains(p.StoryId)&&
                    !reader.InactiveNoteParts.Contains(model.Source(p.Id).Location.PartUri))
                    gaps.Add(new(model.Source(p.Id),Owner(p.Id) is null?"PAGE_STORY_PENDING":"PAGE_REGION_UNAVAILABLE"));
            }
            if(prepareReadingTables)
                foreach(var table in validTables)
                {
                    Check();
                    if(blocked.ContainsKey(table.Id)||table.Cells.Count==0||table.Rows.Count==0||
                        table.Cells.Where(c=>c.MergeOrigin is null).SelectMany(c=>c.ParagraphIds).Any(p=>!byParagraph.ContainsKey(p)))continue;
                    try
                    {
                        readingTables.Add(table.Id,Composer.PlaceContinuousTable(table,adjusted,byParagraph,painter,Check));
                    }
                    catch(InvalidDataException error) when(error.Message.StartsWith("LAYOUT_",StringComparison.Ordinal)||
                        error.InnerException is not null)
                    {
                        Add(new(error.Message,table.Source,"Placement continu du tableau indisponible.",false));
                    }
                }
            long bodyPlacementStarted=Stopwatch.GetTimestamp();
            var placedBody=PlaceBody(byParagraph,blocked,Add,true);
            pages=placedBody.Composition;
            var chosen=placedBody.Chosen;
            if(verifiedReplayState is not null&&
                FieldState(pages,fieldPasses[^1].Fields,PlaceBookmarks(pages,bookmarks,Check),placedBody.Obstacle,Check)!=verifiedReplayState)
            {
                fieldStability="PAGE_FIELD_REPLAY_DIVERGED";
                Add(new(fieldStability,null,"La reprise de région et le placement intégral diffèrent. Pages non publiables.",true));
            }
            if(fieldPasses.Count==0)initialBodyPlacementMs=Stopwatch.GetElapsedTime(bodyPlacementStarted).TotalMilliseconds;
            if(pages.Pages.Count==0)Add(new("PAGE_NO_PLACED_BODY",null,"Corps sans page certifiable. Lecture de secours disponible selon l’inspection.",true));
            foreach(var p in layout.Paragraphs.Where(p=>p.TableId is null))
            {
                Check();
                var block=Owner(p.Id);
                if(block is not null&&(pages.Pages.Count==0||!chosen.Contains(block)))
                    gaps.Add(new(FlowSource(p.Id),blocked.GetValueOrDefault(block)??"PAGE_AFTER_UNPLACED"));
            }
            if(variableWidths&&pages.Pages.Count>0)
            {
                var rendered=PlacedLines(pages);
                lines=lines with{Lines=[..lines.Lines.Select(l=>l.ParagraphId).Distinct(StringComparer.Ordinal)
                    .SelectMany(id=>rendered.GetValueOrDefault(id,byParagraph[id]))]};
            }
            if(footnoteLayout is not null||endnoteRequests is not null)
            {
                lines=lines with{Lines=[..lines.Lines,..pages.Pages.SelectMany(p=>p.Footnotes)
                    .SelectMany(n=>n.Lines).Where(l=>!l.IsRepeated&&l.Native is not null).Select(l=>l.Native!)]};
                var committed=pages.Pages.SelectMany(p=>p.Footnotes).Where(n=>n.Call is not null)
                    .Select(n=>n.Call!.Value.NodeId).ToHashSet(StringComparer.Ordinal);
                foreach(var pending in (footnoteRequests?.Notes??[]).Where(n=>!committed.Contains(n.Call.NodeId)))
                    gaps.Add(new(pending.Call,"PAGE_FOOTNOTE_UNPLACED"));
                foreach(var pending in (endnoteRequests?.Notes??[]).Where(n=>!committed.Contains(n.Call.NodeId)))
                    gaps.Add(new(pending.Call,"PAGE_ENDNOTE_UNPLACED"));
            }
            var placed=pages.Pages.SelectMany(p=>p.Lines).Where(l=>!l.IsRepeated&&l.Native is not null)
                .Select(l=>(l.ParagraphId,l.Native!.Index)).ToHashSet();
            if(footnoteLayout is not null||endnoteGroups.Count>0)
                foreach(var l in pages.Pages.SelectMany(p=>p.Footnotes).SelectMany(n=>n.Lines))
                    if(l.Native is {} note)placed.Add((l.ParagraphId,note.Index));
            foreach(var line in lines.Lines)
            {
                Check();
                if(placed.Contains((line.ParagraphId,line.Index)))continue;
                string? block=Owner(line.ParagraphId);
                var reason=line.Diagnostics.FirstOrDefault(BlocksLine)?.Code??
                    (block is null?null:blocked.GetValueOrDefault(block))??"PAGE_AFTER_UNPLACED";
                gaps.Add(new(line.ParagraphSource,reason,line.ContextRange));
            }
            if(floatingIds.Count>0)
            {
                long floatingStarted=Stopwatch.GetTimestamp();
                var handled=new HashSet<string>(StringComparer.Ordinal);
                var physicalPages=ImmutableArray.CreateBuilder<ComposedPage>(pages.Pages.Count);
                foreach(var page in pages.Pages)
                {
                    var anchored=ImmutableArray.CreateBuilder<PlacedFloatingPicture>();
                    var textFrames=ImmutableArray.CreateBuilder<PlacedFloatingTextFrame>();
                    foreach(var line in page.Lines)
                    {
                        Check();
                        if(line.Native is not {} native)continue;
                        foreach(var part in native.Parts)
                        {
                            Check();
                            if(part.Control is not {Kind:"Drawing"} control)continue;
                            string id=control.Source.NodeId;
                            if(paintedFrames.TryGetValue(id,out var painted))
                            {
                                var frame=painted.Frame;
                                var column=line.CellId is not null?(line.NativeOffsetX,Points(native.Region.Width)):
                                    line.FlowRegion is {} flow?(Points(flow.Bounds.Left),Points(flow.Bounds.Width)):((float,float)?)null;
                                float? character=null;
                                if(frame.HorizontalReference=="character")
                                {
                                    int index=native.Parts.IndexOf(part);
                                    var visual=native.Visual.Where(v=>v.Part==index).Take(2).ToArray();
                                    if(native.Properties.RightToLeft||visual is not [var occurrence])
                                    {
                                        handled.Add(id);
                                        RefuseDrawing(id,"PAGE_FLOATING_CHARACTER_REFERENCE");
                                        continue;
                                    }
                                    character=line.NativeOffsetX+Points(occurrence.X);
                                }
                                var (frameX,frameY)=frame.PageOrigin(page.Geometry,page.Number,line.FloatingAnchorTop??line.NativeTop,column,character);
                                if(!handled.Add(id)||native.Index!=0||line.ParagraphId!=floatingFrames[id].Paragraph||
                                    frame.Square is not null&&line.SideExclusion is null)
                                    RefuseDrawing(id,"PAGE_FLOATING_TEXTBOX_ANCHOR_UNQUALIFIED");
                                else
                                {
                                    var placedFrame=new PlacedFloatingTextFrame(frame with{X=frameX,Y=frameY},frame.Rotation==0?
                                        MoveFrame(painted.Page,frameX,frameY,line.Section,page.Geometry):
                                        MoveFrame(painted.Page,0,0,line.Section,painted.Page.Geometry));
                                    bool frameOutside=frameX>=page.Geometry.Width||frameY>=page.Geometry.Height||
                                        frameX+frame.Width<=0||frameY+frame.Height<=0;
                                    bool overflowOutside=!placedFrame.OverflowFits(page.Geometry);
                                    if(frame.Square is not null&&(frameOutside&&frame.Exclusion is not {ClipToSheet:true}||
                                        overflowOutside&&frame.WrapText))
                                        RefuseDrawing(id,frameOutside?"PAGE_FLOATING_TEXTBOX_OUTSIDE_SHEET":"PAGE_FLOATING_TEXTBOX_OVERFLOW_OUTSIDE_SHEET");
                                    else
                                    {
                                        if(placedFrame.CrossesSheet(page.Geometry)||overflowOutside)
                                            Add(new("PAGE_FLOATING_TEXTBOX_CLIPPED_BY_SHEET",frame.Drawing,
                                                "L’ancre est conservée et la peinture est découpée aux limites de la feuille.",false));
                                        textFrames.Add(placedFrame.ClipOn(page.Geometry));
                                    }
                                }
                                continue;
                            }
                            if(!floatingPictures.TryGetValue(id,out var anchor))continue;
                            var picture=anchor.Picture;
                            if(!handled.Add(picture.Id)||native.Index!=0||line.ParagraphId!=anchor.Paragraph)
                            {RefuseDrawing(id,"PAGE_FLOATING_ANCHOR_UNQUALIFIED");continue;}
                            var format=page.Geometry;
                            float horizontal=picture.HorizontalReference switch
                            {
                                "column" when line.FlowRegion is {} flow=>Points(flow.Bounds.Left),
                                "margin" or "column"=>format.Left,
                                _=>0
                            };
                            float x=horizontal+Points(picture.XOffset);
                            float y=(picture.YFromMargin?format.Top:0)+Points(picture.YOffset);
                            PageRegion? exclusion=null;
                            if(picture.Exclusion is {} reserved)
                            {
                                long top=Emu(line.NativeTop)-reserved.Height;
                                y=Points(top+reserved.Above);
                                var bounds=line.FlowRegion?.Bounds??new(Emu(format.Left),Emu(format.Top),
                                    Emu(format.Width-format.Left-format.Right),Emu(format.BodyHeight));
                                exclusion=new(bounds.Left,top,bounds.Width,reserved.Height);
                                if(top<bounds.Top-Emu(.01f)||x-picture.Image.Effects.Left<Points(bounds.Left)-.01f||
                                    x+picture.Image.Width+picture.Image.Effects.Right>Points(bounds.Left+bounds.Width)+.01f)
                                {RefuseDrawing(id,"PAGE_FLOW_EXCLUSION_OUTSIDE_REGION");continue;}
                            }
                            else if(picture.SideExclusion is not null)
                            {
                                if(line.SideExclusion is not {} side)
                                {RefuseDrawing(id,"PAGE_FLOW_SIDE_EXCLUSION_UNPLACED");continue;}
                                y=line.NativeTop+Points(picture.YOffset);
                                exclusion=side;
                            }
                            if(x>=format.Width||y>=format.Height||x+picture.Image.Width<=0||y+picture.Image.Height<=0)
                            {RefuseDrawing(id,"PAGE_FLOATING_OUTSIDE_SHEET");continue;}
                            anchored.Add(new(picture,line.ParagraphId,x,y){ExcludedRegion=exclusion});
                        }
                    }
                    physicalPages.Add(anchored.Count==0&&textFrames.Count==0?page:page with
                    {FloatingPictures=anchored.ToImmutable(),FloatingTextFrames=textFrames.ToImmutable()});
                }
                foreach(var anchor in floatingPictures.Values)
                {
                    Check();
                    if(chosen.Contains(anchor.Paragraph)&&!handled.Contains(anchor.Picture.Id))
                        RefuseDrawing(anchor.Picture.Id,"PAGE_FLOATING_ANCHOR_UNQUALIFIED");
                }
                foreach(var frame in floatingFrames.Values)
                {
                    Check();
                    if(chosen.Contains(frame.Paragraph)&&!handled.Contains(frame.Frame.Id))
                        RefuseDrawing(frame.Frame.Id,"PAGE_FLOATING_TEXTBOX_ANCHOR_UNQUALIFIED");
                }
                pages=pages with{Pages=physicalPages.ToImmutable()};
                floatingPlacementMs=Stopwatch.GetElapsedTime(floatingStarted).TotalMilliseconds;

                void RefuseDrawing(string id,string code)
                {
                    var source=model.Source(id);
                    Add(new(code,source.Location,"Le flottant ne possède pas de position certifiable dans la feuille de son ancre.",true));
                    gaps.Add(new(source,code));
                }
            }
            bool completeBody=pages.Pages.Count>0&&gaps.Count==0&&!issues.Any(i=>i.BlocksRendering)&&
                !semanticDiagnostics.Any(i=>i.BlocksRendering)&&!measured.Diagnostics.Any(i=>i.BlocksRendering);
            long storiesStarted=Stopwatch.GetTimestamp();
            var running=ComposeRunningStories(pages,sections,model,reader,painter,typography,measured,
                textParagraphs,imageRuns,semanticDiagnostics,tableStyles,bookmarks,Check,Add,gaps,token,completeBody);
            (pages,storyRegions)=(running.Item1,running.Item2);
            fieldResolutionMs+=running.ResolutionMs;
            fieldRecompositionMs+=running.RecompositionMs;
            runningFieldResolutionMs=running.ResolutionMs;
            runningFieldMeasurementMs=running.RecompositionMs;
            runningStoriesMs=Stopwatch.GetElapsedTime(storiesStarted).TotalMilliseconds;
            assemblyMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return Result();

            (float Minimum,float Preferred) MeasureCell(LayoutParagraph p)
            {
                var text=textParagraphs.GetValueOrDefault(p.Id);
                if(text is not {RightToLeft:false,Controls:[{Kind:"Tab"} tab]}||
                    text.Context.AsSpan(tab.Offset+1).IndexOfAnyExcept(' ')>=0||
                    p.List is not null||p.Properties.Alignment!="left"||
                    model.Name(tab.Source.NodeId)!=SemanticIndex.QName(SemanticIndex.W,"tab"))
                    return Intrinsic(p,text,imageRuns,Check,markers.GetValueOrDefault(p.Id),noteCallWidth:NoteWidth,
                        floatingDrawings:floatingIds);

                var trial=painter.Compose(_=>new(p.Id,0,Emu(1440)),token,markers:markers,orderedParagraphs:[text]);
                intrinsicTabWork+=trial.Performance.Work;
                if(intrinsicTabWork>1_000_000)throw new InvalidDataException("PAGE_AUTOFIT_TAB_MEASUREMENT_LIMIT");
                if(trial.Lines is not [var line]||line.Occupied is not {} extent||
                    line.Tabs.Length!=1||line.Diagnostics.Any(BlocksLine))
                    throw new InvalidDataException("PAGE_AUTOFIT_INLINE_PENDING");
                float width=Points(extent)+p.Properties.Left+p.Properties.Right+Math.Max(0,p.Properties.FirstLine);
                return (width,width);

                long? NoteWidth(TextParagraph paragraph,TextControl call)
                {
                    if(intrinsicNoteWidths.TryGetValue(call.Source.NodeId,out long width))return width;
                    if(painter.MeasureNoteCall(paragraph,call,Check,count=>
                    {
                        intrinsicNoteGlyphs+=count;
                        if(intrinsicNoteGlyphs>8192)throw new InvalidDataException("PAGE_AUTOFIT_NOTE_GLYPH_LIMIT");
                    }) is not {} measuredWidth)return null;
                    intrinsicNoteWidths.Add(call.Source.NodeId,measuredWidth);
                    return measuredWidth;
                }
            }

            RegionalTable FitRegion(string id,long width)
            {
                if(regionalFits.TryGetValue((id,width),out var existing))return existing;
                if(regionalFits.Count>=96)throw new InvalidDataException("PAGE_TABLE_REGION_LIMIT");
                var draft=regionalDrafts[id];
                var format=adjusted[id].Geometry;
                var paper=format with{Right=format.Width-format.Left-Points(width)};
                var cellParagraphs=draft.Cells.SelectMany(c=>c.ParagraphIds).Distinct(StringComparer.Ordinal)
                    .ToDictionary(p=>p,p=>adjusted[p],StringComparer.Ordinal);
                var fitted=Composer.FitPageTable(draft with{PhysicalPage=paper,AvailableWidth=Points(width)-draft.Indent},
                    cellParagraphs,Check,MeasureCell);
                if(fitted.WidthResult is not {ContentVerified:true})
                    throw new TableWidthFailure("PAGE_TABLE_CONTENT_UNMEASURED",draft.Source,"Mesure intrinsèque hors profil.");
                var ids=fitted.Cells.Where(c=>c.MergeOrigin is null).SelectMany(c=>c.ParagraphIds);
                var result=new RegionalTable(fitted,new(ids.Select(p=>cellParagraphs[p]).ToArray(),[]){Tables=[fitted]});
                regionalFits.Add((id,width),result);
                return result;
            }

            Dictionary<string,ImmutableArray<ComposedLine>> GroupLines(ComposedLines source)=>
                source.Lines.GroupBy(l=>l.ParagraphId).ToDictionary(g=>g.Key,g=>g.ToImmutableArray(),StringComparer.Ordinal);

            Dictionary<string,ImmutableArray<ComposedLine>> PlacedLines(Composition placed)=>
                placed.Pages.SelectMany(p=>p.Lines).Where(l=>!l.IsRepeated&&l.Native is not null)
                    .GroupBy(l=>l.ParagraphId,StringComparer.Ordinal)
                    .ToDictionary(g=>g.Key,g=>g.Select(l=>l.Native!).ToImmutableArray(),StringComparer.Ordinal);

            ComposedLines StabilizeFields(Dictionary<string,BodyPageField> active)
            {
                if(active.Count==0)
                {
                    fieldStability="PAGE_FIELD_UNPLACED";
                    return baseLines;
                }
                var currentLines=GroupLines(baseLines);
                var activeText=active.Keys.Select(id=>textParagraphs[id]).ToArray();
                var values=active.ToDictionary(entry=>entry.Key,entry=>entry.Value.PropertyValue??entry.Value.StoredValue??"0",StringComparer.Ordinal);
                var dirty=active.Keys.ToHashSet(StringComparer.Ordinal);
                var seen=new HashSet<string>(StringComparer.Ordinal);
                var sourceOrder=flowBlocks.Select((block,index)=>(block,index)).ToDictionary(x=>x.block,x=>x.index,StringComparer.Ordinal);
                bool canReplay=sections.All(s=>s.ColumnLayout is not {Balance:true});
                List<PageRegionCheckpoint>? previousCheckpoints=null;
                IReadOnlyList<ComposedPage>? previousSheets=null;
                string? previous=null;
                var budget=new LineBudget();
                int lineWork=baseLines.Performance.Work,lineGlyphs=baseLines.Performance.ShapedGlyphs,lineCount=baseLines.Lines.Length;
                for(int pass=1;pass<=MaxFieldPasses;pass++)
                {
                    Check();
                    if(dirty.Count>0)
                    {
                        long started=Stopwatch.GetTimestamp();
                        var generated=dirty.ToDictionary(id=>id,id=>new GeneratedPageField(model.Source(active[id].Field.Id),
                            values[id],active[id].Style){StoredRange=active[id].StoredRange,ResultId=active[id].TocRow?.Id},StringComparer.Ordinal);
                        var changed=painter.Compose(r=>paragraphRegions[r.ParagraphId],token,dirty,markers,activeText,generated);
                        lineWork+=changed.Performance.Work;
                        lineGlyphs+=changed.Performance.ShapedGlyphs;
                        lineCount+=changed.Lines.Length;
                        if(lineWork>budget.MaxWork||lineGlyphs>budget.MaxGlyphs||lineCount>budget.MaxLines)
                            throw new InvalidDataException("PAGE_FIELD_LINE_BUDGET_LIMIT");
                        foreach(var entry in GroupLines(changed))currentLines[entry.Key]=entry.Value;
                        fieldRecompositionMs+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                    long placedStarted=Stopwatch.GetTimestamp();
                    var trialBlocked=new Dictionary<string,string>(baseBlocked,StringComparer.Ordinal);
                    bool trialBlocks=false;
                    void TrialIssue(FlowIssue issue)=>trialBlocks|=issue.BlocksRendering;
                    RegisterLines(currentLines,trialBlocked,TrialIssue);
                    PageRegionReplay? replay=null;
                    var checkpoints=canReplay?new List<PageRegionCheckpoint>():null;
                    if(previousCheckpoints is {Count:>0}&&previousSheets is not null)
                    {
                        int changedAt=dirty.Select(id=>Owner(id) is {} block?sourceOrder.GetValueOrDefault(block,-1):-1)
                            .DefaultIfEmpty(int.MaxValue).Min();
                        foreach(var checkpoint in previousCheckpoints)
                        {
                            Check();
                            if(checkpoint.BlockIndex>changedAt)break;
                            replay=new(checkpoint,previousSheets);
                        }
                    }
                    var candidate=PlaceBody(currentLines,trialBlocked,TrialIssue,false,replay,checkpoints);
                    double placementMs=Stopwatch.GetElapsedTime(placedStarted).TotalMilliseconds;
                    if(pass==1)initialBodyPlacementMs=placementMs;
                    else fieldRecompositionMs+=placementMs;
                    var trial=candidate.Composition;
                    previousCheckpoints=trial.Pages.Count>0?checkpoints:null;
                    previousSheets=trial.Pages;
                    long resolvedStarted=Stopwatch.GetTimestamp();
                    var anchors=PlaceBookmarks(trial,bookmarks,Check);
                    bool complete=trial.Pages.Count>0&&candidate.Chosen.Count==flowBlocks.Length&&!trialBlocks&&
                        !issues.Any(i=>i.BlocksRendering)&&!semanticDiagnostics.Any(i=>i.BlocksRendering)&&
                        !measured.Diagnostics.Any(i=>i.BlocksRendering)&&
                        (variableWidths||trial.Pages.Sum(p=>p.Lines.Count(l=>!l.IsRepeated))==baseLines.Lines.Select(l=>l.ParagraphId).Distinct()
                            .Sum(id=>currentLines[id].Length));
                    var resolved=ResolveBodyFields(trial,sections,bodyFields,bookmarks,anchors,model,complete,Check);
                    fieldResolutionMs+=Stopwatch.GetElapsedTime(resolvedStarted).TotalMilliseconds;
                    var bodyIssue=trial.Issues.FirstOrDefault(i=>i.BlocksRendering);
                    fieldPasses.Add(new(pass,trial.Pages.Count,trial.Pages.Sum(p=>p.Lines.Count(l=>!l.IsRepeated)),
                        bodyIssue?.Code??candidate.Obstacle,bodyIssue?.Source is {} location&&locations.TryGetValue(location,out var node)?
                            model.Source(node):candidate.ObstacleSource,resolved));
                    long checkedStarted=Stopwatch.GetTimestamp();
                    dirty.Clear();
                    foreach(var (field,resolution) in bodyFields.Values.Zip(resolved))
                    {
                        if(resolution.Text is not {} value||!values.TryGetValue(field.ParagraphId,out string? current)||value==current)continue;
                        values[field.ParagraphId]=value;
                        dirty.Add(field.ParagraphId);
                    }
                    bool unresolved=resolved.Any(f=>f.Diagnostic is not null);
                    string state=FieldState(trial,resolved,anchors,candidate.Obstacle,Check);
                    bool stable=!unresolved&&dirty.Count==0&&state==previous;
                    bool oscillation=state!=previous&&!seen.Add(state);
                    stabilizationMs+=Stopwatch.GetElapsedTime(checkedStarted).TotalMilliseconds;
                    if(stable)
                    {
                        if(replayedFieldSheets.Any(n=>n>0))verifiedReplayState=state;
                        fieldStability="Stable";
                        fieldBookmarks=anchors.ToImmutableDictionary(StringComparer.Ordinal);
                        pageLinkTargets=model.References.Where(r=>r.Kind=="bookmark"&&r.Status==ReferenceStatus.Resolved&&
                                r.TargetId is {} target&&anchors.ContainsKey(target))
                            .Select(r=>(Id:r.SourceId,Target:r.TargetId!))
                            .Concat(active.Values.Where(f=>f.Syntax.Hyperlink&&(f.TocRow is not null||f.Field.BookmarkId is not null))
                                .Select(f=>(Id:f.ResultId,Target:f.TocRow?.Destination.NodeId??f.Field.BookmarkId!)))
                            .ToImmutableDictionary(p=>p.Id,p=>p.Target,StringComparer.Ordinal);
                        return baseLines with {Lines=[..baseLines.Lines.Select(l=>l.ParagraphId).Distinct(StringComparer.Ordinal)
                            .SelectMany(id=>currentLines[id])]};
                    }
                    if(oscillation||unresolved&&dirty.Count==0)
                    {
                        fieldStability=oscillation?"PAGE_FIELD_OSCILLATION":"PAGE_FIELD_BODY_UNRESOLVED";
                        return baseLines;
                    }
                    previous=state;
                }
                fieldStability="PAGE_FIELD_PASS_LIMIT";
                return baseLines;
            }

            void RegisterLines(Dictionary<string,ImmutableArray<ComposedLine>> byParagraph,Dictionary<string,string> lineBlocked,Action<FlowIssue> add)
            {
                foreach(var entry in byParagraph)
                {
                    foreach(var line in entry.Value)
                    {
                        Check();
                        bool provisional=adjusted[line.ParagraphId].CellOwner is null&&sideProfile||
                            adjusted[line.ParagraphId].FlowColumns is {} regions&&
                            regions.Columns.Any(c=>c.Width!=regions.Narrowest);
                        bool BlocksProvisional(FlowIssue issue)=>BlocksLine(issue)&&!(provisional&&issue.Code=="LINE_OVERFLOW");
                        foreach(var issue in line.Diagnostics)
                        {
                            if(issue.Code=="LINE_SPACING_PENDING")continue;
                            if(provisional&&issue.Code=="LINE_OVERFLOW")continue;
                            add(issue with{BlocksRendering=BlocksProvisional(issue)});
                        }
                        bool underline=line.HasUnsupportedUnderline;
                        if(underline)
                            add(new("PAGE_INLINE_DECORATION_PENDING",line.ParagraphSource.Location,
                                "Soulignement conservé. Décoration de ligne à positionner dans le profil natif.",true));
                        bool columnInCell=line.Cut==LineCutKind.Column&&adjusted[line.ParagraphId].CellOwner is not null;
                        if(line.Occupied is null||columnInCell||underline||line.Diagnostics.Any(BlocksProvisional))
                        {
                            var block=Owner(line.ParagraphId);
                            if(block is not null)lineBlocked.TryAdd(block,line.Diagnostics.FirstOrDefault(BlocksProvisional)?.Code??
                                (underline?"PAGE_INLINE_DECORATION_PENDING":"PAGE_LINE_UNPLACED"));
                        }
                    }
                }
            }

            (Composition Composition,HashSet<string> Chosen,string? Obstacle,SemanticSource? ObstacleSource) PlaceBody(
                Dictionary<string,ImmutableArray<ComposedLine>> byParagraph, Dictionary<string,string> lineBlocked,
                Action<FlowIssue> add,bool final,PageRegionReplay? replay=null,List<PageRegionCheckpoint>? checkpoints=null)
            {
                string? obstacle=null;
                SemanticSource? obstacleSource=null;
                Composition placement=new([],[]);
                var prefix=new List<string>();
                bool stop=false;
                foreach(var id in flowBlocks)
                {
                    Check();
                    if(!adjusted.TryGetValue(id,out var block)||noSectionGeometry.Contains(block.Section))
                        lineBlocked.TryAdd(id,"PAGE_REGION_UNAVAILABLE");
                    else if(pendingSections.Contains(block.Section))
                        lineBlocked.TryAdd(id,sections[block.Section-1].Diagnostics.First(i=>i.BlocksRendering).Code);
                    else if(block.TableId is not null)
                    {
                        if(!cellsByTable.TryGetValue(id,out var cells)||cells.Count==0||
                            cells.Where(c=>c.MergeOrigin is null).SelectMany(c=>c.ParagraphIds).Any(p=>!byParagraph.ContainsKey(p)))
                            lineBlocked.TryAdd(id,"PAGE_TABLE_UNPLACED");
                    }
                    else if(!byParagraph.ContainsKey(id))lineBlocked.TryAdd(id,"PAGE_LINE_UNPLACED");
                    if(lineBlocked.TryGetValue(id,out string? reason))
                    {
                        if(!stop)
                        {
                            obstacle=reason;
                            obstacleSource=FlowSource(id);
                            var precise=layout.Issues.FirstOrDefault(i=>i.Code==reason&&i.Source is {} source&&
                                locations.TryGetValue(source,out var issueId)&&Owner(issueId)==id);
                            if(precise?.Source is {} location)obstacleSource=model.Source(locations[location]);
                        }
                        stop=true;
                        Block(id,reason,"Bloc conservé sans géométrie de page qualifiée.");
                    }
                    if(stop&&final)gaps.Add(new(FlowSource(id),reason??"PAGE_AFTER_UNPLACED"));
                    if(!stop)prefix.Add(id);
                }
                string? successor=stop?flowBlocks[prefix.Count]:null;
                while(successor is not null&&prefix.Count>0&&adjusted.TryGetValue(successor,out var next)&&
                    adjusted[prefix[^1]] is {} previous&&previous.Properties.KeepNext&&Composer.CanJoinNative(previous,next)&&
                    (previous.TableId is not null||byParagraph[previous.Id][^1].Cut is not (LineCutKind.Page or LineCutKind.Column)))
                {
                    Check();
                    var id=prefix[^1];prefix.RemoveAt(prefix.Count-1);
                    Block(id,"PAGE_KEEP_NEXT_BLOCKED","Le paragraphe solidaire attend le placement du bloc suivant ("+lineBlocked[successor]+").");
                    if(final)gaps.Add(new(FlowSource(id),"PAGE_KEEP_NEXT_BLOCKED"));
                    successor=id;
                }
                var chosen=prefix.ToHashSet(StringComparer.Ordinal);
                for(int attempt=0;attempt<4&&prefix.Count>0;attempt++)
                {
                    var placedLayout=layout with
                    {
                        Paragraphs=layout.Paragraphs.Where(p=>!aliases.Contains(p.Id)&&
                            (p.CellOwner is null?chosen.Contains(p.Id):Owner(p.Id) is {} block&&chosen.Contains(block))).ToImmutableArray(),
                        Tables=layout.Tables.Where(t=>chosen.Contains(t.Id)).ToImmutableArray()
                    };
                    try
                    {
                        if(replay is not null&&!chosen.Contains(replay.Checkpoint.BlockId))replay=null;
                        if(!final)replayedFieldSheets.Add(replay?.Checkpoint.Sheets??0);
                        placement=Composer.PlaceNativeLayout(placedLayout,byParagraph,painter,Check,
                            ()=>{if(final)firstPageMs??=timer.Elapsed.TotalMilliseconds;},sections,Reflow,footnoteLayout,replay,checkpoints,
                            regionalDrafts.Count==0?null:regionalDrafts.Keys.ToHashSet(StringComparer.Ordinal),FitRegion,
                            endnoteGroups,chosen.Count==flowBlocks.Length);
                        if(variableWidths&&placement.Pages.Count>0)
                            VerifyPlacement(placedLayout,placement);
                        foreach(var issue in placement.Issues)add(issue);
                        if(!final||bodyFields.Count!=0||placement.Pages.Count!=0||
                            placement.Issues.FirstOrDefault(i=>i.BlocksRendering&&i.Source is not null) is not {} failed||
                            !locations.TryGetValue(failed.Source!,out string? sourceId)||Owner(sourceId) is not {} block||
                            prefix.IndexOf(block) is not (>0 and var cut))break;
                        obstacle=failed.Code;
                        obstacleSource=model.Source(sourceId);
                        while(cut>0&&adjusted[prefix[cut-1]] is {Properties.KeepNext:true} previous&&
                            Composer.CanJoinNative(previous,adjusted[prefix[cut]]))cut--;
                        if(cut==0||attempt==3)break;
                        for(int index=cut;index<prefix.Count;index++)
                        {
                            Check();
                            string trim=prefix[index];
                            string diagnostic=trim==block?failed.Code:"PAGE_AFTER_PLACEMENT_FAILURE";
                            lineBlocked.TryAdd(trim,diagnostic);
                            gaps.Add(new(FlowSource(trim),diagnostic));
                            chosen.Remove(trim);
                        }
                        prefix.RemoveRange(cut,prefix.Count-cut);
                    }
                    catch(InvalidDataException error)
                    {
                        placement=new([],[]);
                        obstacle=error.Message;
                        add(new(error.Message,null,"Les positions ne peuvent pas être certifiées dans ce profil.",true));
                        break;
                    }
                }
                return (placement,chosen,obstacle,obstacleSource);

                ImmutableArray<ComposedLine> Reflow(LayoutParagraph paragraph,LineRegion region,LineResume checkpoint)
                {
                    var original=byParagraph[paragraph.Id];
                    var generated=original.SelectMany(l=>l.Generated).SingleOrDefault(g=>g.Kind=="pageField");
                    var fields=generated is null?null:new Dictionary<string,GeneratedPageField>(StringComparer.Ordinal)
                    {
                        [paragraph.Id]=new(generated.Cause,generated.Fragment.Text,generated.Fragment.Style)
                        {Measured=generated.Fragment,StoredRange=bodyFields.GetValueOrDefault(paragraph.Id)?.StoredRange,ResultId=generated.ResultId}
                    };
                    var alternate=painter.Compose(_=>region,token,markers:markers,
                        orderedParagraphs:[textParagraphs[paragraph.Id]],fields:fields,resume:checkpoint);
                    columnWork+=alternate.Performance.Work;
                    columnGlyphs+=alternate.Performance.ShapedGlyphs;
                    if(columnWork>5_000_000||columnGlyphs>500_000)
                        throw new InvalidDataException("PAGE_COLUMN_REMEASUREMENT_LIMIT");
                    if(alternate.Lines.IsEmpty||alternate.Lines[0].Resume!=checkpoint||
                        alternate.Lines.Any(l=>l.Occupied is null||l.Diagnostics.Any(d=>d.Code!="LINE_OVERFLOW"&&BlocksLine(d))))
                        throw new InvalidDataException("PAGE_REGION_REFLOW_UNQUALIFIED");
                    return alternate.Lines;
                }

                void VerifyPlacement(PaginatedDocument document,Composition placed)
                {
                    var actual=PlacedLines(placed);
                    foreach(var paragraph in document.Paragraphs.Where(p=>p.TableId is null))
                    {
                        Check();
                        var text=textParagraphs[paragraph.Id];
                        if(!actual.TryGetValue(paragraph.Id,out var rendered))
                            throw new InvalidDataException("PAGE_REGION_SOURCE_COVERAGE");
                        int offset=0,ordinal=0;
                        foreach(var line in rendered)
                        {
                            Check();
                            if(line.Index!=ordinal++||line.ContextRange.Start!=offset||line.ContextRange.EndExclusive<offset)
                                throw new InvalidDataException("PAGE_REGION_SOURCE_COVERAGE");
                            offset=line.ContextRange.EndExclusive;
                        }
                        if(offset!=text.ContextLength||
                            !rendered.SelectMany(l=>l.Parts).Where(p=>p.Control is not null)
                                .Select(p=>p.Control!.Value).SequenceEqual(text.Controls)||
                            !rendered.SelectMany(l=>l.Generated).Where(g=>g.Kind=="pageField").Select(g=>g.Cause)
                                .SequenceEqual(byParagraph[paragraph.Id].SelectMany(l=>l.Generated).Where(g=>g.Kind=="pageField").Select(g=>g.Cause)))
                            throw new InvalidDataException("PAGE_REGION_SOURCE_COVERAGE");
                    }
                }

                void Block(string id,string code,string message)
                {
                    lineBlocked.TryAdd(id,code);
                    add(new(code,FlowSource(id).Location,message,true));
                }
            }

        }
        catch(InvalidDataException error)
        {
            Add(new(error.Message,null,"Budget ou contrat de composition dépassé. Texte disponible en lecture de secours.",true));
            return Result();
        }
        finally
        {
            if(!ownsLease){painter?.Dispose();typography?.Dispose();}
        }

        void Add(FlowIssue issue)
        {
            var key=(issue.Code,issue.Source);
            bool upgrade=keys.TryGetValue(key,out int existing);
            if(upgrade&&(!issue.BlocksRendering||issues[existing].BlocksRendering))return;
            const int reserved=256;
            if(!issue.BlocksRendering&&informational>=MaxDiagnostics-reserved-1)
            {
                if(!informationLimited)
                {
                    informationLimited=true;
                    issues.Add(new("PAGE_INFORMATION_LIMIT",null,"Avis détaillés plafonnés. Sources conservées dans l’inventaire.",false));
                }
                return;
            }
            if(issue.BlocksRendering&&blocking>=reserved-1)
            {
                if(!blockingLimited)
                {
                    blockingLimited=true;
                    issues.Add(new("PAGE_DIAGNOSTIC_LIMIT",null,"Diagnostics bloquants plafonnés. Couverture partielle.",true));
                }
                return;
            }
            if(issue.BlocksRendering)blocking++;else informational++;
            if(upgrade){informational--;issues[existing]=issue;}
            else{keys.Add(key,issues.Count);issues.Add(issue);}
        }
        PageCompositionLease Result()
        {
            long publicationStarted=Stopwatch.GetTimestamp();
            foreach(var issue in semanticDiagnostics)Add(issue);
            if(textSnapshot is not null)
                foreach(var issue in textSnapshot.Diagnostics)Add(issue);
            var pagination=pages.Pages.Count==0?PageCoverage.Fallback:issues.Any(i=>i.BlocksRendering)||gaps.Count>0?PageCoverage.Partial:PageCoverage.Complete;
            if(pagination==PageCoverage.Partial&&!issues.Any(i=>i.BlocksRendering))
                Add(new("PAGE_PARTIAL",null,"Des sources restent accessibles sans géométrie de page.",true));
            int missing=0,userChoices=0;
            if(textSnapshot is not null)
                foreach(var p in textSnapshot.Paragraphs)
                foreach(var fragment in p.Fragments.Concat(p.Controls.Select(c=>c.Symbol).OfType<TextFragment>()))
                {
                    token.ThrowIfCancellationRequested();
                    if(fragment.LegacySymbol?.Method=="user")userChoices++;
                    if(fragment.LegacySymbol?.Method=="missing-marker"||fragment.MeasurementIssue=="TEXT_MISSING_FACE")missing++;
                }
            var rendering=new PageRenderingStatus(pagination,pagination==PageCoverage.Complete&&missing==0,missing,userChoices);
            var coverage=pagination==PageCoverage.Complete&&(missing>0||userChoices>0||issues.Any(i=>i.Code=="LINE_TAB_OUTSIDE_PAPER"))?
                PageCoverage.Partial:pagination;
            var composition=pages with{Issues=issues.ToImmutableArray(),Profile=Profile,Fonts=layout?.Fonts??new()};
            string? id=inspection.Manifest.SourceSha256;
            var nativePages=composition.Pages.Select(p=>new NativePage(new(id,p.Number),sections[p.Section-1],
                PhysicalPageSections(p).ToImmutableArray(),p)).ToImmutableArray();
            var snapshot=new PageCompositionSnapshot(id,model,textSnapshot,lines,sections,nativePages,gaps.Distinct().ToImmutableArray(),
                composition,coverage,new(semanticMs,typographyMs,regionsMs,linesMs,assemblyMs,composition.Pages.Count==0?null:firstPageMs)
                {FieldResolutionMs=fieldResolutionMs,FieldRecompositionMs=fieldRecompositionMs,FieldAnalysisMs=fieldAnalysisMs,
                    BookmarkIndexMs=bookmarkIndexMs,FieldStabilizationMs=stabilizationMs,InitialBodyPlacementMs=initialBodyPlacementMs,
                    RunningStoriesMs=runningStoriesMs,RunningFieldResolutionMs=runningFieldResolutionMs,
                    RunningFieldMeasurementMs=runningFieldMeasurementMs,FloatingPlacementMs=floatingPlacementMs,
                    FloatingTextMeasurementMs=floatingTextMeasurementMs,
                    PublicationMs=Stopwatch.GetElapsedTime(publicationStarted).TotalMilliseconds},layout)
                {ListMarkers=markers,StoryRegions=storyRegions,StyleDecision=styleDecision,ReadingTables=readingTables,ReadingGrids=readingGrids,
                    FieldPasses=fieldPasses.ToImmutable(),ReplayedFieldSheets=replayedFieldSheets.ToImmutable(),
                    FieldStability=fieldStability,FieldBookmarks=fieldBookmarks,PageLinkTargets=pageLinkTargets,Rendering=rendering};
            ownsLease=true;
            return new(snapshot,typography,painter);
        }
    }

    static bool BlocksLine(FlowIssue issue)=>issue.Code is not
        ("LINE_SPACING_PENDING" or "LINE_LIST_FACE_SUBSTITUTED" or "LINE_TAB_OUTSIDE_PAPER");

    internal static long Emu(float points)=>checked((long)Math.Round((double)points*TypographySession.EmuPerPoint));
    internal static float Points(long emu)=>(float)emu/TypographySession.EmuPerPoint;

    internal static (float Minimum,float Preferred) Intrinsic(LayoutParagraph layout,TextParagraph? p,
        IReadOnlyDictionary<string,InlineImage> images,Action check,MeasuredListMarker? marker=null,long fieldWidth=0,bool fixedTabs=false,
        Func<TextParagraph,TextControl,long?>? noteCallWidth=null,IReadOnlySet<string>? floatingDrawings=null)
    {
        if(p is null)throw new InvalidDataException("PAGE_AUTOFIT_SOURCE");
        if(layout.List is not null&&marker?.TextStart is null)throw new InvalidDataException("PAGE_AUTOFIT_UNMEASURED");
        var units=new List<(int End,long Advance,bool Space,bool Hard)>();
        var breaks=p.Breaks.ToDictionary(b=>b.Offset,b=>b.Mandatory);
        foreach(var f in p.Fragments)
        {
            check();
            if(f.Measure is not {} measured)throw new InvalidDataException("PAGE_AUTOFIT_UNMEASURED");
            foreach(var c in measured.Clusters)
            {
                check();
                if(c.MissingGlyph)throw new InvalidDataException("PAGE_AUTOFIT_UNMEASURED");
                long advance=0;
                for(int i=0;i<c.GlyphCount;i++)advance+=measured.Glyphs[c.FirstGlyph+i].AdvanceX;
                if(!LineComposer.TrackingQualified(f,c,advance))throw new InvalidDataException("PAGE_AUTOFIT_TRACKING_UNQUALIFIED");
                var text=f.Text.AsSpan(c.Range.Start,c.Range.EndExclusive-c.Range.Start);
                int end=f.ContextOffset+c.Range.EndExclusive;
                units.Add((end,advance,text.IndexOfAnyExcept(' ')<0,breaks.GetValueOrDefault(end)));
            }
        }
        foreach(var c in p.Controls)
        {
            check();
            if(c.Kind=="Break")units.Add((c.Offset+1,0,false,true));
            else if(c.Kind=="Drawing"&&images.TryGetValue(c.Source.NodeId,out var image))
                units.Add((c.Offset+1,Emu(image.Width),false,false));
            else if(c.Kind=="Drawing"&&floatingDrawings?.Contains(c.Source.NodeId)==true)
                units.Add((c.Offset+1,0,false,false));
            else if(c.Kind=="NoteCall"&&noteCallWidth?.Invoke(p,c) is {} noteWidth)
            {
                units.Add((c.Offset+1,noteWidth,false,false));
                breaks.Remove(noteWidth==0?c.Offset+1:c.Offset);
            }
            else if(c.Symbol?.Measure is {} symbol)
                units.Add((c.Offset+1,symbol.Metrics.Width,false,false));
            else if(c.NonBreakingHyphen?.Measure is {} hyphen)
                units.Add((c.Offset+1,hyphen.Metrics.Width,false,false));
            else if(c.Kind=="Tab"&&fixedTabs)
                units.Add((c.Offset+1,0,false,false));
            else throw new InvalidDataException("PAGE_AUTOFIT_INLINE_PENDING");
        }
        if(fieldWidth>0)
        {
            // End-of-paragraph is not a legal cut between a terminal tab and its field.
            breaks.Remove(p.ContextLength);
            units.Add((p.ContextLength+1,fieldWidth,false,false));
        }
        long word=0,line=0,trailing=0,minimum=0,preferred=0;
        long? firstMinimum=null,firstPreferred=null;
        foreach(var unit in units.OrderBy(u=>u.End))
        {
            check();
            word+=unit.Advance;line+=unit.Advance;
            trailing=unit.Space?trailing+unit.Advance:0;
            if(unit.Hard||breaks.ContainsKey(unit.End))
            {
                if(firstMinimum is null)firstMinimum=Math.Max(0,word-trailing);
                else minimum=Math.Max(minimum,word-trailing);
                word=0;
            }
            if(unit.Hard)
            {
                if(firstPreferred is null)firstPreferred=Math.Max(0,line-trailing);
                else preferred=Math.Max(preferred,line-trailing);
                line=0;trailing=0;
            }
        }
        if(firstMinimum is null)firstMinimum=Math.Max(0,word-trailing);
        else minimum=Math.Max(minimum,word-trailing);
        if(firstPreferred is null)firstPreferred=Math.Max(0,line-trailing);
        else preferred=Math.Max(preferred,line-trailing);
        var inset=layout.Properties.Left+layout.Properties.Right+Math.Max(0,layout.Properties.FirstLine);
        if(marker?.TextStart is not {} textStart)
            return (Points(Math.Max(firstMinimum.Value,minimum))+inset,Points(Math.Max(firstPreferred.Value,preferred))+inset);
        long left=marker.Properties.Left,right=marker.Properties.Right;
        long rightInk=marker.Decoration?.Fragment.Measure?.Metrics.Ink?.Right??0;
        long inkEdge=(marker.X??0)+rightInk;
        return (Points(Math.Max(inkEdge,Math.Max(firstMinimum.Value+textStart,minimum+left)+right)),
            Points(Math.Max(inkEdge,Math.Max(firstPreferred.Value+textStart,preferred+left)+right)));
    }

    static ImmutableArray<PageSection> ReadSections(SemanticDocument model,PaginatedDocument layout,CancellationToken token)
    {
        if(layout.SectionFormats.Count!=model.Sections.Length)return [];
        string? settings=model.RelatedRoot("settings","settings");
        var result=ImmutableArray.CreateBuilder<PageSection>();
        var declared=new (long Width,long Height,long Left,long Top,long Right,long Bottom,long? Header,long? Footer,long? Gutter,string? Orientation)[model.Sections.Length];
        for(int i=declared.Length-1;i>=0;i--)
        {
            token.ThrowIfCancellationRequested();
            var format=layout.SectionFormats[i];
            string? id=model.Sections[i].PropertiesId;
            string? s=id is null?null:model.ChildId(id,"pgSz"),m=id is null?null:model.ChildId(id,"pgMar");
            bool inherit=i+1<declared.Length&&id is not null&&model.ChildId(id,"type") is {} t&&model.Attribute(t,"val")=="continuous";
            var following=inherit?declared[i+1]:default;
            declared[i]=(Twip(s,"w",Emu(format.Width),following.Width),Twip(s,"h",Emu(format.Height),following.Height),
                Twip(m,"left",Emu(format.Left),following.Left),Twip(m,"top",Emu(format.Top),following.Top),
                Twip(m,"right",Emu(format.Right),following.Right),Twip(m,"bottom",Emu(format.Bottom),following.Bottom),
                Distance(m,"header")??following.Header,Distance(m,"footer")??following.Footer,Distance(m,"gutter")??following.Gutter,
                (s is null?null:model.Attribute(s,"orient"))??following.Orientation);
            long Twip(string? node,string key,long fallback,long next)=>
                Distance(node,key)??(inherit&&(node is null||model.Attribute(node,key) is null)?next:fallback);
        }
        PageSection? previous=null;
        foreach(var source in model.Sections)
        {
            token.ThrowIfCancellationRequested();
            string? id=source.PropertiesId;
            string? margin=id is null?null:model.ChildId(id,"pgMar");
            string? cols=id is null?null:model.ChildId(id,"cols");
            string start=id is not null&&model.ChildId(id,"type") is {} type?model.Attribute(type,"val")??"nextPage":"nextPage";
            var format=layout.SectionFormats[source.Number-1];
            var geometry=declared[source.Number-1];
            var diagnostics=ImmutableArray.CreateBuilder<FlowIssue>();
            var runningIssues=ImmutableArray.CreateBuilder<FlowIssue>();
            var orientation=geometry.Orientation;
            long? header=geometry.Header,footer=geometry.Footer,gutter=geometry.Gutter;
            bool mirror=ReadFlag(settings,"mirrorMargins");
            bool gutterAtTop=ReadFlag(settings,"gutterAtTop");
            bool gutterOnRight=ReadFlag(id,"rtlGutter");
            foreach(string setting in new[]{"bookFoldPrinting","bookFoldRevPrinting","printTwoOnOne"})
                if(ReadFlag(settings,setting))
                    diagnostics.Add(new("PAGE_IMPOSITION_UNQUALIFIED",model.Source(model.ChildId(settings!,setting)!).Location,
                        "Disposition du papier en livret ou deux pages par feuille hors profil.",true));
            long left=geometry.Left,top=geometry.Top,right=geometry.Right;
            if(gutter is >0)
            {
                if(gutterAtTop&&!mirror)top+=gutter.Value;
                else if(gutterOnRight)right+=gutter.Value;
                else left+=gutter.Value;
                format=format with{Left=Points(left),Top=Points(top),Right=Points(right)};
            }
            var region=new PageRegion(left,top,geometry.Width-left-right,geometry.Height-top-geometry.Bottom);
            string? compat=settings is null?null:model.ChildId(settings,"compat");
            var columnLayout=ReadFlowColumns(model,cols,region.Width,!ReadFlag(compat,"noColumnBalance"),Refuse);
            int columns=columnLayout?.Columns.Length??1;
            bool title=ReadFlag(id,"titlePg"),even=ReadFlag(settings,"evenAndOddHeaders");
            var pageNumbers=Single(id,"pgNumType");
            int? number=Read(pageNumbers,"start",0,32767) is {} nstart?(int)nstart:null;
            var numberFormat=pageNumbers is null?"decimal":model.Attribute(pageNumbers,"fmt")??"decimal";
            if(pageNumbers is not null&&model.Attribute(pageNumbers,"start") is not null&&number is null)
                RunningIssue(pageNumbers,"PAGE_NUMBERING_INVALID","Valeur initiale de la numérotation hors profil.");
            if(numberFormat is not ("decimal" or "upperRoman" or "lowerRoman" or "upperLetter" or "lowerLetter"))
                RunningIssue(pageNumbers!,"PAGE_NUMBER_FORMAT_UNQUALIFIED","Format de numérotation affichée hors profil.");
            if(id is not null)
            {
                if(margin is not null)
                {
                    if(model.Attribute(margin,"gutter") is not null&&Distance(margin,"gutter") is null)
                        Refuse("PAGE_MARGIN_UNQUALIFIED","Distance de reliure hors profil.");
                    foreach(string property in new[]{"header","footer"})
                        if(model.Attribute(margin,property) is not null&&Distance(margin,property) is null)
                            RunningIssue(margin,"PAGE_STORY_DISTANCE_INVALID","Distance d’histoire hors profil.");
                }
                if(model.ChildId(id,"docGrid") is {} grid&&model.Attribute(grid,"type") is not (null or "default")||
                    model.ChildId(id,"vAlign") is {} valign&&model.Attribute(valign,"val") is not ("top"))
                    Refuse("PAGE_VERTICAL_REGION_PENDING","Grille ou alignement vertical actif hors profil.");
                if(source.Number>1&&start is not ("continuous" or "nextPage" or "oddPage" or "evenPage" or "nextColumn"))
                    Refuse("PAGE_SECTION_TRANSITION_PENDING","Transition de section à résoudre.");
                if(source.Number>1&&number is {} restart&&
                    (start=="oddPage"&&restart%2==0||start=="evenPage"&&restart%2!=0))
                    Refuse("PAGE_SECTION_PARITY_CONFLICT","La parité de la reprise contredit le type de saut déclaré.");
                if(previous is not null&&(start is "continuous" or "nextColumn")&&(previous.Format!=format||previous.HeaderDistance!=header||
                    previous.FooterDistance!=footer||previous.TitlePage||title||number is not null||
                    previous.PageNumberFormat!=numberFormat||!previous.Semantic.RunningStories.SequenceEqual(source.RunningStories)))
                    Refuse("PAGE_CONTINUOUS_INCOMPATIBLE","Les régions ou histoires de la section changent lors d’une transition continue.");
                if(previous is not null&&start=="nextColumn"&&
                    !SameColumns(previous.ColumnLayout,columnLayout))
                    Refuse("PAGE_NEXT_COLUMN_GEOMETRY_UNQUALIFIED","Le saut de colonne change la grille de la bande.");
            }
            if(region.Width<36*TypographySession.EmuPerPoint||region.Height<36*TypographySession.EmuPerPoint)
                Refuse("PAGE_REGION_INVALID","La zone de texte est trop petite.");
            previous=new(source,id is null?null:model.Source(id),format,region,geometry.Width,geometry.Height,start,orientation,header,footer,gutter,columns,Distance(cols,"space"),mirror,
                diagnostics.ToImmutable())
            {TitlePage=title,EvenAndOdd=even,PageNumberStart=number,PageNumberFormat=numberFormat,
                RunningIssues=runningIssues.ToImmutable(),ColumnLayout=columnLayout};
            result.Add(previous);
            void Refuse(string code,string reason)=>diagnostics.Add(new(code,id is null?null:model.Source(id).Location,reason,true));
            void RunningIssue(string node,string code,string reason)=>runningIssues.Add(new(code,model.Source(node).Location,reason,true));
            string? Single(string? owner,string local)
            {
                if(owner is null)return null;
                var matches=model.Children(owner).Where(e=>model.Name(e.Id)==SemanticIndex.QName(SemanticIndex.W,local)).Take(2).ToArray();
                if(matches.Length==2)RunningIssue(matches[1].Id,"PAGE_STORY_SETTING_AMBIGUOUS","Déclaration répétée : "+local+".");
                return matches.Length==1?matches[0].Id:null;
            }
            bool ReadFlag(string? owner,string local)
            {
                if(Single(owner,local) is not {} property)return false;
                string? raw=model.Attribute(property,"val");
                if(raw is null or "1" or "on" or "true")return true;
                if(raw is not ("0" or "off" or "false"))RunningIssue(property,"PAGE_STORY_SETTING_INVALID","Valeur booléenne invalide : "+local+".");
                return false;
            }
        }
        return result.ToImmutable();

        long? Distance(string? node,string property)=>Read(node,property,0,28_800)*LinePropertiesReader.EmuPerTwip;
        long? Read(string? node,string attr,int min,int max)=>
            node is not null&&int.TryParse(model.Attribute(node,attr),NumberStyles.Integer,CultureInfo.InvariantCulture,out int value)&&value>=min&&value<=max?value:null;
    }
}
