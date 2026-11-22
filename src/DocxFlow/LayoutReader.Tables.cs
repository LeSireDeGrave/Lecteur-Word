using LecteurWord.DocxCore;
using System.Globalization;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        IReadOnlyList<InventoryNode> TableStyles(InventoryNode table,InventoryNode? properties)
        {
            if(tableCascade is not null)return tableCascade.Table(table.Id).Select(id=>nodes[id]).ToArray();
            string id=properties is not null&&Child(properties,"tblStyle") is {} reference?A(reference,"val"):styles.Values.FirstOrDefault(n=>A(n,"type")=="table"&&A(n,"default") is "1" or "true") is {} ds?A(ds,"styleId"):"";
            var result=Chain(id);
            foreach(var style in result)
            {
                if(A(style,"type")!="table")Issue("LAYOUT_TABLE_STYLE_TYPE",style,"Style de tableau référencé de type incompatible.");
                foreach(var conditional in Elements(style).Where(n=>Is(n,"tblStylePr")))
                    if(Elements(conditional).Any())Issue("LAYOUT_CONDITIONAL_TABLE_STYLE_UNQUALIFIED",conditional,"Style conditionnel de tableau non qualifié ; aucune suppression silencieuse des régions.");
                if(Child(style,"pPr") is {} pp&&Child(pp,"rPr") is {} mark)Issue("LAYOUT_TABLE_MARK_STYLE_UNQUALIFIED",mark,"Style de marque de paragraphe provenant du tableau hors profil.");
                if(Child(style,"trPr") is {} rowStyle&&Elements(rowStyle).Any())Issue("LAYOUT_TABLE_ROW_STYLE_UNQUALIFIED",rowStyle,"Propriétés de ligne provenant du style de tableau hors profil.");
            }
            return result;
        }
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
                if(tableCascade is null&&Is(n,"tcBorders")&&(Is(c,"insideH")||Is(c,"insideV")))Issue("LAYOUT_CELL_INTERNAL_BORDER_UNQUALIFIED",c,"Bordure interne à une cellule hors profil.");
                if(tableCascade is null)WAttributes(c,"val","sz","color","space");
                else WAttributes(c,"val","sz","color","space","themeColor","themeTint","themeShade");
                string value=A(c,"val"),color=tableCascade is null?A(c,"color"):ReadColor(c,"color");
                if(color is "" or "auto")color="000000";
                if(color.Length!=6||!color.All(Uri.IsHexDigit))Issue("LAYOUT_BORDER_COLOR_UNQUALIFIED",c,"Couleur de bordure hors profil.");
                if(value is not ("single" or "double" or "dotted" or "dashed" or "nil" or "none")||A(c,"space") is not ("" or "0"))Issue("LAYOUT_BORDER_UNQUALIFIED",c,"Bordure ou espacement non qualifiés.");
                var border=new LayoutBorder(value is "nil" or "none"?0:Number(c,"sz",.5f,0,12,8),color,value is "nil" or "none"?"single":value);
                if(Is(c,"top"))result=result with{Top=border};else if(Is(c,"bottom"))result=result with{Bottom=border};else if(Is(c,"left")||Is(c,"start"))result=result with{Left=border};else if(Is(c,"right")||Is(c,"end"))result=result with{Right=border};else if(Is(c,"insideH"))result=result with{InsideH=border};else if(Is(c,"insideV"))result=result with{InsideV=border};else Issue("LAYOUT_BORDER_SIDE_UNQUALIFIED",c,"Bordure diagonale/inconnue hors profil.");
            }
            return result;
        }
        string? Fill(InventoryNode n)
        {
            if(tableCascade is null)WAttributes(n,"fill","color","val");
            else WAttributes(n,"fill","color","val","themeColor","themeTint","themeShade","themeFill","themeFillTint","themeFillShade");
            string pattern=A(n,"val");
            if(pattern=="nil")return null;
            if(pattern is not ("" or "clear" or "solid"))Issue("LAYOUT_SHADING_UNQUALIFIED",n,"Motif de trame hors profil.");
            bool solid=pattern=="solid";
            string fill=tableCascade is null?A(n,solid?"color":"fill"):solid?ReadColor(n,"color"):
                ReadColor(n,"fill","themeFill","themeFillTint","themeFillShade",automatic:"");
            if(solid&&fill is "" or "auto")fill="000000";
            if(fill is "" or "auto")return null;
            if(fill.Length!=6||!fill.All(Uri.IsHexDigit)){Issue("LAYOUT_SHADING_UNQUALIFIED",n,"Couleur de fond hors profil.");return null;}
            return fill;
        }
        void ReadTable(InventoryNode table,int section,PageGeometry geometry,string start,List<LayoutParagraph> output)
        {
            token.ThrowIfCancellationRequested();WAttributes(table);
            if(tables.Count>=128){Issue("LAYOUT_TABLE_LIMIT",table,"Budget de tableaux dépassé.");return;}
            var properties=Child(table,"tblPr");var tableStyles=TableStyles(table,properties);
            var propertySets=tableStyles.Select(s=>Child(s,"tblPr")).Append(properties).Where(p=>p is not null).Cast<InventoryNode>().ToArray();
            string declaredLayout=propertySets.SelectMany(Elements).LastOrDefault(n=>Is(n,"tblLayout")) is {} dl?A(dl,"type"):"";
            bool autoFit=declaredLayout is "" or "autofit";
            var grid=Child(table,"tblGrid");var columns=new List<float>();
            if(grid is not null)
            {
                WAttributes(grid);foreach(var c in Elements(grid))
                {if(!Is(c,"gridCol")){Issue("LAYOUT_GRID_UNQUALIFIED",c,"Grille hors profil.");continue;}WAttributes(c,"w");columns.Add(Number(c,"w",0,autoFit||tableCascade is not null?0:1,1440,20));}
            }
            if(tableCascade is not null)
            {
                int required=0;
                foreach(var row in Elements(table).Where(r=>Is(r,"tr")))
                {
                    token.ThrowIfCancellationRequested();
                    int count=GridOmissions(row,"gridBefore")+GridOmissions(row,"gridAfter");
                    foreach(var cell in Elements(row).Where(c=>Is(c,"tc")))
                    {
                        if(count>32)break;
                        count+=Child(cell,"tcPr") is {} pr&&Child(pr,"gridSpan") is {} span?(int)Number(span,"val",1,1,32):1;
                    }
                    required=Math.Max(required,count);
                }
                if(required>32){Issue("PAGE_TABLE_COLUMN_LIMIT",table,"Budget de 32 colonnes dépassé.");return;}
                if(required>columns.Count)
                {
                    Issue("PAGE_TABLE_GRID_EXTENDED",table,"Grille complétée par les positions et fusions déclarées.",false);
                    while(columns.Count<required)columns.Add(0);
                }
            }
            else if(autoFit&&columns.Count==0)
            {
                var first=Elements(table).FirstOrDefault(n=>Is(n,"tr"));
                if(first is not null)foreach(var cell in Elements(first).Where(n=>Is(n,"tc")))
                {var cp=Child(cell,"tcPr");var span=cp is null?null:Child(cp,"gridSpan");int count=span is null?1:(int)Number(span,"val",1,1,32);for(int i=0;i<count&&columns.Count<=32;i++)columns.Add(0);}
            }
            if(tableCascade is null&&autoFit&&columns.Count is >0 and <=32)for(int i=0;i<columns.Count;i++)if(columns[i]<=0)columns[i]=(geometry.Width-geometry.Left-geometry.Right)/columns.Count;
            if(columns.Count is <1 or >32||tableCascade is null&&columns.Any(w=>w<=0)){Issue("LAYOUT_GRID_REQUIRED",table,"Une grille explicite de 1 à 32 colonnes positives est nécessaire.");return;}
            float natural=columns.Sum(),width=natural,indent=0;float? preferredWidth=null;string alignment="left";var margins=new CellMargins();var borders=new CellBorders();string? tableFill=null;
            TableWidthPreference? nativeWidth=null;
            var tableParagraph=paragraphDefaults;var tableCharacter=defaults;
            foreach(var style in tableStyles){tableParagraph=Paragraph(Child(style,"pPr"),tableParagraph);tableCharacter=Character(Child(style,"rPr"),tableCharacter,true);}
            foreach(var propertySet in propertySets)
            {
                WAttributes(propertySet);foreach(var c in Elements(propertySet))
                {
                    if(Is(c,"tblW")){WAttributes(c,"type","w");string unit=A(c,"type");if(tableCascade is not null)nativeWidth=WidthPreference(c);else if(unit=="dxa")preferredWidth=width=Number(c,"w",natural,1,1440,20);else if(unit=="pct")preferredWidth=width=(geometry.Width-geometry.Left-geometry.Right)*Number(c,"w",5000,1,5000)/5000;else if(unit is "auto" or "nil" or "")preferredWidth=null;else Issue("LAYOUT_TABLE_WIDTH_UNQUALIFIED",c,"Largeur de tableau hors profil.");}
                    else if(Is(c,"tblInd")){WAttributes(c,"type","w");if(A(c,"type") is not ("" or "dxa"))Issue("LAYOUT_TABLE_INDENT_UNQUALIFIED",c,"Retrait hors profil.");indent=Number(c,"w",0,tableCascade is null?0:-720,720,20);}
                    else if(Is(c,"jc")){WAttributes(c,"val");alignment=A(c,"val");if(alignment is not ("left" or "center" or "right"))Issue("LAYOUT_TABLE_ALIGNMENT_UNQUALIFIED",c,"Alignement hors profil.");}
                    else if(Is(c,"tblBorders"))borders=Borders(c,borders);
                    else if(Is(c,"tblCellMar"))margins=Margins(c,margins);
                    else if(Is(c,"shd"))tableFill=Fill(c);
                    else if(Is(c,"tblLayout")){WAttributes(c,"type");if(A(c,"type") is not ("fixed" or "autofit"))Issue("LAYOUT_TABLE_AUTOFIT_UNQUALIFIED",c,"Mode de disposition de tableau inconnu.");}
                    else if(tableCascade is not null&&Is(c,"tblCellSpacing")&&A(c,"type")=="dxa"&&A(c,"w")=="0")
                        WAttributes(c,"type","w");
                    else if(Is(c,"tblStyle")){WAttributes(c,"val");}
                    else if(Is(c,"tblLook")){WAttributes(c,"val","firstRow","lastRow","firstColumn","lastColumn","noHBand","noVBand");if(tableCascade is null)Issue("LAYOUT_TABLE_LOOK",c,"Sélection des régions conservée ; styles conditionnels actifs refusés séparément.",false);}
                    else if(tableCascade is not null&&(Is(c,"tblStyleRowBandSize")||Is(c,"tblStyleColBandSize")||Is(c,"tblCaption")||Is(c,"tblDescription")))WAttributes(c,"val");
                    else if(tableCascade is not null&&Is(c,"tblpPr"))Issue("PAGE_TABLE_FLOAT_PENDING",c,"Ancrage de tableau flottant à résoudre.");
                    else Issue("LAYOUT_TABLE_PROPERTY_UNQUALIFIED",c,"Propriété de tableau non qualifiée : "+c.Name);
                }
            }
            if(tableCascade is null&&!autoFit&&width+indent>geometry.Width-geometry.Left-geometry.Right+.5f){Issue("LAYOUT_TABLE_TOO_WIDE",table,"Tableau plus large que la zone de texte.");return;}
            if(alignment!="left"&&indent!=0)Issue("LAYOUT_TABLE_ALIGNMENT_INDENT_UNQUALIFIED",table,"Retrait combiné à un alignement non gauche hors profil.");
            float ratio=tableCascade is null?width/natural:1;var scaled=columns.Select(w=>w*ratio).ToArray();
            var rows=new List<LayoutRow>();var cells=new List<LayoutCell>();var active=new Dictionary<int,LayoutCell>();bool bodySeen=false;
            // Sentinel preserves the table's exact position among body paragraphs.
            output.Add(new(table.Id,table.Source,[],new(),section,geometry,start){TableId=table.Id});
            foreach(var row in Elements(table).Where(c=>!Is(c,"tblPr")&&!Is(c,"tblGrid")))
            {
                token.ThrowIfCancellationRequested();if(!Is(row,"tr")){Issue("LAYOUT_TABLE_BLOCK_UNQUALIFIED",row,"Bloc de tableau hors profil.");continue;}
                if(rows.Count>=2048){Issue("LAYOUT_TABLE_ROW_LIMIT",row,"Budget de lignes dépassé.");break;}
                WAttributes(row,"rsidR","rsidRPr","rsidTr");float rowHeight=0;string rule="atLeast";bool header=false;
                var directRow=Child(row,"trPr");
                foreach(var rp in PropertyLayers(tableCascade?.Row(row.Id)??[],"trPr",directRow))
                {
                    WAttributes(rp);foreach(var c in Elements(rp))
                    {
                        if(Is(c,"trHeight")){WAttributes(c,"val","hRule");rowHeight=Number(c,"val",0,0,1440,20);rule=A(c,"hRule");if(rule=="")rule="atLeast";if(rule is not ("auto" or "atLeast" or "exact"))Issue("LAYOUT_ROW_HEIGHT_UNQUALIFIED",c,"Hauteur de ligne hors profil.");}
                        else if(Is(c,"tblHeader")){WAttributes(c,"val");header=Toggle(c);}
                        else if(Is(c,"cantSplit")){WAttributes(c,"val");}
                        else if(tableCascade is not null&&(Is(c,"gridBefore")||Is(c,"gridAfter")))
                        {
                            WAttributes(c,"val");
                            if(rp!=directRow)Issue("PAGE_TABLE_STYLE_TOPOLOGY",c,"Décalage de grille déclaré par un style.");
                        }
                        else if(tableCascade is not null&&(Is(c,"wBefore")||Is(c,"wAfter")))
                        {
                            WAttributes(c,"w","type");
                            bool before=Is(c,"wBefore");
                            int count=GridOmissions(row,before?"gridBefore":"gridAfter");
                            var omitted=before?scaled.Take(count):scaled.TakeLast(count);
                            if(rp!=directRow||count==0||count>scaled.Length||A(c,"type")!="dxa"||
                                !int.TryParse(A(c,"w"),NumberStyles.None,CultureInfo.InvariantCulture,out int twips)||
                                Math.Abs(omitted.Sum()*20-twips)>.01f)
                                Issue("PAGE_TABLE_ROW_WIDTH_UNQUALIFIED",c,"La largeur de la zone omise ne concorde pas avec la grille déclarée.");
                        }
                        else if(tableCascade is not null&&Is(c,"cnfStyle")){}
                        else if(tableCascade is not null&&Is(c,"jc")&&A(c,"val")==alignment)WAttributes(c,"val");
                        else Issue("LAYOUT_ROW_PROPERTY_UNQUALIFIED",c,"Propriété de ligne hors profil.");
                    }
                }
                if(header&&bodySeen)Issue("LAYOUT_HEADER_NONCONTIGUOUS",row,"Les en-têtes doivent former un préfixe contigu.");if(!header)bodySeen=true;
                if(rule=="auto")rowHeight=0;
                int rowIndex=rows.Count;rows.Add(new(rowIndex,rowHeight,rule,header));int column=tableCascade is null?0:GridOmissions(row,"gridBefore");var nextActive=new Dictionary<int,LayoutCell>();
                foreach(var cell in Elements(row).Where(c=>!Is(c,"trPr")))
                {
                    if(!Is(cell,"tc")){Issue("LAYOUT_ROW_CONTENT_UNQUALIFIED",cell,"Contenu de ligne hors profil.");continue;}WAttributes(cell,"id");
                    int span=1;string? merge=null;var cellMargins=margins;var cellBorders=new CellBorders();string valign="top";string? fill=tableFill;float? declaredWidth=null;
                    TableWidthPreference? cellWidthPreference=null;
                    var applied=tableCascade is null?tableStyles.Select(s=>s.Id):tableCascade.Cell(cell.Id);
                    var directCell=Child(cell,"tcPr");
                    foreach(var cp in PropertyLayers(applied,"tcPr",directCell))
                    {
                        WAttributes(cp);foreach(var c in Elements(cp))
                        {
                            if(tableCascade is not null&&cp!=directCell&&
                                (Is(c,"gridSpan")||Is(c,"vMerge")||Is(c,"hMerge")||Is(c,"tcW")||Is(c,"cnfStyle")))
                            {Issue("PAGE_TABLE_STYLE_TOPOLOGY",c,"La topologie de grille requiert une déclaration directe.");continue;}
                            if(Is(c,"gridSpan")){WAttributes(c,"val");span=(int)Number(c,"val",1,1,32);}
                            else if(Is(c,"vMerge")){WAttributes(c,"val");merge=A(c,"val");if(merge.Length==0)merge="continue";if(merge is not ("restart" or "continue"))Issue("LAYOUT_VERTICAL_MERGE_UNQUALIFIED",c,"Fusion verticale inconnue.");}
                            else if(Is(c,"tcW")){WAttributes(c,"type","w");if(tableCascade is not null)cellWidthPreference=WidthPreference(c);else if(A(c,"type")=="dxa")declaredWidth=Number(c,"w",0,0,1440,20);else if(A(c,"type")=="pct"&&autoFit)declaredWidth=(preferredWidth??geometry.Width-geometry.Left-geometry.Right-indent)*Number(c,"w",5000,0,5000)/5000;else if(A(c,"type") is "" or "auto" or "nil")declaredWidth=null;else Issue("LAYOUT_CELL_WIDTH_UNQUALIFIED",c,"Largeur de cellule hors profil.");}
                            else if(Is(c,"tcMar"))cellMargins=Margins(c,cellMargins);
                            else if(Is(c,"tcBorders"))cellBorders=Borders(c,cellBorders);
                            else if(Is(c,"vAlign")){WAttributes(c,"val");valign=A(c,"val");if(valign is not ("top" or "center" or "bottom"))Issue("LAYOUT_CELL_ALIGNMENT_UNQUALIFIED",c,"Alignement vertical hors profil.");}
                            else if(Is(c,"shd"))fill=Fill(c);
                            else if(tableCascade is not null&&Is(c,"cnfStyle")){}
                            else Issue("LAYOUT_CELL_PROPERTY_UNQUALIFIED",c,"Propriété de cellule non qualifiée : "+c.Name);
                        }
                    }
                    if(column+span>scaled.Length){Issue("LAYOUT_GRID_SPAN_INVALID",cell,"Cellule hors grille.");continue;}
                    float cellWidth=scaled.Skip(column).Take(span).Sum();if(tableCascade is null&&!autoFit&&declaredWidth.HasValue&&Math.Abs(declaredWidth.Value*ratio-cellWidth)>.5f)Issue("LAYOUT_CELL_WIDTH_CONFLICT",cell,"Largeur de cellule incompatible avec la grille.");
                    if(tableCascade is null&&!autoFit&&cellWidth-cellMargins.Left-cellMargins.Right<12)Issue("LAYOUT_CELL_BODY_INVALID",cell,"Zone de cellule trop étroite.");
                    var paragraphIds=new List<string>();
                    foreach(var block in Elements(cell).Where(c=>!Is(c,"tcPr")))
                    {
                        if(!Is(block,"p")){Issue(Is(block,"tbl")?"LAYOUT_NESTED_TABLE_UNQUALIFIED":"LAYOUT_CELL_BLOCK_UNQUALIFIED",block,"Bloc imbriqué non qualifié ; aucune mise à plat.");continue;}
                        var paragraphStyle=tableCascade is null?tableParagraph:paragraphDefaults;
                        if(tableCascade is not null)
                            foreach(var layer in tableCascade.Text(block.Id))
                                paragraphStyle=Paragraph(Child(nodes[layer],"pPr"),paragraphStyle);
                        var p=ReadParagraph(block,section,new(cellWidth,geometry.Height,cellMargins.Left,0,cellMargins.Right,0),start,paragraphStyle,tableCharacter) with{CellOwner=cell.Id};
                        if(p.Properties.KeepNext&&semantic is null)Issue("LAYOUT_CELL_KEEP_NEXT_UNQUALIFIED",block,"Solidarité avec le paragraphe suivant dans/au-delà des cellules à qualifier séparément.");
                        if(p.Properties.PageBreakBefore||p.Runs.Any(r=>r.Kind=="pageBreak")||Child(Child(block,"pPr")??block,"sectPr") is not null)Issue("LAYOUT_CELL_BREAK_UNQUALIFIED",block,"Saut de page/section interne à une cellule hors profil.");
                        paragraphIds.Add(p.Id);output.Add(p);
                    }
                    if(semantic is not null&&paragraphIds.Count>0&&output[^1].Properties.KeepNext)
                        Issue("LAYOUT_CELL_KEEP_NEXT_UNQUALIFIED",nodes[output[^1].Id],"Solidarité franchissant une limite de cellule hors profil.");
                    if(paragraphIds.Count==0)Issue("LAYOUT_CELL_PARAGRAPH_MISSING",cell,"Cellule sans paragraphe qualifié.");
                    var model=new LayoutCell(cell.Id,cell.Source,rowIndex,column,span,1,null,cellMargins,cellBorders,valign,fill,paragraphIds)
                    {PreferredWidth=declaredWidth,NativeWidth=cellWidthPreference,AppliedTableStyles=tableCascade?.Cell(cell.Id)??[]};
                    if(merge=="continue")
                    {
                        if(!active.TryGetValue(column,out var origin)||origin.ColumnSpan!=span)Issue("LAYOUT_VERTICAL_MERGE_INVALID",cell,"Fusion verticale sans origine compatible.");
                        else
                        {
                            if(output.Where(p=>paragraphIds.Contains(p.Id)).Any(p=>p.Text.Length>0||p.List is not null||p.Runs.Any(r=>r.Image is not null)))Issue("LAYOUT_VERTICAL_MERGE_CONTENT",cell,"Contenu dans une cellule de continuation : refus plutôt que perte.");
                            if(origin.Margins!=cellMargins||origin.Borders!=cellBorders||origin.Fill!=fill||origin.VerticalAlignment!=valign)Issue("LAYOUT_VERTICAL_MERGE_STYLE",cell,"Propriétés différentes dans une fusion verticale hors profil.");
                            model=model with{MergeOrigin=origin.Id};var updated=origin with{RowSpan=rowIndex-origin.Row+1};int index=cells.FindIndex(c=>c.Id==origin.Id);cells[index]=updated;nextActive[column]=updated;
                        }
                    }
                    else if(merge=="restart")nextActive[column]=model;
                    cells.Add(model);column+=span;
                }
                int after=tableCascade is null?0:GridOmissions(row,"gridAfter");
                if(column+after!=scaled.Length)Issue("LAYOUT_GRID_ROW_INVALID",row,"La ligne ne couvre pas exactement la grille.");active=nextActive;
            }
            if(rows.Count==0)Issue("LAYOUT_TABLE_EMPTY",table,"Tableau sans ligne.");
            foreach(var c in cells.Where(c=>c.MergeOrigin is null&&c.RowSpan>1))if(rows.Skip(c.Row).Take(c.RowSpan).Select(r=>r.Header).Distinct().Count()>1)Issue("LAYOUT_HEADER_MERGE_UNQUALIFIED",table,"Fusion traversant la limite d’en-tête non qualifiée.");
            tables.Add(new(table.Id,table.Source,scaled,rows,cells,alignment,indent){Borders=borders,AutoFit=autoFit,PreferredWidth=preferredWidth,AvailableWidth=geometry.Width-geometry.Left-geometry.Right-indent,NativeWidth=nativeWidth,PhysicalPage=tableCascade is null?null:geometry});
            Issue("LAYOUT_ATOMIC_TABLE_ROWS",table,"Lignes et groupes fusionnés conservés entiers ; partage interne d’une ligne non qualifié.",false);
        }

        IEnumerable<InventoryNode> PropertyLayers(IEnumerable<string> layers,string name,InventoryNode? direct) =>
            layers.Select(id=>Child(nodes[id],name)).Append(direct).OfType<InventoryNode>();

        int GridOmissions(InventoryNode row,string name)=>
            Child(row,"trPr") is {} properties&&Child(properties,name) is {} declared?(int)Number(declared,"val",0,0,32):0;

        TableWidthPreference? WidthPreference(InventoryNode declaration)
        {
            string unit=A(declaration,"type"),raw=A(declaration,"w");
            if(unit is "" or "auto" or "nil")return null;
            if(unit=="dxa"&&int.TryParse(raw,NumberStyles.Integer,CultureInfo.InvariantCulture,out int twips)&&twips is >=0 and <=28_800)
                return new(unit,twips/20f,declaration.Source);
            if(unit=="pct")
            {
                bool suffixed=raw.EndsWith('%');
                if(float.TryParse(suffixed?raw[..^1]:raw,suffixed?NumberStyles.AllowDecimalPoint:NumberStyles.None,
                    CultureInfo.InvariantCulture,out float value)&&float.IsFinite(value)&&value>=0&&value<=(suffixed?100:5000))
                    return new(unit,value/(suffixed?100:5000),declaration.Source);
            }
            Issue("PAGE_TABLE_WIDTH_UNIT",declaration,"Préférence de largeur ou unité hors profil.");
            return null;
        }
    }
}
