using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class PageCorpusCases
{
    static void Check(bool value,string message)=>LineCases.Check(value,message);
    static JsonElement[] Manifest(string key)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable(key)
            ??throw new InvalidOperationException("Choisir le manifeste privé pour "+key)));
        var rows=doc.RootElement.EnumerateArray().Select(e=>e.Clone()).OrderBy(x=>x.GetProperty("id").GetInt32()).ToArray();
        Check(rows.Select(x=>x.GetProperty("id").GetInt32()).SequenceEqual(Enumerable.Range(0,11)),"Onze copies contrôlées attendues.");
        return rows;
    }
    internal static void Run(string root)
    {
        var sources=Manifest("LECTEUR_WORD_QA_CORPUS_MANIFEST");
        var reference=Manifest("LECTEUR_WORD_QA_CORPUS_REFERENCE");
        var linesReference=Manifest("LECTEUR_WORD_QA_LINE_REFERENCE");
        var records=new List<object>();
        for(int id=0;id<sources.Length;id++)
        {
            string copy=sources[id].GetProperty("copy").GetString()!, hash=sources[id].GetProperty("sha256").GetString()!;
            Check(Sha(copy)==hash,"Copie non conforme avant recette.");
            var input=DocumentInspector.InspectDocument(copy);
            var manifest=ManifestJson.Serialize(input.Manifest);
            var reading=ReadableReader.Read(input);
            Check(reading.Available,"Lecture de secours indisponible pour "+id);
            var prior=Composer.Compose(input);
            Check(prior.Pages.Count==reference[id].GetProperty("A_pages").GetInt32()&&
                prior.Available==reference[id].GetProperty("A_paginated").GetBoolean(),"Régression du profil C# précédent.");
            double? legacyRasterMs=null;
            if(id==1)
            {
                long start=Stopwatch.GetTimestamp();
                byte[] png=Composer.PagePng(prior.Pages[0],1.5f);
                legacyRasterMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                File.WriteAllBytes(Path.Combine(root,"lettre-legacy.png"),png);
            }
            var result=Probe(input,root,id,linesReference[id].GetProperty("coverage"));
            records.Add(new{id,sha256=hash,previousPages=prior.Pages.Count,reading=reading.Available,
                result.coverage,result.performance,legacyRasterMs,memoryAfterDispose=GC.GetTotalMemory(true)});
            Check(Sha(copy)==hash&&manifest.AsSpan().SequenceEqual(ManifestJson.Serialize(input.Manifest)),"Copie ou manifeste modifié.");
        }
        File.WriteAllText(Path.Combine(root,"native-page-corpus.json"),JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (object coverage,object performance) Probe(InspectionResult input,string root,int id,JsonElement baseline)
    {
        long managedBase=GC.GetTotalMemory(true);
        var residentBefore=Memory();
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        using var lease=PageComposer.Compose(input);
        long allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        var result=lease.Snapshot;
        long managedRetained=GC.GetTotalMemory(true);
        var residentAfter=Memory();
        Check(result.SnapshotId==input.Manifest.SourceSha256&&result.Semantic is not null&&result.Typography is not null,
            "Instantané d’origine absent.");
        var semantic=result.Semantic!;
        var typo=result.Typography!;
        var fragments=typo.Paragraphs.SelectMany(p=>p.Fragments).ToArray();
        int missing=fragments.Count(f=>f.Measure is null);
        Check(missing==baseline.GetProperty("missing").GetInt32()-(id==5?5:0),"Fragment TEXT-001 manquant ou remplacé.");
        if(id==5)Check(fragments.Count(f=>f.LegacySymbol is {Family:"Symbol",Code:0xf02b or 0xf02d}&&f.Measure is not null)==5,
            "Les cinq opérateurs Symbol de Stage 2 doivent conserver leur source et être mesurés.");
        if(id==7)Check(fragments.Count(f=>f.Measure is null&&f.Text=="\uf0e8")==5,"Caractères privés du CDC.");
        if(id is 2 or 3)
            Check(semantic.Stories.Count(s=>s.Kind is "footnote" or "endnote")>0&&typo.Tokens.Any(t=>t.Control=="NoteCall")&&
                result.Gaps.Any(g=>g.Reason=="PAGE_STORY_PENDING"),"Les notes 004D doivent rester dans l’inventaire.");
        if(id==5)
            Check(result.Sections.Length==10&&semantic.Tables.Length==16&&result.Coverage!=PageCoverage.Complete&&
                typo.Tokens.Any(t=>t.Role==TextRole.StoredField),"Sections, champs et tableaux du dossier Stage 2.");
        if(id==4)Check(semantic.Tables.Length==33,"33 tableaux du Guide attendus.");
        if(result.Composition.Pages.Count>0)PageCases.VerifyPlaced(result);
        var placed=result.Composition.Pages.SelectMany(p=>p.Lines).Where(l=>!l.IsRepeated).ToArray();
        var composed=result.Lines?.Lines??[];
        var gapped=result.Gaps.Where(g=>g.ContextRange is not null).Select(g=>(g.Source.NodeId,g.ContextRange!.Value)).ToHashSet();
        var located=placed.Select(l=>(l.ParagraphId,l.Native!.ContextRange)).ToHashSet();
        Check(composed.All(l=>located.Contains((l.ParagraphId,l.ContextRange))||gapped.Contains((l.ParagraphId,l.ContextRange))),
            "Une ligne perdue entre composition et placement.");
        Check(result.Coverage==PageCoverage.Complete==result.Composition.Available,"Contrat de disponibilité incomplet.");
        VerifyMeasuredSource(typo,composed);
        double? rasterMs=null;
        if(result.Coverage==PageCoverage.Complete)
        {
            long t=Stopwatch.GetTimestamp();
            byte[] png=Composer.PagePng(result.Composition.Pages[0],1.5f);
            rasterMs=Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            File.WriteAllBytes(Path.Combine(root,$"native-{id:00}-page1.png"),png);
        }
        if(id==1&&result.Composition.Pages.Count>0)
        {
            long t=Stopwatch.GetTimestamp();
            byte[] png=Composer.PagePng(result.Composition.Pages[0],1.5f);
            rasterMs=Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            File.WriteAllBytes(Path.Combine(root,"lettre-native-candidate.png"),png);
        }
        if(id==4&&result.Composition.Pages.Count>0)
        {
            Check(result.Composition.Pages.All(p=>p.RunningStories.Count==2),"Histoires attendues dans chaque page candidate du Guide.");
            File.WriteAllBytes(Path.Combine(root,"guide-premiere-candidate.png"),Composer.PagePng(result.Composition.Pages[0],1.5f));
            File.WriteAllBytes(Path.Combine(root,"guide-derniere-candidate.png"),Composer.PagePng(result.Composition.Pages[^1],1.5f));
        }
        var coverage=new{status=result.Coverage.ToString(),sections=result.Sections.Length,
            sectionsWithCandidateLines=composed.Select(l=>l.Region.Id).Where(i=>i.StartsWith("section-",StringComparison.Ordinal)).Distinct().Count(),
            sectionProfiles=result.Sections.Select(s=>new{s.Semantic.Number,s.Start,s.Orientation,s.Columns,format=s.Format,
                diagnostics=s.Diagnostics.Select(i=>i.Code).ToArray()}).ToArray(),
            candidatePages=result.Composition.Pages.Count,qualifiedNativePages=result.Composition.Available?result.Composition.Pages.Count:0,
            placed=placed.Length,composed=composed.Length,unplaced=composed.Length-placed.Length,geometryUnknown=composed.Count(l=>l.Height is null),
            lineReference=baseline.GetProperty("lines").GetInt32(),unmeasuredReference=baseline.GetProperty("unknownGeometry").GetInt32(),
            textParagraphs=typo.Paragraphs.Length,paragraphsWithoutRegion=typo.Paragraphs.Length-composed.Select(l=>l.ParagraphId).Distinct().Count(),
            missing,stored=typo.Tokens.Count(t=>t.Role==TextRole.StoredField),notes=typo.Tokens.Count(t=>t.Control=="NoteCall"),
            tables=semantic.Tables.Length,tableGrids=result.RulerModel?.Tables.Count??0,
            uniqueCellWidths=composed.Where(l=>!l.Region.Id.StartsWith("section-",StringComparison.Ordinal)).Select(l=>l.Region.Width).Distinct().Count(),
            leaderLines=composed.Count(l=>l.Generated.Any(d=>d.Kind=="leader")),images=composed.Sum(l=>l.Boxes.Length),
            gaps=result.Gaps.GroupBy(g=>g.Reason).ToDictionary(g=>g.Key,g=>g.Count()),
            diagnostics=result.Composition.Issues.GroupBy(i=>i.Code).ToDictionary(g=>g.Key,g=>g.Count())};
        var performance=new{result.Times,rasterMs,allocatedBytes,managedBase,managedRetained,managedDelta=managedRetained-managedBase,
            residentBefore,residentAfter,text=typo.Timings,line=result.Lines?.Performance};
        Console.WriteLine($"OK pages {id:00} · {coverage.status} · {coverage.candidatePages} candidates · {coverage.placed}/{coverage.composed} lignes placées · {coverage.tableGrids}/{coverage.tables} grilles");
        return (coverage,performance);
    }

    static void VerifyMeasuredSource(TypographyDocument typography,IEnumerable<ComposedLine> source)
    {
        var byId=typography.Paragraphs.ToDictionary(p=>p.Id);
        foreach(var group in source.GroupBy(l=>l.ParagraphId))
        {
            var original=byId[group.Key];
            Check(string.Concat(group.SelectMany(l=>l.Parts).Select(p=>p.Text))==string.Concat(original.Fragments.Select(f=>f.Text))&&
                group.SelectMany(l=>l.Parts).Where(p=>p.Control is not null).Select(p=>p.Control!.Value).SequenceEqual(original.Controls),
                "Reconstruction des fragments après application de la largeur réelle.");
        }
    }

    internal static void Gui(string root)
    {
        var sources=Manifest("LECTEUR_WORD_QA_CORPUS_MANIFEST");
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
        var w=new ReaderWindow();w.Show();
        var rows=new List<object>();
        try
        {
            for(int id=0;id<sources.Length;id++)
            {
                var source=sources[id].GetProperty("copy").GetString()!;
                AboutCases.Complete(w.OpenDocumentAsync(source));
                Check(w.Inspection?.Manifest.SourceSha256==sources[id].GetProperty("sha256").GetString()&&w.CurrentComposition is not null,
                    "Le document attendu n’a pas été ouvert.");
                var composition=w.CurrentComposition!;
                int? page=null;
                if(composition.Available)
                {
                    w.GoToPage(composition.Pages.Count-1);
                    Check(w.CurrentPage==composition.Pages.Count,"Navigation de page.");
                    page=w.CurrentPage;
                }
                else Check(w.IsSimplifiedReading&&w.CurrentPage==0,"Repli ou nombre de pages fictif.");
                w.SetZoom(1.5);
                Check(w.Zoom==1.5,"Zoom 150 %.");
                w.GetVisualDescendants().OfType<Button>().First(b=>b.Name?.StartsWith("Action_search_",StringComparison.Ordinal)==true)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var query=w.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="SearchBox");
                var reading=w.CurrentReading!;
                string needle=ReadablePresentation.Walk(reading.Presentation.BodyBlocks)
                    .Where(b=>b.Kind is "paragraph" or "heading").SelectMany(b=>reading.Presentation.TextFor(b).Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(5))
                    .First(t=>t.Length>=4&&t.All(char.IsLetter));
                query.Text=needle;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Check(query.Text==needle&&query.IsVisible,"Commande de recherche indisponible.");
                var hit=w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.Content is string text&&
                    (text.Contains(needle,StringComparison.CurrentCultureIgnoreCase)||
                    ToolTip.GetTip(b) is string tip&&tip.Contains(needle,StringComparison.CurrentCultureIgnoreCase)));
                Check(hit is not null,"Le passage de recherche choisi dans le corps doit être affichable.");
                rows.Add(new{id,native=w.NativePageStatus,composition=composition.Profile,reading=true,page,zoom=w.Zoom,
                    search=true,resultVisible=hit is not null});
                query.Text="";
            }
        }
        finally{w.Close();}
        File.WriteAllText(Path.Combine(root,"native-page-gui.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    }

    static string Sha(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    static Dictionary<string,long> Memory()
    {
        var data=new Dictionary<string,long>();
        if(!File.Exists("/proc/self/smaps_rollup"))return data;
        foreach(string line in File.ReadLines("/proc/self/smaps_rollup"))
        {
            string[] columns=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(columns.Length>=2&&columns[0] is "Rss:" or "Pss:" or "Private_Clean:" or "Private_Dirty:")
                data[columns[0].TrimEnd(':')]=long.Parse(columns[1],CultureInfo.InvariantCulture)*1024;
        }
        return data;
    }
}
