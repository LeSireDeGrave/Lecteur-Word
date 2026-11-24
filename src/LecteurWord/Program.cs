using Avalonia;
using LecteurWord.Platform;

namespace LecteurWord;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [ReadingProtocol.WorkerArgument])
            return ReadingProtocol.RunWorker(Console.OpenStandardInput(), Console.OpenStandardOutput());
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()=>AppBuilder.Configure<ReaderApp>().UsePlatformDetect().With(new X11PlatformOptions{WmClass="LecteurWord"}).WithInterFont();
}
