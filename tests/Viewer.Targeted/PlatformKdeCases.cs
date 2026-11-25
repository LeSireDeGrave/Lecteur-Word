using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.Platform;

static partial class PlatformCases
{
    internal static int Kde(string root, bool themesOnly = false)
    {
        string? directory = themesOnly ? root : Environment.GetEnvironmentVariable("LECTEUR_WORD_PLATFORM_QA");
        if (directory is null) throw new InvalidOperationException("Choisir le dossier de recette locale avec LECTEUR_WORD_PLATFORM_QA.");
        PlatformKdeApp.Root = root;
        PlatformKdeApp.Output = Path.GetFullPath(directory);
        PlatformKdeApp.ThemesOnly = themesOnly;
        return AppBuilder.Configure<PlatformKdeApp>().UsePlatformDetect().With(new X11PlatformOptions{WmClass="LecteurWordPlatformQa"})
            .WithInterFont().StartWithClassicDesktopLifetime([]);
    }

    internal sealed partial class PlatformKdeApp : Application
    {
        internal static string Root = null!;
        internal static string Output = null!;
        internal static bool ThemesOnly;
        public override void Initialize() => Styles.Add(ReaderApp.CreateTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var paths = Paths(Root);
                var file = CreateDocument(Root);
                string hash = Sha(file);
                if (!ThemesOnly) SeedInterruption(paths,file);
                var recovery = new SessionRecoveryService(paths);
                var manager = new ExtensionManagerService(paths,ReaderVersion);
                var window = new ReaderWindow(new ThemePreference(Path.Combine(paths.Config,"settings.json")),recovery,extensions:manager);
                desktop.MainWindow = window;
                window.Opened += async (_,_) =>
                {
                    int result = 0;
                    try
                    {
                        if (ThemesOnly) await ExerciseThemes(window,file,hash,manager);
                        else
                        {
                            await Exercise(window,file,hash,recovery,manager);
                            window.Close();
                            await ResumeCleanSession(paths,file,hash,manager);
                            Console.WriteLine("KDE X11: reprise, rapport, GitHub, installateur, thème, statistiques et quarantaine.");
                        }
                    }
                    catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
                    finally
                    {
                        foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
                        if (window.IsVisible) window.Close();
                        desktop.Shutdown(result);
                    }
                };
            }
            base.OnFrameworkInitializationCompleted();
        }

        static string CreateDocument(string path)
        {
            string body = """
                <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Recette de la plateforme</w:t></w:r></w:p>
                <w:p><w:r><w:t>Ce document synthétique sert aux tests de récupération et d’extensions.</w:t></w:r></w:p>
                <w:p><w:pPr><w:outlineLvl w:val="0"/><w:pageBreakBefore/></w:pPr><w:r><w:t>Deuxième page</w:t></w:r></w:p>
                <w:p><w:r><w:t>Aucune donnée personnelle.</w:t></w:r></w:p>
                <w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1080" w:bottom="1080" w:left="850" w:right="850"/></w:sectPr>
                """;
            return Fixtures.Write(path,"recette-plateforme.docx",Fixtures.Parts(body));
        }

        static void SeedInterruption(PlatformPaths paths, string document)
        {
            using var previous = new SessionRecoveryService(paths);
            previous.Checkpoint(SessionRecoveryService.DefaultReading() with
            {
                Document = document, Page = 2, View = SessionView.FacingPages, PagedZoom = .8, Elements = true
            },completedOperation:true);
            string lockPath = Path.Combine(paths.State,"session.json");
            Check(File.Exists(lockPath),"Le marqueur avant interruption manque.");
            // Dispose releases process ownership. The marker remains unclean.
        }

        static async Task Exercise(ReaderWindow window, string file, string hash, SessionRecoveryService recovery, ExtensionManagerService manager)
        {
            Check(window.TryGetPlatformHandle()?.HandleDescriptor == "XID","Avalonia doit fournir une fenêtre X11 réelle.");
            Check(manager.List().Items.Count == 0,"Registre initial vide");
            await Task.Delay(250);
            var startup = window.StartReaderAsync(null);
            await Task.Delay(100);
            if (startup.IsCompleted) await startup;
            var restore = await Opened(window);
            await Capture(restore,"01-recuperation");
            // Establish the XWayland input seat before the first action.
            await Xdo("key","Shift_L");
            await Task.Delay(850);
            await Interact(restore,ButtonByText(restore,"Consulter le rapport"));
            var report = await Opened(restore);
            string reportText = Find<TextBox>(report,"ReportPreview").Text ?? "";
            Check(reportText.Contains("Pile : indisponible") && !reportText.Contains(file),"Diagnostic d’arrêt brutal sans chemin privé");
            await Capture(report,"02-rapport");
            string? previous = await window.Clipboard!.TryGetTextAsync();
            try
            {
                await Interact(report,Find<Button>(report,"CopyIncident"));
                await Until(() => TextVisible(report,"Rapport copié."),"La copie du rapport se termine.");
                Check(await report.Clipboard!.TryGetTextAsync()==reportText,"Texte du rapport dans le presse-papiers natif.");
                await Interact(report,ButtonByText(report,"Préparer un signalement"));
                var draft = await Opened(report);
                await Interact(draft,Find<TextBox>(draft,"ReproductionSteps"));
                await Xdo("type","--clearmodifiers","Reproduction de test");
                await Until(() => Find<TextBox>(draft,"SupportPreview").Text?.Contains("Reproduction de test")==true,"Aperçu du signalement actualisé");
                Check(!Find<Button>(draft,"OpenGitHub").IsEnabled &&
                    Find<TextBox>(draft,"SupportPreview").Text?.Contains("Pile : indisponible")==true,
                    "Brouillon de l’incident et destination distante absente");
                await Interact(draft,Find<Button>(draft,"CopySupportDraft"));
                await Until(() => TextVisible(draft,"Brouillon copié."),"La copie du brouillon se termine.");
                Check((await draft.Clipboard!.TryGetTextAsync())?.Contains("Reproduction de test")==true,"Brouillon copié.");
                await Capture(draft,"03-signalement");
                await Interact(draft,ButtonByText(draft,"Fermer"));
            }
            finally
            {
                if (previous is null) await window.Clipboard!.ClearAsync();
                else await window.Clipboard!.SetTextAsync(previous);
                await window.Clipboard!.FlushAsync();
            }
            await Interact(report,ButtonByText(report,"Fermer"));
            await Interact(restore,Find<Button>(restore,"ResumeReading"));
            await startup.WaitAsync(TimeSpan.FromSeconds(25));
            Check(window.NativePageStatus=="Complete" && window.CurrentPage==2 && window.CurrentReadingView==ReadingView.FacingPages &&
                Math.Abs(window.Zoom-.8)<.001,"La double page, position et le zoom ont été restaurés.");

            await Interact(window,Find<Button>(window,"PlatformExtensions"));
            var dialog = await Opened(window);
            Check(Find<ListBox>(dialog,"InstalledExtensions").ItemCount==0,"Le gestionnaire fonctionne sans extension.");
            await Capture(dialog,"04-extensions-vide");
            await Install(dialog,Path.Combine(Output,"demo-theme.lwext"),"Forêt");
            await Interact(dialog,Find<Button>(dialog,"EnableExtension"));
            await Until(() => manager.List().Items.Single().Enabled,"Activation du thème");
            Check(UsesSurface(window,"#E5F4E9"),"Le thème peint la surface du lecteur.");
            await Capture(window,"05-theme-foret");
            await Interact(dialog,Find<Button>(dialog,"ConfigureExtension"));
            var settings = await Opened(dialog);
            await Interact(settings,Find<RadioButton>(settings,"ExtensionSetting1"));
            await Interact(settings,Find<Button>(settings,"ApplyExtensionSettings"));
            await Until(() => manager.List().Items.Single().Settings.AlternateTheme,"Palette alternative sauvegardée");
            Check(UsesSurface(window,"#E9F2FC"),"La variante alternative est appliquée.");
            await Interact(dialog,Find<Button>(dialog,"DisableExtension"));
            await Until(() => !manager.List().Items.Single().Enabled,"Désactivation du thème");
            Check(!UsesSurface(window,"#E9F2FC"),"La désactivation retire la surface de l’extension.");
            await Interact(dialog,Find<Button>(dialog,"UninstallExtension"));
            var uninstall = await Opened(dialog);
            await Interact(uninstall,Find<Button>(uninstall,"ConfirmUninstall"));
            await Until(() => manager.List().Items.Count==0,"Désinstallation du thème");

            await Install(dialog,Path.Combine(Output,"demo-reading.lwext"),"Repères de lecture");
            await Interact(dialog,Find<Button>(dialog,"EnableExtension"));
            await Until(() => manager.List().Items.Any(item => item.Enabled),"Activation de l’outil de lecture");
            await Capture(dialog,"06-gestionnaire");
            await Interact(dialog,Find<Button>(dialog,"RunReadingExtension"));
            var result = await Opened(dialog);
            Check(TextVisible(result,"Nombre de titres : 2,0"),"Statistique calculée en processus de lecture.");
            await Capture(result,"07-statistiques");
            await Interact(result,ButtonByText(result,"Fermer"));

            await Install(dialog,Path.Combine(Output,"demo-navigation.lwext"),"Navigation de lecture", drop:true);
            await Interact(dialog,Find<Button>(dialog,"EnableExtension"));
            await Until(() => manager.List().Items.Any(item => item.Package.Navigation is not null && item.Enabled),"Extension de navigation activée");

            string preferencesKey = "ui." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Préférences\nPreferences")))[..24];
            string translation = CreatePackage(Root,"test.native-language",
                new TranslationContribution("fr-CA","Français canadien",new() { [preferencesKey]="Réglages" }));
            await Install(dialog,translation,"test.native-language");
            await Interact(dialog,Find<Button>(dialog,"EnableExtension"));
            await Until(() => manager.List().Items.Any(item => item.Package.Translation is not null && item.Enabled),"Extension de traduction activée");

            string faulty = CreatePackage(Root,"test.nativefault",
                new ReadingContribution([new("Division",["1","0","/"])]));
            await Install(dialog,faulty,"test.nativefault");
            await Interact(dialog,Find<Button>(dialog,"EnableExtension"));
            await Until(() => manager.List().Items.Any(item => item.Manifest.Id=="test.nativefault" && item.Enabled),"Activation du témoin d’erreur");
            await Interact(dialog,Find<Button>(dialog,"RunReadingExtension"));
            var incident = await Opened(dialog);
            Check(recovery.LatestReport?.Extension == new ExtensionIdentity("test.nativefault","1.0.0") &&
                manager.List().Items.Any(item => item.Manifest.Id=="test.nativefault" && item.Quarantined && !item.Enabled),
                "L’incident désactive la version et préserve le lecteur.");
            await Capture(incident,"08-incident-extension");
            await Interact(incident,ButtonByText(incident,"Fermer"));
            await Interact(dialog,ButtonByText(dialog,"Fermer"));
            await ExerciseReadingPreferences(window);
            Check(window.CurrentPage==2 && Sha(file)==hash,"Le document et la position sont préservés.");
        }

        static async Task ExerciseReadingPreferences(ReaderWindow window)
        {
            await Interact(window,Find<Button>(window,"ExtensionNavigation_mark_position"));
            await Until(() => TextVisible(window,"NAVIGATION") && window.GetVisualDescendants().OfType<Button>()
                .Any(button => button.Content is string text && text.StartsWith("Page 2 ·")), "Repère ajouté à la position courante.");
            await ChooseLanguage("test_native_language","fr-CA");
            await Until(() => window.Language == "fr-CA","La traduction de l’extension est appliquée.");
            await Interact(window,Find<Button>(window,"ReaderPreferences"));
            var preferences = await Opened(window);
            Check(preferences.Title == "Réglages" && TextVisible(preferences,"Taille de l’interface"),
                "La traduction de l’extension conserve un repli pour les chaînes absentes.");
            await Interact(preferences,Find<Button>(preferences,"ClosePreferences"));
            await ChooseLanguage("en","English");
            await Until(() => window.Language == "en","Traduction à chaud.");
            await Interact(window,Find<Button>(window,"ReaderPreferences"));
            preferences = await Opened(window);
            Check(preferences.Title == "Preferences" &&
                AutomationProperties.GetName(Find<Button>(window,"ThemeMicrosoftBlue"))=="Microsoft Blue","Commandes accessibles traduites.");
            await Interact(preferences,Find<RadioButton>(preferences,"Display_high-contrast"));
            await Interact(preferences,Find<RadioButton>(preferences,"InterfaceScale115"));
            Check(window.Content is LayoutTransformControl && preferences.Content is LayoutTransformControl,"Mise à l’échelle des deux fenêtres.");
            await Capture(preferences,"09-accessibilite-en");
            await Interact(preferences,Find<RadioButton>(preferences,"InterfaceScale100"));
            await Interact(preferences,Find<RadioButton>(preferences,"Display_document"));
            await Interact(preferences,Find<Button>(preferences,"ClosePreferences"));
            await ChooseLanguage("fr","fr");
            var languageButton=Find<Button>(window,"LanguageToggle");
            string xid=window.TryGetPlatformHandle()!.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
            await Xdo("windowactivate","--sync",xid);
            Check(languageButton.Focus() && languageButton.IsFocused,"Le globe reçoit le focus avant le parcours des zones.");
            var open=Find<Button>(window,"Action_open_0");
            PressNativeF6(shift:false);
            await Until(() => open.IsFocused,"F6 passe de l’en-tête à la commande Ouvrir de la barre d’outils.");
            PressNativeF6(shift:true);
            await Until(() => languageButton.IsFocused,"Maj+F6 revient à l’en-tête.");
            Console.WriteLine("Globe, recherche, flèche, tabulation, validation, F6 et Maj+F6 vérifiés au clavier natif.");

            await Xdo("key","ctrl+shift+4");
            await Until(() => window.GetVisualDescendants().OfType<NumericUpDown>().Any(item => item.Name == "ExtensionPageNumber"),
                "Le raccourci d’extension ouvre la navigation directe.");
            Find<NumericUpDown>(window,"ExtensionPageNumber").Value = 1;
            await Interact(window,Find<Button>(window,"GoToExtensionPage"));
            await Until(() => window.CurrentPage == 1,"La commande ouvre la page 1.");
            await Interact(window,Find<Button>(window,"ExtensionNavigation_back"));
            await Until(() => window.CurrentPage == 2,"Retour à la position précédente.");

            async Task ChooseLanguage(string id, string query)
            {
                await Until(() => !window.OwnedWindows.Any(dialog=>dialog.IsVisible),"Le dialogue précédent rend le focus au lecteur.");
                var globe=Find<Button>(window,"LanguageToggle");
                var flyout=(Flyout)globe.Flyout!;
                await Interact(window,globe);
                await Until(()=>flyout.IsOpen,"Entrée sur la planète ouvre la liste.");
                var menu=(Control)flyout.Content!;
                var search=menu.GetVisualDescendants().OfType<TextBox>().Single(item=>item.Name=="LanguageSearch");
                await Until(()=>search.IsFocused,"La recherche reçoit le focus à l’ouverture.");
                await Xdo("type","--clearmodifiers",query);
                await Until(()=>search.Text==query,"Le clavier remplit le filtre des langues.");
                var rows=menu.GetVisualDescendants().OfType<Button>().Where(b=>b.Name?.StartsWith("Language_",StringComparison.Ordinal)==true).ToArray();
                int choice=Array.FindIndex(rows,row=>row.Name=="Language_"+id);
                Check(choice>=0,"Le filtre conserve la langue recherchée.");
                await Xdo("key","Down");
                await Until(()=>rows[0].IsFocused,"Flèche bas vers la première langue filtrée.");
                for(int position=1;position<=choice;position++)
                {
                    await Xdo("key","Tab");
                    await Until(()=>rows[position].IsFocused,"Tab rejoint la langue filtrée suivante.");
                }
                await Xdo("key","Return");
                await Until(()=>!flyout.IsOpen,"Entrée valide la langue et ferme la liste.");
            }
        }

        static async Task ResumeCleanSession(PlatformPaths paths,string file,string hash,ExtensionManagerService manager)
        {
            using var cleanRecovery = new SessionRecoveryService(paths);
            Check(!cleanRecovery.Start.Interrupted && cleanRecovery.Start.Reading is { Page: 2, Elements: true } snapshot &&
                snapshot.Document == file,"La fermeture normale a conservé la lecture et les volets.");
            var reader = new ReaderWindow(new ThemePreference(Path.Combine(paths.Config,"settings.json")),cleanRecovery,extensions:manager);
            reader.Show();
            try
            {
                await Task.Delay(250);
                await reader.StartReaderAsync(null);
                Check(reader.CurrentReading is null,"Le lecteur ouvre une fenêtre vide après fermeture normale.");
                await Interact(reader,Find<Button>(reader,"PlatformHelp"));
                var help = await Opened(reader);
                await Interact(help,ButtonByText(help,"Reprendre la dernière lecture"));
                await Until(() => reader.NativePageStatus=="Complete" && reader.CurrentPage==2,"Le menu reprend la session fermée normalement.");
                Check(reader.CurrentReadingView==ReadingView.FacingPages && Math.Abs(reader.Zoom-.8)<.001 && Sha(file)==hash,
                    "Mode de lecture, zoom et octets du DOCX vérifiés après fermeture normale.");
                await Interact(reader,Find<Button>(reader,"ExtensionNavigation_marks"));
                await Until(() => reader.GetVisualDescendants().OfType<Button>().Any(button =>
                    button.Content is string text && text.StartsWith("Page 2 ·")), "Le repère de lecture persiste après réouverture.");
                string rejected = Path.Combine(Root,"document.txt");
                await File.WriteAllTextAsync(rejected,"Fichier synthétique hors format.");
                var before = reader.CurrentReading;
                Check(await RaiseFileDrop(reader,rejected,DragDrop.DragOverEvent) == DragDropEffects.None,
                    "Le glisser-déposer écarte les fichiers hors DOCX.");
                await RaiseFileDrop(reader,rejected,DragDrop.DropEvent);
                Check(ReferenceEquals(before,reader.CurrentReading),"Un dépôt hors format préserve le document courant.");
                string dropped = Path.Combine(Root,"Document déposé.DOCX");
                File.Copy(file,dropped);
                Check(await RaiseFileDrop(reader,dropped,DragDrop.DragOverEvent) == DragDropEffects.Copy,
                    "Un DOCX local avec extension majuscule est accepté en copie.");
                await RaiseFileDrop(reader,dropped,DragDrop.DropEvent);
                await Until(() => !ReferenceEquals(before,reader.CurrentReading) && reader.Title!.StartsWith("Document déposé.DOCX",StringComparison.Ordinal)
                    && reader.NativePageStatus=="Complete", "Le dépôt ouvre le document dans le lecteur.");
                Check(Sha(dropped)==hash,"Le dépôt n’altère pas le DOCX.");
                Console.WriteLine("Dépôt DOCX et refus du témoin hors format vérifiés sur une fenêtre native.");
            }
            finally
            {
                foreach (var owned in reader.OwnedWindows.ToArray()) owned.Close();
                reader.Close();
            }
        }

        static async Task Install(Window manager, string path, string expected, bool drop = false)
        {
            Check(File.Exists(path),"Paquet de recette présent.");
            if (drop)
                await RaiseFileDrop(manager,path,DragDrop.DropEvent);
            else
            {
                var input = Find<TextBox>(manager,"LocalExtensionPath");
                await Interact(manager,input);
                input.Text = path;
                await Xdo("key","Return");
            }
            Window install;
            try { install = await Opened(manager); }
            catch (InvalidOperationException)
            {
                Console.Error.WriteLine("Textes du gestionnaire : " + string.Join(" · ",
                    manager.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)));
                throw;
            }
            Check(TextVisible(install,expected),"La fiche du paquet est visible avant installation.");
            await Interact(install,Find<Button>(install,"ConfirmExtension"));
            await Until(() => Find<ListBox>(manager,"InstalledExtensions").Items.Cast<object>().Any(row => row.ToString()==expected),
                "Paquet publié dans le gestionnaire.");
        }

        static bool UsesSurface(Window window,string hex) => window.GetVisualDescendants().OfType<Border>().Any(control =>
            control.Background is ISolidColorBrush brush && brush.Color == Color.Parse(hex));

        static string Sha(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        static bool TextVisible(Window window,string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(c => c.Text==text);
        static T Find<T>(Window window,string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().Single(c => c.Name==name);
        static Button ButtonByText(Window window,string text) =>
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string==text && b.IsEffectivelyVisible);

        static async Task<Window> Opened(Window owner)
        {
            await Until(() => owner.OwnedWindows.Any(dialog => dialog.IsVisible),"Une boîte de dialogue doit s’ouvrir.");
            return owner.OwnedWindows.Last(dialog => dialog.IsVisible);
        }

        static async Task Until(Func<bool> condition,string failure)
        {
            var time = Stopwatch.StartNew();
            while (!condition())
            {
                if (time.Elapsed > TimeSpan.FromSeconds(9)) throw new InvalidOperationException(failure);
                await Task.Delay(60);
            }
            await Task.Delay(160);
        }

        static async Task<DragDropEffects> RaiseFileDrop(Window window, string path,
            Avalonia.Interactivity.RoutedEvent<DragEventArgs> dragEvent)
        {
            var item = await window.StorageProvider.TryGetFileFromPathAsync(path);
            Check(item is not null,"Le stockage natif résout le fichier glissé.");
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(item!));
            var target = (Control)window.Content!;
            var args = new DragEventArgs(dragEvent, data, target, new Point(), KeyModifiers.None)
                { DragEffects = DragDropEffects.Copy };
            target.RaiseEvent(args);
            return args.DragEffects;
        }

        static async Task Interact(Window window, Control target)
        {
            await Task.Delay(180);
            Check(target.IsEffectivelyVisible,"Contrôle visible lors de l’interaction X11.");
            string xid = window.TryGetPlatformHandle()!.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
            await Xdo("windowactivate","--sync",xid);
            await Task.Delay(150);
            Check(target.Focus() && target.IsFocused, "Le contrôle doit être accessible au clavier.");
            Console.WriteLine($"Interaction KWin · {window.Title} · {target.Name ?? (target as Button)?.Content}");
            if (target is RadioButton) await Xdo("key","space");
            else if (target is Button) await Xdo("key","Return");
            else if (target is ComboBox) await Xdo("key","space");
        }

        static Task Xdo(params string[] words) => ProcessAsync("xdotool",words);

        static void PressNativeF6(bool shift)
        {
            // Sur ce clavier XWayland, xdotool ajoute Ctrl au symbole F6. XTEST accepte directement le code de la touche.
            const uint f6Symbol = 0xffc3;
            const uint leftShiftSymbol = 0xffe1;
            nint display = XOpenDisplay(0);
            Check(display != 0,"Ouverture du serveur XWayland pour XTEST.");
            try
            {
                uint f6 = XKeysymToKeycode(display,f6Symbol);
                uint leftShift = XKeysymToKeycode(display,leftShiftSymbol);
                Check(f6 != 0 && leftShift != 0,"Codes de touches F6 et Maj gauche dans le clavier XWayland.");
                int pressedShift = shift ? XTestFakeKeyEvent(display,leftShift,1,0) : 1;
                int pressedF6 = XTestFakeKeyEvent(display,f6,1,0);
                int releasedF6 = XTestFakeKeyEvent(display,f6,0,0);
                int releasedShift = shift ? XTestFakeKeyEvent(display,leftShift,0,0) : 1;
                XSync(display,0);
                Check(pressedShift != 0 && pressedF6 != 0 && releasedF6 != 0 && releasedShift != 0,
                    "XTEST envoie et relâche les touches F6 et Maj demandées.");
            }
            finally { XCloseDisplay(display); }
        }

        [DllImport("libX11.so.6")]
        static extern nint XOpenDisplay(nint displayName);
        [DllImport("libX11.so.6")]
        static extern byte XKeysymToKeycode(nint display, nuint keySymbol);
        [DllImport("libX11.so.6")]
        static extern int XSync(nint display, int discard);
        [DllImport("libX11.so.6")]
        static extern int XCloseDisplay(nint display);
        [DllImport("libXtst.so.6")]
        static extern int XTestFakeKeyEvent(nint display, uint keycode, int pressed, nuint delay);

        static async Task Capture(TopLevel window,string name)
        {
            await Task.Delay(250);
            string xid = "0x" + window.TryGetPlatformHandle()!.Handle.ToInt64().ToString("x",CultureInfo.InvariantCulture);
            await ProcessAsync("import",["-window",xid,Path.Combine(Output,name + ".png")]);
        }

        static async Task ProcessAsync(string executable,string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(process.ExitCode==0,executable + " a échoué.");
        }
    }
}
