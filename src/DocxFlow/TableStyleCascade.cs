using System.Collections.Immutable;
using System.Globalization;

namespace LecteurWord.Flow;

/// <summary>Resolves which declarations participate in the native table profile before shaping.</summary>
internal sealed class TableStyleCascade
{
    // Word precedence, from least to most specific. Mask order follows ST_Cnf.
    static readonly string[] Precedence=["wholeTable","band1Horz","band2Horz","band1Vert","band2Vert",
        "firstCol","lastCol","firstRow","lastRow","nwCell","neCell","swCell","seCell"];
    static readonly string[] MaskOrder=["firstRow","lastRow","firstCol","lastCol","band1Vert","band2Vert",
        "band1Horz","band2Horz","nwCell","neCell","swCell","seCell"];
    static readonly string[] MaskAttributes=["firstRow","lastRow","firstColumn","lastColumn","oddVBand","evenVBand",
        "oddHBand","evenHBand","firstRowFirstColumn","firstRowLastColumn","lastRowFirstColumn","lastRowLastColumn"];
    readonly SemanticDocument model;
    readonly Dictionary<string,ImmutableArray<string>> whole=new(StringComparer.Ordinal);
    readonly Dictionary<string,ImmutableArray<string>> cells=new(StringComparer.Ordinal);
    readonly Dictionary<string,ImmutableArray<string>> rows=new(StringComparer.Ordinal);
    readonly Dictionary<string,ImmutableArray<string>> paragraphs=new(StringComparer.Ordinal);
    readonly List<(string TableId,FlowIssue Issue)> issues=[];
    internal IReadOnlyList<(string TableId,FlowIssue Issue)> Issues=>issues;
    internal ImmutableArray<string> Cell(string id)=>Get(cells,id);
    internal ImmutableArray<string> Row(string id)=>Get(rows,id);
    internal ImmutableArray<string> Table(string id)=>Get(whole,id);
    internal ImmutableArray<string> Text(string id)
    {
        for(string? parent=id;parent is not null;parent=model.ParentId(parent))
        {
            if(paragraphs.TryGetValue(parent,out var p))return p;
            if(cells.TryGetValue(parent,out var c))return c;
            if(model.Element(parent).Kind==SemanticKind.Story)break;
        }
        return [];
    }

    internal TableStyleCascade(SemanticDocument model,CancellationToken token)
    {
        this.model=model;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        int count=0;
        string activeTable="";
        var defaultStyle=model.Styles.Where(s=>s.Type=="table"&&s.IsDefault).SingleOrDefaultIfUnique()?.Id;
        foreach(var table in model.Tables)
        {
            activeTable=table.Id;
            Check();
            string? direct=model.ChildId(table.Id,"tblPr");
            string? use=Child(direct,"tblStyle")??defaultStyle;
            if(use is null)continue;
            var style=model.StyleUse(use);
            if(style?.Resolved!=true)
            {
                Issue(use,"PAGE_TABLE_STYLE_UNRESOLVED","Héritage de style de tableau incomplet.");
                continue;
            }
            var definitions=style.BaseToDerivedIds;
            var byCondition=new Dictionary<string,List<string>>(StringComparer.Ordinal);
            foreach(string definition in definitions)
            {
                Check();
                var seen=new HashSet<string>(StringComparer.Ordinal);
                foreach(var condition in model.BlocksOf(definition).Where(e=>model.Name(e.Id)==SemanticIndex.QName(SemanticIndex.W,"tblStylePr")))
                {
                    Check();
                    string? type=model.Attribute(condition.Id,"type");
                    if(type is null||!Precedence.Contains(type)||!seen.Add(type))
                    {
                        Issue(condition.Id,"PAGE_TABLE_STYLE_CONDITION","Type de style conditionnel inconnu ou répété.");
                        continue;
                    }
                    if(!byCondition.TryGetValue(type,out var layer))byCondition.Add(type,layer=[]);
                    layer.Add(condition.Id);
                }
            }
            var baseLayers=definitions.AddRange(byCondition.GetValueOrDefault("wholeTable")??[]);
            whole.Add(table.Id,baseLayers);
            foreach(string declaration in baseLayers)
                if(Child(Child(declaration,"pPr"),"rPr") is {} mark)
                    Issue(mark,"PAGE_TABLE_MARK_STYLE","Propriétés de marque de paragraphe hors profil de tableau.");
            var tableProps=baseLayers.Select(d=>model.ChildId(d,"tblPr")).Append(direct).ToArray();
            var look=tableProps.Select(p=>Child(p,"tblLook")).LastOrDefault(l=>l is not null);
            bool firstRow=Look("firstRow",0x20,true),lastRow=Look("lastRow",0x40,false),
                firstCol=Look("firstColumn",0x80,true),lastCol=Look("lastColumn",0x100,false),
                hBand=!Look("noHBand",0x200,false),vBand=!Look("noVBand",0x400,true);
            int rowSize=BandSize("tblStyleRowBandSize"),colSize=BandSize("tblStyleColBandSize");
            long columns=Math.Max(table.GridColumns.Length,table.Rows.SelectMany(r=>r.Cells)
                .Select(c=>(long)(c.Column??0)+(c.ColumnSpan??0)).DefaultIfEmpty().Max());
            if(columns>32){Issue(table.Id,"PAGE_TABLE_STYLE_GRID","Nombre de colonnes hors profil.");continue;}
            var reported=new HashSet<string>(StringComparer.Ordinal);
            for(int r=0;r<table.Rows.Length;r++)
            {
                var row=table.Rows[r];
                Check();
                var rowMask=new HashSet<string>(StringComparer.Ordinal);
                if(firstRow&&r==0)rowMask.Add("firstRow");
                if(lastRow&&r==table.Rows.Length-1)rowMask.Add("lastRow");
                if(rowMask.Count==0&&hBand)rowMask.Add((r-(firstRow?1:0))/rowSize%2==0?"band1Horz":"band2Horz");
                Patch(rowMask,Child(model.ChildId(row.Id,"trPr"),"cnfStyle"));
                rows[row.Id]=Layers(rowMask);
                foreach(var cell in row.Cells)
                {
                    Check();
                    if(cell.Column is not {} col||cell.ColumnSpan is not {} span||col<0||span<1||col>columns-span)
                    {
                        Issue(cell.Id,"PAGE_TABLE_STYLE_GRID","Région conditionnelle sans identité de colonne exploitable.");
                        continue;
                    }
                    var mask=new HashSet<string>(rowMask,StringComparer.Ordinal);
                    bool left=firstCol&&col==0,right=lastCol&&col+span==columns;
                    if(left)mask.Add("firstCol");
                    if(right)mask.Add("lastCol");
                    if(!left&&!right&&vBand)
                    {
                        int at=col-(firstCol?1:0);
                        if(at/colSize!=(at+span-1)/colSize&&(byCondition.ContainsKey("band1Vert")||byCondition.ContainsKey("band2Vert")))
                            Issue(cell.Id,"PAGE_TABLE_STYLE_MERGED_BAND","Une fusion traverse plusieurs bandes de colonnes.");
                        else mask.Add(at/colSize%2==0?"band1Vert":"band2Vert");
                    }
                    if(mask.Contains("firstRow")&&left)mask.Add("nwCell");
                    if(mask.Contains("firstRow")&&right)mask.Add("neCell");
                    if(mask.Contains("lastRow")&&left)mask.Add("swCell");
                    if(mask.Contains("lastRow")&&right)mask.Add("seCell");
                    Patch(mask,Child(model.ChildId(cell.Id,"tcPr"),"cnfStyle"));
                    var cellLayers=Layers(mask);
                    cells[cell.Id]=cellLayers;
                    foreach(string layer in cellLayers.Skip(baseLayers.Length))
                    {
                        if(!reported.Add(layer))continue;
                        string type=model.Attribute(layer,"type")!;
                        if(Child(layer,"tblPr") is {} localTable&&model.BlocksOf(localTable).Any())
                            Issue(localTable,"PAGE_TABLE_CONDITIONAL_GEOMETRY","Propriétés conditionnelles de tableau entier à résoudre.");
                        if(type is not ("firstRow" or "lastRow" or "band1Horz" or "band2Horz")&&
                            (Child(layer,"trPr") is {} localRow&&model.BlocksOf(localRow).Any()||
                            Child(layer,"tcPr") is {} tc&&new[]{"tcMar","noWrap","vAlign"}.Any(k=>Child(tc,k) is not null)))
                            Issue(layer,"PAGE_TABLE_CONDITIONAL_ROW_GEOMETRY","Effet de colonne ou de coin sur toute la rangée à résoudre.");
                    }
                    foreach(var block in model.BlocksOf(cell.Id))
                    {
                        Check();
                        if(block.Kind!=SemanticKind.Paragraph||Child(model.ChildId(block.Id,"pPr"),"cnfStyle") is not {} cnf)continue;
                        var overrideMask=new HashSet<string>(mask,StringComparer.Ordinal);
                        Patch(overrideMask,cnf);
                        paragraphs[block.Id]=Layers(overrideMask);
                    }
                }
            }

            ImmutableArray<string> Layers(HashSet<string> mask)=>baseLayers.AddRange(Precedence.Where(mask.Contains)
                .SelectMany(key=>byCondition.GetValueOrDefault(key)??[]));
            bool Look(string name,int bit,bool fallback)
            {
                if(look is null)return fallback;
                if(model.Attribute(look,name) is {} value)
                {
                    if(Toggle(value) is {} flag)return flag;
                    Issue(look,"PAGE_TABLE_LOOK_VALUE","Indicateur tblLook invalide.");
                    return fallback;
                }
                if(model.Attribute(look,"val") is not {} raw)return fallback;
                if(ushort.TryParse(raw,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var flags))return (flags&bit)!=0;
                Issue(look,"PAGE_TABLE_LOOK_VALUE","Masque tblLook invalide.");
                return fallback;
            }
            int BandSize(string name)
            {
                if(tableProps.Select(p=>Child(p,name)).LastOrDefault(x=>x is not null) is not {} at)return 1;
                if(int.TryParse(model.Attribute(at,"val"),NumberStyles.None,CultureInfo.InvariantCulture,out int size)&&size is >=1 and <=2048)return size;
                Issue(at,"PAGE_TABLE_BAND_SIZE","Taille de bande hors profil.");
                return 1;
            }
            void Patch(HashSet<string> mask,string? source)
            {
                if(source is null)return;
                string? value=model.Attribute(source,"val");
                bool valid=value is {Length:12}&&value.All(c=>c is '0' or '1');
                if(value is not null&&!valid)Issue(source,"PAGE_TABLE_CNF_VALUE","Masque cnfStyle invalide.");
                for(int i=0;i<MaskOrder.Length;i++)
                {
                    bool? flag=valid?value![i]=='1':null;
                    if(model.Attribute(source,MaskAttributes[i]) is {} attribute)
                    {
                        flag=Toggle(attribute);
                        if(flag is null)Issue(source,"PAGE_TABLE_CNF_VALUE","Attribut cnfStyle invalide.");
                    }
                    if(flag==true)mask.Add(MaskOrder[i]);
                    else if(flag==false)mask.Remove(MaskOrder[i]);
                }
            }
        }

        void Check()
        {
            token.ThrowIfCancellationRequested();
            if(++count>120_000||clock.ElapsedMilliseconds>5000)throw new InvalidDataException("PAGE_TABLE_STYLE_LIMIT");
        }
        void Issue(string id,string code,string message)=>issues.Add((activeTable,new(code,model.Source(id).Location,message,true)));
    }

    string? Child(string? id,string name)=>id is null?null:model.ChildId(id,name);
    static ImmutableArray<string> Get(Dictionary<string,ImmutableArray<string>> map,string id)=>map.GetValueOrDefault(id,[]);
    static bool? Toggle(string raw)=>raw switch{"1" or "true" or "on"=>true,"0" or "false" or "off"=>false,_=>null};
}
