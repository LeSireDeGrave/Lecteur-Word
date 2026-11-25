using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LecteurWord.Platform;

static partial class PlatformCases
{
    static readonly Version ReaderVersion = new(0,1,0);

    internal static async Task ExtensionsAsync(string root)
    {
        var paths = Paths(root);
        var manager = new ExtensionManagerService(paths,ReaderVersion);
        Check(manager.List() is { Items.Count: 0, Unreadable: 0 },"Empty extension registry");
        var contribution = new ThemeContribution(new("#E5F4E9","#214D3D"),new("#E9F2FC","#263C70"));
        var theme = manager.InspectLocal(CreatePackage(root,"test.theme",contribution));
        manager.Install(theme);
        var enabled = manager.SetEnabled(theme.Manifest.Id,true);
        Check(manager.CanRun(enabled) && enabled.Package.Theme == contribution,"Theme contributed from external package");
        manager.Configure(theme.Manifest.Id,new(AlternateTheme:true));
        var stored = new ExtensionManagerService(paths,ReaderVersion).List().Items.Single();
        Check(stored.Settings.AlternateTheme && stored.Enabled,"Activation and configuration persist");

        Fails(() => manager.InspectLocal(CreatePackage(root,"test.theme",contribution,version:"1.1.0",badDigest:true)),
            "Bad digest is refused");
        Fails(() => manager.InspectLocal(CreatePackage(root,"test.theme",contribution,api:2)), "Incompatible API is refused");
        Fails(() => manager.InspectLocal(CreatePackage(root,"test.theme",contribution,minimum:"2.0.0")),
            "Incompatible reader is refused");
        Check(manager.List().Items.Single().Manifest.Version == "1.0.0" && manager.List().Items.Single().Enabled,
            "A failed update preserves both the installed version and activation");
        var themeUpdate = manager.InspectLocal(CreatePackage(root,"test.theme",contribution,version:"1.1.0"));
        var updated = manager.Install(themeUpdate);
        Check(!updated.Enabled && updated.Settings.AlternateTheme && updated.Manifest.Version == "1.1.0",
            "Accepted update preserves settings and requires activation");
        Fails(() => manager.Install(theme), "Downgrade is refused");
        Check(manager.List().Items.Single().Manifest.Version == "1.1.0", "Downgrade leaves the accepted version");
        manager.SetEnabled(theme.Manifest.Id,true);
        manager.SetEnabled(theme.Manifest.Id,false);
        manager.Uninstall(theme.Manifest.Id);
        Check(manager.List().Items.Count == 0,"Disable and uninstall");

        var reading = new ReadingContribution([new("Part",["headings","100","*","bodyBlocks","/"])]);
        var readerPackage = manager.InspectLocal(CreatePackage(root,"test.reading",reading));
        manager.Install(readerPackage);
        var readingItem = manager.SetEnabled(readerPackage.Manifest.Id,true);
        var metrics = new DocumentMetrics(20,15,3,2,1,4);
        var results = await ReadingProtocol.RunAsync(readingItem.Package.Reading!,metrics,CancellationToken.None);
        Check(results is [{Label:"Part",Value:15}],"Independent worker correctly evaluates the read-only metrics API");
        var failing = manager.InspectLocal(CreatePackage(root,"test.error",new ReadingContribution([new("Division",["1","0","/"])])));
        manager.Install(failing);
        manager.SetEnabled(failing.Manifest.Id,true);
        using (var recovery = new SessionRecoveryService(paths))
        {
            recovery.Begin(new(SessionStage.RunExtension,Extension:failing.Manifest.Identity));
            try
            {
                await ReadingProtocol.RunAsync(failing.Reading!,metrics,CancellationToken.None);
                throw new InvalidOperationException("Faulty extension produced a result");
            }
            catch (InvalidDataException error)
            {
                recovery.Record(error);
                manager.Quarantine(failing.Manifest.Identity);
            }
            Check(recovery.LatestReport?.Extension == failing.Manifest.Identity &&
                manager.List().Items.Single(item => item.Manifest.Id == failing.Manifest.Id) is { Enabled: false, Quarantined: true },
                "Worker error isolated and identified in recovery journal");
            recovery.Close(SessionRecoveryService.DefaultReading());
        }

        var remote = new CatalogEntry(readerPackage.Manifest,"https://catalog.example/lwext",
            Convert.ToHexStringLower(SHA256.HashData(readerPackage.Archive)));
        var nextReader = manager.InspectLocal(CreatePackage(root,"test.reading",reading,version:"1.2.0"));
        var compatibleUpdate = new CatalogEntry(nextReader.Manifest,remote.PackageUrl,Convert.ToHexStringLower(SHA256.HashData(nextReader.Archive)));
        var incompatibleUpdate = remote with { Manifest = remote.Manifest with { Version = "2.0.0", Compatibility = new("2.0.0","3.0.0") } };
        using var transport = new HttpClient(new CatalogHandler(new CatalogIndex(1,[remote,compatibleUpdate,incompatibleUpdate]),nextReader.Archive));
        var catalog = new ExtensionCatalog("https://catalog.example/index.json",ReaderVersion,transport);
        var index = await catalog.ReadAsync(CancellationToken.None);
        Check(index is [{ Manifest.Version: "1.2.0" }],"Catalog selects latest compatible release");
        var downloaded = await catalog.DownloadAsync(index.Single(),CancellationToken.None);
        manager.Install(downloaded,index.Single().PackageUrl);
        Check(manager.List().Items.Single(item => item.Manifest.Id == remote.Manifest.Id).DownloadSource == remote.PackageUrl,
            "Catalog install persists verified provenance");
        try
        {
            await catalog.DownloadAsync(remote,CancellationToken.None);
            throw new InvalidOperationException("Inconsistent package accepted");
        }
        catch (InvalidDataException) { }
    }

    static string CreatePackage<T>(string root, string id, T payload, string version = "1.0.0",
        int api = 1, string minimum = "0.1.0", bool badDigest = false, ExtensionDependency[]? dependencies = null)
    {
        (string Feature, string[] Permissions) contribution = payload switch
        {
            ThemeContribution => (ExtensionPackage.ThemeFeature, []),
            TranslationContribution => (ExtensionPackage.TranslationFeature, []),
            NavigationContribution => (ExtensionPackage.NavigationFeature, [ExtensionPackage.NavigationPermission]),
            AccessibilityContribution => (ExtensionPackage.AccessibilityFeature, []),
            _ => (ExtensionPackage.ReadingFeature, [ExtensionPackage.MetricsPermission])
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload,PlatformFiles.Json);
        var manifest = new ExtensionManifest(id,id,"Extension de test","Recette locale",version,new(minimum,"3.0.0"),api,
            [contribution.Feature],contribution.Permissions,"MIT",new("Local",null),
            badDigest ? new string('0',64) : Convert.ToHexStringLower(SHA256.HashData(bytes))) { Dependencies = dependencies };
        return Fixtures.Write(root,id + ".lwext",new()
        {
            ["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest,PlatformFiles.Json),
            ["payload.json"] = bytes
        });
    }

    internal static void Contributions(string root)
    {
        var manager = new ExtensionManagerService(Paths(root),ReaderVersion);
        var translation = new TranslationContribution("fr-CA", "Français canadien",
            new() { ["document.words.one"] = "{0:N0} mot", ["diagnostic.export_nova_tabs"] = "Taquets de tabulation adaptés." });
        var language = manager.Install(manager.InspectLocal(CreatePackage(root,"test.language",translation)));
        var contrast = new AccessibilityContribution("high-contrast",115);
        var access = manager.Install(manager.InspectLocal(CreatePackage(root,"test.assistance",contrast,
            dependencies:[new("test.language","1.0.0","2.0.0")])));
        Fails(() => manager.SetEnabled(access.Manifest.Id,true),"A disabled dependency prevents activation");
        Check(manager.SetEnabled(language.Manifest.Id,true).Package.Translation?.Culture == "fr-CA", "Language pack valid");
        access = manager.SetEnabled(access.Manifest.Id,true);
        Check(manager.CanRun(access) && access.Package.Accessibility == contrast,"Accessibility settings can load");
        manager.Quarantine(language.Manifest.Identity);
        Check(!manager.CanRun(access),"A quarantined dependency stops the dependent package");
        manager.SetEnabled(language.Manifest.Id,true);
        Check(manager.CanRun(access),"Reactivating the dependency restores the dependent package");
        manager.Install(manager.InspectLocal(CreatePackage(root,"test.language",translation,version:"2.0.0")));
        manager.SetEnabled(language.Manifest.Id,true);
        Check(!manager.CanRun(access),"A version outside the declared interval stops the dependent package");

        var navigation = new NavigationContribution([new("pages","Pages","Pages",["sidebar"],4)]);
        manager.Install(manager.InspectLocal(CreatePackage(root,"test.navigation",navigation)));
        manager.SetEnabled("test.navigation",true);
        manager.Install(manager.InspectLocal(CreatePackage(root,"test.conflict",navigation)));
        Fails(() => manager.SetEnabled("test.conflict",true),"Two enabled navigation extensions cannot claim the same shortcut");
        manager.SetEnabled("test.navigation",false);
        Check(manager.CanRun(manager.SetEnabled("test.conflict",true)),"A shortcut can be reused after disable");
        Fails(() => manager.InspectLocal(CreatePackage(root,"test.invalid",
            new NavigationContribution([new("exec","Exécuter","Run",["toolbar"],1)]))),"Unsupported navigation action is refused");
        Fails(() => manager.InspectLocal(CreatePackage(root,"test.accessibility",
            new AccessibilityContribution("high-contrast",3000))),"Unsupported magnification is refused");
    }

    static void Fails(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    sealed class CatalogHandler(CatalogIndex index, byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri!.AbsolutePath == "/index.json"
                    ? JsonSerializer.SerializeToUtf8Bytes(index,PlatformFiles.Json) : archive)
            });
    }
}
