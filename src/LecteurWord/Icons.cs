using Avalonia.Controls;
using Avalonia.Media;
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
        ["sun"]="M16 12 A4 4 0 1 1 8 12 A4 4 0 1 1 16 12 M12 0 L12 4 M12 20 L12 24 M0 12 L4 12 M20 12 L24 12 M3 3 L6 6 M18 18 L21 21 M3 21 L6 18 M18 6 L21 3",
        ["moon"]="M21 15 A10 10 0 1 1 9 2 A8 8 0 0 0 21 15",
        ["system"]="M2 3 L22 3 L22 18 L2 18 Z M8 22 L16 22 M12 18 L12 22",
        ["info"]="M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M12 7 L12 9 M12 11 L12 18",
        ["cancel"]="M4 4 L20 20 M4 20 L20 4",
        ["copy"]="M8 7 L21 7 L21 22 L8 22 Z M4 17 L2 17 L2 2 L16 2 L16 4",
        ["fit"]="M2 3 L2 21 M22 3 L22 21 M4 12 L20 12 M8 8 L4 12 L8 16 M16 8 L20 12 L16 16"
    };
    public static Control Make(string name)=>new Viewbox{Width=20,Height=20,Child=new Avalonia.Controls.Shapes.Path
    {Width=24,Height=24,Data=Geometry.Parse(Paths[name]),Stroke=new SolidColorBrush(Color.Parse("#647992")),StrokeThickness=1.8,StrokeLineCap=PenLineCap.Round,Stretch=Stretch.None}};
}
