using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class TypographyCorpusCases
{
    public static void Corpus(string root)
    {
        string path=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_MANIFEST")
            ??throw new InvalidOperationException("Choisir le manifeste privé des onze copies.");
        using var manifest=JsonDocument.Parse(File.ReadAllText(path));
        var entries=manifest.RootElement.EnumerateArray().ToArray();
        Check(entries.Select(x=>x.GetProperty("id").GetInt32()).Order().SequenceEqual(Enumerable.Range(0,11)),
            "Corpus de onze empreintes distinctes attendu.");
        var reports=new List<object>();
        foreach(var entry in entries)
        {
            int id=entry.GetProperty("id").GetInt32();
            string copy=entry.GetProperty("copy").GetString()!, expected=entry.GetProperty("sha256").GetString()!;
            Check(Hash(copy)==expected,"Copie hors référence.");
            var inspected=DocumentInspector.InspectDocument(copy);
            var before=ManifestJson.Serialize(inspected.Manifest);
            var semantic=TypographyCases.Model(inspected);
            long managedBase=GC.GetTotalMemory(true);
            var residentBefore=Memory();
            var run=Exercise(inspected,semantic,id);
            long afterDispose=GC.GetTotalMemory(true);
            var residentAfter=Memory();
            Check(expected==semantic.SnapshotId&&Hash(copy)==expected&&before.AsSpan().SequenceEqual(ManifestJson.Serialize(inspected.Manifest)),
                "Modification de source ou d’inventaire.");
            reports.Add(new{id,run.details,run.retained,managedBase,afterDispose,residentBefore,run.residentDuring,residentAfter});
            Console.WriteLine($"OK texte {id:00} · {run.fragments} fragments · {run.unmeasured} sans mesure");
        }
        File.WriteAllText(Path.Combine(root,"text-corpus.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions {WriteIndented=true}));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (object details,long retained,Dictionary<string,long> residentDuring,int fragments,int unmeasured)
        Exercise(InspectionResult inspected,SemanticDocument semantic,int id)
    {
        long start=Stopwatch.GetTimestamp();
        using var session=new TypographySession(inspected,semantic);
        double initializationMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var cold=Measure(session);
        var warm=id is 1 or 5 ? Measure(session) : default;
        var doc=cold.Document ?? throw new InvalidOperationException("Mesure à froid absente.");
        long retained=GC.GetTotalMemory(true);
        var residentDuring=Memory();
        TypographyCases.Verify(semantic,doc);
        var fragments=doc.Paragraphs.SelectMany(p=>p.Fragments).ToArray();
        if(warm.Document is not null)
        {
            var matches=warm.Document.Paragraphs.SelectMany(p=>p.Fragments).ToArray();
            Check(matches.Length==fragments.Length&&matches.Zip(fragments).All(pair=>pair.First.Text==pair.Second.Text&&
                pair.First.Measure?.Metrics==pair.Second.Measure?.Metrics&&
                (pair.First.Measure is null || pair.First.Measure.Glyphs.SequenceEqual(pair.Second.Measure!.Glyphs))),
                "Caches à chaud et mesures à froid divergent.");
        }
        if(id is 2 or 3)
            Check(doc.Tokens.Any(t=>t.Original?.EnumerateRunes().Any(r=>r.Value>0xffff)==true&&t.StoryId is {} story&&
                semantic.Stories.Any(s=>s.RootId==story&&s.Kind is "footnote" or "endnote")),"Unicode supplémentaire des notes absent.");
        if(id==7)
            Check(fragments.Any(f=>f.Text.Contains('\uf0e8')&&f.Sources.Length>0),"Caractère privé du CDC perdu.");
        if(id==5)
            Check(doc.Tokens.Any(t=>t.Role==TextRole.StoredField)&&doc.Paragraphs.Any(p=>p.Controls.Any(c=>c.Kind=="Tab")),
                "Le résultat stocké ou les tabulations du sommaire ont disparu.");
        if(id==4)
            Check(doc.Paragraphs.Any(p=>Ancestors(semantic,p.Id).Any(n=>semantic.Element(n).Kind==SemanticKind.Cell)&&p.Fragments.Length>0),
                "Aucune mesure dans les cellules du guide.");

        var styles=fragments.GroupBy(f=>new{f.Style.Bold,f.Style.Italic,f.Style.Tracking,f.Style.Color,f.Style.Underline,
            f.PointSize,f.Script,f.Language,f.FontSlot,f.BidiLevel,f.RequestedFamily,theme=f.Request?.Theme??false,
            requestOrigin=f.Request?.Source,face=f.Face?.UsedFamily,sha256=f.Face?.Sha256,index=f.Face?.FaceIndex,
            status=f.Face?.Status,reason=f.Face?.Reason}).Select(g=>new{style=g.Key,fragments=g.Count(),
            sampleSources=g.First().Style.Origins.ToDictionary(x=>x.Key,x=>x.Value.Location)}).ToArray();
        var missing=fragments.SelectMany(f=>f.Measure is null ? [f.Text] :
                f.Measure.Clusters.Where(c=>c.MissingGlyph).Select(c=>f.Text[c.Range.Start..c.Range.EndExclusive]))
            .SelectMany(t=>t.EnumerateRunes()).Select(r=>r.Value).Distinct().Order().ToArray();
        var legacy=fragments.Where(f=>f.Measure is not null&&f.Face is not null&&Composer.SupportedText(f.Text)).Take(20)
            .Select(f=>new{source=f.Sources.First().Source.Location,hash=f.Face!.Sha256,face=f.Face.UsedFamily,
                pt=f.PointSize,legacyPt=session.LegacyAdvance(f),shapedPt=(double)f.Measure!.Metrics.Width/TypographySession.EmuPerPoint}).ToArray();
        return (new{initializationMs,doc.IcuVersion,doc.UnicodeVersion,paragraphs=doc.Paragraphs.Length,
                fragments=fragments.Length,measured=fragments.Count(f=>f.Measure is not null),
                tokens=doc.Tokens.GroupBy(t=>t.Role.ToString()).ToDictionary(g=>g.Key,g=>g.Count()),
                glyphs=fragments.Sum(f=>f.Measure?.Glyphs.Length??0),missingScalars=missing,
                diagnostics=doc.Diagnostics.GroupBy(d=>d.Code).ToDictionary(g=>g.Key,g=>g.Count()),
                cold=new{cold.Ms,cold.Allocated,doc.Timings},warm=warm.Document is null?null:new{warm.Ms,warm.Allocated,warm.Document.Timings},
                styles,legacy},retained,residentDuring,fragments.Length,fragments.Count(f=>f.Measure is null));
    }

    static IEnumerable<string> Ancestors(SemanticDocument semantic,string id)
    {
        for(string? p=semantic.ParentId(id);p is not null;p=semantic.ParentId(p))yield return p;
    }

    static (TypographyDocument? Document,double Ms,long Allocated) Measure(TypographySession session)
    {
        long before=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
        var doc=session.Measure();
        return (doc,Stopwatch.GetElapsedTime(start).TotalMilliseconds,GC.GetAllocatedBytesForCurrentThread()-before);
    }

    static Dictionary<string,long> Memory()
    {
        var values=new Dictionary<string,long>();
        if(!File.Exists("/proc/self/smaps_rollup"))return values;
        foreach(string line in File.ReadLines("/proc/self/smaps_rollup"))
        {
            var columns=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(columns.Length>=2&&columns[0] is "Rss:" or "Pss:" or "Private_Clean:" or "Private_Dirty:")
                values[columns[0].TrimEnd(':')]=long.Parse(columns[1],CultureInfo.InvariantCulture)*1024;
        }
        return values;
    }

    static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
