using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using LecteurWord.Platform;

namespace LecteurWord;

internal sealed record ReaderPalette(Color Header, Color HeaderInk, Color HeaderMuted, Color Toolbar, Color Footer,
    Color Panel, Color Stage, Color Ink, Color Secondary, Color Line, Color Selected, Color Accent, Color Field)
{
    internal static readonly ThemeVariant ChocolateVariant = new("Chocolate", ThemeVariant.Light);
    internal static readonly ThemeVariant BlueVariant = new("MicrosoftBlue", ThemeVariant.Light);
    internal static readonly ThemeVariant ContrastVariant = new("ReaderHighContrast", ThemeVariant.Dark);

    internal static readonly ReaderPalette Light = From("#F7F8FA", "#20252D", "#4C5668", "#EDF0F5", "#F7F8FA",
        "#FFFFFF", "#E4E8EF", "#20252D", "#4C5668", "#7C899D", "#D5E5FC", "#254F91", "#FFFFFF");
    internal static readonly ReaderPalette Chocolate = From("#FCF5EB", "#30221B", "#665145", "#F2E5D3", "#FCF5EB",
        "#F5EBDD", "#E8DCCD", "#30221B", "#665145", "#907B69", "#E9D5BF", "#79482F", "#FFF9F0");
    internal static readonly ReaderPalette Blue = From("#0B4778", "#FFFFFF", "#EAF3FC", "#EAF3FC", "#EAF3FC",
        "#FFFFFF", "#D7E8F8", "#102D46", "#345571", "#6685A1", "#CFE4F7", "#0F6CBD", "#FFFFFF");
    internal static readonly ReaderPalette Night = From("#202633", "#F3F5FA", "#CBD4E3", "#282F3D", "#202633",
        "#252C39", "#171B25", "#F3F5FA", "#CBD4E3", "#7B8CA9", "#394A63", "#A9CBFF", "#191F2B");
    internal static readonly ReaderPalette Contrast = From("#000000", "#FFFFFF", "#FFFFFF", "#000000", "#000000",
        "#000000", "#202020", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#424B00", "#FFFF00", "#000000");

    internal static readonly IBrush ReadingNightPaper = Brush(Color.Parse("#191F2B"));
    internal static readonly IBrush ReadingNightInk = Brush(Color.Parse("#F3F5FA"));
    internal static readonly IBrush ReadingNightAccent = Brush(Color.Parse("#A9CBFF"));

    internal static ReaderPalette FromExtension(ThemeColors colors)
    {
        var surface = Color.Parse(colors.Surface);
        var accent = Color.Parse(colors.Accent);
        var ink = ContrastingInk(surface);
        var reverse = ContrastingInk(ink);
        return new(surface, ink, Blend(surface, ink, .75), Blend(surface, accent, .08), surface, surface,
            Blend(surface, accent, .16), ink, Blend(surface, ink, .78), Blend(surface, ink, .54),
            Blend(surface, accent, .22), accent, Blend(surface, reverse, .20));
    }

    internal static IBrush Brush(Color color) => new ImmutableSolidColorBrush(color);
    internal static Color Blend(Color a, Color b, double weight) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * weight), (byte)Math.Round(a.G + (b.G - a.G) * weight),
        (byte)Math.Round(a.B + (b.B - a.B) * weight));

    internal static double ContrastRatio(Color a, Color b)
    {
        static double Linear(byte channel) => channel / 255d is var s && s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double first = Luminance(a) + .05, second = Luminance(b) + .05;
        return Math.Max(first, second) / Math.Min(first, second);
    }

    internal static Color ContrastingInk(Color surface) =>
        ContrastRatio(surface, Colors.Black) >= ContrastRatio(surface, Colors.White) ? Colors.Black : Colors.White;

    static ReaderPalette From(string header, string headerInk, string headerMuted, string toolbar, string footer, string panel,
        string stage, string ink, string secondary, string line, string selected, string accent, string field) =>
        new(Color.Parse(header), Color.Parse(headerInk), Color.Parse(headerMuted), Color.Parse(toolbar), Color.Parse(footer),
            Color.Parse(panel), Color.Parse(stage), Color.Parse(ink), Color.Parse(secondary), Color.Parse(line),
            Color.Parse(selected), Color.Parse(accent), Color.Parse(field));
}
