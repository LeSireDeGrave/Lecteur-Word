using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LecteurWord.Platform;

namespace LecteurWord;

internal sealed record ReaderLanguage(string Id, CultureInfo Culture, string NativeName, TranslationContribution? Catalog)
{
    internal static IReadOnlyList<ReaderLanguage> BuiltIn { get; } = Load();

    static IReadOnlyList<ReaderLanguage> Load()
    {
        List<ReaderLanguage> languages =
        [
            new("fr", CultureInfo.GetCultureInfo("fr"), "Français", null),
            new("en", CultureInfo.GetCultureInfo("en"), "English", null)
        ];
        var assembly = typeof(ReaderLanguage).Assembly;
        const string prefix = "LecteurWord.Languages.";
        foreach (string file in assembly.GetManifestResourceNames().Where(name =>
                     name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(file)!;
            using var data = new MemoryStream();
            stream.CopyTo(data);
            var catalog = TranslationContribution.Read(data.ToArray());
            var culture = CultureInfo.GetCultureInfo(catalog.Culture);
            languages.Add(new(culture.Name, culture, catalog.Name, catalog));
        }
        return languages.AsReadOnly();
    }

    internal static ReaderLanguage SystemLanguage(CultureInfo system) =>
        BuiltIn.FirstOrDefault(item => item.Culture.Equals(system)) ??
        (system.TwoLetterISOLanguageName switch
        {
            "zh" when CultureInfo.GetCultureInfo("zh-Hans").Equals(system) || system.Name is "zh-SG" or "zh" =>
                BuiltIn.FirstOrDefault(item => item.Id == "zh-CN"),
            "zh" => null,
            "pt" => BuiltIn.FirstOrDefault(item => item.Id == "pt-PT"),
            _ => BuiltIn.FirstOrDefault(item => item.Culture.TwoLetterISOLanguageName == system.TwoLetterISOLanguageName)
        }) ?? BuiltIn[0];
}

public sealed partial class ReaderWindow
{
    IEnumerable<ReaderLanguage> AvailableLanguages() => ReaderLanguage.BuiltIn.Concat(translations.Select(item =>
    {
        var catalog = item.Package.Translation!;
        return new ReaderLanguage(item.Manifest.Id, CultureInfo.GetCultureInfo(catalog.Culture), catalog.Name, catalog);
    }));

    Button BuildLanguageButton()
    {
        var anchor = IconButton("globe", "Langue de l’interface", "Interface language", () => Task.CompletedTask);
        anchor.Name = "LanguageToggle";
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        anchor.Flyout = flyout;
        flyout.Opening += (_, _) => BuildLanguageMenu(flyout);
        flyout.Opened += (_, _) =>
        {
            if (flyout.Content is Panel panel && panel.Children[0] is TextBox search)
                Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Input);
        };
        return anchor;
    }

    void BuildLanguageMenu(Flyout flyout)
    {
        var panel = new StackPanel { Name = "ReaderLanguageMenu", Width = 300, Spacing = 8, FlowDirection = FlowDirection };
        var search = new TextBox { Name = "LanguageSearch", MaxLength = 80,
            PlaceholderText = T("Rechercher une langue", "Search languages") };
        AutomationProperties.SetName(search, search.PlaceholderText);
        var options = new StackPanel { Spacing = 2 };
        var scroll = new ScrollViewer { Content = options, MaxHeight = 280,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        string installLabel = T("Installer d’autres langues", "Install more languages");
        var install = new Button { Name = "InstallMoreLanguages", Content = DialogText(installLabel),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsEnabled = extensions is not null };
        AutomationProperties.SetName(install, installLabel);
        install.Click += async (_, _) =>
        {
            flyout.Hide();
            await ShowExtensionsAsync(ExtensionPackage.TranslationFeature);
        };
        panel.Children.Add(search);
        panel.Children.Add(scroll);
        panel.Children.Add(install);
        var installed = AvailableLanguages().OrderBy(item => item.NativeName, StringComparer.Create(uiCulture, true)).ToArray();
        var system = ReaderLanguage.SystemLanguage(CultureInfo.CurrentUICulture);
        var selected = installed.Any(item => item.Id == languageChoice) ? languageChoice : "system";
        string systemLabel = T("Langue du système", "System language");
        search.TextChanged += (_, _) => Refresh();
        search.KeyDown += (_, args) =>
        {
            if (args.Key is Key.Down or Key.Up)
            {
                var choices = options.Children.OfType<Button>();
                var next = args.Key == Key.Down ? choices.FirstOrDefault() : choices.LastOrDefault();
                args.Handled = next?.Focus(NavigationMethod.Directional) == true;
            }
        };
        flyout.Content = panel;
        Refresh();

        void Refresh()
        {
            string query = search.Text?.Trim() ?? "";
            var comparison = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            options.Children.Clear();
            if (Matches(systemLabel) || Matches(system.NativeName)) Add("system", systemLabel + " · " + system.NativeName);
            foreach (var language in installed)
                if (Matches(language.NativeName) || Matches(language.Culture.Name)) Add(language.Id, language.NativeName, language.Culture);
            if (options.Children.Count == 0)
                options.Children.Add(DialogText(T("Aucune langue trouvée", "No languages found")));

            bool Matches(string text) => uiCulture.CompareInfo.IndexOf(text, query, comparison) >= 0;
        }

        void Add(string id, string label, CultureInfo? culture = null)
        {
            bool active = id == selected;
            // The language name has its own text direction inside the surrounding interface direction.
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            if (culture is not null)
                text.FlowDirection = culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            var row = new Grid { ColumnDefinitions = new("24,*"), Children = { new TextBlock { Text = active ? "✓" : "" }, text } };
            Grid.SetColumn(text, 1);
            var option = new Button { Name = "Language_" + id.Replace('.', '_').Replace('-', '_'), Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Classes = { "reader-flat" } };
            AutomationProperties.SetName(option, label);
            AutomationProperties.SetItemStatus(option, active ? T("Sélectionné", "Selected") : "");
            UpdateToggle(option, active);
            option.Click += (_, _) => { flyout.Hide(); SelectLanguage(id); };
            options.Children.Add(option);
        }
    }

    internal static string CountCategory(CultureInfo culture, long value)
    {
        ulong n = value >= 0 ? (ulong)value : (ulong)(-(value + 1)) + 1;
        var language = culture.TwoLetterISOLanguageName;
        return language switch
        {
            "ar" when n == 0 => "zero",
            "ar" when n == 2 => "two",
            "ar" when n % 100 is >= 3 and <= 10 => "few",
            "ar" when n % 100 is >= 11 and <= 99 => "many",
            "ru" or "uk" when n % 10 == 1 && n % 100 != 11 => "one",
            "ru" or "uk" or "pl" when n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14) => "few",
            "ru" or "uk" => "many",
            "pl" when n != 1 => "many",
            "cs" when n is >= 2 and <= 4 => "few",
            "ja" or "ko" or "zh" => "other",
            "es" or "it" or "fr" or "pt" when n > 0 && n % 1_000_000 == 0 => "many",
            "fr" when n == 0 => "one",
            "pt" when n == 0 && culture.Name is "pt" or "pt-BR" => "one",
            _ when n == 1 => "one",
            _ => "other"
        };
    }
}
