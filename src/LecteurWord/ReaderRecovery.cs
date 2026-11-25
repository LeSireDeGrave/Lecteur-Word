using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    readonly SessionRecoveryService? recovery;
    readonly string? startupWarning;
    readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    string? activeDocumentPath;
    SessionOperation? activeDocumentOperation;
    ReadingSnapshot? resumeCandidate;
    bool startupRecoveryPending;

    void InitializePlatformButtons()
    {
        var help = IconButton("help", "Aide et récupération", "Help and recovery", ShowPlatformHelpAsync);
        help.Name = "PlatformHelp";
        AddGroup(help);
    }

    void InitializeRecovery()
    {
        if (recovery is null) return;
        resumeCandidate = recovery.Start.Reading;
        startupRecoveryPending = recovery.Start.Interrupted;
        if (resumeCandidate is {} snapshot) ApplyConsultationPreferences(snapshot);
        sessionTimer.Tick += (_, _) => SaveReading();
        Closing += (_, args) =>
        {
            if (args.Cancel) return;
            if (!startupRecoveryPending)
            {
                SaveReading();
                TryRecovery(service => service.Close(CaptureReading()));
            }
        };
        Closed += (_, _) =>
        {
            sessionTimer.Stop();
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledAppException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            recovery.Dispose();
        };
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledAppException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    void OnUnhandledAppException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception error)
            TryRecovery(service => service.Record(error, fatal: args.IsTerminating), notify: false);
    }

    void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        TryRecovery(service => service.Record(args.Exception, SessionStage.Background), notify: false);

    internal async Task StartReaderAsync(string? commandLineDocument)
    {
        if (startupWarning is not null || recovery?.StorageWarning is not null)
            status.Text = ExtensionError((startupWarning ?? recovery?.StorageWarning)!);
        if (startupRecoveryPending)
        {
            var start = recovery!.Start;
            if (start.InterruptedExtension is {} identity) QuarantineExtension(identity);
            var choice = await AskRecoveryAsync(start);
            ReloadPlatformExtensions();
            startupRecoveryPending = false;
            if (choice && start.Reading?.Document is {} path)
                await RestoreReadingAsync(start.Reading, path);
            else
                resumeCandidate = null;
        }
        else
        {
            ReloadPlatformExtensions();
            if (!string.IsNullOrWhiteSpace(commandLineDocument)) await OpenDocumentAsync(commandLineDocument);
        }
        SaveReading(completed: true);
        if (!closed && recovery is not null) sessionTimer.Start();
    }

    async Task RestoreReadingAsync(ReadingSnapshot snapshot, string path)
    {
        if (!File.Exists(path))
        {
            status.Text = T("Le document de la session est introuvable.", "The session document could not be found.");
            return;
        }
        await OpenDocumentAsync(path, snapshot);
    }

    void SaveReading(bool completed = false, string? successfulDocument = null)
    {
        if (startupRecoveryPending) return;
        TryRecovery(service => service.Checkpoint(CaptureReading(), completed, successfulDocument, activeExtensionOperation ?? activeDocumentOperation));
    }

    ReadingSnapshot CaptureReading()
    {
        var snapshot = new ReadingSnapshot(activeDocumentPath,
            simplified ? ContinuousAnchor() : CurrentBodyAnchor(), ShowsCalculatedPages ? page+1 : 0, PageOffsetInPoints(), zoom, readingZoom,
            readingView switch
            {
                ReadingView.SinglePage => SessionView.SinglePage, ReadingView.FacingPages => SessionView.FacingPages,
                ReadingView.Continuous => SessionView.Continuous, _ => SessionView.Unavailable
            },
            navVisible, elementsVisible, navMode, navigationWidth, elementWidth, preferElements, english,
            rulerRequested, marksRequested, fittingWidth, elementCategoryIndex, inspection?.Manifest.SourceSha256);
        if (activeDocumentPath is null && resumeCandidate?.Document is {} prior)
            return snapshot with { Document = prior, DocumentHash = resumeCandidate.DocumentHash, Anchor = resumeCandidate.Anchor, Page = resumeCandidate.Page,
                PageOffset = resumeCandidate.PageOffset, View = resumeCandidate.View, FitWidth = resumeCandidate.FitWidth };
        return snapshot;
    }

    void ApplyConsultationPreferences(ReadingSnapshot snapshot)
    {
        navVisible = snapshot.Navigation;
        elementsVisible = snapshot.Elements;
        preferElements = snapshot.PreferElements;
        navigationWidth = Math.Clamp(snapshot.NavigationWidth, 168, 360);
        elementWidth = Math.Clamp(snapshot.ElementsWidth, 210, 422);
        navMode = snapshot.Panel is "outline" or "bookmarks" or "notes" or "sections" or "search" or "diagnostics" or "properties"
            ? snapshot.Panel : "pages";
        rulerRequested = snapshot.Ruler;
        marksRequested = snapshot.Marks;
        elementCategoryIndex = Math.Clamp(snapshot.ElementCategory,0,elementCategory.ItemCount > 0 ? elementCategory.ItemCount - 1 : 0);
        zoom = Math.Clamp(snapshot.PagedZoom, .3, 2);
        readingZoom = Math.Clamp(snapshot.ContinuousZoom, .8, 1.7);
        UpdatePanels();
        if (!hasLanguagePreference && LanguagePath is null)
        {
            languageChoice = snapshot.English ? "en" : "fr";
            ApplyLanguage(refresh: true);
        }
    }

    void ApplyReadingSnapshot(ReadingSnapshot snapshot)
    {
        ApplyConsultationPreferences(snapshot);
        bool changed = snapshot.DocumentHash is {} hash && inspection?.Manifest.SourceSha256 != hash;
        if (changed) snapshot = snapshot with { Page = 0, Anchor = null, PageOffset = 0 };
        if (snapshot.View == SessionView.Continuous)
        {
            if (ShowsCalculatedPages) GoToPage(Math.Max(0,snapshot.Page - 1));
            SetReadingView(ReadingView.Continuous);
            if (simplified && snapshot.Anchor is {} anchor) NavigateToBlock(anchor, alignReadingStart: true);
        }
        else if (ShowsCalculatedPages)
        {
            SetReadingView(snapshot.View == SessionView.FacingPages ? ReadingView.FacingPages : ReadingView.SinglePage);
            GoToPage(Math.Max(0, snapshot.Page - 1));
            if (snapshot.FitWidth) FitToWidth();
            RevealPage(page, Math.Max(0, snapshot.PageOffset));
        }
        UpdateUi();
        if (changed) status.Text = T("Le document a changé. La lecture reprend au début.","The document changed. Reading resumes at the beginning.");
    }

    void RecordIncident(Exception error, SessionStage stage, ExtensionIdentity? extension = null) =>
        TryRecovery(service => service.Record(error, stage, extension));

    void TryRecovery(Action<SessionRecoveryService> action, bool notify = true)
    {
        if (recovery is null) return;
        try { action(recovery); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (notify) status.Text = T("Écriture du journal impossible.", "Could not write the session journal.");
        }
    }

    async Task<bool> ConfirmBlockedOpenAsync()
    {
        var dialog = PlatformDialog(T("Document en attente de contrôle","Document awaiting review"), 540, 205);
        var panel = DialogStack(dialog);
        panel.Children.Add(DialogText(T("Deux tentatives de reprise ont été interrompues. Réessayer d’ouvrir ce document ?",
            "Two restore attempts were interrupted. Try opening this document again?")));
        var actions = DialogActions(panel);
        ActionButton(actions, T("Annuler","Cancel"), () => dialog.Close(false));
        ActionButton(actions, T("Réessayer","Try again"), () => dialog.Close(true));
        return await dialog.ShowDialog<bool>(this);
    }

    async Task<bool> AskRecoveryAsync(SessionStart start)
    {
        var dialog = PlatformDialog(T("Récupération de session", "Session recovery"), 650, 300);
        var panel = DialogStack(dialog);
        panel.Children.Add(DialogText(T("La dernière session s'est interrompue. Voulez-vous reprendre votre lecture ?",
            "The last session was interrupted. Would you like to resume reading?")));
        if (start.ResumeBlocked)
            panel.Children.Add(DialogText(T("Deux reprises ont été interrompues. Le document reste fermé.", "Two restore attempts were interrupted. The document stays closed.")));
        if (start.InterruptedExtension is {} identity)
            panel.Children.Add(DialogText(T("Extension impliquée : ", "Extension involved: ") + identity.Id + " " + identity.Version));
        if (extensions is not null)
        {
            var once = new CheckBox { Content = T("Désactiver les extensions pour ce démarrage", "Disable extensions for this startup"),
                IsChecked = extensionsSuspended };
            once.IsCheckedChanged += (_, _) => extensionsSuspended = once.IsChecked == true;
            panel.Children.Add(once);
        }
        var actions = DialogActions(panel);
        if (!start.ResumeBlocked && start.Reading?.Document is not null)
            ActionButton(actions,T("Reprendre","Resume"), () => dialog.Close(true), name:"ResumeReading").IsDefault = true;
        ActionButton(actions,T("Ouvrir sans reprendre","Open without resuming"), () => dialog.Close(false));
        if (start.Report is {} report)
            AsyncButton(actions,T("Consulter le rapport","View report"), () => ShowIncidentAsync(report, dialog));
        return await dialog.ShowDialog<bool>(this);
    }

    async Task ShowPlatformHelpAsync()
    {
        var dialog = PlatformDialog(T("Aide et récupération","Help and recovery"), 480, 405);
        var panel = DialogStack(dialog);
        if (activeDocumentPath is null && resumeCandidate is { Document: {} path } candidate && recovery?.BlockedDocument != path)
        {
            AsyncButton(panel,T("Reprendre la dernière lecture","Resume last reading"), async () =>
            {
                dialog.Close();
                await RestoreReadingAsync(candidate, path);
            });
        }
        if (recovery?.LatestReport is {} incident)
            AsyncButton(panel,T("Consulter le dernier rapport","View latest report"), () => ShowIncidentAsync(incident, dialog));
        else panel.Children.Add(DialogText(T("Aucun rapport d’incident disponible.", "No incident report available.")));
        AsyncButton(panel,T("Préférences de polices","Font preferences"),()=>ShowHistoricalFontsAsync(dialog));
        AddSupportActions(panel, dialog, CreateSupportService());
        ActionButton(panel,T("Fermer","Close"), dialog.Close);
        await dialog.ShowDialog(this);
    }

    Task ShowIncidentAsync(Incident incident, Window owner) =>
        ShowTextReportAsync(T("Rapport d’incident","Incident report"), LocalIncident(incident), owner, incident);

    string LocalIncident(Incident incident)
    {
        string line = Environment.NewLine;
        return string.Join(line, $"Lecteur Word {incident.Version}",
            T("Incident : ", "Incident: ") + incident.Id.ToString("N") + " · " + incident.Utc.ToLocalTime().ToString("F", uiCulture),
            T("Build : ", "Build: ") + incident.Build, T("Système : ", "System: ") + incident.System,
            T("Étape : ", "Stage: ") + incident.Stage,
            T("Arrêt non propre : ", "Unclean shutdown: ") + (incident.Unclean ? T("oui", "yes") : T("non", "no")),
            T("Extension : ", "Extension: ") + (incident.Extension is {} extension ? $"{extension.Id} {extension.Version}" : "—"),
            T("Erreur : ", "Error: ") + (incident.ErrorType ?? T("non observée", "not observed")),
            incident.FrameNames.Length == 0 ? T("Pile : indisponible", "Stack: unavailable")
                : T("Pile : ", "Stack: ") + line + string.Join(line,incident.FrameNames))
            + (incident.LocalStack is null ? "" : line + line + incident.LocalStack);
    }

    async Task ShowTextReportAsync(string title, string report, Window owner, Incident? incident = null)
    {
        var dialog = PlatformDialog(title, 735, 510);
        var grid = new Grid { RowDefinitions = new("*,Auto"), Margin = new Thickness(18) };
        dialog.Content = grid;
        grid.Children.Add(new TextBox { Name = "ReportPreview", Text = report, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, Margin = new Thickness(0,12,0,0) };
        Grid.SetRow(actions,1);
        grid.Children.Add(actions);
        var feedback = DialogText("");
        AsyncButton(actions,T("Copier","Copy"),async () =>
        {
            if (dialog.Clipboard is not {} clipboard) { feedback.Text=T("Presse-papiers indisponible.","Clipboard unavailable."); return; }
            await clipboard.SetTextAsync(report);
            feedback.Text=T("Rapport copié.","Report copied.");
        }, name:"CopyIncident");
        AsyncButton(actions,T("Enregistrer","Save"), () => SaveTextReportAsync(dialog,report,feedback));
        if (incident is not null)
            AsyncButton(actions,T("Préparer un signalement","Prepare an issue"), () => ShowSupportDraftAsync(
                incident.Extension is null ? SupportKind.Bug : SupportKind.Extension, CreateSupportService(), dialog, incident));
        ActionButton(actions,T("Fermer","Close"),dialog.Close);
        actions.Children.Add(feedback);
        await dialog.ShowDialog(owner);
    }

    Window PlatformDialog(string title, double width, double height)
    {
        var dialog = TrackDialog(new Window { Title = title, Width = width, Height = height, MinWidth = 390, MinHeight = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = BrandAssets.WindowIcon,
            FontFamily = FontFamily });
        dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(); };
        return dialog;
    }

    static StackPanel DialogStack(Window dialog)
    {
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        dialog.Content = new ScrollViewer { Content = panel };
        return panel;
    }

    static TextBlock DialogText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    static WrapPanel DialogActions(Panel parent)
    {
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        parent.Children.Add(actions);
        return actions;
    }

    static Button ActionButton(Panel parent, string label, Action action, string? name = null)
    {
        var button = new Button { Name = name, Content = label };
        Avalonia.Automation.AutomationProperties.SetName(button,label);
        parent.Children.Add(button);
        button.Click += (_, _) => action();
        return button;
    }

    Button AsyncButton(Panel parent, string label, Func<Task> action, TextBlock? feedback = null, string? name = null)
    {
        var button = new Button { Name = name, Content = label };
        Avalonia.Automation.AutomationProperties.SetName(button,label);
        parent.Children.Add(button);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is InvalidDataException or TimeoutException)
            {
                (feedback ?? status).Text = ExtensionError(error.Message);
            }
            catch (Exception error)
            {
                RecordIncident(error, SessionStage.Interface);
                (feedback ?? status).Text = T("Action interrompue. Consultez le rapport.","Action interrupted. See the report.");
            }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    async Task SaveTextReportAsync(Window dialog, string report, TextBlock feedback)
    {
        var file = await dialog.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = T("Enregistrer le rapport","Save report"), SuggestedFileName = "lecteur-word-rapport.txt", DefaultExtension = "txt"
        });
        if (file is null) return;
        if (file.TryGetLocalPath() is not {} path)
        {
            feedback.Text = T("Sélectionnez un dossier local.","Select a local folder.");
            return;
        }
        try
        {
            PlatformFiles.SaveReport(path, report);
            feedback.Text = T("Rapport enregistré.","Report saved.");
        }
        catch (IOException) when (File.Exists(path))
        {
            feedback.Text = T("Ce fichier existe. Choisissez un nouveau nom.","This file exists. Choose a new name.");
        }
    }
}
