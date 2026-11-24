using System.Collections.Immutable;
using System.Diagnostics;

namespace LecteurWord.Flow;

internal sealed record PageStoryRegion(int Section,string Kind,string Variant,RunningStory? Selection,
    PageRegion? AvailableRegion,string Status,int Lines,int Cells,ImmutableArray<FlowIssue> Diagnostics)
{
    internal ImmutableArray<int> PhysicalPages {get;init;}=[];
    internal int FloatingFrames {get;init;}
    internal long? FlowHeight {get;init;}
}
internal sealed record PageStoryPlacement(RunningStory Selection,PageRegion Region,IReadOnlyList<PageLine> Lines,IReadOnlyList<PageCell> Cells);

internal static partial class PageComposer
{
    const int MaxStoryTemplates=128,MaxStoryPlacements=60_000;
    sealed record StoryTemplate(ComposedPage? Page,long Height,ImmutableArray<FlowIssue> Issues)
    {
        internal long TopInk {get;init;}
        internal long? StructuralHeight {get;init;}
        internal ImmutableArray<PendingStoryField> Fields {get;init;}=[];
        internal PaginatedDocument? Layout {get;init;}

        internal StoryTemplate FitHeader(long available)=>Height>available&&StructuralHeight is {} minimum?
            this with{Height=minimum}:this;
    }

    static ImmutableArray<PageSection> MeasureRunningMargins(ImmutableArray<PageSection> sections,SemanticDocument model,
        LayoutReader.Reader reader,LineComposer painter,TypographySession session,TypographyDocument typography,
        Dictionary<string,TextParagraph> text,Dictionary<string,InlineImage> images,ImmutableArray<FlowIssue> semanticIssues,
        TableStyleCascade tableStyles,Action check,CancellationToken token)
    {
        if(!sections.Any(s=>!s.Semantic.RunningStories.IsEmpty))return sections;
        var plan=ComposeRunningStories(new([],[]),sections,model,reader,painter,session,typography,text,images,semanticIssues,
            tableStyles,ImmutableDictionary<string,PageBookmark>.Empty,check,_=>{},ImmutableArray.CreateBuilder<PagePlacementGap>(),
            token,completeBody:false,prepareOnly:true).Item2.ToLookup(r=>r.Section);
        return sections.Select(section=>
        {
            var margins=ImmutableDictionary.CreateBuilder<string,StoryMargins>();
            foreach(var variant in plan[section.Semantic.Number].GroupBy(r=>r.Variant))
            {
                check();
                long top=section.Body.Top,bottom=section.PhysicalHeight-top-section.Body.Height;
                long adjustedTop=Reserve("header",section.HeaderDistance,top);
                long adjustedBottom=Reserve("footer",section.FooterDistance,bottom);
                if((adjustedTop>top||adjustedBottom>bottom)&&section.PhysicalHeight-adjustedTop-adjustedBottom>=Emu(36))
                    margins.Add(variant.Key,new(adjustedTop,adjustedBottom));

                long Reserve(string kind,long? distance,long declared)=>
                    distance is {} offset&&variant.Single(r=>r.Kind==kind).FlowHeight is >0 and var occupied?
                        Math.Max(declared,offset+occupied):declared;
            }
            return margins.Count==0?section:section with{RunningMargins=margins.ToImmutable()};
        }).ToImmutableArray();
    }

    static (Composition,ImmutableArray<PageStoryRegion>,double ResolutionMs,double RecompositionMs) ComposeRunningStories(
        Composition body,ImmutableArray<PageSection> sections,SemanticDocument model,LayoutReader.Reader reader,
        LineComposer painter,TypographySession session,TypographyDocument typography,Dictionary<string,TextParagraph> text,
        Dictionary<string,InlineImage> images,ImmutableArray<FlowIssue> semanticIssues,TableStyleCascade tableStyles,
        IReadOnlyDictionary<string,PageBookmark> bookmarks,
        Action check,Action<FlowIssue> add,ImmutableArray<PagePlacementGap>.Builder gaps,CancellationToken token,bool completeBody,
        bool prepareOnly=false)
    {
        var roots=model.Stories.Where(s=>s.Kind is "hdr" or "ftr").ToDictionary(s=>s.RootId,StringComparer.Ordinal);
        var paragraphs=typography.Paragraphs.Where(p=>roots.ContainsKey(p.StoryId)).ToLookup(p=>p.StoryId,StringComparer.Ordinal);
        var frameRoots=model.Stories.Where(s=>s.Kind=="txbxContent").ToDictionary(s=>s.RootId,StringComparer.Ordinal);
        var frameText=typography.Paragraphs.Where(p=>frameRoots.ContainsKey(p.StoryId)).ToLookup(p=>p.StoryId,StringComparer.Ordinal);
        var frameFields=model.Fields.Where(f=>frameRoots.ContainsKey(f.StoryId)).Select(f=>f.StoryId).ToHashSet(StringComparer.Ordinal);
        var frameTemplates=new Dictionary<string,(FloatingTextFrame Frame,ComposedPage Page)>(StringComparer.Ordinal);
        var fields=model.Fields.Where(f=>roots.ContainsKey(f.StoryId)&&!model.TocMarkerFields.Contains(f.Id))
            .ToLookup(f=>f.StoryId,StringComparer.Ordinal);
        var storyTokens=typography.Tokens.Where(t=>t.ParagraphId is not null&&t.StoryId is {} id&&roots.ContainsKey(id))
            .ToLookup(t=>t.ParagraphId!,StringComparer.Ordinal);
        var properties=fields.Any(story=>story.Any(f=>f.Command=="DOCPROPERTY"))?model.TextDocumentProperties(check):null;
        var parentFields=model.Fields.Where(f=>f.ParentFieldId is not null).Select(f=>f.ParentFieldId!).ToHashSet(StringComparer.Ordinal);
        var partIssues=semanticIssues.Where(i=>i.Code is "SEMANTIC_MUST_UNDERSTAND" or "SEMANTIC_ALTERNATIVE_UNRESOLVED")
            .Concat(typography.Diagnostics.Where(i=>i.Code is "TEXT_WORD_RTL_PARTIAL" or "TEXT_PROPERTY_PARTIAL" or
                "TEXT_HIDDEN_PARAGRAPH_MARK" or "TEXT_HIDDEN_CONTROL_UNQUALIFIED" or "TEXT_VISIBILITY_UNRESOLVED"))
            .Concat(tableStyles.Issues.Select(i=>i.Issue))
            .Where(i=>i.Source is not null).ToLookup(i=>i.Source!.PartUri,StringComparer.Ordinal);
        var templates=new Dictionary<(string Root,PageGeometry Geometry),StoryTemplate>();
        var resolvedTemplates=new Dictionary<(string Root,PageGeometry Geometry,string Value),StoryTemplate>();
        var placedBookmarks=PlaceBookmarks(body,bookmarks,check);
        var sectionTotals=completeBody&&fields.Any(group=>group.Any(f=>f.Command=="SECTIONPAGES"))?
            SectionPageTotals(body,check):null;
        var generatedRegions=new HashSet<(int Section,string Kind,string Variant)>();
        var unresolvedRegions=new HashSet<(int Section,string Kind,string Variant)>();
        long resolutionTicks=0,recompositionTicks=0;
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
            long bodyTop=section.Body.Top,bodyBottom=section.PhysicalHeight-section.Body.Top-section.Body.Height;
            if(section.RunningMargins.TryGetValue(variant,out var margin))
                (bodyTop,bodyBottom)=(margin.Top,margin.Bottom);
            long height=distance is {} d?(kind=="header"?bodyTop:bodyBottom)-d:0;
            var geometry=section.Format with{Top=0,Bottom=0};
            var template=Template(story,geometry);
            if(kind=="header")template=template.FitHeader(height);
            if(distance is null||height<0||section.Body.Width<=0||
                height==0&&(kind!="header"||template.StructuralHeight is null))
            {
                Set("NoRegion",prepared:template,issues:template.Issues.Add(
                    new("PAGE_STORY_REGION_UNQUALIFIED",reference.Location,"La distance et la marge ne déterminent pas une région auxiliaire utilisable.",true)));
                continue;
            }
            var region=new PageRegion(section.Body.Left,kind=="header"?distance.Value:section.PhysicalHeight-bodyBottom,section.Body.Width,height);
            if(!template.Fields.IsEmpty)
            {
                Set("FieldPending",region,template);
                continue;
            }
            if(template.Page is null)
            {
                Set("Unplaced",region,issues:template.Issues);
                continue;
            }
            if(template.Height>region.Height+Emu(.01f))
            {
                Set("Overlap",region,template,template.Issues.Add(new("PAGE_STORY_OVERLAP",reference.Location,
                    "L’histoire dépasserait la marge du corps. Une recomposition avec régions interdépendantes est nécessaire.",true)));
                continue;
            }
            Set("Measured",region,template,template.Issues);

            void Set(string status,PageRegion? available=null,StoryTemplate? prepared=null,ImmutableArray<FlowIssue> issues=default)
            {
                plan.Add(key,(new(key.Number,kind,variant,selection,available,status,prepared?.Page?.Lines.Count??0,
                    prepared?.Page?.Cells.Count??0,issues.IsDefault?[]:issues)
                    {FloatingFrames=prepared?.Page?.FloatingTextFrames.Count??0,FlowHeight=prepared?.Page is null?null:prepared.Height},prepared));
            }
        }
        if(prepareOnly)return (body,plan.Values.Select(p=>p.Region).ToImmutableArray(),0,0);

        var result=new List<ComposedPage>(body.Pages.Count);
        var used=new Dictionary<(int Section,string Kind,string Variant),List<int>>();
        var described=new HashSet<string>(StringComparer.Ordinal);
        int previousSection=0;
        int occurrences=0,fieldGlyphs=0,fieldOccurrences=0;
        foreach(var page in body.Pages)
        {
            check();
            if(page.IntercalarySectionBreak is not null){result.Add(page);continue;}
            var section=sections[page.Section-1];
            bool first=page.Section!=previousSection;
            int? displayedNumber=page.PrintedPageNumber;
            if(displayedNumber is null&&section.EvenAndOdd&&!(section.TitlePage&&first))
            {
                add(new("PAGE_STORY_PARITY_UNRESOLVED",section.Source?.Location,
                    "La numérotation déclarée ne permet pas de choisir les histoires paires et impaires.",true));
                result.Add(page);
                previousSection=page.Section;
                continue;
            }
            string variant=section.RunningVariant(first,displayedNumber);
            var stories=ImmutableArray.CreateBuilder<PageStoryPlacement>(2);
            var pageFrames=page.FloatingTextFrames.ToImmutableArray().ToBuilder();
            foreach(string kind in new[]{"header","footer"})
            {
                var key=(page.Section,kind,variant);
                var entry=plan[key];
                if(!used.TryGetValue(key,out var physical))used[key]=physical=[];
                physical.Add(page.Number);
                var available=entry.Region.AvailableRegion;
                var geometry=page.Geometry with{Top=0,Bottom=0};
                bool distinctMargins=available is not null&&geometry!=section.Format with{Top=0,Bottom=0};
                if(!distinctMargins)
                    foreach(var issue in entry.Region.Diagnostics)add(issue);
                if(entry.Region.Selection is not {} selection||selection.Reference.TargetId is not {} root)continue;
                described.Add(root);
                var prepared=entry.Template;
                bool flowOverlap=entry.Region.Status=="Overlap";
                if(distinctMargins)
                {
                    available=available!.Value with{Left=Emu(page.Geometry.Left)};
                    prepared=Template(roots[root],geometry);
                    if(kind=="header")prepared=prepared.FitHeader(available.Value.Height);
                    foreach(var issue in prepared.Issues)add(issue);
                    flowOverlap=prepared.Fields.IsEmpty&&prepared.Height>available.Value.Height+Emu(.01f);
                    if(flowOverlap)
                    {
                        add(new("PAGE_STORY_OVERLAP",model.Source(selection.ReferenceId).Location,
                            "L’histoire dépasserait la marge du corps.",true));
                    }
                }
                if(available is not null&&prepared is {Fields.IsEmpty:false,Layout:{} storyLayout})
                {
                    var pending=prepared.Fields;
                    var values=new string[pending.Length];
                    bool resolved=true;
                    long started=Stopwatch.GetTimestamp();
                    for(int i=0;i<pending.Length;i++)
                    {
                        check();
                        var planField=pending[i];
                        if(Resolve(planField,section,displayedNumber) is {} value){values[i]=value;continue;}
                        var field=planField.Field;
                        string code=field.Command is ("PAGEREF" or "REF")&&(field.BookmarkId is not {} target||!bookmarks.ContainsKey(target))?
                            "PAGE_FIELD_BOOKMARK_UNRESOLVED":field.Command=="SECTIONPAGES"?
                                "PAGE_FIELD_SECTION_TOTAL_UNRESOLVED":"PAGE_STORY_FIELD_UNRESOLVED";
                        add(new(code,model.Source(field.Id).Location,"Valeur du champ auxiliaire indéterminée.",true));
                        Gap(root,code);
                        resolved=false;
                    }
                    resolutionTicks+=Stopwatch.GetTimestamp()-started;
                    if(!resolved)
                    {
                        unresolvedRegions.Add(key);
                        continue;
                    }
                    fieldOccurrences+=pending.Length;
                    if(fieldOccurrences>MaxStoryPlacements)throw new InvalidDataException("PAGE_STORY_FIELD_OCCURRENCE_LIMIT");
                    var cacheKey=(root,geometry,string.Concat(values.Select(v=>v.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+v)));
                    if(!resolvedTemplates.TryGetValue(cacheKey,out prepared))
                    {
                        if(resolvedTemplates.Count>=MaxStoryTemplates)throw new InvalidDataException("PAGE_STORY_FIELD_VALUE_LIMIT");
                        started=Stopwatch.GetTimestamp();
                        var generated=new Dictionary<string,GeneratedPageField>(StringComparer.Ordinal);
                        var fieldIssues=Template(roots[root],geometry).Issues.ToBuilder();
                        for(int i=0;i<pending.Length;i++)
                        {
                            check();
                            var item=pending[i];
                            var origin=model.Source(item.Field.Id);
                            string value=values[i];
                            var measured=session.ShapeLineDecoration(item.Paragraph.Id,value,item.Style,origin,0,check,count=>
                            {
                                fieldGlyphs+=count;
                                if(fieldGlyphs>500_000)throw new InvalidDataException("PAGE_STORY_FIELD_GLYPH_LIMIT");
                            });
                            if(measured is null)
                                fieldIssues.Add(new("PAGE_FIELD_GLYPH_UNRESOLVED",origin.Location,"Glyphes du résultat calculé indisponibles.",true));
                            else
                                generated.Add(item.Inline?item.Field.Id:item.Paragraph.Id,
                                    new(origin,value,item.Style){Measured=measured,StoredRange=item.StoredRange});
                        }
                        prepared=fieldIssues.Any(i=>i.BlocksRendering)?new(null,0,fieldIssues.ToImmutable()):
                            PlaceStory(roots[root],geometry,storyLayout,fieldIssues,pending,generated);
                        resolvedTemplates.Add(cacheKey,prepared);
                        recompositionTicks+=Stopwatch.GetTimestamp()-started;
                    }
                    foreach(var issue in prepared.Issues)add(issue);
                    if(kind=="header")prepared=prepared.FitHeader(available.Value.Height);
                    if(prepared.Height>available!.Value.Height+Emu(.01f))
                    {
                        add(new("PAGE_STORY_OVERLAP",model.Source(pending[0].Field.Id).Location,
                            "Le résultat calculé dépasse la région auxiliaire.",true));
                        unresolvedRegions.Add(key);
                        Gap(root,"PAGE_STORY_OVERLAP");
                        continue;
                    }
                    if(prepared.Page is not null)generatedRegions.Add(key);
                    else unresolvedRegions.Add(key);
                }
                if(prepared?.Page is {} frameContent)
                    foreach(var placement in frameContent.FloatingTextFrames)
                    {
                        check();
                        var frame=placement.Frame;
                        float paragraphTop=0;
                        if(frame.VerticalReference=="paragraph")
                        {
                            if(flowOverlap||available is not {} anchorRegion)continue;
                            var anchor=frameContent.Lines.Single(l=>l.Native?.Parts.Any(p=>p.Control?.Source.NodeId==frame.Id)==true);
                            var firstLine=frameContent.Lines.First(l=>l.ParagraphId==anchor.ParagraphId);
                            long origin=kind=="header"?anchorRegion.Top:anchorRegion.Top+anchorRegion.Height-prepared.Height;
                            paragraphTop=firstLine.NativeTop+Points(origin-prepared.TopInk);
                        }
                        var (frameX,frameY)=frame.PageOrigin(page.Geometry,page.Number,paragraphTop);
                        occurrences+=placement.Lines.Count+placement.Cells.Count+1;
                        if(occurrences>MaxStoryPlacements)throw new InvalidDataException("PAGE_STORY_PLACEMENT_LIMIT");
                        var placed=new PlacedFloatingTextFrame(frame with{X=frameX,Y=frameY},
                            MoveFrame(placement.Content,frameX,frameY,page.Section,page.Geometry)).ClipOn(page.Geometry);
                        if(placed.CrossesSheet(page.Geometry)||placed.OutsideSheet)
                            add(new("PAGE_FLOATING_TEXTBOX_CLIPPED_BY_SHEET",frame.Drawing,
                                "L’ancre est conservée et la peinture est découpée aux limites de la feuille.",false));
                        pageFrames.Add(placed);
                    }
                if(flowOverlap)
                {
                    Gap(root,"PAGE_STORY_OVERLAP");
                    continue;
                }
                if(prepared?.Page is not {} template||available is not {} region)
                {
                    Gap(root,(distinctMargins?prepared?.Issues:entry.Region.Diagnostics)?.FirstOrDefault(i=>i.BlocksRendering)?.Code??"PAGE_STORY_PENDING");
                    continue;
                }
                occurrences+=template.Lines.Count+template.Cells.Count;
                if(occurrences>MaxStoryPlacements)throw new InvalidDataException("PAGE_STORY_PLACEMENT_LIMIT");
                long y=kind=="header"?region.Top:region.Top+region.Height-prepared.Height;
                float shift=Points(y-prepared.TopInk);
                var movedLines=template.Lines.Select(l=>l with
                {
                    Section=page.Section,Baseline=l.Baseline+shift,NativeTop=l.NativeTop+shift,
                    Images=l.Images.Select(i=>i with{Y=i.Y+shift}).ToImmutableArray(),
                    ParagraphBorder=l.ParagraphBorder is {} border?border with{Top=border.Top+shift}:null,
                    ParagraphTopBorder=l.ParagraphTopBorder is {} topBorder?topBorder with{Top=topBorder.Top+shift}:null
                }).ToImmutableArray();
                var movedCells=template.Cells.Select(c=>c with{Y=c.Y+shift}).ToImmutableArray();
                stories.Add(new(selection,region with{Top=y,Height=prepared.Height},movedLines,movedCells));
            }
            previousSection=page.Section;
            result.Add(page with{RunningStories=stories.ToImmutable(),FloatingTextFrames=pageFrames.ToImmutable()});
        }
        var regions=plan.Select(kv=>kv.Value.Region with
        {
            PhysicalPages=used.TryGetValue(kv.Key,out var hits)?[..hits]:[],
            Status=unresolvedRegions.Contains(kv.Key)?"FieldUnresolved":
                generatedRegions.Contains(kv.Key)?"FieldCalculated":kv.Value.Region.Status
        }).ToImmutableArray();
        // Beyond an unplaced primary section, preserve the still referenced auxiliary sources.
        var reached=body.Pages.SelectMany(p=>p.Lines.Select(l=>l.Section).Append(p.Section)).ToHashSet();
        foreach(var entry in regions)
        {
            check();
            if(!reached.Contains(entry.Section)&&entry.Status!="Inactive"&&entry.Selection?.Reference.TargetId is {} root&&described.Add(root))
                Gap(root,"PAGE_STORY_PENDING");
        }
        return (body with{Pages=result.ToImmutableArray()},regions,
            resolutionTicks*1000d/Stopwatch.Frequency,recompositionTicks*1000d/Stopwatch.Frequency);

        void Gap(string root,string code)
        {
            gaps.Add(new(model.Source(root),code));
            foreach(var paragraph in paragraphs[root]){check();gaps.Add(new(model.Source(paragraph.Id),code));}
        }

        string? Resolve(PendingStoryField field,PageSection section,int? displayedNumber)
        {
            if(field.PropertyValue is {} property)return property;
            if(field.Field.Command=="PAGE")
                return FormatPageNumber(displayedNumber,section.PageNumberFormat,field.Syntax!);
            if(!completeBody)return null;
            return field.Field.Command switch
            {
                "NUMPAGES"=>FormatPageNumber(body.Pages.Count,"decimal",field.Syntax!),
                "SECTIONPAGES" when sectionTotals is not null&&sectionTotals.TryGetValue(section.Semantic.Number,out int total)=>
                    FormatPageNumber(total,"decimal",field.Syntax!),
                "PAGEREF" when field.Field.BookmarkId is {} id&&placedBookmarks.TryGetValue(id,out var target)=>
                    FormatPageNumber(body.Pages[target.Page-1].PrintedPageNumber,
                        sections[body.Pages[target.Page-1].Section-1].PageNumberFormat,field.Syntax!),
                "REF" when field.Field.BookmarkId is {} id&&placedBookmarks.ContainsKey(id)=>
                    ResolveBookmarkText(bookmarks.GetValueOrDefault(id),field.Style),
                _=>null
            };
        }

        StoryTemplate Template(SemanticStory story,PageGeometry geometry)
        {
            var key=(story.RootId,geometry);
            if(templates.TryGetValue(key,out var template))return template;
            if(templates.Count>=MaxStoryTemplates)throw new InvalidDataException("PAGE_STORY_TEMPLATE_LIMIT");
            templates.Add(key,template=Prepare(story,geometry));
            return template;
        }

        StoryTemplate Prepare(SemanticStory story,PageGeometry geometry)
        {
            var source=model.Source(story.RootId);
            var storyIssues=ImmutableArray.CreateBuilder<FlowIssue>();
            var layout=reader.ReadStory(story,geometry);
            storyIssues.AddRange(layout.Issues);
            storyIssues.AddRange(partIssues[source.Location.PartUri].Select(i=>i with{BlocksRendering=true}));
            var pending=ImmutableArray.CreateBuilder<PendingStoryField>();
            var generatedParagraphs=new HashSet<string>(StringComparer.Ordinal);
            var inlineIds=new HashSet<string>(StringComparer.Ordinal);
            if(layout.Tables.Count==0)
            foreach(var group in fields[story.RootId].GroupBy(f=>Ancestor(model,f.Id,SemanticKind.Paragraph)))
            {
                check();
                if(group.Key is not {} id||!text.TryGetValue(id,out var paragraph))continue;
                var members=group.Take(9).ToArray();
                if(members.Length is <2 or >8)continue;
                var plans=members.Select(field=>ReadInlineStoryField(model,field,paragraph,session,parentFields,check)).ToArray();
                if(plans.Any(p=>p is null))continue;
                if(pending.Count+plans.Length>128)throw new InvalidDataException("PAGE_STORY_FIELD_COUNT_LIMIT");
                pending.AddRange(plans.OfType<PendingStoryField>());
                inlineIds.UnionWith(members.Select(f=>f.Id));
                generatedParagraphs.Add(id);
            }
            foreach(var field in fields[story.RootId])
            {
                check();
                if(inlineIds.Contains(field.Id))continue;
                string? property=field is {Command:"DOCPROPERTY",StructureVerified:true,Locked:false,ParentFieldId:null}&&
                    ReadPropertyOperand(model,field) is {} operand?properties?.GetValueOrDefault(operand):null;
                if(property is not null&&property==model.StoredText(field))
                {
                    storyIssues.Add(new("PAGE_STORY_PROPERTY_VERIFIED",model.Source(field.Id).Location,
                        "Résultat DOCPROPERTY vérifié dans les propriétés textuelles internes du paquet.",false));
                    continue;
                }
                var planned=ReadStoryField(model,field,layout,text,session,parentFields,check,property);
                if(planned is null&&layout.Tables.Count==0&&!field.StoredResultIds.IsEmpty&&
                    Ancestor(model,field.Id,SemanticKind.Paragraph) is {} id&&text.TryGetValue(id,out var sourceParagraph))
                    planned=ReadInlineStoryField(model,field,sourceParagraph,session,parentFields,check,requireFollowingContent:true);
                if(planned is not null&&
                    generatedParagraphs.Add(planned.Paragraph.Id))
                {
                    if(pending.Count>=128)throw new InvalidDataException("PAGE_STORY_FIELD_COUNT_LIMIT");
                    pending.Add(planned);
                    continue;
                }
                bool cached=field.StructureVerified&&!field.StoredResultIds.IsEmpty&&model.StoredText(field) is not null;
                string code=!cached||property is not null?"PAGE_STORY_FIELD_UNRESOLVED":
                    !field.Locked&&field.Command is ("PAGE" or "NUMPAGES" or "SECTIONPAGES" or "PAGEREF" or "REF")?
                        "PAGE_STORY_FIELD_DYNAMIC_PENDING":"PAGE_STORY_FIELD_STORED";
                storyIssues.Add(new(code,model.Source(field.Id).Location,
                    cached?"Résultat stocké conservé pour "+field.Command+".":"Champ auxiliaire sans résultat stocké exploitable.",
                    code!="PAGE_STORY_FIELD_STORED"));
            }
            if(pending.Count>0)
                return storyIssues.Any(i=>i.BlocksRendering)?new(null,0,storyIssues.ToImmutable()):
                    new(null,0,storyIssues.ToImmutable()){Fields=pending.ToImmutable(),Layout=layout};
            return PlaceStory(story,geometry,layout,storyIssues,[],null);
        }

        StoryTemplate PlaceStory(SemanticStory story,PageGeometry geometry,PaginatedDocument layout,
            ImmutableArray<FlowIssue>.Builder storyIssues,ImmutableArray<PendingStoryField> pending,
            IReadOnlyDictionary<string,GeneratedPageField>? generated)
        {
            var source=model.Source(story.RootId);
            if(layout.Issues.Any(i=>i.BlocksRendering)||storyIssues.Any(i=>i.BlocksRendering&&i.Code!="PAGE_STORY_FIELD_DYNAMIC_PENDING"))
                return new(null,0,storyIssues.ToImmutable());
            if(layout.Paragraphs.Count==0)
                return new(new(1,[]){Geometry=geometry,NativePainter=painter},0,storyIssues.ToImmutable());
            try
            {
                var adjusted=layout.Paragraphs.ToDictionary(p=>p.Id,StringComparer.Ordinal);
                foreach(var run in layout.Paragraphs.SelectMany(p=>p.Runs).Where(r=>r.Image is not null))
                    images.TryAdd(run.Id,run.Image!);
                var overrides=pending.Where(f=>!f.Inline).ToDictionary(f=>f.Paragraph.Id,f=>f.Paragraph,StringComparer.Ordinal);
                foreach(var group in pending.Where(f=>f.Inline).GroupBy(f=>f.Paragraph.Id))
                {
                    check();
                    overrides.Add(group.Key,session.ShapeInlineStoryFields(group.First().Paragraph,storyTokens[group.Key],
                        group.Select(f=>generated![f.Field.Id]).ToArray(),
                        group.SelectMany(f=>f.Field.StoredResultIds).ToHashSet(StringComparer.Ordinal)));
                }
                TextParagraph? Paragraph(string id)=>overrides.GetValueOrDefault(id)??text.GetValueOrDefault(id);
                var frames=ImmutableArray.CreateBuilder<PlacedFloatingTextFrame>();
                var frameIds=new HashSet<string>(StringComparer.Ordinal);
                foreach(var run in layout.Paragraphs.SelectMany(p=>p.Runs).Where(r=>r.FloatingTextFrame is not null))
                {
                    check();
                    var frame=run.FloatingTextFrame!;
                    if(!frameIds.Add(frame.Id)||frameIds.Count>128)
                        return Failed("PAGE_STORY_FLOATING_FRAME_LIMIT",frame.Drawing);
                    if(!frameTemplates.TryGetValue(frame.Id,out var positioned))
                    {
                        if(frameTemplates.Count>=512)throw new InvalidDataException("PAGE_FLOATING_PICTURE_LIMIT");
                        if(!frameRoots.TryGetValue(frame.StoryId,out var frameStory)||frameFields.Contains(frame.StoryId)||
                            PrepareFrame(frame,frameStory,reader,painter,text,images,frameText[frame.StoryId],check,
                                storyIssues.Add,token) is not {} composed)
                            return Failed("PAGE_STORY_FLOATING_FRAME_LAYOUT_UNQUALIFIED",frame.Drawing);
                        frameTemplates.Add(frame.Id,positioned=composed);
                    }
                    frames.Add(new(positioned.Frame,positioned.Page));
                }
                var content=ComposeAuxiliaryLayout(story,layout,adjusted,painter,images,text,Paragraph,
                    paragraphs[story.RootId].Select(p=>overrides.GetValueOrDefault(p.Id)??p),generated,storyIssues,check,token,frameIds);
                if(content.Page is not {} page)
                    return content.Failure is {} reason?Failed(reason,content.Location??source.Location):new(null,0,storyIssues.ToImmutable());
                float top=Math.Min(0,page.Cells.Select(c=>c.Y-(c.Borders.Top?.Width??0)/2).DefaultIfEmpty().Min());
                float bottom=Math.Max(page.Lines.Select(l=>l.ParagraphBorder is {} border?
                        border.Top+border.Border.Width:l.NativeTop+l.Height).DefaultIfEmpty().Max(),
                    page.Cells.Select(c=>c.Y+c.Height+(c.Borders.Bottom?.Width??0)/2).DefaultIfEmpty().Max());
                var found=page.Lines.SelectMany(l=>l.Native?.Parts??[]).Where(p=>p.Control is {Kind:"Drawing"})
                    .Select(p=>p.Control!.Value.Source.NodeId).Where(frameIds.Contains).ToArray();
                if(found.Length!=frameIds.Count||!frameIds.SetEquals(found))
                    return Failed("PAGE_STORY_FLOATING_FRAME_ANCHOR_UNQUALIFIED",source.Location);
                long? structuralHeight=null;
                // A single anchor or paragraph mark has no ink. Keep all decorations and visible content.
                if(page.Cells.Count==0&&page.Lines is [var line]&&line.Native is {} native&&
                    native.Parts.All(p=>p.Control is {Kind:"Drawing"} control&&frameIds.Contains(control.Source.NodeId))&&
                    native.Generated.IsEmpty&&line.Images.Count==0&&line.ParagraphBorder is null&&
                    line.LeftBorder is null&&line.RightBorder is null&&line.ParagraphShading is null)
                    structuralHeight=line.ParagraphTopBorder is {} rule?Emu(rule.Top+rule.Border.Width):0;
                return new(page with{FloatingTextFrames=frames.ToImmutable()},Emu(bottom)-Emu(top),storyIssues.ToImmutable())
                {TopInk=Emu(top),StructuralHeight=structuralHeight};
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

    static (ComposedPage? Page,string? Failure,LecteurWord.DocxCore.SourceLocation? Location) ComposeAuxiliaryLayout(
        SemanticStory story,PaginatedDocument layout,Dictionary<string,LayoutParagraph> adjusted,LineComposer painter,
        Dictionary<string,InlineImage> images,Dictionary<string,TextParagraph> text,Func<string,TextParagraph?> paragraph,
        IEnumerable<TextParagraph> ordered,IReadOnlyDictionary<string,GeneratedPageField>? generated,
        ImmutableArray<FlowIssue>.Builder diagnostics,Action check,CancellationToken token,IReadOnlySet<string>? floatingIds=null,
        bool unwrappedFrame=false)
    {
        var markers=painter.MeasureListMarkers(layout.Paragraphs,text,token);
        var grids=layout.Tables.Select(t=>Composer.FitPageTable(t,adjusted,check,p=>
        {
            var field=generated?.GetValueOrDefault(p.Id);
            return Intrinsic(p,paragraph(p.Id),images,check,markers.GetValueOrDefault(p.Id),
                field?.Measured?.Measure?.Metrics.Width??0,fixedTabs:field is not null&&!t.AutoFit);
        })).ToImmutableArray();
        if(grids.Any(t=>t.WidthResult?.ContentVerified!=true))return (null,"PAGE_STORY_TABLE_UNMEASURED",null);
        var aliases=grids.SelectMany(t=>t.Cells).Where(c=>c.MergeOrigin is not null)
            .SelectMany(c=>c.ParagraphIds).ToHashSet(StringComparer.Ordinal);
        if(aliases.Any(id=>paragraph(id) is {ContextLength:>0}||generated?.ContainsKey(id)==true))
            return (null,"PAGE_STORY_MERGE_CONTENT",null);
        var items=layout.Paragraphs.Select(p=>adjusted[p.Id]).Where(p=>!aliases.Contains(p.Id)).ToArray();
        var widths=items.Where(p=>p.TableId is null).ToDictionary(p=>p.Id,
            p=>new LineRegion(p.CellOwner??story.RootId,0,Emu(p.Geometry.Width)-Emu(p.Geometry.Left)-Emu(p.Geometry.Right))
                {NoWrap=unwrappedFrame,AllowHorizontalOverflow=unwrappedFrame},StringComparer.Ordinal);
        var measured=painter.Compose(r=>widths[r.ParagraphId],token,widths.Keys.ToHashSet(StringComparer.Ordinal),
            markers,ordered,generated,auxiliaryFloatingIds:floatingIds);
        foreach(var line in measured.Lines)
        {
            check();
            diagnostics.AddRange(line.Diagnostics.Where(i=>i.Code!="LINE_SPACING_PENDING").Select(i=>i with{BlocksRendering=BlocksLine(i)}));
            if(line.Cut is LineCutKind.Page or LineCutKind.Column||line.Properties.PageBefore||line.Occupied is null||
                line.HasUnsupportedUnderline)
                return (null,"PAGE_STORY_LINE_UNQUALIFIED",line.ParagraphSource.Location);
        }
        if(diagnostics.Any(i=>i.BlocksRendering&&i.Code!="PAGE_STORY_FIELD_DYNAMIC_PENDING"))return (null,null,null);
        var byId=measured.Lines.GroupBy(l=>l.ParagraphId).ToDictionary(g=>g.Key,g=>g.ToImmutableArray(),StringComparer.Ordinal);
        var placed=Composer.PlaceNativeLayout(layout with{Paragraphs=items,Tables=grids},byId,painter,check,()=>{},
            textBoxBorders:story.Kind=="txbxContent");
        if(placed.Pages.Count==1&&!placed.Issues.Any(i=>i.BlocksRendering))return (placed.Pages[0],null,null);
        diagnostics.AddRange(placed.Issues);
        return (null,"PAGE_STORY_PLACEMENT_UNQUALIFIED",null);
    }
}
