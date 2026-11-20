using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LecteurWord;

public sealed class AboutWindow:Window
{
    public const string OfficialCredits="Lecteur Word est un logiciel imaginé par Le Sire De Grave, conçu et développé avec l'assistance de ChatGPT, une intelligence artificielle d'OpenAI.\n\nOffert gracieusement par « Le Sire De Grave ».\n\n© 2026 Le Sire De Grave.";
    public static Uri RepositoryUri { get; }=new("https://github.com/LeSireDeGrave/Lecteur-Word");
    static readonly Lazy<string> notices=new(ReadNotices);
    readonly bool english;
    readonly TextBlock copyStatus=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Name="AboutCopyStatus"};
    readonly DispatcherTimer feedbackTimer=new(){Interval=TimeSpan.FromSeconds(3)};
    readonly Border heading=new(),actions=new();
    readonly SelectableTextBlock information=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Name="AboutBuildInformation"};
    public Button CopyButton { get; }
    public string InformationText=>BuildReference.Current.FullReference(english);
    public string CopyStatusText=>copyStatus.Text??"";
    public static string ThirdPartyNotices=>notices.Value;

    public AboutWindow(bool english=false)
    {
        this.english=english;
        Title=english?"About Lecteur Word":"À propos de Lecteur Word";
        Icon=BrandAssets.WindowIcon;
        Name="AboutWindow";Width=620;Height=710;MinWidth=320;MinHeight=350;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        FontFamily=new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");
        var root=new Grid{RowDefinitions=new RowDefinitions("Auto,*,Auto")};
        var wordmark=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,*"),ColumnSpacing=16,Margin=new Thickness(24,22)};
        var logo=BrandAssets.CreateImage(72,true);
        AutomationProperties.SetName(logo,"Lecteur Word");
        wordmark.Children.Add(logo);
        var identity=new StackPanel{Spacing=5,VerticalAlignment=VerticalAlignment.Center};
        identity.Children.Add(new TextBlock{Text="Lecteur Word",FontSize=25,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});
        identity.Children.Add(new TextBlock{Text=BuildReference.Current.CompactReference(english),FontSize=12,TextWrapping=TextWrapping.Wrap,MaxWidth=420});
        Grid.SetColumn(identity,1);wordmark.Children.Add(identity);heading.Child=wordmark;root.Children.Add(heading);

        var content=new StackPanel{Spacing=18,Margin=new Thickness(24,18)};
        information.Text=InformationText;
        AutomationProperties.SetName(information,english?"Build information":"Informations de compilation");
        content.Children.Add(information);
        content.Children.Add(new TextBlock{Text=english?"Lecteur Word is software imagined by Le Sire De Grave, designed and developed with assistance from ChatGPT, an artificial intelligence by OpenAI.\n\nOffered free of charge by « Le Sire De Grave ».\n\n© 2026 Le Sire De Grave.":OfficialCredits,FontSize=13,TextWrapping=TextWrapping.Wrap,Name="AboutCredits"});
        content.Children.Add(new TextBlock{Text=english?"Project license: to be defined":"Licence du projet : à définir",FontSize=13,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap,Name="AboutProjectLicense"});
        var repository=new Button{Content=english?"Project on GitHub ↗":"Le projet sur GitHub ↗",HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,8),Name="AboutRepository"};
        AutomationProperties.SetName(repository,english?"Open the project repository on GitHub":"Ouvrir le dépôt du projet sur GitHub");
        ToolTip.SetTip(repository,RepositoryUri.AbsoluteUri);
        repository.Click+=async (_,_)=>
        {
            try{if(!await Launcher.LaunchUriAsync(RepositoryUri))SetFeedback(english?"The link could not be opened":"Impossible d’ouvrir le lien");}
            catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){SetFeedback(english?"The link could not be opened":"Impossible d’ouvrir le lien");}
        };
        content.Children.Add(repository);
        var noticeHeading=new TextBlock{Text=english?"Third-party components and notices":"Composants tiers et notices",TextWrapping=TextWrapping.Wrap};
        var thirdParty=new Expander{Header=noticeHeading,HorizontalAlignment=HorizontalAlignment.Stretch,Name="AboutNotices"};
        AutomationProperties.SetName(thirdParty,noticeHeading.Text);
        thirdParty.Loaded+=(_,_)=>{foreach(var toggle in thirdParty.GetVisualDescendants().OfType<ToggleButton>())AutomationProperties.SetName(toggle,noticeHeading.Text);};
        SizeChanged+=(_,_)=>noticeHeading.MaxWidth=Math.Max(120,ClientSize.Width-150);
        // Large native notices are instantiated on request, keeping the ordinary About view lightweight.
        thirdParty.Expanding+=(_,_)=>
        {
            if(thirdParty.Content is not null)return;
            var text=new TextBox{Text=ThirdPartyNotices,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=260,FontSize=11,Name="AboutNoticesText"};
            AutomationProperties.SetName(text,english?"Full third-party license notices":"Notices complètes des licences tierces");
            thirdParty.Content=text;
        };
        content.Children.Add(thirdParty);
        var scroll=new ScrollViewer{Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};
        Grid.SetRow(scroll,1);root.Children.Add(scroll);

        var footer=new StackPanel{Spacing=8,Margin=new Thickness(24,14)};
        var footerButtons=new WrapPanel{Orientation=Orientation.Horizontal,ItemSpacing=8,LineSpacing=8};
        CopyButton=new Button{Content=english?"Copy information":"Copier les informations",Padding=new Thickness(12,9),HorizontalAlignment=HorizontalAlignment.Left,Name="AboutCopy"};
        AutomationProperties.SetName(CopyButton,english?"Copy complete build information":"Copier les informations complètes du build");
        CopyButton.Click+=async (_,_)=>await CopyInformationAsync();footerButtons.Children.Add(CopyButton);
        var close=new Button{Content=english?"Close":"Fermer",Padding=new Thickness(12,9),IsCancel=true,Name="AboutClose"};
        close.Click+=(_,_)=>Close();footerButtons.Children.Add(close);
        footer.Children.Add(footerButtons);footer.Children.Add(copyStatus);
        actions.Child=footer;Grid.SetRow(actions,2);root.Children.Add(actions);Content=root;
        feedbackTimer.Tick+=(_,_)=>{copyStatus.Text="";feedbackTimer.Stop();};
        Closed+=(_,_)=>feedbackTimer.Stop();
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        ActualThemeVariantChanged+=(_,_)=>ApplyPalette();ApplyPalette();
    }

    public async Task<bool> CopyInformationAsync()
    {
        try
        {
            if(Clipboard is null){SetFeedback(english?"Clipboard unavailable":"Presse-papiers indisponible");return false;}
            await Clipboard.SetTextAsync(InformationText);
            SetFeedback(english?"Reference copied":"Référence copiée");return true;
        }
        catch(Exception ex) when(ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or NotSupportedException)
        {SetFeedback(english?"Clipboard unavailable":"Presse-papiers indisponible");return false;}
    }

    void SetFeedback(string text){copyStatus.Text=text;feedbackTimer.Stop();feedbackTimer.Start();}
    void ApplyPalette()
    {
        bool dark=ActualThemeVariant==ThemeVariant.Dark;
        Background=new SolidColorBrush(Color.Parse(dark?"#111827":"#F8FAFC"));
        Foreground=new SolidColorBrush(Color.Parse(dark?"#E5E7EB":"#172033"));
        heading.Background=actions.Background=new SolidColorBrush(Color.Parse(dark?"#182235":"#FFFFFF"));
        copyStatus.Foreground=new SolidColorBrush(Color.Parse(dark?"#A5B4FC":"#3156A5"));
    }

    static string ReadNotices()
    {
        var assembly=typeof(AboutWindow).Assembly;
        return string.Join("\n\n",assembly.GetManifestResourceNames().Where(n=>n.StartsWith("LecteurWord.Notices.",StringComparison.Ordinal)).OrderBy(n=>n,StringComparer.Ordinal).Select(n=>
        {
            using var stream=assembly.GetManifestResourceStream(n)!;
            using var reader=new StreamReader(stream);
            return "===== "+n["LecteurWord.Notices.".Length..]+" =====\n\n"+reader.ReadToEnd();
        }));
    }
}
