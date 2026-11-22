using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal sealed record PageStoryRegion(int Section,string Kind,string Variant,RunningStory? Selection,
    PageRegion? AvailableRegion,string Status,int Lines,int Cells,ImmutableArray<FlowIssue> Diagnostics)
{
    internal ImmutableArray<int> PhysicalPages {get;init;}=[];
}
internal sealed record PageStoryPlacement(RunningStory Selection,PageRegion Region,IReadOnlyList<PageLine> Lines,IReadOnlyList<PageCell> Cells);

internal static partial class PageComposer
{
    const int MaxStoryTemplates=128,MaxStoryPlacements=60_000;
    sealed record StoryTemplate(ComposedPage? Page,long Height,ImmutableArray<FlowIssue> Issues)
    {
        internal long TopInk {get;init;}
    }

    static (Composition,ImmutableArray<PageStoryRegion>,IReadOnlyDictionary<int,int?>) ComposeRunningStories(
        Composition body,ImmutableArray<PageSection> sections,SemanticDocument model,LayoutReader.Reader reader,
        LineComposer painter,TypographyDocument typography,Dictionary<string,TextParagraph> text,
        Dictionary<string,InlineImage> images,ImmutableArray<FlowIssue> semanticIssues,TableStyleCascade tableStyles,
        Action check,Action<FlowIssue> add,ImmutableArray<PagePlacementGap>.Builder gaps,CancellationToken token)
    {
        var roots=model.Stories.Where(s=>s.Kind is "hdr" or "ftr").ToDictionary(s=>s.RootId,StringComparer.Ordinal);
        var paragraphs=typography.Paragraphs.Where(p=>roots.ContainsKey(p.StoryId)).ToLookup(p=>p.StoryId,StringComparer.Ordinal);
        var fields=model.Fields.Where(f=>roots.ContainsKey(f.StoryId)).ToLookup(f=>f.StoryId,StringComparer.Ordinal);
        var partIssues=semanticIssues.Where(i=>i.Code is "SEMANTIC_MUST_UNDERSTAND" or "SEMANTIC_ALTERNATIVE_UNRESOLVED")
            .Concat(typography.Diagnostics.Where(i=>i.Code is "TEXT_WORD_RTL_PARTIAL" or "TEXT_PROPERTY_PARTIAL"))
            .Concat(tableStyles.Issues.Select(i=>i.Issue))
            .Where(i=>i.Source is not null).ToLookup(i=>i.Source!.PartUri,StringComparer.Ordinal);
        var templates=new Dictionary<(string Root,PageGeometry Geometry),StoryTemplate>();
        var plan=new Dictionary<(int Section,string Kind,string Variant),(PageStoryRegion Region,StoryTemplate? Template)>();
        foreach(var section in sections)
        foreach(string kind in new[]{"header","footer"})
        foreach(string variant in new[]{"default","first","even"})
        {
            check();
            var selection=section.Semantic.RunningStories.SingleOrDefault(s=>s.Kind==kind&&s.Variant==variant);
            var key=(section.Semantic.Number,kind,variant);
            if((variant=="first"&&!section.TitlePage)||(variant=="even"&&!section.EvenAndOdd))
            {Set("Inactive");continue;}
            if(selection is null){Set("Blank");continue;}
            var reference=model.Source(selection.ReferenceId);
            if(selection.Reference is not {Status:ReferenceStatus.Resolved,TargetId:{} target,Relation.ResolvedPart:{} part}||
                !roots.TryGetValue(target,out var story)||story.Kind!=(kind=="header"?"hdr":"ftr")||
                !model.Parts.Any(p=>p.PartUri==part&&p.ContentType=="application/vnd.openxmlformats-officedocument.wordprocessingml."+kind+"+xml"))
            {
                Set("Unresolved",issues:[new("PAGE_STORY_REFERENCE_UNRESOLVED",reference.Location,"Relation ou partie auxiliaire non qualifiée.",true)]);
                continue;
            }
            long? distance=kind=="header"?section.HeaderDistance:section.FooterDistance;
            long height=distance is {} d?(kind=="header"?section.Body.Top:section.PhysicalHeight-section.Body.Top-section.Body.Height)-d:0;
            if(distance is null||height<=0||section.Body.Width<=0)
            {
                Set("NoRegion",issues:[new("PAGE_STORY_REGION_UNQUALIFIED",reference.Location,"La distance et la marge ne déterminent pas une région auxiliaire utilisable.",true)]);
                continue;
            }
            var region=new PageRegion(section.Body.Left,kind=="header"?distance.Value:section.Body.Top+section.Body.Height,section.Body.Width,height);
            var geometry=section.Format with{Top=0,Bottom=0};
            if(!templates.TryGetValue((target,geometry),out var template))
            {
                if(templates.Count>=MaxStoryTemplates)throw new InvalidDataException("PAGE_STORY_TEMPLATE_LIMIT");
                templates[(target,geometry)]=template=Prepare(story,geometry);
            }
            if(template.Page is null)
            {
                Set("Unplaced",region,issues:template.Issues);
                continue;
            }
            if(template.Height>region.Height+Emu(.01f))
            {
                Set("Overlap",region,issues:template.Issues.Add(new("PAGE_STORY_OVERLAP",reference.Location,
                    "L’histoire dépasserait la marge du corps. Une recomposition avec régions interdépendantes est nécessaire.",true)));
                continue;
            }
            Set("Measured",region,template,template.Issues);

            void Set(string status,PageRegion? available=null,StoryTemplate? prepared=null,ImmutableArray<FlowIssue> issues=default)
            {
                plan.Add(key,(new(key.Number,kind,variant,selection,available,status,prepared?.Page?.Lines.Count??0,
                    prepared?.Page?.Cells.Count??0,issues.IsDefault?[]:issues),prepared));
            }
        }

        var result=new List<ComposedPage>(body.Pages.Count);
        var pageNumbers=new Dictionary<int,int?>();
        var used=new Dictionary<(int Section,string Kind,string Variant),List<int>>();
        var described=new HashSet<string>(StringComparer.Ordinal);
        int previousSection=0,number=0,occurrences=0;
        bool numberingKnown=true;
        foreach(var page in body.Pages)
        {
            check();
            var section=sections[page.Section-1];
            bool first=page.Section!=previousSection;
            if(first&&section.PageNumberStart is {} restart){number=restart;numberingKnown=true;}
            else number++;
            if(section.RunningIssues.Any(i=>i.Code is "PAGE_NUMBERING_INVALID" or "PAGE_STORY_SETTING_AMBIGUOUS"))
                numberingKnown=false;
            pageNumbers.Add(page.Number,numberingKnown?number:null);
            if(!numberingKnown&&section.EvenAndOdd&&!(section.TitlePage&&first))
            {
                add(new("PAGE_STORY_PARITY_UNRESOLVED",section.Source?.Location,
                    "La numérotation déclarée ne permet pas de choisir les histoires paires et impaires.",true));
                result.Add(page);
                previousSection=page.Section;
                continue;
            }
            string variant=section.TitlePage&&first?"first":section.EvenAndOdd&&number%2==0?"even":"default";
            var stories=ImmutableArray.CreateBuilder<PageStoryPlacement>(2);
            foreach(string kind in new[]{"header","footer"})
            {
                var key=(page.Section,kind,variant);
                var entry=plan[key];
                if(!used.TryGetValue(key,out var physical))used[key]=physical=[];
                physical.Add(page.Number);
                foreach(var issue in entry.Region.Diagnostics)add(issue);
                if(entry.Region.Selection is not {} selection||selection.Reference.TargetId is not {} root)continue;
                described.Add(root);
                if(entry.Template?.Page is not {} template||entry.Region.AvailableRegion is not {} region)
                {
                    Gap(root,entry.Region.Diagnostics.FirstOrDefault(i=>i.BlocksRendering)?.Code??"PAGE_STORY_PENDING");
                    continue;
                }
                occurrences+=template.Lines.Count+template.Cells.Count;
                if(occurrences>MaxStoryPlacements)throw new InvalidDataException("PAGE_STORY_PLACEMENT_LIMIT");
                long y=kind=="header"?region.Top:region.Top+region.Height-entry.Template.Height;
                float shift=Points(y-entry.Template.TopInk);
                var movedLines=template.Lines.Select(l=>l with
                {
                    Section=page.Section,Baseline=l.Baseline+shift,NativeTop=l.NativeTop+shift,
                    Images=l.Images.Select(i=>i with{Y=i.Y+shift}).ToImmutableArray()
                }).ToImmutableArray();
                var movedCells=template.Cells.Select(c=>c with{Y=c.Y+shift}).ToImmutableArray();
                stories.Add(new(selection,region with{Top=y,Height=entry.Template.Height},movedLines,movedCells));
            }
            previousSection=page.Section;
            result.Add(page with{RunningStories=stories.ToImmutable()});
        }
        var regions=plan.Select(kv=>kv.Value.Region with{PhysicalPages=used.TryGetValue(kv.Key,out var hits)?[..hits]:[]}).ToImmutableArray();
        // Beyond an unplaced primary section, preserve the still referenced auxiliary sources.
        var reached=body.Pages.SelectMany(p=>p.Lines.Select(l=>l.Section).Append(p.Section)).ToHashSet();
        foreach(var entry in regions)
        {
            check();
            if(!reached.Contains(entry.Section)&&entry.Status!="Inactive"&&entry.Selection?.Reference.TargetId is {} root&&described.Add(root))
                Gap(root,"PAGE_STORY_PENDING");
        }
        return (body with{Pages=result.ToImmutableArray()},regions,pageNumbers);

        void Gap(string root,string code)
        {
            gaps.Add(new(model.Source(root),code));
            foreach(var paragraph in paragraphs[root]){check();gaps.Add(new(model.Source(paragraph.Id),code));}
        }

        StoryTemplate Prepare(SemanticStory story,PageGeometry geometry)
        {
            var source=model.Source(story.RootId);
            var storyIssues=ImmutableArray.CreateBuilder<FlowIssue>();
            var layout=reader.ReadStory(story,geometry);
            storyIssues.AddRange(layout.Issues);
            storyIssues.AddRange(partIssues[source.Location.PartUri].Select(i=>i with{BlocksRendering=true}));
            foreach(var field in fields[story.RootId])
            {
                check();
                bool cached=field.StructureVerified&&!field.StoredResultIds.IsEmpty&&model.StoredText(field) is not null;
                string code=!cached?"PAGE_STORY_FIELD_UNRESOLVED":
                    !field.Locked&&field.Command is ("PAGE" or "NUMPAGES" or "SECTIONPAGES" or "PAGEREF")?
                        "PAGE_STORY_FIELD_DYNAMIC_PENDING":"PAGE_STORY_FIELD_STORED";
                storyIssues.Add(new(code,model.Source(field.Id).Location,
                    cached?"Résultat stocké conservé pour "+field.Command+".":"Champ auxiliaire sans résultat stocké exploitable.",
                    code!="PAGE_STORY_FIELD_STORED"));
            }
            if(layout.Issues.Any(i=>i.BlocksRendering)||storyIssues.Any(i=>i.BlocksRendering&&i.Code!="PAGE_STORY_FIELD_DYNAMIC_PENDING"))
                return new(null,0,storyIssues.ToImmutable());
            if(layout.Paragraphs.Count==0)
                return new(new(1,[]){Geometry=geometry,NativePainter=painter},0,storyIssues.ToImmutable());
            try
            {
                var adjusted=layout.Paragraphs.ToDictionary(p=>p.Id,StringComparer.Ordinal);
                foreach(var run in layout.Paragraphs.SelectMany(p=>p.Runs).Where(r=>r.Image is not null))
                    images.TryAdd(run.Id,run.Image!);
                var markers=painter.MeasureListMarkers(layout.Paragraphs,text,token);
                var grids=layout.Tables.Select(t=>Composer.FitPageTable(t,adjusted,check,
                    p=>Intrinsic(p,text.GetValueOrDefault(p.Id),images,check,markers.GetValueOrDefault(p.Id)))).ToImmutableArray();
                if(grids.Any(t=>t.WidthResult?.ContentVerified!=true))
                    return Failed("PAGE_STORY_TABLE_UNMEASURED",source.Location);
                var aliases=grids.SelectMany(t=>t.Cells).Where(c=>c.MergeOrigin is not null)
                    .SelectMany(c=>c.ParagraphIds).ToHashSet(StringComparer.Ordinal);
                if(aliases.Any(id=>text.TryGetValue(id,out var paragraph)&&paragraph.ContextLength>0))
                    return Failed("PAGE_STORY_MERGE_CONTENT",source.Location);
                var items=layout.Paragraphs.Select(p=>adjusted[p.Id]).Where(p=>!aliases.Contains(p.Id)).ToArray();
                var widths=items.Where(p=>p.TableId is null).ToDictionary(p=>p.Id,
                    p=>new LineRegion(p.CellOwner??story.RootId,0,Emu(p.Geometry.Width)-Emu(p.Geometry.Left)-Emu(p.Geometry.Right)),StringComparer.Ordinal);
                var measured=painter.Compose(r=>widths[r.ParagraphId],token,widths.Keys.ToHashSet(StringComparer.Ordinal),
                    markers,paragraphs[story.RootId]);
                foreach(var line in measured.Lines)
                {
                    check();
                    storyIssues.AddRange(line.Diagnostics.Where(i=>i.Code!="LINE_SPACING_PENDING").Select(i=>i with{BlocksRendering=BlocksLine(i)}));
                    if(line.Cut is LineCutKind.Page or LineCutKind.Column||line.Properties.PageBefore||line.Occupied is null||
                        line.Parts.Any(p=>p.Fragment is {} fragment&&fragment.Style.Underline!="none"))
                        return Failed("PAGE_STORY_LINE_UNQUALIFIED",line.ParagraphSource.Location);
                }
                if(storyIssues.Any(i=>i.BlocksRendering&&i.Code!="PAGE_STORY_FIELD_DYNAMIC_PENDING"))
                    return new(null,0,storyIssues.ToImmutable());
                var byId=measured.Lines.GroupBy(l=>l.ParagraphId).ToDictionary(g=>g.Key,g=>g.ToImmutableArray(),StringComparer.Ordinal);
                var placed=Composer.PlaceNativeLayout(layout with{Paragraphs=items,Tables=grids},byId,painter,check,()=>{});
                if(placed.Pages.Count!=1)
                {
                    storyIssues.AddRange(placed.Issues);
                    return Failed("PAGE_STORY_PLACEMENT_UNQUALIFIED",source.Location);
                }
                var page=placed.Pages[0];
                float top=Math.Min(0,page.Cells.Select(c=>c.Y-(c.Borders.Top?.Width??0)/2).DefaultIfEmpty().Min());
                float bottom=Math.Max(page.Lines.Select(l=>l.NativeTop+l.Height).DefaultIfEmpty().Max(),
                    page.Cells.Select(c=>c.Y+c.Height+(c.Borders.Bottom?.Width??0)/2).DefaultIfEmpty().Max());
                return new(page,Emu(bottom)-Emu(top),storyIssues.ToImmutable()){TopInk=Emu(top)};
            }
            catch(TableWidthFailure error){return Failed(error.Message,error.Location??source.Location);}
            catch(InvalidDataException error) when (!error.Message.EndsWith("_LIMIT",StringComparison.Ordinal))
            {return Failed(error.Message,source.Location);}

            StoryTemplate Failed(string code,LecteurWord.DocxCore.SourceLocation location)
            {
                storyIssues.Add(new(code,location,"L’histoire et ses sources sont conservées. Son placement reste hors profil.",true));
                return new(null,0,storyIssues.ToImmutable());
            }
        }
    }
}
