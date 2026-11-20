using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    ReadableDocument? readable;
    bool simplified,updatingReadingMode;
    readonly ComboBox readingMode=new(){Name="ReadingMode",Margin=new Thickness(8),MinWidth=155,VerticalAlignment=VerticalAlignment.Top};
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
        Closed+=(_,_)=>{foreach(var image in readingImages.Values)image.Dispose();};
    }
    void UpdateReadingMode()
    {
        if(!simplified&&notePane.IsVisible)CloseReadingContext();
        updatingReadingMode=true;
        readingMode.ItemsSource=new[]{T("Lecture paginée","Paginated reading"),T("Lecture simplifiée","Simplified reading")};readingMode.SelectedIndex=simplified?1:0;
        readingMode.IsVisible=readable?.Available==true;readingMode.IsEnabled=composition?.Available==true;
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
        int index=source.ToList().FindIndex(b=>Descendants(b).Any(n=>n.Id==id));
        if(index<0)return false;
        if(!simplified&&composition?.Available==true)
        {
            var target=Descendants(source[index]).First(b=>b.Id==id);
            int found=composition.Pages.ToList().FindIndex(p=>p.Lines.Any(l=>l.Source==target.Source));
            if(found>=0){GoToPage(found);CurrentBlockId=id;return true;}
        }
        simplified=true;CurrentBlockId=id;UpdateUi();readingList.SelectedIndex=-1;readingList.ScrollIntoView(index);
        return true;
    }
    void FillReadingNavigation(bool bookmarks)
    {
        var entries=bookmarks?readable?.Bookmarks:readable?.Outline;
        if(entries is null||entries.Count==0){navItems.Children.Add(new TextBlock{Text=T("Aucun élément interprété dans cette vue.","No interpreted item in this view."),TextWrapping=TextWrapping.Wrap});return;}
        var headingLabels=bookmarks?[]:readable!.Blocks.SelectMany(Descendants).Where(b=>b.Kind=="heading").GroupBy(b=>b.Source).ToDictionary(g=>g.Key,g=>readable.Presentation.TextFor(g.First()));
        foreach(var item in entries)
        {
            string label=headingLabels.GetValueOrDefault(item.Source,item.Label);
            var button=new Button{Content=new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap},HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,Margin=new Thickness(Math.Clamp(item.Level,0,8)*6,0,0,0)};
            AutomationProperties.SetName(button,label);button.Click+=(_,_)=>NavigateToBlock(item.BlockId);navItems.Children.Add(button);
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
    Control RenderReadingBlock(ReadableBlock block)
    {
        if(block.Kind is "paragraph" or "heading")
        {
            double titleSize=block.HeadingLevel switch{0 or 1=>28,2=>24,3=>21,_=>19};
            var text=new TextBlock{Name=block.Kind=="heading"?"ReadingHeading":"ReadingParagraph",Tag=block.Id,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(Math.Clamp(block.ListLevel,0,8)*16,block.Kind=="heading"?24:0,0,block.Kind=="heading"?12:16),FontSize=block.Kind=="heading"?titleSize:17,LineHeight=block.Kind=="heading"?titleSize*1.4:28,
                TextAlignment=block.Alignment switch{"center"=>TextAlignment.Center,"right" or "end"=>TextAlignment.Right,"both"=>TextAlignment.Justify,_=>TextAlignment.Left}};
            if(block.Kind=="heading")text.FontWeight=FontWeight.SemiBold;
            if(block.ListLabel is {} label)text.Inlines!.Add(new Run(label+" "));
            AppendReadingInline(text.Inlines!,block,heading:block.Kind=="heading");return text;
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
    Control RenderReadingTable(ReadableBlock block)
    {
        var table=new StackPanel{Spacing=0,Margin=new Thickness(0,8)};
        table.Children.Add(new TextBlock{Text=T("Tableau · disposition simplifiée · défilement interne","Table · simplified layout · internal scrolling"),FontSize=11,Opacity=.75,TextWrapping=TextWrapping.Wrap});
        var rows=new ListBox{ItemsSource=block.Children,Height=Math.Clamp(block.Children.Count*64,64,480),Background=Brushes.Transparent,BorderThickness=new Thickness(0)};StretchRows(rows);
        rows.ItemTemplate=new FuncDataTemplate<ReadableBlock>((row,_)=>
        {
            if(row is null)return new Border();
            if(row.Kind!="row")return RenderReadingBlock(row);
            int columns=row.Children.Sum(c=>Math.Clamp(c.ColumnSpan,1,64));
            if(columns>64)
            {
                var linear=new StackPanel();linear.Children.Add(new TextBlock{Text=T("Ligne large présentée verticalement","Wide row presented vertically"),FontSize=11});foreach(var cell in row.Children)linear.Children.Add(RenderReadingBlock(cell));return linear;
            }
            var grid=new Grid{ColumnDefinitions=new ColumnDefinitions(string.Join(',',Enumerable.Repeat("*",Math.Max(1,columns))))};int column=0;
            foreach(var cell in row.Children)
            {
                var content=new StackPanel{Spacing=3};
                if(cell.VerticalMerge is not null)content.Children.Add(LocalMarker(cell with{Kind="placeholder",Label="Fusion verticale : "+cell.VerticalMerge}));
                if(cell.Kind!="cell")content.Children.Add(RenderReadingBlock(cell));else foreach(var child in cell.Children)content.Children.Add(RenderReadingBlock(child));
                var border=new Border{BorderBrush=Brushes.Gray,BorderThickness=new Thickness(.5),Padding=new Thickness(8),Child=content};Grid.SetColumn(border,column);int span=Math.Clamp(cell.ColumnSpan,1,64);Grid.SetColumnSpan(border,span);column+=span;grid.Children.Add(border);
            }
            return grid;
        });table.Children.Add(rows);return table;
    }
    static void StretchRows(ListBox list)=>list.Styles.Add(new Style(s=>s.OfType<ListBoxItem>()){Setters={new Setter(ContentControl.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch)}});
    void AppendReadingInline(InlineCollection destination,ReadableBlock block,bool nested=false,bool heading=false)
    {
        bool nestedParagraph=nested&&block.Kind is "paragraph" or "heading";
        if(nestedParagraph)destination.Add(new LineBreak());
        if(block.Kind=="image"){destination.Add(new InlineUIContainer(RenderReadingImage(block)));return;}
        if(block.Kind=="break"&&block.Text=="\n"){destination.Add(new LineBreak());return;}
        if(block.Kind=="note"&&readable?.Presentation.Notes.TryGetValue(block.Id,out var note)==true&&note.Verified)
        {destination.Add(new InlineUIContainer(NoteCall(note)){BaselineAlignment=BaselineAlignment.Superscript});return;}
        if(block.Kind=="field")
        {
            if(readable?.Presentation.Fields.TryGetValue(block.Id,out var field)==true)
            {
                destination.Add(new InlineUIContainer(FieldMarker(block,field)));
                foreach(var child in block.Children)AppendReadingInline(destination,child,true,heading);
                return;
            }
            if(readable?.Presentation.FieldDetailNodes.Contains(block.Id)==true)return;
            destination.Add(new InlineUIContainer(LocalMarker(block)));
            // Unmatched instruction remains in diagnostics, never mixed with prose.
            if(block.Label?.StartsWith("Code de champ",StringComparison.Ordinal)==true)return;
            foreach(var child in block.Children)AppendReadingInline(destination,child,true,heading);return;
        }
        if(block.Kind is "placeholder" or "break" or "field" or "revision" or "note" or "section")destination.Add(new InlineUIContainer(LocalMarker(block)));
        if(block.Kind=="link")
        {
            if(block.LinkTarget is {} target&&target.StartsWith('#'))
            {
                var link=new Button{Content=target,Padding=new Thickness(2),Foreground=Brushes.DodgerBlue,Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
                AutomationProperties.SetName(link,T("Aller au signet : ","Go to bookmark: ")+target);
                link.Click+=(_,_)=>{var mark=readable?.Bookmarks.FirstOrDefault(b=>b.Label==target[1..]);if(mark is not null)NavigateToBlock(mark.BlockId);else{ShowPanel("diagnostics");status.Text=T("Cible du signet introuvable","Bookmark target not found");}};destination.Add(new InlineUIContainer(link));
            }
            else destination.Add(new InlineUIContainer(LocalMarker(block)));
        }
        if(block.Hidden&&block.Kind=="run")destination.Add(new InlineUIContainer(LocalMarker(block)));
        var span=new Span{FontWeight=heading?FontWeight.SemiBold:block.Bold?FontWeight.Bold:FontWeight.Normal,FontStyle=block.Italic?FontStyle.Italic:FontStyle.Normal,TextDecorations=block.Underline?TextDecorations.Underline:[]};
        if(block.Revision!="unchanged")span.Foreground=new SolidColorBrush(Color.Parse(ReadingDark?"#DCC2AD":"#80533B"));
        if(block.Revision=="deleted")span.TextDecorations=TextDecorations.Strikethrough;
        if(block.Kind!="break"&&block.Text.Length>0&&(block.Children.Count==0||block.Kind is not ("paragraph" or "heading" or "run" or "link")))span.Inlines.Add(new Run(block.Text));foreach(var child in block.Children)AppendReadingInline(span.Inlines,child,true,heading);destination.Add(span);
        if(nestedParagraph)destination.Add(new LineBreak());
    }
    Button LocalMarker(ReadableBlock block)
    {
        string symbol=block.Kind switch{"field"=>"ƒ?","note"=>"?","link"=>"↗","revision"=>block.Revision=="deleted"?"−":"+","section"=>"§","break"=>"↵","image"=>"◇",_=>"·"};
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
