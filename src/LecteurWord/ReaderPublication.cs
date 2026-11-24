using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    sealed record PublicationPreferences(ExportFormat Format,string Directory,int PdfImageQuality=101,
        PublicationPolicy Policy=PublicationPolicy.Compatible);
    PublicationPreferences? publicationPreferences;
    internal Window? ActivePublicationDialog {get;private set;}

    async Task ShowPublicationAsync()
    {
        if(inspection is not {} inspected||nativePageLease is not {} lease||ActivePublicationDialog is not null)return;
        using var retained=lease.Retain();
        var source=new PublicationSource(inspected,lease.Snapshot,Path.GetFileNameWithoutExtension(fileName));
        PublicationPreferences? preferences=publicationPreferences??ReadPublicationPreferences();
        const int labelWidth=110;
        var order=new[]{ExportFormat.Pdf,ExportFormat.Odt,ExportFormat.Epub3,ExportFormat.Nova};
        string[] names=["PDF (.pdf)",T("OpenDocument texte (.odt)","OpenDocument Text (.odt)"),"EPUB 3 (.epub)","OpusNova (.nova)"];
        var dialog=new Window
        {
            Name="PublicationDialog",Title=T("Exporter le document","Export document"),
            Width=600,Height=536,MinWidth=490,MinHeight=450,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,RequestedThemeVariant=RequestedThemeVariant,
            Icon=Icon,FontFamily=FontFamily,FontSize=FontSize
        };
        var format=new ComboBox{Name="PublicationFormat",ItemsSource=names,SelectedIndex=Math.Max(0,Array.IndexOf(order,preferences?.Format??ExportFormat.Pdf)),
            HorizontalAlignment=HorizontalAlignment.Stretch};
        var output=new TextBox{Name="PublicationDestination",HorizontalAlignment=HorizontalAlignment.Stretch};
        var browse=new Button{Name="PublicationBrowse",Content=T("Parcourir…","Browse…")};
        var selectRange=new CheckBox{Name="PublicationRange",Content=T("Sélectionner des feuilles","Select pages")};
        int count=Math.Max(1,source.Pages.Count);
        var first=new NumericUpDown{Name="PublicationFirst",Value=1,Minimum=1,Maximum=count,Increment=1,FormatString="0",Width=95};
        var last=new NumericUpDown{Name="PublicationLast",Value=count,Minimum=1,Maximum=count,Increment=1,FormatString="0",Width=95};
        AutomationProperties.SetName(first,T("Première feuille","First page"));
        AutomationProperties.SetName(last,T("Dernière feuille","Last page"));
        var cover=new CheckBox{Name="PublicationCover",Content=T("Utiliser la première illustration comme couverture","Use the first illustration as cover")};
        var coverName=new TextBox{Name="PublicationCoverName",IsReadOnly=true};
        var chooseCover=new Button{Name="PublicationChooseCover",Content=T("Choisir…","Choose…")};
        var clearCover=new Button{Content=T("Retirer","Remove")};
        var coverRow=Row(T("Couverture EPUB","EPUB cover"),coverName,new StackPanel
            {Orientation=Orientation.Horizontal,Spacing=8,Children={chooseCover,clearCover}});
        PublicationCover? selectedCover=null;
        var provenance=new CheckBox{Name="PublicationPdfProvenance",Content=T("Joindre la provenance des glyphes remplacés","Attach provenance of replacement glyphs")};
        var imageQuality=new ComboBox{Name="PublicationPdfQuality",HorizontalAlignment=HorizontalAlignment.Stretch,
            ItemsSource=new[]{T("Sans perte","Lossless"),T("JPEG haute qualité","High-quality JPEG"),T("JPEG compact","Compact JPEG")},
            SelectedIndex=preferences?.PdfImageQuality switch{92=>1,72=>2,_=>0}};
        var qualityRow=Row(T("Images PDF","PDF images"),imageQuality);
        var policy=new ComboBox{Name="PublicationPolicy",HorizontalAlignment=HorizontalAlignment.Stretch,
            ItemsSource=new[]{T("Export compatible","Compatible export"),T("Conservation intégrale","Full preservation")},
            SelectedIndex=preferences?.Policy==PublicationPolicy.Complete?1:0};
        var policyRow=Row(T("Politique","Policy"),policy);
        var range=new StackPanel{Spacing=8,Orientation=Orientation.Horizontal,Children=
            {new TextBlock{Text=T("De","From"),VerticalAlignment=VerticalAlignment.Center},first,
                new TextBlock{Text=T("à","to"),VerticalAlignment=VerticalAlignment.Center},last}};
        var diagnostic=new TextBlock{Name="PublicationDiagnostics",TextWrapping=TextWrapping.Wrap};
        var details=new TextBlock{Name="PublicationDetails",TextWrapping=TextWrapping.Wrap};
        var technical=new Expander{Name="PublicationTechnicalDetails",Header=T("Détails de conservation","Preservation details"),
            Content=new ScrollViewer{MaxHeight=175,Content=details},IsVisible=false};
        var feedback=new TextBlock{Name="PublicationFeedback",TextWrapping=TextWrapping.Wrap};
        var progress=new ProgressBar{Name="PublicationProgress",Minimum=0,Maximum=1,IsVisible=false};
        var publish=new Button{Name="PublicationWrite",Content=T("Exporter","Export"),IsEnabled=false};
        var cancelOrClose=new Button{Name="PublicationCancel",Content=T("Fermer","Close")};
        var openFile=new Button{Name="PublicationOpenFile",Content=T("Ouvrir le fichier","Open file"),IsVisible=false};
        var openFolder=new Button{Name="PublicationOpenFolder",Content=T("Ouvrir le dossier","Open folder"),IsVisible=false};
        var fields=new StackPanel{Spacing=12,Children={Row(T("Format","Format"),format),
            Row(T("Destination","Destination"),output,browse),policyRow,selectRange,range,qualityRow,provenance,cover,coverRow,
            new ScrollViewer{MaxHeight=196,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content=new StackPanel{Spacing=6,Children={diagnostic,technical}}}}};
        var controls=new StackPanel{Spacing=8,Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,
            Children={openFile,openFolder,cancelOrClose,publish}};
        var bottom=new StackPanel{Spacing=9,Children={feedback,progress,controls}};
        var layout=new DockPanel{Margin=new Thickness(22),LastChildFill=true};
        DockPanel.SetDock(bottom,Dock.Bottom);
        layout.Children.Add(bottom);
        layout.Children.Add(fields);
        dialog.Content=layout;

        string safeName=Path.GetFileNameWithoutExtension(fileName);
        if(string.IsNullOrWhiteSpace(safeName))safeName="document";
        string folder=preferences?.Directory??Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if(!Directory.Exists(folder))folder=Path.GetDirectoryName(activeDocumentPath??"")??"";
        if(!Directory.Exists(folder))folder=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        output.Text=Path.Combine(folder,safeName+"."+Publications.Extension(Selected()));
        PublicationPlan? plan=null;
        CancellationTokenSource? preview=null;
        Task? previewTask=null;
        string? completedPath=null;
        bool writing=false,finished=false;
        format.SelectionChanged+=(_,_)=>
        {
            if(!string.IsNullOrEmpty(output.Text))output.Text=Path.ChangeExtension(output.Text,Publications.Extension(Selected()));
            _=PreviewAsync();
        };
        selectRange.IsCheckedChanged+=(_,_)=>_=PreviewAsync();
        cover.IsCheckedChanged+=(_,_)=>_=PreviewAsync();
        chooseCover.Click+=async(_,_)=>await ChooseCoverAsync();
        clearCover.Click+=(_,_)=>{selectedCover=null;coverName.Text=null;_=PreviewAsync();};
        provenance.IsCheckedChanged+=(_,_)=>_=PreviewAsync();
        policy.SelectionChanged+=(_,_)=>_=PreviewAsync();
        imageQuality.SelectionChanged+=(_,_)=>_=PreviewAsync();
        first.ValueChanged+=(_,_)=>_=PreviewAsync();
        last.ValueChanged+=(_,_)=>_=PreviewAsync();
        output.TextChanged+=(_,_)=>publish.IsEnabled=plan?.Allowed==true&&!writing&&!string.IsNullOrWhiteSpace(output.Text);
        browse.Click+=async(_,_)=>await BrowseAsync();
        publish.Click+=async(_,_)=>await SaveAsync();
        cancelOrClose.Click+=(_,_)=>{if(writing)exportCancellation?.Cancel();else dialog.Close();};
        openFile.Click+=(_,_)=>Open(completedPath);
        openFolder.Click+=(_,_)=>Open(completedPath is null?null:Path.GetDirectoryName(completedPath));
        dialog.Opened+=(_,_)=>_=PreviewAsync();
        dialog.Closing+=(_,args)=>
        {
            if(writing){args.Cancel=true;exportCancellation?.Cancel();}
        };
        try
        {
            ActivePublicationDialog=dialog;
            export.IsEnabled=false;
            await dialog.ShowDialog(this);
        }
        finally
        {
            finished=true;
            if(preview is not null)
            {
                await preview.CancelAsync();
                if(previewTask is not null)await previewTask;
            }
            ActivePublicationDialog=null;
            if(!closed)UpdateActions();
        }

        Grid Row(string label,Control input,Control? button=null)
        {
            var row=new Grid{ColumnDefinitions=new ColumnDefinitions(button is null?$"{labelWidth},*":$"{labelWidth},*,Auto"),
                ColumnSpacing=8};
            row.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center});
            AutomationProperties.SetName(input,label);
            Grid.SetColumn(input,1);row.Children.Add(input);
            if(button is not null){Grid.SetColumn(button,2);row.Children.Add(button);}
            return row;
        }

        ExportFormat Selected()=>order[Math.Clamp(format.SelectedIndex,0,order.Length-1)];
        PublicationRequest Request()
        {
            if(Selected()!=ExportFormat.Pdf)return new(Selected(),UseCoverImage:Selected()==ExportFormat.Epub3&&cover.IsChecked==true,
                Cover:Selected()==ExportFormat.Epub3?selectedCover:null,
                Policy:policy.SelectedIndex==1?PublicationPolicy.Complete:PublicationPolicy.Compatible);
            return new(ExportFormat.Pdf,selectRange.IsChecked==true?(int?)first.Value:null,
                selectRange.IsChecked==true?(int?)last.Value:null,
                PdfImageQuality:imageQuality.SelectedIndex switch{1=>92,2=>72,_=>101},IncludePdfProvenance:provenance.IsChecked==true);
        }

        Task PreviewAsync()
        {
            var preceding=previewTask;
            preview?.Cancel();
            var cancellation=new CancellationTokenSource();
            preview=cancellation;
            previewTask=CheckAsync(preceding,cancellation);
            return previewTask;
        }

        async Task CheckAsync(Task? preceding,CancellationTokenSource cancellation)
        {
            plan=null;
            publish.IsEnabled=false;
            bool pdf=Selected()==ExportFormat.Pdf;
            policyRow.IsVisible=!pdf;
            selectRange.IsVisible=qualityRow.IsVisible=provenance.IsVisible=pdf;
            range.IsVisible=pdf&&selectRange.IsChecked==true;
            coverRow.IsVisible=cover.IsVisible=Selected()==ExportFormat.Epub3;
            cover.IsEnabled=selectedCover is null&&!writing;
            clearCover.IsEnabled=selectedCover is not null;
            PublicationRequest requested=Request();
            diagnostic.Text=T("Vérification du contenu…","Checking the document…");
            technical.IsVisible=false;
            try
            {
                if(preceding is not null)await preceding;
                cancellation.Token.ThrowIfCancellationRequested();
                var check=await Task.Run(()=>Publications.Prepare(source,requested,cancellation.Token),cancellation.Token);
                if(finished||preview!=cancellation)return;
                plan=check;
                string status=check.Status switch
                {
                    PublicationStatus.Ready=>T("Export sans adaptation identifiée","Export with no identified adaptations"),
                    PublicationStatus.Converted=>T("Export avec conversions","Export with conversions"),
                    PublicationStatus.Reserved=>T("Export avec réserves","Export with limitations"),
                    _=>T("Export impossible avec ce réglage","Export unavailable with these settings")
                };
                var visible=check.Status==PublicationStatus.Reserved?check.Reservations:check.Issues;
                diagnostic.Text=visible.IsEmpty?status:status+"\n"+Summarize(visible,4);
                details.Text=string.Join("\n",check.Features.Select(f=>f.Detail).Concat(check.Issues.Select(i=>
                    i.Code+" · "+i.Message+(i.Source is {} location?" · "+location.PartUri+location.Path:""))));
                technical.IsVisible=details.Text.Length>0;
                publish.Content=check.Status==PublicationStatus.Reserved?T("Exporter avec réserves","Export with limitations"):T("Exporter","Export");
                publish.IsEnabled=check.Allowed&&!writing&&!string.IsNullOrWhiteSpace(output.Text);
            }
            catch(OperationCanceledException){}
            catch(Exception error) when(error is InvalidDataException or IOException or ArgumentException)
            {
                if(!finished&&preview==cancellation)
                    diagnostic.Text=T("Le contenu dépasse le profil de ce format.","The document exceeds this format’s supported profile.");
            }
            finally{if(preview==cancellation)preview=null;cancellation.Dispose();}
        }

        string Summarize(IEnumerable<FlowIssue> issues,int limit=12)=>string.Join("\n",issues.GroupBy(i=>i.Message,StringComparer.Ordinal)
            .OrderByDescending(g=>g.Any(i=>i.BlocksExport)).Take(limit)
            .Select(g=>"• "+g.Key+
                (g.Count()>1?$" ({g.Count().ToString(CultureInfo.CurrentCulture)})":"")));

        async Task ChooseCoverAsync()
        {
            try
            {
                var files=await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title=T("Choisir une couverture EPUB","Choose an EPUB cover"),
                    FileTypeFilter=[new FilePickerFileType("PNG / JPEG"){Patterns=["*.png","*.jpg","*.jpeg"]}]
                });
                if(finished||files.Count==0)return;
                await using var stream=await files[0].OpenReadAsync();
                byte[] buffer=new byte[PublicationCover.MaxBytes+1];
                int length=await stream.ReadAtLeastAsync(buffer,buffer.Length,false);
                var accepted=await Task.Run(()=>PublicationCover.Read(buffer.AsSpan(0,length).ToArray(),default));
                if(finished)return;
                selectedCover=accepted;
                cover.IsChecked=false;
                coverName.Text=files[0].Name;
                feedback.Text="";
                _=PreviewAsync();
            }
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                if(!finished)feedback.Text=T("Image de couverture PNG ou JPEG hors du profil accepté.","The PNG or JPEG cover is outside the supported profile.");
            }
        }

        async Task BrowseAsync()
        {
            try
            {
                string extension=Publications.Extension(Selected());
                string? parent=Path.GetDirectoryName(output.Text);
                var selection=await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title=T("Choisir la destination","Choose destination"),SuggestedFileName=Path.GetFileName(output.Text),
                    SuggestedStartLocation=Directory.Exists(parent)?await dialog.StorageProvider.TryGetFolderFromPathAsync(parent!):null,
                    DefaultExtension=extension,FileTypeChoices=[new FilePickerFileType(names[format.SelectedIndex]){Patterns=["*."+extension]}]
                });
                if(selection?.TryGetLocalPath() is {} local)output.Text=local;
                else if(selection is not null)feedback.Text=T("Choisissez un dossier local.","Choose a local folder.");
            }
            catch(Exception error) when(error is IOException or ArgumentException or NotSupportedException)
            {feedback.Text=T("Le sélecteur de fichiers est indisponible.","The file picker is unavailable.");}
        }

        async Task SaveAsync()
        {
            if(writing||plan?.Allowed!=true||string.IsNullOrWhiteSpace(output.Text))return;
            string destination;
            try
            {
                destination=Path.GetFullPath(output.Text);
                if(!Path.IsPathFullyQualified(output.Text))throw new ArgumentException();
            }
            catch(Exception error) when(error is ArgumentException or NotSupportedException or IOException)
            {
                feedback.Text=T("Saisissez un chemin de fichier complet.","Enter a full file path.");
                return;
            }
            var request=Request();
            bool overwrite=File.Exists(destination);
            if(overwrite&&!await ConfirmOverwriteAsync(dialog,Path.GetFileName(destination)))return;
            using var cancellation=new CancellationTokenSource();
            exportCancellation=cancellation;
            writing=true;
            completedPath=null;
            browse.IsEnabled=format.IsEnabled=policy.IsEnabled=output.IsEnabled=selectRange.IsEnabled=cover.IsEnabled=coverRow.IsEnabled=
                range.IsEnabled=qualityRow.IsEnabled=provenance.IsEnabled=publish.IsEnabled=false;
            openFile.IsVisible=openFolder.IsVisible=false;
            cancelOrClose.Content=T("Annuler","Cancel");
            progress.IsVisible=true;
            progress.IsIndeterminate=true;
            feedback.Text=T("Export en cours…","Exporting…");
            var observer=new Progress<PublicationProgress>(info=>
            {
                if(!writing)return;
                progress.IsIndeterminate=info.Phase=="verify";
                progress.Value=info.Total<=0?0:(double)info.Completed/info.Total;
                if(info.Phase=="verify")feedback.Text=T("Vérification du fichier…","Verifying the file…");
            });
            try
            {
                completedPath=await Task.Run(()=>Publications.Write(source,request,destination,overwrite,observer,cancellation.Token),cancellation.Token);
                openFile.IsVisible=openFolder.IsVisible=true;
                feedback.Text=T("Fichier exporté.","File exported.");
                publicationPreferences=new(request.Format,Path.GetDirectoryName(completedPath)!,
                    request.Format==ExportFormat.Pdf?request.PdfImageQuality:publicationPreferences?.PdfImageQuality??preferences?.PdfImageQuality??101,
                    request.Format==ExportFormat.Pdf?publicationPreferences?.Policy??preferences?.Policy??PublicationPolicy.Compatible:request.Policy);
                SavePublicationPreferences(publicationPreferences);
            }
            catch(OperationCanceledException){feedback.Text=T("Export annulé. La destination a été préservée.","Export cancelled. The destination was preserved.");}
            catch(PublicationRefusal refusal){diagnostic.Text=Summarize(refusal.Issues);}
            catch(Exception error)
            {
                RecordIncident(error,SessionStage.ExportDocument);
                feedback.Text=error.Message switch
                {
                    "EXPORT_DESTINATION_EXTENSION"=>T("L’extension doit correspondre au format choisi.","The extension must match the selected format."),
                    "EXPORT_DESTINATION_CHANGED" or "EXPORT_DESTINATION_EXISTS"=>T("La destination a changé. Vérifiez le nom du fichier.","The destination changed. Check the file name."),
                    _=>T("L’export a échoué. Vérifiez le dossier ou consultez le rapport.","Export failed. Check the folder or see the report.")
                };
            }
            finally
            {
                writing=false;
                exportCancellation=null;
                progress.IsVisible=false;
                browse.IsEnabled=format.IsEnabled=policy.IsEnabled=output.IsEnabled=selectRange.IsEnabled=coverRow.IsEnabled=
                    range.IsEnabled=qualityRow.IsEnabled=provenance.IsEnabled=true;
                cover.IsEnabled=selectedCover is null;
                publish.IsEnabled=plan?.Allowed==true;
                cancelOrClose.Content=T("Fermer","Close");
            }
        }

        void Open(string? path)
        {
            if(path is null)return;
            try{using var _=Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
            catch(Exception error) when(error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {feedback.Text=T("L’ouverture est indisponible sur ce poste.","Opening is unavailable on this device.");}
        }
    }

    async Task<bool> ConfirmOverwriteAsync(Window owner,string name)
    {
        var box=new Window{Title=T("Remplacer le fichier ?","Replace the file?"),SizeToContent=SizeToContent.WidthAndHeight,
            CanResize=false,RequestedThemeVariant=RequestedThemeVariant,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var keep=new Button{Content=T("Conserver","Keep"),IsDefault=true};
        var replace=new Button{Content=T("Remplacer","Replace")};
        keep.Click+=(_,_)=>box.Close(false);
        replace.Click+=(_,_)=>box.Close(true);
        box.Content=new StackPanel{Margin=new Thickness(20),Spacing=12,MaxWidth=440,Children=
            {new TextBlock{Text=name,TextWrapping=TextWrapping.Wrap},
                new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right,
                    Children={keep,replace}}}};
        return await box.ShowDialog<bool>(owner);
    }

    string? PublicationSettingsPath=>themePreference is null?null:Path.Combine(themePreference.DirectoryPath,"publication.json");
    PublicationPreferences? ReadPublicationPreferences()
    {
        if(PublicationSettingsPath is not {} path)return null;
        try
        {
            var info=new FileInfo(path);
            return info.Exists&&info.Length<=65536&&info.LinkTarget is null?
                JsonSerializer.Deserialize<PublicationPreferences>(File.ReadAllBytes(path)):null;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){return null;}
    }

    void SavePublicationPreferences(PublicationPreferences value)
    {
        if(PublicationSettingsPath is not {} path||new FileInfo(path).LinkTarget is not null)return;
        string directory=Path.GetDirectoryName(path)!;
        string temp=Path.Combine(directory,".publication-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
            if(OperatingSystem.IsLinux())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using(var file=new FileStream(temp,options)){JsonSerializer.Serialize(file,value);file.Flush(flushToDisk:true);}
            File.Move(temp,path,true);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
        finally
        {
            try{File.Delete(temp);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
        }
    }
}
