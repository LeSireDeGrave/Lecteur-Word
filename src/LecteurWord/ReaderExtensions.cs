using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    readonly ExtensionManagerService? extensions;
    InstalledExtension? activeTheme;
    SessionOperation? activeExtensionOperation;
    bool extensionsSuspended;

    void ReloadPlatformExtensions(bool propagateError = false)
    {
        if (extensions is null) return;
        InstalledExtension? theme = null;
        try
        {
            var installed = extensionsSuspended ? [] : extensions.List().Items;
            var active = installed.Where(item => extensions.CanRun(item, installed)).ToArray();
            translations = active.Where(item => item.Package.Translation is not null).ToArray();
            ReloadNavigation(active);
            var previousAccessibility = extensionAccessibility;
            extensionAccessibility = active.FirstOrDefault(item => item.Package.Accessibility is not null)?.Package.Accessibility;
            theme = active.FirstOrDefault(item => item.Package.Theme is not null);
            if (theme is not null) BeginExtension(SessionStage.ApplyExtension,theme.Manifest.Identity);
            activeTheme = theme;
            if (previousAccessibility != extensionAccessibility) ApplyDisplayPreferences();
            else ApplyTheme();
            if (activeTranslation != (AvailableLanguages().FirstOrDefault(item => item.Id == languageChoice)
                ?? ReaderLanguage.SystemLanguage(CultureInfo.CurrentUICulture)).Catalog)
                ApplyLanguage(refresh: true);
        }
        catch (Exception error)
        {
            RecordIncident(error,SessionStage.ApplyExtension,theme?.Manifest.Identity);
            if (theme is not null) QuarantineExtension(theme.Manifest.Identity);
            activeTheme = null;
            ApplyTheme();
            if (propagateError) throw new InvalidDataException("Le thème a été désactivé après une erreur.",error);
        }
        finally
        {
            if (theme is not null) EndExtension();
        }


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

    async Task ShowExtensionsAsync(string? initialCategory = null)
    {
        if (extensions is not {} manager) return;
        var dialog = PlatformDialog(T("Extensions","Extensions"), 840, 610);
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(16) };
        dialog.Content = root;
        var top = DialogActions(root);
        var feedback = DialogText("");
        IReadOnlyList<CatalogEntry> catalogEntries = [];
        bool catalogLoaded = false;
        bool catalogBusy = false;
        ExtensionCatalog? catalog = null;
        string? catalogProblem = null;
        var paths = PlatformPaths.ForCurrentUser();
        CommunityConfiguration? configuration = null;
        try
        {
            configuration = GitHubSupportService.Load(paths);
            LoadCatalog();
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
        if (initialCategory is not null) tabs.SelectedIndex = 1;
        Grid.SetRow(tabs,3);
        root.Children.Add(tabs);
        Grid.SetRow(feedback,4);
        root.Children.Add(feedback);
        var listing = manager.List();
        var filters = new StackPanel { Spacing = 6, Margin = new Thickness(0,8,0,0) };
        var search = new TextBox { Name = "ExtensionSearch", MaxLength = 120,
            PlaceholderText = T("Nom, auteur, identifiant ou description", "Name, author, identifier or description") };
        AutomationProperties.SetName(search,T("Rechercher une extension","Search extensions"));
        filters.Children.Add(search);
        var categories = DialogActions(filters);
        string? category = initialCategory;
        installed.List.SelectionChanged += (_, _) => DescribeInstalled();
        discover.List.SelectionChanged += (_, _) => DescribeCatalog(discover,update:false);
        updates.List.SelectionChanged += (_, _) => DescribeCatalog(updates,update:true);
        string[] featureChoices = [ExtensionPackage.ThemeFeature,ExtensionPackage.TranslationFeature,
            ExtensionPackage.ReadingFeature,ExtensionPackage.NavigationFeature,ExtensionPackage.AccessibilityFeature];
        foreach (string? feature in featureChoices.Prepend(null))
        {
            var option = new RadioButton { Name = feature is null ? "ExtensionCategoryAll" : "ExtensionCategory_" + feature.Replace('.','_'),
                GroupName = "ExtensionCategory", Content = feature is null ? T("Toutes","All") : ExtensionCategory(feature),
                IsChecked = feature == category };
            option.IsCheckedChanged += (_, _) =>
            {
                if (option.IsChecked != true) return;
                category = feature;
                Refresh(refreshRegistry:false);
            };
            categories.Children.Add(option);
        }
        search.TextChanged += (_, _) => Refresh(refreshRegistry:false);
        Grid.SetRow(filters,2);
        root.Children.Add(filters);

        var localInput = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 7, 0, 0) };
        var localPath = new TextBox { Name = "LocalExtensionPath", FlowDirection = FlowDirection.LeftToRight,
            PlaceholderText = T("Chemin du fichier .lwext ou glisser-déposer", "Path to .lwext file or drag and drop"), MaxLength = 4096 };
        AutomationProperties.SetName(localPath,T("Chemin du paquet local", "Local package path"));
        localInput.Children.Add(localPath);
        var localAction = new StackPanel { Margin = new Thickness(6,0,0,0) };
        Grid.SetColumn(localAction,1);
        localInput.Children.Add(localAction);
        bool installing = false;
        var install = AsyncButton(localAction,T("Installer","Install"),() => InstallLocalAsync(localPath.Text),feedback,
            name:"InstallExtensionFromPath");
        localPath.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && install.IsEnabled)
            {
                args.Handled = true;
                _ = InstallLocalAsync(localPath.Text);
            }
        };
        Grid.SetRow(localInput,1);
        root.Children.Add(localInput);
        DragDrop.SetAllowDrop(root, true);
        DragDrop.AddDragOverHandler(root, (_, args) =>
        {
            args.DragEffects = TryLocalDrop(args) is null ? DragDropEffects.None : DragDropEffects.Copy;
            args.Handled = true;
        });
        DragDrop.AddDropHandler(root, async (_, args) =>
        {
            args.Handled = true;
            if (TryLocalDrop(args) is {} path)
            {
                localPath.Text = path;
                await InstallLocalAsync(path);
            }
        });

        AsyncButton(top,T("Parcourir…","Browse…"),async () =>
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
            localPath.Text = path;
            await InstallLocalAsync(path);
        },feedback, name:"InstallLocalExtension");

        var refreshCatalog = AsyncButton(top,T("Actualiser le catalogue","Refresh catalog"),async () =>
        {
            if (catalogBusy || configuration?.CatalogOffline == true || catalog is not {} source) return;
            catalogBusy = true;
            try
            {
                catalogEntries = await source.ReadAsync(lifetime.Token);
                catalogLoaded = true;
                catalogProblem = null;
                Refresh();
                feedback.Text = T("Catalogue vérifié.","Catalog checked.");
            }
            catch (Exception error) when (catalogLoaded && error is (IOException or InvalidDataException or TimeoutException))
            {
                feedback.Text = T("Actualisation impossible. La dernière liste vérifiée reste affichée.",
                    "Refresh failed. The last verified list is still shown.");
            }
            finally { catalogBusy = false; }
        },feedback,name:"CheckExtensionCatalog");
        refreshCatalog.IsVisible = catalog is not null && configuration?.CatalogOffline != true;
        var settings = AsyncButton(top,T("Catalogue…","Catalog…"),async () =>
        {
            if (catalogBusy || configuration is not {} current) return;
            var edited = await EditCatalogAsync(dialog,current);
            if (edited is null) return;
            GitHubSupportService.Save(paths,edited);
            configuration = edited;
            LoadCatalog();
            refreshCatalog.IsVisible = catalog is not null && !edited.CatalogOffline;
            Refresh();
            feedback.Text = catalogProblem ?? (edited.CatalogOffline ? T("Mode hors connexion.","Offline mode.") : "");
        },feedback,name:"ExtensionCatalogSettings");
        settings.IsEnabled = configuration is not null;
        ActionButton(top,T("Fermer","Close"),dialog.Close);
        Refresh();
        if (configuration?.CatalogOffline == true)
            feedback.Text = catalogProblem ?? T("Mode hors connexion.","Offline mode.");
        if (extensionsSuspended) feedback.Text = T("Les extensions sont suspendues pour ce démarrage.","Extensions are suspended for this startup.");
        await dialog.ShowDialog(this);

        string? TryLocalDrop(DragEventArgs args)
        {
            if (installing || args.DataTransfer.TryGetFiles() is not [IStorageFile file]) return null;
            return file.TryGetLocalPath() is {} path && Path.GetExtension(path).Equals(".lwext",StringComparison.OrdinalIgnoreCase)
                ? path : null;
        }

        async Task InstallLocalAsync(string? path)
        {
            if (installing || lifetime.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                !Path.GetExtension(path).Equals(".lwext",StringComparison.OrdinalIgnoreCase))
            {
                feedback.Text = T("Indiquez le chemin absolu d’un fichier .lwext.", "Enter the absolute path to a .lwext file.");
                return;
            }
            installing = true;
            try
            {
                var candidate = await Task.Run(() => manager.InspectLocal(path),lifetime.Token);
                if (lifetime.IsCancellationRequested || !await ConfirmExtensionAsync(dialog,candidate.Manifest)) return;
                manager.Install(candidate);
                ReloadPlatformExtensions();
                Refresh(candidate.Manifest.Id);
                feedback.Text = T("Paquet installé. Activez-le depuis sa fiche.","Package installed. Enable it from its details.");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                feedback.Text = error is InvalidDataException ? ExtensionError(error.Message)
                    : T("Le fichier local ne peut pas être lu ou installé.", "The local file cannot be read or installed.");
            }
            finally { installing = false; }
        }

        void LoadCatalog()
        {
            catalogEntries = [];
            catalogLoaded = false;
            catalogProblem = null;
            catalog = null;
            if (configuration?.CatalogUrl is not {} url) return;
            try
            {
                catalog = new ExtensionCatalog(url,manager.ReaderVersion,cacheRoot:PlatformPaths.ExtensionCatalogCache);
                if (catalog.ReadCached() is {} entries)
                {
                    catalogEntries = entries;
                    catalogLoaded = true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                catalogProblem = ExtensionError(error.Message);
            }
        }

        bool Matches(ExtensionManifest manifest)
        {
            if (category is not null && manifest.Features[0] != category) return false;
            if (search.Text?.Trim() is not { Length: > 0 } query) return true;
            const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            return new[] {manifest.Name,manifest.Description,manifest.Id,manifest.Author}.Any(
                text => uiCulture.CompareInfo.IndexOf(text,query,options) >= 0);
        }

        void Refresh(string? installedId = null, bool refreshRegistry = true)
        {
            if (refreshRegistry) listing = manager.List();
            string none = T("Aucune extension ne correspond aux filtres.","No extensions match the filters.");
            installed.Show(listing.Items.Where(item => Matches(item.Manifest)).Select(item => new ExtensionRow(item.Manifest,item,null)),
                installedId,listing.Items.Count != 0 ? none : listing.Unreadable == 0 ? T("Aucune extension installée.","No installed extensions.")
                : T("Des fiches locales d’extensions sont illisibles.","Some installed extension records are unreadable."));
            var byId = listing.Items.ToDictionary(item => item.Manifest.Id,StringComparer.Ordinal);
            string state = catalogProblem ?? (catalog is null
                ? T("Le catalogue communautaire doit encore être configuré.","The community catalog still needs to be configured.")
                : configuration?.CatalogOffline == true ? T("Aucun catalogue vérifié en cache.","No verified catalog is cached.")
                : T("Actualisez le catalogue pour charger les extensions et les mises à jour compatibles.",
                    "Refresh the catalog to load compatible extensions and updates."));
            var discoveries = catalogEntries.Where(entry => !byId.ContainsKey(entry.Manifest.Id)).ToArray();
            discover.Show(discoveries.Where(entry => Matches(entry.Manifest)).Select(entry => new ExtensionRow(entry.Manifest,null,entry)),
                null,catalogLoaded ? (discoveries.Length > 0 ? none : T("Aucune nouvelle extension compatible.","No new compatible extensions.")) : state);
            var upgrades = catalogEntries.Where(entry => byId.TryGetValue(entry.Manifest.Id,out var previous) &&
                ExtensionPackage.StableVersion(entry.Manifest.Version) > ExtensionPackage.StableVersion(previous.Manifest.Version))
                .ToArray();
            updates.Show(upgrades.Where(entry => Matches(entry.Manifest)).Select(entry => new ExtensionRow(entry.Manifest,byId[entry.Manifest.Id],entry)),
                null,catalogLoaded ? (upgrades.Length > 0 ? none : T("Aucune mise à jour compatible.","No compatible updates.")) : state);
            if (listing.Unreadable > 0) feedback.Text = LocalFormat("extensions.unreadable","Fiches locales illisibles : {0}","Unreadable local records: {0}",listing.Unreadable);
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
            bool dependenciesMet = manager.DependenciesAvailable(item, listing.Items);
            details.Children.Add(DialogText(item.Quarantined ? T("Désactivée après un incident.","Disabled after an incident.")
                : item.Enabled && compatible && dependenciesMet ? T("Activée","Enabled") : T("Désactivée","Disabled")));
            if (!compatible) details.Children.Add(DialogText(T("Cette version est incompatible.","This version is incompatible.")));
            if (!dependenciesMet) details.Children.Add(DialogText(T("Dépendances absentes, incompatibles ou désactivées.",
                "Dependencies are missing, incompatible, or disabled.")));
            if (item.DownloadSource is not null)
                details.Children.Add(DialogText(T("Origine du téléchargement : ","Download source: ") + item.DownloadSource));
            var actions = DialogActions(details);
            bool canEnable = compatible && dependenciesMet && !extensionsSuspended;
            if (item.Enabled || canEnable)
                AsyncButton(actions,item.Enabled ? T("Désactiver","Disable") : T("Activer","Enable"),async () =>
                {
                    if (!item.Enabled && item.Quarantined && !await ConfirmExtensionAsync(dialog,manifest,retry:true)) return;
                    manager.SetEnabled(manifest.Id,!item.Enabled);
                    try
                    {
                        ReloadPlatformExtensions(propagateError:true);
                        feedback.Text = "";
                    }
                    finally { Refresh(manifest.Id); }
                },feedback,name:item.Enabled ? "DisableExtension" : "EnableExtension");
            if (compatible && (item.Package.Theme is not null || item.Package.Reading is not null))
                AsyncButton(actions,T("Configurer","Configure"),async () =>
                {
                    if (await EditExtensionSettingsAsync(dialog,item) is {} settings)
                    {
                        manager.Configure(manifest.Id,settings);
                        try { ReloadPlatformExtensions(propagateError:true); }
                        finally { Refresh(manifest.Id); }
                    }
                },feedback, name:"ConfigureExtension");
            AsyncButton(actions,T("Désinstaller","Uninstall"),async () =>
            {
                if (!await ConfirmUninstallAsync(dialog,manifest.Name)) return;
                manager.Uninstall(manifest.Id);
                ReloadPlatformExtensions();
                Refresh();
            },feedback, name:"UninstallExtension");
            if (!extensionsSuspended && manager.CanRun(item,listing.Items) && item.Package.Reading is not null && readable?.Available == true)
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
            if (pane.List.SelectedItem is not ExtensionRow { Remote: {} entry } row || catalog is not {} source) return;
            AddExtensionDetails(details,entry.Manifest);
            if (update) details.Children.Add(DialogText(T("Version installée : ","Installed version: ") + row.Installed!.Manifest.Version));
            bool offline = configuration?.CatalogOffline == true;
            bool cached = source.HasCachedPackage(entry);
            details.Children.Add(DialogText(T("Adresse du paquet : ","Package address: ") + entry.PackageUrl));
            if (cached) details.Children.Add(DialogText(T("Paquet disponible dans le cache.","Package available in cache.")));
            else if (offline) details.Children.Add(DialogText(T("Ce paquet est indisponible hors connexion.","This package is unavailable offline.")));
            var actions = DialogActions(details);
            if (!cached && !offline)
                AsyncButton(actions,T("Télécharger","Download"),() => FetchCatalogPackage(install:false),feedback,name:"DownloadCatalogExtension");
            if (!offline || cached)
                AsyncButton(actions,update ? T("Installer la mise à jour","Install update") : T("Installer depuis le catalogue","Install from catalog"),
                    () => FetchCatalogPackage(install:true),feedback,name:update?"UpdateCatalogExtension":"InstallCatalogExtension");

            async Task FetchCatalogPackage(bool install)
            {
                if (catalogBusy) return;
                catalogBusy = true;
                try
                {
                    if (install && !await ConfirmExtensionAsync(dialog,entry.Manifest)) return;
                    var candidate = await source.DownloadAsync(entry,lifetime.Token,offline,cacheRequired:!install);
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (!install)
                    {
                        Refresh(refreshRegistry:false);
                        feedback.Text = T("Paquet vérifié et téléchargé.","Verified package downloaded.");
                        return;
                    }
                    manager.Install(candidate,entry.PackageUrl);
                    ReloadPlatformExtensions();
                    Refresh(entry.Manifest.Id);
                    feedback.Text = T("Paquet installé. Activez-le depuis sa fiche.","Package installed. Enable it from its details.");
                }
                finally { catalogBusy = false; }
            }
        }
    }

    string ExtensionCategory(string feature) => feature switch
    {
        ExtensionPackage.ThemeFeature => T("Thèmes","Themes"),
        ExtensionPackage.TranslationFeature => T("Langues","Languages"),
        ExtensionPackage.ReadingFeature => T("Statistiques","Statistics"),
        ExtensionPackage.NavigationFeature => T("Navigation","Navigation"),
        ExtensionPackage.AccessibilityFeature => T("Accessibilité","Accessibility"),
        _ => feature
    };

    async Task<CommunityConfiguration?> EditCatalogAsync(Window owner, CommunityConfiguration configuration)
    {
        var dialog = PlatformDialog(T("Catalogue d’extensions","Extension catalog"),570,310);
        var panel = DialogStack(dialog);
        panel.Children.Add(DialogText(T("Adresse HTTPS de l’index de confiance","HTTPS address of the trusted index")));
        var address = new TextBox { Name = "ExtensionCatalogUrl", Text = configuration.CatalogUrl,
            MaxLength = 2048, FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(address,T("Adresse du catalogue","Catalog address"));
        panel.Children.Add(address);
        var offline = new CheckBox { Name = "ExtensionCatalogOffline", Content = T("Hors connexion","Offline"),
            IsChecked = configuration.CatalogOffline };
        panel.Children.Add(offline);
        panel.Children.Add(DialogText(T("Le mode hors connexion utilise uniquement les paquets locaux et les téléchargements vérifiés.",
            "Offline mode uses local packages and verified downloads only.")));
        var feedback = DialogText("");
        panel.Children.Add(feedback);
        var actions = DialogActions(panel);
        ActionButton(actions,T("Annuler","Cancel"),dialog.Close);
        ActionButton(actions,T("Enregistrer","Save"),() =>
        {
            string text = address.Text?.Trim() ?? "";
            if (text.Length != 0 && !ExtensionPackage.IsHttps(text))
            {
                feedback.Text = T("Saisissez une adresse HTTPS valide.","Enter a valid HTTPS address.");
                return;
            }
            dialog.Close(configuration with { CatalogUrl = text.Length == 0 ? null : text, CatalogOffline = offline.IsChecked == true });
        },name:"SaveExtensionCatalogSettings");
        return await dialog.ShowDialog<CommunityConfiguration?>(owner);
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
        catch (PlatformNotSupportedException) { throw; }
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
        foreach (var result in results)
            details.Children.Add(DialogText(result.Label + " : " + result.Value.ToString($"F{item.Settings.DecimalPlaces}",uiCulture)));
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
        if (manifest.Dependencies is { Length: > 0 } dependencies)
            details.Children.Add(DialogText(T("Dépendances : ", "Dependencies: ") + string.Join(", ",
                dependencies.Select(d => $"{d.Id} ≥ {d.Minimum} · < {d.MaximumExclusive}"))));
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
        var variants = DialogActions(panel);
        int selected = theme ? Convert.ToInt32(item.Settings.AlternateTheme) : item.Settings.DecimalPlaces;
        string[] options = theme ? [T("Principale","Default"),T("Alternative","Alternate")] : ["0", "1", "2", "3"];
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var choice = new RadioButton { Content = options[i], Name = "ExtensionSetting" + i, GroupName = "ExtensionSettings",
                IsChecked = i == selected };
            choice.IsCheckedChanged += (_, _) => { if (choice.IsChecked == true) selected = index; };
            variants.Children.Add(choice);
        }
        var actions = DialogActions(panel);
        ActionButton(actions,T("Annuler","Cancel"),dialog.Close);
        ActionButton(actions,T("Appliquer","Apply"),() => dialog.Close(theme
            ? item.Settings with { AlternateTheme = selected == 1 }
            : item.Settings with { DecimalPlaces = selected }), name:"ApplyExtensionSettings");
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
