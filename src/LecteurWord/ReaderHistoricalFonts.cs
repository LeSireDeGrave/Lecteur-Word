using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using LecteurWord.Flow;
using LecteurWord.Platform;

namespace LecteurWord;

internal sealed record HistoricalFontPreference(string Snapshot, HistoricalFontChoice Choice);

internal sealed class HistoricalFontPreferences(string path)
{
    internal HistoricalFontPreference[] Read()
    {
        try{return ReadVerified();}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){return [];}
    }

    internal void Set(string snapshot,HistoricalFontChoice key,HistoricalFontChoice? value)
    {
        PlatformFiles.MakePrivateDirectory(Path.GetDirectoryName(path)!);
        string lockFile=path+".lock";
        if(new FileInfo(lockFile).LinkTarget is not null)throw new IOException("Verrou de polices inaccessible.");
        var options=new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None};
        if(OperatingSystem.IsLinux())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        using var guard=new FileStream(lockFile,options);
        var existing=ReadVerified().Where(p=>p.Snapshot!=snapshot||!SameSource(p.Choice,key)).ToList();
        if(value is not null&&existing.Count>=128)throw new InvalidDataException("Capacité des préférences de polices atteinte.");
        if(value is not null)existing.Insert(0,new(snapshot,value));
        PlatformFiles.Write(path,existing.ToArray());
    }

    HistoricalFontPreference[] ReadVerified()
    {
        if(new FileInfo(path).LinkTarget is not null)throw new IOException("Configuration de polices inaccessible.");
        var entries=PlatformFiles.ReadOptional<HistoricalFontPreference[]>(path)??[];
        if(entries.Length>128||entries.Any(p=>p is not {Snapshot.Length:64,Choice.Valid:true}||!p.Snapshot.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("Configuration de polices invalide.");
        return entries;
    }

    internal static bool SameSource(HistoricalFontChoice a,HistoricalFontChoice b)=>
        a.Code==b.Code&&a.Packed==b.Packed&&a.Family.Equals(b.Family,StringComparison.OrdinalIgnoreCase);
}

public sealed partial class ReaderWindow
{
    readonly HistoricalFontPreferences? historicalFontPreferences;
    readonly object historicalFontGate=new();
    readonly Dictionary<string,List<(HistoricalFontChoice Key,HistoricalFontChoice? Choice)>> temporaryHistoricalFonts=new(StringComparer.Ordinal);

    HistoricalFontChoice[] ReadHistoricalChoices(string? snapshot)
    {
        if(snapshot is null)return [];
        lock(historicalFontGate)
        {
            var choices=historicalFontPreferences?.Read().Where(p=>p.Snapshot==snapshot).Select(p=>p.Choice).ToList()??[];
            if(temporaryHistoricalFonts.TryGetValue(snapshot,out var changes))
                foreach(var entry in changes)
                {
                    choices.RemoveAll(c=>HistoricalFontPreferences.SameSource(c,entry.Key));
                    if(entry.Choice is not null)choices.Add(entry.Choice);
                }
            return [..choices];
        }
    }

    IEnumerable<(TextFragment Fragment,bool Packed)> HistoricalFontConcerns()
    {
        if(nativePageLease?.Snapshot.Typography is not {} typography)yield break;
        foreach(var p in typography.Paragraphs)
        {
            foreach(var f in p.Fragments)
                if(NeedsReview(f))yield return (f,false);
            foreach(var c in p.Controls)
                if(c.Symbol is {} f&&NeedsReview(f))yield return (f,true);
        }
        static bool NeedsReview(TextFragment f)=>f.LegacySymbol?.Method is "missing-marker" or "user" or "unresolved";
    }

    async Task ShowHistoricalFontsAsync(Window owner)
    {
        var dialog=PlatformDialog(T("Préférences de polices","Font preferences"),650,550);
        var panel=DialogStack(dialog);
        var document=activeDocumentPath;
        var reading=CaptureReading();
        string? snapshot=inspection?.Manifest.SourceSha256;
        var groups=HistoricalFontConcerns().GroupBy(x=>(x.Fragment.LegacySymbol!.Family,x.Fragment.LegacySymbol.Code,x.Packed))
            .Take(100).ToArray();
        if(groups.Length==0||document is null||snapshot is null)
        {
            panel.Children.Add(DialogText(T("Aucun glyphe historique à examiner dans le document ouvert.",
                "No historical glyph needs review in the open document.")));
            ActionButton(panel,T("Fermer","Close"),dialog.Close);
            await dialog.ShowDialog(owner);
            return;
        }
        var entries=new ComboBox { Name="HistoricalGlyph", HorizontalAlignment=HorizontalAlignment.Stretch,
            ItemsSource=groups.Select(group=>$"{group.Key.Family} · U+{group.Key.Code:X4} · {group.Count()}").ToArray(),SelectedIndex=0 };
        var family=new ComboBox { Name="HistoricalFontFamily",HorizontalAlignment=HorizontalAlignment.Stretch,
            ItemsSource=FontResolver.InstalledFamilies() };
        var code=new TextBox {Name="HistoricalFontCode",MaxLength=6};
        var persistent=new CheckBox { Name="PersistHistoricalFont",IsEnabled=historicalFontPreferences is not null,
            Content=T("Mémoriser pour ce document","Remember for this document") };
        var readout=DialogText("");
        var message=DialogText("");
        var preview=new Image { Name="HistoricalGlyphPreview",Width=120,Height=76,HorizontalAlignment=HorizontalAlignment.Left };
        Bitmap? raster=null;
        HistoricalFontChoice? selected=null;
        var actions=new WrapPanel { Orientation=Orientation.Horizontal };
        panel.Children.Add(DialogText(T("Police demandée et caractère source","Requested font and source character")));
        panel.Children.Add(entries);
        panel.Children.Add(readout);
        panel.Children.Add(DialogText(T("Police de substitution installée","Installed substitute font")));
        panel.Children.Add(family);
        panel.Children.Add(DialogText(T("Code du glyphe dans cette police, en hexadécimal","Glyph code in this font, in hexadecimal")));
        panel.Children.Add(code);
        panel.Children.Add(preview);
        panel.Children.Add(message);
        panel.Children.Add(persistent);
        panel.Children.Add(actions);
        (HistoricalFontChoice Key, HistoricalFontChoice? Value, bool Save)? edit=null;
        var apply=ActionButton(actions,T("Appliquer et recomposer","Apply and recompose"),()=>
        {
            if(selected is not {} choice)return;
            edit=(choice,choice,persistent.IsChecked==true);
            dialog.Close();
        },name:"ApplyHistoricalFont");
        ActionButton(actions,T("Rétablir l’automatique","Restore automatic resolution"),()=>
        {
            var key=groups[entries.SelectedIndex].Key;
            edit=(new(key.Family,key.Code,key.Packed,key.Family,key.Code),null,persistent.IsChecked==true);
            dialog.Close();
        },name:"ResetHistoricalFont");
        ActionButton(actions,T("Fermer","Close"),dialog.Close);

        entries.SelectionChanged+=(_,_)=>SelectEntry();
        family.SelectionChanged+=(_,_)=>Preview();
        code.TextChanged+=(_,_)=>Preview();
        SelectEntry();
        try{await dialog.ShowDialog(owner);}
        finally{raster?.Dispose();}
        if(edit is not {} selectedEdit||activeDocumentPath!=document||inspection?.Manifest.SourceSha256!=snapshot)return;

        try
        {
            lock(historicalFontGate)
            {
                if(selectedEdit.Save)historicalFontPreferences!.Set(snapshot,selectedEdit.Key,selectedEdit.Value);
                if(!temporaryHistoricalFonts.TryGetValue(snapshot,out var list))
                    temporaryHistoricalFonts.Add(snapshot,list=[]);
                list.RemoveAll(e=>HistoricalFontPreferences.SameSource(e.Key,selectedEdit.Key));
                if(!selectedEdit.Save)list.Add((selectedEdit.Key,selectedEdit.Value));
            }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            status.Text=T("Impossible d’enregistrer la substitution.","The substitution could not be saved.");
            return;
        }
        await OpenDocumentAsync(document,reading);

        void SelectEntry()
        {
            var group=groups[entries.SelectedIndex];
            var choice=ReadHistoricalChoices(snapshot).FirstOrDefault(c=>c.Code==group.Key.Code&&c.Packed==group.Key.Packed&&
                c.Family.Equals(group.Key.Family,StringComparison.OrdinalIgnoreCase));
            string[] used=group.Select(item=>item.Fragment.Face?.UsedFamily is {} font?
                item.Fragment.LegacySymbol?.Method=="missing-marker"?font+T(" (repère de glyphe absent)"," (missing-glyph marker)"):font:
                T("Aucune","None")).Distinct().ToArray();
            readout.Text=T("Face utilisée","Used face")+" · "+string.Join(", ",used);
            family.SelectedItem=choice?.SelectedFamily;
            code.Text=(choice?.SelectedCode??group.Key.Code).ToString("X4",CultureInfo.InvariantCulture);
            persistent.IsChecked=historicalFontPreferences?.Read().Any(p=>p.Snapshot==snapshot&&p.Choice==choice)==true;
            Preview();
        }

        void Preview()
        {
            preview.Source=null;
            raster?.Dispose();
            raster=null;
            selected=null;
            apply.IsEnabled=false;
            message.Text="";
            if(entries.SelectedIndex<0||family.SelectedItem is not string chosen||
                !int.TryParse(code.Text,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out int scalar))return;
            var group=groups[entries.SelectedIndex];
            var variants=group.Select(x=>(x.Fragment.Style.Bold,x.Fragment.Style.Italic)).Distinct().ToArray();
            var images=variants.Select(v=>TypographySession.PreviewHistoricalGlyph(chosen,scalar,v.Bold,v.Italic)).ToArray();
            if(images[0] is {} png)
            {
                using var stream=new MemoryStream(png,writable:false);
                preview.Source=raster=new Bitmap(stream);
            }
            bool usable=images.All(i=>i is not null);
            message.Text=usable?T("Choix manuel pour ce document. La valeur source est conservée.",
                "Manual choice for this document. The source character is retained."):
                T("La face ne fournit pas ce glyphe dans toutes les variantes utilisées.","This face lacks the glyph in one or more required styles.");
            if(!usable)return;
            selected=new(group.Key.Family,group.Key.Code,group.Key.Packed,chosen,scalar);
            apply.IsEnabled=true;
        }
    }
}
