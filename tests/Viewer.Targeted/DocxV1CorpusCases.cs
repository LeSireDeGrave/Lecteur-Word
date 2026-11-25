using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class DocxV1CorpusCases
{
    sealed record Reference(string Name,string Copy,string Sha256,string Coverage,int Pages,int BodyLines,string BodyHash,
        int? Sections,int? Passes,string[]? BodyFieldValues,int? PageFieldsInFooters,int? Notes,
        int? DiagramShapes,int? DiagramLabels,string? Pagination,int? HistoryGlyphsMissing);

    internal static void Run(string root)
    {
        string manifest=Environment.GetEnvironmentVariable("LECTEUR_WORD_DOCX_V1_CORPUS")??
            throw new InvalidOperationException("Sélectionner le manifeste local du corpus DOCX V1.");
        var entries=JsonSerializer.Deserialize<Reference[]>(File.ReadAllBytes(manifest),new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ??throw new InvalidOperationException("Manifeste du corpus absent.");
        LineCases.Check(entries.Length>=15&&entries.Select(r=>r.Sha256).Distinct().Count()==entries.Length,
            "Quinze références distinctes au minimum.");
        bool measureProgress=Environment.GetEnvironmentVariable("LECTEUR_WORD_DOCX_V1_MEASURE_PROGRESS")=="1";
        var results=new List<object>();
        var regressions=new List<string>();
        using var process=Process.GetCurrentProcess();
        foreach(var reference in entries)
        {
            var clock=Stopwatch.StartNew();
            var cpu=process.TotalProcessorTime;
            long allocations=GC.GetTotalAllocatedBytes(true);
            var inspection=DocumentInspector.InspectDocument(reference.Copy);
            double inspectMs=clock.Elapsed.TotalMilliseconds;
            LineCases.Check(inspection.Manifest.SourceSha256==reference.Sha256,"Empreinte de corpus modifiée. "+reference.Name);
            ReadableReader.Read(inspection,default,out var model);
            double readableMs=clock.Elapsed.TotalMilliseconds-inspectMs;
            using var lease=PageComposer.Compose(inspection,default,true,model);
            double composeMs=clock.Elapsed.TotalMilliseconds-inspectMs-readableMs;
            var s=lease.Snapshot;
            var pages=s.Composition.Pages;
            double? renderMs=null;
            int firstPagePngBytes=0;
            if(pages.Count>0)
            {
                long start=Stopwatch.GetTimestamp();
                firstPagePngBytes=Composer.PagePng(pages[0]).Length;
                renderMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            double cpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds;
            long managedAllocatedBytes=GC.GetTotalAllocatedBytes(true)-allocations;
            process.Refresh();
            int lines=pages.Sum(p=>p.Lines.Count(l=>!l.IsRepeated));
            string bodyHash=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                pages.Select(p=>p.Lines.Select(l=>new{l.Source,l.ParagraphId,l.Text,l.NativeTop,l.Baseline,l.X,l.Height,l.Section})))));
            var originalCoverage=Enum.Parse<PageCoverage>(reference.Coverage);
            bool same=s.Coverage==originalCoverage&&pages.Count==reference.Pages&&lines==reference.BodyLines&&bodyHash==reference.BodyHash;
            bool gain=originalCoverage!=PageCoverage.Complete&&s.Coverage<=originalCoverage&&pages.Count>=reference.Pages&&
                lines>=reference.BodyLines&&(s.Coverage<originalCoverage||pages.Count>reference.Pages||lines>reference.BodyLines);
            if(!(same||measureProgress&&gain))
                regressions.Add($"{reference.Name} · {s.Coverage} · {pages.Count} feuilles · {lines} lignes · {bodyHash}");
            if(reference.Sections is {} sections)LineCases.Check(s.Sections.Length==sections,reference.Name+" · sections");
            if(reference.Passes is {} passes)LineCases.Check(s.FieldStability=="Stable"&&s.FieldPasses.Length==passes,reference.Name+" · passes");
            if(reference.BodyFieldValues is {} values)
                LineCases.Check(s.FieldPasses[^1].Fields.Select(f=>f.Text).SequenceEqual(values)&&
                    s.FieldPasses[^1].Fields.All(f=>f.Diagnostic is null&&f.BookmarkId is {} id&&
                        s.FieldBookmarks.TryGetValue(id,out var target)&&target.Page==f.PhysicalPage),
                    reference.Name+" · renvois placés");
            int footers=pages.SelectMany(p=>p.RunningStories.Where(r=>r.Selection.Kind=="footer")
                .SelectMany(r=>r.Lines).SelectMany(l=>l.Native?.Generated??[])).Count(g=>g.Kind=="pageField");
            int notes=pages.Sum(p=>p.Footnotes.Count(n=>n.Call is not null));
            if(reference.PageFieldsInFooters is {} expectedFooters)LineCases.Check(footers==expectedFooters,reference.Name+" · pieds");
            if(reference.Notes is {} expectedNotes)LineCases.Check(notes==expectedNotes,reference.Name+" · notes");
            if(reference.Pagination is {} pagination)LineCases.Check(s.Rendering.Pagination.ToString()==pagination,reference.Name+" · pagination");
            if(reference.HistoryGlyphsMissing is {} missing)LineCases.Check(s.Rendering.MissingGlyphs==missing,reference.Name+" · glyphes manquants");
            if(s.Coverage==PageCoverage.Complete)
                LineCases.Check(s.Gaps.IsEmpty&&firstPagePngBytes>0,reference.Name+" · raster natif");
            var displayLines=pages.SelectMany(p=>p.DisplayLines).ToArray();
            var diagrams=displayLines.SelectMany(l=>l.Images).Select(i=>i.Image.Diagram).OfType<DiagramDrawing>().ToArray();
            int diagramShapes=diagrams.Sum(d=>d.Shapes.Length);
            int diagramLabels=diagrams.Sum(d=>d.Shapes.Count(shape=>!shape.Text.IsEmpty));
            if(reference.DiagramShapes is {} shapes)LineCases.Check(diagramShapes==shapes,reference.Name+" · formes SmartArt peintes");
            if(reference.DiagramLabels is {} labels)LineCases.Check(diagramLabels==labels,reference.Name+" · libellés SmartArt peints");
            var generated=displayLines.SelectMany(l=>l.Native?.Generated??[]).Where(g=>g.Kind=="pageField")
                .GroupBy(g=>g.Cause.NodeId).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.Ordinal);
            var fieldCounts=s.Semantic?.Fields.Where(f=>generated.ContainsKey(f.Id)).GroupBy(f=>f.Command)
                .ToDictionary(g=>g.Key,g=>g.Sum(f=>generated[f.Id]));
            var shapedFragments=s.Typography?.Paragraphs.SelectMany(p=>p.Fragments.Concat(
                p.Controls.Select(c=>c.Symbol).OfType<TextFragment>())).ToArray()??[];
            var declaredSymbols=shapedFragments.Where(f=>f.LegacySymbol is not null).ToArray();
            var paintedSymbols=displayLines.SelectMany(l=>l.Native?.Parts??[])
                .Select(p=>p.Fragment??p.Control?.Symbol).OfType<TextFragment>().Count(f=>f.LegacySymbol is not null);
            results.Add(new{reference.Name,reference.Sha256,Coverage=s.Coverage.ToString(),Pages=pages.Count,BodyLines=lines,
                Pagination=s.Rendering.Pagination.ToString(),s.Rendering.ContentComplete,s.Rendering.MissingGlyphs,
                QualifiedPages=s.Coverage==PageCoverage.Complete?pages.Count:0,
                CandidatePages=s.Coverage==PageCoverage.Partial?pages.Count:0,
                BodyHash=bodyHash,Footers=footers,Notes=notes,Passes=s.FieldPasses.Length,CalculatedFieldPlacements=fieldCounts,
                Bookmarks=s.FieldBookmarks,
                Symbols=new{Declared=declaredSymbols.Length,Resolved=declaredSymbols.Count(f=>f.Measure is not null),
                    Placed=paintedSymbols,Markers=s.ListMarkers.Values.Count(m=>m.Decoration?.Fragment.LegacySymbol is not null)},
                Fonts=shapedFragments.Select(f=>new{f.RequestedFamily,f.Face?.UsedFamily,f.Face?.Status,f.Face?.Sha256,
                    f.Face?.FaceIndex,f.Face?.Weight,f.Face?.Slant}).Distinct()
                    .OrderBy(f=>f.RequestedFamily,StringComparer.Ordinal).ThenBy(f=>f.UsedFamily,StringComparer.Ordinal),
                Placed=new{InlineRaster=displayLines.SelectMany(l=>l.Images).Count(i=>i.Image is {VmlVector:null,Diagram:null}),
                    InlineSmartArtVectors=diagrams.Length,DiagramShapes=diagramShapes,DiagramLabels=diagramLabels,
                    InlineVmlVectors=displayLines.SelectMany(l=>l.Images).Count(i=>i.Image.VmlVector is not null),
                    PassiveMetafiles=displayLines.SelectMany(l=>l.Images).Count(i=>i.Image.Metafile is not null),
                    FloatingPictures=pages.Sum(p=>p.FloatingPictures.Count),FloatingTextFrames=pages.Sum(p=>p.FloatingTextFrames.Count),
                    TableCells=pages.Sum(p=>p.DisplayCells.Count()),RunningStories=pages.Sum(p=>p.RunningStories.Count),
                    FootnoteFragments=notes,FootnoteSeparators=pages.Sum(p=>p.Footnotes.Count(n=>n.Call is null))},
                SmartArt=s.RulerModel?.SmartArt.Select(d=>new{d.Source,Shapes=d.Shapes.Count,
                    TextShapes=d.Shapes.Count(shape=>shape.TextLayout is not null),
                    d.Data,d.Layout,d.Styles,d.Colors,d.Drawing}),
                Media=inspection.Manifest.Media.Select(m=>new{m.PartUri,m.ContentType,m.Sha256,m.Width,m.Height,m.Coverage}),
                InspectedStories=s.Semantic?.Stories.GroupBy(story=>story.Kind).ToDictionary(g=>g.Key,g=>g.Count()),
                Performance=new{inspectMs,readableMs,composeMs,renderMs,cpuMs,managedAllocatedBytes,
                    ResidentBytes=process.WorkingSet64,ProcessPeakResidentBytes=process.PeakWorkingSet64,firstPagePngBytes,s.Times},
                Gaps=s.Gaps,Issues=s.Composition.Issues.Where(i=>i.BlocksRendering)});
            Console.WriteLine($"{(same?"OK":measureProgress&&gain?"GAIN":"ÉCART")} {reference.Name} · {s.Coverage} · {pages.Count} feuilles · {lines} lignes");
        }
        string report=Path.Combine(root,"corpus-docx-v1.json");
        File.WriteAllText(report,JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(report);
        LineCases.Check(regressions.Count==0,string.Join(Environment.NewLine,regressions));
    }
}
