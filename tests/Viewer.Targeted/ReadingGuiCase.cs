using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.Flow;

static class ReadingGuiCase
{
    static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    static string InlineText(Inline inline)=>inline is Run run?run.Text??"":inline is Span span?string.Concat(span.Inlines.Select(InlineText)):"";
    public static void Run(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow();window.Show();Dispatcher.UIThread.RunJobs();
        string body="<w:p><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr><w:bookmarkStart w:id=\"1\" w:name=\"cible\"/><w:r><w:rPr><w:b/></w:rPr><w:t>Titre Ω 😀</w:t></w:r><w:r><w:rPr><w:b w:val=\"0\"/></w:rPr><w:t> normal</w:t></w:r></w:p>"+
            "<w:tbl><w:tr><w:tc>"+Fixtures.Paragraph("Cellule lisible")+"</w:tc></w:tr></w:tbl>"+
            "<w:p><w:r><w:drawing><a:blip xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" r:embed=\"picture\"/></w:drawing></w:r></w:p>"+
            "<w:p><w:r><w:t>Appel</w:t><w:footnoteReference w:id=\"1\"/></w:r><w:del w:id=\"2\"><w:r><w:delText>Ancien texte</w:delText></w:r></w:del></w:p>"+
            "<x:future xmlns:x=\"urn:visible-test\">Contenu inconnu lisible</x:future>";
        string path=Fixtures.Write(root,"lecture-structuree.docx",Fixtures.Parts(body,extra:new(){["word/footnotes.xml"]=Fixtures.Xml("footnotes","<w:footnote w:id=\"1\">"+Fixtures.Paragraph("Corps de note")+"</w:footnote>")},relationships:Fixtures.Relationship("picture","image","media/picture.png")+Fixtures.Relationship("note","footnotes","footnotes.xml"),binary:new(){["word/media/picture.png"]=Fixtures.Png}));
        string hash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));AboutCases.Complete(window.OpenDocumentAsync(path));
        window.SetReadingView(ReadingView.Continuous);Dispatcher.UIThread.RunJobs();
        Check(window.IsSimplifiedReading&&window.CurrentPage==0&&window.CurrentReading!.Available,"Explicit continuous reading failed");
        var reading=window.GetVisualDescendants().OfType<ListBox>().Single(l=>l.Name=="SimplifiedReading");Check(reading.IsVisible&&reading.GetRealizedContainers().Any(),"No visual reading items");
        var texts=window.GetVisualDescendants().OfType<TextBlock>().Select(t=>(t.Text??"")+string.Concat(t.Inlines?.Select(InlineText)??[])).ToArray();
        Check(texts.Any(t=>t.Contains("Titre Ω 😀 normal",StringComparison.Ordinal)),"Unicode runs absent or reordered in UI");
        var table=window.CurrentReading!.Presentation.BodyBlocks.Single(b=>b.Kind=="table");
        Check(window.NavigateToBlock(table.Id),"Table navigation failed");window.UpdateLayout();Dispatcher.UIThread.RunJobs();
        var tableView=reading.GetVisualDescendants().OfType<ScrollViewer>().Single(v=>v.Name=="ReadingTable"&&Equals(v.Tag,table.Id));
        Check(tableView.GetVisualDescendants().OfType<Control>().Any(c=>c.IsVisible&&
            AutomationProperties.GetName(c)?.Contains("Cellule lisible",StringComparison.Ordinal)==true),
            "The visible table must expose the text of its cell.");
        if(window.ReadingTableSnapshot?.ReadingTables.TryGetValue(table.Id,out var scene)==true)
            Check(scene.Lines.Any(l=>l.Text=="Cellule lisible"&&l.CellId is not null),"The table scene must paint the cell text.");
        var noteBlock=window.CurrentReading!.Blocks.Single(b=>b.PlainText.Contains("Corps de note",StringComparison.Ordinal));window.NavigateToBlock(noteBlock.Id);Dispatcher.UIThread.RunJobs();
        var noteCall=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name?.StartsWith("NoteCall_",StringComparison.Ordinal)==true);noteCall.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        Check(window.ActiveReadingContext is not null&&ReadingUxCases.VisibleText(window.ActiveReadingContext).Contains("Corps de note",StringComparison.Ordinal),"Note no longer accessible in context");
        window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NoteReturn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        Check(window.GetVisualDescendants().OfType<Image>().Any(i=>i.Source is not null&&i.IsVisible),"Qualified PNG not represented in UI");
        var outline=window.CurrentReading!.Outline.Single();Check(window.NavigateToBlock(outline.BlockId)&&window.CurrentBlockId==outline.BlockId,"Heading navigation failed");
        Check(window.NavigateToBlock(window.CurrentReading.Bookmarks.Single().BlockId),"Bookmark navigation failed");
        var placeholder=window.CurrentReading.Blocks.Last();Check(window.NavigateToBlock(placeholder.Id),"Unknown block not reachable");Dispatcher.UIThread.RunJobs();
        var marker=window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>AutomationProperties.GetName(b)?.StartsWith("Diagnostics de cet élément",StringComparison.Ordinal)==true);
        Check(marker is not null,"No localized unknown marker");marker!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        Check(window.GetVisualDescendants().OfType<Expander>().Any(e=>e.Header is string s&&s.StartsWith("Élément sélectionné",StringComparison.Ordinal)),"Marker diagnostics inaccessible");
        using var report=JsonDocument.Parse(window.DetailedDiagnosticReport());Check(report.RootElement.GetProperty("inspection").GetProperty("diagnostics").GetArrayLength()==window.Inspection!.Manifest.Diagnostics.Count,"Detailed diagnostics lost");
        Check(Enum.GetValues<ExportFormat>().All(f=>!window.ExportAvailable(f)),"Simplified reading enabled unqualified exports");
        window.SetTheme(2);window.SetLanguage(true);window.SetTheme(1);window.SetLanguage(false);
        AboutCases.Complete(window.OpenDocumentAsync(path));
        window.SetReadingView(ReadingView.Continuous);
        Check(window.CurrentReading?.Available==true&&window.IsSimplifiedReading,"Reopening structured document failed during container recycling");
        Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))==hash,"Structured reading modified source");window.Close();
    }
}
