using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.Platform;

static partial class PlatformCases
{
    internal static ExtensionManagerService LocalLanguageManager(string root) => new(Paths(root), ReaderVersion);

    internal static void InstallLongLanguage(string root, ExtensionManagerService manager, string name, string command)
    {
        string key = "ui." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "Installer d’autres langues\nInstall more languages")))[..24];
        var translation = new TranslationContribution("de-DE", name, new() { [key] = command });
        var extension = manager.Install(manager.InspectLocal(CreatePackage(root, "test.language.layout", translation)));
        manager.SetEnabled(extension.Manifest.Id, true);
    }
}

static class LanguageCases
{
    internal static void CompactLayout(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();
        const string longName = "Deutsch · ausführliche gemeinschaftliche Fassung für die Benutzeroberfläche";
        const string longCommand = "Weitere von der Gemeinschaft gepflegte und geprüfte Übersetzungen installieren";
        var manager = PlatformCases.LocalLanguageManager(root);
        PlatformCases.InstallLongLanguage(root, manager, longName, longCommand);
        string input = Fixtures.Write(root, "interface.docx", Fixtures.Parts(Fixtures.Paragraph("Texte témoin")));
        var reader = new ReaderWindow(new ThemePreference(Path.Combine(root, "settings.json")), extensions:manager)
            { Width = 650, Height = 510 };
        reader.Show();
        try
        {
            AboutCases.Complete(reader.StartReaderAsync(input));
            var languageButton = Find<Button>(reader, "LanguageToggle");
            var open = reader.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Action_open_0");
            Check(languageButton.Focus(), "Le globe reçoit le focus avant F6.");
            reader.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
            reader.KeyReleaseQwerty(PhysicalKey.F6, RawInputModifiers.None);
            Check(open.IsFocused, "F6 passe de l’en-tête à la barre d’outils.");
            reader.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.Shift);
            reader.KeyReleaseQwerty(PhysicalKey.F6, RawInputModifiers.Shift);
            Check(languageButton.IsFocused, "Maj+F6 revient à l’en-tête.");
            string[] scripts = ["ja", "ko", "zh-CN", "ar"];
            foreach (var locale in ReaderLanguage.BuiltIn)
            {
                ThemeCases.ChooseLanguage(reader, locale.Id.Replace('-', '_'));
                Layout();
                var puzzle = Find<Button>(reader, "PlatformExtensions");
                var planetX = languageButton.PointToScreen(new Point(languageButton.Bounds.Width / 2, 0)).X;
                var puzzleX = puzzle.PointToScreen(new Point(puzzle.Bounds.Width / 2, 0)).X;
                Check(puzzleX > planetX && puzzleX - planetX <= languageButton.Bounds.Width + 2,
                    $"{locale.Id} · le puzzle doit rester immédiatement à droite de la planète.");
                Click(Find<Button>(reader, "OpenPublication"));
                var export = reader.ActivePublicationDialog!;
                export.Width = export.MinWidth;
                export.Height = export.MinHeight;
                Find<RadioButton>(export, "PublicationFormatEpub3").IsChecked = true;
                Find<Expander>(export, "PublicationAdvanced").IsExpanded = true;
                Layout();
                Check(export.FlowDirection == (locale.Culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight),
                    $"{locale.Id} · direction de la fenêtre d’export.");
                Check(Find<TextBox>(export, "PublicationDestination").FlowDirection == FlowDirection.LeftToRight &&
                    Find<TextBox>(export, "PublicationCoverName").FlowDirection == FlowDirection.LeftToRight,
                    $"{locale.Id} · l’ordre des composants du chemin reste de gauche à droite.");
                foreach (var control in export.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Text is { Length: > 0 }))
                    CheckText(control, locale.Id);
                if (scripts.Contains(locale.Id))
                {
                    var labels = export.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible &&
                        text.Text?.EnumerateRunes().Any(rune => rune.Value >= 0x600 && Rune.IsLetter(rune)) == true).ToArray();
                    Check(labels.Length > 0, $"{locale.Id} · libellés natifs réellement affichés.");
                    foreach (var label in labels)
                        foreach (var run in label.TextLayout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>())
                            Check(run.GlyphRun.GlyphInfos.All(glyph => glyph.GlyphIndex != 0), $"{locale.Id} · glyphe manquant dans {label.Text}.");
                }
                export.Close();
                AboutCases.Complete(WaitForExportClose(reader));
                Click(Find<Button>(reader, "PlatformExtensions"));
                var extensions = reader.OwnedWindows.Single();
                extensions.Width = Math.Max(extensions.MinWidth, 520);
                Layout();
                Check(Find<TextBox>(extensions, "LocalExtensionPath").FlowDirection == FlowDirection.LeftToRight,
                    $"{locale.Id} · le chemin du paquet conserve son ordre de lecture.");
                foreach (var label in extensions.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Text is { Length: > 0 }))
                    CheckText(label, locale.Id);
                extensions.Close();
                Layout();
                Console.WriteLine($"{locale.Id} · export et extensions en fenêtre compacte.");
            }
            ThemeCases.ChooseLanguage(reader, "test_language_layout");
            var globe = Find<Button>(reader, "LanguageToggle");
            var flyout = (Flyout)globe.Flyout!;
            flyout.ShowAt(globe);
            Layout();
            var popup = (Control)flyout.Content!;
            Find<TextBox>(popup, "LanguageSearch").Text = "gemeinschaftliche";
            Layout();
            CheckText(popup.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == longName), "nom communautaire");
            CheckText(popup.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == longCommand), "commande communautaire");
            flyout.Hide();
            Console.WriteLine("20 langues en fenêtres compactes · glyphes JA, KO, ZH-CN, AR · nom et commande communautaires longs.");
        }
        finally
        {
            foreach (var dialog in reader.OwnedWindows.ToArray()) dialog.Close();
            reader.Close();
        }

        static void CheckText(TextBlock text, string context) => Check(text.TextLayout.Width <= text.Bounds.Width + 2 &&
            text.TextLayout.Height <= text.Bounds.Height + 2, $"{context} · texte tronqué {text.Text} · {text.TextLayout.Width:F0} × {text.TextLayout.Height:F0} / {text.Bounds.Size}.");

        static async Task WaitForExportClose(ReaderWindow window)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (window.ActivePublicationDialog is not null) await Task.Delay(12, timeout.Token);
        }
    }

    internal static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();
        string[] locales = ["ar", "cs", "da", "de", "en", "es", "fi", "fr", "it", "ja", "ko", "nl", "pl", "pt-BR", "pt-PT", "ru", "sv", "tr", "uk", "zh-CN"];
        Check(ReaderLanguage.BuiltIn.Select(language => language.Id).Order(StringComparer.Ordinal).SequenceEqual(locales),
            "Les 20 langues livrées sont effectivement chargées.");
        foreach (var (locale, examples) in new (string, (long Number, string Category)[])[]
        {
            ("ar", [(0, "zero"), (1, "one"), (2, "two"), (3, "few"), (11, "many"), (100, "other")]),
            ("pl", [(1, "one"), (2, "few"), (12, "many"), (21, "many")]),
            ("uk", [(12, "many"), (21, "one"), (24, "few")]),
            ("cs", [(3, "few"), (5, "other")]),
            ("ja", [(1, "other")]),
            ("pt-BR", [(0, "one"), (1_000_000, "many")]),
            ("pt-PT", [(0, "other"), (1_000_000, "many")])
        })
        {
            var culture = CultureInfo.GetCultureInfo(locale);
            var messages = ReaderLanguage.BuiltIn.Single(language => language.Id == locale).Catalog!.Messages;
            foreach (var (number, category) in examples)
            {
                Check(ReaderWindow.CountCategory(culture, number) == category, $"Pluriel {locale} · {number}");
                Check(messages.TryGetValue("document.words." + category, out var text) && text.Contains("{0:N0}"),
                    $"Compteur traduit et paramètre conservé pour {locale} · {category}.");
            }
        }

        var previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");
        var preference = new ThemePreference(Path.Combine(root, "settings.json"));
        var manager = PlatformCases.LocalLanguageManager(root);
        var window = new ReaderWindow(preference, extensions:manager);
        window.Show();
        try
        {
            var globe = Find<Button>(window, "LanguageToggle");
            var flyout = (Flyout)globe.Flyout!;
            Layout();
            var center = globe.TranslatePoint(new Point(globe.Bounds.Width / 2, globe.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Layout();
            Check(flyout.IsOpen && window.OwnedWindows.Count == 0, "Un clic ouvre directement le menu ancré au globe.");
            var panel = (Control)flyout.Content!;
            var system = Find<Button>(panel, "Language_system");
            Check(window.Language == "fr-CA" && !string.IsNullOrWhiteSpace(AutomationProperties.GetItemStatus(system)),
                "Langue système active avec variante régionale.");
            Check(panel.GetVisualDescendants().OfType<Button>().Count(button => button.Name?.StartsWith("Language_") == true) == 21,
                "Chaque langue intégrée et la langue du système sont accessibles dans le menu.");
            Check(panel.GetVisualDescendants().OfType<ScrollViewer>().Any(), "La liste est défilante.");

            var search = Find<TextBox>(panel, "LanguageSearch");
            search.Text = "zzzz";
            Layout();
            Check(!panel.GetVisualDescendants().OfType<Button>().Any(button => button.Name?.StartsWith("Language_") == true),
                "Filtre sans résultat.");
            search.Text = "pt-BR";
            Layout();
            var brazil = Find<Button>(panel, "Language_pt_BR");
            Check(AutomationProperties.GetName(brazil) == "Português (Brasil)", "Nom natif affiché.");
            Click(brazil);
            Check(window.Language == "pt-BR" && !flyout.IsOpen && AutomationProperties.GetName(globe) == "Idioma da interface",
                "Bascule immédiate et fermeture du menu.");

            ThemeCases.ChooseLanguage(window, "ar");
            Check(window.Language == "ar" && window.FlowDirection == FlowDirection.RightToLeft &&
                AutomationProperties.GetName(globe) == "لغة الواجهة", "Arabe affiché de droite à gauche.");
            Click(Find<Button>(window, "ReaderPreferences"));
            var preferences = window.OwnedWindows.Single();
            Check(preferences.FlowDirection == FlowDirection.RightToLeft && preferences.Title == "التفضيلات", "Dialogue en arabe.");
            window.SetLanguage(true);
            Layout();
            Check(preferences.FlowDirection == FlowDirection.LeftToRight && preferences.Title == "Preferences", "Dialogue ouvert mis à jour.");
            preferences.Close();
            Layout();

            flyout.ShowAt(globe);
            Layout();
            Click(Find<Button>((Control)flyout.Content!, "InstallMoreLanguages"));
            var extensions = window.OwnedWindows.Single();
            Check(Find<TabControl>(extensions, "ExtensionTabs").SelectedIndex == 1 &&
                Find<RadioButton>(extensions, "ExtensionCategory_appearance_translation").IsChecked == true,
                "Lien direct vers la découverte des packs de langues.");
            extensions.Close();
            ThemeCases.ChooseLanguage(window, "pt_PT");
        }
        finally
        {
            window.Close();
            CultureInfo.CurrentUICulture = previousCulture;
        }
        var reopened = new ReaderWindow(preference, extensions:manager);
        Check(reopened.Language == "pt-PT" && reopened.FlowDirection == FlowDirection.LeftToRight, "Préférence régionale relue.");
        reopened.Close();
    }

    static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
    }

    static void Layout()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
