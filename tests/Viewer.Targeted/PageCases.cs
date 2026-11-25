using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    static void Check(bool condition,string detail)=>LineCases.Check(condition,detail);
    static string Run(string text)=>"<w:r><w:t xml:space=\"preserve\">"+text+"</w:t></w:r>";
    static string P(string text,string props="")=>Paragraph(Run(text),props);
    static string Paragraph(string runs,string props="")=>"<w:p>"+(props.Length>0?"<w:pPr>"+props+"</w:pPr>":"")+runs+"</w:p>";
    static string Section(int width=3200,int height=3600,string start="nextPage",string orientation="portrait",int margins=240)=>
        $"""<w:sectPr><w:type w:val="{start}"/><w:pgSz w:w="{width}" w:h="{height}" w:orient="{orientation}"/><w:pgMar w:top="{margins}" w:right="{margins}" w:bottom="{margins}" w:left="{margins}" w:header="120" w:footer="140"/></w:sectPr>""";
    static string Write(string root,string name,string body)=>Fixtures.Write(root,name+".docx",Fixtures.Parts(body));
    static PageCompositionLease Compose(string path)=>PageComposer.Compose(DocumentInspector.InspectDocument(path));
    static void Complete(PageCompositionSnapshot s,int? count=null) =>
        Check(s.Coverage==PageCoverage.Complete&&s.Composition.Available&&(count is null||count==s.Composition.Pages.Count),
            $"Composition {s.Coverage}, pages {s.Composition.Pages.Count}. "+
            string.Join(" | ",s.Composition.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code+" "+i.Source?.Path).Take(8)));
    static PageLine Line(PageCompositionSnapshot snapshot,string text)=>snapshot.Composition.Pages.SelectMany(p=>p.Lines).Single(l=>l.Text==text);
    static bool Close(float p,float q)=>Math.Abs(p-q)<.02f;

    internal static void Geometry(string root)
    {
        string path=Write(root,"page-sections",P("Café alphabet",Section())+
            P("Été en continu",Section(start:"continuous"))+
            P("Paysage אבג 12","<w:bidi/>")+Section(4800,3200,orientation:"landscape"));
        using var lease=Compose(path);
        var s=lease.Snapshot;
        Complete(s,2);
        Check(s.Sections.Length==3&&s.Composition.Pages[0].Geometry is {Width:160,Height:180,Left:12,Top:12,Right:12,Bottom:12}&&
            s.Composition.Pages[0].Lines.Select(l=>l.Section).SequenceEqual(new[]{1,2})&&
            s.Composition.Pages[1].Geometry is {Width:240,Height:160}&&s.Sections[2].Orientation=="landscape","Formats successifs.");
        Check(s.Sections[0] is {Body.Left:152400,Body.Width:1727200,HeaderDistance:76200,FooterDistance:88900}&&
            s.Sections[0].Source is not null,"Provenance et unités des marges.");
        Check(s.Pages[0].Id==new CalculatedPageId(s.SnapshotId,1)&&s.Pages[0].Sections.SequenceEqual(new[]{1,2})&&
            s.Pages[1].Origin.PhysicalWidth==4800L*635,"Identité physique et sections de la page.");
        var bidi=Line(s,"Paysage אבג 12");
        var clusters=PageCoordinates.Clusters(bidi).ToArray();
        Check(!clusters.Select(c=>c.ContextRange.Start).SequenceEqual(clusters.Select(c=>c.ContextRange.Start).Order())&&
            clusters.All(c=>c.Sources.All(src=>s.Semantic!.Source(src.NodeId,src.Span)==src)),"Ordre visuel, rectangles et plages UTF-16.");
        Check(clusters.All(c=>c.Rect.Height>0&&c.Rect.Width>0),"Clusters dessinables sans rectangle.");
        using var again=Compose(path);
        long rasterStart=Stopwatch.GetTimestamp();
        var first=Composer.PagePng(s.Composition.Pages[1],1.5f);
        double rasterMs=Stopwatch.GetElapsedTime(rasterStart).TotalMilliseconds;
        var second=Composer.PagePng(again.Snapshot.Composition.Pages[1],1.5f);
        Check(first.AsSpan().SequenceEqual(second),"Raster natif non déterministe.");
        File.WriteAllBytes(Path.Combine(root,"paysage-bidi.png"),first);
        File.WriteAllBytes(Path.Combine(root,"portrait-continu.png"),Composer.PagePng(s.Composition.Pages[0],1.5f));
        File.WriteAllText(Path.Combine(root,"native-raster-performance.json"),JsonSerializer.Serialize(
            new{s.Times,rasterMs,dpi=108,pages=s.Composition.Pages.Count},new JsonSerializerOptions{WriteIndented=true}));
        VerifyPlaced(s);

        using var bad=Compose(Write(root,"page-bad-region",P("Source conservée")+Section(3200,3600,margins:2000)));
        Check(bad.Snapshot.Coverage==PageCoverage.Fallback&&bad.Snapshot.Composition.Pages.Count==0&&
            bad.Snapshot.Gaps.Length>0&&bad.Snapshot.Lines!.Typography.Tokens.Any(t=>t.Original=="Source conservée"),"Région impossible.");
        using var odd=Compose(Write(root,"page-parity",P("Première",Section())+P("Deuxième")+Section(start:"oddPage")));
        Complete(odd.Snapshot,3);
        Check(odd.Snapshot.Pages[1].Geometry.IntercalarySectionBreak==odd.Snapshot.Sections[1].Source&&
            odd.Snapshot.Pages[2].Geometry.Lines.Single().Text=="Deuxième","Le saut impair crée un intercalaire sourcé.");
        using var different=Compose(Write(root,"page-incompatible",P("Première",Section())+P("Deuxième")+Section(4800,3200,"continuous","landscape")));
        Check(different.Snapshot.Coverage==PageCoverage.Partial&&different.Snapshot.Composition.Pages.Count==1&&
            different.Snapshot.Sections[1].Diagnostics.Any(i=>i.Code=="PAGE_CONTINUOUS_INCOMPATIBLE"),"Section continue incompatible.");
        using var inherited=Compose(Write(root,"page-continuous-inherited",P("Première",Section())+
            P("Deuxième","""<w:sectPr><w:type w:val="continuous"/></w:sectPr>""")+P("Troisième")+Section()));
        Complete(inherited.Snapshot,2);
        Check(inherited.Snapshot.Sections[1].Body==inherited.Snapshot.Sections[2].Body&&inherited.Snapshot.Sections[1].HeaderDistance==76200,
            "Dimensions et repères manquants d’une section continue.");
    }

    internal static void Constraints(string root)
    {
        const string line="""<w:spacing w:lineRule="exact" w:line="400"/>""";
        string forced="<w:r><w:br w:type=\"page\"/></w:r>";
        string lines=string.Join("<w:r><w:br/></w:r>",Enumerable.Range(1,4).Select(i=>Run("Ligne "+i)));
        var witness=Write(root,"page-constraints",
            P("Bloc avant", """<w:spacing w:after="2000"/>"""+Section())+
            P("Insécable un","<w:keepNext/>"+line)+
            P("Insécable deux","<w:keepNext/>"+line)+Paragraph(lines,"<w:keepLines/>"+line)+
            Paragraph(Run("Saut avant")+forced+Run("Saut après"),"<w:keepLines/>"+line)+
            P("Nouveau",line+"<w:pageBreakBefore/>")+Section(start:"continuous"));
        using var lease=Compose(witness);
        var s=lease.Snapshot;
        Complete(s);
        var pages=s.Composition.Pages;
        Check(pages.Count==4&&pages[1].Lines.Select(l=>l.Text).Take(6).SequenceEqual(new[]
            {"Insécable un","Insécable deux","Ligne 1","Ligne 2","Ligne 3","Ligne 4"}), "Chaîne keepNext/keepLines.");
        Check(pages[1].Lines.Last().HasExplicitPageBreak&&pages[2].Lines[0].Text=="Saut après"&&pages[3].Lines[0].Text=="Nouveau",
            "Le saut explicite prime et pageBreakBefore déclenche une page.");
        Check(Close(pages[1].Lines[1].Baseline-pages[1].Lines[0].Baseline,20),"Interligne exact.");
        VerifyPlaced(s);

        string six=string.Join("<w:r><w:br/></w:r>",Enumerable.Range(1,6).Select(i=>Run("Rang "+i)));
        using var widow=Compose(Write(root,"page-widow",P("Début","""<w:spacing w:after="500"/>""")+Paragraph(six,line)+Section(3200,2880)));
        Complete(widow.Snapshot,2);
        var chunks=widow.Snapshot.Composition.Pages.Select(p=>p.Lines.Count(l=>l.Text.StartsWith("Rang",StringComparison.Ordinal))).ToArray();
        Check(chunks.SequenceEqual(new[]{4,2}),"Veuve ou orpheline isolée.");
        using var large=Compose(Write(root,"page-oversize",Paragraph(six,"<w:keepLines/>"+line)+Section(3200,2880,margins:400)));
        Check(large.Snapshot.Coverage!=PageCoverage.Complete&&large.Snapshot.Composition.Issues.Any(i=>i.Code=="LAYOUT_KEEP_LINES_TOO_LARGE"),
            "Paragraphe insécable impossible non diagnostiqué.");
        using var unknown=Compose(Write(root,"page-unknown",P("Début sûr")+P("Début "+char.ConvertFromUtf32(0x10fffd)+" fin")+P("Après")+Section()));
        Check(unknown.Snapshot.Coverage==PageCoverage.Partial&&unknown.Snapshot.Composition.Pages.Count==1&&
            unknown.Snapshot.Composition.Pages[0].Lines.Count==1&&unknown.Snapshot.Lines!.Lines.Any(l=>l.Occupied is null)&&
            unknown.Snapshot.Gaps.Any(g=>g.Reason=="PAGE_AFTER_UNPLACED"),"Hauteur inconnue ou reprise avec position inventée.");

        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        bool interrupted=false;
        try{using var ignored=PageComposer.Compose(DocumentInspector.InspectDocument(witness),cancelled.Token);}
        catch(OperationCanceledException){interrupted=true;}
        Check(interrupted,"Annulation de la composition.");
        using var longDoc=Compose(Write(root,"page-budget",string.Concat(Enumerable.Range(0,502).Select(i=>P("Page",i==0?"":"<w:pageBreakBefore/>")))+Section()));
        Check(longDoc.Snapshot.Coverage!=PageCoverage.Complete&&longDoc.Snapshot.Composition.Issues.Any(i=>i.Code=="LAYOUT_PAGE_LIMIT"),
            "Budget de pages.");
    }

    internal static void Structures(string root)
    {
        static string Cell(string text,string extra="",int width=1360)=>$"""<w:tc><w:tcPr><w:tcW w:w="{width}" w:type="dxa"/>{extra}</w:tcPr>{P(text)}</w:tc>""";
        static string Row(string cells,string extra="")=>"<w:tr><w:trPr>"+extra+"</w:trPr>"+cells+"</w:tr>";
        string table="""
            <w:tbl><w:tblPr><w:tblW w:w="2720" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr>
            <w:tblGrid><w:gridCol w:w="1360"/><w:gridCol w:w="1360"/></w:tblGrid>
            """+Row(Cell("Entête", """<w:gridSpan w:val="2"/>""",2720),"<w:tblHeader/>")+
            Row(Cell("M", """<w:vMerge w:val="restart"/>""")+Cell("Rang 1"),"""<w:trHeight w:val="1000" w:hRule="atLeast"/>""")+
            Row(Cell("", "<w:vMerge/>")+Cell("Rang 2"),"""<w:trHeight w:val="1000" w:hRule="atLeast"/>""")+
            Row(Cell("A")+Cell("Rang 3"),"""<w:trHeight w:val="1000" w:hRule="atLeast"/>""")+"</w:tbl>";
        using var lease=Compose(Write(root,"page-table",table+Section()));
        Complete(lease.Snapshot,2);
        var pages=lease.Snapshot.Composition.Pages;
        Check(pages[1].Cells.Any(c=>c.IsRepeated)&&pages[0].Cells.Any(c=>c.MergedSourceIds.Count==1&&c.Height>=100),
            "En-tête répété ou groupe fusionné.");
        var row=pages[0].Lines.Single(l=>l.Text=="Rang 1");
        Check(row.CellId is not null&&Close(PageComposer.Points(row.Native!.Region.Width),57.2f),"Largeur de la cellule et marges.");
        File.WriteAllBytes(Path.Combine(root,"table-repetition.png"),Composer.PagePng(pages[1],1.5f));
        string autoTable=System.Text.RegularExpressions.Regex.Replace(
            table.Replace("w:type=\"fixed\"","w:type=\"autofit\"",StringComparison.Ordinal),
            """<w:tcW w:w="[0-9]+" w:type="dxa"/>""","");
        using var auto=Compose(Write(root,"page-autofit",autoTable+Section()));
        Complete(auto.Snapshot,2);
        Check(auto.Snapshot.RulerModel!.Tables.Single().AutoFit&&
            auto.Snapshot.Composition.Pages[1].Cells.Any(c=>c.IsRepeated)&&
            auto.Snapshot.RulerModel.Tables.Single().Columns.Distinct().Count()==2,"Grille AutoFit issue des intrinsèques HarfBuzz.");

        foreach(var format in new[]{SKEncodedImageFormat.Png,SKEncodedImageFormat.Jpeg})
        {
            string extension=format==SKEncodedImageFormat.Png?"png":"jpg";
            using var bitmap=new SKBitmap(24,12);
            bitmap.Erase(SKColors.OrangeRed);
            using var image=SKImage.FromBitmap(bitmap);using var encoded=image.Encode(format,95);
            const string drawing="""
                <w:drawing xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                    xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                    xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
                    <wp:inline><wp:extent cx="457200" cy="228600"/><wp:docPr id="8" name="Image"/>
                    <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                    <pic:pic><pic:blipFill><a:blip r:embed="pic"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                    <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="457200" cy="228600"/></a:xfrm><a:prstGeom prst="rect"/></pic:spPr></pic:pic>
                    </a:graphicData></a:graphic></wp:inline>
                </w:drawing>
                """;
            var parts=Fixtures.Parts(Paragraph(Run("Avant")+"<w:r>"+drawing+"</w:r>"+Run("Après"))+Section(),
                relationships:Fixtures.Relationship("pic","image","media/image."+extension),binary:new(){["word/media/image."+extension]=encoded.ToArray()});
            if(extension=="jpg")
                parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(parts["[Content_Types].xml"])
                    .Replace("</Types>","<Default Extension=\"jpg\" ContentType=\"image/jpeg\"/></Types>",StringComparison.Ordinal));
            string input=Fixtures.Write(root,extension+".docx",parts);
            using var issue=Compose(input);
            Complete(issue.Snapshot,1);
            var page=issue.Snapshot.Composition.Pages[0];
            var l=page.Lines.Single();
            var box=l.Images.Single();
            byte[] raster=Composer.PagePng(page);
            using var pixels=SKBitmap.Decode(raster);
            Check(box.Image.Width==36&&box.Image.Height==18&&l.Native!.Boxes.Single().Source.NodeId==box.RunId&&
                pixels.GetPixel((int)box.X+5,(int)box.Y+5).Red>180,"Boîte inline ou dessin "+extension);
            File.WriteAllBytes(Path.Combine(root,"page-image-"+extension+".png"),raster);
        }
    }

    internal static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
        var w=new ReaderWindow();
        w.Show();
        try
        {
            var source=Write(root,"page-native-gui",P("Début du document",Section())+P("Résultat recherché à la page suivante")+Section());
            AboutCases.Complete(w.OpenDocumentAsync(source));
            Check(w.CurrentComposition is {Profile:PageComposer.Profile,Available:true}&&w.CurrentPage==1&&w.NativePageStatus=="Complete","Activation contrôlée Avalonia.");
            w.GoToPage(1);w.SetZoom(1.5);
            Check(w.CurrentPage==2&&w.Zoom==1.5&&w.GetVisualDescendants().OfType<Image>().All(i=>i.Source is not null||i.Name!="CalculatedPage"),
                "Navigation et zoom.");
            w.GoToPage(0);
            w.GetVisualDescendants().OfType<Button>().First(b=>b.Name?.StartsWith("Action_search_",StringComparison.Ordinal)==true)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            w.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="SearchBox").Text="recherché";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var hit=w.GetVisualDescendants().OfType<Button>().Single(b=>b.Content is string s&&s.Contains("recherché",StringComparison.Ordinal));
            hit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(w.CurrentPage==2&&w.SelectedNativeSources.Count>0,"Navigation et provenance de recherche.");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using(var capture=new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)w.ClientSize.Width,(int)w.ClientSize.Height),new Vector(96,96)))
            {
                capture.Render(w);
                capture.Save(Path.Combine(root,"avalonia-pages.png"),PngBitmapEncoderOptions.Default);
            }
            var unknown=Write(root,"page-native-gui-fallback",P("Lecture "+char.ConvertFromUtf32(0x10fffd))+Section());
            AboutCases.Complete(w.OpenDocumentAsync(unknown));
            Check(w.CurrentReadingView==ReadingView.Unavailable&&w.CurrentPage==0&&w.NativePageStatus=="Fallback","Signalement du document hors profil.");
        }
        finally{w.Close();}
    }

    internal static void VerifyPlaced(PageCompositionSnapshot snapshot)
    {
        foreach(var page in snapshot.Composition.Pages)
        foreach(var l in page.DisplayLines)
        {
            var native=l.Native!;
            Check(native.Height is >0&&l.Baseline>0&&l.Baseline<=page.Geometry.Height&&native.ParagraphSource.SnapshotId==snapshot.SnapshotId,
                "Ancre ou géométrie verticale.");
            foreach(var part in native.Parts)
            foreach(var s in part.Fragment?.Sources??[])
            {
                var span=s.Source.Span!.Value;
                Check(snapshot.Semantic!.Source(s.Source.NodeId,span)==s.Source&&
                    snapshot.Semantic.RawText(s.Source.NodeId)![span.Start..span.EndExclusive]==part.Text![s.FragmentRange.Start..s.FragmentRange.EndExclusive],
                    "La provenance d’un cluster composé diffère du texte source.");
            }
        }
    }
}
