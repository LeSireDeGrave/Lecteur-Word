using System.Collections.Immutable;

namespace LecteurWord.Flow;

public static partial class Composer
{
    sealed record CellWidthConstraint(LayoutCell Cell,float? Minimum,float? IntrinsicPreferred);

    static (LayoutTable Table,float[] Columns) SolveTableWidths(LayoutTable table,
        IReadOnlyDictionary<string,LayoutParagraph> paragraphs,Func<LayoutParagraph,(float Minimum,float Preferred)> intrinsic,Action check)
    {
        const float epsilon=.025f; // Half a twip.
        const int maxCells=8192;
        if(table.Cells.Count>maxCells||table.Columns.Count is <1 or >32)throw new LayoutFailure("PAGE_TABLE_CONSTRAINT_LIMIT",table.Source);
        var page=table.PhysicalPage??throw new LayoutFailure("PAGE_TABLE_REGION",table.Source);
        float body=page.Width-page.Left-page.Right;
        float available=table.AvailableWidth;
        if(!float.IsFinite(available)||available<=0)throw new LayoutFailure("PAGE_TABLE_REGION",table.Source);
        int count=table.Columns.Count;
        var grid=table.Columns.ToArray();
        var constraints=new List<CellWidthConstraint>();
        table=table with {Cells=table.Cells.Select(ReserveBorder).ToImmutableArray()};
        foreach(var cell in table.Cells)
        {
            check();
            if(cell.Column<0||cell.ColumnSpan<1||cell.Column>count-cell.ColumnSpan)
                throw new LayoutFailure("PAGE_TABLE_GRID_RANGE",cell.Source);
            float minimum=1,preferred=1;
            bool known=true;
            foreach(string id in cell.ParagraphIds)
            {
                check();
                try
                {
                    var size=intrinsic(paragraphs[id]);
                    minimum=Math.Max(minimum,size.Minimum);
                    preferred=Math.Max(preferred,size.Preferred);
                }
                catch(InvalidDataException e) when(e.Message is "PAGE_AUTOFIT_SOURCE" or "PAGE_AUTOFIT_UNMEASURED" or "PAGE_AUTOFIT_INLINE_PENDING")
                {
                    if(table.AutoFit)throw new LayoutFailure(e.Message,cell.Source);
                    known=false;
                }
            }
            float padding=cell.Margins.Left+cell.Margins.Right;
            constraints.Add(new(cell,known?minimum+padding:null,known?preferred+padding:null));
        }
        var lower=MinimumColumns();
        var exactPreference=table.NativeWidth?.At(body);
        float minTotal=lower.Sum();
        float preferredTotal;
        float[] chosen;
        if(table.AutoFit)
        {
            if(minTotal>available+epsilon)
                throw new LayoutFailure("LAYOUT_AUTOFIT_MINIMUM_TOO_WIDE",table.Source,
                    FormattableString.Invariant($"Minimum {minTotal:F2} pt, région {available:F2} pt."));
            var ordered=constraints.OrderBy(c=>c.Cell.ColumnSpan).ToArray();
            var natural=lower.ToArray();
            foreach(var c in ordered)
                Grow(natural,c.Cell,c.Cell.NativeWidth is {Unit:"dxa"} preference?
                    Math.Max(preference.Value,c.IntrinsicPreferred??0):c.IntrinsicPreferred??0);
            preferredTotal=exactPreference??natural.Sum();
            float target=Math.Clamp(preferredTotal,minTotal,Math.Max(minTotal,available));
            var desired=lower.ToArray();
            foreach(var c in ordered)
                Grow(desired,c.Cell,c.Cell.NativeWidth?.At(target)??c.IntrinsicPreferred??0);
            chosen=lower;
            float extra=target-minTotal,capacity=desired.Sum()-minTotal;
            if(extra>0&&capacity>epsilon)
                for(int i=0;i<count;i++)chosen[i]+=Math.Min(extra,capacity)*(desired[i]-chosen[i])/capacity;
            FillRemainder(chosen,target);
        }
        else
        {
            // A saved grid agreeing with the declared table width supplies the fixed geometry.
            // With an automatic table width the grid can grow to its cells' explicit preferences.
            chosen=grid.Select(w=>Math.Max(1,w)).ToArray();
            bool saved=grid.All(w=>w>0)&&exactPreference is {} target&&Math.Abs(grid.Sum()-target)<=epsilon;
            if(!saved)
            {
                float percentBasis=exactPreference??available;
                foreach(var c in constraints)
                {
                    check();
                    if(c.Cell.NativeWidth is not {} pref)continue;
                    if(exactPreference is null&&pref.Unit=="pct")
                    {
                        if(grid.Any(w=>w<=0))throw new LayoutFailure("PAGE_TABLE_FIXED_PERCENT_BASIS",pref.Source);
                        continue;
                    }
                    Grow(chosen,c.Cell,pref.At(percentBasis));
                }
                if(exactPreference is {} width)
                {
                    float sum=chosen.Sum();
                    if(width<count)throw new LayoutFailure("PAGE_TABLE_FIXED_WIDTH",table.Source);
                    if(sum>width+epsilon)
                        for(int i=0;i<count;i++)chosen[i]=chosen[i]*width/sum;
                    else FillRemainder(chosen,width);
                }
                else if(grid.Select((w,i)=>(w,i)).Any(x=>x.w<=0&&!constraints.Any(c=>
                    c.Cell.NativeWidth is {Unit:"dxa",Value:>0}&&c.Cell.Column<=x.i&&x.i<c.Cell.Column+c.Cell.ColumnSpan)))
                    throw new LayoutFailure("PAGE_TABLE_FIXED_GRID_EVIDENCE",table.Source);
            }
            preferredTotal=exactPreference??chosen.Sum();
        }
        float final=chosen.Sum();
        float left=TableLeft(table,page,final);
        bool overhang=left<page.Left-epsilon||left+final>page.Width-page.Right+epsilon;
        float border=AllBorders(table.Borders).Concat(table.Cells.SelectMany(c=>AllBorders(c.Borders))).Max(b=>b?.Width??0)/2;
        if(chosen.Any(w=>!float.IsFinite(w)||w<=0)||left-border<-.01f||left+final+border>page.Width+.01f)
            throw new LayoutFailure("PAGE_TABLE_OUTSIDE_PAPER",table.Source,
                FormattableString.Invariant($"Tableau {final:F2} pt, origine {left:F2} pt, papier {page.Width:F2} pt."));
        var results=ImmutableArray.CreateBuilder<TableCellWidthResult>();
        foreach(var c in constraints)
        {
            check();
            var cell=c.Cell;
            float width=Span(chosen,cell);
            if(width<cell.Margins.Left+cell.Margins.Right+1-epsilon)
                throw new LayoutFailure("LAYOUT_CELL_BODY_INVALID",cell.Source);
            if(c.Minimum is {} minimum&&width<minimum-epsilon)
                throw new LayoutFailure("PAGE_TABLE_FIXED_CONTENT_TOO_WIDE",cell.Source,
                    FormattableString.Invariant($"Cellule {width:F2} pt, minimum {minimum:F2} pt."));
            results.Add(new(cell.Id,c.Minimum,cell.NativeWidth?.At(final)??c.IntrinsicPreferred,width,cell.Source));
        }
        bool verified=constraints.All(c=>c.Minimum is not null);
        return (table with {WidthResult=new(verified?minTotal:null,preferredTotal,final,overhang,verified,results.ToImmutable())},chosen);

        LayoutCell ReserveBorder(LayoutCell cell)
        {
            check();
            var b=EffectiveBorders(table,cell);
            return cell with {Margins=new(Math.Max(cell.Margins.Top,(b.Top?.Width??0)/2),Math.Max(cell.Margins.Right,(b.Right?.Width??0)/2),
                Math.Max(cell.Margins.Bottom,(b.Bottom?.Width??0)/2),Math.Max(cell.Margins.Left,(b.Left?.Width??0)/2))};
        }
        float[] MinimumColumns()
        {
            // The constraints on contiguous spans form a forward graph over column boundaries.
            // The longest feasible prefix lower bounds give the minimum total without double counting overlaps.
            var byStart=constraints.ToLookup(c=>c.Cell.Column);
            var prefix=new double[count+1];
            for(int i=0;i<count;i++)
            {
                check();
                prefix[i+1]=Math.Max(prefix[i+1],prefix[i]+1);
                foreach(var c in byStart[i])
                {
                    check();
                    int end=i+c.Cell.ColumnSpan;
                    prefix[end]=Math.Max(prefix[end],prefix[i]+(c.Minimum??(c.Cell.Margins.Left+c.Cell.Margins.Right+1)));
                }
            }
            return Enumerable.Range(0,count).Select(i=>(float)(prefix[i+1]-prefix[i])).ToArray();
        }
        void Grow(float[] widths,LayoutCell cell,float goal)
        {
            check();
            float extra=goal-Span(widths,cell);
            if(extra<=epsilon)return;
            float weight=Span(grid,cell);
            for(int col=cell.Column;col<cell.Column+cell.ColumnSpan;col++)
                widths[col]+=extra*(weight>0?grid[col]/weight:1f/cell.ColumnSpan);
        }
        void FillRemainder(float[] widths,float target)
        {
            float extra=target-widths.Sum();
            if(extra<=epsilon)return;
            float weight=grid.Sum();
            for(int i=0;i<count;i++)widths[i]+=extra*(weight>0?grid[i]/weight:1f/count);
        }
    }

    static float Span(float[] widths,LayoutCell cell)
    {
        float sum=0;
        foreach(float width in widths.AsSpan(cell.Column,cell.ColumnSpan))sum+=width;
        return sum;
    }
    static LayoutBorder?[] AllBorders(CellBorders b)=>[b.Top,b.Right,b.Bottom,b.Left,b.InsideH,b.InsideV];
    static float TableLeft(LayoutTable table,PageGeometry geometry,float width)=>table.Alignment switch
    {
        "center"=>geometry.Left+table.Indent+(geometry.Width-geometry.Left-geometry.Right-width-table.Indent)/2,
        "right"=>geometry.Width-geometry.Right-width,
        _=>geometry.Left+table.Indent
    };
}
