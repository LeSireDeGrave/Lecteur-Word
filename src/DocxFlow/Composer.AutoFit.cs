using System.Text.RegularExpressions;
using System.Globalization;

namespace LecteurWord.Flow;

public static partial class Composer
{
    static PaginatedDocument FitTables(PaginatedDocument document,LayoutFonts fonts,Action check,
        Func<LayoutParagraph,(float Minimum,float Preferred)>? intrinsic=null)
    {
        var paragraphs=document.Paragraphs.ToDictionary(p=>p.Id);var tables=new List<LayoutTable>();var issues=document.Issues.ToList();
        foreach(var table in document.Tables)
        {
            check();
            if(intrinsic is not null)
            {
                var solved=SolveTableWidths(table,paragraphs,intrinsic,check);
                foreach(var cell in solved.Table.Cells)
                {
                    check();
                    float width=Span(solved.Columns,cell);
                    foreach(string id in cell.ParagraphIds)
                    {
                        var p=paragraphs[id];
                        paragraphs[id]=p with{Geometry=p.Geometry with{Width=width,Left=cell.Margins.Left,Right=cell.Margins.Right},
                            CenteredImageOverhang=CenteredCellImageOverhang(solved.Table,cell,p)};
                    }
                }
                tables.Add(solved.Table with{Columns=solved.Columns});
                continue;
            }
            if(!table.AutoFit){tables.Add(table);continue;}
            int count=table.Columns.Count;var minimum=new float[count];var desired=new float[count];
            void Constrain(float[] sizes,int start,int span,float width)
            {
                float deficit=width-sizes.Skip(start).Take(span).Sum();
                if(deficit>0)for(int i=start;i<start+span;i++)sizes[i]+=deficit/span;
            }
            foreach(var cell in table.Cells.OrderBy(c=>c.ColumnSpan))
            {
                check();float min=12,max=12;
                foreach(string id in cell.ParagraphIds)
                {
                    var p=paragraphs[id];
                    var size=IntrinsicLegacy(p,fonts,check);
                    min=Math.Max(min,size.Minimum);max=Math.Max(max,size.Preferred);
                }
                float margins=cell.Margins.Left+cell.Margins.Right;
                Constrain(minimum,cell.Column,cell.ColumnSpan,min+margins);
                Constrain(desired,cell.Column,cell.ColumnSpan,Math.Max(max+margins,cell.PreferredWidth??0));
            }
            for(int i=0;i<count;i++){minimum[i]=Math.Max(12,minimum[i]);desired[i]=Math.Max(minimum[i],desired[i]);}
            float minSum=minimum.Sum(),maxSum=desired.Sum(),limit=table.AvailableWidth;
            if(!float.IsFinite(limit)||limit<12||minSum>limit+.01f)throw new LayoutFailure("LAYOUT_AUTOFIT_MINIMUM_TOO_WIDE",table.Source,FormattableString.Invariant($"Minimum mesuré {minSum:F2} pt ; largeur disponible {limit:F2} pt."));
            float target=Math.Min(limit,Math.Max(minSum,table.PreferredWidth??maxSum));var columns=minimum.ToArray();
            float extra=target-minSum,capacity=maxSum-minSum;
            for(int i=0;i<count;i++)columns[i]+=capacity>.001f?Math.Min(extra,capacity)*(desired[i]-minimum[i])/capacity:0;
            float remainder=target-columns.Sum();if(remainder>.001f)for(int i=0;i<count;i++)columns[i]+=remainder*table.Columns[i]/table.Columns.Sum();
            foreach(var cell in table.Cells)
            {
                float width=columns.Skip(cell.Column).Take(cell.ColumnSpan).Sum();
                if(width-cell.Margins.Left-cell.Margins.Right<12-.01f)throw new LayoutFailure("LAYOUT_CELL_BODY_INVALID",cell.Source);
                foreach(string id in cell.ParagraphIds){var p=paragraphs[id];paragraphs[id]=p with{Geometry=p.Geometry with{Width=width}};}
            }
            tables.Add(table with{Columns=columns});
            issues.Add(new("LAYOUT_AUTOFIT_MEASURED",table.Source,"AutoFit simple expérimental : colonnes mesurées (pt) "+string.Join(", ",columns.Select(w=>w.ToString("F2",CultureInfo.InvariantCulture)))+" ; préférences arbitrées dans la largeur disponible. Algorithme Word complet non qualifié.",false));
        }
        return document with{Tables=tables,Paragraphs=document.Paragraphs.Select(p=>paragraphs[p.Id]).ToArray(),Issues=issues};
    }

    static (float Minimum,float Preferred) IntrinsicLegacy(LayoutParagraph p,LayoutFonts fonts,Action check)
    {
        float line=0,word=0,longestWord=0,longestLine=0;
        foreach(var run in p.Runs.Where(r=>r.Kind is not ("paragraphMark" or "listMarker")))
        {
            check();
            if(run.Kind=="tab")throw new LayoutFailure("LAYOUT_AUTOFIT_TAB_UNQUALIFIED",run.Source);
            if(run.Kind is "lineBreak" or "pageBreak"){longestLine=Math.Max(longestLine,line);longestWord=Math.Max(longestWord,word);line=word=0;continue;}
            if(run.Kind=="image"){longestWord=Math.Max(longestWord,Math.Max(word,run.Image!.Width));word=0;line+=run.Image!.Width;continue;}
            var font=fonts.Get(run.Style);line+=font.MeasureText(run.Text);
            foreach(string text in Regex.Split(run.Text,"( )"))
            {
                check();if(text==" "){longestWord=Math.Max(longestWord,word);word=0;}else word+=font.MeasureText(text);
            }
        }
        float inset=p.Properties.Left+p.Properties.Right+Math.Max(0,p.Properties.FirstLine);

        return (Math.Max(longestWord,word)+inset,Math.Max(longestLine,line)+inset);
    }
}
