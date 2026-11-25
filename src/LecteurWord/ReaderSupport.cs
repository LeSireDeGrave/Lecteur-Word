using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    GitHubSupportService CreateSupportService()
    {
        try { return new GitHubSupportService(GitHubSupportService.Load(PlatformPaths.ForCurrentUser())); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            RecordIncident(error,SessionStage.Interface);
            return new GitHubSupportService(CommunityConfiguration.Empty);
        }
    }

    void AddSupportActions(StackPanel panel, Window owner, GitHubSupportService support)
    {
        AsyncButton(panel,T("Signaler un problème","Report a problem"),
            () => ShowSupportDraftAsync(SupportKind.Bug, support, owner), name:"ReportProblem");
        AsyncButton(panel,T("Proposer une amélioration","Suggest an improvement"),
            () => ShowSupportDraftAsync(SupportKind.Feature, support, owner), name:"SuggestFeature");
        AsyncButton(panel,T("Consulter les versions","View releases"),
            () => ShowCommunityPageAsync(T("Versions","Releases"), BuildReference.Current.FullReference(english), support.VersionsUrl, owner));
        AsyncButton(panel,T("Contribuer","Contribute"),
            () => ShowCommunityPageAsync(T("Contribuer","Contribute"),
                T("Vous pouvez contribuer en proposant une amélioration, en préparant une recette reproductible ou en créant une extension.",
                  "You can help by suggesting an improvement, preparing a reproducible test, or creating an extension."),
                support.ContributeUrl, owner));
    }

    async Task ShowCommunityPageAsync(string title, string detail, Uri? address, Window owner)
    {
        var dialog = PlatformDialog(title, 590, 360);
        var panel = DialogStack(dialog);
        panel.Children.Add(DialogText(detail));
        if (address is null) panel.Children.Add(DialogText(NoPublicSupport));
        var actions = DialogActions(panel);
        if (address is not null)
            AsyncButton(actions,T("Ouvrir GitHub","Open GitHub"), () => { GitHubSupportService.OpenBrowser(address); return Task.CompletedTask; });
        ActionButton(actions,T("Fermer","Close"),dialog.Close);
        await dialog.ShowDialog(owner);
    }

    string NoPublicSupport => T("L’espace public de support doit encore être configuré.",
        "The public support repository still needs to be configured.");

    async Task ShowSupportDraftAsync(SupportKind initialKind, GitHubSupportService support, Window owner, Incident? selected = null)
    {
        var dialog = PlatformDialog(initialKind == SupportKind.Feature ? T("Proposer une amélioration","Suggest an improvement")
            : T("Signaler un problème","Report a problem"), 785, 720);
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(18) };
        dialog.Content = grid;
        var inputs = new StackPanel { Spacing = 7 };
        grid.Children.Add(inputs);
        var kinds = Enum.GetValues<SupportKind>();
        var kindsPanel = DialogActions(inputs);
        SupportKind selectedKind = initialKind;
        var steps = new TextBox { Name = "ReproductionSteps", PlaceholderText = T("Étapes de reproduction","Reproduction steps"),
            MaxLength = 8000, Height = 95, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var extra = new TextBox { Name = "SupportDetails", PlaceholderText = T("Informations supplémentaires (facultatives)","Additional information (optional)"),
            MaxLength = 8000, Height = 76, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        inputs.Children.Add(steps);
        inputs.Children.Add(extra);
        var incident = selected ?? recovery?.LatestReport;
        var include = new CheckBox { Content = T("Inclure le diagnostic technique de l’incident","Include technical incident diagnostic"),
            IsVisible = incident is not null, IsChecked = selected is not null };
        inputs.Children.Add(include);
        inputs.Children.Add(DialogText(T("Aperçu du texte à utiliser. Vérifiez les informations que vous avez saisies.",
            "Preview of the report. Review the information you entered.")));
        var preview = new TextBox { Name = "SupportPreview", IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,8), MinHeight = 80 };
        Grid.SetRow(preview,1);
        grid.Children.Add(preview);
        var footerPanel = new StackPanel { Spacing = 6 };
        Grid.SetRow(footerPanel,2);
        grid.Children.Add(footerPanel);
        var actions = DialogActions(footerPanel);
        var feedback = DialogText("");
        AsyncButton(actions,T("Copier","Copy"),async () =>
        {
            if (dialog.Clipboard is not {} clipboard) { feedback.Text=T("Presse-papiers indisponible.","Clipboard unavailable."); return; }
            await clipboard.SetTextAsync(preview.Text);
            feedback.Text=T("Brouillon copié.","Draft copied.");
        }, feedback, name:"CopySupportDraft");
        AsyncButton(actions,T("Enregistrer","Save"), () => SaveTextReportAsync(dialog,preview.Text ?? "",feedback), feedback);
        var github = AsyncButton(actions,T("Ouvrir GitHub","Open GitHub"), () =>
        {
            if (support.IssueUrl(CurrentKind()) is {} url) GitHubSupportService.OpenBrowser(url);
            feedback.Text=T("Collez le texte dans le formulaire GitHub si vous souhaitez le publier.",
                "Paste the text into the GitHub form if you choose to submit it.");
            return Task.CompletedTask;
        }, feedback, name:"OpenGitHub");
        ActionButton(actions,T("Fermer","Close"),dialog.Close);
        footerPanel.Children.Add(feedback);

        foreach (var kind in kinds)
        {
            var choice = new RadioButton { Name = "SupportKind_" + kind, GroupName = "SupportKind",
                Content = T(GitHubSupportService.KindLabel(kind,false),GitHubSupportService.KindLabel(kind,true)),
                IsChecked = kind == initialKind };
            choice.IsCheckedChanged += (_, _) => { if (choice.IsChecked == true) { selectedKind = kind; Refresh(); } };
            kindsPanel.Children.Add(choice);
        }
        steps.TextChanged += (_, _) => Refresh();
        extra.TextChanged += (_, _) => Refresh();
        include.IsCheckedChanged += (_, _) => Refresh();
        Refresh();
        await dialog.ShowDialog(owner);

        SupportKind CurrentKind() => selectedKind;
        void Refresh()
        {
            preview.Text = GitHubSupportService.Draft(CurrentKind(),steps.Text ?? "",extra.Text ?? "",
                include.IsChecked == true ? incident : null, english);
            github.IsEnabled = support.IssueUrl(CurrentKind()) is not null;
            feedback.Text = github.IsEnabled ? "" : NoPublicSupport;
        }
    }
}
