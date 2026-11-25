using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    sealed record PublicationPreferences(ExportFormat Format,string Directory,int PdfImageQuality=101);
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
        var dialog=TrackDialog(new Window
        {
            Name="PublicationDialog",Title=T("Exporter le document","Export document"),
            Width=650,Height=480,MinWidth=540,MinHeight=430,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Icon=Icon,FontFamily=FontFamily,FontSize=FontSize
        });
        var formats=new UniformGrid{Name="PublicationFormat",Columns=4};
        string[] labels=["PDF","ODT","EPUB 3","OpusNova"];
        var formatChoices=order.Select((value,index)=>Choice("PublicationFormat"+value,"PublicationFormats",
            labels[index],(preferences?.Format??ExportFormat.Pdf)==value,Icons.ForExportFormat(value))).ToArray();
        int formatIndex=Math.Max(0,Array.FindIndex(formatChoices,choice=>choice.IsChecked==true));
        formatChoices[formatIndex].IsChecked=true;
        for(int index=0;index<formatChoices.Length;index++)
        {
            ToolTip.SetTip(formatChoices[index],names[index]);
            AutomationProperties.SetName(formatChoices[index],names[index]);
            formats.Children.Add(formatChoices[index]);
        }
        var output=new TextBox{Name="PublicationDestination",HorizontalAlignment=HorizontalAlignment.Stretch,FlowDirection=FlowDirection.LeftToRight};
        var browse=new Button{Name="PublicationBrowse",Content=T("Parcourir…","Browse…")};
        var selectRange=new CheckBox{Name="PublicationRange",Content=T("Sélectionner des feuilles","Select pages")};
        int count=Math.Max(1,source.Pages.Count);
        var first=new NumericUpDown{Name="PublicationFirst",Value=1,Minimum=1,Maximum=count,Increment=1,FormatString="0",NumberFormat=uiCulture.NumberFormat,Width=95};
        var last=new NumericUpDown{Name="PublicationLast",Value=count,Minimum=1,Maximum=count,Increment=1,FormatString="0",NumberFormat=uiCulture.NumberFormat,Width=95};
        AutomationProperties.SetName(first,T("Première feuille","First page"));
        AutomationProperties.SetName(last,T("Dernière feuille","Last page"));
        var cover=new CheckBox{Name="PublicationCover",Content=T("Utiliser la première illustration comme couverture","Use the first illustration as cover")};
        var coverName=new TextBox{Name="PublicationCoverName",IsReadOnly=true,FlowDirection=FlowDirection.LeftToRight};
        var chooseCover=new Button{Name="PublicationChooseCover",Content=T("Choisir…","Choose…")};
        var clearCover=new Button{Content=T("Retirer","Remove")};
        var coverRow=Row(T("Couverture EPUB","EPUB cover"),coverName,new StackPanel
            {Orientation=Orientation.Horizontal,Spacing=8,Children={chooseCover,clearCover}});
        PublicationCover? selectedCover=null;
        var provenance=new CheckBox{Name="PublicationPdfProvenance",Content=T("Joindre la provenance des glyphes remplacés","Attach provenance of replacement glyphs")};
        var imageQuality=new UniformGrid{Name="PublicationPdfQuality",Columns=3};
        int selectedQuality=preferences?.PdfImageQuality switch{92=>92,72=>72,_=>101};
        var lossless=Choice("PublicationPdfLossless","PublicationImageQuality",T("Sans perte","Lossless"),
            selectedQuality==101);
        var high=Choice("PublicationPdfHigh","PublicationImageQuality",T("Haute qualité","High quality"),selectedQuality==92);
        var compact=Choice("PublicationPdfCompact","PublicationImageQuality",T("Compact","Compact"),selectedQuality==72);
        AutomationProperties.SetName(high,T("JPEG haute qualité","High-quality JPEG"));
        AutomationProperties.SetName(compact,T("JPEG compact","Compact JPEG"));
        ToolTip.SetTip(high,T("JPEG haute qualité","High-quality JPEG"));
        ToolTip.SetTip(compact,T("JPEG compact","Compact JPEG"));
        imageQuality.Children.Add(lossless);imageQuality.Children.Add(high);imageQuality.Children.Add(compact);
        var qualityRow=Row(T("Images PDF","PDF images"),imageQuality);
        var policy=new CheckBox{Name="PublicationPolicy",Content=T("Exiger la conservation intégrale","Require full preservation"),
            IsChecked=false};
        ToolTip.SetTip(policy,T("Refuse l’export si une réserve de conservation subsiste.",
            "Refuses export when a preservation limitation remains."));
        var range=new StackPanel{Spacing=8,Orientation=Orientation.Horizontal,Children=
            {new TextBlock{Text=T("De","From"),VerticalAlignment=VerticalAlignment.Center},first,
                new TextBlock{Text=T("à","to"),VerticalAlignment=VerticalAlignment.Center},last}};
        var diagnostic=new TextBlock{Name="PublicationDiagnostics",TextWrapping=TextWrapping.Wrap};
        var details=new TextBox{Name="PublicationDetails",TextWrapping=TextWrapping.Wrap,IsReadOnly=true,MaxHeight=170,
            AcceptsReturn=true,FlowDirection=FlowDirection.LeftToRight};
        var copyDetails=new Button{Name="PublicationCopyDiagnostics",Content=T("Copier les diagnostics","Copy diagnostics")};
        var technical=new Expander{Name="PublicationTechnicalDetails",Header=T("Détails de conservation","Preservation details"),
            Content=new StackPanel{Spacing=7,Children={details,copyDetails}},IsVisible=false};
        var advanced=new Expander{Name="PublicationAdvanced",Header=T("Options avancées","Advanced options"),
            Content=new StackPanel{Spacing=9,Children={policy,technical}}};
        var feedback=new TextBlock{Name="PublicationFeedback",TextWrapping=TextWrapping.Wrap};
        var progress=new ProgressBar{Name="PublicationProgress",Minimum=0,Maximum=1,IsVisible=false};
        var publish=new Button{Name="PublicationWrite",Content=T("Exporter","Export"),IsEnabled=false};
        var cancelOrClose=new Button{Name="PublicationCancel",Content=T("Fermer","Close")};
        var openFile=new Button{Name="PublicationOpenFile",Content=T("Ouvrir le fichier","Open file"),IsVisible=false};
        var openFolder=new Button{Name="PublicationOpenFolder",Content=T("Ouvrir le dossier","Open folder"),IsVisible=false};
        var pdfOptions=new StackPanel{Spacing=9,Children={qualityRow,selectRange,range,provenance}};
        var epubOptions=new StackPanel{Spacing=9,Children={cover,coverRow}};
        var form=new StackPanel{Spacing=13,Children={
            Row(T("Destination","Destination"),output,browse),pdfOptions,epubOptions,diagnostic,advanced}};
        var fields=new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Content=form};
        var controls=new StackPanel{Spacing=8,Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,
            Children={openFile,openFolder,cancelOrClose,publish}};
        var bottom=new StackPanel{Spacing=9,Children={feedback,progress,controls}};
        var layout=new DockPanel{Margin=new Thickness(22),LastChildFill=true};
        DockPanel.SetDock(bottom,Dock.Bottom);
        DockPanel.SetDock(formats,Dock.Top);
        formats.Margin=new Thickness(0,0,0,15);
        bottom.Margin=new Thickness(0,10,0,0);
        publish.Classes.Add("accent");
        layout.Children.Add(bottom);
        layout.Children.Add(formats);
        layout.Children.Add(fields);
        dialog.Content=layout;

        string safeName=Path.GetFileNameWithoutExtension(fileName);
        if(string.IsNullOrWhiteSpace(safeName))safeName="document";
        string folder=preferences?.Directory??Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if(!Directory.Exists(folder))folder=Path.GetDirectoryName(activeDocumentPath??"")??"";
        if(!Directory.Exists(folder))folder=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        output.Text=Path.Combine(folder,safeName+"."+Publications.Extension(Selected()));
        Publications.Prepared? prepared=null;
        CancellationTokenSource? preview=null;
        Task? previewTask=null;
        string? completedPath=null;
        bool writing=false,finished=false;
        foreach(var (choice,index) in formatChoices.Select((choice,index)=>(choice,index)))
            choice.IsCheckedChanged+=(_,_) =>
            {
                if(choice.IsChecked!=true)return;
                formatIndex=index;
                if(!string.IsNullOrEmpty(output.Text))output.Text=Path.ChangeExtension(output.Text,Publications.Extension(Selected()));
                _=PreviewAsync();
            };
        selectRange.IsCheckedChanged+=(_,_)=>_=PreviewAsync();
        cover.IsCheckedChanged+=(_,_)=>_=PreviewAsync();
        chooseCover.Click+=async(_,_)=>await ChooseCoverAsync();
        clearCover.Click+=(_,_)=>{selectedCover=null;coverName.Text=null;_=PreviewAsync();};
        provenance.IsCheckedChanged+=(_,_)=>_=PreviewAsync();
        policy.IsCheckedChanged+=(_,_)=>_=PreviewAsync();
        foreach(var (choice,quality) in new[]{(lossless,101),(high,92),(compact,72)})
            choice.IsCheckedChanged+=(_,_) =>
            {
                if(choice.IsChecked!=true)return;
                selectedQuality=quality;
                _=PreviewAsync();
            };
        first.ValueChanged+=(_,_)=>_=PreviewAsync();
        last.ValueChanged+=(_,_)=>_=PreviewAsync();
        output.TextChanged+=(_,_)=>UpdatePublish();
        copyDetails.Click+=async(_,_)=>
        {
            try
            {
                if(dialog.Clipboard is not {} clipboard)throw new InvalidOperationException();
                await clipboard.SetTextAsync(details.Text??"");
                feedback.Text=T("Rapport copié.","Report copied.");
            }
            catch(Exception error) when(error is IOException or InvalidOperationException or NotSupportedException)
            { feedback.Text=T("Presse-papiers indisponible.","Clipboard unavailable."); }
        };
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
            row.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
            AutomationProperties.SetName(input,label);
            Grid.SetColumn(input,1);row.Children.Add(input);
            if(button is not null){Grid.SetColumn(button,2);row.Children.Add(button);}
            return row;
        }

        RadioButton Choice(string name,string group,string label,bool selected,string? icon=null)
        {
            var choice=new RadioButton{Name=name,GroupName=group,IsChecked=selected,
                Padding=new Thickness(5,4),MinHeight=icon is null?32:56,
                HorizontalAlignment=HorizontalAlignment.Stretch,Content=label};
            if(icon is not null)
            {
                var symbol=Icons.Make(icon);
                symbol.Width=symbol.Height=21;
                choice.Content=new StackPanel{Spacing=5,HorizontalAlignment=HorizontalAlignment.Center,
                    Children={symbol,new TextBlock{Text=label,FontWeight=FontWeight.SemiBold,HorizontalAlignment=HorizontalAlignment.Center}}};
            }
            return choice;
        }

        ExportFormat Selected()=>order[formatIndex];
        PublicationRequest Request()
        {
            if(Selected()!=ExportFormat.Pdf)return new(Selected(),UseCoverImage:Selected()==ExportFormat.Epub3&&cover.IsChecked==true,
                Cover:Selected()==ExportFormat.Epub3?selectedCover:null,
                Policy:policy.IsChecked==true?PublicationPolicy.Complete:PublicationPolicy.Compatible);
            return new(ExportFormat.Pdf,selectRange.IsChecked==true?(int?)first.Value:null,
                selectRange.IsChecked==true?(int?)last.Value:null,
                PdfImageQuality:selectedQuality,IncludePdfProvenance:provenance.IsChecked==true,
                Policy:policy.IsChecked==true?PublicationPolicy.Complete:PublicationPolicy.Compatible);
        }

        void UpdatePublish()=>publish.IsEnabled=prepared?.Plan.Allowed==true&&!writing&&!string.IsNullOrWhiteSpace(output.Text);

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
            prepared=null;
            publish.IsEnabled=false;
            bool pdf=Selected()==ExportFormat.Pdf;
            pdfOptions.IsVisible=pdf;
            range.IsVisible=pdf&&selectRange.IsChecked==true;
            epubOptions.IsVisible=Selected()==ExportFormat.Epub3;
            cover.IsEnabled=selectedCover is null&&!writing;
            clearCover.IsEnabled=selectedCover is not null;
            PublicationRequest requested=Request();
            diagnostic.Text=T("Vérification du contenu…","Checking the document…");
            technical.IsVisible=false;
            try
            {
                if(preceding is not null)await preceding;
                cancellation.Token.ThrowIfCancellationRequested();
                var candidate=await Task.Run(()=>Publications.PrepareWrite(source,requested,cancellation.Token),cancellation.Token);
                if(finished||preview!=cancellation)return;
                prepared=candidate;
                var check=candidate.Plan;
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
                    i.Code+" · "+DiagnosticText(i.Code,i.Message)+(i.Source is {} location?" · "+location.PartUri+location.Path:"")
                    +(DiagnosticText(i.Code,i.Message)==i.Message?"":"\n"+i.Message))));
                technical.IsVisible=details.Text.Length>0;
                UpdatePublish();
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
            .Select(g=>"• "+(g.First().Code=="EXPORT_PACKAGE_INCOMPATIBLE"||g.Key.StartsWith("EXPORT_",StringComparison.Ordinal)?
                T("Le contenu dépasse le profil de ce format.","The document exceeds this format’s supported profile."):
                DiagnosticText(g.First().Code,g.Key))+
                (g.Count()>1?$" ({g.Count().ToString("N0",uiCulture)})":"")));

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
                    DefaultExtension=extension,FileTypeChoices=[new FilePickerFileType(names[formatIndex]){Patterns=["*."+extension]}]
                });
                if(selection?.TryGetLocalPath() is {} local)output.Text=local;
                else if(selection is not null)feedback.Text=T("Choisissez un dossier local.","Choose a local folder.");
            }
            catch(Exception error) when(error is IOException or ArgumentException or NotSupportedException)
            {feedback.Text=T("Le sélecteur de fichiers est indisponible.","The file picker is unavailable.");}
        }

        async Task SaveAsync()
        {
            if(writing||prepared is not {Plan.Allowed:true} candidate||string.IsNullOrWhiteSpace(output.Text))return;
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
            var request=candidate.Request;
            bool overwrite=File.Exists(destination);
            writing=true;
            formats.IsEnabled=fields.IsEnabled=publish.IsEnabled=false;
            try
            {
                if(overwrite&&!await ConfirmOverwriteAsync(dialog,Path.GetFileName(destination)))return;
            }
            finally
            {
                writing=false;
                formats.IsEnabled=fields.IsEnabled=true;
                UpdatePublish();
            }
            using var cancellation=new CancellationTokenSource();
            exportCancellation=cancellation;
            writing=true;
            completedPath=null;
            formats.IsEnabled=fields.IsEnabled=publish.IsEnabled=false;
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
                completedPath=await Task.Run(()=>Publications.Write(candidate,destination,overwrite,observer,cancellation.Token),cancellation.Token);
                openFile.IsVisible=openFolder.IsVisible=true;
                feedback.Text=T("Fichier exporté.","File exported.");
                publicationPreferences=new(request.Format,Path.GetDirectoryName(completedPath)!,
                    request.Format==ExportFormat.Pdf?request.PdfImageQuality:publicationPreferences?.PdfImageQuality??preferences?.PdfImageQuality??101);
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
                formats.IsEnabled=fields.IsEnabled=true;
                cover.IsEnabled=selectedCover is null;
                UpdatePublish();
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
        var box=TrackDialog(new Window{Title=T("Remplacer le fichier ?","Replace the file?"),SizeToContent=SizeToContent.WidthAndHeight,
            CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner});
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
