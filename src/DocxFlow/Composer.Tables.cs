using SkiaSharp;
using System.Collections.Immutable;
namespace LecteurWord.Flow;

public static partial class Composer
{
    sealed record MeasuredCell(LayoutCell Cell,IReadOnlyList<MeasuredParagraph> Paragraphs,float Height);
    sealed record MeasuredTable(LayoutTable Table,IReadOnlyDictionary<string,MeasuredCell> Cells,float[] Heights,IReadOnlyList<(int First,int Last)> Groups);
    static MeasuredTable MeasureTable(LayoutTable table,IReadOnlyDictionary<string,MeasuredParagraph> paragraphs,Action check)
    {
        var heights=table.Rows.Select(r=>r.Height).ToArray();var cells=new Dictionary<string,MeasuredCell>();
        foreach(var cell in table.Cells.Where(c=>c.MergeOrigin is null))
        {
            check();var items=cell.ParagraphIds.Select(id=>paragraphs[id]).ToArray();float height=cell.Margins.Top+cell.Margins.Bottom,after=0;
            foreach(var item in items){height+=Math.Max(after,item.Paragraph.Properties.Before)+item.Height;after=item.Paragraph.Properties.After;}
            height+=after;cells[cell.Id]=new(cell,items,height);
            if(cell.RowSpan==1)
            {
                if(table.Rows[cell.Row].HeightRule=="exact"&&height>heights[cell.Row]+.01f)throw new InvalidDataException("LAYOUT_EXACT_ROW_CLIPS_CONTENT");
                heights[cell.Row]=Math.Max(heights[cell.Row],height);
            }
        }
        foreach(var cell in cells.Values.Where(c=>c.Cell.RowSpan>1))
        {
            float height=heights.Skip(cell.Cell.Row).Take(cell.Cell.RowSpan).Sum();float extra=cell.Height-height;
            if(extra>0)
            {
                int row=Enumerable.Range(cell.Cell.Row,cell.Cell.RowSpan).LastOrDefault(i=>table.Rows[i].HeightRule!="exact",-1);
                if(row<0)throw new InvalidDataException("LAYOUT_EXACT_MERGE_CLIPS_CONTENT");heights[row]+=extra;
            }
        }
        // Merge-connected row intervals are indivisible. This preserves vertical spans.
        var groups=new List<(int First,int Last)>();
        for(int first=0;first<table.Rows.Count;)
        {
            int last=first==0?Math.Max(0,table.Rows.TakeWhile(r=>r.Header).Count()-1):first;
            for(int row=first;row<=last;row++)foreach(var cell in table.Cells.Where(c=>c.MergeOrigin is null&&c.Row==row))last=Math.Max(last,row+cell.RowSpan-1);
            groups.Add((first,last));first=last+1;
        }
        return new(table,cells,heights,groups);
    }
    static float TableStartHeight(MeasuredTable table)
    {
        int headers=table.Table.Rows.TakeWhile(r=>r.Header).Count();float height=table.Heights.Take(headers).Sum();
        if(headers<table.Table.Rows.Count){var first=table.Groups.First(g=>g.First>=headers);height+=table.Heights.Skip(first.First).Take(first.Last-first.First+1).Sum();}
        return height;
    }
    static CellBorders EffectiveBorders(LayoutTable table,LayoutCell cell)
    {
        var b=cell.Borders;
        return new(b.Top??(cell.Row==0?table.Borders.Top:b.InsideH??table.Borders.InsideH),
            b.Right??(cell.Column+cell.ColumnSpan==table.Columns.Count?table.Borders.Right:b.InsideV??table.Borders.InsideV),
            b.Bottom??(cell.Row+cell.RowSpan==table.Rows.Count?table.Borders.Bottom:b.InsideH??table.Borders.InsideH),
            b.Left??(cell.Column==0?table.Borders.Left:b.InsideV??table.Borders.InsideV));
    }
    static void PlaceTableRows(MeasuredTable measured,int first,int last,float x,float y,bool repeated,List<PageLine> lines,List<PageCell> boxes,LayoutFonts fonts,Action check)
    {
        var table=measured.Table;var offsets=new float[table.Rows.Count+1];for(int i=0;i<table.Rows.Count;i++)offsets[i+1]=offsets[i]+measured.Heights[i];
        foreach(var data in measured.Cells.Values.Where(c=>c.Cell.Row>=first&&c.Cell.Row<=last).OrderBy(c=>c.Cell.Row).ThenBy(c=>c.Cell.Column))
        {
            check();var cell=data.Cell;float cx=x+table.Columns.Take(cell.Column).Sum(),cy=y+offsets[cell.Row]-offsets[first],width=table.Columns.Skip(cell.Column).Take(cell.ColumnSpan).Sum(),height=offsets[cell.Row+cell.RowSpan]-offsets[cell.Row];
            boxes.Add(new(table.Id,cell.Id,cell.Source,cx,cy,width,height,EffectiveBorders(table,cell),cell.Fill){IsRepeated=repeated,MergedSourceIds=table.Cells.Where(c=>c.MergeOrigin==cell.Id).Select(c=>c.Id).ToImmutableArray()});
            float extra=height-data.Height;float cursor=cy+cell.Margins.Top+(cell.VerticalAlignment=="bottom"?extra:cell.VerticalAlignment=="center"?extra/2:0),after=0;
            foreach(var p in data.Paragraphs)
            {
                cursor+=Math.Max(after,p.Paragraph.Properties.Before);int index=0;
                foreach(var line in p.Lines)
                {
                    if(line.Native is not null)
                    {
                        lines.Add(PositionNative(line,p.Paragraph,cx+cell.Margins.Left,cursor,cx,cy,cx+width,cy+height,cell.Id,repeated));
                        cursor+=line.Height;index++;
                        continue;
                    }
                    float left=cx+cell.Margins.Left+p.Paragraph.Properties.Left+(index==0&&p.Paragraph.List is null?p.Paragraph.Properties.FirstLine:0),available=cx+width-cell.Margins.Right-p.Paragraph.Properties.Right-left;
                    float shift=p.Paragraph.Properties.Alignment switch{"center"=>(available-line.Width)/2,"right"=>available-line.Width,_=>0};float baseline=cursor+line.Ascent;
                    var fragments=Justify(line,available,p.Paragraph.Properties.Alignment=="both"&&index<p.Lines.Count-1,fonts).Select(f=>f with{X=f.X+left+shift}).ToArray();
                    foreach(var f in fragments)
                    {fonts.Get(f.Style).MeasureText(f.Text,out var ink);if(f.X+ink.Left<cx-.01f||f.X+ink.Right>cx+width+.01f||baseline+ink.Top<cy-.01f||baseline+ink.Bottom>cy+height+.01f)throw new InvalidDataException("LAYOUT_CELL_GLYPH_OUTSIDE");}
                    var images=line.Images.Select(i=>i with{X=i.X+left+shift,Y=baseline-i.Image.Height,IsRepeated=repeated}).ToArray();
                    if(images.Any(i=>i.X<cx-.01f||i.X+i.Image.Width>cx+width+.01f||i.Y<cy-.01f||i.Y+i.Image.Height>cy+height+.01f))throw new InvalidDataException("LAYOUT_CELL_IMAGE_OUTSIDE");
                    lines.Add(new(p.Paragraph.Id,p.Paragraph.Source,line.Text,left+shift,baseline){Fragments=fragments,Height=line.Height,SourceBreak=line.SourceBreak,Section=p.Paragraph.Section,Images=images,Tabs=line.Tabs.Select(t=>t with{From=t.From+left+shift,To=t.To+left+shift}).ToArray(),CellId=cell.Id,IsRepeated=repeated});cursor+=line.Height;index++;
                }
                after=p.Paragraph.Properties.After;
            }
        }
    }
    static void ValidateCellBorders(IReadOnlyList<PageCell> boxes,Action check)
    {
        // Compare only edges sharing a boundary, once per finalized page.
        foreach(var table in boxes.GroupBy(b=>b.TableId))
        foreach(bool horizontal in new[]{true,false})
        {
            var edges=table.SelectMany(b=>horizontal?new[]{(Pos:b.Y,Start:b.X,End:b.X+b.Width,Border:b.Borders.Top,Side:0),(Pos:b.Y+b.Height,Start:b.X,End:b.X+b.Width,Border:b.Borders.Bottom,Side:1)}:new[]{(Pos:b.X,Start:b.Y,End:b.Y+b.Height,Border:b.Borders.Left,Side:0),(Pos:b.X+b.Width,Start:b.Y,End:b.Y+b.Height,Border:b.Borders.Right,Side:1)});
            foreach(var group in edges.GroupBy(e=>Math.Round(e.Pos,3)))
            foreach(var first in group.Where(e=>e.Side==0))
            {
                check();foreach(var second in group.Where(e=>e.Side==1))
                    if(first.Start<second.End-.01f&&second.Start<first.End-.01f&&first.Border is {Width:>0}&&second.Border is {Width:>0}&&first.Border!=second.Border)throw new InvalidDataException("LAYOUT_BORDER_CONFLICT_UNQUALIFIED");
            }
        }
    }
    static void DrawCells(SKCanvas canvas,ComposedPage page)
    {
        using var paint=new SKPaint{IsAntialias=true};
        foreach(var cell in page.DisplayCells)
        {
            if(cell.Fill is {Length:6}){paint.Style=SKPaintStyle.Fill;paint.Color=SKColor.Parse("#"+cell.Fill);canvas.DrawRect(cell.X,cell.Y,cell.Width,cell.Height,paint);}
        }
    }
    static void DrawCellBorders(SKCanvas canvas,ComposedPage page)
    {
        using var paint=new SKPaint{IsAntialias=true,Style=SKPaintStyle.Stroke};
        foreach(var c in page.DisplayCells)
        {
            void Line(LayoutBorder? border,float x1,float y1,float x2,float y2)
            {
                if(border is not {Width:>0})return;paint.Color=SKColor.Parse("#"+border.Color);paint.StrokeWidth=border.Width;
                using var effect=border.Style is "dotted" or "dashed"?SKPathEffect.CreateDash(border.Style=="dotted"?[border.Width,border.Width*2]:[border.Width*4,border.Width*2],0):null;paint.PathEffect=effect;
                if(border.Style=="double")
                {
                    paint.StrokeWidth=border.Width/3;float dx=y1==y2?0:border.Width/3,dy=y1==y2?border.Width/3:0;
                    canvas.DrawLine(x1-dx,y1-dy,x2-dx,y2-dy,paint);canvas.DrawLine(x1+dx,y1+dy,x2+dx,y2+dy,paint);
                }
                else canvas.DrawLine(x1,y1,x2,y2,paint);
                paint.PathEffect=null;
            }
            Line(c.Borders.Top,c.X,c.Y,c.X+c.Width,c.Y);Line(c.Borders.Bottom,c.X,c.Y+c.Height,c.X+c.Width,c.Y+c.Height);Line(c.Borders.Left,c.X,c.Y,c.X,c.Y+c.Height);Line(c.Borders.Right,c.X+c.Width,c.Y,c.X+c.Width,c.Y+c.Height);
        }
    }
}
