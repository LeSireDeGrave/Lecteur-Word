using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;

static class ReadingFinishCases
{
    static void Check(bool yes,string message){if(!yes)throw new InvalidOperationException(message);}
    static Rect RectIn(Control c,Control root)=>new(c.TranslatePoint(new Point(),root)!.Value,c.Bounds.Size);
    static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();}
    static void Layout(){Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();}
    public static void Gui(string output)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow();window.Show();AboutCases.Complete(window.OpenDocumentAsync(ReadingUxCases.Fixture(output)));
        var model=window.CurrentReading!;var note=model.Presentation.Notes.Values.First(n=>n.Verified&&n.Kind=="footnote");
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);window.Width=1280;window.Height=850;Layout();window.NavigateToBlock(note.OriginBlockId);Layout();
            var call=window.GetVisualDescendants().OfType<Button>().First(b=>b.Name=="NoteCall_"+note.ReferenceId);Click(call);Layout();
            Check(window.NotePaneVisible&&window.NotePanePlacement=="right","Wide layout must reserve a right note pane");
            Check(ReadingUxCases.VisibleText(window.ActiveReadingContext!).Contains("Corps de note vérifié",StringComparison.Ordinal),"Wrong note content");
            Check(call.BorderThickness.Bottom==1&&call.Background is ISolidColorBrush bg&&bg.Color.A==0,"Origin marker must be discreet");
            AssertGeometry(window);
            window.Width=860;window.Height=620;Layout();
            Check(window.NotePaneVisible&&window.NotePanePlacement=="bottom","Small layout must keep the note open below the passage");AssertGeometry(window);
            window.SetTheme(theme==1?2:1);Layout();Check(window.NotePaneVisible&&window.CurrentNoteReferenceId==note.ReferenceId,"Theme change lost the open note");window.SetTheme(theme);Layout();
            var close=window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NoteClose");Check(close.Focus(),"Close button not keyboard reachable");
            window.KeyPressQwerty(PhysicalKey.Tab,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Tab,RawInputModifiers.None);Layout();
            Check(window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Any(b=>b.Name=="NoteReturn"&&b.IsFocused),"Tab navigation inside note failed");
            window.KeyPressQwerty(PhysicalKey.Escape,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Escape,RawInputModifiers.None);Layout();Check(!window.NotePaneVisible,"Escape did not close the note");
            call=window.GetVisualDescendants().OfType<Button>().First(b=>b.Name=="NoteCall_"+note.ReferenceId);Check(call.IsFocused,"Escape did not restore focus to reference");
            Click(call);Layout();Click(window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NoteClose"));Check(!window.NotePaneVisible,"Explicit close failed");
            Click(call);Layout();Click(window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NoteReturn"));Check(window.CurrentBlockId==note.OriginBlockId&&!window.NotePaneVisible,"Return to source failed");
            var reading=window.GetVisualDescendants().OfType<ListBox>().Single(l=>l.Name=="SimplifiedReading");Check(reading.SelectedIndex==-1,"Navigation still selects the entire paragraph");
            var diagnostic=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="Diagnostics");Click(diagnostic);
            var summary=window.GetVisualDescendants().OfType<TextBlock>().Single(b=>b.Name=="DiagnosticUserSummary");Check(summary.Text=="Lecture possible","User diagnostic summary missing");
            var technical=window.GetVisualDescendants().OfType<Expander>().Single(b=>b.Name=="DiagnosticTechnicalDetails");Check(!technical.IsExpanded,"Technical XML details must be opt-in");
            technical.IsExpanded=true;Layout();technical.BringIntoView();Layout();
            var list=((StackPanel)technical.Content!).Children.OfType<ListBox>().Single();var groups=((IEnumerable<string>)list.ItemsSource!).ToArray();int group=Array.FindIndex(groups,s=>s.Contains("ELEMENT_SEMANTICS_UNQUALIFIED",StringComparison.Ordinal));Check(group>=0,"Diagnostic occurrences lost");list.BringIntoView();Layout();list.ScrollIntoView(group);Layout();
            Check(ReadingUxCases.VisibleText(technical).Contains("ELEMENT_SEMANTICS_UNQUALIFIED",StringComparison.Ordinal),"Detailed diagnostics no longer accessible");technical.IsExpanded=false;
            using var shot=new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96));shot.Render(window);shot.Save(System.IO.Path.Combine(output,"finish-"+theme+".png"),PngBitmapEncoderOptions.Default);
            Check(ReferenceEquals(model,window.CurrentReading),"UI correction rebuilt or changed the document model");
        }
        window.Close();
    }
    static void AssertGeometry(ReaderWindow window)
    {
        var reading=window.GetVisualDescendants().OfType<ListBox>().Single(l=>l.Name=="SimplifiedReading");var pane=window.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="NotePane");
        var r=RectIn(reading,window);var n=RectIn(pane,window);Check(!r.Intersects(n),"Note pane overlaps the passage");
        Check(n.X>=0&&n.Y>=0&&n.Right<=window.ClientSize.Width+.5&&n.Bottom<=window.ClientSize.Height+.5,"Note pane outside window bounds");
        Check(r.Height>=200&&r.Width>=400,"Passage too small to remain readable");
        foreach(var b in window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Where(b=>b.Name is "NoteClose" or "NoteReturn"))Check(n.Contains(RectIn(b,window)),"Note action clipped");
    }
    public static async Task Native(ReaderWindow window,string output)
    {
        var model=window.CurrentReading!;var note=model.Presentation.Notes.Values.First(n=>n.Verified&&n.Kind=="footnote");
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);window.Width=1240;window.Height=820;await Task.Delay(350);window.NavigateToBlock(note.OriginBlockId);await Task.Delay(100);
            Click(window.GetVisualDescendants().OfType<Button>().First(b=>b.Name=="NoteCall_"+note.ReferenceId));await Task.Delay(200);Check(window.NotePanePlacement=="right","Native wide note placement");AssertGeometry(window);Capture("wide");
            window.Width=860;window.Height=620;await Task.Delay(350);Check(window.NotePanePlacement=="bottom"&&window.NotePaneVisible,"Native resize lost note");AssertGeometry(window);Capture("small");
            Click(window.ActiveReadingContext!.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="NoteReturn"));await Task.Delay(100);Check(window.CurrentBlockId==note.OriginBlockId,"Native note return");
            Check(ReferenceEquals(model,window.CurrentReading),"Native UI changed document model");
            void Capture(string size)
            {
                using var shot=new RenderTargetBitmap(new PixelSize((int)(window.ClientSize.Width*window.RenderScaling),(int)(window.ClientSize.Height*window.RenderScaling)),new Vector(96*window.RenderScaling,96*window.RenderScaling));shot.Render(window);shot.Save(System.IO.Path.Combine(output,$"finish-native-{theme}-{size}.png"),PngBitmapEncoderOptions.Default);
            }
        }
        window.SetLanguage(false);Console.WriteLine("Native finishing UX OK: reserved note space, resizing, themes, source return and unchanged model.");
    }
}
