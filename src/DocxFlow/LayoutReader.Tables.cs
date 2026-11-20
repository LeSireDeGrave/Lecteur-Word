using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        IEnumerable<InventoryNode> Elements(InventoryNode n)=>children[n.Id].Where(c=>!c.Name.StartsWith('#'));
        CellMargins Margins(InventoryNode? n,CellMargins inherited)
        {
            if(n is null)return inherited;WAttributes(n);var result=inherited;
            foreach(var c in Elements(n))
            {
                WAttributes(c,"type","w");if(A(c,"type") is not ("" or "dxa"))Issue("LAYOUT_CELL_MARGIN_UNIT_UNQUALIFIED",c,"Marge interne hors profil.");
                float value=Number(c,"w",0,0,144,20);
                if(Is(c,"top"))result=result with{Top=value};else if(Is(c,"bottom"))result=result with{Bottom=value};else if(Is(c,"left")||Is(c,"start"))result=result with{Left=value};else if(Is(c,"right")||Is(c,"end"))result=result with{Right=value};else Issue("LAYOUT_CELL_MARGIN_UNQUALIFIED",c,"Marge inconnue.");
            }
            return result;
        }
        CellBorders Borders(InventoryNode? n,CellBorders inherited)
        {
            if(n is null)return inherited;WAttributes(n);var result=inherited;
            foreach(var c in Elements(n))
            {
                if(Is(n,"tcBorders")&&(Is(c,"insideH")||Is(c,"insideV")))Issue("LAYOUT_CELL_INTERNAL_BORDER_UNQUALIFIED",c,"Bordure interne à une cellule hors profil.");
                WAttributes(c,"val","sz","color","space");string value=A(c,"val"),color=A(c,"color");if(color is "" or "auto")color="000000";
                if(color.Length!=6||!color.All(Uri.IsHexDigit))Issue("LAYOUT_BORDER_COLOR_UNQUALIFIED",c,"Couleur de bordure hors profil.");
                if(value is not ("single" or "nil" or "none")||A(c,"space") is not ("" or "0"))Issue("LAYOUT_BORDER_UNQUALIFIED",c,"Bordure ou espacement non qualifiés.");
                var border=new LayoutBorder(value is "nil" or "none"?0:Number(c,"sz",.5f,0,12,8),color);
                if(Is(c,"top"))result=result with{Top=border};else if(Is(c,"bottom"))result=result with{Bottom=border};else if(Is(c,"left")||Is(c,"start"))result=result with{Left=border};else if(Is(c,"right")||Is(c,"end"))result=result with{Right=border};else if(Is(c,"insideH"))result=result with{InsideH=border};else if(Is(c,"insideV"))result=result with{InsideV=border};else Issue("LAYOUT_BORDER_SIDE_UNQUALIFIED",c,"Bordure diagonale/inconnue hors profil.");
            }
            return result;
        }
        string? Fill(InventoryNode n)
        {
            WAttributes(n,"fill","color","val");if(A(n,"val") is not ("" or "clear" or "solid"))Issue("LAYOUT_SHADING_UNQUALIFIED",n,"Motif de trame hors profil.");
            string fill=A(n,"val")=="solid"?A(n,"color"):A(n,"fill");if(A(n,"val")=="solid"&&fill is "" or "auto")fill="000000";if(fill is "" or "auto")return null;if(fill.Length!=6||!fill.All(Uri.IsHexDigit)){Issue("LAYOUT_SHADING_UNQUALIFIED",n,"Couleur de fond hors profil.");return null;}return fill;
        }
        void ReadTable(InventoryNode table,int section,PageGeometry geometry,string start,List<LayoutParagraph> output)
        {
            token.ThrowIfCancellationRequested();WAttributes(table);
            if(tables.Count>=128){Issue("LAYOUT_TABLE_LIMIT",table,"Budget de tableaux dépassé.");return;}
            var grid=Child(table,"tblGrid");var columns=new List<float>();
            if(grid is not null)
            {
                WAttributes(grid);foreach(var c in Elements(grid))
                {if(!Is(c,"gridCol")){Issue("LAYOUT_GRID_UNQUALIFIED",c,"Grille hors profil.");continue;}WAttributes(c,"w");columns.Add(Number(c,"w",0,1,1440,20));}
            }
            if(columns.Count is <1 or >32||columns.Any(w=>w<=0)){Issue("LAYOUT_GRID_REQUIRED",table,"Une grille explicite de 1 à 32 colonnes positives est nécessaire.");return;}
            float natural=columns.Sum(),width=natural,indent=0;string alignment="left",layout="";var margins=new CellMargins();var borders=new CellBorders();string? tableFill=null;
            var properties=Child(table,"tblPr");if(properties is not null)
            {
                WAttributes(properties);foreach(var c in Elements(properties))
                {
                    if(Is(c,"tblW")){WAttributes(c,"type","w");string unit=A(c,"type");if(unit=="dxa")width=Number(c,"w",natural,1,1440,20);else if(unit=="pct")width=(geometry.Width-geometry.Left-geometry.Right)*Number(c,"w",5000,1,5000)/5000;else if(unit is not ("auto" or "nil" or ""))Issue("LAYOUT_TABLE_WIDTH_UNQUALIFIED",c,"Largeur de tableau hors profil.");}
                    else if(Is(c,"tblInd")){WAttributes(c,"type","w");if(A(c,"type") is not ("" or "dxa"))Issue("LAYOUT_TABLE_INDENT_UNQUALIFIED",c,"Retrait hors profil.");indent=Number(c,"w",0,0,720,20);}
                    else if(Is(c,"jc")){WAttributes(c,"val");alignment=A(c,"val");if(alignment is not ("left" or "center" or "right"))Issue("LAYOUT_TABLE_ALIGNMENT_UNQUALIFIED",c,"Alignement hors profil.");}
                    else if(Is(c,"tblBorders"))borders=Borders(c,borders);
                    else if(Is(c,"tblCellMar"))margins=Margins(c,margins);
                    else if(Is(c,"shd"))tableFill=Fill(c);
                    else if(Is(c,"tblLayout")){WAttributes(c,"type");layout=A(c,"type");if(layout!="fixed")Issue("LAYOUT_TABLE_AUTOFIT_UNQUALIFIED",c,"AutoFit explicite non qualifié.");}
                    else Issue("LAYOUT_TABLE_PROPERTY_UNQUALIFIED",c,"Propriété de tableau non qualifiée : "+c.Name);
                }
            }
            if(layout.Length==0)Issue("LAYOUT_TABLE_GRID_PREVIEW",table,"Grille explicite utilisée à largeur calculée ; AutoFit Word non reproduit.",false);
            if(width+indent>geometry.Width-geometry.Left-geometry.Right+.5f){Issue("LAYOUT_TABLE_TOO_WIDE",table,"Tableau plus large que la zone de texte.");return;}
            if(alignment!="left"&&indent!=0)Issue("LAYOUT_TABLE_ALIGNMENT_INDENT_UNQUALIFIED",table,"Retrait combiné à un alignement non gauche hors profil.");
            float ratio=width/natural;var scaled=columns.Select(w=>w*ratio).ToArray();
            var rows=new List<LayoutRow>();var cells=new List<LayoutCell>();var active=new Dictionary<int,LayoutCell>();bool bodySeen=false;
            // Sentinel preserves the table's exact position among body paragraphs.
            output.Add(new(table.Id,table.Source,[],new(),section,geometry,start){TableId=table.Id});
            foreach(var row in Elements(table).Where(c=>!Is(c,"tblPr")&&!Is(c,"tblGrid")))
            {
                token.ThrowIfCancellationRequested();if(!Is(row,"tr")){Issue("LAYOUT_TABLE_BLOCK_UNQUALIFIED",row,"Bloc de tableau hors profil.");continue;}
                if(rows.Count>=2048){Issue("LAYOUT_TABLE_ROW_LIMIT",row,"Budget de lignes dépassé.");break;}
                WAttributes(row,"rsidR","rsidRPr","rsidTr");float rowHeight=0;string rule="atLeast";bool header=false;
                if(Child(row,"trPr") is {} rp)
                {
                    WAttributes(rp);foreach(var c in Elements(rp))
                    {
                        if(Is(c,"trHeight")){WAttributes(c,"val","hRule");rowHeight=Number(c,"val",0,0,1440,20);rule=A(c,"hRule");if(rule=="")rule="atLeast";if(rule is not ("auto" or "atLeast" or "exact"))Issue("LAYOUT_ROW_HEIGHT_UNQUALIFIED",c,"Hauteur de ligne hors profil.");}
                        else if(Is(c,"tblHeader")){WAttributes(c,"val");header=Toggle(c);}
                        else if(Is(c,"cantSplit")){WAttributes(c,"val");}
                        else Issue("LAYOUT_ROW_PROPERTY_UNQUALIFIED",c,"Propriété de ligne hors profil.");
                    }
                }
                if(header&&bodySeen)Issue("LAYOUT_HEADER_NONCONTIGUOUS",row,"Les en-têtes doivent former un préfixe contigu.");if(!header)bodySeen=true;
                if(rule=="auto")rowHeight=0;
                int rowIndex=rows.Count;rows.Add(new(rowIndex,rowHeight,rule,header));int column=0;var nextActive=new Dictionary<int,LayoutCell>();
                foreach(var cell in Elements(row).Where(c=>!Is(c,"trPr")))
                {
                    if(!Is(cell,"tc")){Issue("LAYOUT_ROW_CONTENT_UNQUALIFIED",cell,"Contenu de ligne hors profil.");continue;}WAttributes(cell,"id");
                    int span=1;string? merge=null;var cellMargins=margins;var cellBorders=new CellBorders();string valign="top";string? fill=tableFill;float? declaredWidth=null;
                    if(Child(cell,"tcPr") is {} cp)
                    {
                        WAttributes(cp);foreach(var c in Elements(cp))
                        {
                            if(Is(c,"gridSpan")){WAttributes(c,"val");span=(int)Number(c,"val",1,1,32);}
                            else if(Is(c,"vMerge")){WAttributes(c,"val");merge=A(c,"val");if(merge.Length==0)merge="continue";if(merge is not ("restart" or "continue"))Issue("LAYOUT_VERTICAL_MERGE_UNQUALIFIED",c,"Fusion verticale inconnue.");}
                            else if(Is(c,"tcW")){WAttributes(c,"type","w");if(A(c,"type")=="dxa")declaredWidth=Number(c,"w",0,0,1440,20);else if(A(c,"type") is not ("" or "auto"))Issue("LAYOUT_CELL_WIDTH_UNQUALIFIED",c,"Largeur de cellule hors profil.");}
                            else if(Is(c,"tcMar"))cellMargins=Margins(c,cellMargins);
                            else if(Is(c,"tcBorders"))cellBorders=Borders(c,cellBorders);
                            else if(Is(c,"vAlign")){WAttributes(c,"val");valign=A(c,"val");if(valign is not ("top" or "center" or "bottom"))Issue("LAYOUT_CELL_ALIGNMENT_UNQUALIFIED",c,"Alignement vertical hors profil.");}
                            else if(Is(c,"shd"))fill=Fill(c);
                            else Issue("LAYOUT_CELL_PROPERTY_UNQUALIFIED",c,"Propriété de cellule non qualifiée : "+c.Name);
                        }
                    }
                    if(column+span>scaled.Length){Issue("LAYOUT_GRID_SPAN_INVALID",cell,"Cellule hors grille.");continue;}
                    float cellWidth=scaled.Skip(column).Take(span).Sum();if(declaredWidth.HasValue&&Math.Abs(declaredWidth.Value*ratio-cellWidth)>.5f)Issue("LAYOUT_CELL_WIDTH_CONFLICT",cell,"Largeur de cellule incompatible avec la grille.");
                    if(cellWidth-cellMargins.Left-cellMargins.Right<12)Issue("LAYOUT_CELL_BODY_INVALID",cell,"Zone de cellule trop étroite.");
                    var paragraphIds=new List<string>();
                    foreach(var block in Elements(cell).Where(c=>!Is(c,"tcPr")))
                    {
                        if(!Is(block,"p")){Issue(Is(block,"tbl")?"LAYOUT_NESTED_TABLE_UNQUALIFIED":"LAYOUT_CELL_BLOCK_UNQUALIFIED",block,"Bloc imbriqué non qualifié ; aucune mise à plat.");continue;}
                        var p=ReadParagraph(block,section,new(cellWidth,geometry.Height,cellMargins.Left,0,cellMargins.Right,0),start) with{CellOwner=cell.Id};
                        if(p.Properties.KeepNext)Issue("LAYOUT_CELL_KEEP_NEXT_UNQUALIFIED",block,"Solidarité avec le paragraphe suivant dans/au-delà des cellules à qualifier séparément.");
                        if(p.Properties.PageBreakBefore||p.Runs.Any(r=>r.Kind=="pageBreak")||Child(Child(block,"pPr")??block,"sectPr") is not null)Issue("LAYOUT_CELL_BREAK_UNQUALIFIED",block,"Saut de page/section interne à une cellule hors profil.");
                        paragraphIds.Add(p.Id);output.Add(p);
                    }
                    if(paragraphIds.Count==0)Issue("LAYOUT_CELL_PARAGRAPH_MISSING",cell,"Cellule sans paragraphe qualifié.");
                    var model=new LayoutCell(cell.Id,cell.Source,rowIndex,column,span,1,null,cellMargins,cellBorders,valign,fill,paragraphIds);
                    if(merge=="continue")
                    {
                        if(!active.TryGetValue(column,out var origin)||origin.ColumnSpan!=span)Issue("LAYOUT_VERTICAL_MERGE_INVALID",cell,"Fusion verticale sans origine compatible.");
                        else
                        {
                            if(output.Where(p=>paragraphIds.Contains(p.Id)).Any(p=>p.Text.Length>0||p.Runs.Any(r=>r.Image is not null||r.Kind=="listMarker")))Issue("LAYOUT_VERTICAL_MERGE_CONTENT",cell,"Contenu dans une cellule de continuation : refus plutôt que perte.");
                            if(origin.Margins!=cellMargins||origin.Borders!=cellBorders||origin.Fill!=fill||origin.VerticalAlignment!=valign)Issue("LAYOUT_VERTICAL_MERGE_STYLE",cell,"Propriétés différentes dans une fusion verticale hors profil.");
                            model=model with{MergeOrigin=origin.Id};var updated=origin with{RowSpan=rowIndex-origin.Row+1};int index=cells.FindIndex(c=>c.Id==origin.Id);cells[index]=updated;nextActive[column]=updated;
                        }
                    }
                    else if(merge=="restart")nextActive[column]=model;
                    cells.Add(model);column+=span;
                }
                if(column!=scaled.Length)Issue("LAYOUT_GRID_ROW_INVALID",row,"La ligne ne couvre pas exactement la grille.");active=nextActive;
            }
            if(rows.Count==0)Issue("LAYOUT_TABLE_EMPTY",table,"Tableau sans ligne.");
            foreach(var c in cells.Where(c=>c.MergeOrigin is null&&c.RowSpan>1))if(rows.Skip(c.Row).Take(c.RowSpan).Select(r=>r.Header).Distinct().Count()>1)Issue("LAYOUT_HEADER_MERGE_UNQUALIFIED",table,"Fusion traversant la limite d’en-tête non qualifiée.");
            tables.Add(new(table.Id,table.Source,scaled,rows,cells,alignment,indent){Borders=borders});
            Issue("LAYOUT_ATOMIC_TABLE_ROWS",table,"Lignes et groupes fusionnés conservés entiers ; partage interne d’une ligne non qualifié.",false);
        }
    }
}
