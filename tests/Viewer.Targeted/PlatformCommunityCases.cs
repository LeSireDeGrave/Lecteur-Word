using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.Platform;

static partial class PlatformCases
{
    internal static async Task CommunityAsync(string root)
    {
        const string id = "test.community.navigation";
        const string address = "https://localhost:8642/";
        string source = Path.Combine(root,"sources");
        string packages = Path.Combine(root,"packages");
        Directory.CreateDirectory(packages);
        Tool("init","navigation",id,source);
        string manifest = Path.Combine(source,"manifest.json");
        string payload = Path.Combine(source,"payload.json");
        string version1 = Path.Combine(packages,"navigation-1.lwext");
        Tool("pack",manifest,payload,version1);
        Tool("validate",version1);
        string index1 = Path.Combine(root,"index-v1.json");
        Tool("catalog",address,packages,index1);
        Tool("validate-catalog",index1);

        var manager = new ExtensionManagerService(Paths(root),ReaderVersion);
        using var handler = new CommunityFixture(index1,packages);
        using var client = new HttpClient(handler);
        string cache = Path.Combine(root,"cache");
        var catalog = new ExtensionCatalog(address+"index.json",ReaderVersion,client,cache);
        var first = (await catalog.ReadAsync(default)).Single();
        Check(!catalog.HasCachedPackage(first),"The index does not download packages");
        manager.Install(await catalog.DownloadAsync(first,default,cacheRequired:true),first.PackageUrl);
        Check(catalog.HasCachedPackage(first) && manager.SetEnabled(id,true).Enabled,"First package cached and activated");

        var next = JsonNode.Parse(File.ReadAllBytes(manifest))!;
        next["version"] = "1.1.0";
        File.WriteAllText(manifest,next.ToJsonString());
        string version2 = Path.Combine(packages,"navigation-2.lwext");
        Tool("pack",manifest,payload,version2);
        string index2 = Path.Combine(root,"index-v2.json");
        Tool("catalog",address,packages,index2);
        handler.Index = index2;
        var upgrade = (await catalog.ReadAsync(default)).Single();
        Check(upgrade.Manifest.Version == "1.1.0","Catalog exposes the newest compatible update");
        handler.Corrupt = true;
        try
        {
            await catalog.DownloadAsync(upgrade,default);
            throw new InvalidOperationException("A tampered package was accepted");
        }
        catch (InvalidDataException) { }
        Check(!catalog.HasCachedPackage(upgrade) && manager.List().Items.Single().Manifest.Version=="1.0.0","Tampering preserves the installed package");

        handler.Corrupt = false;
        await catalog.DownloadAsync(upgrade,default,cacheRequired:true);
        int requests = handler.Requests;
        handler.Offline = true;
        var offline = new ExtensionCatalog(address+"index.json",ReaderVersion,client,cache);
        var cached = offline.ReadCached()!.Single();
        Check(cached.Manifest.Identity == upgrade.Manifest.Identity,"Verified index survives restart");
        var updated = manager.Install(await offline.DownloadAsync(cached,default,offline:true),cached.PackageUrl);
        Check(!updated.Enabled && updated.Manifest.Version=="1.1.0" && handler.Requests == requests,
            "Offline update preserves the opt-in activation policy and uses no network");
        manager.SetEnabled(id,true);
        manager.Quarantine(updated.Manifest.Identity);
        Check(!manager.CanRun(manager.List().Items.Single()),"The updated package is isolated after quarantine");
        Check(manager.SetEnabled(id,true) is { Enabled:true, Quarantined:false },"Explicit reactivation restores a version");
        manager.Uninstall(id);
        Check(manager.List().Items.Count==0,"The installed package is removed");

        var range = first.Manifest with { Compatibility = new("0.1.0","1.0.0") };
        Check(ExtensionPackage.IsCompatible(range,ExtensionHostVersion.Parse("1.0.0-rc.1")) &&
            !ExtensionPackage.IsCompatible(range,ExtensionHostVersion.Parse("1.0.0")),
            "The candidate is below the final release at an exclusive compatibility boundary");
        Check(new ExtensionCatalog(address+"other.json",ReaderVersion,client,cache).ReadCached() is null,
            "A different index address cannot reuse the first cache");
    }

    internal static void CommunityGui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();
        string document = Path.Combine(root,"demo");
        Tool("init","navigation","test.gui.navigation",document);
        string package = Path.Combine(root,"test.lwext");
        Tool("pack",Path.Combine(document,"manifest.json"),Path.Combine(document,"payload.json"),package);
        var manager = new ExtensionManagerService(Paths(root),ReaderVersion);
        manager.Install(manager.InspectLocal(package));
        var reader = new ReaderWindow(null,extensions:manager);
        reader.Show();
        try
        {
            var planet = Find<Button>(reader,"LanguageToggle");
            var puzzle = Find<Button>(reader,"PlatformExtensions");
            Dispatcher.UIThread.RunJobs();
            var planetX = planet.PointToScreen(new Point(planet.Bounds.Width / 2, 0)).X;
            var puzzleX = puzzle.PointToScreen(new Point(puzzle.Bounds.Width / 2, 0)).X;
            Check(puzzleX > planetX && puzzleX - planetX <= planet.Bounds.Width + 2,
                "The extensions button is directly to the right of the globe");
            puzzle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var dialog = reader.OwnedWindows.Single();
            var list = Find<ListBox>(dialog,"InstalledExtensions");
            Check(list.ItemCount==1,"One click opens the populated extension manager");
            Find<TextBox>(dialog,"ExtensionSearch").Text = "GÛI";
            Dispatcher.UIThread.RunJobs();
            Check(list.ItemCount==1,"Search ignores case and accents");
            Find<TextBox>(dialog,"ExtensionSearch").Text = "absent";
            Dispatcher.UIThread.RunJobs();
            Check(list.ItemCount==0,"Search filters the visible list");
            Find<TextBox>(dialog,"ExtensionSearch").Text = "";
            Find<RadioButton>(dialog,"ExtensionCategory_appearance_theme").IsChecked=true;
            Dispatcher.UIThread.RunJobs();
            Check(list.ItemCount==0,"The category filter hides nonmatching packages");
            Find<RadioButton>(dialog,"ExtensionCategory_reading_navigation").IsChecked=true;
            Dispatcher.UIThread.RunJobs();
            Check(list.ItemCount==1,"The category filter includes the matching package");
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally { reader.Close(); }

    }

    internal static void CommunityHttpsGui(string root)
    {
        string site = Environment.GetEnvironmentVariable("LECTEUR_WORD_COMMUNITY_FIXTURE") ??
            throw new InvalidOperationException("Select a local HTTPS fixture for this case.");
        string address = File.ReadAllText(Path.Combine(site,"url")).Trim();
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var manager = new ExtensionManagerService(Paths(root),ReaderVersion);
        var reader = new ReaderWindow(null,extensions:manager);
        reader.Show();
        try
        {
            Click(reader,"PlatformExtensions");
            var dialog = reader.OwnedWindows.Single();
            ChangeCatalog(dialog,false);
            Click(dialog,"CheckExtensionCatalog");
            Select(dialog,1);
            Until(() => Find<ListBox>(dialog,"DiscoverExtensions").ItemCount==1,"HTTPS index available in discovery");
            Click(dialog,"InstallCatalogExtension");
            Confirm(dialog);
            Until(() => manager.List().Items.Count==1,"HTTPS package installed");
            Select(dialog,0);
            Click(dialog,"EnableExtension");
            Until(() => manager.List().Items.Single().Enabled,"First release explicitly activated");

            File.Copy(Path.Combine(site,"index-update.json"),Path.Combine(site,"index.json"),overwrite:true);
            Click(dialog,"CheckExtensionCatalog");
            Select(dialog,2);
            Until(() => Find<ListBox>(dialog,"ExtensionUpdates").ItemCount==1,"HTTPS index exposes an update");
            Click(dialog,"DownloadCatalogExtension");
            Until(() => Directory.EnumerateFiles(PlatformPaths.ExtensionCatalogCache,"*.lwext",SearchOption.AllDirectories).Count()==2,
                "The update was cached before disconnecting");
            File.WriteAllText(Path.Combine(site,"stop"),"stop");
            Until(() => File.Exists(Path.Combine(site,"stopped")),"Local HTTPS server shut down");
            int requests = File.ReadAllLines(Path.Combine(site,"requests")).Length;
            ChangeCatalog(dialog,true);
            Select(dialog,2);
            Click(dialog,"UpdateCatalogExtension");
            Confirm(dialog);
            Until(() => manager.List().Items.Single() is { Manifest.Version: "1.1.0", Enabled:false },"Cached update installed offline");
            Check(File.ReadAllLines(Path.Combine(site,"requests")).Length==requests,"Offline update used no HTTP requests");
            Select(dialog,0);
            Click(dialog,"EnableExtension");
            Until(() => manager.List().Items.Single().Enabled,"Updated version explicitly activated");
            Click(dialog,"DisableExtension");
            Until(() => !manager.List().Items.Single().Enabled,"Updated version disabled");
            Click(dialog,"UninstallExtension");
            Until(() => dialog.OwnedWindows.Any(),"Uninstall confirmation opened");
            Click(dialog.OwnedWindows.Single(),"ConfirmUninstall");
            Until(() => manager.List().Items.Count==0,"Updated version uninstalled");
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            foreach (var dialog in reader.OwnedWindows.ToArray()) dialog.Close();
            reader.Close();
        }

        void ChangeCatalog(Window owner,bool offline)
        {
            Click(owner,"ExtensionCatalogSettings");
            var setup = owner.OwnedWindows.Single();
            Find<TextBox>(setup,"ExtensionCatalogUrl").Text = address+"index.json";
            Find<CheckBox>(setup,"ExtensionCatalogOffline").IsChecked = offline;
            Click(setup,"SaveExtensionCatalogSettings");
            Until(() => !owner.OwnedWindows.Any(),"Catalog preferences saved");
        }

        static void Confirm(Window owner)
        {
            Until(() => owner.OwnedWindows.Any(),"Package confirmation opened");
            Click(owner.OwnedWindows.Single(),"ConfirmExtension");
        }

        static void Select(Window window,int index)
        {
            Find<TabControl>(window,"ExtensionTabs").SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();
        }

        static void Click(Window window,string name)
        {
            Find<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        static void Until(Func<bool> condition,string message)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                Dispatcher.UIThread.RunJobs();
                if (condition()) return;
                Thread.Sleep(15);
            }
            throw new InvalidOperationException(message);
        }
    }

    static T Find<T>(Window window,string name) where T:Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name==name);

    static void Tool(params string[] arguments)
    {
        using var output = new StringWriter();
        Check(ExtensionTool.Run(arguments,output)==0,output.ToString());
    }

    sealed class CommunityFixture(string index, string packages) : HttpMessageHandler
    {
        internal string Index = index;
        internal bool Offline;
        internal bool Corrupt;
        internal int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Offline) throw new InvalidOperationException("Offline operation attempted a request");
            string filename = Path.GetFileName(request.RequestUri!.AbsolutePath);
            byte[] data = filename.EndsWith(".json",StringComparison.Ordinal) ? File.ReadAllBytes(Index)
                : Corrupt ? [0,1,2] : File.ReadAllBytes(Path.Combine(packages,filename));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }
}
