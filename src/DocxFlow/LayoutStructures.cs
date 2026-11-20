using System.Text.Json.Serialization;
using LecteurWord.DocxCore;
namespace LecteurWord.Flow;

public sealed record TabStop(float Position,string Alignment);
public sealed record ListLabel(string NumId,int Level,string Text,string Alignment,string Suffix,SourceLocation Source,LayoutStyle Style);
public sealed record InlineImage(string ResourceId,SourceLocation Source,float Width,float Height,int PixelWidth,int PixelHeight,string Sha256)
{
    [JsonIgnore] public byte[] RenderBytes {get;init;}=[];
}
public sealed record CellMargins(float Top=0,float Right=5.4f,float Bottom=0,float Left=5.4f);
public sealed record LayoutBorder(float Width=0,string Color="000000");
public sealed record CellBorders(LayoutBorder? Top=null,LayoutBorder? Right=null,LayoutBorder? Bottom=null,LayoutBorder? Left=null,LayoutBorder? InsideH=null,LayoutBorder? InsideV=null);
public sealed record LayoutCell(string Id,SourceLocation Source,int Row,int Column,int ColumnSpan,int RowSpan,string? MergeOrigin,CellMargins Margins,CellBorders Borders,string VerticalAlignment,string? Fill,IReadOnlyList<string> ParagraphIds);
public sealed record LayoutRow(int Index,float Height,string HeightRule,bool Header);
public sealed record LayoutTable(string Id,SourceLocation Source,IReadOnlyList<float> Columns,IReadOnlyList<LayoutRow> Rows,IReadOnlyList<LayoutCell> Cells,string Alignment,float Indent)
{
    public CellBorders Borders {get;init;}=new();
}
public sealed record PageCell(string TableId,string CellId,SourceLocation Source,float X,float Y,float Width,float Height,CellBorders Borders,string? Fill)
{
    public bool IsRepeated {get;init;}
    public IReadOnlyList<string> MergedSourceIds {get;init;}=[];
}
public sealed record PageImage(string RunId,InlineImage Image,float X,float Y)
{
    public bool IsRepeated {get;init;}
    public SourceLocation Source=>Image.Source;
}
public sealed record PageTab(string RunId,SourceLocation Source,float From,float To,string Alignment);
