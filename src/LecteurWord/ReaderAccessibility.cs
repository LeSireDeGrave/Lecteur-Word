using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    sealed record AccessibilityPreference(string Display = "document", int InterfacePercent = 100);
    AccessibilityPreference accessibility = new();
    AccessibilityContribution? extensionAccessibility;
    string DisplayMode => extensionAccessibility?.Display ?? accessibility.Display;
    int InterfacePercent => extensionAccessibility?.InterfacePercent ?? accessibility.InterfacePercent;
    bool HighContrast => DisplayMode == "high-contrast";
    IBrush ReadingSurface => DisplayMode switch
    {
        "dark" => ReaderPalette.ReadingNightPaper,
        "warm" => Brush("#F8E8C5"),
        "high-contrast" => Brushes.White,
        _ => appearance.Brush(ReaderAppearance.Surface)
    };
    IBrush TextDisplayInk => DisplayMode switch
    {
        "dark" => ReaderPalette.ReadingNightInk,
        "warm" or "high-contrast" => Brushes.Black,
        _ => appearance.Brush(ReaderAppearance.Ink)
    };
    string? AccessibilityPath => themePreference is {} preference ? Path.Combine(preference.DirectoryPath, "accessibility.json") : null;
    void LoadAccessibility()
    {
        if (AccessibilityPath is not {} path) return;
        try
        {
            if (PlatformFiles.ReadOptional<AccessibilityPreference>(path) is {} stored &&
                stored.Display is "document" or "warm" or "dark" or "high-contrast" &&
                stored.InterfacePercent is 100 or 115 or 130)
                accessibility = stored;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { RecordIncident(error, SessionStage.Startup); }
    }

    void SetAccessibility(AccessibilityPreference preference)
    {
        accessibility = preference;
        ApplyDisplayPreferences();
        if (AccessibilityPath is not {} path) return;
        try { PlatformFiles.Write(path, preference); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RecordIncident(error, SessionStage.Interface);
            status.Text = T("Préférences d’accessibilité non enregistrées.", "Accessibility preferences could not be saved.");
        }
    }

    void ApplyDisplayPreferences()
    {
        double offset = PageOffsetInPoints();
        string? anchor = IsSimplifiedReading ? ContinuousAnchor() : null;
        ResetPageDeck();
        ApplyInterfaceScale();
        ApplyTheme();
        RefreshReadingAppearance();
        UpdateUi();
        if (anchor is not null && documentIndex?.BodyIndices.TryGetValue(anchor, out int index) == true)
            MoveReadingBlock(index, alignAtTop: true);
        else if (CurrentPage > 0) RevealPage(page, offset);
    }

    void ApplyInterfaceScale()
    {
        ScaleWindow(this);
        foreach (var window in readerDialogs) ScaleWindow(window);
    }

    void ScaleWindow(Window window)
    {
        var existing = window.Content as LayoutTransformControl;
        if (InterfacePercent == 100)
        {
            if (existing?.Name == "InterfaceMagnifier")
            {
                var child = existing.Child;
                existing.Child = null;
                window.Content = child;
            }
            return;
        }
        if (existing?.Name != "InterfaceMagnifier")
        {
            if (window.Content is not Control child) return;
            window.Content = null;
            existing = new LayoutTransformControl { Name = "InterfaceMagnifier", Child = child };
            window.Content = existing;
        }
        double scale = InterfacePercent / 100d;
        existing.LayoutTransform = new ScaleTransform(scale, scale);
    }

    void FocusWorkspaceRegion(bool backwards)
    {
        Control[] regions = [header, toolbarBorder, sidebar, stage, documentPanel, footer];
        var targets = regions.Where(region => region.IsVisible)
            .Select(region => (Region: region, Target: region.GetLogicalDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Focusable && control.IsEffectivelyEnabled && control.IsEffectivelyVisible)))
            .Where(entry => entry.Target is not null).ToArray();
        if (targets.Length == 0) return;
        var focused = FocusManager?.GetFocusedElement() as Control;
        int index = Array.FindIndex(targets, entry => focused == entry.Region ||
            focused?.GetLogicalAncestors().Contains(entry.Region) == true);
        int destination = index < 0 ? (backwards ? targets.Length - 1 : 0)
            : (index + targets.Length + (backwards ? -1 : 1)) % targets.Length;
        targets[destination].Target!.Focus(NavigationMethod.Tab);
    }

    async Task ShowPreferencesAsync()
    {
        var dialog = PlatformDialog(T("Préférences", "Preferences"), 570, 480);
        var panel = DialogStack(dialog);
        panel.Children.Add(DialogText(T("Taille de l’interface", "Interface size")));
        var magnification = DialogActions(panel);
        foreach (int percent in new[] { 100, 115, 130 })
            Option(magnification, (percent / 100d).ToString("P0", uiCulture), "InterfaceScale" + percent,
                "InterfaceScale", accessibility.InterfacePercent == percent,
                () => SetAccessibility(accessibility with { InterfacePercent = percent }));
        panel.Children.Add(DialogText(T("Couleurs de lecture", "Reading colors")));
        var colors = DialogActions(panel);
        foreach (var (id, fr, en) in new[] { ("document", "Document", "Document"), ("warm", "Chaud", "Warm"),
            ("dark", "Inverse", "Inverted"), ("high-contrast", "Contraste élevé", "High contrast") })
            Option(colors, T(fr,en), "Display_" + id, "Display", accessibility.Display == id,
                () => SetAccessibility(accessibility with { Display = id }));
        if (extensionAccessibility is not null)
        {
            panel.Children.Add(DialogText(T("Un profil d’accessibilité actif applique ses réglages d’affichage.",
                "An active accessibility profile applies its display settings.")));
            magnification.IsEnabled = colors.IsEnabled = false;
        }
        panel.Children.Add(DialogText(T("F6 / Maj+F6 : changer de zone · Tab : parcourir les commandes · Ctrl++ / Ctrl+- : zoom du document.",
            "F6 / Shift+F6: switch regions · Tab: move through controls · Ctrl++ / Ctrl+-: document zoom.")));
        ActionButton(DialogActions(panel), T("Fermer", "Close"), dialog.Close, "ClosePreferences");
        await dialog.ShowDialog(this);

        static void Option(Panel panel, string label, string name, string group, bool selected, Action action)
        {
            var choice = new RadioButton { Content = label, Name = name, GroupName = group, IsChecked = selected };
            AutomationProperties.SetName(choice, label);
            choice.IsCheckedChanged += (_, _) => { if (choice.IsChecked == true) action(); };
            panel.Children.Add(choice);
        }
    }
}
