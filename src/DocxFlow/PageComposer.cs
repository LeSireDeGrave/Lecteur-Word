using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal enum PageCoverage { Complete, Partial, Fallback }

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
}
internal readonly record struct CalculatedPageId(string? SnapshotId,int PhysicalIndex);
internal sealed record NativePage(CalculatedPageId Id,PageSection Origin,ImmutableArray<int> Sections,ComposedPage Geometry)
{
    internal int? SectionPageNumber {get;init;}
}
internal sealed record PagePlacementGap(SemanticSource Source,string Reason,Utf16Span? ContextRange=null);
internal sealed record PageCompositionTimes(double SemanticMs,double TypographyMs,double RegionsMs,double LinesMs,double AssemblyMs,
    double? FirstPageMs);
internal sealed record PageCompositionSnapshot(string? SnapshotId,SemanticDocument? Semantic,TypographyDocument? Typography,ComposedLines? Lines,
    ImmutableArray<PageSection> Sections,ImmutableArray<NativePage> Pages,ImmutableArray<PagePlacementGap> Gaps,Composition Composition,PageCoverage Coverage,
    PageCompositionTimes Times,PaginatedDocument? RulerModel)
{
    internal IReadOnlyDictionary<string,MeasuredListMarker> ListMarkers {get;init;}=ImmutableDictionary<string,MeasuredListMarker>.Empty;
    internal ImmutableArray<PageStoryRegion> StoryRegions {get;init;}=[];
    internal PageStyleDecision? StyleDecision {get;init;}
    internal IReadOnlyDictionary<string,ComposedPage> ReadingTables {get;init;}=ImmutableDictionary<string,ComposedPage>.Empty;
    internal IReadOnlyDictionary<string,LayoutTable> ReadingGrids {get;init;}=ImmutableDictionary<string,LayoutTable>.Empty;
}

/// <summary>Owns the faces used to draw one immutable composition. The caller releases the lease on document close.</summary>
internal sealed class PageCompositionLease(PageCompositionSnapshot snapshot,TypographySession? typography=null,LineComposer? painter=null) : IDisposable
{
    internal PageCompositionSnapshot Snapshot {get;}=snapshot;
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
    internal static PageCompositionLease Compose(InspectionResult inspection,CancellationToken token=default,bool prepareReadingTables=false)
    {
        token.ThrowIfCancellationRequested();
        var timer=Stopwatch.StartNew();
        double semanticMs=0,typographyMs=0,regionsMs=0,linesMs=0,assemblyMs=0;
        double? firstPageMs=null;
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
        IReadOnlyDictionary<int,int?> sectionPageNumbers=ImmutableDictionary<int,int?>.Empty;
        var gaps=ImmutableArray.CreateBuilder<PagePlacementGap>();
        var issues=new List<FlowIssue>();
        var keys=new HashSet<(string,SourceLocation?)>();
        ImmutableArray<FlowIssue> semanticDiagnostics=[];
        Composition pages=new([],[]);
        var readingTables=new Dictionary<string,ComposedPage>(StringComparer.Ordinal);
        var readingGrids=new Dictionary<string,LayoutTable>(StringComparer.Ordinal);
        bool ownsLease=false;
        try
        {
            var semantic=SemanticReader.Build(inspection,token);
            semanticMs=timer.Elapsed.TotalMilliseconds;
            model=semantic.Document;
            semanticDiagnostics=semantic.Issues;
            if(model is null)return Result();
            long start=Stopwatch.GetTimestamp();
            var tableStyles=new TableStyleCascade(model,token);
            typography=new(inspection,model,cancellation:token,tableStyles:tableStyles);
            var measured=typography.Measure(token);
            textSnapshot=measured;
            typographyMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start=Stopwatch.GetTimestamp();
            void Check()
            {
                token.ThrowIfCancellationRequested();
                if(Stopwatch.GetElapsedTime(start).TotalMilliseconds>15_000)throw new InvalidDataException("PAGE_REGION_TIME_LIMIT");
            }
            var reader=new LayoutReader.Reader(inspection,token,model,tableStyles);
            layout=reader.Read();
            styleDecision=reader.StyleDecision;
            sections=ReadSections(model,layout,token);
            var runningParts=model.Stories.Where(s=>s.Kind is "hdr" or "ftr")
                .Select(s=>model.Source(s.RootId).Location.PartUri).ToHashSet(StringComparer.Ordinal);
            var body=model.BodyBlocks.ToHashSet(StringComparer.Ordinal);
            var owner=new Dictionary<string,string?>(StringComparer.Ordinal);
            string? Owner(string node)
            {
                if(body.Contains(node))return node;
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
            foreach(var issue in measured.Diagnostics.Where(i=>i.Code=="TEXT_WORD_RTL_PARTIAL"))
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
                blocked.TryAdd(diagnostic.TableId,diagnostic.Issue.Code);
            }

            void Register(FlowIssue issue)
            {
                Check();
                if(issue.Source is {} part&&runningParts.Contains(part.PartUri))return;
                string? node=issue.Source is {} location?locations.GetValueOrDefault(location):null;
                Add(issue);
                if(!issue.BlocksRendering||node is null)return;
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
            var textParagraphs=measured.Paragraphs.ToDictionary(p=>p.Id,StringComparer.Ordinal);
            painter=new(model,measured,typography,images:imageRuns,tableStyles:tableStyles);
            markers=painter.MeasureListMarkers(layout.Paragraphs,textParagraphs,token);
            foreach(var marker in markers.Values)
                foreach(var issue in marker.Issues)
                    Add(issue with{BlocksRendering=BlocksLine(issue)});
            var validTables=new List<LayoutTable>();
            var adjusted=layout.Paragraphs.ToDictionary(p=>p.Id,StringComparer.Ordinal);
            foreach(var table in layout.Tables)
            {
                Check();
                if(prepareReadingTables)readingGrids.Add(table.Id,table);
                if(!adjusted.TryGetValue(table.Id,out var sentinel)||noSectionGeometry.Contains(sentinel.Section))continue;
                try
                {
                    var tableLayout=Composer.FitPageTable(table,adjusted,Check,
                        p=>Intrinsic(p,textParagraphs.GetValueOrDefault(p.Id),imageRuns,Check,markers.GetValueOrDefault(p.Id)));
                    validTables.Add(tableLayout);
                    if(prepareReadingTables)readingGrids[table.Id]=tableLayout;
                    if(tableLayout.WidthResult is {} geometry)
                    {
                        if(geometry.Overhang)
                            Add(new("PAGE_TABLE_MARGIN_OVERHANG",table.Source,"Les dimensions du tableau dépassent la marge tout en restant dans le papier.",false));
                        if(!geometry.ContentVerified)
                            Block(table.Id,"PAGE_TABLE_CONTENT_UNMEASURED","Grille résolue. Au moins une cellule n’a pas de mesure intrinsèque qualifiée.");
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
                long width=p.CellOwner is null?sections[p.Section-1].Body.Width:Emu(p.Geometry.Width)-Emu(p.Geometry.Left)-Emu(p.Geometry.Right);
                if(width>0)paragraphRegions.Add(p.Id,new(p.CellOwner??$"section-{p.Section}",0,width));
            }
            regionsMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start=Stopwatch.GetTimestamp();
            lines=painter.Compose(r=>paragraphRegions[r.ParagraphId],token,paragraphRegions.Keys.ToHashSet(StringComparer.Ordinal),markers);
            linesMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start=Stopwatch.GetTimestamp();
            var byParagraph=lines.Lines.GroupBy(l=>l.ParagraphId).ToDictionary(g=>g.Key,g=>g.ToImmutableArray(),StringComparer.Ordinal);
            foreach(var entry in byParagraph)
            {
                foreach(var line in entry.Value)
                {
                    Check();
                    foreach(var issue in line.Diagnostics)
                    {
                        if(issue.Code=="LINE_SPACING_PENDING")continue;
                        Add(issue with{BlocksRendering=BlocksLine(issue)});
                    }
                    bool underline=line.Parts.Any(p=>p.Fragment is {} text&&text.Style.Underline!="none");
                    if(underline)
                        Add(new("PAGE_INLINE_DECORATION_PENDING",line.ParagraphSource.Location,
                            "Soulignement conservé. Décoration de ligne à positionner dans le profil natif.",true));
                    if(line.Occupied is null||line.Cut==LineCutKind.Column||underline||line.Diagnostics.Any(BlocksLine))
                    {
                        var block=Owner(line.ParagraphId);
                        if(block is not null)blocked.TryAdd(block,line.Diagnostics.FirstOrDefault(BlocksLine)?.Code??
                            (underline?"PAGE_INLINE_DECORATION_PENDING":"PAGE_LINE_UNPLACED"));
                    }
                }
            }
            foreach(var p in measured.Paragraphs)
            {
                Check();
                if(!paragraphRegions.ContainsKey(p.Id)&&!aliases.Contains(p.Id)&&!runningParts.Contains(model.Source(p.Id).Location.PartUri))
                    gaps.Add(new(model.Source(p.Id),Owner(p.Id) is null?"PAGE_STORY_PENDING":"PAGE_REGION_UNAVAILABLE"));
            }
            var cellsByTable=validTables.ToDictionary(t=>t.Id,t=>t.Cells,StringComparer.Ordinal);
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
            var prefix=new List<string>();
            bool stop=false;
            foreach(var id in model.BodyBlocks)
            {
                Check();
                if(!adjusted.TryGetValue(id,out var block)||noSectionGeometry.Contains(block.Section))
                    blocked.TryAdd(id,"PAGE_REGION_UNAVAILABLE");
                else if(pendingSections.Contains(block.Section))
                    blocked.TryAdd(id,sections[block.Section-1].Diagnostics.First(i=>i.BlocksRendering).Code);
                else if(block.TableId is not null)
                {
                    if(!cellsByTable.TryGetValue(id,out var cells)||cells.Count==0||
                        cells.Where(c=>c.MergeOrigin is null).SelectMany(c=>c.ParagraphIds).Any(p=>!byParagraph.ContainsKey(p)))
                        blocked.TryAdd(id,"PAGE_TABLE_UNPLACED");
                }
                else if(!byParagraph.ContainsKey(id))blocked.TryAdd(id,"PAGE_LINE_UNPLACED");
                if(blocked.TryGetValue(id,out string? reason))
                {
                    stop=true;
                    Block(id,reason,"Bloc conservé sans géométrie de page qualifiée.");
                }
                if(stop)gaps.Add(new(model.Source(id),reason??"PAGE_AFTER_UNPLACED"));
                else prefix.Add(id);
            }
            string? successor=stop?model.BodyBlocks[prefix.Count]:null;
            while(successor is not null&&prefix.Count>0&&adjusted.TryGetValue(successor,out var next)&&
                adjusted[prefix[^1]] is {} previous&&previous.Properties.KeepNext&&Composer.CanJoinNative(previous,next)&&
                byParagraph[previous.Id][^1].Cut!=LineCutKind.Page)
            {
                Check();
                var id=prefix[^1];prefix.RemoveAt(prefix.Count-1);
                Block(id,"PAGE_KEEP_NEXT_BLOCKED","Le paragraphe solidaire attend le placement du bloc suivant ("+blocked[successor]+").");
                gaps.Add(new(model.Source(id),"PAGE_KEEP_NEXT_BLOCKED"));
                successor=id;
            }
            var chosen=prefix.ToHashSet(StringComparer.Ordinal);
            if(prefix.Count>0)
            {
                var placedLayout=layout with
                {
                    Paragraphs=layout.Paragraphs.Where(p=>!aliases.Contains(p.Id)&&
                        (p.CellOwner is null?chosen.Contains(p.Id):Owner(p.Id) is {} block&&chosen.Contains(block))).ToImmutableArray(),
                    Tables=layout.Tables.Where(t=>chosen.Contains(t.Id)).ToImmutableArray()
                };
                try
                {
                    pages=Composer.PlaceNativeLayout(placedLayout,byParagraph,painter,Check,
                        ()=>firstPageMs??=timer.Elapsed.TotalMilliseconds);
                    foreach(var issue in pages.Issues)Add(issue);
                }
                catch(InvalidDataException error)
                {
                    Add(new(error.Message,null,"Les positions ne peuvent pas être certifiées dans ce profil.",true));
                }
            }
            if(pages.Pages.Count==0)Add(new("PAGE_NO_PLACED_BODY",null,"Corps sans page certifiable. Lecture de secours disponible selon l’inspection.",true));
            foreach(var p in layout.Paragraphs.Where(p=>p.TableId is null))
            {
                Check();
                var block=Owner(p.Id);
                if(block is not null&&(pages.Pages.Count==0||!chosen.Contains(block)))
                    gaps.Add(new(model.Source(p.Id),blocked.GetValueOrDefault(block)??"PAGE_AFTER_UNPLACED"));
            }
            var placed=pages.Pages.SelectMany(p=>p.Lines).Where(l=>!l.IsRepeated&&l.Native is not null)
                .Select(l=>(l.ParagraphId,l.Native!.Index)).ToHashSet();
            foreach(var line in lines.Lines)
            {
                Check();
                if(placed.Contains((line.ParagraphId,line.Index)))continue;
                string? block=Owner(line.ParagraphId);
                var reason=line.Diagnostics.FirstOrDefault(BlocksLine)?.Code??
                    (block is null?null:blocked.GetValueOrDefault(block))??"PAGE_AFTER_UNPLACED";
                gaps.Add(new(line.ParagraphSource,reason,line.ContextRange));
            }
            (pages,storyRegions,sectionPageNumbers)=ComposeRunningStories(pages,sections,model,reader,painter,measured,
                textParagraphs,imageRuns,semanticDiagnostics,tableStyles,Check,Add,gaps,token);
            assemblyMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return Result();

            void Block(string id,string code,string message)
            {
                blocked.TryAdd(id,code);
                Add(new(code,model.Source(id).Location,message,true));
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
            if(keys.Count>=MaxDiagnostics)
            {
                if(keys.Add(("PAGE_DIAGNOSTIC_LIMIT",null)))
                    issues.Add(new("PAGE_DIAGNOSTIC_LIMIT",null,"Détails bornés. Couverture partielle.",true));
                return;
            }
            if(keys.Add((issue.Code,issue.Source)))issues.Add(issue);
        }
        PageCompositionLease Result()
        {
            foreach(var issue in semanticDiagnostics)Add(issue);
            if(textSnapshot is not null)
                foreach(var issue in textSnapshot.Diagnostics)Add(issue);
            var coverage=pages.Pages.Count==0?PageCoverage.Fallback:issues.Any(i=>i.BlocksRendering)||gaps.Count>0?PageCoverage.Partial:PageCoverage.Complete;
            if(coverage==PageCoverage.Partial&&!issues.Any(i=>i.BlocksRendering))
                Add(new("PAGE_PARTIAL",null,"Des sources restent accessibles sans géométrie de page.",true));
            var composition=pages with{Issues=issues.ToImmutableArray(),Profile=Profile,Fonts=layout?.Fonts??new()};
            string? id=inspection.Manifest.SourceSha256;
            var nativePages=composition.Pages.Select(p=>new NativePage(new(id,p.Number),sections[p.Section-1],
                p.Lines.Select(l=>l.Section).Append(p.Section).Distinct().Order().ToImmutableArray(),p)
                {SectionPageNumber=sectionPageNumbers.GetValueOrDefault(p.Number)}).ToImmutableArray();
            var snapshot=new PageCompositionSnapshot(id,model,textSnapshot,lines,sections,nativePages,gaps.Distinct().ToImmutableArray(),
                composition,coverage,new(semanticMs,typographyMs,regionsMs,linesMs,assemblyMs,composition.Pages.Count==0?null:firstPageMs),layout)
                {ListMarkers=markers,StoryRegions=storyRegions,StyleDecision=styleDecision,ReadingTables=readingTables,ReadingGrids=readingGrids};
            ownsLease=true;
            return new(snapshot,typography,painter);
        }
    }

    static bool BlocksLine(FlowIssue issue)=>issue.Code is not ("LINE_SPACING_PENDING" or "LINE_LIST_FACE_SUBSTITUTED");

    internal static long Emu(float points)=>checked((long)Math.Round((double)points*TypographySession.EmuPerPoint));
    internal static float Points(long emu)=>(float)emu/TypographySession.EmuPerPoint;

    internal static (float Minimum,float Preferred) Intrinsic(LayoutParagraph layout,TextParagraph? p,
        IReadOnlyDictionary<string,InlineImage> images,Action check,MeasuredListMarker? marker=null)
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
            else if(c.Symbol?.Measure is {} symbol)
                units.Add((c.Offset+1,symbol.Metrics.Width,false,false));
            else throw new InvalidDataException("PAGE_AUTOFIT_INLINE_PENDING");
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
        bool mirror=settings is not null&&model.ChildId(settings,"mirrorMargins") is {} mirrorId&&
            model.Attribute(mirrorId,"val") is not ("0" or "off" or "false");
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
            var region=new PageRegion(geometry.Left,geometry.Top,geometry.Width-geometry.Left-geometry.Right,
                geometry.Height-geometry.Top-geometry.Bottom);
            var diagnostics=ImmutableArray.CreateBuilder<FlowIssue>();
            var runningIssues=ImmutableArray.CreateBuilder<FlowIssue>();
            int? columns=Read(cols,"num",1,45) is {} n?(int)n:cols is null||model.Attribute(cols,"num") is null?1:null;
            var orientation=geometry.Orientation;
            long? header=geometry.Header,footer=geometry.Footer,gutter=geometry.Gutter;
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
                if(columns!=1)Refuse("PAGE_COLUMNS_PENDING","Colonnes multiples ou invalides. Région de texte à résoudre.");
                if(mirror||gutter>0)Refuse("PAGE_MIRROR_GUTTER_PENDING","Marges miroir ou reliure. Région physique à résoudre.");
                if(model.ChildId(id,"docGrid") is {} grid&&model.Attribute(grid,"type") is not (null or "default")||
                    model.ChildId(id,"vAlign") is {} valign&&model.Attribute(valign,"val") is not ("top"))
                    Refuse("PAGE_VERTICAL_REGION_PENDING","Grille ou alignement vertical actif hors profil.");
                if(source.Number>1&&start is not ("continuous" or "nextPage"))
                    Refuse("PAGE_SECTION_TRANSITION_PENDING","Transition de section à résoudre.");
                if(previous is not null&&start=="continuous"&&(previous.Format!=format||previous.HeaderDistance!=header||
                    previous.FooterDistance!=footer||previous.TitlePage||title||number is not null||
                    previous.PageNumberFormat!=numberFormat||!previous.Semantic.RunningStories.SequenceEqual(source.RunningStories)))
                    Refuse("PAGE_CONTINUOUS_INCOMPATIBLE","Les régions ou histoires de la section changent lors d’une transition continue.");
            }
            if(mirror&&id is null)Refuse("PAGE_MIRROR_GUTTER_PENDING","Marges miroir. Région physique à résoudre.");
            if(region.Width<36*TypographySession.EmuPerPoint||region.Height<36*TypographySession.EmuPerPoint)
                Refuse("PAGE_REGION_INVALID","La zone de texte est trop petite.");
            previous=new(source,id is null?null:model.Source(id),format,region,geometry.Width,geometry.Height,start,orientation,header,footer,gutter,columns,Distance(cols,"space"),mirror,
                diagnostics.ToImmutable())
            {TitlePage=title,EvenAndOdd=even,PageNumberStart=number,PageNumberFormat=numberFormat,RunningIssues=runningIssues.ToImmutable()};
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
