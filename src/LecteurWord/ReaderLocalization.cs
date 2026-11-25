using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    // Key pairs are independent of the current culture and of the order of windows opened.
    readonly Dictionary<(string French, string English), string> messageKeys = [];
    readonly Dictionary<string, (string French, string English)> displayedMessages = new(StringComparer.Ordinal);
    string languageChoice = "system";
    bool hasLanguagePreference;
    CultureInfo uiCulture = CultureInfo.GetCultureInfo("fr");
    TranslationContribution? activeTranslation;
    IReadOnlyList<InstalledExtension> translations = [];
    string? LanguagePath => themePreference is {} preference ? Path.Combine(preference.DirectoryPath, "language.json") : null;

    string Translate(string french, string englishText)
    {
        if (!messageKeys.TryGetValue((french, englishText), out string? key))
        {
            key = "ui." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(french + "\n" + englishText)))[..24];
            if (messageKeys.Count < 8192) messageKeys[(french, englishText)] = key;
        }
        string value = activeTranslation?.Messages.GetValueOrDefault(key) ?? (english ? englishText : french);
        if (displayedMessages.Count < 16384) displayedMessages.TryAdd(value, (french, englishText));
        return value;
    }

    static readonly Regex FormatTokens = new(@"\{[0-9](?::[NPF][0-3])?\}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    static bool SameFormat(string source, string candidate) =>
        !FormatTokens.Replace(candidate, "").Any(character => character is '{' or '}') &&
        FormatTokens.Matches(source).Select(match => match.Value).Order(StringComparer.Ordinal)
            .SequenceEqual(FormatTokens.Matches(candidate).Select(match => match.Value).Order(StringComparer.Ordinal));

    string LocalFormat(string id, string french, string englishText, params object[] args)
    {
        string fallback = english ? englishText : french;
        var message = activeTranslation?.Messages.GetValueOrDefault(id);
        return string.Format(uiCulture, message is not null && SameFormat(fallback, message) ? message : fallback, args);
    }

    string LocalCount(string id, long value, string frenchOne, string frenchOther, string englishOne, string englishOther)
    {
        string category = CountCategory(uiCulture, value);
        string fallbackFr = value is 0 or 1 ? frenchOne : frenchOther;
        string fallbackEn = value == 1 ? englishOne : englishOther;
        if (activeTranslation?.Messages.ContainsKey(id + "." + category) != true && category != "one")
            category = "other";
        return LocalFormat(id + "." + category, fallbackFr, fallbackEn, value);
    }

    void LoadLanguage()
    {
        if (LanguagePath is {} path)
        {
            try
            {
                var selected = PlatformFiles.ReadOptional<string>(path);
                if (selected == "system" || ReaderLanguage.BuiltIn.Any(item => item.Id == selected) || ExtensionPackage.IsId(selected))
                {
                    languageChoice = selected!;
                    hasLanguagePreference = true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { RecordIncident(error, SessionStage.Startup); }
        }
        ApplyLanguage(refresh: false);
    }

    public void SetLanguage(bool useEnglish) => SelectLanguage(useEnglish ? "en" : "fr");

    void SelectLanguage(string selected)
    {
        languageChoice = selected;
        hasLanguagePreference = true;
        ApplyLanguage(refresh: true);
        if (LanguagePath is not {} path) return;
        try { PlatformFiles.Write(path, selected); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RecordIncident(error, SessionStage.Interface);
            status.Text = T("Préférence de langue non enregistrée.", "Language preference could not be saved.");
        }
    }

    void ApplyLanguage(bool refresh)
    {
        var previousMessages = refresh ? new Dictionary<string, (string French, string English)>(displayedMessages, StringComparer.Ordinal) : null;
        displayedMessages.Clear();
        var system = CultureInfo.CurrentUICulture;
        var selected = AvailableLanguages().FirstOrDefault(item => item.Id == languageChoice);
        var language = selected ?? ReaderLanguage.SystemLanguage(system);
        activeTranslation = language.Catalog;
        uiCulture = selected is null && language.Culture.TwoLetterISOLanguageName == system.TwoLetterISOLanguageName
            ? system : language.Culture;
        english = uiCulture.TwoLetterISOLanguageName != "fr";
        FlowDirection = uiCulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (!refresh) return;
        foreach (var (button, french, englishText) in labels)
        {
            ToolTip.SetTip(button, T(french, englishText));
            AutomationProperties.SetName(button, T(french, englishText));
        }
        status.Text = Replace(status.Text);
        foreach (var window in readerDialogs.ToArray()) RefreshWindowLabels(window);
        RefreshNavigationToolbar();
        NameWorkspaceRegions();
        UpdateThemeSelection();
        RefreshReadingAppearance();
        RefreshElements();
        UpdateUi();
        RefreshPageLabels();
        FillPanel();

        void RefreshWindowLabels(Window window)
        {
            window.FlowDirection = FlowDirection;
            window.Title = Replace(window.Title);
            if (window is AboutWindow about) about.RefreshBuildInformation();
            foreach (var control in window.GetLogicalDescendants().OfType<Control>())
            {
                if (control is TextBlock { Text: {} text } block) block.Text = Replace(text);
                if (control is ContentControl { Content: string content } item) item.Content = Replace(content);
                if (control is HeaderedContentControl { Header: string header } headed) headed.Header = Replace(header);
                if (control is TextBox { PlaceholderText: {} placeholder } input) input.PlaceholderText = Replace(placeholder);
                if (control is NumericUpDown number) number.NumberFormat = uiCulture.NumberFormat;
                if (ToolTip.GetTip(control) is string tip) ToolTip.SetTip(control, Replace(tip));
                if (AutomationProperties.GetName(control) is {} name) AutomationProperties.SetName(control, Replace(name));
            }
        }

        string? Replace(string? current) => current is not null && previousMessages!.TryGetValue(current, out var pair)
            ? T(pair.French, pair.English) : current;
    }

    string ExtensionError(string message)
    {
        if (!ExtensionErrorTranslations.TryGetValue(message, out var englishText))
            return message.StartsWith("Extension incompatible avec Lecteur Word", StringComparison.Ordinal)
                ? T("Cette version est incompatible.", "This version is incompatible.") : message;
        return T(message, englishText);
    }

    static readonly Dictionary<string, string> ExtensionErrorTranslations = new(StringComparer.Ordinal)
    {
        ["Paquet trop volumineux."] = "Package exceeds the size limit.",
        ["Taille du paquet invalide."] = "Invalid package size.",
        ["Le paquet doit contenir manifest.json et payload.json."] = "The package must contain manifest.json and payload.json.",
        ["Intégrité du contenu du paquet invalide."] = "Package content failed its integrity check.",
        ["JSON du paquet invalide."] = "Invalid package JSON.",
        ["Objet JSON absent."] = "Missing JSON object.",
        ["Version attendue au format majeur.mineur.correctif."] = "Expected a major.minor.patch version.",
        ["Identité ou fiche de l’extension invalide."] = "Invalid extension identity or manifest.",
        ["Intervalle de compatibilité invalide."] = "Invalid compatibility range.",
        ["Provenance ou empreinte absente."] = "Missing provenance or checksum.",
        ["Fonctionnalités ou permissions invalides."] = "Invalid features or permissions.",
        ["Fonctionnalité ou permission inconnue pour cette version d’API."] = "Unknown feature or permission for this API version.",
        ["Dépendances d’extension invalides."] = "Invalid extension dependencies.",
        ["Traductions d’extension invalides."] = "Invalid extension translations.",
        ["Langue d’extension invalide."] = "Invalid extension language.",
        ["Commandes de navigation invalides."] = "Invalid navigation commands.",
        ["Préférences d’accessibilité invalides."] = "Invalid accessibility preferences.",
        ["Une version égale ou plus récente est déjà installée."] = "This version or a newer version is already installed.",
        ["Nombre maximal d’extensions installées atteint."] = "The installed extension limit has been reached.",
        ["Extension incompatible."] = "Incompatible extension.",
        ["Dépendances absentes, incompatibles ou désactivées."] = "Dependencies are missing, incompatible, or disabled.",
        ["Désactivez d’abord le thème d’extension déjà actif."] = "Disable the current extension theme first.",
        ["Désactivez d’abord le profil d’accessibilité déjà actif."] = "Disable the active accessibility profile first.",
        ["Raccourci d’extension déjà attribué."] = "The shortcut is already assigned to an extension.",
        ["Index du catalogue invalide."] = "Invalid catalog index.",
        ["Catalogue local invalide."] = "Invalid cached catalog.",
        ["Ce paquet est indisponible hors connexion."] = "This package is unavailable offline.",
        ["Version ou taille du catalogue incompatible."] = "Unsupported catalog version or size.",
        ["Entrée du catalogue invalide."] = "Invalid catalog entry.",
        ["Version dupliquée dans le catalogue."] = "Duplicate version in the catalog.",
        ["Adresse du paquet invalide."] = "Invalid package address.",
        ["Empreinte du paquet différente du catalogue."] = "Package checksum differs from the catalog.",
        ["Le manifeste du paquet diffère de la fiche du catalogue."] = "The package manifest differs from the catalog entry.",
        ["Ressource du catalogue trop volumineuse."] = "Catalog resource exceeds the size limit.",
        ["Le service de catalogue n’a pas répondu dans le délai prévu."] = "The catalog service timed out.",
        ["La ressource du catalogue est inaccessible."] = "The catalog resource is unavailable.",
        ["Journal de session indisponible. Vérifiez les droits d’accès ou fermez l’autre fenêtre de Lecteur Word."] =
            "Session journal unavailable. Check permissions or close the other Lecteur Word window.",
        ["Version de Lecteur Word indisponible pour vérifier les extensions."] =
            "The Lecteur Word version is unavailable for checking extensions.",
        ["Journal de session illisible. Il a été conservé pour examen."] = "The unreadable session journal was retained for review.",
        ["Le dernier rapport d’incident est illisible. Le fichier est conservé."] = "The latest incident report is unreadable and was retained."
    };
}
