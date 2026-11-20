using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

namespace LecteurWord;
public sealed partial class ReaderWindow:Window
{
    readonly Grid body=new(){ColumnDefinitions=new ColumnDefinitions("224,*")};
    readonly StackPanel navigation=new(){Spacing=8,Margin=new Thickness(16)};
    readonly StackPanel navItems=new(){Spacing=6};
    readonly Border sidebar=new(),stage=new(),header=new(),toolbarBorder=new(),footer=new();
    readonly StackPanel tools=new(){Orientation=Orientation.Horizontal,Spacing=5,Margin=new Thickness(14,10)};
    readonly TextBlock fileLabel=new(){Text="Lecteur Word",FontSize=14,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    readonly TextBlock status=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock buildLabel=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=220,Name="BuildReference"};
    readonly TextBlock copyConfirmation=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center,IsVisible=false,Name="CopyConfirmation"};
    readonly Avalonia.Threading.DispatcherTimer copyTimer=new(){Interval=TimeSpan.FromSeconds(3)};
    readonly TextBlock pageLabel=new(){MinWidth=88,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock warning=new(){TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(16,8)};
    readonly TextBox query=new(){PlaceholderText="Rechercher / Search",Name="SearchBox"};
    readonly Image paper=new(){Name="CalculatedPage",Stretch=Stretch.Uniform};
    readonly Border paperFrame=new(){Background=Brushes.White,Padding=new Thickness(0),BoxShadow=new BoxShadows(new BoxShadow{Blur=16,Color=Color.Parse("#25000000"),OffsetY=3})};
    readonly ScrollViewer viewport=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    readonly TextBlock empty=new(){FontSize=24,TextWrapping=TextWrapping.Wrap,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,MaxWidth=480,TextAlignment=TextAlignment.Center};
    readonly Button previous,next,export,zoomOut,zoomIn,diagnostics,cancel,fitWidth;
    bool fittingWidth;
    ComposedPage? rasterPage;float rasterScale;
    readonly SemaphoreSlim loader=new(1,1);
    readonly List<(Button Button,string Fr,string En)> labels=[];
    readonly List<Bitmap> thumbnails=[];
    CancellationTokenSource? pending,exportCancellation;
    InspectionResult? inspection;FlowDocument? flow;Composition? composition;Bitmap? pageBitmap;
    Dictionary<ExportFormat,ExportPreflight> preflights=[];
    int page,theme;double zoom=1;bool english,navVisible=true,initialized,closed;
    string navMode="pages",fileName="";
    public InspectionResult? Inspection=>inspection;
    public Composition? CurrentComposition=>composition;
    public int CurrentPage=>composition?.Available==true&&!simplified?page+1:0;
    public double Zoom=>zoom;
    public string Language=>english?"en":"fr";
    public bool NavigationVisible=>navVisible;
    public bool ExportAvailable(ExportFormat format)=>preflights.TryGetValue(format,out var check)&&check.Allowed;
    string T(string fr,string en)=>english?en:fr;
    public ReaderWindow()
    {
        Title="Lecteur Word";Icon=BrandAssets.WindowIcon;Width=1180;Height=850;MinWidth=820;MinHeight=600;
        FontFamily=new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");FontSize=13;
        var root=new Grid{RowDefinitions=new RowDefinitions("56,Auto,*,38")};Content=root;
        var heading=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,Auto,*") ,Margin=new Thickness(20,0)};
        heading.Children.Add(BrandAssets.CreateImage(34));
        var brand=new TextBlock{Text="Lecteur Word",FontWeight=FontWeight.Bold,FontSize=19,Margin=new Thickness(12,0,22,0),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(brand,1);heading.Children.Add(brand);
        Grid.SetColumn(fileLabel,2);heading.Children.Add(fileLabel);header.Child=heading;root.Children.Add(header);
        tools.Children.Add(IconButton("open","Ouvrir un DOCX (Ctrl+O)","Open DOCX (Ctrl+O)",async()=>await ChooseOpen()));
        var print=IconButton("print","Imprimer — Non qualifié","Print — Not qualified",()=>Task.CompletedTask);print.IsEnabled=false;tools.Children.Add(print);
        tools.Children.Add(IconButton("search","Rechercher (Ctrl+F)","Search (Ctrl+F)",()=>{ShowPanel("search");query.Focus();return Task.CompletedTask;}));
        tools.Children.Add(IconButton("nav","Afficher/masquer la navigation (F9)","Toggle navigation (F9)",()=>{ToggleNavigation();return Task.CompletedTask;}));
        var bookmarks=IconButton("bookmark","Signets du document","Document bookmarks",()=>{ShowPanel("bookmarks");return Task.CompletedTask;});tools.Children.Add(bookmarks);
        tools.Children.Add(new Border{Width=1,Margin=new Thickness(8,3),Background=Brushes.Gray});
        previous=IconButton("prev","Page précédente (Alt+←)","Previous page (Alt+←)",()=>{GoToPage(page-1);return Task.CompletedTask;});tools.Children.Add(previous);tools.Children.Add(pageLabel);
        next=IconButton("next","Page suivante (Alt+→)","Next page (Alt+→)",()=>{GoToPage(page+1);return Task.CompletedTask;});tools.Children.Add(next);
        zoomOut=IconButton("minus","Réduire (Ctrl+-)","Zoom out (Ctrl+-)",()=>{SetZoom(zoom-.1);return Task.CompletedTask;});tools.Children.Add(zoomOut);
        var zoomReset=new Button{Content="100 %",MinWidth=65,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Name="ZoomReset"};zoomReset.Click+=(_,_)=>SetZoom(1);tools.Children.Add(zoomReset);ToolTip.SetTip(zoomReset,"Zoom 100 %");AutomationProperties.SetName(zoomReset,"Zoom 100 %");
        zoomIn=IconButton("plus","Agrandir (Ctrl++)","Zoom in (Ctrl++)",()=>{SetZoom(zoom+.1);return Task.CompletedTask;});tools.Children.Add(zoomIn);
        fitWidth=IconButton("fit","Ajuster à la largeur (Ctrl+9)","Fit to width (Ctrl+9)",()=>{FitToWidth();return Task.CompletedTask;});tools.Children.Add(fitWidth);
        export=IconButton("export","Exporter","Export",()=>Task.CompletedTask);export.Background=new SolidColorBrush(Color.Parse("#FF8B00"));export.Foreground=Brushes.White;tools.Children.Add(export);
        tools.Children.Add(IconButton("sun","Thème clair","Light theme",()=>{SetTheme(1);return Task.CompletedTask;}));
        tools.Children.Add(IconButton("moon","Thème sombre","Dark theme",()=>{SetTheme(2);return Task.CompletedTask;}));
        tools.Children.Add(IconButton("system","Thème système","System theme",()=>{SetTheme(0);return Task.CompletedTask;}));
        var language=new Button{Content="FR / EN",Background=Brushes.Transparent,BorderThickness=new Thickness(0),MinHeight=36};language.Click+=(_,_)=>SetLanguage(!english);ToolTip.SetTip(language,"Français / English");AutomationProperties.SetName(language,"Français / English");tools.Children.Add(language);
        cancel=IconButton("cancel","Annuler l’opération","Cancel operation",()=>{pending?.Cancel();exportCancellation?.Cancel();return Task.CompletedTask;});cancel.IsVisible=false;tools.Children.Add(cancel);
        toolbarBorder.Child=new ScrollViewer{Content=tools,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(toolbarBorder,1);root.Children.Add(toolbarBorder);
        sidebar.Child=new ScrollViewer{Content=navigation,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};body.Children.Add(sidebar);
        var center=new Grid{RowDefinitions=new RowDefinitions("Auto,*")};
        var readingHeader=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};readingHeader.Children.Add(warning);Grid.SetColumn(readingMode,1);readingHeader.Children.Add(readingMode);center.Children.Add(readingHeader);
        viewport.Content=paperFrame;Grid.SetRow(viewport,1);center.Children.Add(viewport);Grid.SetRow(readingWorkspace,1);center.Children.Add(readingWorkspace);Grid.SetRow(empty,1);center.Children.Add(empty);stage.Child=center;Grid.SetColumn(stage,1);body.Children.Add(stage);Grid.SetRow(body,2);root.Children.Add(body);InitializeReading();
        paperFrame.Child=paper;paperFrame.HorizontalAlignment=HorizontalAlignment.Center;paperFrame.VerticalAlignment=VerticalAlignment.Top;paperFrame.Margin=new Thickness(24,16,24,28);
        var bottom=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),Margin=new Thickness(16,0)};status.TextTrimming=TextTrimming.CharacterEllipsis;bottom.Children.Add(status);
        var identity=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};identity.Children.Add(copyConfirmation);identity.Children.Add(buildLabel);
        var copyBuild=IconButton("copy","Copier la référence du build","Copy build reference",CopyBuildReferenceAsync);copyBuild.Name="CopyBuildReference";identity.Children.Add(copyBuild);
        diagnostics=IconButton("info","À propos de Lecteur Word","About Lecteur Word",async()=>await new AboutWindow(english){RequestedThemeVariant=RequestedThemeVariant}.ShowDialog(this));diagnostics.Name="AboutLecteurWord";identity.Children.Add(diagnostics);Grid.SetColumn(identity,1);bottom.Children.Add(identity);footer.Child=bottom;Grid.SetRow(footer,3);root.Children.Add(footer);
        copyTimer.Tick+=(_,_)=>{copyConfirmation.IsVisible=false;copyTimer.Stop();};
        query.TextChanged+=(_,_)=>{if(navMode=="search")FillSearch();};
        viewport.SizeChanged+=(_,_)=>{if(fittingWidth&&composition?.Available==true&&!simplified)FitToWidth();};
        KeyDown+=OnKeys;Closed+=(_,_)=>{closed=true;copyTimer.Stop();pending?.Cancel();exportCancellation?.Cancel();pageBitmap?.Dispose();foreach(var thumb in thumbnails)thumb.Dispose();};
        ActualThemeVariantChanged+=(_,_)=>{if(initialized)PaintTheme();};
        initialized=true;SetTheme(0);UpdateUi();
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
        var b=new Button{Content=Icons.Make(icon),Width=38,Height=36,Padding=new Thickness(8),CornerRadius=new CornerRadius(7),Background=Brushes.Transparent,BorderThickness=new Thickness(0),Name="Action_"+icon+"_"+labels.Count};
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
        pending?.Cancel();var cancellation=new CancellationTokenSource();pending=cancellation;cancel.IsVisible=true;
        status.Text=T("Lecture et inspection…","Reading and inspecting…");
        try
        {
            await loader.WaitAsync(cancellation.Token);
            try
            {
                var data=await Task.Run(()=>
                {
                    var result=DocumentInspector.InspectDocument(path,cancellationToken:cancellation.Token);
                    var model=FlowReader.Read(result);var layout=Composer.Compose(result,cancellation.Token);
                    var reading=ReadableReader.Read(result,cancellation.Token);
                    var checks=Enum.GetValues<ExportFormat>().ToDictionary(f=>f,f=>DocumentExports.Preflight(result,f));return(result,model,layout,reading,checks);
                },cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();if(closed)return;
                readingList.ItemsSource=null;foreach(var image in readingImages.Values)image.Dispose();readingImages.Clear();readingImagePixels=0;
                rasterPage=null;inspection=data.result;flow=data.model;composition=data.layout;readable=data.reading;preflights=data.checks;fileName=Path.GetFileName(path);page=0;CurrentBlockId=null;CurrentNoteReferenceId=null;CurrentSection=1;ActiveReadingContext=null;simplified=!data.layout.Available;readingList.ItemsSource=readable.Presentation.BodyBlocks;RefreshReadingAppearance();UpdateUi();
            }
            finally{loader.Release();}
        }
        catch(OperationCanceledException){if(pending==cancellation&&!closed){UpdateUi();status.Text=T("Lecture annulée ; document précédent conservé","Inspection cancelled; previous document retained");}}
        catch(Exception){if(pending==cancellation&&!closed)status.Text=T("Rendu interrompu — contenu hors limites","Rendering stopped — resource or format limit");}
        finally{cancellation.Dispose();if(pending==cancellation)pending=null;cancel.IsVisible=pending is not null||exportCancellation is not null;}
    }
    public void GoToPage(int index){if(composition?.Available!=true)return;simplified=false;UpdateReadingMode();page=Math.Clamp(index,0,composition.Pages.Count-1);ShowPage();UpdateActions();if(navMode=="pages")FillPanel();}
    public void SetZoom(double value){fittingWidth=false;ApplyPageZoom(value);}
    void ApplyPageZoom(double value){zoom=Math.Clamp(value,.3,2);var geometry=composition?.Pages.ElementAtOrDefault(page)?.Geometry??new PageGeometry();paper.Width=geometry.Width*96/72*zoom;paper.Height=geometry.Height*96/72*zoom;ToolTip.SetTip(paper,$"{zoom:P0}");RenderPageBitmap();}
    public void FitToWidth(){if(composition?.Available!=true||simplified)return;fittingWidth=true;ApplyPageZoom(Math.Max(1,viewport.Bounds.Width-64)/(composition.Pages[page].Geometry.Width*96/72));}
    public void ToggleNavigation(){navVisible=!navVisible;sidebar.IsVisible=navVisible;body.ColumnDefinitions[0].Width=new GridLength(navVisible?224:0);}
    public void SetTheme(int value){theme=value;RequestedThemeVariant=value switch{1=>ThemeVariant.Light,2=>ThemeVariant.Dark,_=>ThemeVariant.Default};PaintTheme();}
    public void SetLanguage(bool useEnglish){english=useEnglish;foreach(var item in labels){ToolTip.SetTip(item.Button,T(item.Fr,item.En));AutomationProperties.SetName(item.Button,T(item.Fr,item.En));}RefreshReadingAppearance();UpdateUi();}
    void PaintTheme()
    {
        bool dark=ActualThemeVariant==ThemeVariant.Dark;
        header.Background=toolbarBorder.Background=footer.Background=new SolidColorBrush(Color.Parse(dark?"#202730":"#F8FAFE"));
        sidebar.Background=new SolidColorBrush(Color.Parse(dark?"#19212B":"#F1F5FC"));stage.Background=new SolidColorBrush(Color.Parse(dark?"#111820":"#E7EDF5"));
        Foreground=new SolidColorBrush(Color.Parse(dark?"#EBF0F8":"#18253B"));warning.Foreground=new SolidColorBrush(Color.Parse(dark?"#FFCB82":"#865000"));
        foreach(var item in labels)
            if(item.Button.Content is Viewbox vb && vb.Child is Avalonia.Controls.Shapes.Path path)
                path.Stroke=item.Button==export?Brushes.White:Foreground;
        if(initialized){RefreshReadingAppearance();FillPanel();}
    }
    void UpdateUi()
    {
        buildLabel.Text=BuildReference.Current.CompactReference(english);ToolTip.SetTip(buildLabel,BuildReference.Current.FullReference(english));
        if(copyConfirmation.IsVisible)copyConfirmation.Text=T("Référence copiée","Reference copied");
        fileLabel.Text=fileName.Length==0?T("Lecture seule · vos originaux restent intacts","Read only · your originals stay untouched"):fileName;
        Title=fileName.Length==0?"Lecteur Word":fileName+" — Lecteur Word";
        bool available=composition?.Available==true;UpdateReadingMode();empty.IsVisible=!available&&readable?.Available!=true;
        empty.Text=inspection is null?T("Vos documents Word, au calme.\nOuvrez un DOCX pour commencer.","Your Word documents, in focus.\nOpen a DOCX to begin."):T("Ce contenu ne peut pas encore être composé.\nL’inventaire et ses diagnostics restent consultables.","This content cannot be composed yet.\nIts inventory and diagnostics remain available.");
        warning.Text=inspection is null?"":available?T("Pagination expérimentale · pages calculées par Lecteur Word, fidélité Word non démontrée.","Experimental pagination · pages calculated by Lecteur Word, Word fidelity not demonstrated.")+(composition!.Issues.Any(i=>i.Code=="LAYOUT_FONT_SUBSTITUTION")?T(" Polices substituées : détails disponibles."," Font substitutions: details available."):""):T("Pagination hors profil ; lecture simplifiée disponible pour les contenus interprétables.","Pagination outside profile; simplified reading available for interpretable content.");
        status.Text=inspection is null?T("DOCX · Lecture seule","DOCX · Read only"):inspection.Manifest.InspectionComplete?T("DOCX · Lecture seule · Inventaire achevé · Import non qualifié","DOCX · Read only · Inventory complete · Import unqualified"):T("Inspection refusée · diagnostics disponibles","Inspection refused · diagnostics available");
        if(simplified&&readable?.Available==true)warning.Text=T("Lecture simplifiée · mise en page adaptée · détails disponibles depuis les repères.","Simplified reading · adapted layout · details available from contextual marks.");
        if(available&&!simplified)ShowPage();UpdateActions();FillPanel();
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
        bool available=composition?.Available==true&&!simplified;previous.IsEnabled=available&&page>0;next.IsEnabled=available&&page<composition!.Pages.Count-1;
        previous.IsVisible=next.IsVisible=zoomIn.IsVisible=zoomOut.IsVisible=composition?.Available==true&&!simplified;
        fitWidth.IsVisible=fitWidth.IsEnabled=available;
        foreach(var reset in tools.Children.OfType<Button>().Where(b=>b.Name=="ZoomReset"))reset.IsVisible=available;
        zoomIn.IsEnabled=zoomOut.IsEnabled=available;export.IsEnabled=inspection?.Manifest.InspectionComplete==true&&exportCancellation is null;
        pageLabel.Text=available?$"{page+1} / {composition!.Pages.Count}":simplified&&readable?.Available==true?T("Sans pages","No pages"):"— / —";
        ToolTip.SetTip(pageLabel,T("Pages calculées par Lecteur Word","Pages calculated by Lecteur Word"));
    }
    void ShowPanel(string mode){CloseReadingContext();navMode=mode;if(!navVisible)ToggleNavigation();FillPanel();}
    void FillPanel()
    {
        navigation.Children.Clear();
        foreach(var thumb in thumbnails)thumb.Dispose();thumbnails.Clear();
        navigation.Children.Add(new TextBlock{Text=T("NAVIGATION","NAVIGATION"),FontSize=11,FontWeight=FontWeight.Bold,Margin=new Thickness(0,4,0,6)});
        foreach(var (key,fr,en) in new[]{("pages","Miniatures","Thumbnails"),("outline","Sommaire","Outline"),("notes","Notes","Notes"),("sections","En-têtes et pieds","Headers and footers"),("search","Recherche","Search"),("diagnostics","Diagnostics","Diagnostics"),("properties","Propriétés","Properties")})
        {
            if(key=="pages"&&(composition?.Available!=true||simplified)||key=="notes"&&readable?.Presentation.Notes.Count is not >0||key=="sections"&&readable?.Presentation.Sections.Any(s=>s.Stories.Count>0)!=true)continue;
            var button=new Button{Content=T(fr,en),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,Background=navMode==key?new SolidColorBrush(Color.Parse(ActualThemeVariant==ThemeVariant.Dark?"#51331D":"#D5E5FE")):Brushes.Transparent,BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(6)};
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
            navItems.Children.Add(new TextBlock{Text=T("Lecture simplifiée : aucune page calculée. Utilisez le sommaire, les signets ou la recherche.","Simplified reading: no calculated pages. Use the outline, bookmarks or search."),TextWrapping=TextWrapping.Wrap});
        else if(navMode=="pages"&&composition?.Available==true)
        {
            foreach(int i in Enumerable.Range(Math.Max(0,page-1),Math.Min(composition.Pages.Count-1,page+1)-Math.Max(0,page-1)+1))
            {
                using var s=new MemoryStream(Composer.PagePng(composition.Pages[i],.18f));var bitmap=new Bitmap(s);thumbnails.Add(bitmap);var image=new Image{Source=bitmap,Width=108,Height=108*composition.Pages[i].Geometry.Height/composition.Pages[i].Geometry.Width};
                var button=new Button{Content=new StackPanel{Children={image,new TextBlock{Text=(i+1).ToString(CultureInfo.InvariantCulture),HorizontalAlignment=HorizontalAlignment.Center}}},HorizontalAlignment=HorizontalAlignment.Center,BorderThickness=new Thickness(i==page?2:1),BorderBrush=i==page?new SolidColorBrush(Color.Parse("#FF8B00")):Brushes.Gray,Padding=new Thickness(4),Background=Brushes.White,Foreground=Brushes.Black};
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
    void OnKeys(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape&&notePane.IsVisible){DismissNote(false);e.Handled=true;return;}
        if(e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if(e.Key==Key.O){_=ChooseOpen();e.Handled=true;}
            else if(e.Key==Key.F){ShowPanel("search");query.Focus();e.Handled=true;}
            else if(e.Key is Key.OemPlus or Key.Add){SetZoom(zoom+.1);e.Handled=true;}
            else if(e.Key is Key.OemMinus or Key.Subtract){SetZoom(zoom-.1);e.Handled=true;}
            else if(e.Key==Key.D0){SetZoom(1);e.Handled=true;}
            else if(e.Key==Key.D9){FitToWidth();e.Handled=true;}
        }
        else if(e.Key==Key.F9){ToggleNavigation();e.Handled=true;}
        else if(!simplified&&e.KeyModifiers.HasFlag(KeyModifiers.Alt)&&e.Key==Key.Left){GoToPage(page-1);e.Handled=true;}
        else if(!simplified&&e.KeyModifiers.HasFlag(KeyModifiers.Alt)&&e.Key==Key.Right){GoToPage(page+1);e.Handled=true;}
        else if(e.Key==Key.Escape){pending?.Cancel();exportCancellation?.Cancel();}
    }
}
