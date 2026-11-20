using Avalonia;
namespace LecteurWord;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)=>BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp()=>AppBuilder.Configure<ReaderApp>().UsePlatformDetect().With(new X11PlatformOptions{WmClass="LecteurWord"}).WithInterFont();
}
