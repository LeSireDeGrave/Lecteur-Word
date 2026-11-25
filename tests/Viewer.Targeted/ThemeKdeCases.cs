using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.Platform;

static partial class PlatformCases
{
    internal sealed partial class PlatformKdeApp
    {
        static async Task ExerciseThemes(ReaderWindow reader, string file, string hash, ExtensionManagerService manager)
        {
            Check(reader.TryGetPlatformHandle()?.HandleDescriptor == "XID", "Fenêtre XWayland requise pour les captures.");
            reader.SetLanguage(false);
            await reader.StartReaderAsync(file);
            Check(reader.NativePageStatus == "Complete", "Le document synthétique est disponible.");

            var forest = new ThemeColors("#E5F4E9", "#214D3D");
            var community = new ThemeColors("#FAE8FA", "#611054");
            foreach (var (id, palette) in new[] { ("test.theme.foret", forest), ("test.theme.communaute", community) })
                manager.Install(manager.InspectLocal(CreatePackage(Root, id, new ThemeContribution(palette, palette))));
            var confirmationPackage = CreatePackage(Root, "test.theme.confirmation", new ThemeContribution(forest, forest));
            var systemVariant = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Light;
            var systemPalette = ThemePreference.Resolve(ReaderTheme.System, systemVariant) == ThemeVariant.Dark
                ? ReaderPalette.Night : ReaderPalette.Light;

            (string Name, int Choice, string? Extension, ReaderPalette Palette)[] themes =
            [
                ("systeme", 0, null, systemPalette),
                ("clair", 4, null, ReaderPalette.Light),
                ("sombre", 3, null, ReaderPalette.Night),
                ("chocolat", 1, null, ReaderPalette.Chocolate),
                ("bleu", 2, null, ReaderPalette.Blue),
                ("foret", 4, "test.theme.foret", ReaderPalette.FromExtension(forest)),
                ("communautaire", 4, "test.theme.communaute", ReaderPalette.FromExtension(community))
            ];

            foreach (var (name, action, sample) in new[]
            {
                ("export", "OpenPublication", "PublicationDestination"),
                ("extensions", "PlatformExtensions", "InstalledExtensions"),
                ("confirmation", "PlatformExtensions", "ConfirmExtension"),
                ("preferences", "ReaderPreferences", "Display_document"),
                ("aide", "PlatformHelp", ""),
                ("a-propos", "AboutLecteurWord", "AboutClose"),
                ("polices", "ManageHistoricalFonts", "")
            })
            {
                if (name == "polices") Click(Find<Button>(reader, "DocumentProperties"));
                var dialog = await Open(action, name == "confirmation");
                try
                {
                    foreach (var theme in themes)
                    {
                        await Apply(theme.Choice, theme.Extension);
                        VerifyWindow(dialog, theme.Palette);
                        if (sample.Length > 0) Check(Find<Control>(dialog, sample).IsEffectivelyVisible, $"{name} · contrôle visible");
                        await Capture(dialog, $"{theme.Name}-{name}");
                        Console.WriteLine($"{theme.Name} · {name} · fond {theme.Palette.Panel} · texte {theme.Palette.Ink}");
                        Close(dialog);
                        if (action == "OpenPublication")
                            await Until(() => reader.ActivePublicationDialog is null, "Le dialogue d’export termine sa fermeture.");
                        dialog = await Open(action, name == "confirmation");
                        VerifyWindow(dialog, theme.Palette);
                    }
                }
                finally { Close(dialog); }
            }

            int recoveryNumber = 0;
            var recovery = await OpenRecovery();
            foreach (var theme in themes)
            {
                await Apply(theme.Choice, theme.Extension);
                VerifyWindow(recovery.Dialog, theme.Palette);
                await Capture(recovery.Dialog, $"{theme.Name}-recuperation");
                await CloseRecovery(recovery);
                recovery = await OpenRecovery();
                VerifyWindow(recovery.Dialog, theme.Palette);

                VerifyWindow(reader, theme.Palette);
                Click(Find<Button>(reader, "DocumentProperties"));
                Check(Find<Control>(reader, "DocumentFonts").IsEffectivelyVisible, "Volet des propriétés visible.");
                await Capture(reader, $"{theme.Name}-lecteur");
                Click(ButtonByText(reader, "Diagnostics"));
                await Until(() => Find<Control>(reader, "DiagnosticTechnicalDetails").IsEffectivelyVisible, "Volet des diagnostics visible.");
                await Capture(reader, $"{theme.Name}-diagnostics");

                var language = Find<Button>(reader, "LanguageToggle");
                var flyout = (Flyout)language.Flyout!;
                flyout.ShowAt(language);
                var panel = (Control)flyout.Content!;
                await Until(() => panel.GetVisualAncestors().OfType<FlyoutPresenter>().Any(), "Menu des langues visible.");
                var presenter = panel.GetVisualAncestors().OfType<FlyoutPresenter>().First();
                Check(ColorOf(presenter.Background) == theme.Palette.Field, "Fond effectif du menu des langues.");
                var search = panel.GetVisualDescendants().OfType<TextBox>().Single();
                Check(ColorOf(search.Background) == theme.Palette.Field && ColorOf(search.Foreground) == theme.Palette.Ink,
                    "Couleurs effectives de la recherche du menu des langues.");
                await Capture(TopLevel.GetTopLevel(presenter)!, $"{theme.Name}-langues");
                flyout.Hide();

                var savedTip = ToolTip.GetTip(language);
                var tooltip = new ToolTip { Content = savedTip };
                ToolTip.SetTip(language, tooltip);
                ToolTip.SetIsOpen(language, true);
                await Until(() => TopLevel.GetTopLevel(tooltip) is not null, "Infobulle visible.");
                Check(ColorOf(tooltip.Background) == theme.Palette.Field && ColorOf(tooltip.Foreground) == theme.Palette.Ink,
                    "Couleurs effectives de l’infobulle.");
                ToolTip.SetIsOpen(language, false);
                ToolTip.SetTip(language, savedTip);

                reader.SetReadingView(ReadingView.Continuous);
                await Until(() => reader.GetVisualDescendants().OfType<Control>().Any(control => control.ContextMenu is not null),
                    "Une commande de passage est disponible dans le menu contextuel.");
                var reading=Find<ListBox>(reader,"SimplifiedReading");
                Check(ColorOf(reading.Background)==theme.Palette.Panel&&ColorOf(reading.Foreground)==theme.Palette.Ink,
                    "La lecture continue adopte la palette active en mode de lecture par défaut.");
                await Capture(reader,$"{theme.Name}-lecture-continue");
                var passage = reader.GetVisualDescendants().OfType<Control>().First(control => control.ContextMenu is not null);
                var menu = passage.ContextMenu!;
                menu.Open(passage);
                await Until(() => TopLevel.GetTopLevel(menu) is not null, "Menu contextuel visible.");
                Check(ColorOf(menu.Background) == theme.Palette.Field, $"Fond du menu contextuel · {ColorOf(menu.Background)}.");
                await Capture(TopLevel.GetTopLevel(menu)!, $"{theme.Name}-menu");
                menu.Close();
                reader.SetReadingView(ReadingView.SinglePage);
            }
            await CloseRecovery(recovery);

            reader.SetTheme(2);
            foreach (string locale in new[] { "ja", "ko", "zh-CN", "ar" })
            {
                var globe = Find<Button>(reader, "LanguageToggle");
                var flyout = (Flyout)globe.Flyout!;
                flyout.ShowAt(globe);
                var content = (Control)flyout.Content!;
                string option = "Language_" + locale.Replace('-', '_');
                await Until(() => content.GetVisualDescendants().Any(control => control is Button { Name: var name } && name == option),
                    $"{locale} est accessible dans la liste native.");
                Click(content.GetVisualDescendants().OfType<Button>().Single(button => button.Name == option));
                Check(reader.Language == locale, $"{locale} est affiché dans la fenêtre native.");
                var window = await Open("OpenPublication", confirmation: false);
                Check(window.FlowDirection == (locale == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight),
                    $"{locale} · direction de l’interface native.");
                Find<Expander>(window, "PublicationAdvanced").IsExpanded = true;
                await Capture(window, $"ecriture-{locale}-export");
                window.Close();
                await Until(() => reader.ActivePublicationDialog is null, "Le dialogue d’export termine sa fermeture.");
            }
            reader.SetLanguage(false);
            Check(Sha(file) == hash, "Le DOCX synthétique conserve son empreinte.");

            async Task<(ReaderWindow Reader, Window Dialog, Task Startup)> OpenRecovery()
            {
                var recoveryRoot = Path.Combine(Root, $"recovery-{++recoveryNumber}");
                var paths = Paths(recoveryRoot);
                SeedInterruption(paths, file);
                var recoveryReader = new ReaderWindow(recovery:new SessionRecoveryService(paths), preference:null,extensions:manager);
                recoveryReader.SetLanguage(false);
                recoveryReader.Show();
                var startup = recoveryReader.StartReaderAsync(null);
                return (recoveryReader, await Opened(recoveryReader), startup);
            }

            static async Task CloseRecovery((ReaderWindow Reader, Window Dialog, Task Startup) session)
            {
                session.Dialog.Close(false);
                try { await session.Startup; }
                finally { session.Reader.Close(); }
            }

            async Task<Window> Open(string action, bool confirmation)
            {
                Click(Find<Button>(reader, action));
                var owner = await Opened(reader);
                if (!confirmation) return owner;
                Find<TextBox>(owner, "LocalExtensionPath").Text = confirmationPackage;
                Click(Find<Button>(owner, "InstallExtensionFromPath"));
                return await Opened(owner);
            }

            void Close(Window dialog)
            {
                var owner = dialog.Owner;
                dialog.Close();
                if (owner is Window intermediate && !ReferenceEquals(intermediate, reader)) intermediate.Close();
            }

            async Task Apply(int choice, string? extension)
            {
                reader.SetTheme(choice);
                if (extension is not null)
                {
                    manager.SetEnabled(extension, true);
                    await reader.StartReaderAsync(null);
                }
                await Task.Delay(90);
            }
        }

        static void VerifyWindow(Window window, ReaderPalette palette)
        {
            Check(ColorOf(window.Background) == palette.Panel && ColorOf(window.Foreground) == palette.Ink,
                $"{window.Title} · couleurs effectives {ColorOf(window.Background)}/{ColorOf(window.Foreground)} · attendu {palette.Panel}/{palette.Ink}.");
            var label = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(block =>
                block.IsEffectivelyVisible && block.Foreground is ISolidColorBrush brush && brush.Color == palette.Ink);
            Check(label is not null, $"{window.Title} · couleur de texte héritée par le contenu.");
        }

        static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
