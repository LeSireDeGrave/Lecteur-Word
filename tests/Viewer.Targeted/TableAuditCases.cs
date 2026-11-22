using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static class TableAuditCases
{
    internal static void Run(string root)
    {
        string path=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_MANIFEST")
            ??throw new InvalidOperationException("Manifeste privé attendu.");
        using var manifest=JsonDocument.Parse(File.ReadAllText(path));
        var rows=manifest.RootElement.EnumerateArray().OrderBy(x=>x.GetProperty("id").GetInt32()).ToArray();
        LineCases.Check(rows.Select(r=>r.GetProperty("id").GetInt32()).SequenceEqual(Enumerable.Range(0,11)),"Onze copies attendues.");
        var report=new List<object>();
        foreach(var row in rows)
        {
            int id=row.GetProperty("id").GetInt32();
            string copy=row.GetProperty("copy").GetString()!,hash=row.GetProperty("sha256").GetString()!;
            LineCases.Check(Hash(copy)==hash,"Empreinte de copie incorrecte.");
            var input=DocumentInspector.InspectDocument(copy);
            var frozen=ManifestJson.Serialize(input.Manifest);
            report.Add(Probe(id,input));
            LineCases.Check(Hash(copy)==hash&&frozen.SequenceEqual(ManifestJson.Serialize(input.Manifest)),"Source ou inventaire modifié.");
        }
        File.WriteAllText(Path.Combine(root,"tables.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
    }

    static object Probe(int id,InspectionResult input)
    {
        long retainedBefore=GC.GetTotalMemory(true);
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        long start=Stopwatch.GetTimestamp();
        using var lease=PageComposer.Compose(input);
        var snapshot=lease.Snapshot;
        var measuredMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        var retainedBytes=GC.GetTotalMemory(true)-retainedBefore;
        var m=snapshot.Semantic!;
        var typography=snapshot.Typography!;
        double? drawMs=null;
        if(id==4&&snapshot.Composition.Pages.Count>0)
        {
            var page=snapshot.Composition.Pages[0];
            using var surface=SKSurface.Create(new SKImageInfo((int)Math.Ceiling(page.Geometry.Width),(int)Math.Ceiling(page.Geometry.Height)));
            long draw=Stopwatch.GetTimestamp();
            Composer.DrawPage(surface.Canvas,page);
            surface.Canvas.Flush();
            drawMs=Stopwatch.GetElapsedTime(draw).TotalMilliseconds;
        }
        string geometrySha256=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            snapshot.Composition.Pages.Select(p=>new{p.Number,p.Geometry,
                lines=p.DisplayLines.Select(l=>new{l.ParagraphId,l.Source,l.X,l.Baseline,l.Height,l.Text,l.Fragments,
                    advance=l.Native?.Advance,positions=l.Native?.Visual,styles=l.Native?.Parts.Select(part=>part.Fragment?.Style)}),
                cells=p.DisplayCells}))));
        var text=typography.Paragraphs.ToDictionary(p=>p.Id);
        var layout=snapshot.RulerModel;
        var paragraphs=layout?.Paragraphs.ToDictionary(p=>p.Id)??[];
        var grids=layout?.Tables.ToDictionary(t=>t.Id)??[];
        var sections=m.Sections.SelectMany(s=>m.BodyBlocks[s.StartBlock..s.EndBlockExclusive].Select(b=>(b,s.Number))).ToDictionary(x=>x.b,x=>x.Number);
        var placed=snapshot.Pages.SelectMany(p=>p.Geometry.Cells).Select(c=>c.TableId).ToHashSet();
        var gapById=snapshot.Gaps.Where(g=>g.ContextRange is null).ToLookup(g=>g.Source.NodeId);
        var firstGap=m.BodyBlocks.SelectMany(b=>gapById[b]).FirstOrDefault(g=>g.Reason!="PAGE_AFTER_UNPLACED");
        var firstObstacle=m.BodyBlocks.SelectMany(b=>gapById[b]).FirstOrDefault(g=>g.Reason is not ("PAGE_AFTER_UNPLACED" or "PAGE_KEEP_NEXT_BLOCKED"));
        var lineMarkers=(snapshot.Lines?.Lines??[]).Where(l=>l.ListMarker is not null).ToDictionary(l=>l.ParagraphId);
        var placedMarkers=snapshot.Composition.Pages.SelectMany(p=>p.Lines).Where(l=>!l.IsRepeated&&l.Native?.ListMarker is not null)
            .Select(l=>l.ParagraphId).ToHashSet(StringComparer.Ordinal);
        var lists=m.Numbering.Select(n=>
        {
            var marker=snapshot.ListMarkers.GetValueOrDefault(n.ParagraphId);
            var line=lineMarkers.GetValueOrDefault(n.ParagraphId);
            if(marker is not null)
                LineCases.Check(marker.Label.Numbering==n&&marker.Label.Source==m.Source(n.LevelId!).Location&&
                    (line is null||line.Index==0&&line.ListMarker==marker&&line.Parts.All(p=>p.Fragment is null||
                        p.Fragment.Sources.All(s=>s.Source.Span is not null))),"Libellé ou provenance de la liste perdus.");
            return new{numbering=n,source=m.Source(n.ParagraphId).Location,
                definition=n.LevelId is {} level?m.Source(level).Location:null,marker?.Label.Text,marker?.Label.NumId,
                face=marker?.Decoration?.Fragment.Face,marker?.Style.Slots,marker?.X,marker?.TextStart,marker?.Tab,
                advance=marker?.Decoration?.Fragment.Measure?.Metrics.Width,decision=marker?.Decoration?.Fragment.LegacySymbol,composed=line is not null,
                placed=placedMarkers.Contains(n.ParagraphId),issues=marker?.Issues.Select(i=>i.Code).ToArray()};
        }).ToArray();
        int leaders=snapshot.Lines?.Lines.Count(l=>l.Generated.Any(d=>d.Kind=="leader"))??0;
        if(id==5)
            LineCases.Check(snapshot.Sections.Length==10&&grids.Count==16&&leaders==19&&
                firstObstacle is {Reason:"LAYOUT_RUN_CONTENT_UNQUALIFIED",Source.Location.Path:"/2/1/1"}&&
                lists.Count(l=>l.Text=="\uf0b7"&&l.TextStart is not null)==219&&lists.Count(l=>l.Text=="\uf077")==6,
                "Régression des structures Stage 2 ou listes non examinées après le flottant.");
        if(id==4)
            LineCases.Check(snapshot.Composition.Pages.Count>13&&placed.Count==33&&firstObstacle is null&&
                lists.Count(l=>l.Text=="\uf0b7"&&l.placed)==17&&
                snapshot.Composition.Pages.All(p=>p.RunningStories.Count==2)&&
                snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_DIVERGENT"&&i.BlocksRendering),
                "Le Guide doit placer ses histoires et conserver les divergences actives de styles.");
        var listed=new List<object>();
        var images=paragraphs.Values.SelectMany(p=>p.Runs).Where(r=>r.Image is not null).ToDictionary(r=>r.Id,r=>r.Image!);
        var lineProps=new LinePropertiesReader(m);
        foreach(var table in m.Tables)
        {
            var source=m.Source(table.Id);
            var block=table.Id;
            while(!sections.ContainsKey(block)&&m.ParentId(block) is {} parent)block=parent;
            var section=snapshot.Sections.FirstOrDefault(s=>s.Semantic.Number==sections.GetValueOrDefault(block));
            LayoutTable? calculated=grids.GetValueOrDefault(table.Id);
            var calculatedCells=calculated?.Cells.ToDictionary(c=>c.Id);
            var tableProps=m.ChildId(table.Id,"tblPr");
            var styleUse=tableProps is null?null:m.ChildId(tableProps,"tblStyle");
            styleUse??=m.Styles.FirstOrDefault(s=>s.Type=="table"&&s.IsDefault)?.Id;
            var styleChain=styleUse is null?[]:m.StyleUse(styleUse)?.BaseToDerivedIds??[];
            var issues=snapshot.Composition.Issues.Where(i=>i.Source is {} loc&&Below(loc,source.Location)).ToArray();
            var cells=table.Rows.SelectMany(r=>r.Cells).Select(c=>
            {
                string? properties=m.ChildId(c.Id,"tcPr");
                var geometry=calculatedCells?.GetValueOrDefault(c.Id);
                var sizes=new List<(float Minimum,float Preferred)>();
                bool known=true;
                foreach(var p in m.BlocksOf(c.Id).Where(e=>e.Kind==SemanticKind.Paragraph))
                {
                    if(!text.TryGetValue(p.Id,out var shaped)){known=false;continue;}
                    var lp=paragraphs.GetValueOrDefault(p.Id);
                    if(lp is null)
                    {
                        var property=lineProps.Read(shaped);
                        lp=new(p.Id,m.Source(p.Id).Location,[],new(Left:PageComposer.Points(property.Left),Right:PageComposer.Points(property.Right),
                            FirstLine:PageComposer.Points(property.First)),section?.Semantic.Number??0,section?.Format??new(),"");
                    }
                    try
                    {
                        sizes.Add(PageComposer.Intrinsic(lp,shaped,images,()=>{},snapshot.ListMarkers.GetValueOrDefault(p.Id)));
                    }
                    catch(InvalidDataException){known=false;}
                }
                return new{cell=c,source=m.Source(c.Id).Location,width=Decl(properties,"tcW"),margins=Nested(properties,"tcMar"),
                    borders=Nested(properties,"tcBorders"),conditional=Decl(properties,"cnfStyle"),
                    intrinsicMin=known&&sizes.Count>0?(float?)sizes.Max(s=>s.Minimum):null,
                    intrinsicPreferred=known&&sizes.Count>0?(float?)sizes.Max(s=>s.Preferred):null,
                    effectiveCell=geometry,applied=geometry is null?(float?)null:calculated!.Columns.Skip(geometry.Column).Take(geometry.ColumnSpan).Sum(),
                    appliedStyles=geometry?.AppliedTableStyles.Select(s=>new{type=m.Attribute(s,"type"),source=m.Source(s).Location}).ToArray()};
            }).ToArray();
            listed.Add(new{table=table.Id,source=source.Location,section=section?.Semantic.Number,regionPt=section is null?(float?)null:PageComposer.Points(section.Body.Width),
                tblW=Decl(tableProps,"tblW"),tblInd=Decl(tableProps,"tblInd"),tblLayout=Decl(tableProps,"tblLayout"),
                tblLook=Decl(tableProps,"tblLook"),margins=Nested(tableProps,"tblCellMar"),borders=Nested(tableProps,"tblBorders"),
                grid=table.GridColumns.Select(d=>new{source=m.Source(d).Location,width=m.Attribute(d,"w")}).ToArray(),
                style=styleUse is null?null:m.Attribute(styleUse,"val"),layers=styleChain.Select(d=>new{source=m.Source(d).Location,
                    declarations=m.BlocksOf(d).Where(e=>m.Attribute(e.Id,"type") is not null).Select(e=>new{type=m.Attribute(e.Id,"type"),source=m.Source(e.Id).Location}).ToArray()}).ToArray(),
                rows=table.Rows.Select(r=>new{r.Id,conditional=Decl(m.ChildId(r.Id,"trPr"),"cnfStyle")}).ToArray(),
                cells,columns=calculated?.Columns,solver=calculated?.WidthResult,placed=placed.Contains(table.Id),
                placementGap=gapById[table.Id].FirstOrDefault()?.Reason,
                first=issues.FirstOrDefault(i=>i.BlocksRendering)?.Code,issues=issues.Select(i=>new{i.Code,i.Source,i.BlocksRendering})});
        }
        Console.WriteLine($"OK tableaux {id:00} · {listed.Count} inspectés · {grids.Count} grilles · {snapshot.Composition.Pages.Count} pages candidates.");
        var symbols=typography.Paragraphs.SelectMany(p=>p.Fragments.Where(f=>f.LegacySymbol is not null)
            .Select(f=>(Kind:"text",Fragment:f))
            .Concat(p.Controls.Where(c=>c.Symbol is not null).Select(c=>(Kind:"w:sym",Fragment:c.Symbol!))))
            .Select(entry=>new{entry.Kind,entry.Fragment.ParagraphId,entry.Fragment.Text,entry.Fragment.Request,
                entry.Fragment.Sources,entry.Fragment.LegacySymbol,entry.Fragment.Face,entry.Fragment.MeasurementIssue,
                advance=entry.Fragment.Measure?.Metrics.Width});
        return new{id,listed,lists,symbols,status=snapshot.Coverage.ToString(),pages=snapshot.Composition.Pages.Count,
            lines=snapshot.Composition.Pages.SelectMany(p=>p.Lines).Count(l=>!l.IsRepeated),leaders,
            sections=snapshot.Sections.Length,missing=typography.Paragraphs.Sum(p=>p.Fragments.Count(f=>f.Measure is null)),measuredMs,allocatedBytes,retainedBytes,
            firstGap,firstObstacle,storyGaps=snapshot.Gaps.Where(g=>g.Reason=="PAGE_STORY_PENDING").ToArray(),
            sectionSettings=snapshot.Sections.Select(s=>new{s.Semantic.Number,s.Source,s.TitlePage,s.EvenAndOdd,
                s.PageNumberStart,s.PageNumberFormat,s.HeaderDistance,s.FooterDistance,s.Body,s.Start}),
            auxiliaries=snapshot.StoryRegions.Select(r=>new{r.Section,r.Kind,r.Variant,r.Status,r.AvailableRegion,r.Lines,r.Cells,r.PhysicalPages,
                selection=r.Selection is {} select?new{select.DeclaringSection,reference=m.Source(select.ReferenceId).Location,
                    relation=select.Reference.Relation,part=select.Reference.TargetId is {} target?m.Source(target).Location:null}:null,
                r.Diagnostics}),
            auxiliaryPages=snapshot.Pages.Select(p=>new{p.Id.PhysicalIndex,p.SectionPageNumber,
                stories=p.Geometry.RunningStories.Select(r=>new{r.Selection.Kind,r.Selection.Variant,r.Region,
                    lines=r.Lines.Select(l=>new{l.ParagraphId,l.Source,l.Baseline,l.Height,l.Text}),cells=r.Cells.Count})}),
            documentIssues=snapshot.Composition.Issues.Where(i=>i.BlocksRendering).Take(64).Select(i=>new{i.Code,i.Source,i.Message}).ToArray(),
            snapshot.Times,typography.Timings,drawMs,geometrySha256,m.CustomXmlResolutionMs,
            customXml=m.CustomXml.Select(d=>new{d.Relation,d.PartRelations,impact=d.Impact.ToString(),d.Reason,d.StoreId,
                target=d.RootId is {} targetId?m.Source(targetId).Location:null,
                properties=d.PropertiesId is {} propertiesId?m.Source(propertiesId).Location:null,
                consumer=d.ConsumerId is {} consumerId?m.Source(consumerId).Location:null,
                entries=d.Entries.Select(e=>m.Source(e).Location)}),
            styles=new{selection=snapshot.StyleDecision?.Selection.ToString(),snapshot.StyleDecision?.SelectedRoot,
                impact=snapshot.StyleDecision?.EffectsImpact?.ToString(),
                snapshot.StyleDecision?.ComparisonMs,m.StyleResolutionMs,m.StyleCompatibility,
                ordinary=Evidence(m.OrdinaryStyles),effects=Evidence(m.EffectsStyles),
                guideSamples=id==4?typography.Paragraphs.Where(p=>p.Fragments.Length>0)
                    .GroupBy(p=>StyleName(p.Id)).Select(group=>StyleSample(group.Key,group.First(),group.Count())).ToArray():null}};

        object Evidence(SemanticStyleCollection collection)=>new{collection.Relations,
            root=collection.RootId is {} rootId?m.Source(rootId).Location:null,
            defaults=collection.DefaultsId is {} defaultsId?m.Source(defaultsId).Location:null,
            definitions=collection.Definitions.Length};

        string StyleName(string paragraph)=>m.ChildId(paragraph,"pPr") is {} properties&&m.ChildId(properties,"pStyle") is {} style?
            m.Attribute(style,"val")??"":m.DefaultStyle("paragraph")?.DeclaredId??"";
        object StyleSample(string name,TextParagraph paragraph,int count)
        {
            var first=paragraph.Fragments[0];
            var format=paragraphs.GetValueOrDefault(paragraph.Id)?.Properties;
            return new{name,paragraphs=count,source=m.Source(paragraph.Id).Location,
                first.Style.Size,first.Style.Bold,first.Style.Color,first.RequestedFamily,first.ThemeSource,first.Face?.UsedFamily,
                first.Style.Origins,format?.Before,format?.After,format?.Line,format?.LineRule};
        }

        object? Decl(string? owner,string name)=>owner is not null&&m.ChildId(owner,name) is {} node?
            new{node,source=m.Source(node).Location,attributes=m.Attributes(node).ToDictionary()}:null;
        object? Nested(string? owner,string name)=>owner is not null&&m.ChildId(owner,name) is {} node?
            m.Children(node).Where(e=>!m.Name(e.Id).StartsWith('#')).Select(e=>new{name=m.Name(e.Id),source=m.Source(e.Id).Location,attributes=m.Attributes(e.Id).ToDictionary()}).ToArray():null;
    }
    static bool Below(SourceLocation value,SourceLocation parent)=>value.PartUri==parent.PartUri&&
        (value.Path==parent.Path||value.Path.StartsWith(parent.Path+"/",StringComparison.Ordinal));
    static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
