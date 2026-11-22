using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class LayoutCases
{
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static string Sha(string p)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)));
    static string Section(int width=4000,int height=4800,int margin=400,string start="nextPage")=>$"<w:sectPr><w:type w:val=\"{start}\"/><w:pgSz w:w=\"{width}\" w:h=\"{height}\"/><w:pgMar w:left=\"{margin}\" w:right=\"{margin}\" w:top=\"{margin}\" w:bottom=\"{margin}\"/></w:sectPr>";
    static string Lines(int count,string properties="")=>"<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"400\" w:lineRule=\"exact\"/>"+properties+"</w:pPr><w:r>"+string.Join("<w:br/>",Enumerable.Range(0,count).Select(i=>"<w:t>Ligne "+i+"</w:t>"))+"</w:r></w:p>";
    static InspectionResult Inspect(string root,string body,string name="layout",Dictionary<string,string>? extra=null,string relationships="")
    {
        string path=Fixtures.Write(root,name+".docx",Fixtures.Parts(body,extra,relationships));string before=Sha(path);
        var result=DocumentInspector.InspectDocument(path);Check(result.Manifest.InspectionComplete,"Fixture inspection refused");Check(Sha(path)==before,"Source modified");return result;
    }
    public static string Compatible(string root)
    {
        string styles=Fixtures.Xml("styles","<w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"24\"/></w:rPr></w:rPrDefault></w:docDefaults><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:pPr><w:spacing w:after=\"120\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Times New Roman\"/></w:rPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"0\"/><w:keepNext/><w:spacing w:before=\"180\" w:after=\"160\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"36\"/></w:rPr></w:style><w:style w:type=\"character\" w:styleId=\"Emphasis\"><w:rPr><w:i/><w:color w:val=\"245B9B\"/></w:rPr></w:style>");
        string body="<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Composition expérimentale</w:t></w:r></w:p>";
        for(int i=0;i<45;i++)body+="<w:p><w:pPr><w:spacing w:line=\"300\"/></w:pPr><w:r><w:t>Paragraphe "+(i+1)+" — Texte français conservé, espaces compris. </w:t></w:r><w:r><w:rPr><w:rStyle w:val=\"Emphasis\"/></w:rPr><w:t>Italique et couleur.</w:t></w:r><w:r><w:rPr><w:b/><w:u w:val=\"single\"/></w:rPr><w:t> Accentuation explicite.</w:t></w:r></w:p>";
        body+="<w:p><w:pPr>"+Section(11906,16838,1134)+"</w:pPr><w:r><w:t>Fin de la section portrait.</w:t></w:r></w:p>";
        body+="<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:t>Section paysage</w:t><w:br w:type=\"page\"/><w:t>Après le saut manuel</w:t></w:r></w:p>"+Section(16838,11906,1134);
        return Fixtures.Write(root,"Composition-compatible.docx",Fixtures.Parts(body,new(){["word/styles.xml"]=styles},Fixtures.Relationship("style","styles","styles.xml")));
    }
    public static void Geometry(string root)
    {
        var result=Inspect(root,Lines(26)+Section());var model=LayoutReader.Read(result);Check(model.Available,"Qualified model refused");
        var pages=Composer.Compose(model);Check(pages.Available&&pages.Pages.Count==3,"Expected three calculated pages");
        Check(pages.Pages.Select(p=>p.Lines.Count).SequenceEqual([10,10,6]),"Incorrect physical line capacity");
        Check(pages.Pages.All(p=>p.Geometry.Width==200&&p.Geometry.Height==240&&p.Geometry.Left==20),"Twips conversion incorrect");
        Check(string.Concat(pages.Pages.SelectMany(p=>p.Lines).Select(l=>l.Text+l.SourceBreak))==model.Paragraphs[0].Text,"Text or linebreak lost");
        foreach(var page in pages.Pages)foreach(var line in page.Lines)
            Check(line.Baseline<=page.Geometry.Height-page.Geometry.Bottom&&line.Fragments.All(f=>f.X>=page.Geometry.Left-.01),"Text outside content bounds");
        Check(JsonSerializer.Serialize(pages)==JsonSerializer.Serialize(Composer.Compose(model)),"Pagination nondeterministic");
        File.WriteAllBytes(Path.Combine(root,"page.png"),Composer.PagePng(pages.Pages[0]));Console.WriteLine("3 pages, 10/10/6 lines; 200 × 240 pt, 20 pt margins; exact source text.");
    }
    public static void Styles(string root)
    {
        string path=Compatible(root);var inspection=DocumentInspector.InspectDocument(path);var timer=System.Diagnostics.Stopwatch.StartNew();long allocated=GC.GetTotalAllocatedBytes();var model=LayoutReader.Read(inspection);long readMs=timer.ElapsedMilliseconds;Check(model.Available,"Styled model refused: "+string.Join(',',model.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));
        Check(model.Paragraphs[0].Runs.All(r=>r.Style.Bold&&r.Style.Size==18),"Calculated heading style incorrect");
        Check(model.Paragraphs.Any(p=>p.Runs.Any(r=>r.Style.Italic&&r.Style.Color=="245B9B"))&&model.Issues.Any(i=>i.Code=="LAYOUT_FONT_SUBSTITUTION"),"Run styles/substitution missing");
        timer.Restart();var pages=Composer.Compose(model);long composeMs=timer.ElapsedMilliseconds;Check(pages.Available,"Styled composition refused: "+string.Join(',',pages.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));
        foreach(var p in model.Paragraphs)
            Check(string.Concat(pages.Pages.SelectMany(pg=>pg.Lines).Where(l=>l.ParagraphId==p.Id).Select(l=>l.Text+l.SourceBreak))==p.Text,"Styled paragraph text lost");
        Check(pages.Pages.Any(pg=>pg.Geometry.Width>pg.Geometry.Height)&&pages.Pages.Any(pg=>pg.Geometry.Width<pg.Geometry.Height),"Section dimensions not applied");
        Console.WriteLine("Styled fixture pages="+pages.Pages.Count);File.WriteAllText(Path.Combine(root,"layout.json"),JsonSerializer.Serialize(new{model,pages},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(root,"layout-measures.json"),JsonSerializer.Serialize(new{readMs,composeMs,allocatedBytes=GC.GetTotalAllocatedBytes()-allocated,workingSet=Environment.WorkingSet,pages=pages.Pages.Count,paragraphs=model.Paragraphs.Count},new JsonSerializerOptions{WriteIndented=true}));
    }
    public static void Constraints(string root)
    {
        Composition Compose(string body,string name)=>Composer.Compose(Inspect(root,body+Section(4000,4000),name));
        var widow=Compose(Lines(9),"widow");Check(widow.Available&&widow.Pages.Select(p=>p.Lines.Count).SequenceEqual([7,2]),"Widow/orphan split not enforced");
        var keep=Compose(Lines(6)+Lines(3,"<w:keepLines/>"),"keep");Check(keep.Available&&keep.Pages.Select(p=>p.Lines.Count).SequenceEqual([6,3]),"Keep lines not honored");
        var chain=Compose(Lines(6)+Lines(1,"<w:keepNext/>")+Lines(3),"chain");Check(chain.Available&&chain.Pages.Select(p=>p.Lines.Count).SequenceEqual([6,4]),"Keep next not honored");
        var impossible=Compose(Lines(9,"<w:keepLines/>"),"impossible");Check(!impossible.Available&&impossible.Issues.Any(i=>i.Code=="LAYOUT_KEEP_LINES_TOO_LARGE"),"Impossible constraint silently relaxed");
        var manual=Compose("<w:p><w:r><w:t>Avant</w:t><w:br w:type=\"page\"/><w:t>Après</w:t></w:r></w:p>","manual");Check(manual.Available&&manual.Pages.Count==2&&manual.Pages[1].Lines[0].Text=="Après","Manual break displaced");
        var trailing=Compose("<w:p><w:r><w:t>Avant</w:t><w:br w:type=\"page\"/></w:r></w:p>","trailing");Check(trailing.Available&&trailing.Pages.Count==2&&trailing.Pages[1].Lines.Count==0,"Explicit trailing blank page lost");
        var before=Compose(Lines(1)+Lines(1,"<w:pageBreakBefore/>"),"before");Check(before.Pages.Count==2,"Page break before lost");
        using var cancel=new CancellationTokenSource();cancel.Cancel();try{Composer.Compose(LayoutReader.Read(Inspect(root,Lines(1),"cancel")),cancel.Token);throw new InvalidOperationException("Cancellation ignored");}catch(OperationCanceledException){}
    }
    public static void Sections(string root)
    {
        string First(string props)=>"<w:p><w:pPr>"+props+"</w:pPr><w:r><w:t>Première section</w:t></w:r></w:p>";
        var same=Composer.Compose(Inspect(root,First(Section())+Fixtures.Paragraph("Suite continue")+Section(start:"continuous"),"continuous"));Check(same.Available&&same.Pages.Count==1&&same.Pages[0].Lines.Select(l=>l.Section).Distinct().SequenceEqual([1,2]),"Compatible continuous section failed");
        var changed=Composer.Compose(Inspect(root,First(Section())+Fixtures.Paragraph("Paysage")+Section(6000,4000),"next"));Check(changed.Available&&changed.Pages.Count==2&&changed.Pages[1].Geometry.Width==300,"Next section geometry incorrect");
        var bad=Composer.Compose(Inspect(root,First(Section())+Fixtures.Paragraph("Incompatible")+Section(6000,4000,start:"continuous"),"continuous-bad"));Check(!bad.Available,"Continuous incompatible geometry silently paginated");
        var odd=Composer.Compose(Inspect(root,First(Section())+Fixtures.Paragraph("Impair")+Section(start:"oddPage"),"odd"));Check(!odd.Available,"Unqualified odd-page section rendered");
    }
    public static void Gates(string root)
    {
        foreach(var body in new[]{"<w:tbl><w:tblPr><w:tblLayout w:type=\"fixed\"/></w:tblPr><w:tr><w:tc>"+Fixtures.Paragraph("cellule")+"</w:tc></w:tr></w:tbl>","<w:p><w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>","<w:p><w:fldSimple w:instr=\"PAGE\"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p>",Fixtures.Paragraph("العربية 😀"),"<w:p><w:pPr><w:jc w:val=\"distribute\"/></w:pPr><w:r><w:t>Distribué hors profil</w:t></w:r></w:p>"})
        {var input=Inspect(root,body,"gate-"+Guid.NewGuid().ToString("N"));Check(!Composer.Compose(input).Available&&ReadableReader.Read(input).Available,"Gate bypass or simplified reading lost");}
        var clip=Composer.Compose(Inspect(root,"<w:p><w:pPr><w:spacing w:line=\"120\" w:lineRule=\"exact\"/></w:pPr><w:r><w:t>Texte</w:t></w:r></w:p>","clip"));Check(!clip.Available&&clip.Issues.Any(i=>i.Code=="LAYOUT_LINE_CLIP_UNQUALIFIED"),"Clipping allowed");
        string? real=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT");if(real is not null){string sha=Sha(real);var input=DocumentInspector.InspectDocument(real);var layout=Composer.Compose(input);Check(!layout.Available&&ReadableReader.Read(input).Available&&Sha(real)==sha,"Complex witness falsely paginated or changed");Console.WriteLine("Real witness: 0 qualified pages; simplified reading retained; blockers="+string.Join(',',layout.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code).Distinct()));}
    }
    public static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow();window.Show();AboutCases.Complete(window.OpenDocumentAsync(Compatible(root)));VerifyGui(window);window.Close();
    }
    static void VerifyGui(ReaderWindow window)
    {
        Check(window.CurrentComposition?.Available==true&&window.CurrentPage==1,"Compatible GUI has no calculated pages");
        Check(window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("Pagination expérimentale",StringComparison.Ordinal)==true),"Experimental label missing");
        window.GoToPage(1);Check(window.CurrentPage==2,"Next page failed");window.FitToWidth();Check(window.Zoom>0&&window.Zoom<=2,"Fit failed");
        Dispatcher.UIThread.RunJobs();var image=window.GetVisualDescendants().OfType<Image>().Single(i=>i.Name=="CalculatedPage");Check(image.Width<window.ClientSize.Width-224&&Math.Abs(image.Width/image.Height-window.CurrentComposition!.Pages[1].Geometry.Width/window.CurrentComposition.Pages[1].Geometry.Height)<.001,"Fit or physical page ratio incorrect");
        var mode=window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="ReadingMode");mode.SelectedIndex=1;Dispatcher.UIThread.RunJobs();Check(window.IsSimplifiedReading&&window.CurrentPage==0,"Simplified comparison mode lost");mode.SelectedIndex=0;Dispatcher.UIThread.RunJobs();Check(window.CurrentPage>0,"Return to pages failed");
        Check(!window.ExportAvailable(ExportFormat.Pdf),"New layout incorrectly qualified complex export");
    }
    public static async Task Native(ReaderWindow window,string root)
    {
        string path=Compatible(root),before=Sha(path);var timer=System.Diagnostics.Stopwatch.StartNew();await window.OpenDocumentAsync(path);await Task.Delay(250);VerifyGui(window);
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);window.GoToPage(0);window.FitToWidth();await Task.Delay(200);Capture("layout-native-"+theme);
            window.Width=860;window.Height=620;await Task.Delay(250);Check(window.CurrentPage>0,"Resize lost pages");Capture("layout-native-small-"+theme);
            var mode=window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="ReadingMode");mode.SelectedIndex=1;await Task.Delay(150);Check(window.IsSimplifiedReading,"Native simplified mode failed");mode.SelectedIndex=0;await Task.Delay(150);
        }
        Check(Sha(path)==before,"Native GUI modified DOCX");File.WriteAllBytes(Path.Combine(root,"diagnostics.json"),window.DetailedDiagnosticReport());
        File.WriteAllText(Path.Combine(root,"native-layout-measures.json"),JsonSerializer.Serialize(new{elapsedMs=timer.ElapsedMilliseconds,workingSet=Environment.WorkingSet,pages=window.CurrentComposition!.Pages.Count,geometry=window.CurrentComposition.Pages.Select(p=>p.Geometry),sourceSha256=before,window.RenderScaling},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Native layout OK | calculated pages="+window.CurrentComposition.Pages.Count+" | fixture="+path);
        void Capture(string name)
        {
            // Capture the actual X11 window: RenderTargetBitmap can apply a second
            // DPI transform to embedded page bitmaps and misrepresent the display.
            var handle=window.TryGetPlatformHandle();Check(handle?.HandleDescriptor=="XID","Native layout requires an X11 window");
            var capture=new System.Diagnostics.ProcessStartInfo("import"){UseShellExecute=false};
            foreach(string arg in new[]{"-window",handle!.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),Path.Combine(root,name+".png")})capture.ArgumentList.Add(arg);
            using var process=System.Diagnostics.Process.Start(capture)!;process.WaitForExit();Check(process.ExitCode==0,"X11 window capture failed");
        }
    }
}
