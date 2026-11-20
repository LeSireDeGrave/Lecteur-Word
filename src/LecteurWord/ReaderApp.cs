using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace LecteurWord;
public sealed class ReaderApp:Application
{
    public override void Initialize()=>Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window=new ReaderWindow();desktop.MainWindow=window;
            if(desktop.Args is {Length:>0})window.Opened+=async(_,_)=>await window.OpenDocumentAsync(desktop.Args[0]);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
