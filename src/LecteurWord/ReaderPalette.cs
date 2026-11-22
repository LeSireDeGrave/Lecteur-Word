using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace LecteurWord;

internal sealed record ReaderPalette(IBrush Header,IBrush HeaderInk,IBrush HeaderMuted,IBrush Toolbar,IBrush Footer,
    IBrush Panel,IBrush Stage,IBrush Ink,IBrush Secondary,IBrush Line,IBrush Selected,IBrush Accent,IBrush Field,
    IBrush AboutSurface,IBrush AboutHeading,IBrush AboutInk,IBrush AboutFeedback)
{
    internal static ThemeVariant BlueVariant { get; }=new("MicrosoftBlue",ThemeVariant.Light);
    internal static ReaderPalette Chocolate { get; }=new(
        B("#FCF5EB"),B("#30221B"),B("#665145"),B("#F2E5D3"),B("#FCF5EB"),B("#F5EBDD"),B("#E8DCCD"),
        B("#30221B"),B("#665145"),B("#CFBDA9"),B("#E9D5BF"),B("#79482F"),B("#FFF9F0"),
        B("#F8FAFC"),Brushes.White,B("#172033"),B("#3156A5"));

    internal static ReaderPalette Blue { get; }=new(
        B("#0B4778"),Brushes.White,B("#EAF3FC"),B("#EAF3FC"),B("#EAF3FC"),Brushes.White,B("#D7E8F8"),
        B("#102D46"),B("#345571"),B("#A0BDDA"),B("#CFE4F7"),B("#0B4778"),Brushes.White,
        B("#EAF3FC"),Brushes.White,B("#102D46"),B("#0B4778"));

    internal static ReaderPalette For(ThemeVariant? variant)=>variant==BlueVariant?Blue:Chocolate;
    internal static IBrush BluePrimary { get; }=B("#0F6CBD");
    internal static IBrush BlueLight { get; }=B("#6CA9DE");
    internal static IBrush BlueHeaderPressed { get; }=B("#083453");

    // The document surface keeps the existing light reading colours across interface palettes.
    internal static IBrush ReadingPaper { get; }=B("#FFF9F0");
    internal static IBrush ReadingInk { get; }=B("#38261E");
    internal static IBrush ReadingLine { get; }=B("#CFBDA9");
    internal static IBrush RevisionInk { get; }=B("#80533B");

    internal static ResourceDictionary BlueResources()
    {
        var r=new ResourceDictionary();
        var primary=BluePrimary;
        var hover=B("#E1EFFC");
        var pressed=B("#BDD9F0");
        var edge=B("#6685A1");
        var muted=B("#51657A");
        var disabled=B("#EDF1F5");

        void Paint(IBrush brush,params string[] keys){foreach(string key in keys)r[key]=brush;}

        r["SystemAccentColor"]=Color.Parse("#0F6CBD");
        r["SystemAccentColorDark1"]=Color.Parse("#0B4778");
        r["SystemAccentColorLight1"]=Color.Parse("#6CA9DE");
        Paint(primary,"SystemControlFocusVisualPrimaryBrush","SystemControlHighlightAccentBrush",
            "TextControlBorderBrushFocused","ButtonBorderBrushPressed");
        Paint(Brushes.White,"SystemControlFocusVisualSecondaryBrush");
        Paint(Blue.Ink,"ButtonForeground","ButtonForegroundPointerOver","ButtonForegroundPressed",
            "TextControlForeground","TextControlForegroundFocused","TextControlForegroundPointerOver",
            "ComboBoxForeground","ComboBoxForegroundFocused","ComboBoxForegroundFocusedPressed",
            "ComboBoxItemForeground","ComboBoxItemForegroundPointerOver","ComboBoxItemForegroundSelected",
            "ComboBoxItemForegroundSelectedPointerOver","ComboBoxItemForegroundPressed","ComboBoxItemForegroundSelectedPressed",
            "MenuFlyoutItemForeground","MenuFlyoutItemForegroundPointerOver","MenuFlyoutItemForegroundPressed",
            "ToolTipForeground","SystemControlForegroundBaseHighBrush","SystemControlForegroundBaseMediumHighBrush");
        Paint(Blue.Secondary,"TextControlPlaceholderForeground","TextControlPlaceholderForegroundFocused",
            "TextControlPlaceholderForegroundPointerOver","ComboBoxPlaceHolderForeground","ComboBoxDropDownGlyphForeground");
        r["TextControlPlaceholderOpacity"]=1d;
        Paint(Blue.Field,"TextControlBackground","TextControlBackgroundFocused","TextControlBackgroundPointerOver",
            "ComboBoxBackground","ComboBoxBackgroundUnfocused","ComboBoxDropDownBackground","ButtonBackground",
            "FlyoutPresenterBackground","MenuFlyoutPresenterBackground","ToolTipBackground");
        Paint(edge,"TextControlBorderBrush","TextControlBorderBrushPointerOver","ComboBoxBorderBrush",
            "ComboBoxBorderBrushPointerOver","ComboBoxDropDownBorderBrush","ComboBoxBackgroundBorderBrushUnfocused",
            "MenuFlyoutPresenterBorderBrush","ToolTipBorderBrush","ButtonBorderBrush","ButtonBorderBrushPointerOver");
        Paint(primary,"ComboBoxBackgroundBorderBrushFocused","ComboBoxBorderBrushPressed");
        Paint(hover,"ButtonBackgroundPointerOver","ComboBoxBackgroundPointerOver","ComboBoxItemBackgroundPointerOver",
            "MenuFlyoutItemBackgroundPointerOver","SystemControlHighlightListLowBrush");
        Paint(pressed,"ButtonBackgroundPressed","ComboBoxBackgroundPressed","ComboBoxItemBackgroundPressed",
            "ComboBoxItemBackgroundSelectedPressed","MenuFlyoutItemBackgroundPressed",
            "SystemControlHighlightListAccentHighBrush","SystemControlHighlightListMediumBrush");
        Paint(Blue.Selected,"ComboBoxItemBackgroundSelected","ComboBoxItemBackgroundSelectedPointerOver",
            "SystemControlHighlightListAccentLowBrush","SystemControlHighlightListAccentMediumBrush","SystemControlHighlightAltListAccentLowBrush");
        Paint(disabled,"ButtonBackgroundDisabled","TextControlBackgroundDisabled","ComboBoxBackgroundDisabled");
        Paint(muted,"ButtonForegroundDisabled","TextControlForegroundDisabled","TextControlPlaceholderForegroundDisabled",
            "ComboBoxForegroundDisabled","ComboBoxItemForegroundDisabled","MenuFlyoutItemForegroundDisabled","ComboBoxDropDownGlyphForegroundDisabled");
        Paint(Blue.Line,"ButtonBorderBrushDisabled","TextControlBorderBrushDisabled","ComboBoxBorderBrushDisabled");
        return r;
    }

    static IBrush B(string hex)=>new SolidColorBrush(Color.Parse(hex));
}
