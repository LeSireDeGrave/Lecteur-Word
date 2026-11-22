using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    ReadableDocument? readable;
    bool simplified,updatingReadingMode;
    double readingZoom=1;
    string? visibleBlockId;
    readonly ComboBox readingMode=new(){Name="ReadingMode",Margin=new Thickness(0,0,3,0),MinWidth=106,Padding=new Thickness(7,3),VerticalAlignment=VerticalAlignment.Top};
    readonly ListBox readingList=new(){Name="SimplifiedReading",BorderThickness=new Thickness(0),Padding=new Thickness(40,22,40,36),MaxWidth=1080,HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly Dictionary<string,Bitmap> readingImages=new(StringComparer.Ordinal);
    long readingImagePixels;
    public ReadableDocument? CurrentReading=>readable;
    public bool IsSimplifiedReading=>simplified&&readable?.Available==true;
    public string? CurrentBlockId {get;private set;}

    void InitializeReading()
    {
        InitializeNotePane();
        AutomationProperties.SetName(readingMode,T("Mode de lecture","Reading mode"));
        StretchRows(readingList);
        readingList.ItemTemplate=new FuncDataTemplate<ReadableBlock>((block,_)=>block is null?new Border():RenderReadingBlock(block));
        readingMode.SelectionChanged+=(_,_)=>
        {
            if(updatingReadingMode)return;
            simplified=readingMode.SelectedIndex==1;
            if(!simplified&&composition?.Available!=true)simplified=true;
            UpdateUi();
        };
        readingList.AddHandler(ScrollViewer.ScrollChangedEvent,(_,args)=>
        {
            if(args.Source is not ScrollViewer scroll || scroll.TemplatedParent!=readingList || !IsSimplifiedReading)return;
            var visible=readingList.GetRealizedContainers().OfType<ListBoxItem>()
                .Select(c=>(Control:c,Position:c.TranslatePoint(new Point(),scroll)))
                .Where(x=>x.Position is {} point&&point.Y+x.Control.Bounds.Height>0)
                .OrderBy(x=>x.Position!.Value.Y).FirstOrDefault();
            visibleBlockId=(visible.Control?.DataContext as ReadableBlock)?.Id;
            UpdateNavigationPosition();
        });
        Closed+=(_,_)=>{foreach(var image in readingImages.Values)image.Dispose();};
    }
    void UpdateReadingMode()
    {
        if(!simplified&&notePane.IsVisible)CloseReadingContext();
        updatingReadingMode=true;
        readingMode.ItemsSource=new[]{T("Pages","Pages"),T("Lecture","Reading")};readingMode.SelectedIndex=simplified?1:0;
        readingMode.IsVisible=readable?.Available==true&&composition?.Available==true;
        ToolTip.SetTip(readingMode,composition?.Available==true?T("Pagination interne expérimentale ou flux simplifié","Experimental internal pagination or simplified flow"):T("Composition hors profil : lecture simplifiée disponible","Composition outside profile: simplified reading available"));
        AutomationProperties.SetName(readingMode,T("Mode de lecture","Reading mode"));
        readingWorkspace.IsVisible=readingList.IsVisible=simplified&&readable?.Available==true;viewport.IsVisible=!simplified&&composition?.Available==true;
        if(readingList.IsVisible&&navMode=="pages"&&composition?.Available!=true)navMode="outline";
        updatingReadingMode=false;
    }
    static IEnumerable<ReadableBlock> Descendants(ReadableBlock block)
    {
        yield return block;foreach(var child in block.Children)foreach(var node in Descendants(child))yield return node;
    }
    static string BlockText(ReadableBlock block)=>block.PlainText;
    public bool NavigateToBlock(string id)
    {
        if(readable?.Available!=true)return false;
        CloseReadingContext();
        var source=readable.Presentation.BodyBlocks;
        int index=documentIndex?.BodyIndices.TryGetValue(id,out int i)==true?i:-1;
        if(index<0)return false;
        if(!simplified&&composition?.Available==true)
        {
            var target=Descendants(source[index]).First(b=>b.Id==id);
            int found=composition.Pages.ToList().FindIndex(p=>p.Lines.Any(l=>!l.IsRepeated&&l.Source==target.Source)||
                p.Cells.Any(c=>!c.IsRepeated&&(c.CellId==id||c.MergedSourceIds.Contains(id,StringComparer.Ordinal))));
            if(found>=0)
            {
                GoToPage(found);CurrentBlockId=id;SelectPageParagraph(id);UpdateNavigationPosition();return true;
            }
        }
        bool changed=!simplified;
        simplified=true;CurrentBlockId=id;
        CurrentSection=ReadingSectionAt(index)??CurrentSection;
        if(changed)UpdateUi();
        readingList.SelectedIndex=-1;readingList.ScrollIntoView(index);UpdateNavigationPosition();
        if(source[index].Kind=="table"&&id!=source[index].Id&&documentIndex is {} doc)
            for(string? ancestor=id;ancestor is not null&&ancestor!=source[index].Id;ancestor=doc.Semantic?.ParentId(ancestor))
                if(doc.ReadableCells.TryGetValue(ancestor,out var cell))
                {
                    BringReadingCellIntoView(cell.TableId,ancestor);
                    break;
                }
        return true;
    }

    bool NavigateToCell(string id)
    {
        if(documentIndex?.ReadableCells.TryGetValue(id,out var destination)!=true||!NavigateToBlock(id))
            return false;
        return !IsSimplifiedReading||BringReadingCellIntoView(destination.TableId,id,focus:true);
    }

    readonly HashSet<string> collapsedOutline=new(StringComparer.Ordinal);
    readonly TextBlock navPosition=new(){Name="NavigationPosition",FontSize=10,TextWrapping=TextWrapping.Wrap};

    int? ReadingSectionAt(int index)=>readable?.Presentation.Sections.Where(s=>s.Number>0)
        .FirstOrDefault(s=>s.ClosingBlockId is null||
            documentIndex?.BodyIndices.TryGetValue(s.ClosingBlockId,out int end)==true&&index<=end)?.Number;

    void UpdateNavigationPosition()
    {
        navPosition.Foreground=SecondaryInk;
        string? activeId=CurrentBlockId;
        int currentSection=CurrentSection;
        if(IsSimplifiedReading&&visibleBlockId is {} visible&&documentIndex?.BodyIndices.TryGetValue(visible,out int index)==true)
        {
            currentSection=ReadingSectionAt(index)??currentSection;
            activeId=readable?.Outline.LastOrDefault(o=>documentIndex.BodyIndices.TryGetValue(o.BlockId,out int at)&&at<=index)?.BlockId??activeId;
        }
        string? heading=readable?.Outline.FirstOrDefault(o=>o.BlockId==activeId)?.Label;
        navPosition.Text=CurrentPage>0?T($"Page {CurrentPage} · section {CurrentSection}",$"Page {CurrentPage} · section {CurrentSection}"):
            IsSimplifiedReading?T($"Section {currentSection}",$"Section {currentSection}"):"";
        if(heading is not null)navPosition.Text+=" · "+heading;
        navPosition.IsVisible=navPosition.Text.Length>0;
        string? selected=navMode=="bookmarks"?CurrentBlockId:activeId;
        foreach(var b in outlineActions)b.Background=b.Tag as string==selected?AccentFill:Brushes.Transparent;
    }

    readonly List<Button> outlineActions=[];
    sealed record OutlineEntry(ReadableNavigation Link,string Label)
    {
        internal List<OutlineEntry> Children { get; }=[];
    }

    void FillReadingNavigation(bool bookmarks)
    {
        var entries=bookmarks?readable?.Bookmarks:readable?.Outline;
        if(entries is null||entries.Count==0)
        {navItems.Children.Add(Explain(T("Aucun élément interprété dans cette vue.","No interpreted item in this view.")));return;}
        var headingLabels=bookmarks?[]:readable!.Blocks.SelectMany(Descendants).Where(b=>b.Kind=="heading")
            .GroupBy(b=>b.Source).ToDictionary(g=>g.Key,g=>readable.Presentation.TextFor(g.First()));
        var roots=new List<OutlineEntry>();
        var branch=new Stack<OutlineEntry>();
        foreach(var item in entries.Take(2000))
        {
            var e=new OutlineEntry(item,headingLabels.GetValueOrDefault(item.Source,item.Label));
            while(branch.Count>0&&branch.Peek().Link.Level>=item.Level)branch.Pop();
            var list=branch.Count==0?roots:branch.Peek().Children;
            list.Add(e);
            if(!bookmarks)branch.Push(e);
        }
        foreach(var node in roots)Render(node,navItems);
        if(entries.Count>2000)navItems.Children.Add(Explain(T("Sommaire affiché limité à 2 000 entrées","Outline display limited to 2,000 entries")));
        UpdateNavigationPosition();

        void Render(OutlineEntry node, StackPanel parent)
        {
            var button=new Button{Content=new TextBlock{Text=node.Label,TextWrapping=TextWrapping.Wrap},
                HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,
                Background=Brushes.Transparent,Padding=new Thickness(5,4),Tag=node.Link.BlockId};
            AutomationProperties.SetName(button,node.Label);ToolTip.SetTip(button,node.Label);
            button.Click+=(_,_)=>NavigateToBlock(node.Link.BlockId);outlineActions.Add(button);
            if(node.Children.Count==0){parent.Children.Add(button);return;}
            var group=new StackPanel{Spacing=1};
            var row=new Grid{ColumnDefinitions=new("Auto,*")};
            var toggle=new Button{Name="OutlineBranch",Width=23,Height=25,Padding=new Thickness(2),Background=Brushes.Transparent};
            row.Children.Add(toggle);Grid.SetColumn(button,1);row.Children.Add(button);group.Children.Add(row);
            var children=new StackPanel{Spacing=1,Margin=new Thickness(10,0,0,0)};
            bool built=false;
            void SetExpanded(bool expanded)
            {
                children.IsVisible=expanded;toggle.Content=expanded?"▾":"▸";
                string name=(expanded?T("Replier ","Collapse "):T("Développer ","Expand "))+node.Label;
                AutomationProperties.SetName(toggle,name);ToolTip.SetTip(toggle,name);
                if(expanded&&!built)
                {
                    foreach(var child in node.Children)Render(child,children);
                    built=true;
                }
                UpdateNavigationPosition();
            }
            toggle.Click+=(_,_)=>{if(children.IsVisible)collapsedOutline.Add(node.Link.BlockId);else collapsedOutline.Remove(node.Link.BlockId);SetExpanded(!children.IsVisible);};
            group.Children.Add(children);parent.Children.Add(group);SetExpanded(!collapsedOutline.Contains(node.Link.BlockId));
        }
    }
    void FillReadableSearch()
    {
        if(string.IsNullOrWhiteSpace(query.Text)||readable is null)return;
        var paragraphs=readable.Blocks.SelectMany(Descendants).Where(b=>b.Kind is "paragraph" or "heading").DistinctBy(b=>b.Id).Where(b=>readable.Presentation.TextFor(b).Contains(query.Text,StringComparison.CurrentCultureIgnoreCase)).Take(100).ToArray();
        foreach(var block in paragraphs)
        {
            string text=readable.Presentation.TextFor(block);var button=new Button{Content=SearchExcerpt(text),HorizontalAlignment=HorizontalAlignment.Stretch};ToolTip.SetTip(button,text.Length>300?text[..300]+"…":text);
            button.Click+=(_,_)=>
            {
                var note=readable.Presentation.Notes.Values.FirstOrDefault(n=>n.Verified&&ReadablePresentation.Walk(n.Content).Any(b=>b.Id==block.Id));
                if(note is not null){NavigateToBlock(note.OriginBlockId);OpenNote(note,readingList);return;}
                var story=readable.Presentation.Sections.FirstOrDefault(s=>s.Stories.Any(st=>ReadablePresentation.Walk(st.Content).Any(b=>b.Id==block.Id)));
                if(story is not null){ShowSection(story.Number);OpenStories(story,readingList);return;}
                NavigateToBlock(block.Id);
            };navItems.Children.Add(button);
        }
        navItems.Children.Add(new TextBlock{Text=T("Résultats affichés (max. 100) : ","Shown results (max. 100): ")+paragraphs.Length,TextWrapping=TextWrapping.Wrap});
    }
    Control RenderReadingBlock(ReadableBlock block,bool inCell=false)
    {
        if(block.Kind is "paragraph" or "heading")
        {
            double titleSize=block.HeadingLevel switch{0 or 1=>28,2=>24,3=>21,_=>19};
            var text=new TextBlock{Name=block.Kind=="heading"?"ReadingHeading":"ReadingParagraph",Tag=block.Id,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(Math.Clamp(block.ListLevel,0,8)*16,block.Kind=="heading"?24:0,0,block.Kind=="heading"?12:16),FontSize=(block.Kind=="heading"?titleSize:17)*readingZoom,LineHeight=(block.Kind=="heading"?titleSize*1.4:28)*readingZoom,
                Foreground=ReaderPalette.ReadingInk,
                TextAlignment=block.Alignment switch{"center"=>TextAlignment.Center,"right" or "end"=>TextAlignment.Right,"both"=>TextAlignment.Justify,_=>TextAlignment.Left}};
            if(inCell)
            {
                text.Margin=new Thickness(Math.Clamp(block.ListLevel,0,8)*12*readingZoom,0,0,0);
                text.FontSize=(block.FontSize??11)*PointsToDip*readingZoom;
                text.LineHeight=double.NaN;
            }
            if(block.Kind=="heading")text.FontWeight=FontWeight.SemiBold;
            if(block.ListLabel is {} label)text.Inlines!.Add(new Run(label+" "));
            var budget=new NonprintingBudget();
            AppendReadingInline(text.Inlines!,block,budget,heading:block.Kind=="heading");
            if(documentIndex?.Paragraphs.TryGetValue(block.Id,out var paragraph)==true)
                AddReadingMark(text.Inlines!,"¶",budget,paragraph);
            if(documentIndex?.SectionClosers.TryGetValue(block.Id,out var section)==true)
                AddReadingMark(text.Inlines!,"§",budget,section);
            if(budget.Truncated)ToolTip.SetTip(text,T("Affichage des marques limité dans ce passage","Formatting marks limited in this passage"));
            AddCopyPassage(text,block);
            return text;
        }
        if(block.Kind=="table")
            return RenderReadingTable(block);
        if(block.Kind=="image")return RenderReadingImage(block);
        if(block.Kind=="section")return LocalMarker(block);
        if(block.Kind is "placeholder" or "break" or "field" or "revision" or "note")
        {
            var content=new StackPanel{Spacing=4};content.Children.Add(LocalMarker(block));if(block.Label is not null&&block.Text.Length>0)content.Children.Add(new TextBlock{Text=block.Text,TextWrapping=TextWrapping.Wrap});foreach(var child in block.Children)content.Children.Add(RenderReadingBlock(child));return content;
        }
        var panel=new StackPanel{Spacing=4};if(block.Label is {} contextLabel)panel.Children.Add(new TextBlock{Text=contextLabel,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});if(block.Text.Length>0&&(block.Children.Count==0||block.Kind is not ("run" or "link")))panel.Children.Add(new TextBlock{Text=block.Text,TextWrapping=TextWrapping.Wrap});foreach(var child in block.Children)panel.Children.Add(RenderReadingBlock(child));return panel;
    }
    static void StretchRows(ListBox list)=>list.Styles.Add(new Style(s=>s.OfType<ListBoxItem>()){Setters={new Setter(ContentControl.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch)}});
    void AppendReadingInline(InlineCollection destination,ReadableBlock block,NonprintingBudget budget,bool nested=false,bool heading=false)
    {
        bool nestedParagraph=nested&&block.Kind is "paragraph" or "heading";
        if(nestedParagraph)destination.Add(new LineBreak());
        if(block.Kind=="image"){destination.Add(new InlineUIContainer(RenderReadingImage(block)));return;}
        if(block.Kind=="break"&&block.Text=="\n")
        {
            if(documentIndex?.Breaks.TryGetValue(block.Id,out var newline)==true)AddReadingMark(destination,"↵",budget,newline);
            destination.Add(new LineBreak());return;
        }
        if(block.Kind=="break"&&documentIndex?.BreakKinds.GetValueOrDefault(block.Id)=="page"&&
            documentIndex.Breaks.TryGetValue(block.Id,out var pageBreak))
            AddReadingMark(destination,"↡",budget,pageBreak);
        if(block.Kind=="note"&&readable?.Presentation.Notes.TryGetValue(block.Id,out var note)==true&&note.Verified)
        {destination.Add(new InlineUIContainer(NoteCall(note)){BaselineAlignment=BaselineAlignment.Superscript});return;}
        if(block.Kind=="field")
        {
            if(readable?.Presentation.Fields.TryGetValue(block.Id,out var field)==true)
            {
                destination.Add(new InlineUIContainer(FieldMarker(block,field)));
                foreach(var child in block.Children)AppendReadingInline(destination,child,budget,true,heading);
                return;
            }
            if(readable?.Presentation.FieldDetailNodes.Contains(block.Id)==true)return;
            destination.Add(new InlineUIContainer(LocalMarker(block)));
            // Unmatched instruction remains in diagnostics, never mixed with prose.
            if(block.Label?.StartsWith("Code de champ",StringComparison.Ordinal)==true)return;
            foreach(var child in block.Children)AppendReadingInline(destination,child,budget,true,heading);return;
        }
        if(block.Kind is "placeholder" or "break" or "field" or "revision" or "note" or "section")destination.Add(new InlineUIContainer(LocalMarker(block)));
        if(block.Kind=="link")
        {
            if(block.LinkTarget is {} target&&target.StartsWith('#'))
            {
                var link=new Button{Content=target,Padding=new Thickness(2),Foreground=ReadingAccent,Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
                AutomationProperties.SetName(link,T("Aller au signet : ","Go to bookmark: ")+target);
                link.Click+=(_,_)=>{var mark=readable?.Bookmarks.FirstOrDefault(b=>b.Label==target[1..]);if(mark is not null)NavigateToBlock(mark.BlockId);else{ShowPanel("diagnostics");status.Text=T("Cible du signet introuvable","Bookmark target not found");}};destination.Add(new InlineUIContainer(link));
            }
            else destination.Add(new InlineUIContainer(LocalMarker(block)));
        }
        if(block.Hidden&&block.Kind=="run")destination.Add(new InlineUIContainer(LocalMarker(block)));
        var span=new Span{FontWeight=heading?FontWeight.SemiBold:block.Bold?FontWeight.Bold:FontWeight.Normal,FontStyle=block.Italic?FontStyle.Italic:FontStyle.Normal,TextDecorations=block.Underline?TextDecorations.Underline:[]};
        if(block.Revision!="unchanged")span.Foreground=ReaderPalette.RevisionInk;
        if(block.Revision=="deleted")span.TextDecorations=TextDecorations.Strikethrough;
        if(block.Kind!="break"&&block.Text.Length>0&&(block.Children.Count==0||block.Kind is not ("paragraph" or "heading" or "run" or "link")))AppendSourceText(span.Inlines,block,budget);foreach(var child in block.Children)AppendReadingInline(span.Inlines,child,budget,true,heading);destination.Add(span);
        if(nestedParagraph)destination.Add(new LineBreak());
    }
    void AddCopyPassage(Control text,ReadableBlock block)
    {
        var copy=new MenuItem{Header=T("Copier le texte du passage","Copy passage text")};
        copy.Click+=async(_,_)=>
        {
            if(Clipboard is not {} clipboard||readable is null)return;
            try{await clipboard.SetTextAsync(readable.Presentation.TextFor(block));}
            catch(Exception){status.Text=T("Copie indisponible","Copy unavailable");}
        };
        text.ContextMenu=new ContextMenu{Items={copy}};
    }

    Button LocalMarker(ReadableBlock block)
    {
        string symbol=block.Kind switch{"field"=>"ƒ?","note"=>"?","link"=>"↗","revision"=>block.Revision=="deleted"?"−":"+","section" or "break"=>"ⓘ","image"=>"◇",_=>"·"};
        var button=new Button{Content=symbol,Name="ContextMarker_"+block.Id,Tag=block.Id,FontSize=12,FontWeight=FontWeight.SemiBold,MinWidth=20,MinHeight=21,Padding=new Thickness(3,0),CornerRadius=new CornerRadius(5),Foreground=ReadingAccent,Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
        AutomationProperties.SetName(button,T("Diagnostics de cet élément : ","Diagnostics for this element: ")+MarkerDescription(block));ToolTip.SetTip(button,MarkerDescription(block));
        button.Click+=(_,_)=>ShowElementDiagnostics(block);return button;
    }
    Control RenderReadingImage(ReadableBlock block)
    {
        // Only the bounded, structurally validated PNG profile may reach a native decoder.
        if(block.ResourceId is {} key)
        {
            if(readingImages.TryGetValue(key,out var cached))return ReadingImage(cached);
            if(readable?.Resources.TryGetValue(key,out var renderable)==true)
            {
                long pixels=(long)System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(renderable.AsSpan(16,4))*System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(renderable.AsSpan(20,4));
                if(readingImagePixels+pixels<=16_000_000&&readingImages.Count<32)
                {
                    try{using var stream=new MemoryStream(renderable,false);var bitmap=new Bitmap(stream);readingImages.Add(key,bitmap);readingImagePixels+=pixels;return ReadingImage(bitmap);}
                    catch(ArgumentException){}
                }
            }
        }
        return LocalMarker(block with {Text=T("Image conservée dans l’inventaire ; décodage non qualifié","Image retained in inventory; decoding unqualified")});
    }
    static Image ReadingImage(Bitmap bitmap)=>new(){Source=bitmap,MaxWidth=560,MaxHeight=500,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Left};
}
