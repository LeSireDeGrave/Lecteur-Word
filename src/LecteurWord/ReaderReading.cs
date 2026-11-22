using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
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
    IReadOnlyDictionary<string,LayoutParagraph> readingParagraphs=new Dictionary<string,LayoutParagraph>();
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
    string BlockText(ReadableBlock block)=>readable?.Presentation.TextFor(block)??block.PlainText;
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
        var roots=new List<OutlineEntry>();
        var branch=new Stack<OutlineEntry>();
        foreach(var item in entries.Take(2000))
        {
            var e=new OutlineEntry(item,item.Label);
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
            var layout=readingParagraphs.GetValueOrDefault(block.Id);
            var runs=layout?.Runs.GroupBy(r=>r.Id).ToDictionary(g=>g.Key,g=>g.First().Style,StringComparer.Ordinal);
            var text=new TextBlock{Name=block.Kind=="heading"?"ReadingHeading":"ReadingParagraph",Tag=block.Id,
                TextWrapping=TextWrapping.Wrap,Foreground=ReaderPalette.ReadingInk};
            ConfigureReadingParagraph(text,block,layout,inCell);
            if(block.ListLabel is {} label)text.Inlines!.Add(new Run(label+" "));
            var budget=new NonprintingBudget();
            AppendReadingInline(text.Inlines!,block,budget,styles:runs);
            if(documentIndex?.Paragraphs.TryGetValue(block.Id,out var paragraph)==true)
                AddReadingMark(text.Inlines!,"¶",budget,paragraph);
            if(documentIndex?.SectionClosers.TryGetValue(block.Id,out var section)==true)
                AddReadingMark(text.Inlines!,"§",budget,section);
            if(budget.Truncated)ToolTip.SetTip(text,T("Affichage des marques limité dans ce passage","Formatting marks limited in this passage"));
            AddCopyPassage(text,block);
            AddReadingLinks(text,block);
            return WithTerminalTabLeader(text,block,layout?.Properties);
        }
        if(IsFieldDetail(block))return new Border();
        if(block.Kind=="field")
        {
            var result=new StackPanel();
            if(readable?.Presentation.Fields.GetValueOrDefault(block.Id) is {Matched:true})
                foreach(var child in block.Children)result.Children.Add(RenderReadingBlock(child,inCell));
            return result;
        }
        if(block.Kind=="table")
            return RenderReadingTable(block);
        if(block.Kind=="image")return RenderReadingImage(block);
        if(block.Kind=="section")return LocalMarker(block);
        if(block.Kind is "placeholder" or "break" or "revision" or "note")
        {
            var content=new StackPanel{Spacing=4};content.Children.Add(LocalMarker(block));if(block.Label is not null&&block.Text.Length>0)content.Children.Add(new TextBlock{Text=block.Text,TextWrapping=TextWrapping.Wrap});foreach(var child in block.Children)content.Children.Add(RenderReadingBlock(child));return content;
        }
        var panel=new StackPanel{Spacing=4};if(block.Label is {} contextLabel)panel.Children.Add(new TextBlock{Text=contextLabel,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});if(block.Text.Length>0&&(block.Children.Count==0||block.Kind is not ("run" or "link")))panel.Children.Add(new TextBlock{Text=block.Text,TextWrapping=TextWrapping.Wrap});foreach(var child in block.Children)panel.Children.Add(RenderReadingBlock(child));return panel;
    }
    static void StretchRows(ListBox list)=>list.Styles.Add(new Style(s=>s.OfType<ListBoxItem>()){Setters={new Setter(ContentControl.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch)}});
    bool IsFieldDetail(ReadableBlock block)=>readable?.Presentation.FieldDetailNodes.Contains(block.Id)==true||
        block.Kind=="field"&&block.FieldRole=="instruction"&&block.Label?.StartsWith("Code de champ",StringComparison.Ordinal)==true;

    void AppendReadingInline(InlineCollection destination,ReadableBlock block,NonprintingBudget budget,bool nested=false,
        IReadOnlyDictionary<string,LayoutStyle>? styles=null)
    {
        if(IsFieldDetail(block))return;
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
            if(readable?.Presentation.Fields.GetValueOrDefault(block.Id) is {Matched:true})
                foreach(var child in block.Children)AppendReadingInline(destination,child,budget,true,styles);
            return;
        }
        if(block.Kind is "placeholder" or "break" or "revision" or "note" or "section")destination.Add(new InlineUIContainer(LocalMarker(block)));
        if(block.Hidden&&block.Kind=="run"&&BlockText(block).Length>0)destination.Add(new InlineUIContainer(LocalMarker(block)));
        var style=styles?.GetValueOrDefault(block.Id);
        var span=new Span{FontWeight=(style?.Bold??block.Bold)?FontWeight.Bold:FontWeight.Normal,
            FontStyle=(style?.Italic??block.Italic)?FontStyle.Italic:FontStyle.Normal,
            TextDecorations=(style?.Underline??block.Underline)?TextDecorations.Underline:[]};
        if(block.Kind=="text")
        {
            if(block.FontSize is {} || style is not null)span.FontSize=(style?.Size??block.FontSize!.Value)*PointsToDip*readingZoom;
            if(style?.Font is {} face)span.FontFamily=new FontFamily(face.UsedFamily);
            if(style?.Color is {Length:6} color)span.Foreground=Brush("#"+color);
        }
        if(block.Revision!="unchanged")span.Foreground=ReaderPalette.RevisionInk;
        if(block.Revision=="deleted")span.TextDecorations=TextDecorations.Strikethrough;
        if(block.Kind!="break"&&block.Text.Length>0&&(block.Children.Count==0||block.Kind is not ("paragraph" or "heading" or "run" or "link")))AppendSourceText(span.Inlines,block,budget);foreach(var child in block.Children)AppendReadingInline(span.Inlines,child,budget,true,styles);destination.Add(span);
        if(nestedParagraph)destination.Add(new LineBreak());
    }
    void ConfigureReadingParagraph(TextBlock text,ReadableBlock block,LayoutParagraph? layout,bool inCell)
    {
        double scale=PointsToDip*readingZoom;
        var first=layout?.Runs.FirstOrDefault(r=>r.Kind=="text"&&r.Text.Length>0&&
            readable?.Presentation.FieldDetailNodes.Contains(r.Id)!=true)?.Style;
        var basis=first??layout?.Runs.LastOrDefault(r=>r.Kind=="paragraphMark")?.Style;
        text.FontSize=(basis?.Size??block.FontSize??(block.Kind=="heading"?18:12))*scale;
        if(basis?.Font is {} face)text.FontFamily=new FontFamily(face.UsedFamily);
        text.FontWeight=(basis?.Bold??block.Bold)?FontWeight.Bold:FontWeight.Normal;
        text.FontStyle=(basis?.Italic??block.Italic)?FontStyle.Italic:FontStyle.Normal;
        if(layout?.Properties is not {} p)
        {
            text.Margin=inCell?default:new Thickness(0,0,0,6*scale);
            text.TextAlignment=ReadingAlignment(block.Alignment);
            return;
        }
        double left=p.Left*scale;
        if(block.ListLabel is not null)left=(p.Left+Math.Min(0,p.FirstLine))*scale;
        text.Margin=new Thickness(left,p.Before*scale,p.Right*scale,p.After*scale);
        text.TextAlignment=ReadingAlignment(p.Alignment);
        var metrics=new Typeface(text.FontFamily).GlyphTypeface.Metrics;
        double natural=text.FontSize*metrics.LineSpacing/Math.Max(1d,metrics.DesignEmHeight);
        text.LineSpacing=p.LineRule=="auto"?Math.Max(0,natural*(p.Line/240-1)):Math.Max(0,p.Line/20*scale-natural);
    }

    static TextAlignment ReadingAlignment(string? alignment)=>alignment switch
    {
        "center"=>TextAlignment.Center,"right" or "end"=>TextAlignment.Right,"both"=>TextAlignment.Justify,_=>TextAlignment.Left
    };

    Control WithTerminalTabLeader(TextBlock text,ReadableBlock block,ParagraphLayout? properties)
    {
        // Local reading profile for a single terminal tab, such as a stored TOC entry
        // with an empty page result. The dots are document decoration, excluded from copy.
        if(properties is not {Alignment:"left",Tabs.Count:1}||properties.Tabs[0] is not {Alignment:"right",Leader:"dot"} tab)
            return text;
        string content=BlockText(block);
        if(!content.EndsWith('\t')||content.IndexOf('\t')!=content.Length-1)return text;
        double end=(tab.Position-properties.Left)*PointsToDip*readingZoom;
        return new Grid{Children={text,new ReadingTabLeader(text,end)
            {Name="ReadingTabLeader",Margin=text.Margin,IsHitTestVisible=false}}};
    }

    sealed class ReadingTabLeader(TextBlock text,double end) : Control
    {
        public override void Render(DrawingContext context)
        {
            if(text.TextLayout.TextLines is not {Count:1} lines)return;
            var line=lines[0];
            var face=new Typeface(text.FontFamily,text.FontStyle,text.FontWeight);
            var dot=new FormattedText(".",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,text.FontSize,text.Foreground);
            double right=Math.Min(end,Bounds.Width);
            double available=right-line.Start-line.Width;
            if(dot.Width<=0||available<dot.Width)return;
            int count=Math.Min(512,(int)(available/dot.Width));
            var leader=new FormattedText(new string('.',count),System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,face,text.FontSize,text.Foreground);
            if(leader.Width<=available)
                context.DrawText(leader,new Point(right-leader.Width,line.Baseline-leader.Baseline));
        }
    }

    void AddReadingLinks(TextBlock text,ReadableBlock block)
    {
        if(readable is null)return;
        var links=Descendants(block).Where(b=>b.Kind=="link"&&b.LinkTarget?.StartsWith('#')==true&&BlockText(b).Trim().Length>0)
            .DistinctBy(b=>b.LinkTarget).ToArray();
        foreach(var link in links)
        {
            string name=link.LinkTarget![1..];
            string label=BlockText(link).Trim();
            var action=new MenuItem{Header=T("Suivre le lien · ","Follow link · ")+label};
            action.Click+=(_,_)=>FollowReadingBookmark(name);
            text.ContextMenu!.Items.Add(action);
            if(links.Length!=1||BlockText(block).Trim()!=label)continue;
            text.Cursor=new Cursor(StandardCursorType.Hand);
            text.Focusable=true;
            ToolTip.SetTip(text,T("Aller à « ","Go to “")+label+T(" »","”"));
            text.PointerReleased+=(_,e)=>
            {
                if(e.InitialPressMouseButton!=MouseButton.Left)return;
                FollowReadingBookmark(name);
                e.Handled=true;
            };
            text.KeyDown+=(_,e)=>
            {
                if(e.Key!=Key.Enter)return;
                FollowReadingBookmark(name);
                e.Handled=true;
            };
        }
    }

    void FollowReadingBookmark(string name)
    {
        var marks=readable?.Bookmarks.Where(b=>b.Label==name).DistinctBy(b=>b.Source).Take(2).ToArray();
        if(marks is {Length:1}&&NavigateToBlock(marks[0].BlockId))return;
        ShowPanel("diagnostics");
        status.Text=T("Cible du signet absente ou ambiguë","Missing or ambiguous bookmark target");
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
