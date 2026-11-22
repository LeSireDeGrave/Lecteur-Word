using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class SemanticCorpusCases
{
    public static void Run(string root)
    {
        string manifestPath = Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_MANIFEST")
            ?? throw new InvalidOperationException("Manifeste privé des copies figées requis.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var inputs = manifest.RootElement.EnumerateArray().ToArray();
        Check(inputs.Length == 11 && inputs.Select(x => x.GetProperty("id").GetInt32()).Order().SequenceEqual(Enumerable.Range(0, 11)),
            "Onze copies distinctes requises.");
        var rows = new List<object>();
        foreach (var input in inputs)
        {
            int id = input.GetProperty("id").GetInt32();
            string path = input.GetProperty("copy").GetString()!;
            string hash = input.GetProperty("sha256").GetString()!;
            Check(Sha(path) == hash, "Copie figée modifiée.");
            var inspection = DocumentInspector.InspectDocument(path);
            Check(inspection.Manifest.InspectionComplete && inspection.Manifest.SourceSha256 == hash, "Inspection de la copie refusée.");
            byte[] before = ManifestJson.Serialize(inspection.Manifest);
            var samples = new List<object>();
            SemanticResult? last = null;
            for (int i = 0; i < (id is 1 or 5 ? 3 : 1); i++)
            {
                last = null;
                GC.Collect();
                var memoryBefore = Memory();
                long initial = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                last = SemanticReader.Build(inspection);
                double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - initial;
                var memoryAfter = Memory();
                samples.Add(new { milliseconds, allocated, memoryBefore, memoryAfter });
            }
            var model = last?.Document ?? throw new InvalidOperationException($"Modèle indisponible pour {id}.");
            VerifyInventory(inspection.Manifest, model);
            Check(before.SequenceEqual(ManifestJson.Serialize(inspection.Manifest)) && Sha(path) == hash,
                $"Le modèle a modifié la source ou son inventaire pour {id}.");
            var counts = model.Elements.GroupBy(e => e.Coverage).ToDictionary(g => g.Key.ToString(), g => g.Count());
            var references = model.References.GroupBy(r => r.Kind + "/" + r.Status).ToDictionary(g => g.Key, g => g.Count());
            if (id == 5)
                Check(model.Sections.Length == 10 && model.Fields.Count(f => f.Command == "PAGEREF") == 19 &&
                    model.Fields.Any(f => f.Command == "TOC" && !f.StoredResultIds.IsEmpty) &&
                    model.Elements.Any(e => e.Kind == SemanticKind.FloatingDrawing), "Structure Stage 2 perdue.");
            if (id == 4) Check(model.Tables.Length == 33, "Tables du guide perdues.");
            if (id is 2 or 3 or 10)
            {
                int expected = id == 10 ? 12 : 6;
                Check(model.References.Count(r => r.Kind is "footnote" or "endnote" && r.Status == ReferenceStatus.Resolved) == expected,
                    "Appels des recettes non rattachés.");
                if (id != 10)
                    Check(model.Elements.Any(e => e.Kind == SemanticKind.Text &&
                        model.RawText(e.Id)!.EnumerateRunes().Any(r => r.Value > 0xFFFF)), "Texte hors BMP absent.");
            }
            rows.Add(new { id, model.SnapshotId, nodes = model.Elements.Length, counts, stories = model.Stories.Length,
                sections = model.Sections.Length, tables = model.Tables.Length, fields = model.Fields.Length,
                alternatives = model.Alternatives.Length, references, issues = last!.Issues.GroupBy(i => i.Code).ToDictionary(g => g.Key, g => g.Count()), samples });
        }
        File.WriteAllText(Path.Combine(root, "semantic-corpus.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Onze modèles vérifiés contre leurs inventaires, sources intactes.");
    }

    internal static void VerifyInventory(InspectionManifest manifest, SemanticDocument model)
    {
        Check(model.Elements.Length == manifest.Nodes.Count && model.SnapshotId == manifest.SourceSha256, "Inventaire incomplet.");
        var byId = manifest.Nodes.ToDictionary(n => n.Id);
        for (int i = 0; i < manifest.Nodes.Count; i++)
        {
            var source = manifest.Nodes[i];
            var node = model.Elements[i];
            Check(node.Id == source.Id && node.Order == i && model.Source(node.Id).Location == source.Source &&
                model.ParentId(node.Id) == source.ParentId && model.Name(node.Id) == source.Name &&
                model.RawText(node.Id) == source.Text && model.Revision(node.Id) == source.Revision &&
                model.Attributes(node.Id).SequenceEqual(source.Attributes), "Ordre, source ou valeurs altérés.");
            if (source.Kind == "textValue" && source.Text is {} text)
                Check(model.TextSource(node.Id).Span == new Utf16Span(0, text.Length), "Étendue du texte inexacte.");
        }
        foreach (var link in model.References)
        {
            var source = byId[link.SourceId];
            if (link.Relation is {} relation)
                Check(relation.SourcePart == source.Source.PartUri && manifest.Relationships.Contains(relation), "Relation hors de sa portée.");
            if (link.TargetId is {} target && link.Relation?.ResolvedPart is {} part)
                Check(byId[target].Source.PartUri == part, "Cible hors de la partie déclarée.");
            if (link.Status == ReferenceStatus.External)
                Check(link.TargetId is null && link.Media is null, "Cible externe exposée comme ressource interne.");
        }
        foreach (var table in model.Tables)
            foreach (var row in table.Rows)
                foreach (var cell in row.Cells)
                {
                    Check(model.Element(cell.Id).Kind == SemanticKind.Cell, "Cellule synthétisée.");
                    if (cell.VerticalOriginId is {} vertical)
                        Check(model.Element(vertical).Order <= model.Element(cell.Id).Order, "Origine de fusion en aval.");
                }
        Check(model.BodyBlocks.Select(id => model.Element(id).Order).SequenceEqual(model.BodyBlocks.Select(id => model.Element(id).Order).Order()),
            "Ordre du corps altéré.");
        Check(model.Sections[^1].EndBlockExclusive == model.BodyBlocks.Length, "Sections discontinues.");
        for (int i = 0; i < model.Sections.Length; i++)
            Check(model.Sections[i].StartBlock == (i == 0 ? 0 : model.Sections[i - 1].EndBlockExclusive), "Rupture de section inventée.");
        foreach (var field in model.Fields)
        {
            Check(field.StoredResultIds.All(id => model.Element(id).StoryId == field.StoryId &&
                model.Element(id).Coverage != SemanticCoverage.InactiveAlternative), "Résultat de champ hors de son histoire active.");
            Check(field.InstructionIds.All(id => model.Element(id).Kind == SemanticKind.FieldInstruction &&
                model.Element(id).StoryId == field.StoryId), "Instruction mélangée au résultat.");
        }
    }

    static object Memory()
    {
        var keys = new HashSet<string> { "Rss:", "Pss:", "Private_Clean:", "Private_Dirty:" };
        var values = new Dictionary<string, long>();
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/smaps_rollup"))
        {
            foreach (string line in File.ReadLines("/proc/self/smaps_rollup"))
            {
                string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length >= 2 && keys.Contains(columns[0]))
                    values[columns[0]] = long.Parse(columns[1], CultureInfo.InvariantCulture) * 1024;
            }
        }
        return new { rss = values.GetValueOrDefault("Rss:"), pss = values.GetValueOrDefault("Pss:"),
            privateBytes = values.GetValueOrDefault("Private_Clean:") + values.GetValueOrDefault("Private_Dirty:") };
    }

    static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
