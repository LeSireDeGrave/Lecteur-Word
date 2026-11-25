using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LecteurWord.Flow;
using LecteurWord.Platform;
using SkiaSharp;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    sealed record ReadingColorPreference(string Mode = "auto", string Custom = "#FFF0D1");
    ReadingColorPreference readingColor = new();
    string? ReadingColorPath => themePreference is {} preference ? Path.Combine(preference.DirectoryPath,"reading-color.json") : null;

    static Color Solid(IBrush brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.White;

    internal static Color AdaptReadingInk(Color ink, Color background)
    {
        if(ReaderPalette.ContrastRatio(ink,background)>=4.5)return ink;
        Color target=ReaderPalette.ContrastingInk(background);
        double low=0,high=1;
        for(int i=0;i<9;i++)
        {
            double weight=(low+high)/2;
            if(ReaderPalette.ContrastRatio(ReaderPalette.Blend(ink,target,weight),background)>=4.5)high=weight;
            else low=weight;
        }
        return ReaderPalette.Blend(ink,target,high);
    }

    Color ReadingInk(Color original,Color background) => ResolveReadingInk(readingColor,original,background);

    static Color ResolveReadingInk(ReadingColorPreference preference,Color original,Color background) => preference.Mode switch
    {
        "original" => original,
        "white" => Colors.White,
        "black" => Colors.Black,
        "cream" => Color.FromRgb(255,240,209),
        "custom" => Color.Parse(preference.Custom),
        _ => AdaptReadingInk(original,background)
    };

    static SKColor ToSkia(Color color) => new(color.R,color.G,color.B,color.A);
    static Color FromSkia(SKColor color) => Color.FromArgb(color.Alpha,color.Red,color.Green,color.Blue);

    Func<SKColor,SKColor,SKColor>? ReadingPageInk()
    {
        var preference=readingColor;
        return preference.Mode=="original" ? null :
            (foreground,background)=>ToSkia(ResolveReadingInk(preference,FromSkia(foreground),FromSkia(background)));
    }

    Stream ReadingPagePng(ComposedPage page,float scale) =>
        new MemoryStream(Composer.PagePng(page,scale,ToSkia(Solid(ReadingSurface)),ReadingPageInk()));

    void LoadReadingColor()
    {
        if(ReadingColorPath is not {} path)return;
        try
        {
            if(PlatformFiles.ReadOptional<ReadingColorPreference>(path) is {} stored &&
                stored.Mode is "auto" or "original" or "white" or "black" or "cream" or "custom" &&
                Color.TryParse(stored.Custom,out var custom) && custom.A==255)
                readingColor=stored;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { RecordIncident(error,SessionStage.Startup); }
    }

    void SetReadingColor(string mode,string? custom=null)
    {
        readingColor=new(mode,custom??readingColor.Custom);
        UpdateThemeIndicators();
        if(ReadingColorPath is not {} path)return;
        try { PlatformFiles.Write(path,readingColor); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        { RecordIncident(error,SessionStage.Interface); }
    }

    Button ReadingColorButton()
    {
        var button=IconButton("readingColor","Couleur du texte de lecture","Reading text color",()=>Task.CompletedTask);
        button.Name="ReadingTextColor";
        var menu=new Flyout();
        button.Flyout=menu;
        menu.Opening+=(_,_)=>
        {
            var choices=new StackPanel {Width=235,Spacing=4,FlowDirection=FlowDirection};
            menu.Content=choices;
            foreach(var (mode,label) in new[]{
                ("auto",T("Adaptation automatique","Automatic contrast")),
                ("original",T("Couleurs originales","Original colors")),
                ("white",T("Texte blanc","White text")),
                ("black",T("Texte noir","Black text")),
                ("cream",T("Texte crème","Cream text")),
                ("custom",T("Couleur personnalisée","Custom color"))})
            {
                var item=new RadioButton{Content=DialogText(label),GroupName="ReadingTextColor",IsChecked=mode==readingColor.Mode};
                Avalonia.Automation.AutomationProperties.SetName(item,label);
                item.Click+=async (_,_)=>
                {
                    menu.Hide();
                    if(mode!="custom"){SetReadingColor(mode);return;}
                    var dialog=PlatformDialog(T("Couleur personnalisée","Custom color"),420,220);
                    var panel=DialogStack(dialog);
                    var value=new TextBox{Text=readingColor.Custom,PlaceholderText="#RRGGBB",FlowDirection=FlowDirection.LeftToRight};
                    Avalonia.Automation.AutomationProperties.SetName(value,label);
                    panel.Children.Add(value);
                    var error=DialogText("");
                    panel.Children.Add(error);
                    var apply=new Button{Content=T("Appliquer","Apply"),IsDefault=true};
                    panel.Children.Add(apply);
                    apply.Click+=(_,_)=>
                    {
                        if(value.Text is {} raw&&raw.Length==7&&raw[0]=='#'&&Color.TryParse(raw,out var color)&&color.A==255)
                        {SetReadingColor(mode,raw);dialog.Close();}
                        else error.Text=T("Saisir une couleur au format #RRGGBB.","Enter a color in #RRGGBB format.");
                    };
                    await dialog.ShowDialog(this);
                };
                choices.Children.Add(item);
            }
            choices.Children.Add(new Separator());
            var reset=new Button{Content=DialogText(T("Rétablir le réglage automatique","Restore automatic contrast"))};
            reset.Click+=(_,_)=>{menu.Hide();SetReadingColor("auto");};
            choices.Children.Add(reset);
        };
        return button;
    }

    Button ReadingSearchResult(string excerpt)
    {
        IBrush surface=ReadingSurface;
        return new Button{Content=excerpt,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Stretch,
            Background=surface,Foreground=new SolidColorBrush(ReadingInk(Colors.Black,Solid(surface)))};
    }

    void InvalidateReadingPageColors()
    {
        DetachPageRasters();
        TrimRasters(0);
        if(!IsSimplifiedReading)Dispatcher.UIThread.Post(RealizeVisiblePages,DispatcherPriority.Loaded);
    }
}
