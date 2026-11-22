using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    readonly TextBlock sideTitle=new(){FontSize=15,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBox elementQuery=new(){Name="ElementSearch"};
    readonly ComboBox elementCategory=new(){Name="ElementCategory",HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly ListBox elementList=new(){Name="DocumentElements",Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
    readonly TextBlock elementSummary=new(){Name="ElementSummary",FontSize=11,TextWrapping=TextWrapping.Wrap};
    readonly Button locateElement=new(){Name="LocateElement",HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly Button elementProperties=new(){Name="ElementProperties",HorizontalAlignment=HorizontalAlignment.Stretch};
    bool updatingElementFilter;
    int elementCategoryIndex;
    internal ReaderDocumentIndex? CurrentDocumentIndex=>documentIndex;

    void InitializeElements()
    {
        var box=new Grid{RowDefinitions=new("Auto,Auto,*,Auto"),Margin=new Thickness(12,9)};
        var top=new Grid{ColumnDefinitions=new("*,Auto"),Margin=new Thickness(0,0,0,8)};
        top.Children.Add(sideTitle);
        var close=IconButton("cancel","Masquer les éléments","Hide elements",()=>{ToggleElements();panelToggle?.Focus();return Task.CompletedTask;});
        Grid.SetColumn(close,1);top.Children.Add(close);box.Children.Add(top);
        var filter=new StackPanel{Spacing=6};
        filter.Children.Add(elementQuery);filter.Children.Add(elementCategory);filter.Children.Add(elementSummary);
        Grid.SetRow(filter,1);box.Children.Add(filter);
        ScrollViewer.SetHorizontalScrollBarVisibility(elementList,ScrollBarVisibility.Disabled);
        elementList.Margin=new Thickness(0,6);
        StretchRows(elementList);
        elementList.ItemTemplate=new FuncDataTemplate<DocumentElementRow>((row,_)=>row is null?new Border():ElementCard(row));
        Grid.SetRow(elementList,2);box.Children.Add(elementList);
        var actions=new StackPanel{Spacing=5};actions.Children.Add(locateElement);actions.Children.Add(elementProperties);
        Grid.SetRow(actions,3);box.Children.Add(actions);documentPanel.Child=box;

        elementQuery.TextChanged+=(_,_)=>RefilterElements();
        elementCategory.SelectionChanged+=(_,_)=>{if(updatingElementFilter)return;elementCategoryIndex=Math.Max(0,elementCategory.SelectedIndex);RefilterElements();};
        elementList.SelectionChanged+=(_,_)=>UpdateElementActions();
        elementList.DoubleTapped+=(_,e)=>{if(elementList.SelectedItem is DocumentElementRow selected&&selected.Target!=DocumentTarget.None){Locate(selected);e.Handled=true;}};
        elementList.KeyDown+=(_,e)=>{if(e.Key==Key.Enter&&elementList.SelectedItem is DocumentElementRow selected&&selected.Target!=DocumentTarget.None){Locate(selected);e.Handled=true;}};
        locateElement.Click+=(_,_)=>{if(elementList.SelectedItem is DocumentElementRow selected)Locate(selected);};
        elementProperties.Click+=(_,_)=>ShowElementProperties();
        UpdateElementActions();
    }

    string CategoryLabel(DocumentCategory category)=>category switch
    {
        DocumentCategory.Table=>T("Tableaux","Tables"),
        DocumentCategory.Cell=>T("Cellules","Cells"),
        DocumentCategory.Image=>T("Images","Images"),
        DocumentCategory.Drawing=>T("Formes et dessins","Shapes and drawings"),
        DocumentCategory.Chart=>T("Graphiques et diagrammes","Charts and diagrams"),
        DocumentCategory.Equation=>T("Équations","Equations"),
        DocumentCategory.TextBox=>T("Zones de texte","Text boxes"),
        DocumentCategory.Note=>T("Notes de bas et de fin","Footnotes and endnotes"),
        DocumentCategory.Link=>T("Signets et liens","Bookmarks and links"),
        DocumentCategory.Field=>T("Champs et sommaires","Fields and contents"),
        DocumentCategory.Running=>T("En-têtes et pieds","Headers and footers"),
        DocumentCategory.Section=>T("Sections","Sections"),
        DocumentCategory.Review=>T("Commentaires et révisions","Comments and revisions"),
        _=>T("Objets non interprétés","Uninterpreted objects")
    };

    void RefreshElements(bool reset=false)
    {
        updatingElementFilter=true;
        if(reset){elementCategoryIndex=0;elementQuery.Text="";elementList.SelectedItem=null;}
        elementCategory.ItemsSource=new[]{T("Toutes les catégories","All categories")}.Concat(Enum.GetValues<DocumentCategory>().Select(CategoryLabel)).ToArray();
        elementCategory.SelectedIndex=elementCategoryIndex;
        AutomationProperties.SetName(elementCategory,T("Catégorie des éléments","Element category"));
        AutomationProperties.SetName(elementList,T("Éléments du document","Document elements"));
        updatingElementFilter=false;
        RefilterElements();
    }

    void RefilterElements()
    {
        if(updatingElementFilter)return;
        var selected=elementList.SelectedItem;
        var all=documentIndex?.Rows??[];
        IEnumerable<DocumentElementRow> matching=all;
        if(elementCategoryIndex>0)matching=matching.Where(r=>(int)r.Category+1==elementCategoryIndex);
        if(elementQuery.Text?.Trim() is {Length:>0} needle)
            matching=matching.Where(r=>Match(r.Type,needle)||Match(r.Preview,needle)||Match(CategoryLabel(r.Category),needle)||Match(r.Source.NodeId,needle));

        var shown=matching.ToArray();
        elementList.ItemsSource=shown;
        if(selected is DocumentElementRow row&&shown.Contains(row))elementList.SelectedItem=row;
        else if(shown.Length>0)elementList.SelectedIndex=0;
        elementSummary.Foreground=SecondaryInk;
        elementSummary.Text=documentIndex?.Limitation is {} reason?
            T("Inventaire sémantique indisponible · ","Semantic inventory unavailable · ")+reason:
            inspection is null?T("Ouvrez un DOCX pour consulter ses éléments.","Open a DOCX to inspect its elements."):
            shown.Length==0?T("Aucun élément pour ce filtre.","No objects match this filter."):
            shown.Length==1?T("1 élément","1 element"):
            T($"{shown.Length:N0} éléments",$"{shown.Length:N0} elements");
        UpdateElementActions();

        static bool Match(string? field,string needle)=>field?.Contains(needle,StringComparison.CurrentCultureIgnoreCase)==true;
    }

    void PaintElementTheme()
    {
        var field=Palette.Field;
        foreach(var key in new[]{"TextControlBackground","TextControlBackgroundPointerOver","TextControlBackgroundFocused"})
        {
            elementQuery.Resources[key]=field;
            query.Resources[key]=field;
        }
        foreach(var key in new[]{"TextControlPlaceholderForeground","TextControlPlaceholderForegroundPointerOver","TextControlPlaceholderForegroundFocused"})
        {
            elementQuery.Resources[key]=SecondaryInk;
            query.Resources[key]=SecondaryInk;
        }
        foreach(var key in new[]{"SystemControlHighlightListAccentLowBrush","SystemControlHighlightListAccentMediumBrush",
            "SystemControlHighlightListAccentHighBrush","SystemControlHighlightAltListAccentLowBrush"})
            elementList.Resources[key]=AccentFill;
    }

    Control ElementCard(DocumentElementRow row)
    {
        string type=row.Category switch
        {
            DocumentCategory.Table=>T("Tableau","Table"),
            DocumentCategory.Cell=>T("Cellule","Cell"),
            _=>row.Type
        };
        string title=row.Preview is {} p?type+" · "+p:CategoryLabel(row.Category)+" · "+type;
        var content=new StackPanel{Spacing=3,Margin=new Thickness(3,2)};
        content.Children.Add(new TextBlock{Text=title,FontWeight=FontWeight.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis});
        content.Children.Add(new TextBlock{Text=ElementPosition(row)+" · "+State(row),FontSize=10.5,Foreground=SecondaryInk,TextTrimming=TextTrimming.CharacterEllipsis});
        ToolTip.SetTip(content,title+"\n"+ElementPosition(row)+" · "+State(row));
        return content;
    }

    string ElementPosition(DocumentElementRow row)
    {
        string place=row.Page is {} pageNumber?T("Page calculée ","Calculated page ")+pageNumber:
            row.Section is {} n?T("Section ","Section ")+n+(row.Position is "hdr" or "ftr"?" · "+StoryPosition(row.Position):""):StoryPosition(row.Position);
        if(row.Cell is not {} cell)return place;
        place+=" · "+T("Ligne ","Row ")+cell.Row;
        if(cell.Column is {} column)place+=" · "+T("col. ","col. ")+column;
        return place;
    }

    string StoryPosition(string position)
    {
        return position switch
        {
            "body"=>T("Corps","Body"),"hdr"=>T("En-tête source","Source header"),"ftr"=>T("Pied source","Source footer"),
            "footnote"=>T("Note de bas de page","Footnote"),"endnote"=>T("Note de fin","Endnote"),
            "txbxContent"=>T("Zone de texte","Text box"),"comment"=>T("Commentaire","Comment"),_=>T("Structure source","Source structure")
        };
    }

    string State(DocumentElementRow row)
    {
        if(row.Target==DocumentTarget.Page)return T("Placé par le lecteur","Placed by reader");
        string status=row.Coverage=="Represented"?T("Identifié","Identified"):T("Inventorié","Inventoried");
        return status+" · "+(row.Target switch
        {
            DocumentTarget.Block=>T("Bloc de lecture","Reading block"),
            DocumentTarget.Container=>T("Bloc contenant","Containing block"),
            DocumentTarget.Cell=>T("Cellule consultable","Cell available"),
            DocumentTarget.Note or DocumentTarget.Running=>T("Contenu consultable","Content available"),
            _=>T("Sans localisation","No location")
        });
    }

    void UpdateElementActions()
    {
        var row=elementList.SelectedItem as DocumentElementRow;
        locateElement.IsVisible=row?.Target is not (null or DocumentTarget.None);
        locateElement.Content=row?.Target switch
        {
            DocumentTarget.Page=>T("Aller à la page calculée","Go to calculated page"),
            DocumentTarget.Cell=>T("Aller à la cellule","Go to cell"),
            DocumentTarget.Container=>T("Aller au bloc contenant","Go to containing block"),
            DocumentTarget.Note=>T("Lire la note","Read note"),
            DocumentTarget.Running=>T("Lire la section","Read section"),
            _=>T("Aller au bloc","Go to block")
        };
        elementProperties.IsEnabled=row is not null;
        elementProperties.IsVisible=row is not null;
        elementProperties.Content=T("Propriétés de l’élément","Element properties");
        ToolTip.SetTip(locateElement,locateElement.Content);
        ToolTip.SetTip(elementProperties,row is null?null:State(row));
    }

    bool Locate(DocumentElementRow row)
    {
        if(row.Target==DocumentTarget.Page && row.Page is {} number && composition?.Available==true)
        {
            GoToPage(number-1);
            var pg=composition.Pages[page];
            string id=row.Source.NodeId;
            double? y=pg.DisplayCells.FirstOrDefault(c=>!c.IsRepeated&&(c.TableId==id||c.CellId==id||c.MergedSourceIds.Contains(id)))?.Y;
            y??=pg.DisplayLines.FirstOrDefault(l=>l.ParagraphId==id)?.Baseline;
            if(y is null&&pg.RunningStories.FirstOrDefault(s=>s.Selection.Reference.TargetId==id) is {} running)
                y=PageComposer.Points(running.Region.Top);
            if(y is null && row.Category==DocumentCategory.Section && row.Section is {} n)y=pg.Lines.FirstOrDefault(l=>l.Section==n)?.Baseline;
            if(y is null && row.Category==DocumentCategory.Image && row.Anchor is {} imageId)
                y=pg.DisplayLines.SelectMany(l=>l.Images).FirstOrDefault(i=>i.RunId==imageId&&!i.IsRepeated)?.Y;
            if(y is {} vertical)
            {
                CurrentBlockId=id;
                viewport.Offset=new Vector(viewport.Offset.X,Math.Max(0,vertical*PointsToDip*zoom-32));
                SelectPageParagraph(id);
                UpdateNavigationPosition();
            }
            return true;
        }
        if(row.Target==DocumentTarget.Cell)return NavigateToCell(row.Source.NodeId);
        if(row.Target is DocumentTarget.Block or DocumentTarget.Container && row.Anchor is {} anchor)
            return NavigateToBlock(anchor);
        if(row.Target==DocumentTarget.Note && row.Anchor is {} noteId && readable?.Presentation.Notes.TryGetValue(noteId,out var note)==true)
        {
            OpenNote(note,elementList);return true;
        }
        if(row.Target==DocumentTarget.Running && row.Section is {} section && readable?.Presentation.Sections.FirstOrDefault(s=>s.Number==section) is {} selected)
        {
            CurrentSection=section;OpenStories(selected,elementList);return true;
        }
        return false;
    }

    void ShowElementProperties()
    {
        if(elementList.SelectedItem is not DocumentElementRow row)return;
        var scroll=new ScrollViewer{MaxHeight=390,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var stack=new StackPanel{Spacing=8,MaxWidth=380};
        stack.Children.Add(new TextBlock{Text=CategoryLabel(row.Category)+" · "+row.Type,FontWeight=FontWeight.SemiBold,FontSize=16,TextWrapping=TextWrapping.Wrap});
        if(row.Preview is {} p)stack.Children.Add(Explain(p));
        stack.Children.Add(Explain(ElementPosition(row)+" · "+State(row)));
        stack.Children.Add(Explain(row.Detail));
        if(row.Cell is {} cell)
        {
            string details=T("Tableau source ","Source table ")+cell.TableId;
            if(cell.ColumnSpan is {} span)details+=" · "+T("étendue en colonnes ","column span ")+span;
            if(cell.MergeOrigin is {} merge)details+="\n"+T("Cellule d’origine de la fusion ","Merge origin cell ")+merge;
            stack.Children.Add(Explain(details));
        }
        if(inspection?.Manifest.Nodes.FirstOrDefault(n=>n.Id==row.Source.NodeId) is {} entry)
        {
            var properties=entry.Attributes.Where(a=>!a.Key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal)).Take(13).ToArray();
            if(properties.Length>0)
            {
                string display=string.Join("\n",properties.Take(12).Select(p=>p.Key[(p.Key.IndexOf('}')+1)..]+" = "+SearchExcerpt(p.Value)));
                stack.Children.Add(new SelectableTextBlock{Text=display+(properties.Length>12?"\n…":""),FontSize=11,TextWrapping=TextWrapping.Wrap});
            }
        }
        stack.Children.Add(new SelectableTextBlock{Text=row.Source.NodeId+"\n"+row.Source.Location.PartUri+"\n"+row.Source.Location.Path,FontSize=11,TextWrapping=TextWrapping.Wrap});
        scroll.Content=stack;var flyout=new Flyout{Content=ContextCard(scroll),Placement=PlacementMode.Left};
        flyout.ShowAt(elementProperties);
    }
}
