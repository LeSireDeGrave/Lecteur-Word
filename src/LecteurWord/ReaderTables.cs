using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using LecteurWord.Flow;
using SkiaSharp;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    internal PageCompositionSnapshot? ReadingTableSnapshot=>nativePageLease?.Snapshot;

    Control RenderReadingTable(ReadableBlock block)
    {
        double scale=PointsToDip*readingZoom;
        var snapshot=ReadingTableSnapshot;
        var layout=snapshot?.ReadingGrids.GetValueOrDefault(block.Id);
        var scene=snapshot?.ReadingTables.GetValueOrDefault(block.Id);
        var table=new ScrollViewer{Name="ReadingTable",Tag=block.Id,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,IsScrollChainingEnabled=true,Background=ReaderPalette.ReadingPaper};
        var content=scene is null?ReadingTableGrid(block,layout,scale):ReadingTableScene(block,scene,scale);
        var position=new Border{Child=content,HorizontalAlignment=layout?.Alignment switch
        {
            "center"=>HorizontalAlignment.Center,"right"=>HorizontalAlignment.Right,_=>HorizontalAlignment.Left
        }};
        if(layout?.Alignment=="left")
        {
            double inset=layout.Indent*scale;
            if(inset>0)position.Padding=new Thickness(inset,0,0,0);
            if(inset<0)table.Margin=new Thickness(inset,0,0,0);
        }
        table.Content=position;
        return table;
    }

    Control ReadingTableScene(ReadableBlock block,ComposedPage scene,double scale)
    {
        var canvas=new Canvas{Name="ReadingTableScene",Width=scene.Geometry.Width*scale,Height=scene.Geometry.Height*scale,
            Background=ReaderPalette.ReadingPaper,ClipToBounds=true};
        canvas.Children.Add(new TablePicture(scene,scale){Width=canvas.Width,Height=canvas.Height,IsHitTestVisible=false});
        var cells=block.Children.SelectMany(row=>row.Children).ToDictionary(c=>c.Id,StringComparer.Ordinal);
        foreach(var cell in scene.Cells)
        {
            if(!cells.TryGetValue(cell.CellId,out var source))continue;
            var target=ReadingCell(source,content:null,cell.MergedSourceIds);
            target.Width=cell.Width*scale;target.Height=cell.Height*scale;
            Canvas.SetLeft(target,cell.X*scale);Canvas.SetTop(target,cell.Y*scale);
            canvas.Children.Add(target);
        }
        if(MarksRequested)
        {
            var marks=new PageMarksOverlay{Name="TableNonprintingMarks",Width=canvas.Width,Height=canvas.Height,IsHitTestVisible=false};
            var ends=scene.Lines.GroupBy(l=>l.ParagraphId).ToDictionary(g=>g.Key,g=>g.Last(),StringComparer.Ordinal);
            marks.Set(CollectPageMarks(scene,ends),readingZoom);
            canvas.Children.Add(marks);
        }
        return canvas;
    }

    Control ReadingTableGrid(ReadableBlock block,LayoutTable? layout,double scale)
    {
        var semantic=ReadingTableSnapshot?.Semantic?.Tables.FirstOrDefault(t=>t.Id==block.Id);
        var inventory=semantic?.Rows.SelectMany(r=>r.Cells).ToDictionary(c=>c.Id,StringComparer.Ordinal);
        var known=layout?.Cells.ToDictionary(c=>c.Id,StringComparer.Ordinal);
        var positions=new List<(ReadableBlock Cell,int Row,int Column,int Span,LayoutCell? Layout)>();
        var rows=block.Children;
        int count=layout?.Columns.Count??semantic?.GridColumns.Length??0;
        for(int row=0;row<rows.Count;row++)
        {
            int next=0;
            foreach(var cell in rows[row].Kind=="row"?rows[row].Children:[rows[row]])
            {
                var resolved=known?.GetValueOrDefault(cell.Id);
                var declared=inventory?.GetValueOrDefault(cell.Id);
                int column=resolved?.Column??(declared?.Column is >=0 and <128?declared.Column.Value:next);
                int span=resolved?.ColumnSpan??Math.Clamp(declared?.ColumnSpan??cell.ColumnSpan,1,64);
                positions.Add((cell,row,column,span,resolved));
                next=column+span;count=Math.Max(count,next);
            }
        }

        var grid=new Grid{Name="ReadingTableGrid"};
        for(int column=0;column<count;column++)
        {
            if(layout?.Columns.ElementAtOrDefault(column) is >0 and var width)
                grid.ColumnDefinitions.Add(new ColumnDefinition(width*scale,GridUnitType.Pixel));
            else if(semantic is not null&&column<semantic.GridColumns.Length&&
                ReadingTableSnapshot?.Semantic?.Attribute(semantic.GridColumns[column],"w") is {} raw&&
                int.TryParse(raw,out int twips)&&twips is >0 and <=28800)
                grid.ColumnDefinitions.Add(new ColumnDefinition(twips/20d*scale,GridUnitType.Pixel));
            else
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto){MinWidth=60*scale,MaxWidth=270*scale});
        }
        for(int row=0;row<rows.Count;row++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto){MinHeight=(layout?.Rows.ElementAtOrDefault(row)?.Height??0)*scale});

        var aliases=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        var spans=new Dictionary<string,(int Across,int Down)>(StringComparer.Ordinal);
        var original=positions.ToDictionary(p=>p.Cell.Id,StringComparer.Ordinal);
        foreach(var (cell,row,column,span,resolved) in positions)
        {
            var declared=inventory?.GetValueOrDefault(cell.Id);
            var origin=resolved?.MergeOrigin??
                (declared?.HorizontalOriginId is {} horizontal&&horizontal!=cell.Id?horizontal:declared?.VerticalOriginId);
            bool empty=cell.PlainText.Length==0&&
                !Descendants(cell).Any(c=>c.Kind is "image" or "note" or "field" or "revision" || c.ListLabel is not null);
            if(origin is null||origin==cell.Id||!empty||!original.TryGetValue(origin,out var start))continue;
            // A fallback can show an established rectangular fusion while retaining any
            // actual content independently when a continuation contains source material.
            var extent=spans.GetValueOrDefault(origin,(start.Span,1));
            if(row==start.Row&&column==start.Column+extent.Item1)extent.Item1+=span;
            else if(column==start.Column&&span==extent.Item1&&row==start.Row+extent.Item2)extent.Item2++;
            else continue;
            spans[origin]=extent;
            if(!aliases.TryGetValue(origin,out var ids))aliases.Add(origin,ids=[]);
            ids.Add(cell.Id);
        }
        var covered=aliases.Values.SelectMany(x=>x).ToHashSet(StringComparer.Ordinal);
        foreach(var (cell,row,column,span,resolved) in positions)
        {
            if(covered.Contains(cell.Id))continue;
            var group=new StackPanel();
            if(cell.Kind=="cell")
                foreach(var child in cell.Children)group.Children.Add(RenderReadingBlock(child,inCell:true));
            else group.Children.Add(RenderReadingBlock(cell,inCell:true));
            var target=ReadingCell(cell,group,aliases.GetValueOrDefault(cell.Id)??[]);
            var (across,down)=spans.GetValueOrDefault(cell.Id,(span,1));
            if(resolved is not null&&layout is not null)
            {
                var m=resolved.Margins;
                target.Padding=new Thickness(m.Left*scale,m.Top*scale,m.Right*scale,m.Bottom*scale);
                if(resolved.Fill is {} fill)target.Background=Brush("#"+fill);
                group.VerticalAlignment=resolved.VerticalAlignment switch
                {
                    "center"=>VerticalAlignment.Center,"bottom"=>VerticalAlignment.Bottom,_=>VerticalAlignment.Top
                };
            }
            Grid.SetRow(target,row);Grid.SetColumn(target,column);
            Grid.SetColumnSpan(target,across);Grid.SetRowSpan(target,down);
            grid.Children.Add(target);
            if(resolved is null||layout is null)continue;
            var borders=Composer.EffectiveBorders(layout,resolved with{ColumnSpan=across,RowSpan=down});
            var edges=new[]{borders.Top,borders.Right,borders.Bottom,borders.Left};
            if(edges.Any(b=>b is {Width:>0})&&edges.All(b=>b is null||b.Color.Length==6&&b.Color.All(Uri.IsHexDigit)&&
                b.Style is "single" or "dashed" or "dotted" or "double"))
            {
                var overlay=new CellBorderPicture(borders,(float)scale){IsHitTestVisible=false};
                Grid.SetRow(overlay,row);Grid.SetColumn(overlay,column);
                Grid.SetColumnSpan(overlay,across);Grid.SetRowSpan(overlay,down);
                grid.Children.Add(overlay);
            }
        }
        return grid;
    }

    TableCellTarget ReadingCell(ReadableBlock source,Control? content,IReadOnlyList<string> aliases)
    {
        var target=new TableCellTarget(source.Id,aliases){Child=content};
        AutomationProperties.SetName(target,T("Cellule de tableau","Table cell")+" · "+BlockText(source));
        AddCopyPassage(target,source);
        return target;
    }

    bool BringReadingCellIntoView(string tableId,string id,bool focus=false)
    {
        readingList.UpdateLayout();
        var table=readingList.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(v=>v.Name=="ReadingTable"&&v.Tag as string==tableId);
        var target=table?.GetVisualDescendants().OfType<TableCellTarget>().FirstOrDefault(c=>c.Contains(id));
        if(target is null)return false;
        target.BringIntoView();
        if(focus)target.Focus();
        return true;
    }

    sealed class TableCellTarget : Border
    {
        readonly IReadOnlyList<string> aliases;
        internal TableCellTarget(string id,IReadOnlyList<string> aliases)
        {
            this.aliases=aliases;
            Name="ReadingCell";Tag=id;Focusable=true;IsTabStop=false;Background=Brushes.Transparent;
        }
        internal bool Contains(string id)=>Tag as string==id||aliases.Contains(id,StringComparer.Ordinal);
    }

    sealed class CellBorderPicture(CellBorders borders,float scale) : Control
    {
        public override void Render(DrawingContext context)
        {
            if(Bounds.Width<=0||Bounds.Height<=0)return;
            context.Custom(new PictureDraw(new Rect(Bounds.Size),
                Composer.RecordCellBorders((float)Bounds.Width/scale,(float)Bounds.Height/scale,borders),scale));
        }
    }

    sealed class TablePicture(ComposedPage table,double scale) : Control
    {
        public override void Render(DrawingContext context)
        {
            context.Custom(new PictureDraw(new Rect(Bounds.Size),Composer.RecordContinuousTable(table),(float)scale));
        }
    }

    // Each draw command holds an immutable recording. Avalonia releases it on the render thread.
    sealed class PictureDraw(Rect bounds,SKPicture picture,float scale) : ICustomDrawOperation
    {
        public Rect Bounds=>bounds;
        public bool HitTest(Point p)=>false;
        public bool Equals(ICustomDrawOperation? other)=>ReferenceEquals(this,other);
        public void Render(ImmediateDrawingContext context)
        {
            using var lease=context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
            if(lease is null)return;
            var canvas=lease.SkCanvas;
            int saved=canvas.Save();
            try
            {
                canvas.Scale(scale);
                canvas.DrawPicture(picture);
            }
            finally{canvas.RestoreToCount(saved);}
        }
        public void Dispose()=>picture.Dispose();
    }
}
