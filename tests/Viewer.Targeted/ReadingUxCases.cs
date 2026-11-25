using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class ReadingUxCases
{
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static string Text(Inline item)=>item is Run r?r.Text??"":item is Span s?string.Concat(s.Inlines.Select(Text)):"";
    public static string VisibleText(Control control)=>string.Join("\n",control.GetVisualDescendants().OfType<TextBlock>().Select(t=>(t.Text??"")+string.Concat(t.Inlines?.Select(Text)??[])));
    internal static string Fixture(string root)
    {
        string complex="<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>12</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";
        string body="<w:p><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr><w:r><w:t>Lecture confortable</w:t><w:footnoteReference w:id=\"42\"/><w:endnoteReference w:id=\"7\"/></w:r></w:p>"+
            "<w:p>"+complex+"<w:fldSimple w:instr=\"FUTUREFIELD\"><w:r><w:t>valeur ancienne</w:t></w:r></w:fldSimple><w:fldSimple w:instr=\"DATE\"/><w:r><w:footnoteReference w:id=\"99\"/></w:r></w:p>"+
            "<x:future xmlns:x=\"urn:ux-test\">Texte futur conservé</x:future>"+
            "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"default\" r:id=\"head\"/><w:footerReference w:type=\"default\" r:id=\"foot\"/></w:sectPr></w:pPr><w:r><w:t>Fin première section</w:t></w:r></w:p>"+
            Fixtures.Paragraph("Deuxième section")+"<w:sectPr/>";
        var parts=Fixtures.Parts(body,new()
        {
            ["word/footnotes.xml"]=Fixtures.Xml("footnotes","<w:footnote w:id=\"42\">"+Fixtures.Paragraph("Corps de note vérifié")+"</w:footnote>"),
            ["word/endnotes.xml"]=Fixtures.Xml("endnotes","<w:endnote w:id=\"7\">"+Fixtures.Paragraph("Note de fin vérifiée")+"</w:endnote>"),
            ["word/header1.xml"]=Fixtures.Xml("hdr",Fixtures.Paragraph("En-tête réservé aux sections")),
            ["word/footer1.xml"]=Fixtures.Xml("ftr","<w:p>"+complex+"</w:p>")
        },Fixtures.Relationship("notes","footnotes","footnotes.xml")+Fixtures.Relationship("ends","endnotes","endnotes.xml")+Fixtures.Relationship("head","header","header1.xml")+Fixtures.Relationship("foot","footer","footer1.xml"));
        return Fixtures.Write(root,"lecture-premium.docx",parts);
    }
    public static void Model(string root)
    {
        string path=Fixture(root);string before=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var inspected=DocumentInspector.InspectDocument(path);var reading=ReadableReader.Read(inspected);var p=reading.Presentation;
        Check(reading.Available,"Fixture refused");
        Check(p.Notes.Values.Count(n=>n.Verified)==2&&p.Notes.Values.Count(n=>!n.Verified)==1,"Missing/valid notes not distinguished");
        Check(p.Notes.Values.Where(n=>n.Verified).All(n=>n.Number=="1"),"Reader numbering confused with source identifiers");
        Check(p.Sections.Count==2&&p.Sections[0].Stories.Count==2&&p.Sections[1].Stories.All(s=>s.Inherited&&s.Verified),"Section inheritance incorrect");
        Check(!p.BodyBlocks.Any(b=>b.Source.PartUri.Contains("header",StringComparison.Ordinal)),"Header mixed into main body");
        Check(p.Fields.Values.Count(f=>f.Name=="PAGE"&&f.Known&&f.Matched&&f.StoredValue=="12")==2,"Stored complex fields lost or recalculated");
        Check(p.Fields.Values.Any(f=>f.Name=="FUTUREFIELD"&&!f.Known&&f.StoredValue=="valeur ancienne")&&p.Fields.Values.Any(f=>f.Name=="DATE"&&!f.HasStoredValue),"Unknown or empty fields misrepresented");
        string visible=string.Join('\n',p.BodyBlocks.Select(p.TextFor));Check(!visible.Contains(" PAGE ",StringComparison.Ordinal)&&visible.Contains("12",StringComparison.Ordinal),"Field code mixed with reading text");
        Check(reading.PlainText.Contains("Corps de note vérifié",StringComparison.Ordinal)&&reading.PlainText.Contains("En-tête réservé",StringComparison.Ordinal),"Source tree was deleted");
        Check(Enum.GetValues<ExportFormat>().All(f=>!DocumentExports.Preflight(inspected,f).Allowed),"Reading UX enabled exports");
        Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))==before,"Source changed");
        string implicitPath=Fixtures.Write(root,"section-finale-implicite.docx",Fixtures.Parts("<w:p><w:pPr><w:sectPr/></w:pPr><w:r><w:t>Section un</w:t></w:r></w:p>"+Fixtures.Paragraph("Section deux")));
        Check(ReadableReader.Read(DocumentInspector.InspectDocument(implicitPath)).Presentation.Sections.Count==2,"Implicit final section lost");
        Console.WriteLine("Verified note links, per-section stories, recorded/unknown/empty fields, source and export gates OK");
    }
    public static void Real()
    {
        string path=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT")??throw new InvalidOperationException("Explicit copy required");
        var inspected=DocumentInspector.InspectDocument(path);var reading=ReadableReader.Read(inspected);var p=reading.Presentation;
        const string w="{http://schemas.openxmlformats.org/wordprocessingml/2006/main}";
        string main=inspected.Manifest.Relationships.Single(r=>r.SourcePart=="/"&&r.Type.EndsWith("/officeDocument",StringComparison.Ordinal)).ResolvedPart!;
        var references=inspected.Manifest.Nodes.Where(n=>n.Source.PartUri==main&&(n.Name==w+"footnoteReference"||n.Name==w+"endnoteReference")).ToArray();
        Check(references.Length>0&&p.Notes.Count==references.Length&&p.Notes.Values.All(n=>n.Verified),"Real reference mapping incomplete");
        Check(p.Sections.Count==3&&p.Sections.Sum(s=>s.Stories.Count)==4&&p.Sections.SelectMany(s=>s.Stories).All(s=>s.Verified),"Witness section assignments changed");
        Check(p.Fields.Count==4&&p.Fields.Values.All(f=>f.Known&&f.Matched&&!f.HasStoredValue&&(f.Name is "PAGE" or "NUMPAGES")),"Witness missing cached page values were invented or not recognized");
        Check(p.BodyBlocks.All(b=>b.Source.PartUri==main),"Stories mixed with body");
        var raw=inspected.Manifest.Nodes.ToDictionary(n=>n.Id);var children=inspected.Manifest.Nodes.Where(n=>n.ParentId is not null).ToLookup(n=>n.ParentId!);
        IEnumerable<InventoryNode> Tree(InventoryNode n){yield return n;foreach(var c in children[n.Id])foreach(var d in Tree(c))yield return d;}
        void VerifyTexts(IEnumerable<InventoryNode> source,IReadOnlyList<ReadableBlock> content)
        {
            var represented=ReadablePresentation.Walk(content).Where(b=>b.Kind=="text").GroupBy(b=>b.Id).ToDictionary(g=>g.Key,g=>g.First().Text);
            foreach(var text in source.Where(n=>n.Name==w+"t"||n.Name==w+"delText"))Check(represented.TryGetValue(text.Id,out var value)&&value==string.Concat(children[text.Id].Where(n=>n.Kind=="textValue").Select(n=>n.Text)),"Auxiliary source text lost");
        }
        foreach(var note in p.Notes.Values)
        {
            string id=raw[note.ReferenceId].Attributes[w+"id"];
            var definition=inspected.Manifest.Nodes.Single(n=>n.Name==w+note.Kind&&n.Attributes.GetValueOrDefault(w+"id")==id);
            VerifyTexts(Tree(definition),note.Content);
        }
        foreach(var story in p.Sections.SelectMany(s=>s.Stories))VerifyTexts(inspected.Manifest.Nodes.Where(n=>n.Source.PartUri==story.Source.PartUri),story.Content);
        Console.WriteLine($"Real UX: {references.Length} verified note calls, {p.Sections.Count} sections, {p.Fields.Count} fields; raw source tree retained.");
    }
    public static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow();window.Show();AboutCases.Complete(window.OpenDocumentAsync(Fixture(root)));
        window.SetReadingView(ReadingView.Continuous);
        Check(window.IsSimplifiedReading&&window.CurrentPage==0,"Fictitious pagination");
        Check(!window.GetVisualDescendants().OfType<Button>().Any(b=>b.Content is string s&&s=="Miniatures"),"Non-contextual thumbnail command");
        Check(!VisibleText(window).Contains("urn:ux-test",StringComparison.Ordinal)&&!VisibleText(window).Contains("[Fin de note]",StringComparison.Ordinal),"Technical markup still dominates text");
        Check(!VisibleText(window).Contains("FUTUREFIELD",StringComparison.Ordinal)&&!VisibleText(window).Contains(" PAGE ",StringComparison.Ordinal),"Field instructions exposed in prose");
        var note=window.CurrentReading!.Presentation.Notes.Values.First(n=>n.Verified&&n.Kind=="footnote");window.NavigateToBlock(note.OriginBlockId);Dispatcher.UIThread.RunJobs();
        var call=window.GetVisualDescendants().OfType<Button>().First(b=>b.Name=="NoteCall_"+note.ReferenceId);call.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        Check(window.CurrentNoteReferenceId==note.ReferenceId&&window.ActiveReadingContext is not null&&VisibleText(window.ActiveReadingContext).Contains("Corps de note vérifié",StringComparison.Ordinal),"Numbered note does not open its verified content");
        window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NoteReturn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();Check(window.CurrentBlockId==note.OriginBlockId,"Note return failed");
        Check(window.ShowSection(2),"Section view unavailable");var open=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="Lire les en-têtes et pieds…");open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        Check(VisibleText(window.ActiveReadingContext!).Contains("En-tête réservé aux sections",StringComparison.Ordinal)&&!VisibleText(window.ActiveReadingContext!).Contains(" PAGE ",StringComparison.Ordinal),"Section story content missing or field instructions exposed");
        window.GetVisualDescendants().OfType<Button>().First(b=>AutomationProperties.GetName(b)=="Rechercher (Ctrl+F)").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        var query=window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="SearchBox");query.Text="Corps de note vérifié";Dispatcher.UIThread.RunJobs();
        var result=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="Corps de note vérifié");result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        Check(window.ActiveReadingContext is not null&&VisibleText(window.ActiveReadingContext).Contains(query.Text,StringComparison.Ordinal),"Search did not open note content");
        window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NoteReturn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        query.Text="En-tête réservé";Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content is string s&&s.StartsWith("En-tête réservé",StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        Check(window.ActiveReadingContext is not null&&VisibleText(window.ActiveReadingContext).Contains("En-tête réservé aux sections",StringComparison.Ordinal),"Search did not open section story");
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var shot=new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96));shot.Render(window);shot.Save(Path.Combine(root,"premium-"+theme+".png"),PngBitmapEncoderOptions.Default);
        }
        window.Close();
    }
}
