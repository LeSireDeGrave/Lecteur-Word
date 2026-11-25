using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LecteurWord.Platform;

namespace LecteurWord;
public sealed class ReaderApp:Application
{
    public override void Initialize()=>Styles.Add(CreateTheme());

    internal static FluentTheme CreateTheme() => new();

    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SessionRecoveryService? recovery=null;
            string? startupWarning=null;
            try { recovery=new SessionRecoveryService(PlatformPaths.ForCurrentUser()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                startupWarning="Journal de session indisponible. Vérifiez les droits d’accès ou fermez l’autre fenêtre de Lecteur Word.";
            }
            ExtensionManagerService? extensions = null;
            try
            {
                var version = ExtensionHostVersion.Parse(BuildReference.Current.Version);
                extensions = new ExtensionManagerService(PlatformPaths.ForCurrentUser(),version);
            }
            catch (InvalidDataException error)
            {
                recovery?.Record(error,SessionStage.Startup);
                startupWarning ??= "Version de Lecteur Word indisponible pour vérifier les extensions.";
            }
            var window=new ReaderWindow(ThemePreference.ForCurrentUser(),recovery,startupWarning,extensions);
            desktop.MainWindow=window;
            window.Opened+=(_,_)=>Dispatcher.UIThread.Post(async ()=>await window.StartReaderAsync(desktop.Args?.FirstOrDefault()));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
