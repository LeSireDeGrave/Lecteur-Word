using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

if(args.Length!=2||args[0]!="--case"){Console.Error.WriteLine("Select exactly one --case <name>. No aggregate suite command; see the explicit switch for available cases.");return 64;}
string root=Path.Combine(Path.GetTempPath(),"lecteur-word-target-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    switch(args[1])
    {
        case "composition": CompositionTest();break;
        case "render-gates": RenderGates();break;
        case "export-pdf": Exports(ExportFormat.Pdf);break;
        case "export-odt": Exports(ExportFormat.Odt);break;
        case "export-epub": Exports(ExportFormat.Epub3);break;
        case "export-gates": ExportGates();break;
        case "gui": Gui();break;
        case "gui-search-unicode": GuiSearchUnicode();break;
        case "about-metadata": AboutCases.Metadata();break;
        case "about-gui": AboutCases.Gui(root);break;
        case "about-no-git": AboutCases.Metadata();Assert(BuildReference.Current.Commit is null&&BuildReference.Current.HasLocalChanges is null,"Build captured Git despite missing executable");break;
        case "native-read-about": Assert(NativeCases.Run(root)==0,"Native GUI qualification failed");break;
        case "reading-model": ReadingCases.Model();break;
        case "reading-png": ReadingCases.PngGate();break;
        case "reading-real": ReadingCases.RealDocument();break;
        case "reading-gui": ReadingGuiCase.Run(root);break;
        case "reading-ux-model": ReadingUxCases.Model(root);break;
        case "reading-ux-gui": ReadingUxCases.Gui(root);break;
        case "reading-ux-real": ReadingUxCases.Real();break;
        case "native-reading-ux": Assert(NativeCases.Run(root,true)==0,"Native premium reading failed");break;
        case "reading-finish-gui": ReadingFinishCases.Gui(root);break;
        case "native-reading-finish": Assert(NativeCases.Run(root,finish:true)==0,"Native reading finish failed");break;
        case "fonts-model": FontCases.Model(root);break;
        case "fonts-resolution": FontCases.Resolution(root);break;
        case "fonts-pagination": FontCases.Pagination(root);break;
        case "fonts-embedded": FontCases.Embedded(root);break;
        case "fonts-gui": FontCases.Gui(root);break;
        case "fonts-real": FontCases.Real(root);break;
        case "native-fonts": Assert(NativeCases.Run(root,fonts:true)==0,"Native fonts failed");break;
        case "structure-lists": StructureCases.Lists(root);break;
        case "structure-tabs": StructureCases.Tabs(root);break;
        case "structure-tables": StructureCases.Tables(root);break;
        case "structure-merges": StructureCases.Merges(root);break;
        case "structure-table-pages": StructureCases.TablePages(root);break;
        case "structure-images": StructureCases.Images(root);break;
        case "structure-mixed": StructureCases.Mixed(root);break;
        case "structure-gates": StructureCases.Gates(root);break;
        case "structure-corpus": StructureCases.Corpus(root);break;
        case "structure-real-table": StructureCases.RealTable(root);break;
        case "structure-gui": StructureCases.Gui(root);break;
        case "native-structures": Assert(NativeCases.Run(root,structures:true)==0,"Native structures failed");break;
        case "layout-geometry": LayoutCases.Geometry(root);break;
        case "layout-styles": LayoutCases.Styles(root);break;
        case "layout-constraints": LayoutCases.Constraints(root);break;
        case "layout-sections": LayoutCases.Sections(root);break;
        case "layout-gates": LayoutCases.Gates(root);break;
        case "layout-gui": LayoutCases.Gui(root);break;
        case "native-layout": Assert(NativeCases.Run(root,layout:true)==0,"Native layout failed");break;
        default:throw new InvalidOperationException("Unknown targeted case");
    }
    Console.WriteLine("OK "+args[1]+" | fixtures: "+root);return 0;
}
catch(Exception e){Console.Error.WriteLine("KO "+args[1]+" | "+e+" | fixtures: "+root);return 1;}
void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
string Sha(string p)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)));
InspectionResult Inspect(string body,string name="source")
{
    string path=Fixtures.Write(root,name+".docx",Fixtures.Parts(body));string before=Sha(path);
    var result=DocumentInspector.InspectDocument(path);Assert(Sha(path)==before,"Source changed");Assert(result.Manifest.InspectionComplete,"Inspection refused");return result;
}
void CompositionTest()
{
    string text="Long paragraphe à caractères accentués et espaces significatifs. ";
    var result=Inspect(string.Concat(Enumerable.Range(0,200).Select(i=>Fixtures.Paragraph(i+" "+text+text+text))));
    var flow=FlowReader.Read(result);Assert(flow.Paragraphs.Count==200&&flow.CanExport,"Plain profile changed");
    var pages=Composer.Compose(flow);Assert(pages.Available&&pages.Pages.Count>2,"No calculated pages");
    foreach(var p in flow.Paragraphs)
        Assert(string.Concat(pages.Pages.SelectMany(pg=>pg.Lines).Where(l=>l.ParagraphId==p.Id).Select(l=>l.Text))==p.Text,"Wrapped text lost");
    Assert(pages.Pages.All(pg=>pg.Lines.All(l=>l.Baseline<=Composition.Height-Composition.Margin)),"Line outside page");
    Assert(pages.Pages.SelectMany(pg=>pg.Lines).All(l=>l.Source.PartUri=="/word/document.xml"),"Line provenance lost");
    var again=Composer.Compose(flow);Assert(System.Text.Json.JsonSerializer.Serialize(pages)==System.Text.Json.JsonSerializer.Serialize(again),"Composition nondeterministic");
    var manual=Composer.Compose(FlowReader.Read(Inspect("<w:p><w:r><w:t>avant</w:t><w:br w:type=\"page\"/><w:t>après</w:t></w:r></w:p>","break")));
    Assert(manual.Pages.Count==2&&manual.Pages[1].Lines[0].Text=="après","Manual break lost");
    File.WriteAllBytes(Path.Combine(root,"page.png"),Composer.PagePng(pages.Pages[0]));
}
void RenderGates()
{
    foreach(var (name,body) in new[]{("revision","<w:p><w:del w:id=\"1\"><w:r><w:delText>deleted</w:delText></w:r></w:del></w:p>"),("table","<w:tbl><w:tr><w:tc>"+Fixtures.Paragraph("cell")+"</w:tc></w:tr></w:tbl>"),("field","<w:p><w:fldSimple w:instr=\"PAGE\"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p>")})
    {var layout=Composer.Compose(FlowReader.Read(Inspect(body,name)));Assert(!layout.Available&&layout.Issues.Any(i=>i.BlocksRendering),"Unsupported content rendered: "+name);}
    var unicode=Composer.Compose(FlowReader.Read(Inspect(Fixtures.Paragraph("العربية 中文 😀"),"unicode")));Assert(!unicode.Available&&unicode.Issues.Any(i=>i.Code=="SCRIPT_NOT_QUALIFIED"),"Unqualified shaping allowed");
    var styles=FlowReader.Read(Inspect("<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:t>aperçu</w:t></w:r></w:p>","style"));
    Assert(styles.CanRender&&!styles.CanExport&&Composer.Compose(styles).Available,"Preview/faithfulness not distinguished");
    using var cancel=new CancellationTokenSource();cancel.Cancel();try{Composer.Compose(styles,cancel.Token);throw new InvalidOperationException("Ignored cancellation");}catch(OperationCanceledException){}
    var overflow=Composer.Compose(FlowReader.Read(Inspect(string.Concat(Enumerable.Repeat("<w:p><w:r><w:t>x</w:t><w:br w:type=\"page\"/></w:r></w:p>",502)),"pages-limit")));
    Assert(!overflow.Available&&overflow.Issues.Any(i=>i.Code=="PAGE_LIMIT"),"Page budget not diagnosed");
    var raw=FlowReader.Read(Inspect("<w:p><w:r>unstructured text</w:r></w:p>","raw-text"));
    Assert(!raw.CanRender&&raw.Issues.Any(i=>i.Code=="TEXT_OUTSIDE_RUN"),"Unstructured text silently lost");
    Assert(!Composer.Compose(FlowReader.Read(Inspect(Fixtures.Paragraph("soft&#xAD;hyphen"),"soft-hyphen"))).Available,"Unqualified format character rendered");
}
void Exports(ExportFormat format)
{
    string text=format==ExportFormat.Pdf?"  Recherche — étude française et ligature œ.  ":"  Unicode œ العربية 中文 😀  ";
    string first=format==ExportFormat.Pdf?Fixtures.Paragraph(text):"<w:p><w:r><w:t xml:space=\"preserve\">"+text+"</w:t><w:tab/><w:t>tab</w:t><w:br/><w:t>line</w:t></w:r></w:p>";
    var source=Inspect(first+Fixtures.Paragraph("")+Fixtures.Paragraph("Deuxième paragraphe &amp; contenu &lt;préservé&gt;."));
    Assert(DocumentExports.Preflight(source,format).Allowed,"Plain export refused");
    string output=Path.Combine(root,"export");string path=DocumentExports.Export(source,format,output);
    Assert(File.Exists(path)&&new FileInfo(path).Length>100,"Empty export");
    try{DocumentExports.Export(source,format,output);throw new InvalidOperationException("Output overwritten");}catch(IOException){}
    string[] expected=FlowReader.Read(source).Paragraphs.Select(p=>p.Text).ToArray();
    File.WriteAllText(Path.Combine(root,"expected.json"),System.Text.Json.JsonSerializer.Serialize(expected));
    if(format!=ExportFormat.Pdf)
    {
        using var zip=ZipFile.OpenRead(path);Assert(zip.Entries[0].FullName=="mimetype"&&zip.Entries[0].CompressedLength==zip.Entries[0].Length,"mimetype not first/stored");
        XDocument Part(string name){using var stream=zip.GetEntry(name)!.Open();return XDocument.Load(stream);}
        string[] actual;
        if(format==ExportFormat.Odt)
        {
            XNamespace t="urn:oasis:names:tc:opendocument:xmlns:text:1.0";
            string Read(XElement e)=>string.Concat(e.Nodes().Select(n=>n is XText tx?tx.Value:n is XElement el?el.Name==t+"s"?new string(' ',(int?)el.Attribute(t+"c")??1):el.Name==t+"tab"?"\t":el.Name==t+"line-break"?"\n":Read(el):""));
            actual=Part("content.xml").Descendants(t+"p").Select(Read).ToArray();
        }
        else{XNamespace h="http://www.w3.org/1999/xhtml";actual=Part("EPUB/content.xhtml").Descendants(h+"p").Select(p=>p.Value).ToArray();}
        Assert(actual.SequenceEqual(expected),"Structured export changed characters/paragraphs");
    }
    Console.WriteLine("export: "+path);
}
void ExportGates()
{
    var styles=Inspect("<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>essential style</w:t></w:r></w:p>");
    foreach(var f in Enum.GetValues<ExportFormat>())
    {
        Assert(!DocumentExports.Preflight(styles,f).Allowed,"Unmapped style export enabled");
        string output=Path.Combine(root,f.ToString());try{DocumentExports.Export(styles,f,output);throw new InvalidOperationException("Unqualified export created");}catch(InvalidDataException){}
        Assert(!Directory.Exists(output),"Blocked export left a final output");
    }
    var plain=Inspect(Fixtures.Paragraph("cancel"),"plain");using var cancel=new CancellationTokenSource();cancel.Cancel();
    try{DocumentExports.Export(plain,ExportFormat.Odt,Path.Combine(root,"cancel"),cancel.Token);throw new InvalidOperationException("Cancellation ignored");}catch(OperationCanceledException){}
    Assert(!Directory.EnumerateDirectories(root,".lecteur-export-*").Any(),"Staging leak");
    var tiny=plain with {Manifest=plain.Manifest with {Budgets=new(){MaxOutputBytes=16}}};
    try{DocumentExports.Export(tiny,ExportFormat.Odt,Path.Combine(root,"quota"));throw new InvalidOperationException("Export quota ignored");}catch(OutputLimitException){}
    Assert(!Directory.Exists(Path.Combine(root,"quota")),"Over-budget final export left behind");
    try{DocumentExports.Export(tiny,ExportFormat.Pdf,Path.Combine(root,"quota-pdf"));throw new InvalidOperationException("Native PDF quota ignored");}catch(OutputLimitException){}
    Assert(!Directory.Exists(Path.Combine(root,"quota-pdf")),"Over-budget PDF final output left behind");
    var literalCr=Inspect(Fixtures.Paragraph("literal&#13;CR"),"literal-cr");
    foreach(var f in Enum.GetValues<ExportFormat>())Assert(!DocumentExports.Preflight(literalCr,f).Allowed,"Unqualified XML control exported");
}
void Gui()
{
    AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
    var window=new ReaderWindow();window.Show();Dispatcher.UIThread.RunJobs();
    Assert(window.CurrentPage==0,"Fake pagination before open");
    string path=Fixtures.Write(root,"Apercu-synthetique.docx",Fixtures.Parts(string.Concat(Enumerable.Range(0,90).Select(i=>Fixtures.Paragraph("Paragraphe "+(i+1)+" — Lecture seule. Le contenu du document reste intact. Un aperçu composé par Lecteur Word, sans promesse de pagination Word.")))));
    string before=Sha(path);var task=window.OpenDocumentAsync(path);
    var deadline=DateTime.UtcNow.AddSeconds(20);while(!task.IsCompleted&&DateTime.UtcNow<deadline){Dispatcher.UIThread.RunJobs();Thread.Sleep(5);}task.GetAwaiter().GetResult();Dispatcher.UIThread.RunJobs();
    Assert(window.CurrentComposition?.Available==true&&window.CurrentPage==1,"GUI did not load composition");
    window.GoToPage(1);Assert(window.CurrentPage==2,"Page navigation failed");window.SetZoom(1.4);Assert(Math.Abs(window.Zoom-1.4)<.001,"Zoom failed");window.SetZoom(1);
    window.ToggleNavigation();Assert(!window.NavigationVisible,"Collapse failed");window.ToggleNavigation();
    window.SetLanguage(true);Assert(window.Language=="en","Language failed");window.SetLanguage(false);
    Assert(window.GetVisualDescendants().OfType<Button>().Where(b=>b.Name?.StartsWith("Action_",StringComparison.Ordinal)==true).All(b=>!string.IsNullOrEmpty(AutomationProperties.GetName(b))),"Unlabelled icon");
    Assert(!window.GetVisualDescendants().OfType<Menu>().Any(),"Classic menu present");
    window.KeyPressQwerty(Avalonia.Input.PhysicalKey.F,Avalonia.Input.RawInputModifiers.Control);window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.F,Avalonia.Input.RawInputModifiers.Control);Dispatcher.UIThread.RunJobs();
    var query=window.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="SearchBox");query.Text="Paragraphe 9";Dispatcher.UIThread.RunJobs();
    Assert(query.IsFocused&&window.GetVisualDescendants().OfType<Button>().Any(b=>b.Content is string t&&t.Contains("Paragraphe 9",StringComparison.Ordinal)),"Search/focus failed");
    query.Text="Paragraphe 90";Dispatcher.UIThread.RunJobs();Assert(query.IsFocused,"Typing lost focus");
    // Return to thumbnails through the actual navigation action.
    window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="Miniatures").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    var oldHash=window.Inspection!.Manifest.SourceSha256;
    var cancelled=window.OpenDocumentAsync(path);
    window.GetVisualDescendants().OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Annuler l’opération").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    while(!cancelled.IsCompleted){Dispatcher.UIThread.RunJobs();Thread.Sleep(2);}cancelled.GetAwaiter().GetResult();
    Assert(window.Inspection.Manifest.SourceSha256==oldHash,"Cancelled open changed previous document");
    void Capture(string name,int theme)
    {
        window.SetTheme(theme);window.GoToPage(0);Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var screenshot=new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96));screenshot.Render(window);screenshot.Save(Path.Combine(root,name+".png"),PngBitmapEncoderOptions.Default);
    }
    Capture("viewer-light",1);Capture("viewer-dark",2);
    window.Width=860;window.Height=620;Dispatcher.UIThread.RunJobs();Capture("viewer-small",1);
    Assert(Sha(path)==before,"GUI changed source");window.Close();
}
void GuiSearchUnicode()
{
    AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
    var window=new ReaderWindow();window.Show();Dispatcher.UIThread.RunJobs();
    string prefix=new('A',31);
    string emoji=prefix+"😀tail",combining=prefix+"e\u0301tail";
    string path=Fixtures.Write(root,"unicode-search.docx",Fixtures.Parts(Fixtures.Paragraph(emoji)+Fixtures.Paragraph(combining)));
    string before=Sha(path);var task=window.OpenDocumentAsync(path);
    var deadline=DateTime.UtcNow.AddSeconds(20);
    while(!task.IsCompleted&&DateTime.UtcNow<deadline){Dispatcher.UIThread.RunJobs();Thread.Sleep(5);}
    Assert(task.IsCompleted,"Unicode fixture load timed out");task.GetAwaiter().GetResult();Dispatcher.UIThread.RunJobs();
    Assert(window.Inspection?.Manifest.InspectionComplete==true,"Unicode inventory refused");
    Assert(window.CurrentComposition?.Available==false,"Search fix must not qualify unsupported script rendering");
    window.KeyPressQwerty(Avalonia.Input.PhysicalKey.F,Avalonia.Input.RawInputModifiers.Control);window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.F,Avalonia.Input.RawInputModifiers.Control);Dispatcher.UIThread.RunJobs();
    var query=window.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="SearchBox");query.Text=prefix;Dispatcher.UIThread.RunJobs();
    var excerpts=window.GetVisualDescendants().OfType<Button>().Select(b=>b.Content as string).Where(t=>t?.StartsWith(prefix,StringComparison.Ordinal)==true).ToArray();
    Assert(excerpts.Contains(prefix+"😀…"),"Search excerpt splits a surrogate pair at its limit");
    Assert(excerpts.Contains(prefix+"e\u0301…"),"Search excerpt splits a combining character at its limit");
    Assert(window.Inspection!.Manifest.Nodes.Any(n=>n.Text==emoji)&&window.Inspection.Manifest.Nodes.Any(n=>n.Text==combining),"Search changed inventoried source text");
    Assert(Sha(path)==before,"Search changed the original fixture");window.Close();
}
