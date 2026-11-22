using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

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
            var window=new ReaderWindow(ThemePreference.ForCurrentUser());desktop.MainWindow=window;
            if(desktop.Args is {Length:>0})window.Opened+=async(_,_)=>await window.OpenDocumentAsync(desktop.Args[0]);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
