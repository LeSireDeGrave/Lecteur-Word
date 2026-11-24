using SkiaSharp;
using System.Collections.Immutable;
namespace LecteurWord.Flow;

public static partial class Composer
{
    sealed record MeasuredCell(LayoutCell Cell,IReadOnlyList<MeasuredParagraph> Paragraphs,float Height,ComposedPage? FloatingFlow);
    sealed record MeasuredTable(LayoutTable Table,IReadOnlyDictionary<string,MeasuredCell> Cells,float[] Heights,
        float[] Offsets,IReadOnlyList<(int First,int Last)> Groups);
    sealed record RowLine(LayoutParagraph Paragraph,MeasuredLine Line,float Top,bool BreakAfter);
    sealed record RowCellFlow(LayoutCell Cell,IReadOnlyList<RowLine> Lines,float End);
    sealed record CellSlice(RowCellFlow Flow,int Start,int End,float Origin);
    sealed record RowSlice(IReadOnlyList<CellSlice> Cells,float Height,bool Last);
    static MeasuredTable MeasureTable(LayoutTable table,IReadOnlyDictionary<string,MeasuredParagraph> paragraphs,Action check,
        Func<LayoutParagraph,LineRegion,LineResume,IReadOnlyList<MeasuredLine>>? reflow=null)
    {
        var heights=table.Rows.Select(r=>r.Height).ToArray();var cells=new Dictionary<string,MeasuredCell>();
        foreach(var cell in table.Cells.Where(c=>c.MergeOrigin is null))
        {
            check();var items=cell.ParagraphIds.Select(id=>paragraphs[id]).ToArray();float height=cell.Margins.Top+cell.Margins.Bottom,after=0;
            foreach(var item in items){height+=Math.Max(after,item.Paragraph.Properties.Before)+item.Height;after=item.Paragraph.Properties.After;}
            height+=after;
            ComposedPage? flow=null;
            if(items.Any(p=>p.Paragraph.SideExclusion is not null))
            {
                if(reflow is null||table.AutoFit||cell.RowSpan!=1||table.Rows[cell.Row].Header||
                    items.Count(p=>p.Paragraph.SideExclusion is not null)!=1||
                    items.SelectMany(p=>p.Lines).Any(l=>l.Native is not {} line||
                        line.Parts.Any(part=>part.Control?.Kind=="NoteCall")||
                        line.Generated.Any(g=>g.Kind=="pageField")))
                    throw new LayoutFailure("PAGE_CELL_FLOAT_FLOW_UNQUALIFIED",cell.Source);
                var local=items.Select(p=>p with{Paragraph=p.Paragraph with{CellOwner=null,
                    Geometry=p.Paragraph.Geometry with{Top=cell.Margins.Top,Bottom=cell.Margins.Bottom}}}).ToList();
                using var cellFonts=new LayoutFonts();
                var placed=PlaceMeasured(new(local.Select(p=>p.Paragraph).ToArray(),[]),local,cellFonts,check,true,reflow:reflow);
                if(placed.Pages is not [var inner]||placed.Issues.Any(i=>i.BlocksRendering))
                    throw new LayoutFailure("PAGE_CELL_FLOAT_FLOW_UNQUALIFIED",cell.Source);
                flow=inner;
                height=inner.Lines.Select(line=>Math.Max(line.NativeTop+line.Height,
                        line.SideExclusion is {} box?PageComposer.Points(box.Top+box.Height):0))
                    .DefaultIfEmpty(cell.Margins.Top).Max()+after+cell.Margins.Bottom;
            }
            cells[cell.Id]=new(cell,items,height,flow);
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
        var offsets=new float[heights.Length+1];
        for(int i=0;i<heights.Length;i++)offsets[i+1]=offsets[i]+heights[i];
        return new(table,cells,heights,offsets,groups);
    }
    static float TableStartHeight(MeasuredTable table)
    {
        int headers=table.Table.Rows.TakeWhile(r=>r.Header).Count();float height=table.Heights.Take(headers).Sum();
        if(headers<table.Table.Rows.Count){var first=table.Groups.First(g=>g.First>=headers);height+=table.Heights.Skip(first.First).Take(first.Last-first.First+1).Sum();}
        return height;
    }

    static IEnumerable<MeasuredLine> TableLines(MeasuredTable table,int first,int last)=>
        table.Cells.Values.Where(c=>c.Cell.Row>=first&&c.Cell.Row<=last)
            .OrderBy(c=>c.Cell.Row).ThenBy(c=>c.Cell.Column).SelectMany(c=>c.Paragraphs).SelectMany(p=>p.Lines);

    static bool CanFragmentRow(MeasuredTable table,(int First,int Last) group)
    {
        var row=table.Table.Rows[group.First];
        return group.First==group.Last&&row is {Header:false,CantSplit:false,Height:0,HeightRule:not "exact"}&&
            table.Cells.Values.Where(c=>c.Cell.Row==group.First).All(c=>
                c.FloatingFlow is null&&c.Cell is {RowSpan:1,VerticalAlignment:"top"}&&
                c.Paragraphs.SelectMany(p=>p.Lines).All(l=>l.Native is {Cut:not (LineCutKind.Page or LineCutKind.Column)}));
    }

    static RowCellFlow[] PrepareRowFlow(MeasuredTable table,int row,Action check)
    {
        return table.Cells.Values.Where(c=>c.Cell.Row==row).OrderBy(c=>c.Cell.Column).Select(data=>
        {
            var lines=new List<RowLine>();
            float top=data.Cell.Margins.Top,after=0;
            for(int i=0;i<data.Paragraphs.Count;i++)
            {
                var item=data.Paragraphs[i];var properties=item.Paragraph.Properties;
                top+=Math.Max(after,properties.Before);
                for(int j=0;j<item.Lines.Count;j++)
                {
                    check();
                    bool paragraphEnd=j+1==item.Lines.Count;
                    bool cut=paragraphEnd?(!properties.KeepNext||i+1==data.Paragraphs.Count):
                        !properties.KeepLines&&(!properties.Widow||j>=1&&item.Lines.Count-j-1>=2);
                    var line=item.Lines[j];
                    lines.Add(new(item.Paragraph,line,top,cut));
                    top+=line.Height;
                }
                after=properties.After;
            }
            return new RowCellFlow(data.Cell,lines,top+after+data.Cell.Margins.Bottom);
        }).ToArray();
    }

    static float MinimumRowStart(IReadOnlyList<RowCellFlow> cells)=>cells.Select(cell=>
    {
        var line=cell.Lines.FirstOrDefault(l=>l.BreakAfter);
        return line is null||ReferenceEquals(line,cell.Lines.LastOrDefault())?cell.End:line.Top+line.Line.Height;
    }).DefaultIfEmpty().Max();

    static RowSlice? SliceRow(IReadOnlyList<RowCellFlow> cells,int[] indices,float available,Action check)
    {
        var result=new CellSlice[cells.Count];
        bool advanced=false,last=true;
        float height=0;
        for(int i=0;i<cells.Count;i++)
        {
            check();
            var cell=cells[i];int start=indices[i],end=start;
            if(start==cell.Lines.Count)
            {
                result[i]=new(cell,start,start,0);
                continue;
            }
            float origin=start==0?0:cell.Lines[start].Top;
            for(int j=start;j<cell.Lines.Count;j++)
            {
                check();var line=cell.Lines[j];
                float bottom=j+1==cell.Lines.Count?cell.End:line.Top+line.Line.Height;
                if(bottom-origin>available+.01f)break;
                // A widow-protected continuation must also keep its first two remaining lines together.
                if(line.BreakAfter&&(j>start||j+1==cell.Lines.Count||
                    cell.Lines[j+1].Paragraph.Id!=line.Paragraph.Id||!line.Paragraph.Properties.Widow))
                    end=j+1;
            }
            if(end==start)return null;
            advanced=true;last&=end==cell.Lines.Count;
            float extent=end==cell.Lines.Count?cell.End:cell.Lines[end-1].Top+cell.Lines[end-1].Line.Height;
            height=Math.Max(height,extent-origin);
            result[i]=new(cell,start,end,origin);
        }
        return advanced?new(result,height,last):null;
    }

    static void PlaceRowFragment(MeasuredTable table,int row,int index,RowSlice slice,float x,float y,PlacedFlowRegion? region,
        List<PageLine> lines,List<PageCell> boxes,Action check)
    {
        var source=table.Table.Rows[row];
        var identity=new TableRowFragment(source.SourceId!,source.Source!,index,index==0,slice.Last);
        foreach(var part in slice.Cells)
        {
            check();var cell=part.Flow.Cell;
            float left=x+table.Table.Columns.Take(cell.Column).Sum();
            float width=table.Table.Columns.Skip(cell.Column).Take(cell.ColumnSpan).Sum();
            var edges=EffectiveBorders(table.Table,cell);
            edges=edges with {Top=identity.First?edges.Top:null,Bottom=identity.Last?edges.Bottom:null};
            boxes.Add(new(table.Table.Id,cell.Id,cell.Source,left,y,width,slice.Height,edges,cell.Fill)
                {FlowRegion=region,RowFragment=identity});
            for(int i=part.Start;i<part.End;i++)
            {
                check();
                var entry=part.Flow.Lines[i];
                lines.Add(PositionNative(entry.Line,entry.Paragraph,left+cell.Margins.Left,y+entry.Top-part.Origin,
                    left,y,left+width,y+slice.Height,cell.Id) with{FlowRegion=region});
            }
        }
    }
    internal static CellBorders EffectiveBorders(LayoutTable table,LayoutCell cell)
    {
        var b=cell.Borders;
        return new(b.Top??(cell.Row==0?table.Borders.Top:b.InsideH??table.Borders.InsideH),
            b.Right??(cell.Column+cell.ColumnSpan==table.Columns.Count?table.Borders.Right:b.InsideV??table.Borders.InsideV),
            b.Bottom??(cell.Row+cell.RowSpan==table.Rows.Count?table.Borders.Bottom:b.InsideH??table.Borders.InsideH),
            b.Left??(cell.Column==0?table.Borders.Left:b.InsideV??table.Borders.InsideV));
    }
    static float CellAlignmentOffset(MeasuredCell data,float height)
    {
        float extra=height-data.Height;
        return data.Cell.VerticalAlignment=="bottom"?extra:data.Cell.VerticalAlignment=="center"?extra/2:0;
    }

    static bool CanCutMergedCells(MeasuredTable table,int boundary,Action check)
    {
        foreach(var data in table.Cells.Values.Where(c=>c.Cell.Row<boundary&&c.Cell.Row+c.Cell.RowSpan>boundary))
        {
            check();
            var cell=data.Cell;
            float start=table.Offsets[cell.Row],height=table.Offsets[cell.Row+cell.RowSpan]-start;
            float cut=table.Offsets[boundary]-start;
            float cursor=cell.Margins.Top+CellAlignmentOffset(data,height),after=0;
            bool keepWithNext=false;
            foreach(var item in data.Paragraphs)
            {
                check();
                cursor+=Math.Max(after,item.Paragraph.Properties.Before);
                if(cursor>=cut-.01f)
                {
                    if(keepWithNext)return false;
                    break;
                }
                cursor+=item.Height;
                if(cursor>cut+.01f)return false;
                after=item.Paragraph.Properties.After;
                keepWithNext=item.Paragraph.Properties.KeepNext;
            }
        }
        return true;
    }

    static void PlaceTableRows(MeasuredTable measured,int first,int last,float x,float y,bool repeated,List<PageLine> lines,List<PageCell> boxes,
        LayoutFonts fonts,Action check,bool splitMerges=false)
    {
        var table=measured.Table;var offsets=measured.Offsets;
        foreach(var data in measured.Cells.Values.Where(c=>c.Cell.Row<=last&&(splitMerges?c.Cell.Row+c.Cell.RowSpan>first:c.Cell.Row>=first))
            .OrderBy(c=>c.Cell.Row).ThenBy(c=>c.Cell.Column))
        {
            check();var cell=data.Cell;
            int end=cell.Row+cell.RowSpan;
            int firstRow=splitMerges?Math.Max(cell.Row,first):cell.Row,afterRow=splitMerges?Math.Min(end,last+1):end;
            float cx=x+table.Columns.Take(cell.Column).Sum(),wholeY=y+offsets[cell.Row]-offsets[first];
            float cy=y+offsets[firstRow]-offsets[first],width=table.Columns.Skip(cell.Column).Take(cell.ColumnSpan).Sum();
            float height=offsets[afterRow]-offsets[firstRow],wholeHeight=offsets[end]-offsets[cell.Row];
            var borders=EffectiveBorders(table,cell);
            TableCellFragment? fragment=null;
            if(cell.Row!=firstRow||end!=afterRow)
            {
                fragment=new(firstRow,afterRow-1,cell.Row==firstRow,end==afterRow);
                borders=borders with{Top=fragment.First?borders.Top:null,Bottom=fragment.Last?borders.Bottom:null};
            }
            boxes.Add(new(table.Id,cell.Id,cell.Source,cx,cy,width,height,borders,cell.Fill)
            {IsRepeated=repeated,CellFragment=fragment,MergedSourceIds=table.Cells.Where(c=>c.MergeOrigin==cell.Id).Select(c=>c.Id).ToImmutableArray()});
            if(data.FloatingFlow is {} flow)
            {
                float shift=wholeY+CellAlignmentOffset(data,wholeHeight);
                foreach(var entry in flow.Lines)
                {
                    check();
                    lines.Add(PageComposer.TranslateLine(entry,cx,shift) with{CellId=cell.Id,FlowRegion=null});
                }
                continue;
            }
            float cursor=wholeY+cell.Margins.Top+CellAlignmentOffset(data,wholeHeight),after=0;
            foreach(var p in data.Paragraphs)
            {
                cursor+=Math.Max(after,p.Paragraph.Properties.Before);int index=0;
                foreach(var line in p.Lines)
                {
                    if(line.Native is not null)
                    {
                        if(fragment is not null&&(cursor+line.Height<=cy+.01f||cursor>=cy+height-.01f))
                        {cursor+=line.Height;index++;continue;}
                        if(fragment is not null&&(cursor<cy-.01f||cursor+line.Height>cy+height+.01f))
                            throw new LayoutFailure("PAGE_CELL_FRAGMENT_CUT",cell.Source);
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
        foreach(var cell in page.MainDisplayCells)
        {
            if(cell.Fill is {Length:6}){paint.Style=SKPaintStyle.Fill;paint.Color=SKColor.Parse("#"+cell.Fill);canvas.DrawRect(cell.X,cell.Y,cell.Width,cell.Height,paint);}
        }
    }
    static void DrawCellBorders(SKCanvas canvas,ComposedPage page)
    {
        using var paint=new SKPaint{IsAntialias=true,Style=SKPaintStyle.Stroke};
        foreach(var cell in page.MainDisplayCells)
            DrawCellEdges(canvas,paint,cell.X,cell.Y,cell.Width,cell.Height,cell.Borders);
    }

    internal static SKPicture RecordCellBorders(float width,float height,CellBorders borders)
    {
        using var recorder=new SKPictureRecorder();
        using var paint=new SKPaint{IsAntialias=true,Style=SKPaintStyle.Stroke};
        DrawCellEdges(recorder.BeginRecording(SKRect.Create(width,height)),paint,0,0,width,height,borders);
        return recorder.EndRecording();
    }

    static void DrawCellEdges(SKCanvas canvas,SKPaint paint,float x,float y,float width,float height,CellBorders borders)
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
        Line(borders.Top,x,y,x+width,y);
        Line(borders.Bottom,x,y+height,x+width,y+height);
        Line(borders.Left,x,y,x,y+height);
        Line(borders.Right,x+width,y,x+width,y+height);
    }
}
