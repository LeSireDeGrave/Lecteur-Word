using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Platform;
using Avalonia.Styling;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

namespace LecteurWord;
public sealed partial class ReaderWindow:Window
{
    readonly Grid body=new(){ColumnDefinitions=new ColumnDefinitions("220,5,*,0,0")};
    readonly StackPanel navigation=new(){Spacing=8,Margin=new Thickness(16)};
    readonly StackPanel navItems=new(){Spacing=6};
    readonly Border sidebar=new(),stage=new(),header=new(),toolbarBorder=new(),footer=new();
    readonly TextBlock fileLabel=new(){Text="Lecteur Word",FontSize=12,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    readonly TextBlock status=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock buildLabel=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=142,Name="BuildReference"};
    readonly TextBlock copyConfirmation=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center,IsVisible=false,Name="CopyConfirmation"};
    readonly Avalonia.Threading.DispatcherTimer copyTimer=new(){Interval=TimeSpan.FromSeconds(3)};
    readonly TextBlock pageLabel=new(){Name="PageStatus",VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock warning=new(){Name="ReadingBanner",TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(16,8)};
    readonly TextBox query=new(){PlaceholderText="Rechercher / Search",Name="SearchBox"};
    readonly Image paper=new(){Name="CalculatedPage",Stretch=Stretch.Uniform};
    readonly Border paperFrame=new(){Background=Brushes.White,Padding=new Thickness(0),BoxShadow=new BoxShadows(new BoxShadow{Blur=16,Color=Color.Parse("#25000000"),OffsetY=3})};
    readonly ScrollViewer viewport=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    readonly TextBlock empty=new(){FontSize=24,TextWrapping=TextWrapping.Wrap,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,MaxWidth=480,TextAlignment=TextAlignment.Center};
    readonly Button previous,next,export,zoomOut,zoomIn,diagnostics,cancel,fitWidth;
    bool fittingWidth;
    readonly Control[] sizeDependentControls;
    ComposedPage? rasterPage;float rasterScale;
    readonly SemaphoreSlim loader=new(1,1);
    readonly List<(Button Button,string Fr,string En)> labels=[];
    readonly List<Bitmap> thumbnails=[];
    CancellationTokenSource? pending,exportCancellation;
    InspectionResult? inspection;FlowDocument? flow;Composition? composition;Bitmap? pageBitmap;
    PageCompositionLease? nativePageLease;
    internal bool PreferNativePages {get;set;}=Environment.GetEnvironmentVariable("LECTEUR_WORD_PAGE_PROFILE")=="native";
    internal string? NativePageStatus {get;private set;}
    IReadOnlyList<PagePlacementGap> nativePageGaps=[];
    IReadOnlyList<FlowIssue> nativePageIssues=[];
    Dictionary<ExportFormat,ExportPreflight> preflights=[];
    int page;double zoom=1;bool english,navVisible=true,initialized,closed;
    readonly ThemePreference? themePreference;
    ReaderTheme themeChoice;
    IPlatformSettings? themePlatform;
    readonly Button chocolateTheme,blueTheme,systemTheme;
    string navMode="pages",fileName="";
    public InspectionResult? Inspection=>inspection;
    public Composition? CurrentComposition=>composition;
    public int CurrentPage=>composition?.Available==true&&!simplified?page+1:0;
    public double Zoom=>IsSimplifiedReading?readingZoom:zoom;
    public string Language=>english?"en":"fr";
    public bool NavigationVisible=>navVisible;
    public bool ExportAvailable(ExportFormat format)=>preflights.TryGetValue(format,out var check)&&check.Allowed;
    string T(string fr,string en)=>english?en:fr;
    public ReaderWindow():this(null){}

    internal ReaderWindow(ThemePreference? preference)
    {
        themePreference=preference;
        Title="Lecteur Word";Icon=BrandAssets.WindowIcon;Width=1240;Height=850;MinWidth=640;MinHeight=500;
        FontFamily=new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");FontSize=13;
        var root=new Grid{RowDefinitions=new RowDefinitions("43,Auto,*,Auto")};Content=root;
        var open=IconButton("open","Ouvrir un DOCX (Ctrl+O)","Open DOCX (Ctrl+O)",ChooseOpen);
        var properties=IconButton("info","Propriétés du document","Document properties",()=>{ShowPanel("properties");return Task.CompletedTask;});
        properties.Name="DocumentProperties";
        export=IconButton("export","Exporter","Export",()=>Task.CompletedTask);
        AddGroup(open,properties,export);

        var outline=IconButton("nav","Sommaire","Outline",()=>{ShowPanel("outline");return Task.CompletedTask;});
        var find=IconButton("search","Rechercher (Ctrl+F)","Search (Ctrl+F)",()=>{ShowPanel("search");query.Focus();return Task.CompletedTask;});
        var bookmarks=IconButton("bookmark","Signets du document","Document bookmarks",()=>{ShowPanel("bookmarks");return Task.CompletedTask;});
        previous=IconButton("prev","Page précédente (Alt+←)","Previous page (Alt+←)",()=>{GoToPage(page-1);return Task.CompletedTask;});
        next=IconButton("next","Page suivante (Alt+→)","Next page (Alt+→)",()=>{GoToPage(page+1);return Task.CompletedTask;});
        pageActions.Children.Add(previous);pageActions.Children.Add(next);
        AddGroup(outline,find,bookmarks,pageActions);

        rulerToggle=IconButton("ruler","Afficher ou masquer la règle (F7)","Toggle ruler (F7)",()=>{ToggleRuler();return Task.CompletedTask;});rulerToggle.Name="ToggleRuler";
        marksToggle=IconButton("marks","Afficher ou masquer les marques de mise en forme","Toggle formatting marks",()=>{ToggleMarks();return Task.CompletedTask;});marksToggle.Name="ToggleNonprinting";
        var fitPage=IconButton("fit","Ajuster à la largeur (Ctrl+9)","Fit to width (Ctrl+9)",()=>{FitToWidth();return Task.CompletedTask;});fitPage.Name="TopFitWidth";
        chocolateTheme=IconButton("sun","Chocolat & Crème","Chocolate & Cream",()=>{SetTheme(1);return Task.CompletedTask;});
        blueTheme=IconButton("palette","Bleu Microsoft","Microsoft Blue",()=>{SetTheme(2);return Task.CompletedTask;});
        systemTheme=IconButton("system","Système","System",()=>{SetTheme(0);return Task.CompletedTask;});
        chocolateTheme.Name="ThemeChocolate";blueTheme.Name="ThemeMicrosoftBlue";systemTheme.Name="ThemeSystem";
        AddGroup(readingMode,rulerToggle,marksToggle,fitPage,chocolateTheme,blueTheme,systemTheme);

        navigationToggle=IconButton("sidebar","Afficher ou masquer la navigation (F9)","Toggle navigation (F9)",()=>{ToggleNavigation();return Task.CompletedTask;});navigationToggle.Name="ToggleNavigation";
        panelToggle=IconButton("drawer","Afficher ou masquer les éléments (F8)","Toggle document elements (F8)",()=>{ToggleElements();return Task.CompletedTask;});panelToggle.Name="ToggleElements";
        cancel=IconButton("cancel","Annuler l’opération","Cancel operation",()=>{pending?.Cancel();exportCancellation?.Cancel();return Task.CompletedTask;});cancel.IsVisible=false;
        AddGroup(navigationToggle,panelToggle,cancel);

        zoomOut=IconButton("minus","Réduire (Ctrl+-)","Zoom out (Ctrl+-)",()=>{SetZoom(Zoom-.1);return Task.CompletedTask;});
        zoomIn=IconButton("plus","Agrandir (Ctrl++)","Zoom in (Ctrl++)",()=>{SetZoom(Zoom+.1);return Task.CompletedTask;});
        fitWidth=IconButton("fit","Ajuster à la largeur (Ctrl+9)","Fit to width (Ctrl+9)",()=>{FitToWidth();return Task.CompletedTask;});
        var copyBuild=IconButton("copy","Copier la référence du build","Copy build reference",CopyBuildReferenceAsync);copyBuild.Name="CopyBuildReference";
        diagnostics=IconButton("info","À propos de Lecteur Word","About Lecteur Word",async()=>await new AboutWindow(english){RequestedThemeVariant=RequestedThemeVariant}.ShowDialog(this));diagnostics.Name="AboutLecteurWord";
        var footerProperties=IconButton("properties","Propriétés du document","Document properties",()=>{ShowPanel("properties");return Task.CompletedTask;});
        footerProperties.Name="FooterDocumentProperties";

        InitializeWorkspace(root);InitializeFooter(root,footerProperties,copyBuild);
        sizeDependentControls=[fitPage];
        copyTimer.Tick+=(_,_)=>{copyConfirmation.IsVisible=false;copyTimer.Stop();};
        query.TextChanged+=(_,_)=>{if(navMode=="search")FillSearch();};
        viewport.SizeChanged+=(_,_)=>{if(fittingWidth&&composition?.Available==true&&!simplified)FitToWidth();UpdateRuler();};
        AddHandler(KeyDownEvent,OnWorkspaceKeys,RoutingStrategies.Tunnel);
        KeyDown+=OnKeys;Closed+=(_,_)=>{closed=true;copyTimer.Stop();typographyCancellation?.Cancel();pending?.Cancel();exportCancellation?.Cancel();pageBitmap?.Dispose();nativePageLease?.Dispose();foreach(var thumb in thumbnails)thumb.Dispose();};
        ActualThemeVariantChanged+=(_,_)=>{if(initialized)PaintTheme();};
        themeChoice=themePreference?.Load()??ReaderTheme.System;
        themePlatform=Application.Current?.PlatformSettings;
        if(themePlatform is not null)themePlatform.ColorValuesChanged+=OnSystemColorsChanged;
        Closed+=(_,_)=>{if(themePlatform is not null)themePlatform.ColorValuesChanged-=OnSystemColorsChanged;};
        initialized=true;ApplyTheme();UpdateUi();
    }
    public async Task CopyBuildReferenceAsync()
    {
        if(Clipboard is not {} clipboard){status.Text=T("Presse-papiers indisponible","Clipboard unavailable");return;}
        try
        {
            await clipboard.SetTextAsync(BuildReference.Current.FullReference(english));
            copyConfirmation.Text=T("Référence copiée","Reference copied");copyConfirmation.IsVisible=true;copyTimer.Stop();copyTimer.Start();
        }
        catch(Exception){status.Text=T("Copie indisponible","Copy unavailable");}
    }
    Button IconButton(string icon,string fr,string en,Func<Task> action)
    {
        var b=new Button{Content=Icons.Make(icon),Width=32,Height=31,Padding=new Thickness(6),CornerRadius=new CornerRadius(5),Background=Brushes.Transparent,BorderThickness=new Thickness(0),Name="Action_"+icon+"_"+labels.Count};
        labels.Add((b,fr,en));ToolTip.SetTip(b,T(fr,en));AutomationProperties.SetName(b,T(fr,en));
        b.Click+=async(_,_)=>{try{await action();}catch(Exception){status.Text=T("Action interrompue — consulter les diagnostics","Action interrupted — check diagnostics");}};return b;
    }
    async Task ChooseOpen()
    {
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title=T("Ouvrir un document Word","Open a Word document"),AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("DOCX"){Patterns=["*.docx"]},FilePickerFileTypes.All]});
        if(files.Count>0 && files[0].TryGetLocalPath() is {} path)await OpenDocumentAsync(path);
    }
    public async Task OpenDocumentAsync(string path)
    {
        typographyCancellation?.Cancel();pending?.Cancel();var cancellation=new CancellationTokenSource();pending=cancellation;cancel.IsVisible=true;
        PageCompositionLease? candidate=null;bool preferNative=PreferNativePages;
        status.Text=T("Lecture et inspection…","Reading and inspecting…");
        try
        {
            await loader.WaitAsync(cancellation.Token);
            try
            {
                var data=await Task.Run(()=>
                {
                    var result=DocumentInspector.InspectDocument(path,cancellationToken:cancellation.Token);
                    var model=FlowReader.Read(result);
                    var reading=ReadableReader.Read(result,cancellation.Token);
                    bool hasTables=reading.Available&&reading.Presentation.BodyBlocks.Any(b=>b.Kind=="table");
                    if(preferNative||hasTables)candidate=PageComposer.Compose(result,cancellation.Token,hasTables);
                    var proposed=candidate?.Snapshot;
                    bool adopt=(preferNative||hasTables)&&proposed is {Coverage:PageCoverage.Complete,Composition.Available:true,RulerModel:not null};
                    var pageModel=adopt?proposed!.RulerModel!:LayoutReader.Read(result,cancellation.Token);
                    var layout=adopt?proposed!.Composition:Composer.Compose(pageModel,cancellation.Token);
                    var index=ReaderDocumentIndex.Build(result,reading,layout,cancellation.Token,proposed?.Semantic);
                    var checks=Enum.GetValues<ExportFormat>().ToDictionary(f=>f,f=>DocumentExports.Preflight(result,f));
                    return(result,model,layout,pageModel,reading,index,checks,nativeStatus:proposed?.Coverage.ToString(),
                        gaps:proposed?.Gaps??[],nativeIssues:adopt?[]:proposed?.Composition.Issues??[]);
                },cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();if(closed)return;
                readingList.ItemsSource=null;foreach(var image in readingImages.Values)image.Dispose();readingImages.Clear();readingImagePixels=0;
                nativePageLease?.Dispose();nativePageLease=candidate;candidate=null;
                NativePageStatus=data.nativeStatus;SetNativeSelection(0,[]);
                nativePageGaps=data.gaps;nativePageIssues=data.nativeIssues;
                rasterPage=null;inspection=data.result;flow=data.model;composition=data.layout;pageModel=data.layout.Available?data.pageModel:null;
                readable=data.reading;typographyDiagnostics=null;documentIndex=data.index;preflights=data.checks;fileName=Path.GetFileName(path);
                page=0;collapsedOutline.Clear();visibleBlockId=null;CurrentBlockId=null;CurrentNoteReferenceId=null;CurrentSection=1;ActiveReadingContext=null;
                simplified=!data.layout.Available;readingList.ItemsSource=readable.Presentation.BodyBlocks;
                PreparePageAids();SelectPageParagraph();RefreshReadingAppearance();RefreshElements(reset:true);UpdateUi();
            }
            finally{loader.Release();}
        }
        catch(OperationCanceledException){if(pending==cancellation&&!closed){UpdateUi();status.Text=T("Lecture annulée ; document précédent conservé","Inspection cancelled; previous document retained");}}
        catch(Exception){if(pending==cancellation&&!closed)status.Text=T("Rendu interrompu — contenu hors limites","Rendering stopped — resource or format limit");}
        finally{candidate?.Dispose();cancellation.Dispose();if(pending==cancellation)pending=null;cancel.IsVisible=pending is not null||exportCancellation is not null;}
    }
    public void GoToPage(int index)
    {
        if(composition?.Available!=true)return;
        simplified=false;UpdateReadingMode();page=Math.Clamp(index,0,composition.Pages.Count-1);
        CurrentSection=composition.Pages[page].Section;CurrentBlockId=null;
        SelectPageParagraph();ShowPage();UpdateActions();UpdateNavigationPosition();if(navMode=="pages")FillPanel();
    }
    public void SetZoom(double value)
    {
        if(IsSimplifiedReading)
        {
            readingZoom=Math.Clamp(value,.8,1.7);SetReadingTemplate();UpdateFacts(false);return;
        }
        fittingWidth=false;ApplyPageZoom(value);
    }
    void ApplyPageZoom(double value)
    {
        zoom=Math.Clamp(value,.3,2);
        var geometry=composition?.Pages.ElementAtOrDefault(page)?.Geometry??new PageGeometry();
        paper.Width=geometry.Width*PointsToDip*zoom;paper.Height=geometry.Height*PointsToDip*zoom;
        ToolTip.SetTip(paper,$"{zoom:P0}");RenderPageBitmap();UpdateFacts(CurrentPage>0);UpdateRuler();UpdateNativeSelection();UpdatePageMarks();
    }
    public void FitToWidth()
    {
        if(CurrentPage==0)return;
        double width=viewport.Viewport.Width>0?viewport.Viewport.Width:viewport.Bounds.Width;
        if(width<=0)return;
        fittingWidth=true;ApplyPageZoom(Math.Max(1,width-2*PageInset-3)/(composition!.Pages[page].Geometry.Width*PointsToDip));
    }
    public void ToggleNavigation()
    {
        if(navVisible&&!sidebar.IsVisible)preferElements=false;
        else {navVisible=!navVisible;if(navVisible)preferElements=false;}
        UpdatePanels();
    }
    public void SetTheme(int value)
    {
        themeChoice=value switch{1=>ReaderTheme.Chocolate,2=>ReaderTheme.MicrosoftBlue,_=>ReaderTheme.System};
        ApplyTheme();
        if(themePreference?.Save(themeChoice)==false)
            status.Text=T("Thème appliqué · préférence non enregistrée","Theme applied · preference could not be saved");
    }

    void OnSystemColorsChanged(object? sender,PlatformColorValues colors)
    {
        void Update(){if(!closed&&themeChoice==ReaderTheme.System)ApplyTheme(colors.ThemeVariant);}
        if(Avalonia.Threading.Dispatcher.UIThread.CheckAccess())Update();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Update);
    }

    void ApplyTheme(PlatformThemeVariant? platform=null)
    {
        var previous=ActualThemeVariant;
        RequestedThemeVariant=ThemePreference.Resolve(themeChoice,
            platform??themePlatform?.GetColorValues().ThemeVariant??PlatformThemeVariant.Light);
        if(previous==ActualThemeVariant)PaintTheme();
        foreach(var window in OwnedWindows)window.RequestedThemeVariant=RequestedThemeVariant;
    }

    ReaderPalette Palette=>ReaderPalette.For(ActualThemeVariant);
    public void SetLanguage(bool useEnglish){english=useEnglish;foreach(var item in labels){ToolTip.SetTip(item.Button,T(item.Fr,item.En));AutomationProperties.SetName(item.Button,T(item.Fr,item.En));}UpdateThemeSelection();RefreshReadingAppearance();RefreshElements();UpdateUi();}
    void PaintTheme()
    {
        header.Background=Palette.Header;footer.Background=Palette.Footer;
        header.SetValue(TextElement.ForegroundProperty,Palette.HeaderInk);
        toolbarBorder.Background=Palette.Toolbar;
        footer.BorderBrush=toolbarBorder.BorderBrush=FineLine;
        toolbarBorder.BorderThickness=new Thickness(0,1,0,1);footer.BorderThickness=new Thickness(0,1,0,0);
        sidebar.Background=documentPanel.Background=PanelColor;
        sidebar.BorderBrush=documentPanel.BorderBrush=FineLine;sidebar.BorderThickness=new Thickness(0,0,1,0);documentPanel.BorderThickness=new Thickness(1,0,0,0);
        stage.Background=Palette.Stage;Foreground=Palette.Ink;
        fileLabel.Foreground=Palette.HeaderMuted;buildLabel.Foreground=warning.Foreground=SecondaryInk;
        navigationSplitter.Background=elementSplitter.Background=FineLine;
        foreach(var divider in groupDividers)divider.Background=FineLine;
        foreach(var item in labels)
            if(item.Button.Content is Viewbox vb && vb.Child is Avalonia.Controls.Shapes.Path path)
            {
                bool inHeader=item.Button.Name=="LanguageToggle";
                path.Stroke=inHeader?Palette.HeaderInk:Foreground;
                if(inHeader)PaintHeaderAction(item.Button);
            }
        UpdateThemeSelection();
        pageMarks.SetInk(ReferenceEquals(Palette,ReaderPalette.Blue)?Palette.Accent:Brush("#795238"));
        export.Background=AccentFill;PaintElementTheme();UpdateToggle(marksToggle,marksRequested);UpdatePanels();UpdateRuler();
        if(initialized){RefreshReadingAppearance();RefreshElements();FillPanel();}
    }

    void UpdateThemeSelection()
    {
        UpdateToggle(chocolateTheme,themeChoice==ReaderTheme.Chocolate);
        UpdateToggle(blueTheme,themeChoice==ReaderTheme.MicrosoftBlue);
        UpdateToggle(systemTheme,themeChoice==ReaderTheme.System);
        foreach(var action in new[]{chocolateTheme,blueTheme,systemTheme})
            AutomationProperties.SetItemStatus(action,action.Classes.Contains("active")?T("Sélectionné","Selected"):"");
    }

    void PaintHeaderAction(Button button)
    {
        if(ActualThemeVariant==ReaderPalette.BlueVariant)
        {
            button.Resources["ButtonBackgroundPointerOver"]=ReaderPalette.BluePrimary;
            button.Resources["ButtonBackgroundPressed"]=ReaderPalette.BlueHeaderPressed;
            button.Resources["SystemControlFocusVisualPrimaryBrush"]=ReaderPalette.BlueLight;
        }
        else
        {
            button.Resources.Remove("ButtonBackgroundPointerOver");
            button.Resources.Remove("ButtonBackgroundPressed");
            button.Resources.Remove("SystemControlFocusVisualPrimaryBrush");
        }
    }
    void UpdateUi()
    {
        buildLabel.Text=BuildReference.Current.CompactReference(english);ToolTip.SetTip(buildLabel,BuildReference.Current.FullReference(english));
        if(copyConfirmation.IsVisible)copyConfirmation.Text=T("Référence copiée","Reference copied");
        fileLabel.Text=fileName;ToolTip.SetTip(fileLabel,fileName.Length==0?null:fileName);
        Title=fileName.Length==0?"Lecteur Word":fileName+" — Lecteur Word";
        bool available=composition?.Available==true;UpdateReadingMode();empty.IsVisible=!available&&readable?.Available!=true;
        empty.Text=inspection is null?T("Ouvrez un document DOCX","Open a DOCX document"):T("Lecture indisponible. Consultez les diagnostics.","Reading unavailable. Open diagnostics.");
        warning.Text=inspection is null?"":!inspection.Manifest.InspectionComplete?T("Inspection refusée · détails dans les diagnostics","Inspection refused · details in diagnostics"):
            !available&&readable?.Available!=true?T("Lecture et règle indisponibles","Reading and ruler unavailable"):
            simplified?T("Lecture simplifiée · règle indisponible","Simplified reading · ruler unavailable"):
            T("Pages calculées · pagination expérimentale","Calculated pages · experimental pagination");
        ToolTip.SetTip(warning,T("Fidélité à Microsoft Word non qualifiée. Consultez les propriétés et diagnostics du document.",
            "Microsoft Word fidelity is unqualified. See document properties and diagnostics."));
        warning.IsVisible=!string.IsNullOrWhiteSpace(warning.Text);
        status.Text="";
        if(available&&!simplified){SelectPageParagraph(CurrentBlockId);ShowPage();}
        UpdateActions();FillPanel();
        var choices=new StackPanel{Spacing=5,MinWidth=250};
        foreach(var format in Enum.GetValues<ExportFormat>())
        {
            bool allowed=ExportAvailable(format);
            var b=new Button{Content=format+" — "+(allowed?T("profil texte simple","plain text profile")+(format==ExportFormat.Pdf?T(" · pagination distincte"," · separate pagination"):""):T("Non qualifié","Not qualified")),HorizontalAlignment=HorizontalAlignment.Stretch,IsEnabled=allowed};
            AutomationProperties.SetName(b,b.Content.ToString());ToolTip.SetTip(b,allowed?T("Nouveau dossier de sortie, après précontrôle","New output folder, after preflight"):T("Contenu ou propriétés hors profil ; ouvrez les diagnostics","Content or properties outside profile; open diagnostics"));
            b.Click+=async(_,_)=>await SaveExport(format);choices.Children.Add(b);
        }
        export.Flyout=new Flyout{Content=choices};
    }
    async Task SaveExport(ExportFormat format)
    {
        if(inspection is null||!ExportAvailable(format)||exportCancellation is not null)return;
        // Capture the inspected source before the asynchronous destination picker.
        var source=inspection;
        using var cancellation=new CancellationTokenSource();exportCancellation=cancellation;export.IsEnabled=false;
        try
        {
            var folders=await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions{Title=T("Choisir un dossier de sortie","Choose an output folder"),AllowMultiple=false});
            if(folders.Count==0||folders[0].TryGetLocalPath() is not {} parent)return;
            cancel.IsVisible=true;
            string output=Path.Combine(parent,"LecteurWord-"+format+"-"+Guid.NewGuid().ToString("N")[..8]);
            string path=await Task.Run(()=>DocumentExports.Export(source,format,output,cancellation.Token));
            if(!closed)status.Text=T("Export créé : ","Export created: ")+path;
        }
        catch(OperationCanceledException){if(!closed)status.Text=T("Export annulé ; original conservé","Export cancelled; original preserved");}
        catch(Exception){if(!closed)status.Text=T("Export refusé ; aucune sortie finale créée","Export refused; no final output created");}
        finally{exportCancellation=null;cancel.IsVisible=pending is not null;if(!closed)UpdateActions();}
    }
    void ShowPage()
    {
        if(composition?.Available!=true)return;
        if(fittingWidth)FitToWidth();else ApplyPageZoom(zoom);viewport.Offset=new Vector(0,0);
    }
    void RenderPageBitmap()
    {
        if(composition?.Available!=true||simplified)return;
        var source=composition.Pages[page];float maximum=(float)Math.Min(3,Math.Sqrt(16_000_000d/(source.Geometry.Width*source.Geometry.Height)));
        float scale=Math.Clamp((float)(96d/72*zoom*RenderScaling),.5f,maximum);
        if(ReferenceEquals(rasterPage,source)&&Math.Abs(rasterScale-scale)<.03)return;
        using var stream=new MemoryStream(Composer.PagePng(source,scale));var bitmap=new Bitmap(stream);paper.Source=bitmap;pageBitmap?.Dispose();pageBitmap=bitmap;rasterPage=source;rasterScale=scale;
    }
    void UpdateActions()
    {
        bool available=CurrentPage>0;
        pageActions.IsVisible=available&&composition!.Pages.Count>1;
        previous.IsVisible=available&&page>0;next.IsVisible=available&&page<composition!.Pages.Count-1;
        foreach(var control in sizeDependentControls)control.IsVisible=available;
        export.IsEnabled=inspection?.Manifest.InspectionComplete==true&&exportCancellation is null;
        export.IsVisible=inspection?.Manifest.InspectionComplete==true;
        pageLabel.IsVisible=available||IsSimplifiedReading;
        pageLabel.Text=available?T($"Page {page+1} / {composition!.Pages.Count}",$"Page {page+1} / {composition!.Pages.Count}"):T("Lecture continue","Continuous reading");
        ToolTip.SetTip(pageLabel,available?T("Pages calculées par Lecteur Word","Pages calculated by Lecteur Word"):
            T("Lecture simplifiée, sans pagination calculée","Simplified reading, without calculated pages"));
        if(marksToggle is not null)marksToggle.IsVisible=available||IsSimplifiedReading;
        UpdateFacts(available);UpdateRuler();UpdateNativeSelection();UpdatePageMarks();
    }
    void ShowPanel(string mode)
    {
        CloseReadingContext();navMode=mode;navVisible=true;preferElements=false;UpdatePanels();FillPanel();
    }
    void FillPanel()
    {
        navigation.Children.Clear();outlineActions.Clear();
        foreach(var thumb in thumbnails)thumb.Dispose();thumbnails.Clear();
        navigation.Children.Add(new TextBlock{Text=T("NAVIGATION","NAVIGATION"),FontSize=11,FontWeight=FontWeight.Bold,Margin=new Thickness(0,4,0,1)});
        navigation.Children.Add(navPosition);UpdateNavigationPosition();
        foreach(var (key,fr,en) in new[]{("pages","Miniatures","Thumbnails"),("outline","Sommaire","Outline"),("bookmarks","Signets","Bookmarks"),("notes","Notes","Notes"),("sections","En-têtes et pieds","Headers and footers"),("search","Recherche","Search"),("diagnostics","Diagnostics","Diagnostics"),("properties","Propriétés","Properties")})
        {
            if(key=="pages"&&(composition?.Available!=true||simplified)||key=="notes"&&readable?.Presentation.Notes.Count is not >0||key=="sections"&&readable?.Presentation.Sections.Any(s=>s.Stories.Count>0)!=true)continue;
            var button=new Button{Content=T(fr,en),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,
                Background=navMode==key?AccentFill:Brushes.Transparent,BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(6),MinHeight=28,Padding=new Thickness(7,4)};
            button.Click+=(_,_)=>{navMode=key;FillPanel();};navigation.Children.Add(button);
        }
        navItems.Children.Clear();navigation.Children.Add(navItems);
        if(navMode=="search")
        {
            navigation.Children.Add(query);
            FillSearch();
        }
        else if(navMode is "outline" or "bookmarks")FillReadingNavigation(navMode=="bookmarks");
        else if(navMode=="notes")FillNotes();
        else if(navMode=="sections")FillSections();
        else if(navMode=="pages"&&simplified&&readable?.Available==true)
            FillReadingNavigation(false);
        else if(navMode=="pages"&&composition?.Available==true)
        {
            foreach(int i in Enumerable.Range(Math.Max(0,page-1),Math.Min(composition.Pages.Count-1,page+1)-Math.Max(0,page-1)+1))
            {
                using var s=new MemoryStream(Composer.PagePng(composition.Pages[i],.18f));var bitmap=new Bitmap(s);thumbnails.Add(bitmap);var image=new Image{Source=bitmap,Width=108,Height=108*composition.Pages[i].Geometry.Height/composition.Pages[i].Geometry.Width};
                var button=new Button{Content=new StackPanel{Children={image,new TextBlock{Text=(i+1).ToString(CultureInfo.InvariantCulture),HorizontalAlignment=HorizontalAlignment.Center}}},HorizontalAlignment=HorizontalAlignment.Center,BorderThickness=new Thickness(i==page?2:1),BorderBrush=i==page?ReferenceEquals(Palette,ReaderPalette.Blue)?Palette.Accent:Brush("#986749"):Brushes.Gray,Padding=new Thickness(4),Background=Brushes.White,Foreground=Brushes.Black};
                AutomationProperties.SetName(button,T("Page ","Page ")+(i+1));button.Click+=(_,_)=>GoToPage(i);navItems.Children.Add(button);
            }
        }
        else if(navMode=="diagnostics")
        {
            FillDiagnostics();
        }
        else if(navMode=="properties")FillProperties();
        else navItems.Children.Add(new TextBlock{Text=T("Aucun élément qualifié pour cette vue.","No qualified item for this view."),TextWrapping=TextWrapping.Wrap});
    }
    void FillSearch()
    {
        navItems.Children.Clear();
        if(query.Text is {Length:>0} needle&&FillNativePageSearch(needle))return;
        if(readable?.Available==true){FillReadableSearch();return;}
        if(flow?.CanRender==false){navItems.Children.Add(new TextBlock{Text=T("Recherche non qualifiée pour cette structure.","Search unqualified for this structure."),TextWrapping=TextWrapping.Wrap});return;}
            if(flow is not null&&!string.IsNullOrWhiteSpace(query.Text))
            {
                int count=0;foreach(var p in flow.Paragraphs.Where(p=>p.Text.Contains(query.Text,StringComparison.CurrentCultureIgnoreCase)).Take(100))
                {
                    count++;var b=new Button{Content=SearchExcerpt(p.Text),HorizontalAlignment=HorizontalAlignment.Stretch};
                    b.Click+=(_,_)=>{int index=composition?.Pages.ToList().FindIndex(pg=>pg.Lines.Any(l=>l.ParagraphId==p.Id))??-1;if(index>=0)GoToPage(index);};navItems.Children.Add(b);
                }
                navItems.Children.Add(new TextBlock{Text=T("Résultats affichés (max. 100) : ","Shown results (max. 100): ")+count,TextWrapping=TextWrapping.Wrap});
            }
    }
    static string SearchExcerpt(string text)
    {
        // Bound the excerpt by user-perceived characters, without scanning the whole paragraph.
        var elements=StringInfo.GetTextElementEnumerator(text);int end=0;
        for(int count=0;count<32&&elements.MoveNext();count++)end=elements.ElementIndex+elements.GetTextElement().Length;
        return end<text.Length?text[..end]+"…":text;
    }
    void OnWorkspaceKeys(object? sender,KeyEventArgs e)
    {
        if(e.KeyModifiers!=KeyModifiers.None)return;
        switch(e.Key)
        {
            case Key.F7 when CurrentPage>0:ToggleRuler();break;
            case Key.F8:ToggleElements();break;
            case Key.F9:ToggleNavigation();break;
            default:return;
        }
        e.Handled=true;
    }

    void OnKeys(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape&&notePane.IsVisible){DismissNote(false);e.Handled=true;return;}
        if(e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if(e.Key==Key.O){_=ChooseOpen();e.Handled=true;}
            else if(e.Key==Key.F){ShowPanel("search");query.Focus();e.Handled=true;}
            else if(e.Key is Key.OemPlus or Key.Add){SetZoom(Zoom+.1);e.Handled=true;}
            else if(e.Key is Key.OemMinus or Key.Subtract){SetZoom(Zoom-.1);e.Handled=true;}
            else if(e.Key==Key.D0){SetZoom(1);e.Handled=true;}
            else if(e.Key==Key.D9){FitToWidth();e.Handled=true;}
        }
        else if(!simplified&&e.KeyModifiers.HasFlag(KeyModifiers.Alt)&&e.Key==Key.Left){GoToPage(page-1);e.Handled=true;}
        else if(!simplified&&e.KeyModifiers.HasFlag(KeyModifiers.Alt)&&e.Key==Key.Right){GoToPage(page+1);e.Handled=true;}
        else if(e.Key==Key.Escape){pending?.Cancel();exportCancellation?.Cancel();}
    }
}
