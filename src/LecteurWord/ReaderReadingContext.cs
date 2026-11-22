using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LecteurWord.Flow;

namespace LecteurWord;
public sealed partial class ReaderWindow
{
    IBrush ReadingAccent=>Palette.Accent;
    public string? CurrentNoteReferenceId {get;private set;}
    public int CurrentSection {get;private set;}=1;
    public Control? ActiveReadingContext {get;private set;}
    Flyout? readingFlyout;
    void CloseReadingContext(){readingFlyout?.Hide();readingFlyout=null;notePane.IsVisible=false;notePane.Child=null;ActiveReadingContext=null;UpdateNotePlacement();MarkNoteOrigin();}
    Control ContextCard(Control content)
    {
        var card=new Border{Child=content,Padding=new Thickness(18),CornerRadius=new CornerRadius(10),Background=Palette.Field};
        card.SetValue(TextElement.ForegroundProperty,ActualThemeVariant==ReaderPalette.BlueVariant?Palette.Ink:ReaderPalette.ReadingInk);return card;
    }
    void RefreshReadingAppearance()
    {
        var activeNote=notePane.IsVisible&&CurrentNoteReferenceId is {} id?readable?.Presentation.Notes.GetValueOrDefault(id):null;
        CloseReadingContext();
        readingList.Background=ReaderPalette.ReadingPaper;
        readingList.Foreground=ReaderPalette.ReadingInk;
        // Recreate only the realized templates when theme/language changes.
        SetReadingTemplate();
        if(activeNote is not null)ShowNotePane(activeNote);
    }
    string MarkerDescription(ReadableBlock b)=>b.Kind switch
    {
        "field"=>T("Champ enregistré, non recalculé","Stored field, not recalculated"),
        "note"=>T("Correspondance de note non vérifiée","Unverified note reference"),
        "revision"=>b.Revision=="deleted"?T("Texte supprimé conservé","Deleted text retained"):T("Texte inséré conservé","Inserted text retained"),
        "section"=>T("Changement de section · détails disponibles","Section change · details available"),
        "image"=>T("Image non représentée · détails disponibles","Unrepresented image · details available"),
        "link"=>T("Lien externe · cible disponible dans les détails","External link · target available in details"),
        "break"=>T("Saut du document · disposition adaptée","Document break · adapted layout"),
        _=>T("Contenu affiché partiellement · détails disponibles","Partially displayed content · details available")
    };
    Button NoteCall(ReadableNoteLink note)
    {
        var button=new Button{Content=note.Number,Name="NoteCall_"+note.ReferenceId,Tag=note.ReferenceId,FontSize=12,FontWeight=FontWeight.SemiBold,Foreground=ReadingAccent,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(3,0),MinWidth=18,MinHeight=22};
        string kind=note.Kind=="footnote"?T("Note de bas de page ","Footnote "):T("Note de fin ","Endnote ");
        AutomationProperties.SetName(button,kind+note.Number);ToolTip.SetTip(button,kind+note.Number+T(" · ouvrir la note"," · open note"));
        button.Click+=(_,_)=>OpenNote(note,button);return button;
    }
    void OpenNote(ReadableNoteLink note,Control anchor)
    {
        if(!readingList.GetVisualDescendants().OfType<Button>().Any(b=>b.Name=="NoteCall_"+note.ReferenceId))NavigateToBlock(note.OriginBlockId);
        ShowNotePane(note);
    }
    void FillNotes()
    {
        if(readable is null)return;
        navItems.Children.Add(new TextBlock{Text=T("Numérotation de lecture. Cliquez pour lire une note, puis revenir au passage.","Reading numbers. Open a note, then return to its passage."),TextWrapping=TextWrapping.Wrap,FontSize=12});
        foreach(var note in readable.Presentation.Notes.Values.Where(n=>n.Verified))
        {
            var button=NoteCall(note);button.Content=(note.Kind=="footnote"?T("Bas de page ","Footnote "):T("Fin ","Endnote "))+note.Number;navItems.Children.Add(button);
        }
    }
    public bool ShowSection(int section)
    {
        if(readable?.Presentation.Sections.Any(s=>s.Number==section)!=true)return false;
        CurrentSection=section;ShowPanel("sections");return true;
    }
    void FillSections()
    {
        if(readable is null)return;
        foreach(var section in readable.Presentation.Sections)
        {
            var button=new Button{Content=section.Number==0?T("Contenus non rattachés","Unassigned content"):T("Section ","Section ")+section.Number,HorizontalAlignment=HorizontalAlignment.Stretch};
            button.Click+=(_,_)=>{CurrentSection=section.Number;FillPanel();};navItems.Children.Add(button);
        }
        var selected=readable.Presentation.Sections.FirstOrDefault(s=>s.Number==CurrentSection);if(selected is null)return;
        if(selected.Stories.Count==0){navItems.Children.Add(new TextBlock{Text=T("Aucun en-tête ou pied rattaché à cette section.","No header or footer assigned to this section."),TextWrapping=TextWrapping.Wrap});return;}
        var open=new Button{Content=T("Lire les en-têtes et pieds…","Read headers and footers…"),HorizontalAlignment=HorizontalAlignment.Stretch};
        open.Click+=(_,_)=>OpenStories(selected,open);navItems.Children.Add(open);
    }
    void OpenStories(ReadableSection selected,Control anchor)
    {
            var content=new StackPanel{Spacing=16,Width=510};content.Children.Add(new TextBlock{Text=T("Section ","Section ")+selected.Number,FontSize=20,FontWeight=FontWeight.SemiBold});
            content.Children.Add(new TextBlock{Text=T("Contenus de section · champs enregistrés, non recalculés. Aucun placement sur des pages n’est supposé.","Section content · stored fields, not recalculated. No page placement is assumed."),TextWrapping=TextWrapping.Wrap,FontSize=12});
            foreach(var story in selected.Stories)
            {
                string variant=story.Variant switch{"first"=>T("première page","first page"),"even"=>T("pages paires","even pages"),"default"=>T("principal","default"),_=>T("non rattaché","unassigned")};
                content.Children.Add(new TextBlock{Text=(story.Kind=="header"?T("En-tête · ","Header · "):T("Pied de page · ","Footer · "))+variant+(story.Inherited?T(" · lié à la section précédente"," · linked to previous section"):""),FontSize=15,FontWeight=FontWeight.SemiBold});
                if(!story.Verified)content.Children.Add(new TextBlock{Text=T("Correspondance incertaine · contenu conservé","Uncertain assignment · content retained"),TextWrapping=TextWrapping.Wrap,FontSize=12});
                foreach(var block in story.Content)content.Children.Add(RenderReadingBlock(block));
            }
        var card=ContextCard(new ScrollViewer{Content=content,MaxHeight=520,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
            readingFlyout?.Hide();readingFlyout=new Flyout{Content=card,Placement=PlacementMode.Right};ActiveReadingContext=card;readingFlyout.ShowAt(anchor);
    }
    Button FieldMarker(ReadableBlock block,ReadableFieldValue field)
    {
        var button=LocalMarker(block);
        button.Content=field.Matched&&field.Known&&field.HasStoredValue?"ƒ":field.HasStoredValue?"ƒ?":"ƒ∅";
        string message=field.Known?(field.HasStoredValue?T("Valeur enregistrée, non recalculée","Stored value, not recalculated"):T("Aucune valeur enregistrée","No stored value")):T("Champ non pris en charge · valeur enregistrée si disponible","Unsupported field · stored value if available");
        ToolTip.SetTip(button,(field.Name.Length>0?field.Name+" · ":"")+message);AutomationProperties.SetName(button,message);
        return button;
    }
}
