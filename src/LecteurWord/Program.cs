using Avalonia;
using LecteurWord.Platform;
using System.Text.Json;

namespace LecteurWord;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [ReadingProtocol.WorkerArgument])
            return ReadingProtocol.RunIsolatedWorker();
        if (args is [ExtensionTool.Argument,..var command])
            return ExtensionTool.Run(command,Console.Out);
        if (args is ["--build-info"])
        {
            Console.WriteLine(JsonSerializer.Serialize(BuildReference.Current.TechnicalIdentity()));
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()=>AppBuilder.Configure<ReaderApp>().UsePlatformDetect().With(new X11PlatformOptions{WmClass="LecteurWord"}).WithInterFont();
}
