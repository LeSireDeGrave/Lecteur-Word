using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class LineCorpusCases
{
    internal static void Run(string root)
    {
        string path=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_MANIFEST")
            ??throw new InvalidOperationException("Choisir le manifeste des onze copies contrôlées.");
        using var manifest=JsonDocument.Parse(File.ReadAllText(path));
        var inputs=manifest.RootElement.EnumerateArray().ToArray();
        LineCases.Check(inputs.Select(x=>x.GetProperty("id").GetInt32()).Order().SequenceEqual(Enumerable.Range(0,11)),
            "Onze copies distinctes attendues.");
        var results=new List<object>();
        foreach(var input in inputs)
        {
            int id=input.GetProperty("id").GetInt32();
            string copy=input.GetProperty("copy").GetString()!, expected=input.GetProperty("sha256").GetString()!;
            LineCases.Check(Hash(copy)==expected,"Empreinte de la copie avant inspection.");
            var inspection=DocumentInspector.InspectDocument(copy);
            var frozen=ManifestJson.Serialize(inspection.Manifest);
            var model=TypographyCases.Model(inspection);
            var result=Exercise(inspection,model,id);
            long after=GC.GetTotalMemory(true);
            LineCases.Check(model.SnapshotId==expected&&Hash(copy)==expected&&
                frozen.AsSpan().SequenceEqual(ManifestJson.Serialize(inspection.Manifest)),"Copie ou inventaire modifié.");
            results.Add(new{id,result.coverage,result.samples,managedAfterDocument=after});
            Console.WriteLine($"OK lignes {id:00} · {result.lines} lignes · {result.withGeometry} avec géométrie · {result.missing} fragments sans mesure");
            if(id==1)
            {
                var legacy=Composer.Compose(inspection);
                LineCases.Check(legacy.Available&&legacy.Pages.Count==1,"La page C# de la lettre a régressé.");
                File.WriteAllBytes(Path.Combine(root,"lettre-raster.png"),Composer.PagePng(legacy.Pages[0],1.5f));
            }
        }
        File.WriteAllText(Path.Combine(root,"line-corpus.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (object coverage,List<object> samples,int lines,int withGeometry,int missing)
        Exercise(InspectionResult inspection,SemanticDocument model,int id)
    {
        var cells= new Dictionary<string,string>();
        foreach(var p in model.Elements.Where(e=>e.Kind==SemanticKind.Paragraph))
            if(Ancestor(model,p.Id,SemanticKind.Cell) is {} cell)cells.Add(p.Id,cell);
        // Fixed qualification regions. Grid sizing and page margins are the responsibility of PAGE-001B.
        LineRegion Region(LineRequest r)=>cells.TryGetValue(r.ParagraphId,out string? cell)
            ?new(cell,0,144*LineCases.Pt):new(r.StoryId,0,475*LineCases.Pt);
        int runs=id is 1 or 5?3:1;
        var samples=new List<object>();
        object? coverage=null;
        int lines=0,withGeometry=0,missing=0;
        for(int i=0;i<runs;i++)
        {
            var sample=Sample(inspection,model,cells,id,Region,i==0,runs>1);
            if(i==0)
            {
                coverage=sample.coverage;
                lines=sample.lines;
                withGeometry=sample.withGeometry;
                missing=sample.missing;
            }
            samples.Add(sample.report);
        }
        return (coverage!,samples,lines,withGeometry,missing);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (object? coverage,object report,int lines,int withGeometry,int missing)
        Sample(InspectionResult inspection,SemanticDocument model,Dictionary<string,string> cells,int id,
            Func<LineRequest,LineRegion> region,bool verify,bool repeat)
    {
        long start=Stopwatch.GetTimestamp();
        using var typography=new TypographySession(inspection,model);
        double init=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        start=Stopwatch.GetTimestamp();
        var doc=typography.Measure();
        double textMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long textAllocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
        long afterTypography=GC.GetTotalMemory(true);
        var pssBefore=Resident();
        using var composer=new LineComposer(model,doc,typography);
        var (cold,coldMs,coldAllocated)=Measure(composer,region);
        long afterCold=GC.GetTotalMemory(true);
        var pssCold=Resident();
        object? coverage=null;
        if(verify)
        {
            LineCases.Verify(model,cold);
            coverage=VerifyCorpus(model,cold,cells,id);
        }
        object? warmReport=repeat?Warm(composer,region,LineCases.Fingerprint(cold)):null;
        var report=new {initializationMs=init,textMs,textAllocated,typography=doc.Timings,managedAfterTypography=afterTypography,
            residentAfterTypography=pssBefore,cold=new {ms=coldMs,allocated=coldAllocated,performance=cold.Performance,
                managedRetained=afterCold,managedDelta=afterCold-afterTypography,resident=pssCold},warm=warmReport};
        return (coverage,report,cold.Lines.Length,cold.Lines.Count(l=>l.Occupied is not null),
            doc.Paragraphs.Sum(p=>p.Fragments.Count(f=>f.Measure is null)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static object Warm(LineComposer composer,Func<LineRequest,LineRegion> region,int expected)
    {
        var (warm,ms,allocated)=Measure(composer,region);
        long managed=GC.GetTotalMemory(true);
        var resident=Resident();
        LineCases.Check(expected==LineCases.Fingerprint(warm),"Résultat différent avec le cache réutilisé.");
        return new{ms,allocated,performance=warm.Performance,managedRetainedWithBothSnapshots=managed,residentWithBothSnapshots=resident};
    }

    static (ComposedLines Lines,double Ms,long Allocated) Measure(LineComposer composer,Func<LineRequest,LineRegion> region)
    {
        long before=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
        var result=composer.Compose(region);
        return (result,Stopwatch.GetElapsedTime(start).TotalMilliseconds,GC.GetAllocatedBytesForCurrentThread()-before);
    }

    static object VerifyCorpus(SemanticDocument model,ComposedLines composed,Dictionary<string,string> cells,int id)
    {
        var lines=composed.Lines;
        var doc=composed.Typography;
        var missing=doc.Paragraphs.SelectMany(p=>p.Fragments).Where(f=>f.Measure is null).ToArray();
        var retained=lines.SelectMany(l=>l.Parts).Where(p=>p.Fragment?.MeasurementIssue is not null).ToArray();
        int expected=id switch{2 or 3=>12,7=>5,_=>0};
        LineCases.Check(missing.Length==expected&&retained.Length==expected&&retained.Zip(missing).All(pair=>
            pair.First.Fragment!.Text==pair.Second.Text&&pair.First.Fragment.Sources.SequenceEqual(pair.Second.Sources)&&
            pair.First.Limitation==pair.Second.MeasurementIssue),
            $"Couverture TEXT-001 modifiée pour le document {id:00}.");
        int cellTables=lines.Where(l=>cells.ContainsKey(l.ParagraphId)).Select(l=>Ancestor(model,l.ParagraphId,SemanticKind.Table))
            .Where(t=>t is not null).Distinct().Count();
        int cellWrapped=lines.Where(l=>cells.ContainsKey(l.ParagraphId)&&l.Cut is LineCutKind.Allowed or LineCutKind.SoftHyphen).Count();
        int storedField=lines.SelectMany(l=>l.Parts).SelectMany(p=>p.Fragment?.Sources??[]).Count(s=>s.Role==TextRole.StoredField);
        int leaders=lines.Sum(l=>l.Generated.Count(g=>g.Kind=="leader"));
        int tocLeaders=lines.Count(l=>l.Generated.Any(g=>g.Kind=="leader")&&
            l.Parts.SelectMany(p=>p.Fragment?.Sources??[]).Any(s=>s.Role==TextRole.StoredField));
        int noteScalars=lines.Where(l=>model.Stories.Any(s=>s.RootId==l.StoryId&&s.Kind is "footnote" or "endnote")).SelectMany(l=>l.Parts)
            .SelectMany(p=>p.Text?.EnumerateRunes().ToArray()??[]).Count(r=>r.Value>0xffff);
        if(id==4)
            LineCases.Check(cellTables==33&&cellWrapped>0,"33 tableaux du guide, avec retours en cellule, attendus.");
        if(id==5)
            LineCases.Check(cellTables==16&&leaders>0&&storedField>0&&tocLeaders>0&&
                doc.Paragraphs.SelectMany(p=>p.Fragments).Count(f=>f.LegacySymbol is {Family:"Symbol",Code:0xf02b or 0xf02d}&&f.Measure is not null)==5,
                "16 tableaux, opérateurs Symbol, points de suite et champs du sommaire attendus.");
        if(id is 2 or 3)LineCases.Check(noteScalars>0,"Caractères hors BMP des notes absents.");
        if(id==7)
            LineCases.Check(retained.All(p=>p.Text=="\uf0e8")&&lines.Where(l=>l.Parts.Any(p=>p.Text=="\uf0e8"))
                .All(l=>l.Coverage==LineCoverage.Partial&&l.Occupied is null),"Cinq occurrences CDC privées non préservées.");
        return new {paragraphs=doc.Paragraphs.Length,lines=lines.Length,complete=lines.Count(l=>l.Coverage==LineCoverage.Complete),
            partial=lines.Count(l=>l.Coverage==LineCoverage.Partial),unknownGeometry=lines.Count(l=>l.Occupied is null),
            fragments=doc.Paragraphs.Sum(p=>p.Fragments.Length),missing=missing.Length,
            missingReasons=missing.GroupBy(f=>f.MeasurementIssue).ToDictionary(g=>g.Key??"unknown",g=>g.Count()),
            cuts=lines.GroupBy(l=>l.Cut.ToString()).ToDictionary(g=>g.Key,g=>g.Count()),
            alignments=lines.GroupBy(l=>l.Properties.Alignment.ToString()).ToDictionary(g=>g.Key,g=>g.Count()),
            tabs=lines.SelectMany(l=>l.Tabs).GroupBy(t=>t.Stop.Kind.ToString()).ToDictionary(g=>g.Key,g=>g.Count()),
            controls=lines.SelectMany(l=>l.Parts).Where(p=>p.Control is not null).GroupBy(p=>p.Control!.Value.Kind).ToDictionary(g=>g.Key,g=>g.Count()),
            diagnostics=lines.SelectMany(l=>l.Diagnostics).GroupBy(d=>d.Code).ToDictionary(g=>g.Key,g=>g.Count()),
            scripts=doc.Paragraphs.SelectMany(p=>p.Fragments).GroupBy(f=>f.Script).ToDictionary(g=>g.Key,g=>g.Count()),
            cellTables,cellWrapped,storedField,leaders,tocLeaders,noteScalars};
    }

    static string? Ancestor(SemanticDocument model,string id,SemanticKind kind)
    {
        for(string? p=model.ParentId(id);p is not null;p=model.ParentId(p))
            if(model.Element(p).Kind==kind)return p;
        return null;
    }

    static Dictionary<string,long> Resident()
    {
        var result=new Dictionary<string,long>();
        if(!File.Exists("/proc/self/smaps_rollup"))return result;
        foreach(string line in File.ReadLines("/proc/self/smaps_rollup"))
        {
            var columns=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(columns.Length>=2&&columns[0] is "Rss:" or "Pss:" or "Private_Clean:" or "Private_Dirty:")
                result[columns[0].TrimEnd(':')]=long.Parse(columns[1],CultureInfo.InvariantCulture)*1024;
        }
        return result;
    }

    static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
