using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    readonly Border documentPanel=new(){Name="DocumentElementsPanel"};
    readonly GridSplitter navigationSplitter=Splitter("NavigationSplitter"),elementSplitter=Splitter("ElementsSplitter");
    readonly WrapPanel tools=new(){Orientation=Orientation.Horizontal,Margin=new Thickness(14,4)};
    readonly WrapPanel facts=new(){Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock documentKind=new(){Name="DocumentKind",FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock footerDocument=new(){Name="FooterDocument",MaxWidth=132,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock countsLabel=new(){Name="DocumentCounts",VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock languageLabel=new(){Name="DocumentLanguage",VerticalAlignment=VerticalAlignment.Center};
    readonly Button zoomReset=new(){Name="ZoomReset",Content="100 %",MinWidth=56,Height=31};
    readonly StackPanel pageActions=new(){Orientation=Orientation.Horizontal};
    readonly StackPanel zoomActions=new(){Orientation=Orientation.Horizontal,Spacing=2};
    readonly List<Border> groupDividers=[];
    Button? rulerToggle,marksToggle,panelToggle,navigationToggle;
    bool elementsVisible;
    bool preferElements;
    double navigationWidth=220,elementWidth=294;
    ReaderDocumentIndex? documentIndex;
    PaginatedDocument? pageModel;

    static GridSplitter Splitter(string name)=>new()
    {
        Name=name,Width=5,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,
        ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndNext,
    };

    void InitializeWorkspace(Grid root)
    {
        var titleRow=new Grid{ColumnDefinitions=new("Auto,Auto,*,Auto"),Margin=new Thickness(16,0)};
        titleRow.Children.Add(BrandAssets.CreateImage(26));
        var title=new TextBlock{Text="Lecteur Word",Margin=new Thickness(9,0,18,0),FontSize=15,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center};
        Grid.SetColumn(title,1);titleRow.Children.Add(title);
        Grid.SetColumn(fileLabel,2);titleRow.Children.Add(fileLabel);
        var lang=IconButton("globe","Passer en anglais","Switch to French",()=>{SetLanguage(!english);return Task.CompletedTask;});
        lang.Name="LanguageToggle";
        lang.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(lang,3);titleRow.Children.Add(lang);
        header.Child=titleRow;root.Children.Add(header);

        toolbarBorder.Child=tools;Grid.SetRow(toolbarBorder,1);root.Children.Add(toolbarBorder);
        var navScroll=new ScrollViewer{Content=navigation,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        sidebar.Child=navScroll;body.Children.Add(sidebar);Grid.SetColumn(navigationSplitter,1);body.Children.Add(navigationSplitter);

        var center=new Grid{RowDefinitions=new("Auto,*")};
        warning.HorizontalAlignment=HorizontalAlignment.Center;warning.TextAlignment=TextAlignment.Center;
        center.Children.Add(warning);
        var pageSurface=new Grid{RowDefinitions=new("Auto,*")};
        pageSurface.Children.Add(BuildRulerBar());
        viewport.Content=pageDeck;Grid.SetRow(viewport,1);pageSurface.Children.Add(viewport);
        Grid.SetRow(pageSurface,1);center.Children.Add(pageSurface);
        Grid.SetRow(readingWorkspace,1);center.Children.Add(readingWorkspace);
        unavailablePanel.Children.Add(empty);
        unavailablePanel.Children.Add(openDiagnostics);
        unavailablePanel.Children.Add(openReading);
        Grid.SetRow(unavailablePanel,1);center.Children.Add(unavailablePanel);
        stage.Child=center;Grid.SetColumn(stage,2);body.Children.Add(stage);
        Grid.SetColumn(elementSplitter,3);body.Children.Add(elementSplitter);
        Grid.SetColumn(documentPanel,4);body.Children.Add(documentPanel);InitializeElements();
        Grid.SetRow(body,2);root.Children.Add(body);
        InitializeReading();
        viewport.ScrollChanged+=(_,e)=>PagedScrollChanged(e);
        navigationSplitter.DragCompleted+=(_,_)=>RememberPanelWidths();
        elementSplitter.DragCompleted+=(_,_)=>RememberPanelWidths();
        navigationSplitter.KeyUp+=(_,_)=>RememberPanelWidths();
        elementSplitter.KeyUp+=(_,_)=>RememberPanelWidths();
        SizeChanged+=(_,_)=>UpdatePanels();
        AutomationProperties.SetName(navigationSplitter,"Largeur de navigation / Navigation width");
        AutomationProperties.SetName(elementSplitter,"Largeur des éléments / Elements width");
    }

    StackPanel AddGroup(params Control[] controls)
    {
        var group=new StackPanel{Orientation=Orientation.Horizontal,Spacing=3,VerticalAlignment=VerticalAlignment.Center};
        if(tools.Children.Count>0)
        {
            var divider=new Border{Width=1,Height=17,Margin=new Thickness(6,0),VerticalAlignment=VerticalAlignment.Center};
            group.Children.Add(divider);groupDividers.Add(divider);
        }
        foreach(var c in controls)group.Children.Add(c);
        tools.Children.Add(group);return group;
    }

    void InitializeFooter(Grid root, Button properties, Button copyBuild)
    {
        var stack=new Grid{RowDefinitions=new("Auto,Auto"),Margin=new Thickness(14,0)};
        status.Name="ReaderOperationStatus";status.IsVisible=false;status.FontSize=10;
        status.PropertyChanged+=(_,e)=>{if(e.Property==TextBlock.TextProperty)status.IsVisible=!string.IsNullOrEmpty(status.Text);};
        stack.Children.Add(status);
        var bottom=new Grid{ColumnDefinitions=new("*,Auto"),MinHeight=37};
        foreach(var f in new[]{footerDocument,documentKind,pageLabel,countsLabel,languageLabel})
        {
            f.FontSize=11;f.Margin=new Thickness(0,0,14,0);facts.Children.Add(f);
        }
        bottom.Children.Add(facts);
        var identity=new StackPanel{Orientation=Orientation.Horizontal,Spacing=2,VerticalAlignment=VerticalAlignment.Center};
        zoomReset.Click+=(_,_)=>SetZoom(1);
        zoomReset.Padding=new Thickness(5,2);zoomReset.Background=Brushes.Transparent;zoomReset.BorderThickness=new Thickness(0);
        zoomActions.Children.Add(zoomOut);zoomActions.Children.Add(zoomReset);zoomActions.Children.Add(zoomIn);zoomActions.Children.Add(fitWidth);
        identity.Children.Add(zoomActions);identity.Children.Add(properties);identity.Children.Add(diagnostics);
        identity.Children.Add(copyBuild);identity.Children.Add(buildLabel);identity.Children.Add(copyConfirmation);
        Grid.SetColumn(identity,1);bottom.Children.Add(identity);Grid.SetRow(bottom,1);stack.Children.Add(bottom);
        footer.Child=stack;Grid.SetRow(footer,3);root.Children.Add(footer);
    }

    void UpdatePanels()
    {
        double width=Bounds.Width>0?Bounds.Width:Width;
        bool showLeft=navVisible,showRight=elementsVisible;
        if(width<1020&&showLeft&&showRight)
        {
            showLeft=!preferElements;showRight=preferElements;
        }
        sidebar.IsVisible=navigationSplitter.IsVisible=showLeft;
        documentPanel.IsVisible=elementSplitter.IsVisible=showRight;
        var columns=body.ColumnDefinitions;
        SizeColumn(0,showLeft,navigationWidth,168,Math.Min(360,width*.34));
        SizeColumn(4,showRight,elementWidth,210,Math.Min(422,width*.41));
        columns[1].Width=new GridLength(showLeft?5:0);
        columns[3].Width=new GridLength(showRight?5:0);
        buildLabel.IsVisible=width>=1050;copyConfirmation.MaxWidth=width>=1050?180:90;
        UpdateToggle(navigationToggle,showLeft);UpdateToggle(panelToggle,showRight);

        void SizeColumn(int index,bool visible,double ideal,double minimum,double maximum)
        {
            var column=columns[index];
            column.MinWidth=visible?minimum:0;
            column.MaxWidth=visible?Math.Max(minimum,maximum):0;
            column.Width=new GridLength(visible?Math.Clamp(ideal,minimum,column.MaxWidth):0);
        }
    }

    void RememberPanelWidths()
    {
        if(sidebar.IsVisible)navigationWidth=body.ColumnDefinitions[0].ActualWidth;
        if(documentPanel.IsVisible)elementWidth=body.ColumnDefinitions[4].ActualWidth;
    }

    void ToggleElements()
    {
        if(elementsVisible&&!documentPanel.IsVisible)preferElements=true;
        else { elementsVisible=!elementsVisible;preferElements=elementsVisible; }
        UpdatePanels();
        if(documentPanel.IsVisible)elementQuery.Focus();
    }

    void UpdateToggle(Button? button,bool active)
    {
        if(button is null)return;
        button.Background=active?AccentFill:Brushes.Transparent;
        button.BorderThickness=active&&ActualThemeVariant==ReaderPalette.BlueVariant?new Thickness(0,0,0,2):new Thickness(0);
        button.BorderBrush=active&&ActualThemeVariant==ReaderPalette.BlueVariant?ReaderPalette.BluePrimary:Brushes.Transparent;
        if(active)button.Classes.Add("active");else button.Classes.Remove("active");
    }

    void UpdateFacts(bool paginated)
    {
        footerDocument.IsVisible=fileName.Length>0;
        footerDocument.Text=fileName;ToolTip.SetTip(footerDocument,fileName);
        documentKind.Text=T("DOCX · Lecture seule","DOCX · Read only");
        countsLabel.IsVisible=readable?.Available==true&&documentIndex is not null;
        if(countsLabel.IsVisible)
        {
            var n=documentIndex!;
            countsLabel.Text=T($"{n.Words:N0} mots · {n.Characters:N0} car.",$"{n.Words:N0} words · {n.Characters:N0} chars");
            ToolTip.SetTip(countsLabel,T("Corps lisible et cellules, y compris les valeurs de champs affichées. Notes, en-têtes et pieds exclus. Caractères comptés en points de code Unicode.",
                "Readable body and cells, including visible stored fields. Excludes notes, headers and footers. Characters are Unicode code points."));
        }
        languageLabel.IsVisible=documentIndex?.Language is not null;
        languageLabel.Text=documentIndex?.Language??"";
        ToolTip.SetTip(languageLabel,T("Langue déclarée du document ou langue par défaut des styles","Declared document language or style default"));
        zoomActions.IsVisible=paginated||IsSimplifiedReading;
        fitWidth.IsVisible=paginated;
        zoomReset.Content=Zoom.ToString("P0");
        ToolTip.SetTip(zoomReset,T("Rétablir le zoom à 100 % (Ctrl+0)","Reset zoom to 100% (Ctrl+0)"));
        AutomationProperties.SetName(zoomReset,T("Rétablir le zoom à 100 %","Reset zoom to 100%"));
        sideTitle.Text=T("Éléments du document","Document elements");
        elementQuery.PlaceholderText=T("Rechercher dans les éléments","Search elements");
        AutomationProperties.SetName(elementQuery,elementQuery.PlaceholderText);
    }

    IBrush AccentFill=>Palette.Selected;
    IBrush FineLine=>Palette.Line;
    IBrush PanelColor=>Palette.Panel;
    IBrush SecondaryInk=>Palette.Secondary;
    static IBrush Brush(string value)=>new SolidColorBrush(Color.Parse(value));
}
