using System.Globalization;
using System.Text.RegularExpressions;

namespace LecteurWord.Platform;

internal sealed record TranslationContribution(string Culture, string Name, Dictionary<string, string> Messages)
{
    internal static TranslationContribution Read(ReadOnlySpan<byte> payload)
    {
        var translation = ExtensionPackage.Decode<TranslationContribution>(payload);
        ExtensionPackage.Require(ExtensionPackage.TextField(translation.Culture, 40)
            && ExtensionPackage.TextField(translation.Name, 80)
            && translation.Messages is { Count: > 0 and <= 2400 }
            && translation.Messages.All(entry =>
                Regex.IsMatch(entry.Key, @"\A(?:ui\.[a-f0-9]{24}|diagnostic\.[a-z0-9_]{1,96}|[a-z][a-z0-9.-]{1,79})\z",
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) &&
                ValidMessage(entry.Value)), "Traductions d’extension invalides.");
        try { _ = CultureInfo.GetCultureInfo(translation.Culture); }
        catch (CultureNotFoundException error) { throw new InvalidDataException("Langue d’extension invalide.", error); }
        return translation;
    }

    static bool ValidMessage(string? value) => value is { Length: > 0 and <= 1600 } && !string.IsNullOrWhiteSpace(value)
        && !value.Any(c => char.IsControl(c) && c is not ('\n' or '\t') ||
            char.GetUnicodeCategory(c) == UnicodeCategory.Format);
}

internal sealed record NavigationCommand(string Action, string French, string English, string[] Places, int? Shortcut);
internal sealed record NavigationContribution(NavigationCommand[] Commands)
{
    internal static NavigationContribution Read(ReadOnlySpan<byte> payload)
    {
        var navigation = ExtensionPackage.Decode<NavigationContribution>(payload);
        ExtensionPackage.Require(navigation.Commands is { Length: > 0 and <= 10 } && navigation.Commands.All(command =>
            command is not null && command.Action is "headings" or "sections" or "bookmarks" or "pages" or "marks"
                or "mark-position" or "history" or "back" or "forward" &&
            ExtensionPackage.TextField(command.French, 75) && ExtensionPackage.TextField(command.English, 75) &&
            command.Places is { Length: > 0 and <= 2 } && command.Places.All(place => place is "sidebar" or "toolbar") &&
            command.Places.Distinct(StringComparer.Ordinal).Count() == command.Places.Length &&
            command.Shortcut is null or (>= 1 and <= 9)) &&
            navigation.Commands.Select(c => c.Action).Distinct(StringComparer.Ordinal).Count() == navigation.Commands.Length &&
            navigation.Commands.Where(c => c.Shortcut.HasValue).GroupBy(c => c.Shortcut).All(g => g.Count() == 1),
            "Commandes de navigation invalides.");
        return navigation;
    }
}

internal sealed record AccessibilityContribution(string Display, int InterfacePercent)
{
    internal static AccessibilityContribution Read(ReadOnlySpan<byte> payload)
    {
        var accessibility = ExtensionPackage.Decode<AccessibilityContribution>(payload);
        ExtensionPackage.Require(accessibility.Display is "document" or "high-contrast" or "warm" or "dark" &&
            accessibility.InterfacePercent is 100 or 115 or 130, "Préférences d’accessibilité invalides.");
        return accessibility;
    }
}
