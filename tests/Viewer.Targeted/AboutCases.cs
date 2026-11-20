using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;

static class AboutCases
{
    static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    public static void Metadata()
    {
        var missing=BuildReference.FromMetadata(new Dictionary<string,string>());
        Check(missing.Version is null&&missing.Commit is null&&missing.CompilationUtc is null&&missing.HasLocalChanges is null,"Missing metadata invented");
        Check(missing.FullReference().Contains("indisponible",StringComparison.Ordinal)&&missing.FullReference(true).Contains("unavailable",StringComparison.Ordinal),"Missing values not explicit in both languages");
        Dictionary<string,string> Data(string utc)=>new(){["LecteurWord.Build.Version"]="0.1.0",["LecteurWord.Build.Utc"]=utc,["LecteurWord.Build.Commit"]=new string('a',40),["LecteurWord.Build.Dirty"]="true",["LecteurWord.Build.Target"]="linux-x64"};
        var summer=BuildReference.FromMetadata(Data("2026-07-01T12:00:00.0000000Z"));
        var paris=TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        Check(summer.FullReference(false,paris).Contains("2026-07-01 14:00:00 +02:00",StringComparison.Ordinal),"Summer UTC conversion wrong");
        var winter=BuildReference.FromMetadata(Data("2026-01-01T12:00:00Z"));
        Check(winter.FullReference(false,paris).Contains("2026-01-01 13:00:00 +01:00",StringComparison.Ordinal),"Winter UTC conversion wrong");
        Check(summer.Commit==new string('a',40)&&summer.HasLocalChanges==true&&summer.CompactReference().EndsWith(" +",StringComparison.Ordinal),"Commit or dirty state lost");
        foreach(var version in new[]{"1.2","01.2.3","1.2.3-..","1.2.3-01"})
        {var invalid=Data("2026-07-01T12:00:00Z");invalid["LecteurWord.Build.Version"]=version;Check(BuildReference.FromMetadata(invalid).Version is null,"Invalid SemVer accepted: "+version);}
        var badDate=Data("2026-07-01T12:00:00");badDate["LecteurWord.Build.Commit"]="fabricated";badDate["LecteurWord.Build.Dirty"]="maybe";
        var invalidMetadata=BuildReference.FromMetadata(badDate);Check(invalidMetadata.CompilationUtc is null&&invalidMetadata.Commit is null&&invalidMetadata.HasLocalChanges is null,"Unverifiable metadata accepted");
        var current=BuildReference.Current;Check(current.Version=="0.1.0"&&current.CompilationUtc is not null,"Build metadata absent");
        Check(current.CompilationUtc<=DateTimeOffset.UtcNow.AddMinutes(1),"Build timestamp in future");
        Check(current.FullReference().Contains(RuntimeInformation.ProcessArchitecture.ToString(),StringComparison.Ordinal)&&current.FullReference().Contains(RuntimeInformation.FrameworkDescription,StringComparison.Ordinal),"Runtime identity missing");
        Console.WriteLine(current.FullReference());
    }
    public static void Gui(string output)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
        var reader=new ReaderWindow();reader.Show();Dispatcher.UIThread.RunJobs();
        Complete(reader.CopyBuildReferenceAsync());
        Check(Complete(reader.Clipboard!.TryGetTextAsync())==BuildReference.Current.FullReference(),"Footer clipboard differs from full build reference");
        Check(reader.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Name=="CopyConfirmation").Text=="Référence copiée","Footer confirmation missing");
        foreach(bool english in new[]{false,true})foreach(int theme in new[]{1,2})
        {
            reader.SetTheme(theme);reader.SetLanguage(english);
            reader.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="AboutLecteurWord").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
            var about=reader.OwnedWindows.OfType<AboutWindow>().Single();
            Check(about.ActualThemeVariant==(theme==1?ThemeVariant.Light:ThemeVariant.Dark),"About theme mismatch");
            Check(about.InformationText==BuildReference.Current.FullReference(english),"About metadata differs");
            Complete(about.CopyInformationAsync());Check(Complete(about.Clipboard!.TryGetTextAsync())==about.InformationText,"About clipboard differs");
            Check(about.CopyStatusText==(english?"Information copied":"Informations copiées")||about.CopyStatusText==(english?"Reference copied":"Référence copiée"),"About copy confirmation missing");
            about.Width=320;about.Height=350;Dispatcher.UIThread.RunJobs();
            Check(about.CopyButton.Focus()&&about.CopyButton.IsFocused,"Copy inaccessible by keyboard focus");
            foreach(var button in about.GetVisualDescendants().OfType<Button>().Where(b=>b.Name is "AboutCopy" or "AboutClose"))
            {var position=button.TranslatePoint(new Point(0,0),about)!.Value;Check(position.X>=0&&position.X+button.Bounds.Width<=about.ClientSize.Width&&position.Y+button.Bounds.Height<=about.ClientSize.Height,"Footer button clipped in small About window");}
            about.KeyPressQwerty(PhysicalKey.Tab,RawInputModifiers.None);about.KeyReleaseQwerty(PhysicalKey.Tab,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();Check(!about.CopyButton.IsFocused,"Tab did not move keyboard focus");
            foreach(int dpi in new[]{96,192})
            {
                using var image=new RenderTargetBitmap(new PixelSize((int)about.ClientSize.Width*dpi/96,(int)about.ClientSize.Height*dpi/96),new Vector(dpi,dpi));image.Render(about);image.Save(Path.Combine(output,$"about-{english}-{theme}-{dpi}.png"),PngBitmapEncoderOptions.Default);
            }
            Check(!about.GetVisualDescendants().OfType<Menu>().Any(),"Unexpected classic menu");
            Check(about.GetVisualDescendants().OfType<Button>().Where(b=>b.Content is not string).All(b=>!string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))),"Unlabelled About icon");
            about.KeyPressQwerty(PhysicalKey.Escape,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
            Check(!about.IsVisible,"Escape did not close About");
        }
        reader.Close();
    }
    public static void Complete(Task task)
    {
        var limit=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted&&DateTime.UtcNow<limit){Dispatcher.UIThread.RunJobs();Thread.Sleep(2);}Check(task.IsCompleted,"UI action timeout");task.GetAwaiter().GetResult();Dispatcher.UIThread.RunJobs();
    }
    public static T Complete<T>(Task<T> task){Complete((Task)task);return task.GetAwaiter().GetResult();}
}
