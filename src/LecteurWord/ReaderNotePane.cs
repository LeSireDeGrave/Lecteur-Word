using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    readonly Grid readingWorkspace=new(){Name="ReadingWorkspace",ColumnDefinitions=new("*,0"),RowDefinitions=new("*,0")};
    readonly Border notePane=new(){Name="NotePane",IsVisible=false,Margin=new Thickness(10,0,0,0)};
    public bool NotePaneVisible=>notePane.IsVisible;
    public string NotePanePlacement {get;private set;}="closed";
    bool noteOriginLayoutQueued;
    void InitializeNotePane()
    {
        readingWorkspace.Children.Add(readingList);readingWorkspace.Children.Add(notePane);
        readingWorkspace.SizeChanged+=(_,_)=>UpdateNotePlacement();
        readingList.SizeChanged+=(_,_)=>readingList.Padding=readingList.Bounds.Width<640?new Thickness(24,20,24,30):new Thickness(40,24,40,40);
        // Selection must never paint the entire source paragraph of a note.
        readingList.Styles.Add(new Avalonia.Styling.Style(s=>s.OfType<ListBoxItem>().Class(":selected"))
        {Setters={new Setter(BackgroundProperty,Brushes.Transparent),new Setter(BorderBrushProperty,Brushes.Transparent)}});
        Closed+=(_,_)=>CloseReadingContext();
    }
    void UpdateNotePlacement()
    {
        double width=readingWorkspace.Bounds.Width,height=readingWorkspace.Bounds.Height;
        if(!notePane.IsVisible)
        {
            readingWorkspace.ColumnDefinitions[1].Width=new GridLength(0);readingWorkspace.RowDefinitions[1].Height=new GridLength(0);NotePanePlacement="closed";return;
        }
        if(width>=920)
        {
            NotePanePlacement="right";Grid.SetColumn(notePane,1);Grid.SetRow(notePane,0);
            readingWorkspace.ColumnDefinitions[1].Width=new GridLength(Math.Clamp(width*.34,310,380));readingWorkspace.RowDefinitions[1].Height=new GridLength(0);
            notePane.Margin=new Thickness(12,0,0,0);
        }
        else
        {
            NotePanePlacement="bottom";Grid.SetColumn(notePane,0);Grid.SetRow(notePane,1);
            readingWorkspace.ColumnDefinitions[1].Width=new GridLength(0);
            readingWorkspace.RowDefinitions[1].Height=new GridLength(Math.Max(150,Math.Min(Math.Clamp(height*.45,190,280),height-200)));
            notePane.Margin=new Thickness(0,10,0,0);
        }
        if(!noteOriginLayoutQueued)
        {
            noteOriginLayoutQueued=true;Dispatcher.UIThread.Post(()=>
            {
                noteOriginLayoutQueued=false;if(!notePane.IsVisible)return;
                readingList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.Name=="NoteCall_"+CurrentNoteReferenceId)?.BringIntoView();MarkNoteOrigin();
            },DispatcherPriority.Loaded);
        }
    }
    void MarkNoteOrigin()
    {
        foreach(var call in readingList.GetVisualDescendants().OfType<Button>().Where(b=>b.Name?.StartsWith("NoteCall_",StringComparison.Ordinal)==true))
        {
            bool active=notePane.IsVisible&&call.Tag as string==CurrentNoteReferenceId;
            call.BorderThickness=new Thickness(0,0,0,active?1:0);call.BorderBrush=ReadingAccent;call.Background=Brushes.Transparent;
        }
    }
    void FocusNoteOrigin(string referenceId)
    {
        Dispatcher.UIThread.Post(()=>
        {
            var call=readingList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.Name=="NoteCall_"+referenceId);
            call?.BringIntoView();call?.Focus();
        },DispatcherPriority.Loaded);
    }
    void DismissNote(bool returnToPassage)
    {
        string? id=CurrentNoteReferenceId;
        ReadableNoteLink? note=id is not null?readable?.Presentation.Notes.GetValueOrDefault(id):null;
        CloseReadingContext();
        if(returnToPassage&&note is not null)NavigateToBlock(note.OriginBlockId);
        if(id is not null)FocusNoteOrigin(id);
    }
    void ShowNotePane(ReadableNoteLink note)
    {
        CloseReadingContext();CurrentNoteReferenceId=note.ReferenceId;
        var root=new Grid{RowDefinitions=new("Auto,*,Auto"),MinWidth=0};
        KeyboardNavigation.SetTabNavigation(root,KeyboardNavigationMode.Cycle);
        var heading=new Grid{ColumnDefinitions=new("*,Auto"),Margin=new Thickness(0,0,0,12)};
        var caption=new StackPanel{Spacing=3};
        caption.Children.Add(new TextBlock{Text=(note.Kind=="footnote"?T("Note de bas de page ","Footnote "):T("Note de fin ","Endnote "))+note.Number,FontSize=17,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});
        caption.Children.Add(new TextBlock{Text=T("Numérotation de lecture","Reading number"),FontSize=11,Opacity=.75});heading.Children.Add(caption);
        var close=new Button{Name="NoteClose",Content=Icons.Make("cancel"),Width=30,Height=30,Padding=new Thickness(6),Background=Brushes.Transparent,BorderThickness=new Thickness(0),VerticalAlignment=VerticalAlignment.Top};
        AutomationProperties.SetName(close,T("Fermer la note (Échap)","Close note (Escape)"));ToolTip.SetTip(close,T("Fermer la note · Échap","Close note · Escape"));close.Click+=(_,_)=>DismissNote(false);Grid.SetColumn(close,1);heading.Children.Add(close);root.Children.Add(heading);
        var text=new StackPanel{Spacing=6};foreach(var block in note.Content)text.Children.Add(RenderReadingBlock(block));
        var scroll=new ScrollViewer{Content=text,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};Grid.SetRow(scroll,1);root.Children.Add(scroll);
        var back=new Button{Content=T("Revenir au passage","Return to passage"),Name="NoteReturn",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,10,0,0)};
        AutomationProperties.SetName(back,T("Revenir à l’appel de note","Return to note reference"));back.Click+=(_,_)=>DismissNote(true);Grid.SetRow(back,2);root.Children.Add(back);
        var card=ContextCard(root);notePane.Child=card;notePane.IsVisible=true;ActiveReadingContext=card;UpdateNotePlacement();MarkNoteOrigin();
        Dispatcher.UIThread.Post(()=>
        {
            var call=readingList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.Name=="NoteCall_"+note.ReferenceId);call?.BringIntoView();MarkNoteOrigin();close.Focus();
        },DispatcherPriority.Loaded);
    }
}
