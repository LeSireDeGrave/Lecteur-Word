using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LecteurWord.Platform;

namespace LecteurWord;
public sealed class ReaderApp:Application
{
    public override void Initialize()=>Styles.Add(CreateTheme());

    internal static FluentTheme CreateTheme()
    {
        var theme=new FluentTheme();
        theme.Palettes.Add(ThemeVariant.Light,new ColorPaletteResources
        {
            Accent=Color.Parse("#875133"),RegionColor=Color.Parse("#FCF5EB"),
            BaseHigh=Color.Parse("#30221B"),BaseMediumHigh=Color.Parse("#534136"),
            ChromeLow=Color.Parse("#F5EBDD"),ChromeMediumLow=Color.Parse("#F2E5D3"),
            ListLow=Color.Parse("#F5E7D8"),ListMedium=Color.Parse("#E9D5BF")
        });
        theme.Resources.ThemeDictionaries.Add(ReaderPalette.BlueVariant,ReaderPalette.BlueResources());
        return theme;
    }
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
            if (recovery is not null)
            {
                try
                {
                    string? version = BuildReference.Current.Version?.Split('-', '+')[0];
                    extensions = new ExtensionManagerService(PlatformPaths.ForCurrentUser(),ExtensionPackage.StableVersion(version));
                }
                catch (InvalidDataException error)
                {
                    recovery.Record(error,SessionStage.Startup);
                    startupWarning = "Version de Lecteur Word indisponible pour vérifier les extensions.";
                }
            }
            var window=new ReaderWindow(ThemePreference.ForCurrentUser(),recovery,startupWarning,extensions);
            desktop.MainWindow=window;
            window.Opened+=(_,_)=>Dispatcher.UIThread.Post(async ()=>await window.StartReaderAsync(desktop.Args?.FirstOrDefault()));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
