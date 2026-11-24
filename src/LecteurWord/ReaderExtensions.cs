using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    readonly ExtensionManagerService? extensions;
    InstalledExtension? activeTheme;
    ReaderPalette? extensionPalette;
    SessionOperation? activeExtensionOperation;
    bool extensionsSuspended;

    void ReloadExtensionTheme(bool propagateError = false)
    {
        if (extensions is null) return;
        InstalledExtension? theme = null;
        try
        {
            theme = extensionsSuspended ? null : extensions.List().Items.FirstOrDefault(
                item => item.Package.Theme is not null && extensions.CanRun(item));
            if (theme is not null)
            {
                BeginExtension(SessionStage.ApplyExtension,theme.Manifest.Identity);
                var contribution = theme.Package.Theme!;
                var colors = theme.Settings.AlternateTheme ? contribution.Alternate : contribution.Default;
                var surface = Color.Parse(colors.Surface);
                var accent = Color.Parse(colors.Accent);
                extensionPalette = ReaderPalette.Chocolate with
                {
                    Header = new SolidColorBrush(surface), Panel = new SolidColorBrush(surface),
                    Footer = new SolidColorBrush(surface), Stage = new SolidColorBrush(Mix(surface,accent,.82)),
                    Toolbar = new SolidColorBrush(Mix(surface,accent,.90)),
                    Selected = new SolidColorBrush(Mix(surface,accent,.78)), Accent = new SolidColorBrush(accent)
                };
                Resources["SystemAccentColor"] = accent;
            }
            else
            {
                extensionPalette = null;
                Resources.Remove("SystemAccentColor");
            }
            activeTheme = theme;
            ApplyTheme();
        }
        catch (Exception error)
        {
            RecordIncident(error,SessionStage.ApplyExtension,theme?.Manifest.Identity);
            if (theme is not null) QuarantineExtension(theme.Manifest.Identity);
            extensionPalette = null;
            activeTheme = null;
            Resources.Remove("SystemAccentColor");
            ApplyTheme();
            if (propagateError) throw new InvalidDataException("Le thème a été désactivé après une erreur.",error);
        }
        finally
        {
            if (theme is not null) EndExtension();
        }

        static Color Mix(Color light, Color dark, double weight) => new(255,
            (byte)(light.R*weight+dark.R*(1-weight)),(byte)(light.G*weight+dark.G*(1-weight)),(byte)(light.B*weight+dark.B*(1-weight)));
    }

    void BeginExtension(SessionStage stage, ExtensionIdentity identity)
    {
        activeExtensionOperation = new SessionOperation(stage,Extension:identity);
        TryRecovery(service => service.Begin(activeExtensionOperation));
    }

    void EndExtension()
    {
        activeExtensionOperation = null;
        SaveReading(completed:true);
    }

    void QuarantineExtension(ExtensionIdentity identity)
    {
        try { extensions?.Quarantine(identity); }
        catch (Exception error)
        {
            extensionsSuspended = true;
            RecordIncident(error,SessionStage.ApplyExtension,identity);
            status.Text=T("Extensions suspendues. L’état de l’extension n’a pas pu être enregistré.",
                "Extensions suspended. The extension state could not be saved.");
        }
    }

    async Task ShowExtensionsAsync()
    {
        if (extensions is not {} manager) return;
        var dialog = PlatformDialog(T("Extensions","Extensions"), 840, 610);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(16) };
        dialog.Content = root;
        var top = DialogActions(root);
        var feedback = DialogText("");
        IReadOnlyList<CatalogEntry> catalogEntries = [];
        bool catalogLoaded = false;
        ExtensionCatalog? catalog = null;
        string? catalogProblem = null;
        try
        {
            string? url = GitHubSupportService.Load(PlatformPaths.ForCurrentUser()).CatalogUrl;
            if (url is not null) catalog = new ExtensionCatalog(url,manager.ReaderVersion);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            catalogProblem = T("La configuration du catalogue est illisible ou invalide.","The catalog configuration is unreadable or invalid.");
        }

        using var lifetime = new CancellationTokenSource();
        dialog.Closed += (_, _) => lifetime.Cancel();
        var installed = new ExtensionPane("InstalledExtensions");
        var discover = new ExtensionPane("DiscoverExtensions");
        var updates = new ExtensionPane("ExtensionUpdates");
        var tabs = new TabControl { Name = "ExtensionTabs", Margin = new Thickness(0,8,0,8), ItemsSource = new[]
        {
            new TabItem { Header = T("Installées","Installed"), Content = installed.Content },
            new TabItem { Header = T("Découvrir","Discover"), Content = discover.Content },
            new TabItem { Header = T("Mises à jour","Updates"), Content = updates.Content }
        }};
        Grid.SetRow(tabs,1);
        root.Children.Add(tabs);
        Grid.SetRow(feedback,2);
        root.Children.Add(feedback);
        installed.List.SelectionChanged += (_, _) => DescribeInstalled();
        discover.List.SelectionChanged += (_, _) => DescribeCatalog(discover,update:false);
        updates.List.SelectionChanged += (_, _) => DescribeCatalog(updates,update:true);

        AsyncButton(top,T("Installer un paquet local…","Install local package…"),async () =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Installer une extension","Install an extension"),
                FileTypeFilter = [new FilePickerFileType("Lecteur Word"){Patterns=["*.lwext"]}]
            });
            if (files.Count == 0) return;
            if (files[0].TryGetLocalPath() is not {} path)
            {
                feedback.Text = T("Sélectionnez un paquet local.","Select a local package.");
                return;
            }
            var candidate = await Task.Run(() => manager.InspectLocal(path),lifetime.Token);
            if (!await ConfirmExtensionAsync(dialog,candidate.Manifest)) return;
            manager.Install(candidate);
            ReloadExtensionTheme();
            Refresh(candidate.Manifest.Id);
            feedback.Text = T("Paquet installé. Activez-le depuis sa fiche.","Package installed. Enable it from its details.");
        },feedback, name:"InstallLocalExtension");

        if (catalog is not null)
            AsyncButton(top,T("Vérifier le catalogue","Check catalog"),async () =>
            {
                catalogEntries = await catalog.ReadAsync(lifetime.Token);
                catalogLoaded = true;
                Refresh();
                feedback.Text = T("Catalogue vérifié.","Catalog checked.");
            },feedback, name:"CheckExtensionCatalog");
        ActionButton(top,T("Fermer","Close"),dialog.Close);
        Refresh();
        if (extensionsSuspended) feedback.Text = T("Les extensions sont suspendues pour ce démarrage.","Extensions are suspended for this startup.");
        await dialog.ShowDialog(this);

        void Refresh(string? installedId = null)
        {
            var listing = manager.List();
            installed.Show(listing.Items.Select(item => new ExtensionRow(item.Manifest,item,null)),
                installedId,listing.Unreadable == 0 ? T("Aucune extension installée.","No installed extensions.")
                : T("Des fiches locales d’extensions sont illisibles.","Some installed extension records are unreadable."));
            var byId = listing.Items.ToDictionary(item => item.Manifest.Id,StringComparer.Ordinal);
            string state = catalogProblem ?? (catalog is null
                ? T("Le catalogue communautaire doit encore être configuré.","The community catalog still needs to be configured.")
                : T("Utilisez « Vérifier le catalogue » pour charger les extensions et les mises à jour compatibles.",
                    "Use “Check catalog” to load available extensions and compatible updates."));
            discover.Show(catalogEntries.Where(entry => !byId.ContainsKey(entry.Manifest.Id)).Select(entry => new ExtensionRow(entry.Manifest,null,entry)),
                null,catalogLoaded ? T("Aucune nouvelle extension compatible.","No new compatible extensions.") : state);
            updates.Show(catalogEntries.Where(entry => byId.TryGetValue(entry.Manifest.Id,out var previous) &&
                ExtensionPackage.StableVersion(entry.Manifest.Version) > ExtensionPackage.StableVersion(previous.Manifest.Version))
                .Select(entry => new ExtensionRow(entry.Manifest,byId[entry.Manifest.Id],entry)),
                null,catalogLoaded ? T("Aucune mise à jour compatible.","No compatible updates.") : state);
            if (listing.Unreadable > 0) feedback.Text = T($"Fiches locales illisibles : {listing.Unreadable}",
                $"Unreadable local records: {listing.Unreadable}");
            DescribeInstalled();
            DescribeCatalog(discover,update:false);
            DescribeCatalog(updates,update:true);
        }

        void DescribeInstalled()
        {
            var details = installed.Details;
            details.Children.Clear();
            if (installed.List.SelectedItem is not ExtensionRow { Installed: {} item }) return;
            var manifest = item.Manifest;
            AddExtensionDetails(details,manifest);
            bool compatible = ExtensionPackage.IsCompatible(manifest,manager.ReaderVersion);
            details.Children.Add(DialogText(item.Quarantined ? T("Désactivée après un incident.","Disabled after an incident.")
                : item.Enabled && compatible ? T("Activée","Enabled") : T("Désactivée","Disabled")));
            if (!compatible) details.Children.Add(DialogText(T("Cette version est incompatible.","This version is incompatible.")));
            if (item.DownloadSource is not null)
                details.Children.Add(DialogText(T("Origine du téléchargement : ","Download source: ") + item.DownloadSource));
            var actions = DialogActions(details);
            bool canEnable = compatible && !extensionsSuspended;
            if (item.Enabled || canEnable)
                AsyncButton(actions,item.Enabled ? T("Désactiver","Disable") : T("Activer","Enable"),async () =>
                {
                    if (!item.Enabled && item.Quarantined && !await ConfirmExtensionAsync(dialog,manifest,retry:true)) return;
                    manager.SetEnabled(manifest.Id,!item.Enabled);
                    try
                    {
                        ReloadExtensionTheme(propagateError:true);
                        feedback.Text = "";
                    }
                    finally { Refresh(manifest.Id); }
                },feedback,name:item.Enabled ? "DisableExtension" : "EnableExtension");
            if (compatible)
                AsyncButton(actions,T("Configurer","Configure"),async () =>
                {
                    if (await EditExtensionSettingsAsync(dialog,item) is {} settings)
                    {
                        manager.Configure(manifest.Id,settings);
                        try { ReloadExtensionTheme(propagateError:true); }
                        finally { Refresh(manifest.Id); }
                    }
                },feedback, name:"ConfigureExtension");
            AsyncButton(actions,T("Désinstaller","Uninstall"),async () =>
            {
                if (!await ConfirmUninstallAsync(dialog,manifest.Name)) return;
                manager.Uninstall(manifest.Id);
                ReloadExtensionTheme();
                Refresh();
            },feedback, name:"UninstallExtension");
            if (!extensionsSuspended && manager.CanRun(item) && item.Package.Reading is not null && readable?.Available == true)
                AsyncButton(actions,T("Calculer les statistiques","Calculate statistics"),
                    async () =>
                    {
                        try { await RunReadingExtensionAsync(dialog, item, lifetime.Token); }
                        finally { if (!lifetime.IsCancellationRequested) Refresh(item.Manifest.Id); }
                    },feedback, name:"RunReadingExtension");
        }

        void DescribeCatalog(ExtensionPane pane, bool update)
        {
            var details = pane.Details;
            details.Children.Clear();
            if (pane.List.SelectedItem is not ExtensionRow { Remote: {} entry } row || catalog is null) return;
            AddExtensionDetails(details,entry.Manifest);
            if (update) details.Children.Add(DialogText(T("Version installée : ","Installed version: ") + row.Installed!.Manifest.Version));
            AsyncButton(details,update ? T("Installer la mise à jour","Install update") : T("Installer depuis le catalogue","Install from catalog"),
                async () =>
                {
                    if (!await ConfirmExtensionAsync(dialog,entry.Manifest)) return;
                    var candidate = await catalog.DownloadAsync(entry,lifetime.Token);
                    manager.Install(candidate,entry.PackageUrl);
                    ReloadExtensionTheme();
                    Refresh(entry.Manifest.Id);
                    feedback.Text = T("Paquet installé. Activez-le depuis sa fiche.","Package installed. Enable it from its details.");
                },feedback);
        }
    }

    async Task RunReadingExtensionAsync(Window owner, InstalledExtension item, CancellationToken cancellation)
    {
        if (item.Package.Reading is not {} contribution || readable?.Available != true) return;
        var identity = item.Manifest.Identity;
        MetricResult[]? results = null;
        BeginExtension(SessionStage.RunExtension,identity);
        try
        {
            var blocks = readable.Presentation.BodyBlocks;
            int paragraphs = 0, headings = 0, tables = 0;
            foreach (var block in blocks.SelectMany(Descendants))
            {
                switch (block.Kind)
                {
                    case "paragraph": paragraphs++; break;
                    case "heading": headings++; break;
                    case "table": tables++; break;
                }
            }
            var metrics = new DocumentMetrics(blocks.Count,paragraphs,headings,tables,readable.Presentation.Notes.Count,
                ShowsCalculatedPages ? composition!.Pages.Count : 0);
            results = await ReadingProtocol.RunAsync(contribution,metrics,cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            RecordIncident(error,SessionStage.RunExtension,identity);
            QuarantineExtension(identity);
        }
        finally { EndExtension(); }

        if (cancellation.IsCancellationRequested) return;
        if (results is null)
        {
            var failed = PlatformDialog(T("Incident d’extension","Extension incident"), 580, 210);
            var panel = DialogStack(failed);
            panel.Children.Add(DialogText(T("L’extension est désactivée après une erreur. La lecture peut continuer.",
                "The extension was disabled after an error. Reading can continue.")));
            var actions = DialogActions(panel);
            if (recovery?.LatestReport is {} incident && incident.Extension == identity)
                AsyncButton(actions,T("Consulter le rapport","View report"), () => ShowIncidentAsync(incident,failed));
            ActionButton(actions,T("Fermer","Close"),failed.Close);
            await failed.ShowDialog(owner);
            return;
        }

        var dialog = PlatformDialog(item.Manifest.Name, 520, 340);
        var details = DialogStack(dialog);
        var culture = CultureInfo.GetCultureInfo(english ? "en-US" : "fr-FR");
        foreach (var result in results)
            details.Children.Add(DialogText(result.Label + " : " + result.Value.ToString($"F{item.Settings.DecimalPlaces}",culture)));
        ActionButton(details,T("Fermer","Close"),dialog.Close);
        await dialog.ShowDialog(owner);
    }

    void AddExtensionDetails(Panel details, ExtensionManifest manifest)
    {
        details.Children.Add(new TextBlock{ Text=manifest.Name, FontSize=17, FontWeight=FontWeight.SemiBold, TextWrapping=TextWrapping.Wrap });
        details.Children.Add(DialogText(manifest.Description));
        details.Children.Add(DialogText(T("Identifiant : ","Identifier: ") + manifest.Id));
        details.Children.Add(DialogText(T("Version : ","Version: ") + manifest.Version));
        details.Children.Add(DialogText(T("Auteur : ","Author: ") + manifest.Author));
        details.Children.Add(DialogText(T("Licence : ","License: ") + manifest.License));
        details.Children.Add(DialogText(T("Provenance déclarée : ","Declared provenance: ") + manifest.Provenance.Publisher));
        if (manifest.Provenance.Source is {} source) details.Children.Add(DialogText(source));
        details.Children.Add(DialogText(T("Compatibilité : ","Compatibility: ") + "≥ " + manifest.Compatibility.Minimum +
            " · < " + manifest.Compatibility.MaximumExclusive + " · API " + manifest.ApiVersion));
        details.Children.Add(DialogText(T("Fonctions : ","Features: ") + string.Join(", ",manifest.Features)));
        details.Children.Add(DialogText(T("Permissions : ","Permissions: ") + (manifest.Permissions.Length == 0 ? T("aucune","none")
            : string.Join(", ",manifest.Permissions))));
    }

    async Task<bool> ConfirmExtensionAsync(Window owner, ExtensionManifest manifest, bool retry = false)
    {
        var dialog = PlatformDialog(retry ? T("Réactiver l’extension","Re-enable extension") : T("Installer l’extension","Install extension"), 600, 495);
        var details = DialogStack(dialog);
        AddExtensionDetails(details,manifest);
        if (retry) details.Children.Add(DialogText(T("Cette version a déjà été désactivée après un incident.","This version was previously disabled after an incident.")));
        var actions = DialogActions(details);
        ActionButton(actions,T("Annuler","Cancel"),() => dialog.Close(false));
        ActionButton(actions,retry ? T("Réactiver","Re-enable") : T("Installer","Install"),() => dialog.Close(true), name:"ConfirmExtension");
        return await dialog.ShowDialog<bool>(owner);
    }

    async Task<bool> ConfirmUninstallAsync(Window owner, string name)
    {
        var dialog = PlatformDialog(T("Désinstaller l’extension","Uninstall extension"), 520, 200);
        var panel = DialogStack(dialog);
        panel.Children.Add(DialogText(T("Désinstaller « ","Uninstall “") + name + T(" » et supprimer ses réglages ?","” and remove its settings?")));
        var actions = DialogActions(panel);
        ActionButton(actions,T("Annuler","Cancel"),() => dialog.Close(false));
        ActionButton(actions,T("Désinstaller","Uninstall"),() => dialog.Close(true), name:"ConfirmUninstall");
        return await dialog.ShowDialog<bool>(owner);
    }

    async Task<ExtensionSettings?> EditExtensionSettingsAsync(Window owner, InstalledExtension item)
    {
        var dialog = PlatformDialog(T("Configurer","Configure") + " — " + item.Manifest.Name, 505, 250);
        var panel = DialogStack(dialog);
        bool theme = item.Package.Theme is not null;
        panel.Children.Add(DialogText(theme ? T("Palette de l’extension","Extension palette")
            : T("Nombre de décimales affichées","Number of displayed decimal places")));
        var variants = new ComboBox { Name = "ExtensionSetting", ItemsSource = theme ?
            new[] { T("Principale","Default"),T("Alternative","Alternate") } : new[] { "0", "1", "2", "3" },
            SelectedIndex = theme ? Convert.ToInt32(item.Settings.AlternateTheme) : item.Settings.DecimalPlaces };
        panel.Children.Add(variants);
        var actions = DialogActions(panel);
        ActionButton(actions,T("Annuler","Cancel"),dialog.Close);
        ActionButton(actions,T("Appliquer","Apply"),() => dialog.Close(theme
            ? item.Settings with { AlternateTheme = variants.SelectedIndex == 1 }
            : item.Settings with { DecimalPlaces = Math.Max(0,variants.SelectedIndex) }), name:"ApplyExtensionSettings");
        return await dialog.ShowDialog<ExtensionSettings?>(owner);
    }

    sealed record ExtensionRow(ExtensionManifest Manifest, InstalledExtension? Installed, CatalogEntry? Remote)
    {
        public override string ToString() => Manifest.Name;
    }

    sealed class ExtensionPane
    {
        internal readonly ListBox List;
        internal readonly StackPanel Details = new(){ Spacing = 8, Margin = new Thickness(14,4,4,4) };
        internal readonly Grid Content;
        readonly TextBlock empty;

        internal ExtensionPane(string name)
        {
            List = new ListBox { Name = name };
            Content = new Grid { ColumnDefinitions = new("225,*"), Margin = new Thickness(0,10,0,0) };
            Content.Children.Add(List);
            var scroller = new ScrollViewer { Content = Details };
            Grid.SetColumn(scroller,1);
            Content.Children.Add(scroller);
            empty = DialogText("");
            empty.Margin = new Thickness(10);
            Grid.SetColumnSpan(empty,2);
            Content.Children.Add(empty);
        }

        internal void Show(IEnumerable<ExtensionRow> source, string? preferredId, string emptyMessage)
        {
            string? selectedId = preferredId ?? (List.SelectedItem as ExtensionRow)?.Manifest.Id;
            var rows = source.ToArray();
            List.ItemsSource = rows;
            List.SelectedItem = rows.FirstOrDefault(row => row.Manifest.Id == selectedId) ?? rows.FirstOrDefault();
            bool hasItems = rows.Length > 0;
            List.IsVisible = Details.IsVisible = hasItems;
            empty.IsVisible = !hasItems;
            empty.Text = emptyMessage;
        }
    }
}
