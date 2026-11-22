using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class TableReadingCases
{
    static string PathVariable(string name)=>Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException(name);
    static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    internal static void Corpus()
    {
        string input=PathVariable("LECTEUR_WORD_QA_CORPUS"),output=PathVariable("LECTEUR_WORD_QA_TABLE_OUTPUT");
        string fixture=PathVariable("LECTEUR_WORD_QA_TABLE_FIXTURE");
        var result=new List<object>();
        var copies=Directory.GetFiles(input,"*.docx").Order().ToArray();
        NativeCases.Check(copies.Length==11,"Onze copies contrôlées sont requises.");
        foreach(var path in copies.Append(fixture))
        {
            string hash=Hash(path);
            var inputDocument=DocumentInspector.InspectDocument(path);
            var readable=ReadableReader.Read(inputDocument);
            long start=Stopwatch.GetTimestamp();
            using var lease=PageComposer.Compose(inputDocument,prepareReadingTables:true);
            var snapshot=lease.Snapshot;
            double prepareMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var tables=snapshot.Semantic?.Tables.ToDictionary(t=>t.Id)??[];
            if(path==fixture)
            {
                var grid=snapshot.ReadingGrids.Values.Single();
                var table=snapshot.ReadingTables.Values.Single();
                NativeCases.Check(snapshot.Coverage==PageCoverage.Complete&&grid.Columns.SequenceEqual(new[]{50f,130f})&&
                    table.Cells.Count==3&&table.Cells[0].Width==180&&table.Cells[0].X==0&&
                    table.Cells[1].Height>=20&&table.Cells[1].Y==table.Cells[2].Y,"Géométrie du témoin à grille fixe.");
                NativeCases.Check(table.Cells.All(c=>c.Borders==new CellBorders()&&c.Fill is null),"Bordure ou fond inventé.");
            }
            var entries=tables.Select(pair=>
            {
                var (id,source)=pair;
                var location=snapshot.Semantic!.Source(id).Location;
                var grid=snapshot.ReadingGrids.GetValueOrDefault(id);
                var scene=snapshot.ReadingTables.GetValueOrDefault(id);
                var placed=snapshot.Pages.SelectMany(p=>p.Geometry.Cells).Where(c=>c.TableId==id&&!c.IsRepeated).ToArray();
                if(scene is not null)
                {
                    var nativeSources=scene.Cells.SelectMany(c=>c.MergedSourceIds.Prepend(c.CellId)).Order();
                    var sourceCells=source.Rows.SelectMany(r=>r.Cells).Select(c=>c.Id).Order();
                    NativeCases.Check(nativeSources.SequenceEqual(sourceCells),"Une cellule source a disparu du tableau continu.");
                    NativeCases.Check(scene.Lines.All(l=>l.Native is not null&&l.CellId is not null),"Mesures natives ou rattachement perdus.");
                    NativeCases.Check(scene.Lines.GroupBy(l=>(l.ParagraphId,l.Native!.Index)).All(g=>g.Count()==1),"Ligne source dupliquée.");
                    NativeCases.Check(scene.Cells.All(c=>c.Width>0&&c.Height>=0&&c.X+c.Width<=scene.Geometry.Width+.01&&c.Y+c.Height<=scene.Geometry.Height+.01),"Géométrie hors grille.");
                    foreach(var cell in placed)
                    {
                        var match=scene.Cells.Single(c=>c.CellId==cell.CellId);
                        NativeCases.Check(Math.Abs(cell.Width-match.Width)<.01&&Math.Abs(cell.Height-match.Height)<.01&&
                            cell.Borders==match.Borders&&cell.Fill==match.Fill,"Géométrie divergente entre page et flux continu.");
                    }
                }
                return new{id,location,rows=source.Rows.Length,cells=source.Rows.Sum(r=>r.Cells.Length),columns=grid?.Columns,
                    grid?.AutoFit,verified=grid?.WidthResult?.ContentVerified,native=scene is not null,
                    width=scene?.Geometry.Width,height=scene?.Geometry.Height,placedCells=placed.Length,lines=scene?.Lines.Count,
                    images=scene?.Lines.Sum(l=>l.Images.Count),markers=scene?.Lines.Count(l=>l.Native?.ListMarker is not null),
                    diagnostics=snapshot.Composition.Issues.Where(g=>g.BlocksRendering&&g.Source is {} s&&s.PartUri==location.PartUri&&
                        (s.Path==location.Path||s.Path.StartsWith(location.Path+"/",StringComparison.Ordinal))).Select(i=>i.Code).Distinct().ToArray()};
            }).ToArray();
            var exportGates=Enum.GetValues<ExportFormat>().Select(format=>
            {
                var preflight=DocumentExports.Preflight(inputDocument,format);
                if(tables.Count>0)
                {
                    NativeCases.Check(!preflight.Allowed&&preflight.Issues.Any(i=>i.Code=="EXPORT_TABLE_UNQUALIFIED"&&i.BlocksExport&&i.Source is not null),
                        "Le tableau est sorti du périmètre de refus d’export.");
                    string destination=Path.Combine(output,"refus-"+Guid.NewGuid().ToString("N"));
                    try{DocumentExports.Export(inputDocument,format,destination);throw new InvalidOperationException("Export hors profil accepté.");}
                    catch(InvalidDataException e) when(e.Message=="EXPORT_PREFLIGHT_BLOCKED"){}
                    NativeCases.Check(!Path.Exists(destination),"Sortie créée malgré le refus d’export.");
                }
                return new{format=format.ToString(),preflight.Allowed,codes=preflight.Issues.Where(i=>i.BlocksExport).Select(i=>i.Code).Distinct()};
            }).ToArray();
            NativeCases.Check(Hash(path)==hash,"Copie de corpus modifiée.");
            result.Add(new{file=Path.GetFileName(path),hash,prepareMs,coverage=snapshot.Coverage.ToString(),candidates=snapshot.Pages.Length,
                pageLines=snapshot.Pages.Sum(p=>p.Geometry.Lines.Count),paragraphs=readable.Presentation.BodyBlocks.Count,
                nativeTables=snapshot.ReadingTables.Count,tables=entries,exportGates});
        }
        File.WriteAllText(Path.Combine(output,"matrice-tableaux.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
    }

    internal static async Task Native(ReaderWindow window)
    {
        string output=PathVariable("LECTEUR_WORD_QA_TABLE_OUTPUT");
        window.Width=1450;window.Height=925;window.SetLanguage(false);window.SetTheme(2);
        NativeCases.Check(window.TryGetPlatformHandle()?.HandleDescriptor=="XID","Fenêtre X11 réelle requise.");
        string input=PathVariable("LECTEUR_WORD_QA_INPUT");
        long start=Stopwatch.GetTimestamp();
        await window.OpenDocumentAsync(input);
        double openMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        NativeCases.Check(window.IsSimplifiedReading,"L’Annexe doit rester consultable en lecture continue.");
        var tables=window.CurrentReading!.Presentation.BodyBlocks.Where(b=>b.Kind=="table").ToArray();
        NativeCases.Check(tables.Length==6,"Les six tableaux sont attendus dans l’Annexe.");
        var render=new List<object>();
        var list=window.GetVisualDescendants().OfType<ListBox>().Single(l=>l.Name=="SimplifiedReading");
        foreach(double zoom in new[]{1d,1.5})
        {
            window.SetZoom(zoom);await Task.Delay(150);
            for(int i=0;i<tables.Length;i++)
            {
                var block=tables[i];
                NativeCases.Check(window.NavigateToBlock(block.Id),"Navigation vers le tableau impossible.");
                await Task.Delay(200);
                list.UpdateLayout();
                var table=list.GetVisualDescendants().OfType<ScrollViewer>().Single(c=>c.Name=="ReadingTable"&&c.Tag as string==block.Id);
                NativeCases.Check(table.VerticalScrollBarVisibility==ScrollBarVisibility.Disabled&&table.ScrollBarMaximum.Y==0,
                    "Défilement vertical interne détecté.");
                bool native=table.GetVisualDescendants().Any(c=>c.Name=="ReadingTableScene");
                var cells=table.GetVisualDescendants().OfType<Border>().Where(c=>c.Name=="ReadingCell").ToArray();
                NativeCases.Check(native&&cells.Length>0&&cells.All(c=>c.BorderThickness==default),"Placement natif, cellules ou bordures incohérents.");
                var scene=window.ReadingTableSnapshot!.ReadingTables[block.Id];
                foreach(var model in scene.Cells)
                {
                    var bounds=cells.Single(c=>c.Tag as string==model.CellId).Bounds;
                    double scale=zoom*96/72;
                    NativeCases.Check(Math.Abs(bounds.Width-scale*model.Width)<1.01&&Math.Abs(bounds.Height-scale*model.Height)<1.01&&
                        Math.Abs(bounds.X-scale*model.X)<1.01&&Math.Abs(bounds.Y-scale*model.Y)<1.01,"Décalage entre placement et Avalonia.");
                }
                render.Add(new{i,zoom,native,visibleWidth=table.Viewport.Width,documentWidth=table.Extent.Width,
                    height=table.Extent.Height,horizontal=table.ScrollBarMaximum.X,cells=cells.Select(c=>new{id=c.Tag,bounds=c.Bounds.ToString()})});
                if(zoom==1.5&&table.ScrollBarMaximum.X>0)
                {
                    table.Offset=new Vector(table.ScrollBarMaximum.X,0);await Task.Delay(80);
                    NativeCases.Check(table.Offset.X>0&&table.Offset.Y==0,"Fin du tableau inaccessible.");
                    table.Offset=default;await Task.Delay(80);
                }
                if(i==2&&zoom==1)
                {
                    var paragraph=ReadablePresentation.Walk(block.Children).First(b=>b.Kind=="paragraph"&&b.PlainText.Contains("service"));
                    NativeCases.Check(window.NavigateToBlock(paragraph.Id)&&window.CurrentBlockId==paragraph.Id,
                        "Le passage trouvé doit rester navigable dans sa cellule source.");
                    await Task.Delay(150);
                }
                if(i is 0 or 2 or 3)
                    await Capture(window,Path.Combine(output,"captures",$"annexe-apres-tableau-{i+1}-{zoom*100:0}.png"));
            }
        }
        File.WriteAllText(Path.Combine(output,"fenetre-annexe.json"),JsonSerializer.Serialize(new{openMs,window.RenderScaling,render},new JsonSerializerOptions{WriteIndented=true}));
    }

    static async Task Capture(Window window,string path)
    {
        var info=new ProcessStartInfo("import"){UseShellExecute=false};
        info.ArgumentList.Add("-window");
        info.ArgumentList.Add(window.TryGetPlatformHandle()!.Handle.ToInt64().ToString());
        info.ArgumentList.Add(path);
        using var command=Process.Start(info)!;
        await command.WaitForExitAsync();
        NativeCases.Check(command.ExitCode==0&&File.Exists(path),"Capture de la vraie fenêtre indisponible.");
    }
}
