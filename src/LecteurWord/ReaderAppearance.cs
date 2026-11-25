using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using LecteurWord.Platform;

namespace LecteurWord;

internal sealed class ReaderAppearance
{
    internal const string Header = "Reader.Header";
    internal const string HeaderInk = "Reader.HeaderInk";
    internal const string HeaderMuted = "Reader.HeaderMuted";
    internal const string Toolbar = "Reader.Toolbar";
    internal const string Footer = "Reader.Footer";
    internal const string Surface = "Reader.Surface";
    internal const string Stage = "Reader.Stage";
    internal const string Ink = "Reader.Ink";
    internal const string Secondary = "Reader.Secondary";
    internal const string Line = "Reader.Line";
    internal const string Selected = "Reader.Selected";
    internal const string Accent = "Reader.Accent";
    internal const string Field = "Reader.Field";

    static readonly ConditionalWeakTable<Application, ReaderAppearance> Instances = new();
    readonly Application application;
    readonly IPlatformSettings? platform;
    readonly ResourceDictionary resources = new();
    ThemeVariant? communityVariant;
    ThemeColors? communityColors;
    bool highContrast;
    bool configured;
    internal ReaderTheme Choice { get; private set; }
    internal bool CommunityActive => communityColors is not null;
    internal event Action? Changed;

    internal static ReaderAppearance Current => Instances.GetValue(Application.Current
        ?? throw new InvalidOperationException("Lecteur Word requires an initialized Avalonia application."),
        static app => new ReaderAppearance(app));

    ReaderAppearance(Application application)
    {
        this.application = application;
        platform = application.PlatformSettings;
        resources.ThemeDictionaries[ThemeVariant.Light] = PaletteResources(ReaderPalette.Light);
        resources.ThemeDictionaries[ThemeVariant.Dark] = PaletteResources(ReaderPalette.Night);
        resources.ThemeDictionaries[ReaderPalette.ChocolateVariant] = PaletteResources(ReaderPalette.Chocolate);
        resources.ThemeDictionaries[ReaderPalette.BlueVariant] = PaletteResources(ReaderPalette.Blue);
        resources.ThemeDictionaries[ReaderPalette.ContrastVariant] = PaletteResources(ReaderPalette.Contrast);
        application.Resources.MergedDictionaries.Add(resources);
        AddStyles(application.Styles);
        if (platform is not null) platform.ColorValuesChanged += OnSystemColorsChanged;
    }

    internal void Configure(ThemePreference? preference, bool contrast)
    {
        if (configured) return;
        configured = true;
        Set(preference?.Load() ?? ReaderTheme.System, null, contrast);
    }

    internal void Set(ReaderTheme choice, ThemeColors? community, bool contrast)
    {
        Choice = choice;
        highContrast = contrast;
        if (community != communityColors)
        {
            var previous = communityVariant;
            communityVariant = null;
            communityColors = community;
            if (community is not null)
            {
                var palette = ReaderPalette.FromExtension(community);
                var basis = palette.Ink == Colors.White ? ThemeVariant.Dark : ThemeVariant.Light;
                communityVariant = new ThemeVariant(new object(), basis);
                resources.ThemeDictionaries[communityVariant] = PaletteResources(palette);
            }
            Apply();
            if (previous is not null) resources.ThemeDictionaries.Remove(previous);
        }
        else Apply();
    }

    void Apply(PlatformThemeVariant? system = null)
    {
        application.RequestedThemeVariant = highContrast ? ReaderPalette.ContrastVariant : communityVariant
            ?? ThemePreference.Resolve(Choice, system ?? platform?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Light);
        Changed?.Invoke();
    }

    void OnSystemColorsChanged(object? sender, PlatformColorValues values)
    {
        void Update()
        {
            if (Choice == ReaderTheme.System && !CommunityActive && !highContrast) Apply(values.ThemeVariant);
        }
        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    internal static void Use(StyledElement element, AvaloniaProperty<IBrush?> property, string key) =>
        element.Bind(property, element.GetResourceObservable(key));

    internal IBrush Brush(string key) =>
        application.TryGetResource(key, application.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush : throw new InvalidOperationException($"Unresolved reader theme resource: {key}.");

    static void AddStyles(Styles styles)
    {
        styles.Add(new Style(s => s.Is<Window>())
        {
            Setters = { new Setter(Window.BackgroundProperty, new DynamicResourceExtension(Surface)),
                new Setter(Window.ForegroundProperty, new DynamicResourceExtension(Ink)) }
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("reader-flat"))
        {
            Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent), new Setter(Button.BorderThicknessProperty, new Thickness(0)) }
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("reader-flat").Class("active"))
        {
            Setters = { new Setter(Button.BackgroundProperty, new DynamicResourceExtension(Selected)) }
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("reader-header"))
        {
            Setters = { new Setter(Button.ForegroundProperty, new DynamicResourceExtension(HeaderInk)) }
        });
        HeaderState("", "Reader.Transparent", HeaderInk);
        HeaderState(":pointerover", "Reader.HeaderHover", HeaderInk);
        HeaderState(":pressed", "Reader.HeaderPressed", HeaderInk);
        HeaderState(":disabled", "Reader.Transparent", HeaderMuted);

        void HeaderState(string state, string surface, string ink) =>
            styles.Add(new Style(s => (state.Length == 0 ? s.OfType<Button>().Class("reader-header")
                : s.OfType<Button>().Class("reader-header").Class(state)).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension(surface)),
                    new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension(ink)) }
            });
    }

    static ResourceDictionary PaletteResources(ReaderPalette p)
    {
        var r = new ResourceDictionary();
        Color hover = ReaderPalette.Blend(p.Field, p.Accent, .11);
        Color pressed = ReaderPalette.Blend(p.Field, p.Accent, .25);
        Color disabled = ReaderPalette.Blend(p.Panel, p.Line, .12);
        Color headerHover = ReaderPalette.Blend(p.Header, p.HeaderInk, .12);
        Color headerPressed = ReaderPalette.Blend(p.Header, p.HeaderInk, .24);
        Color accentInk = ReaderPalette.ContrastingInk(p.Accent);
        Color focus = ReaderPalette.ContrastRatio(p.Accent, p.Toolbar) >= 3 ? p.Accent : ReaderPalette.ContrastingInk(p.Toolbar);
        Color focusOutline = ReaderPalette.ContrastingInk(focus);
        Color accentHover = ReaderPalette.Blend(p.Accent, accentInk, .09);
        Color accentPressed = ReaderPalette.Blend(p.Accent, accentInk, .20);

        void Paint(Color color, params string[] keys)
        {
            var brush = ReaderPalette.Brush(color);
            foreach (string key in keys) r[key] = brush;
        }

        Paint(p.Header, Header);
        Paint(p.HeaderInk, HeaderInk);
        Paint(p.HeaderMuted, HeaderMuted);
        Paint(p.Toolbar, Toolbar, "SystemControlPageBackgroundChromeLowBrush", "SystemControlPageBackgroundChromeMediumLowBrush");
        Paint(p.Footer, Footer);
        Paint(p.Panel, Surface, "SystemControlPageBackgroundAltHighBrush", "SystemControlPageBackgroundAltMediumBrush",
            "SystemControlTransientBackgroundBrush", "ScrollBarTrackFill", "ScrollBarTrackFillPointerOver", "ScrollBarButtonBackground");
        Paint(p.Stage, Stage);
        Paint(p.Ink, Ink, "SystemControlForegroundBaseHighBrush", "SystemControlForegroundBaseMediumHighBrush", "SystemControlPageTextBaseHighBrush",
            "TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver",
            "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed",
            "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundSelected",
            "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundPressed", "ComboBoxItemForegroundSelectedPressed",
            "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed",
            "MenuFlyoutSubItemChevron", "MenuFlyoutSubItemChevronPointerOver", "MenuFlyoutSubItemChevronPressed",
            "MenuFlyoutSubItemChevronSubMenuOpened", "ToolTipForeground", "ScrollBarButtonArrowForegroundPointerOver",
            "ScrollBarButtonArrowForegroundPressed", "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed");
        Paint(p.Secondary, Secondary, "SystemControlForegroundBaseMediumBrush", "SystemControlDescriptionTextForegroundBrush",
            "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused", "TextControlPlaceholderForegroundPointerOver",
            "ComboBoxPlaceHolderForeground", "ComboBoxDropDownGlyphForeground", "MenuFlyoutItemKeyboardAcceleratorTextForeground",
            "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed",
            "ScrollBarForeground", "ScrollBarButtonArrowForeground");
        Paint(p.Line, Line, "SystemControlTransientBorderBrush", "ToolTipBorderBrush", "MenuFlyoutPresenterBorderBrush",
            "ComboBoxDropDownBorderBrush", "ComboBoxBackgroundBorderBrushUnfocused", "ComboBoxBorderBrushDisabled",
            "TextControlBorderBrushDisabled", "ScrollBarBorderBrush", "ScrollBarTrackStrokePointerOver", "ScrollBarThumbFillDisabled");
        Paint(p.Field, Field, "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver",
            "ComboBoxBackground", "ComboBoxBackgroundUnfocused", "ComboBoxDropDownBackground", "ComboBoxItemBackground",
            "FlyoutPresenterBackground", "MenuFlyoutPresenterBackground", "MenuFlyoutItemBackground", "ToolTipBackground");
        Paint(p.Line, "TextControlBorderBrush", "ComboBoxBorderBrush", "ScrollBarThumbFillPointerOver");
        Paint(focus, "TextControlBorderBrushFocused", "TextControlBorderBrushPointerOver", "ComboBoxBackgroundBorderBrushFocused",
            "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed", "SystemControlFocusVisualPrimaryBrush");
        Paint(focusOutline, "SystemControlFocusVisualSecondaryBrush");
        Paint(p.Accent, Accent, "SystemControlForegroundAccentBrush", "SystemControlBackgroundAccentBrush",
            "SystemControlHighlightAccentBrush", "SystemControlHyperlinkTextBrush", "TabItemHeaderSelectedPipeFill",
            "ScrollBarThumbFillPressed");
        Paint(p.Selected, Selected, "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush",
            "SystemControlHighlightAltListAccentLowBrush", "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver");
        Paint(hover, "SystemControlHighlightListLowBrush", "ComboBoxBackgroundPointerOver", "ComboBoxItemBackgroundPointerOver",
            "MenuFlyoutItemBackgroundPointerOver", "ScrollBarButtonBackgroundPointerOver");
        Paint(pressed, "SystemControlHighlightListMediumBrush", "SystemControlHighlightListAccentHighBrush", "ComboBoxBackgroundPressed",
            "ComboBoxItemBackgroundPressed", "ComboBoxItemBackgroundSelectedPressed", "MenuFlyoutItemBackgroundPressed", "ScrollBarButtonBackgroundPressed");
        Paint(disabled, "TextControlBackgroundDisabled", "ComboBoxBackgroundDisabled", "ComboBoxItemBackgroundDisabled",
            "ComboBoxItemBackgroundSelectedDisabled", "MenuFlyoutItemBackgroundDisabled");
        Paint(p.Secondary, "TextControlForegroundDisabled", "TextControlPlaceholderForegroundDisabled", "ComboBoxForegroundDisabled",
            "ComboBoxItemForegroundDisabled", "ComboBoxItemForegroundSelectedDisabled", "MenuFlyoutItemForegroundDisabled",
            "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "MenuFlyoutSubItemChevronDisabled",
            "ComboBoxDropDownGlyphForegroundDisabled", "ScrollBarButtonArrowForegroundDisabled");

        foreach (string control in new[] { "Button", "ToggleButton", "RadioButton" })
        {
            Paint(p.Field, control + "Background");
            Paint(hover, control + "BackgroundPointerOver");
            Paint(pressed, control + "BackgroundPressed");
            Paint(disabled, control + "BackgroundDisabled");
            Paint(p.Ink, control + "Foreground", control + "ForegroundPointerOver", control + "ForegroundPressed");
            Paint(p.Secondary, control + "ForegroundDisabled");
            Paint(p.Line, control + "BorderBrush", control + "BorderBrushDisabled");
            Paint(focus, control + "BorderBrushPointerOver", control + "BorderBrushPressed");
        }
        Paint(p.Accent, "AccentButtonBackground", "AccentButtonBorderBrush");
        Paint(accentHover, "AccentButtonBackgroundPointerOver", "AccentButtonBorderBrushPointerOver");
        Paint(accentPressed, "AccentButtonBackgroundPressed", "AccentButtonBorderBrushPressed");
        Paint(accentInk, "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed");
        Paint(disabled, "AccentButtonBackgroundDisabled");
        Paint(p.Line, "AccentButtonBorderBrushDisabled");
        Paint(p.Secondary, "AccentButtonForegroundDisabled");

        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            bool unavailable = state == "Disabled";
            Color surface = state switch { "PointerOver" => hover, "Pressed" => pressed, "Disabled" => disabled, _ => p.Field };
            Paint(surface, "ExpanderHeaderBackground" + state, "ExpanderChevronBackground" + state);
            Paint(p.Line, "ExpanderHeaderBorderBrush" + state, "ExpanderChevronBorderBrush" + state);
            Paint(unavailable ? p.Secondary : p.Ink, "ExpanderHeaderForeground" + state, "ExpanderChevronForeground" + state);
            foreach (string value in new[] { "Unchecked", "Checked", "Indeterminate" })
            {
                Color fill = unavailable || value == "Unchecked" ? surface : p.Accent;
                Color glyph = unavailable ? p.Secondary : value == "Unchecked" ? p.Ink : accentInk;
                Paint(fill, "CheckBoxBackground" + value + state, "CheckBoxCheckBackgroundFill" + value + state);
                Paint(p.Line, "CheckBoxBorderBrush" + value + state, "CheckBoxCheckBackgroundStroke" + value + state);
                Paint(glyph, "CheckBoxCheckGlyphForeground" + value + state);
                Paint(unavailable ? p.Secondary : p.Ink, "CheckBoxForeground" + value + state);
            }
        }
        Paint(p.Panel, "ExpanderContentBackground");
        Paint(p.Line, "ExpanderContentBorderBrush");
        foreach (string state in new[] { "Checked", "Indeterminate" })
        {
            Paint(p.Selected, "ToggleButtonBackground" + state, "ToggleButtonBackground" + state + "PointerOver");
            Paint(pressed, "ToggleButtonBackground" + state + "Pressed");
            Paint(disabled, "ToggleButtonBackground" + state + "Disabled");
            Paint(p.Ink, "ToggleButtonForeground" + state, "ToggleButtonForeground" + state + "PointerOver", "ToggleButtonForeground" + state + "Pressed");
            Paint(p.Secondary, "ToggleButtonForeground" + state + "Disabled");
            Paint(focus, "ToggleButtonBorderBrush" + state, "ToggleButtonBorderBrush" + state + "PointerOver",
                "ToggleButtonBorderBrush" + state + "Pressed");
            Paint(p.Line, "ToggleButtonBorderBrush" + state + "Disabled");
        }
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            Paint(state == "Disabled" ? disabled : p.Field, "RadioButtonOuterEllipseFill" + state);
            Paint(state is "PointerOver" or "Pressed" ? focus : p.Line, "RadioButtonOuterEllipseStroke" + state);
            Paint(state == "Disabled" ? p.Line : p.Accent, "RadioButtonOuterEllipseCheckedFill" + state, "RadioButtonOuterEllipseCheckedStroke" + state);
            Paint(accentInk, "RadioButtonCheckGlyphFill" + state, "RadioButtonCheckGlyphStroke" + state);
        }
        foreach (string state in new[] { "Selected", "Unselected" })
        {
            Paint(state == "Selected" ? p.Selected : p.Panel, "TabItemHeaderBackground" + state);
            Paint(hover, "TabItemHeaderBackground" + state + "PointerOver");
            Paint(pressed, "TabItemHeaderBackground" + state + "Pressed");
            Paint(p.Ink, "TabItemHeaderForeground" + state, "TabItemHeaderForeground" + state + "PointerOver", "TabItemHeaderForeground" + state + "Pressed");
        }
        Paint(disabled, "TabItemHeaderBackgroundDisabled");
        Paint(p.Secondary, "TabItemHeaderForegroundDisabled");
        Paint(Colors.Transparent, "Reader.Transparent");
        Paint(headerHover, "Reader.HeaderHover");
        Paint(headerPressed, "Reader.HeaderPressed");
        r["SystemAccentColor"] = p.Accent;
        r["SystemAccentColorDark1"] = ReaderPalette.Blend(p.Accent, Colors.Black, .18);
        r["SystemAccentColorLight1"] = ReaderPalette.Blend(p.Accent, Colors.White, .24);
        r["TextControlSelectionHighlightColor"] = p.Selected;
        r["ScrollBarThumbBackgroundColor"] = p.Line;
        r["ScrollBarPanningThumbBackgroundColor"] = p.Line;
        r["TextControlPlaceholderOpacity"] = 1d;
        return r;
    }
}
