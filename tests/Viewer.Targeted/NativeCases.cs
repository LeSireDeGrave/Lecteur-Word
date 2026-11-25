using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LecteurWord;

static class NativeCases
{
    public static string Output="";
    public static bool Ux;
    public static bool Finish;
    public static bool Layout;
    public static bool Fonts;
    public static bool Structures;
    public static bool Compatibility;
    public static bool Opus;
    public static bool Tables;
    public static bool RenderQuality;
    public static bool PagesView;
    public static int Run(string output,bool ux=false,bool finish=false,bool layout=false,bool fonts=false,bool structures=false,bool compatibility=false,bool opus=false,bool tables=false,bool renderQuality=false,bool pagesView=false)
    {
        Output=output;Ux=ux;Finish=finish;Layout=layout;Fonts=fonts;Structures=structures;Compatibility=compatibility;Opus=opus;Tables=tables;RenderQuality=renderQuality;PagesView=pagesView;
        return AppBuilder.Configure<NativeQaApp>().UsePlatformDetect().UseSkia().WithInterFont().StartWithClassicDesktopLifetime([]);
    }
    public static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
}
sealed class NativeQaApp:Application
{
    public override void Initialize()=>Styles.Add(ReaderApp.CreateTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var window=new ReaderWindow();desktop.MainWindow=window;
            window.Opened+=async(_,_)=>
            {
                int result=0;
                try{await Qualify(window);}
                catch(Exception ex){Console.Error.WriteLine(ex);result=1;}
                finally{foreach(var owned in window.OwnedWindows.ToArray())owned.Close();window.Close();desktop.Shutdown(result);}
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
    static async Task Qualify(ReaderWindow window)
    {
        if(NativeCases.PagesView){await PageViewCases.Native(window,NativeCases.Output);return;}
        if(NativeCases.RenderQuality){await RenderQualityCases.Native(window);return;}
        if(NativeCases.Tables){await TableReadingCases.Native(window);return;}
        if(NativeCases.Opus){await OpusUxCases.Native(window,NativeCases.Output);return;}
        if(NativeCases.Compatibility){await CompatibilityCases.Native(window,NativeCases.Output);return;}
        if(NativeCases.Structures){await StructureCases.Native(window,NativeCases.Output);return;}
        if(NativeCases.Fonts){await FontCases.Native(window,NativeCases.Output);return;}
        if(NativeCases.Layout){await LayoutCases.Native(window,NativeCases.Output);return;}
        string input=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT")??throw new InvalidOperationException("Explicit QA copy path required");
        string hash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input)));
        var timer=Stopwatch.StartNew();await window.OpenDocumentAsync(input);long loadMs=timer.ElapsedMilliseconds;
        window.SetReadingView(ReadingView.Continuous);await Task.Delay(400);
        var keyboardChecks=new List<object>();
        NativeCases.Check(window.IsSimplifiedReading&&window.CurrentReading?.Available==true&&window.CurrentPage==0,"Real document remains blank or fake paginated");
        NativeCases.Check(window.GetVisualDescendants().OfType<ListBox>().Single(b=>b.Name=="SimplifiedReading").GetRealizedContainers().Any(),"No visible native reading container");
        if(NativeCases.Ux){await QualifyReadingUx(window);window.SetLanguage(false);}
        if(NativeCases.Finish)await ReadingFinishCases.Native(window,NativeCases.Output);
        var handle=window.TryGetPlatformHandle()??throw new InvalidOperationException("No native handle");
        NativeCases.Check(handle.HandleDescriptor=="XID"&&handle.Handle!=IntPtr.Zero,"Not an X11 graphical session");
        var systemClipboard=window.Clipboard??throw new InvalidOperationException("Native clipboard unavailable");string? clipboard=await systemClipboard.TryGetTextAsync();
        try
        {
            await window.CopyBuildReferenceAsync();NativeCases.Check(await systemClipboard.TryGetTextAsync()==BuildReference.Current.FullReference(),"Native footer clipboard mismatch");
            var first=window.CurrentReading!.Outline.FirstOrDefault();if(first is not null)NativeCases.Check(window.NavigateToBlock(first.BlockId),"Native outline navigation failed");
            foreach(int theme in new[]{1,2})
            {
                window.SetTheme(theme);window.SetLanguage(theme==2);await Task.Delay(250);Capture(window,"native-read-"+theme);
                window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="AboutLecteurWord").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(250);
                var about=window.OwnedWindows.OfType<AboutWindow>().Single();
                NativeCases.Check(await about.CopyInformationAsync(),"Native About copy unavailable");NativeCases.Check(await about.Clipboard!.TryGetTextAsync()==BuildReference.Current.FullReference(theme==2),"Native About clipboard mismatch");
                about.Width=350;about.Height=420;await Task.Delay(200);about.CopyButton.Focus();
                NativeCases.Check(about.CopyButton.IsFocused&&about.CopyButton.Bounds.Right<=about.ClientSize.Width,"Native About copy not accessible in small window");
                Capture(about,"native-about-"+theme);
                var xid=about.TryGetPlatformHandle()!.Handle;
                int keys=0;about.AddHandler(Avalonia.Input.InputElement.KeyDownEvent,(_,e)=>{keys++;Console.WriteLine("Native key="+e.Key+" handled="+e.Handled);},RoutingStrategies.Tunnel|RoutingStrategies.Bubble,true);
                // XTEST through the focused test window; Avalonia XI2 ignores XSendEvent (--window key).
                var key=new ProcessStartInfo("xdotool"){UseShellExecute=false};key.ArgumentList.Add("windowactivate");key.ArgumentList.Add("--sync");key.ArgumentList.Add(xid.ToInt64().ToString());key.ArgumentList.Add("key");key.ArgumentList.Add("Tab");
                using(var process=Process.Start(key)!)await process.WaitForExitAsync();await Task.Delay(300);
                Console.WriteLine("Native About active="+about.IsActive+" keys="+keys+" focus="+(about.FocusManager?.GetFocusedElement() as Control)?.Name);
                if(keys>0)NativeCases.Check(!about.CopyButton.IsFocused,"Native Tab did not move keyboard focus");
                else Console.WriteLine("NT native keyboard injection: no XTEST key event received; Tab/Escape verified separately with Avalonia headless.");
                bool tabReceived=keys>0;int beforeEscape=keys;
                if(tabReceived)
                {
                    var escape=new ProcessStartInfo("xdotool"){UseShellExecute=false};escape.ArgumentList.Add("windowactivate");escape.ArgumentList.Add("--sync");escape.ArgumentList.Add(xid.ToInt64().ToString());escape.ArgumentList.Add("key");escape.ArgumentList.Add("Escape");
                    using(var process=Process.Start(escape)!)await process.WaitForExitAsync();await Task.Delay(300);
                    if(keys>beforeEscape)NativeCases.Check(!about.IsVisible,"Native Escape did not close About");
                }
                keyboardChecks.Add(new{theme,tabReceived,tabMoved=tabReceived&&!about.CopyButton.IsFocused,escapeReceived=keys>beforeEscape,escapeClosed=!about.IsVisible});
                about.Close();await Task.Delay(100);
            }
            window.Width=860;window.Height=620;window.SetLanguage(false);window.SetTheme(1);await Task.Delay(150);Capture(window,"native-read-small");
            var search=window.GetVisualDescendants().OfType<Button>().First(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Rechercher (Ctrl+F)");search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
            var query=window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="SearchBox");query.Text="ouvrage";await Task.Delay(100);
            NativeCases.Check(query.IsFocused,"Native search focus failed");Capture(window,"native-read-search");
            File.WriteAllBytes(Path.Combine(NativeCases.Output,"native-diagnostics.json"),window.DetailedDiagnosticReport());
            File.WriteAllText(Path.Combine(NativeCases.Output,"native-measures.json"),JsonSerializer.Serialize(new{loadMs,window.RenderScaling,window.DesktopScaling,handle=handle.HandleDescriptor,blocks=window.CurrentReading.Blocks.Count,characters=window.CurrentReading.PlainText.Length,sourceSha256=hash,build=BuildReference.Current.FullReference(),workingSet=Environment.WorkingSet,keyboardChecks},new JsonSerializerOptions{WriteIndented=true}));
            NativeCases.Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input)))==hash,"Native session changed input");
            Console.WriteLine("Native X11 OK | loadMs="+loadMs+" | scale="+window.RenderScaling+" | chars="+window.CurrentReading.PlainText.Length);
        }
        finally{if(clipboard is not null)await systemClipboard.SetTextAsync(clipboard);}
    }
    static async Task QualifyReadingUx(ReaderWindow window)
    {
        var p=window.CurrentReading!.Presentation;
        NativeCases.Check(p.Notes.Values.Count(n=>n.Verified)==12&&p.Sections.Count==3,"Real UX mapping not complete");
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);window.SetLanguage(theme==2);await Task.Delay(150);Capture(window,"premium-main-"+theme);
            NativeCases.Check(!ReadingUxCases.VisibleText(window).Contains("schemas.openxmlformats",StringComparison.Ordinal),"XML namespaces exposed in main reading");
            foreach(string kind in new[]{"footnote","endnote"})
            {
                var note=p.Notes.Values.First(n=>n.Verified&&n.Kind==kind);window.NavigateToBlock(note.OriginBlockId);await Task.Delay(150);
                var call=window.GetVisualDescendants().OfType<Button>().First(b=>b.Name=="NoteCall_"+note.ReferenceId);call.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(150);
                var content=window.ActiveReadingContext!;string displayed=ReadingUxCases.VisibleText(content);
                foreach(var text in LecteurWord.Flow.ReadablePresentation.Walk(note.Content).Where(b=>b.Kind=="text"&&b.Text.Length>0))NativeCases.Check(displayed.Contains(text.Text,StringComparison.Ordinal),"Native note text missing");
                CaptureContext(content,"premium-"+kind+"-"+theme,window.RenderScaling);
                content.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NoteReturn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);NativeCases.Check(window.CurrentBlockId==note.OriginBlockId,"Native return failed");
            }
            window.ShowSection(2);var open=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string==(theme==2?"Read headers and footers…":"Lire les en-têtes et pieds…"));open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(200);
            NativeCases.Check(!ReadingUxCases.VisibleText(window.ActiveReadingContext!).Contains("[Champ :",StringComparison.Ordinal),"Field code exposed in section content");CaptureContext(window.ActiveReadingContext!,"premium-stories-"+theme,window.RenderScaling);
        }
        Console.WriteLine("Native premium UX OK: footnote/endnote return, separated section stories, compact marks and recorded fields.");
    }
    static void CaptureContext(Control control,string name,double scale)
    {
        using var shot=new RenderTargetBitmap(new PixelSize(Math.Max(1,(int)(control.Bounds.Width*scale)),Math.Max(1,(int)(control.Bounds.Height*scale))),new Vector(96*scale,96*scale));shot.Render(control);shot.Save(Path.Combine(NativeCases.Output,name+".png"),PngBitmapEncoderOptions.Default);
    }
    static void Capture(Window window,string name)
    {
        double scale=window.RenderScaling;using var shot=new RenderTargetBitmap(new PixelSize((int)(window.ClientSize.Width*scale),(int)(window.ClientSize.Height*scale)),new Vector(96*scale,96*scale));shot.Render(window);shot.Save(Path.Combine(NativeCases.Output,name+".png"),PngBitmapEncoderOptions.Default);
    }
}
