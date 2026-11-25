using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using LecteurWord.Flow;
namespace LecteurWord;
internal static class Icons
{
    static readonly Dictionary<string,string> Paths=new()
    {
        ["open"]="M2 6 L9 6 L11 9 L22 9 L20 21 L2 21 Z M2 9 L2 4 L9 4 L11 6 L20 6 L20 9",
        ["print"]="M6 8 L6 2 L18 2 L18 8 M6 17 L2 17 L2 8 L22 8 L22 17 L18 17 M6 14 L18 14 L18 22 L6 22 Z M17 11 L19 11",
        ["search"]="M15 15 L22 22 M17 10 A7 7 0 1 1 3 10 A7 7 0 1 1 17 10",
        ["nav"]="M3 4 L7 4 L7 8 L3 8 Z M11 6 L22 6 M3 11 L7 11 L7 15 L3 15 Z M11 13 L22 13 M3 18 L7 18 L7 22 L3 22 Z M11 20 L22 20",
        ["bookmark"]="M6 2 L18 2 L18 22 L12 17 L6 22 Z",
        ["prev"]="M15 3 L6 12 L15 21",["next"]="M9 3 L18 12 L9 21",
        ["minus"]="M5 12 L19 12",["plus"]="M5 12 L19 12 M12 5 L12 19",
        ["export"]="M12 16 L12 2 M7 7 L12 2 L17 7 M5 10 L2 10 L2 22 L22 22 L22 10 L19 10",
        ["formatPdf"]="M5 2 H15 L20 7 V22 H5 Z M15 2 V7 H20 M8 17 C12 14 13 10 12 10 C11 10 11 16 17 16 C17 18 12 15 8 17 Z",
        ["formatOdt"]="M5 2 H15 L20 7 V22 H5 Z M15 2 V7 H20 M8 11 H17 M8 15 H17 M8 19 H14",
        ["formatEpub"]="M12 6 C8 3 4 3 2 4 V19 C6 18 9 19 12 21 C15 19 18 18 22 19 V4 C20 3 16 3 12 6 Z M12 6 V21",
        ["formatNova"]="M5 2 H17 A3 3 0 0 1 20 5 V21 H5 A2 2 0 0 1 3 19 V4 A2 2 0 0 1 5 2 Z M3 18 H20 M8 14 V7 L15 14 V7",
        ["moon"]="M21 15 A9 9 0 1 1 9 3 A8 8 0 0 0 21 15 Z",
        ["daylight"]="M2 3 L22 3 L22 21 L2 21 Z M2 8 L22 8 M16 12 L19 12 M16 16 L19 16",
        ["sun"]="M16 12 A4 4 0 1 1 8 12 A4 4 0 1 1 16 12 M12 0 L12 4 M12 20 L12 24 M0 12 L4 12 M20 12 L24 12 M3 3 L6 6 M18 18 L21 21 M3 21 L6 18 M18 6 L21 3",
        ["palette"]="M12 3 A9 9 0 1 0 12 21 L14 21 A2 2 0 0 0 15 17 L14 16 A2 2 0 0 1 16 13 L19 13 A2 2 0 0 0 21 11 A9 8 0 0 0 12 3 Z M7 10 L7.1 10 M10 7 L10.1 7 M15 7 L15.1 7",
        ["system"]="M2 3 L22 3 L22 18 L2 18 Z M8 22 L16 22 M12 18 L12 22",
        ["puzzle"]="M3 3 L9 3 L9 5 A3 3 0 0 0 15 5 L15 3 L21 3 L21 9 L19 9 A3 3 0 0 0 19 15 L21 15 L21 21 L15 21 L15 19 A3 3 0 0 0 9 19 L9 21 L3 21 Z",
        ["help"]="M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M9 8 A3 3 0 1 1 14 10 L12 12 L12 14 M12 17 L12 18",
        ["info"]="M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M12 7 L12 9 M12 11 L12 18",
        ["cancel"]="M4 4 L20 20 M4 20 L20 4",
        ["copy"]="M8 7 L21 7 L21 22 L8 22 Z M4 17 L2 17 L2 2 L16 2 L16 4",
        ["readingColor"]="M5 16 L10 4 H14 L19 16 M7 12 H17",
        ["ruler"]="M2 7 L22 7 L22 17 L2 17 Z M6 7 L6 12 M10 7 L10 10 M14 7 L14 12 M18 7 L18 10",
        ["marks"]="M11 3 L21 3 M14 3 L14 21 M18 3 L18 21 M12 3 L10 3 A5 5 0 0 0 10 13 L14 13",
        ["sidebar"]="M3 4 L21 4 L21 20 L3 20 Z M9 4 L9 20 M5 8 L7 8 M5 12 L7 12",
        ["drawer"]="M3 4 L21 4 L21 20 L3 20 Z M15 4 L15 20 M17 8 L19 8 M17 12 L19 12",
        ["properties"]="M5 2 L15 2 L20 7 L20 22 L5 22 Z M15 2 L15 7 L20 7 M8 11 L17 11 M8 15 L17 15 M8 19 L14 19",
        ["globe"]="M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M2 12 L22 12 M12 2 C6 7 6 17 12 22 C18 17 18 7 12 2",
        ["flow"]="M4 3 L20 3 M4 8 L20 8 M4 13 L20 13 M4 18 L16 18",
        ["sheet"]="M5 2 L19 2 L19 22 L5 22 Z M8 7 L16 7 M8 11 L16 11",
        ["facing"]="M2 4 L10 4 L10 20 L2 20 Z M14 4 L22 4 L22 20 L14 20 Z M4 8 L8 8 M16 8 L20 8",
        ["fit"]="M2 3 L2 21 M22 3 L22 21 M4 12 L20 12 M8 8 L4 12 L8 16 M16 8 L20 12 L16 16"
    };
    internal static string ForExportFormat(ExportFormat format) => format switch
    {
        ExportFormat.Pdf => "formatPdf",
        ExportFormat.Odt => "formatOdt",
        ExportFormat.Epub3 => "formatEpub",
        ExportFormat.Nova => "formatNova",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static Control Make(string name)
    {
        var path=new Avalonia.Controls.Shapes.Path{Width=24,Height=24,Data=Geometry.Parse(Paths[name]),
            StrokeThickness=1.8,StrokeLineCap=PenLineCap.Round,Stretch=Stretch.None};
        path.Bind(Shape.StrokeProperty,path.GetObservable(TextElement.ForegroundProperty));
        if(name!="readingColor")return new Viewbox{Width=17,Height=17,Child=path};
        var underline=new Avalonia.Controls.Shapes.Path{Width=24,Height=24,Data=Geometry.Parse("M3 21 H21"),
            StrokeThickness=3,StrokeLineCap=PenLineCap.Round,Stretch=Stretch.None};
        ReaderAppearance.Use(underline,Shape.StrokeProperty,ReaderAppearance.Accent);
        return new Viewbox{Width=17,Height=17,Child=new Grid{Children={path,underline}}};
    }
}
