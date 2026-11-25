using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using LecteurWord.Platform;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    const int NavigationLimit = 50;
    sealed record ReadingPosition(int Page, string? Anchor, double Offset, bool Continuous);
    sealed record StoredReadingMark(ReadingPosition Position, DateTimeOffset Created);
    readonly StackPanel navigationExtensionsToolbar = new() { Orientation = Orientation.Horizontal, IsVisible = false };
    readonly List<ReadingPosition> previousPositions = [];
    readonly List<ReadingPosition> nextPositions = [];
    readonly List<StoredReadingMark> readingMarks = [];
    IReadOnlyList<(InstalledExtension Owner, NavigationCommand Command)> navigationCommands = [];
    string? navigationDocument;
    bool restoringPosition;
    string? ReadingMarksPath => themePreference is {} settings && navigationDocument is {} hash
        ? Path.Combine(settings.DirectoryPath, "reading-marks", hash + ".json") : null;

    void ReloadNavigation(IReadOnlyList<InstalledExtension> active)
    {
        navigationCommands = active.Where(item => item.Package.Navigation is not null)
            .OrderBy(item => item.Manifest.Id, StringComparer.Ordinal)
            .SelectMany(item => item.Package.Navigation!.Commands.Select(command => (item, command))).ToArray();
        RefreshNavigationToolbar();
    }

    void RefreshNavigationToolbar()
    {
        navigationExtensionsToolbar.Children.Clear();
        foreach (var entry in navigationCommands.Where(entry => entry.Command.Places.Contains("toolbar", StringComparer.Ordinal)))
            navigationExtensionsToolbar.Children.Add(NavigationCommandButton(entry));
        navigationExtensionsToolbar.IsVisible = navigationExtensionsToolbar.Children.Count > 0;
    }

    Button NavigationCommandButton((InstalledExtension Owner, NavigationCommand Command) entry)
    {
        string label = T(entry.Command.French, entry.Command.English);
        string tip = entry.Command.Shortcut is {} number ? label + T(" (Ctrl+Maj+", " (Ctrl+Shift+") + number + ")" : label;
        var button = new Button
        {
            Name = "ExtensionNavigation_" + entry.Command.Action.Replace('-', '_'),
            Content = label,
            IsEnabled = inspection is not null,
            Padding = new Avalonia.Thickness(6, 3)
        };
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => ExecuteNavigation(entry);
        return button;
    }

    bool ReadNavigationShortcut(KeyEventArgs args)
    {
        if (args.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift) || args.Key is < Key.D1 or > Key.D9)
            return false;
        if (inspection is null) return false;
        int shortcut = args.Key - Key.D0;
        foreach (var entry in navigationCommands)
        {
            if (entry.Command.Shortcut != shortcut) continue;
            ExecuteNavigation(entry);
            return true;
        }
        return false;
    }

    void ExecuteNavigation((InstalledExtension Owner, NavigationCommand Command) entry)
    {
        var identity = entry.Owner.Manifest.Identity;
        BeginExtension(SessionStage.RunExtension, identity);
        try
        {
            EnsureNavigationDocument();
            switch (entry.Command.Action)
            {
                case "headings": ShowPanel("outline"); break;
                case "sections": ShowPanel("section-index"); break;
                case "bookmarks": ShowPanel("bookmarks"); break;
                case "pages": ShowPanel("page-index"); break;
                case "marks": ShowPanel("reading-marks"); break;
                case "history": ShowPanel("navigation-history"); break;
                case "mark-position": AddReadingMark(); break;
                case "back": MoveThroughHistory(back: true); break;
                case "forward": MoveThroughHistory(back: false); break;
            }
        }
        catch (Exception error)
        {
            RecordIncident(error, SessionStage.RunExtension, identity);
            QuarantineExtension(identity);
            ReloadPlatformExtensions();
            status.Text = T("Extension de navigation désactivée après un incident.", "Navigation extension disabled after an incident.");
        }
        finally { EndExtension(); }
    }

    void EnsureNavigationDocument()
    {
        string? hash = inspection?.Manifest.SourceSha256;
        if (navigationDocument == hash) return;
        navigationDocument = hash;
        foreach (var button in navigationExtensionsToolbar.Children) button.IsEnabled = hash is not null;
        readingMarks.Clear();
        previousPositions.Clear();
        nextPositions.Clear();
        if (ReadingMarksPath is not {} path) return;
        try
        {
            if (PlatformFiles.ReadOptional<StoredReadingMark[]>(path) is {} marks)
                readingMarks.AddRange(marks.Where(mark => mark?.Position is { Page: >= 0, Offset: >= 0 } position &&
                    double.IsFinite(position.Offset) && position.Anchor is null or { Length: <= 512 }).Take(NavigationLimit));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { RecordIncident(error, SessionStage.Interface); }
    }

    ReadingPosition? CapturePosition() => readable?.Available != true && CurrentPage == 0 ? null
        : new(CurrentPage, IsSimplifiedReading ? ContinuousAnchor() : null,
            IsSimplifiedReading ? 0 : PageOffsetInPoints(), IsSimplifiedReading);

    void RememberNavigation(ReadingPosition? before)
    {
        if (restoringPosition || before is null || before == CapturePosition()) return;
        Push(previousPositions, before);
        nextPositions.Clear();
    }

    static void Push(List<ReadingPosition> list, ReadingPosition position)
    {
        if (list.Count > 0 && list[^1] == position) return;
        list.Add(position);
        if (list.Count > NavigationLimit) list.RemoveAt(0);
    }

    void MoveThroughHistory(bool back)
    {
        var source = back ? previousPositions : nextPositions;
        if (source.Count == 0 || CapturePosition() is not {} current) return;
        var destination = source[^1];
        source.RemoveAt(source.Count - 1);
        if (RestorePosition(destination)) Push(back ? nextPositions : previousPositions, current);
        if (navMode == "navigation-history") FillPanel();
    }

    bool RestorePosition(ReadingPosition position)
    {
        restoringPosition = true;
        try
        {
            if (position.Continuous)
            {
                SetReadingView(ReadingView.Continuous);
                if (!IsSimplifiedReading) return false;
                if (position.Anchor is {} anchor) return NavigateToBlock(anchor, alignReadingStart: true);
                ResetReadingPosition();
                return true;
            }
            if (simplified) SetReadingView(ReadingView.SinglePage);
            if (position.Page <= 0 || !ShowsCalculatedPages || composition!.Pages.Count < position.Page) return false;
            GoToPage(position.Page - 1);
            RevealPage(page, position.Offset);
            return true;
        }
        finally { restoringPosition = false; }
    }

    void AddReadingMark()
    {
        if (CapturePosition() is not {} position) return;
        if (readingMarks.Any(mark => mark.Position == position)) return;
        readingMarks.Insert(0, new(position, DateTimeOffset.Now));
        if (readingMarks.Count > NavigationLimit) readingMarks.RemoveAt(readingMarks.Count - 1);
        SaveReadingMarks();
        ShowPanel("reading-marks");
    }

    void SaveReadingMarks()
    {
        if (ReadingMarksPath is not {} path) return;
        try { PlatformFiles.Write(path, readingMarks); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RecordIncident(error, SessionStage.Interface);
            status.Text = T("Repères conservés pour la session en cours.", "Reading marks kept for the current session.");
        }
    }

    bool FillNavigationExtensionPanel()
    {
        switch (navMode)
        {
            case "section-index":
                if (documentIndex?.Semantic is {} semantic)
                    foreach (var section in semantic.Sections.Take(250))
                    {
                        if (section.StartBlock >= section.EndBlockExclusive) continue;
                        string anchor = semantic.BodyBlocks[section.StartBlock];
                        ActionButton(navItems, T("Section ", "Section ") + section.Number.ToString(uiCulture),
                            () => NavigateToBlock(anchor, alignReadingStart: true));
                    }
                break;
            case "page-index":
                if (ShowsCalculatedPages)
                {
                    var pageInput = new NumericUpDown { Name = "ExtensionPageNumber", Minimum = 1,
                        Maximum = composition!.Pages.Count, Value = page + 1, Increment = 1, FormatString = "0" };
                    AutomationProperties.SetName(pageInput, T("Numéro de page", "Page number"));
                    navItems.Children.Add(pageInput);
                    ActionButton(navItems, T("Aller à la page", "Go to page"), () =>
                    {
                        if (pageInput.Value is not {} number) return;
                        if (simplified) SetReadingView(ReadingView.SinglePage);
                        GoToPage((int)number - 1);
                    }, "GoToExtensionPage");
                }
                break;
            case "reading-marks":
                foreach (var mark in readingMarks.ToArray())
                {
                    var row = DialogActions(navItems);
                    ActionButton(row, PositionLabel(mark.Position) + " · " + mark.Created.ToLocalTime().ToString("g", uiCulture), () =>
                    {
                        var before = CapturePosition();
                        if (RestorePosition(mark.Position)) RememberNavigation(before);
                    });
                    ActionButton(row, T("Supprimer", "Remove"), () =>
                    {
                        readingMarks.Remove(mark);
                        SaveReadingMarks();
                        FillPanel();
                    });
                }
                break;
            case "navigation-history":
                var actions = DialogActions(navItems);
                var back = ActionButton(actions, T("Retour", "Back"), () => MoveThroughHistory(true), "NavigationHistoryBack");
                var forward = ActionButton(actions, T("Avant", "Forward"), () => MoveThroughHistory(false), "NavigationHistoryForward");
                back.IsEnabled = previousPositions.Count > 0;
                forward.IsEnabled = nextPositions.Count > 0;
                foreach (var position in previousPositions.AsEnumerable().Reverse())
                    ActionButton(navItems, PositionLabel(position), () =>
                    {
                        var before = CapturePosition();
                        if (RestorePosition(position)) RememberNavigation(before);
                        FillPanel();
                    });
                break;
            default: return false;
        }
        if (navItems.Children.Count == 0)
            navItems.Children.Add(DialogText(T("Aucune position disponible ici.", "No positions available here.")));
        return true;
    }

    string PositionLabel(ReadingPosition position)
    {
        if (!position.Continuous) return T("Page ", "Page ") + position.Page.ToString(uiCulture);
        if (position.Anchor is {} anchor && documentIndex?.BodyIndices.TryGetValue(anchor, out int index) == true &&
            readable is {} read)
        {
            string excerpt = SearchExcerpt(BlockText(read.Presentation.BodyBlocks[index]));
            if (!string.IsNullOrWhiteSpace(excerpt)) return excerpt;
        }
        return T("Début de la lecture", "Start of reading");
    }
}
