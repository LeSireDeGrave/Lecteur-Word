using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed class AboutWindow:Window
{
    public const string OfficialCredits="Lecteur Word est un logiciel imaginé par Le Sire De Grave, conçu et développé avec l'assistance de ChatGPT, une intelligence artificielle d'OpenAI.\n\nOffert gracieusement par « Le Sire De Grave ».\n\n© 2026 LeSireDeGrave.";
    public static Uri RepositoryUri { get; }=new("https://github.com/LeSireDeGrave/Lecteur-Word");
    static readonly Lazy<string> notices=new(ReadNotices);
    static readonly Lazy<string> branding=new(() => ReadEmbeddedText("LecteurWord.Branding.txt"));
    static readonly Lazy<string> projectLicense=new(() => ReadEmbeddedText("LecteurWord.ProjectLicense.txt"));
    readonly Func<bool> isEnglish;
    readonly Func<string,string,string> localize;
    readonly TextBlock copyStatus=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Name="AboutCopyStatus"};
    readonly DispatcherTimer feedbackTimer=new(){Interval=TimeSpan.FromSeconds(3)};
    readonly Border heading=new(),actions=new();
    readonly SelectableTextBlock information=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Name="AboutBuildInformation"};
    readonly TextBlock novaFormat=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Name="AboutNovaFormat"};
    public Button CopyButton { get; }
    public string InformationText=>BuildReference.Current.FullReference(isEnglish());
    public string CopyStatusText=>copyStatus.Text??"";
    public static string ThirdPartyNotices=>notices.Value;

    public AboutWindow(bool english=false):this(() => english,(fr,en) => english ? en : fr) { }

    internal AboutWindow(Func<bool> isEnglish, Func<string,string,string> localize)
    {
        this.isEnglish = isEnglish;
        this.localize = localize;
        Title=T("À propos de Lecteur Word","About Lecteur Word");
        Icon=BrandAssets.WindowIcon;
        Name="AboutWindow";Width=620;Height=740;MinWidth=320;MinHeight=350;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        FontFamily=new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");
        var root=new Grid{RowDefinitions=new RowDefinitions("Auto,*,Auto")};
        var wordmark=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,*"),ColumnSpacing=16,Margin=new Thickness(24,22)};
        var logo=BrandAssets.CreateImage(72,true);
        logo.Name="AboutLogo";
        AutomationProperties.SetName(logo,"Lecteur Word");
        wordmark.Children.Add(logo);
        var identity=new StackPanel{Spacing=4,VerticalAlignment=VerticalAlignment.Center};
        identity.Children.Add(new TextBlock{Text="Lecteur Word",FontSize=25,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});
        identity.Children.Add(new TextBlock{Text=BuildReference.Current.Version is { } version ? "v"+version : T("Version indisponible","Version unavailable"),
            FontSize=13,TextWrapping=TextWrapping.Wrap,Name="AboutVersion"});
        identity.Children.Add(new TextBlock{Text="LeSireDeGrave",FontSize=13,TextWrapping=TextWrapping.Wrap,Name="AboutAuthor"});
        Grid.SetColumn(identity,1);wordmark.Children.Add(identity);heading.Child=wordmark;root.Children.Add(heading);

        var content=new StackPanel{Spacing=14,Margin=new Thickness(24,16)};
        content.Children.Add(CreateConversionOverview());
        RefreshBuildInformation();
        ReaderAppearance.Use(novaFormat,TextBlock.ForegroundProperty,ReaderAppearance.Secondary);
        content.Children.Add(novaFormat);
        var features=new UniformGrid{Name="AboutFeatures",Columns=2};
        foreach (string label in new[]{T("Lecture DOCX autonome","Standalone DOCX reader"),
            T("20 langues","20 languages"),T("Thèmes personnalisables","Customizable themes"),
            T("Extensions communautaires","Community extensions")})
            features.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,2,10,3),FontSize=13,TextWrapping=TextWrapping.Wrap});
        content.Children.Add(features);
        var licenseLine=new WrapPanel{ItemSpacing=12,LineSpacing=3};
        licenseLine.Children.Add(new TextBlock{Text=T("Licence MIT","MIT license"),FontSize=13,FontWeight=FontWeight.SemiBold,Name="AboutProjectLicense"});
        licenseLine.Children.Add(new TextBlock{Text=T("Logiciel libre","Open-source software"),FontSize=13,TextWrapping=TextWrapping.Wrap,Name="AboutOpenSource"});
        content.Children.Add(licenseLine);
        AutomationProperties.SetName(information,T("Informations de compilation","Build information"));
        content.Children.Add(new TextBlock{Text=T(OfficialCredits,"Lecteur Word is software imagined by Le Sire De Grave, designed and developed with assistance from ChatGPT, an artificial intelligence by OpenAI.\n\nOffered free of charge by « Le Sire De Grave ».\n\n© 2026 LeSireDeGrave."),FontSize=13,TextWrapping=TextWrapping.Wrap,Name="AboutCredits"});
        var repository=new Button{Content=T("Le projet sur GitHub ↗","Project on GitHub ↗"),HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,8),Name="AboutRepository"};
        AutomationProperties.SetName(repository,T("Ouvrir le dépôt du projet sur GitHub","Open the project repository on GitHub"));
        ToolTip.SetTip(repository,RepositoryUri.AbsoluteUri);
        repository.Click+=async (_,_)=>
        {
            try{if(!await Launcher.LaunchUriAsync(RepositoryUri))SetFeedback(T("Impossible d’ouvrir le lien","The link could not be opened"));}
            catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){SetFeedback(T("Impossible d’ouvrir le lien","The link could not be opened"));}
        };
        content.Children.Add(repository);
        content.Children.Add(LegalSection("AboutMitLicense",T("Texte intégral de la licence MIT","Full MIT license text"),
            "AboutMitLicenseText",()=>projectLicense.Value));
        content.Children.Add(LegalSection("AboutNotices",T("Composants tiers et notices légales","Third-party components and legal notices"),
            "AboutNoticesText",()=>branding.Value+"\n\n"+ThirdPartyNotices));
        var technical=Section("AboutTechnical",T("Informations techniques","Technical information"));
        technical.Content=information;
        content.Children.Add(technical);
        var scroll=new ScrollViewer{Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};
        Grid.SetRow(scroll,1);root.Children.Add(scroll);

        var footer=new StackPanel{Spacing=8,Margin=new Thickness(24,14)};
        var footerButtons=new WrapPanel{Orientation=Orientation.Horizontal,ItemSpacing=8,LineSpacing=8};
        CopyButton=new Button{Content=T("Copier les informations techniques","Copy technical information"),Padding=new Thickness(12,9),HorizontalAlignment=HorizontalAlignment.Left,Name="AboutCopy"};
        AutomationProperties.SetName(CopyButton,T("Copier les informations complètes du build","Copy complete build information"));
        CopyButton.Click+=async (_,_)=>await CopyInformationAsync();footerButtons.Children.Add(CopyButton);
        var close=new Button{Content=T("Fermer","Close"),Padding=new Thickness(12,9),IsCancel=true,Name="AboutClose"};
        close.Click+=(_,_)=>Close();footerButtons.Children.Add(close);
        footer.Children.Add(footerButtons);footer.Children.Add(copyStatus);
        actions.Child=footer;Grid.SetRow(actions,2);root.Children.Add(actions);Content=root;
        feedbackTimer.Tick+=(_,_)=>{copyStatus.Text="";feedbackTimer.Stop();};
        Closed+=(_,_)=>feedbackTimer.Stop();
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        _=ReaderAppearance.Current;
        ReaderAppearance.Use(heading,Border.BackgroundProperty,ReaderAppearance.Toolbar);
        ReaderAppearance.Use(actions,Border.BackgroundProperty,ReaderAppearance.Toolbar);
        ReaderAppearance.Use(copyStatus,TextBlock.ForegroundProperty,ReaderAppearance.Accent);
    }

    Control CreateConversionOverview()
    {
        var formats=new UniformGrid{Name="AboutFormats",Columns=2};
        var items=new[]
        {
            (ExportFormat.Pdf,"PDF",T("Partager et imprimer","Share and print")),
            (ExportFormat.Odt,"ODT",T("Modifier librement","Edit freely")),
            (ExportFormat.Epub3,"EPUB 3",T("Lire et publier","Read and publish")),
            (ExportFormat.Nova,"NOVA",T("Écrire dans OpusNova","Write in OpusNova"))
        };
        foreach(var (format,label,description) in items)
        {
            var icon=Icons.Make(Icons.ForExportFormat(format));
            icon.VerticalAlignment=VerticalAlignment.Top;
            ReaderAppearance.Use(icon,TextElement.ForegroundProperty,ReaderAppearance.Accent);
            var caption=new StackPanel{Spacing=3,Children={
                new TextBlock{Text=label,FontWeight=FontWeight.SemiBold},
                new TextBlock{Text=description,FontSize=12,TextWrapping=TextWrapping.Wrap}}};
            Grid.SetColumn(caption,1);
            var tile=new Border{Name="AboutFormat"+format,Margin=new Thickness(0,0,5,5),
                Padding=new Thickness(9),CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1),
                Child=new Grid{ColumnDefinitions=new("Auto,*"),ColumnSpacing=7,Children={icon,caption}}};
            ReaderAppearance.Use(tile,Border.BackgroundProperty,ReaderAppearance.Surface);
            ReaderAppearance.Use(tile,Border.BorderBrushProperty,ReaderAppearance.Line);
            formats.Children.Add(tile);
        }
        var summary=new TextBlock{Text="DOCX → PDF / ODT / EPUB 3 / NOVA",Name="AboutConversion",
            FlowDirection=FlowDirection.LeftToRight,TextWrapping=TextWrapping.Wrap,FontSize=15,FontWeight=FontWeight.SemiBold};
        var overview=new Border{Name="AboutConversionOverview",Padding=new Thickness(12),CornerRadius=new CornerRadius(7),
            Child=new StackPanel{Spacing=9,Children={summary,formats}}};
        ReaderAppearance.Use(overview,Border.BackgroundProperty,ReaderAppearance.Toolbar);
        return overview;
    }

    Expander Section(string name,string title)
    {
        var label=new TextBlock{Text=title,TextWrapping=TextWrapping.Wrap};
        var section=new Expander{Name=name,Header=label,IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch};
        AutomationProperties.SetName(section,title);
        section.Loaded+=(_,_)=>{foreach(var toggle in section.GetVisualDescendants().OfType<ToggleButton>())AutomationProperties.SetName(toggle,label.Text);};
        SizeChanged+=(_,_)=>label.MaxWidth=Math.Max(115,ClientSize.Width-145);
        return section;
    }

    Expander LegalSection(string name,string title,string textName,Func<string> loadText)
    {
        var section=Section(name,title);
        section.Expanding+=(_,_)=>
        {
            if(section.Content is not null)return;
            var text=new TextBox{Name=textName,Text=loadText(),IsReadOnly=true,AcceptsReturn=true,
                TextWrapping=TextWrapping.Wrap,Height=240,FontSize=11,FlowDirection=FlowDirection.LeftToRight};
            AutomationProperties.SetName(text,title);
            section.Content=text;
        };
        return section;
    }

    public async Task<bool> CopyInformationAsync()
    {
        try
        {
            if(Clipboard is null){SetFeedback(T("Presse-papiers indisponible","Clipboard unavailable"));return false;}
            await Clipboard.SetTextAsync(InformationText);
            SetFeedback(T("Référence copiée","Reference copied"));return true;
        }
        catch(Exception ex) when(ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or NotSupportedException)
        {SetFeedback(T("Presse-papiers indisponible","Clipboard unavailable"));return false;}
    }

    string T(string fr,string en)=>localize(fr,en);

    void SetFeedback(string text){copyStatus.Text=text;feedbackTimer.Stop();feedbackTimer.Start();}
    internal void RefreshBuildInformation()
    {
        information.Text=InformationText;
        novaFormat.Text=string.Format(CultureInfo.InvariantCulture,T("Format d'export OpusNova : NOVA (schéma v{0})",
            "OpusNova export format: NOVA (schema v{0})"),NovaPublication.Version);
    }

    static string ReadNotices()
    {
        var assembly=typeof(AboutWindow).Assembly;
        return string.Join("\n\n",assembly.GetManifestResourceNames().Where(n=>n.StartsWith("LecteurWord.Notices.",StringComparison.Ordinal)).OrderBy(n=>n,StringComparer.Ordinal)
            .Select(n=>"===== "+n["LecteurWord.Notices.".Length..]+" =====\n\n"+ReadEmbeddedText(n)));
    }

    static string ReadEmbeddedText(string name)
    {
        using var stream=typeof(AboutWindow).Assembly.GetManifestResourceStream(name)!;
        using var reader=new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
