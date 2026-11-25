using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static class RenderQualityCases
{
    static void Check(bool value,string message)=>NativeCases.Check(value,message);
    static string Run(string text)=>"<w:r><w:t xml:space=\"preserve\">"+text+"</w:t></w:r>";
    static string Marker(string value)=>"<w:r><w:fldChar w:fldCharType=\""+value+"\"/></w:r>";
    static string Code(string value)=>"<w:r><w:instrText xml:space=\"preserve\">"+value+"</w:instrText></w:r>";
    static string Field(string instruction,string value)=>Marker("begin")+Code(instruction)+Marker("separate")+Run(value)+Marker("end");

    internal static void Model(string root)
    {
        string toc=Marker("begin")+Code(" TOC \\f O \\h ")+Marker("separate")+
            "<w:hyperlink w:anchor=\"entry\">"+Run("Titre témoin")+Field(" PAGEREF entry \\h ","")+"</w:hyperlink>"+Marker("end");
        string heading=Marker("begin")+Code(" TC &quot; ")+Field(" SEQ Test ","1")+Field(" REF entry ","Titre témoin")+
            Code(" &quot; \\f O ")+Marker("end")+
            "<w:bookmarkStart w:id=\"1\" w:name=\"entry\"/>"+Run("Titre témoin")+"<w:bookmarkEnd w:id=\"1\"/>";
        string simple="<w:fldSimple w:instr=\"REF entry\">"+Run("Titre témoin")+"</w:fldSimple>";
        string table="<w:tbl><w:tblPr><w:tblW w:w=\"4800\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/></w:tblPr>"+
            "<w:tblGrid>"+string.Concat(Enumerable.Repeat("<w:gridCol w:w=\"1600\"/>",3))+"</w:tblGrid><w:tr>"+
            string.Concat(new[]{"auto","000000","F8E45C"}.Select((fill,i)=>"<w:tc><w:tcPr><w:shd w:val=\"clear\" w:fill=\""+fill+"\"/></w:tcPr><w:p>"+
                "<w:r><w:rPr><w:color w:val=\""+(i==1?"FFFFFF":"000000")+"\"/></w:rPr><w:t>"+(char)('A'+i)+"</w:t></w:r></w:p></w:tc>"))+
            "</w:tr></w:tbl>";
        string section="<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>";
        string body="<w:p>"+toc+"</w:p><w:p><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr>"+heading+"</w:p><w:p>"+simple+"</w:p>"+table+section;
        string docx=Fixtures.Write(root,"rendu.docx",Fixtures.Parts(body));
        var input=DocumentInspector.InspectDocument(docx);
        var reading=ReadableReader.Read(input);
        var p=reading.Presentation;
        var paragraphs=p.BodyBlocks.Take(3).Select(p.TextFor).ToArray();
        using var lease=PageComposer.Compose(input,prepareReadingTables:true);
        var cells=lease.Snapshot.ReadingTables.Values.Single().Cells;
        var fills=cells.Select(c=>c.Fill).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new{paragraphs,outline=reading.Outline.Select(x=>x.Label),fills,fields=p.Fields.Values}));
        Check(paragraphs.SequenceEqual(new[]{"Titre témoin","Titre témoin","Titre témoin"}),
            "Les caches imbriqués dans TC polluent le titre effectif.");
        Check(reading.Outline.Single().Label=="Titre témoin","Le sommaire latéral doit reprendre le titre affiché.");
        Check(p.Fields.Values.Single(f=>f.Name=="PAGEREF") is {Matched:true,HasStoredValue:false}&&
            p.Fields.Values.Single(f=>f.Name=="TC") is {Matched:true,HasStoredValue:false}&&
            p.Fields.Values.Single(f=>f.Name=="SEQ").StoredValue=="1","Provenance ou disponibilité du résultat incorrecte.");
        var titleId=p.BodyBlocks[1].Id;
        Check(lease.Snapshot.Typography!.Tokens.Where(t=>t.ParagraphId==titleId).Count(t=>t.Role==TextRole.Source)==1&&
            !lease.Snapshot.Typography.Tokens.Any(t=>t.ParagraphId==titleId&&t.Role==TextRole.StoredField),"Les opérandes de TC ne doivent pas alimenter le dessin natif.");
        Check(fills.SequenceEqual(new string?[]{null,"000000","F8E45C"}),"Le fond automatique est transparent et les couleurs explicites sont conservées.");
        var page=lease.Snapshot.ReadingTables.Values.Single();
        using var pixels=SKBitmap.Decode(Composer.PagePng(page));
        Check(pixels.GetPixel(70,3)==SKColors.White&&pixels.GetPixel(150,3)==SKColors.Black&&pixels.GetPixel(230,3)==SKColor.Parse("#F8E45C"),
            "Le raster ne correspond pas aux trois fonds.");
    }

    internal static async Task Native(ReaderWindow window)
    {
        string root=Environment.GetEnvironmentVariable("LECTEUR_WORD_RENDER_QA")??throw new InvalidOperationException("Répertoire de qualification requis.");
        string output=Environment.GetEnvironmentVariable("LECTEUR_WORD_RENDER_OUTPUT")??throw new InvalidOperationException("Sortie de qualification requise.");
        bool observe=Environment.GetEnvironmentVariable("LECTEUR_WORD_RENDER_OBSERVE")=="1";
        string phase=observe?"avant":"après";
        window.Width=1240;window.Height=850;window.SetTheme(1);
        Check(window.TryGetPlatformHandle()?.HandleDescriptor=="XID","Fenêtre X11 requise.");
        var records=new List<object>();
        foreach(string document in new[]{"manuscrit","maths","annexe"})
        {
            var time=Stopwatch.StartNew();
            await window.OpenDocumentAsync(Path.Combine(root,"copies",document+".docx"));
            long opening=time.ElapsedMilliseconds;
            var reading=window.CurrentReading??throw new InvalidOperationException("Lecture absente.");
            if(document=="maths"&&!observe)
                Check(window.CurrentReadingView==ReadingView.SinglePage&&window.CurrentComposition is
                    {Profile:PageComposer.Profile,Available:true,Pages.Count:3},"Les trois feuilles du bilan CE1 doivent s’ouvrir en Page simple.");
            else
                Check(window.CurrentReadingView==ReadingView.Unavailable,"Une pagination complète non qualifiée est annoncée.");
            window.SetReadingView(ReadingView.Continuous);
            Check(window.IsSimplifiedReading&&window.CurrentPage==0,"Une pagination complète non qualifiée est annoncée.");
            if(!observe)
                foreach(var format in Enum.GetValues<ExportFormat>())
                    Check(!window.ExportAvailable(format),$"{document} : export {format} hors profil proposé.");
            var body=reading.Presentation.BodyBlocks;
            var tables=body.Where(b=>b.Kind=="table").ToArray();
            var targets=new List<(string Label,ReadableBlock Block)>();
            if(document=="manuscrit")
            {
                var toc=body.First(b=>ReadablePresentation.Walk([b]).Any(l=>l.LinkTarget?.StartsWith("#",StringComparison.Ordinal)==true));
                targets.Add(("sommaire",toc));
                string heading=reading.Outline.First().BlockId;
                targets.Add(("titre",body.First(b=>b.Id==heading)));
                if(!observe)
                {
                    Check(reading.Outline.Count==10&&reading.Outline.Select(o=>o.Label).Distinct().Count()==10,"Dix titres de chapitre uniques attendus.");
                    Check(reading.Presentation.Fields.Values.Count(f=>f.Name=="PAGEREF"&&f.Matched&&!f.HasStoredValue)==10,
                        "Dix renvois de page vides doivent rester diagnostiqués.");
                }
            }
            for(int i=0;i<tables.Length;i++)targets.Add(($"tableau-{i+1}",tables[i]));
            foreach(double zoom in observe?new[]{1d}:new[]{1d,1.5d})
            {
                window.SetZoom(zoom);
                await Task.Delay(200);
                foreach(var (label,block) in targets)
                {
                    Check(window.NavigateToBlock(block.Id),"Navigation vers le témoin impossible.");
                    await Task.Delay(190);
                    var list=window.GetVisualDescendants().OfType<ListBox>().Single(l=>l.Name=="SimplifiedReading");
                    list.UpdateLayout();
                    string value=reading.Presentation.TextFor(block);
                    var text=list.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t=>t.Tag as string==block.Id);
                    var table=list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v=>v.Name=="ReadingTable"&&v.Tag as string==block.Id);
                    var scene=window.ReadingTableSnapshot?.ReadingTables.GetValueOrDefault(block.Id);
                    var grid=window.ReadingTableSnapshot?.ReadingGrids.GetValueOrDefault(block.Id);
                    var rendered=text?.Inlines is {} inlines?string.Concat(inlines.Select(InlineText)):null;
                    int fields=text?.Inlines is {} runs?runs.Sum(InlineWidgets):0;
                    if(!observe&&label is "sommaire" or "titre")
                    {
                        Check(text is not null&&fields==0&&rendered==value,"Le corps du titre ou du sommaire contient des éléments techniques.");
                        if(label=="titre")Check(value.Trim()==reading.Outline.First().Label&&value.Trim().Length>0,"Titre et panneau de navigation divergent.");
                        if(label=="sommaire")
                        {
                            Check(text!.GetVisualParent()?.GetVisualDescendants().Any(c=>c is Control {Name:"ReadingTabLeader"})==true,
                                "Le taquet terminal déclaré dans le style TOC n’est pas représenté.");
                            var link=text!.ContextMenu!.Items.OfType<MenuItem>().Last();
                            link.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                            Check(window.CurrentBlockId==reading.Outline.First().BlockId,"Le lien du sommaire ne rejoint pas le titre.");
                            Check(window.NavigateToBlock(block.Id),"Retour au sommaire impossible.");
                            await Task.Delay(190);
                        }
                    }
                    var fills=(scene?.Cells.Select(c=>c.Fill)??grid?.Cells.Select(c=>c.Fill)??[]).GroupBy(c=>c)
                        .Select(g=>new{fill=g.Key,count=g.Count()}).ToArray();
                    if(!observe&&block.Kind=="table")
                    {
                        Check(table is not null&&table.VerticalScrollBarVisibility==Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled&&
                            table.Offset.Y==0,$"{document}/{label}/{zoom} : tableau absent ou défilement vertical interne ({table?.ScrollBarMaximum.Y}).");
                        Check(fills.All(f=>f.fill!="000000"),"Fond noir automatique dans une copie contrôlée.");
                        if(document=="manuscrit"&&label=="tableau-2")
                            Check(fills.Any(f=>f.fill=="F8E45C")&&fills.Any(f=>f.fill=="F5F5F5"),"Couleurs explicites du manuscrit perdues.");
                        if(document=="annexe")Check(scene is not null,"Régression du placement natif de l’Annexe.");
                    }
                    var snapshot=window.ReadingTableSnapshot;
                    var blockers=snapshot?.Composition.Issues.Where(i=>i.BlocksRendering&&i.Source is {} at&&at.PartUri==block.Source.PartUri&&
                        (at.Path==block.Source.Path||at.Path.StartsWith(block.Source.Path+"/",StringComparison.Ordinal)))
                        .Select(i=>i.Code).Distinct().ToArray();
                    records.Add(new{phase,document,label,zoom,opening,value,rendered,fields,width=text?.Bounds.Width,
                        height=text?.Bounds.Height,lines=text?.TextLayout.TextLines.Count,tableWidth=table?.Extent.Width,tableHeight=table?.Extent.Height,
                        layout=scene is null?"grille":"natif",fills,candidates=snapshot?.Pages.Length,blockers});
                    if(document=="manuscrit"||document=="maths"&&label=="tableau-4"||document=="annexe"&&label is "tableau-1" or "tableau-3")
                        await Capture(window,Path.Combine(output,$"{document}-{label}-{zoom*100:0}.png"));
                }
            }
        }
        File.WriteAllText(Path.Combine(output,"fenetres.json"),JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));
    }

    static string InlineText(Inline i)=>i switch{Run r=>r.Text??"",Span s=>string.Concat(s.Inlines.Select(InlineText)),LineBreak=>"\n",_=>""};
    static int InlineWidgets(Inline i)=>i switch{InlineUIContainer=>1,Span s=>s.Inlines.Sum(InlineWidgets),_=>0};
    static async Task Capture(Window window,string path)
    {
        var args=new ProcessStartInfo("import"){UseShellExecute=false};
        foreach(var arg in new[]{"-window",window.TryGetPlatformHandle()!.Handle.ToInt64().ToString(),path})args.ArgumentList.Add(arg);
        using var process=Process.Start(args)!;
        await process.WaitForExitAsync();
        Check(process.ExitCode==0&&File.Exists(path),"Capture X11 échouée.");
    }
}
