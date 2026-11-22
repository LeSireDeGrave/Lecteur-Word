using System.Collections.Immutable;
using System.Globalization;

namespace LecteurWord.Flow;

internal sealed class LinePropertiesReader(SemanticDocument model,TableStyleCascade? tableStyles=null)
{
    internal const long EmuPerTwip=635;
    const string W=SemanticIndex.W;
    const long DefaultStop=720*EmuPerTwip;
    readonly string? settings=model.RelatedRoot("settings","settings");
    readonly HashSet<string> numbered=model.Numbering.Where(n=>!n.Suppressed).Select(n=>n.ParagraphId).ToHashSet();
    readonly Dictionary<string,(string[] Properties,FlowIssue[] Issues)> tableLayers=[];

    internal LineProperties Read(TextParagraph paragraph,string? levelId=null,bool nativeNumbering=false)
    {
        var issues=ImmutableArray.CreateBuilder<FlowIssue>();
        var origins=ImmutableDictionary.CreateBuilder<string,SemanticSource>(StringComparer.Ordinal);
        var stops=new SortedDictionary<long,LineTabStop>();
        long left=0,right=0,first=0,defaultTab=DefaultStop;
        SemanticSource? defaultSource=null;
        bool keepNext=false,keepLines=false,widow=true,pageBefore=false,autoHyphen=false,suppressHyphen=false,noHangTab=false;
        var alignment=paragraph.RightToLeft?LineAlignment.Right:LineAlignment.Left;
        var table=tableStyles is null?TableProperties(paragraph.Id):default;
        if(tableStyles is null)issues.AddRange(table.Issues);
        foreach(string root in model.PresentationDeclarations(paragraph.Id,
            unresolved:id=>Issue(id,"LINE_STYLE_UNRESOLVED","Chaîne de style incomplète."), tableParagraphProperties:
            tableStyles is null?table.Properties:tableStyles.Text(paragraph.Id).Select(id=>model.ChildId(id,"pPr")).OfType<string>(),
            numberingParagraphProperties:levelId is null?null:model.ChildId(levelId,"pPr")))
            foreach(var e in model.BlocksOf(root))
            {
                string id=e.Id;
                switch(Local(id))
                {
                    case "ind":
                        var logicalStart=Attr(id,"start");var logicalEnd=Attr(id,"end");
                        if(Number(id,paragraph.RightToLeft?"end":"start",out long l)||
                            (paragraph.RightToLeft?logicalEnd:logicalStart) is null&&Number(id,"left",out l))
                        {left=l;Set("left",id);}
                        if(Number(id,paragraph.RightToLeft?"start":"end",out long r)||
                            (paragraph.RightToLeft?logicalStart:logicalEnd) is null&&Number(id,"right",out r))
                        {right=r;Set("right",id);}
                        if(Number(id,"hanging",out long h)){first=-h;Set("first",id);}
                        else if(Number(id,"firstLine",out long f)){first=f;Set("first",id);}
                        if(new[]{"leftChars","rightChars","startChars","endChars","firstLineChars","hangingChars"}.Any(k=>Attr(id,k)is not null))
                            Issue(id,"LINE_CHAR_INDENT","Retrait en caractères hors profil.");
                        break;
                    case "jc":
                        var value=Attr(id,"val") switch
                        {
                            "left"=>LineAlignment.Left,"right"=>LineAlignment.Right,"center"=>LineAlignment.Center,"both"=>LineAlignment.Justify,
                            "start"=>paragraph.RightToLeft?LineAlignment.Right:LineAlignment.Left,
                            "end"=>paragraph.RightToLeft?LineAlignment.Left:LineAlignment.Right,
                            _=>(LineAlignment?)null
                        };
                        if(value is {} j){alignment=j;Set("alignment",id);}
                        else Issue(id,"LINE_ALIGNMENT_UNQUALIFIED","Alignement spécialisé hors profil.");
                        break;
                    case "tabs":
                        foreach(var tab in model.BlocksOf(id).Where(n=>Local(n.Id)=="tab"))
                        {
                            if(!Number(tab.Id,"pos",out long position))
                            {if(Attr(tab.Id,"pos") is null)Issue(tab.Id,"LINE_TAB_POSITION","Position de taquet absente.");continue;}
                            string? align=Attr(tab.Id,"val");
                            if(align=="clear"){stops.Remove(position);continue;}
                            var kind=align switch
                            {
                                "left"=>LineTabKind.Left,"right"=>LineTabKind.Right,
                                "start"=>paragraph.RightToLeft?LineTabKind.Right:LineTabKind.Left,
                                "end"=>paragraph.RightToLeft?LineTabKind.Left:LineTabKind.Right,
                                "center"=>LineTabKind.Center,"decimal"=>LineTabKind.Decimal,
                                "num" or "list" when nativeNumbering=>LineTabKind.Numbering,_=>(LineTabKind?)null
                            };
                            if(kind is null){Issue(tab.Id,"LINE_TAB_KIND","Taquet spécialisé hors profil.");continue;}
                            string leader=Attr(tab.Id,"leader")??"none";
                            if(leader is not ("none" or "dot" or "hyphen" or "underscore" or "middleDot"))
                                Issue(tab.Id,"LINE_LEADER_UNQUALIFIED","Motif de suite hors profil.");
                            if(stops.Count>=128&&!stops.ContainsKey(position))
                            {Issue(tab.Id,"LINE_TAB_LIMIT","Budget de taquets dépassé.");continue;}
                            stops[position]=new(position,kind.Value,leader,model.Source(tab.Id));
                        }
                        break;
                    case "keepNext":keepNext=Boolean(id,keepNext);Set("keepNext",id);break;
                    case "keepLines":keepLines=Boolean(id,keepLines);Set("keepLines",id);break;
                    case "widowControl":widow=Boolean(id,widow);Set("widowControl",id);break;
                    case "pageBreakBefore":pageBefore=Boolean(id,pageBefore);Set("pageBreakBefore",id);break;
                    case "suppressAutoHyphens":suppressHyphen=Boolean(id,suppressHyphen);Set("suppressAutoHyphens",id);break;
                    case "wordWrap" or "kinsoku" or "overflowPunct" or "topLinePunct" or "autoSpaceDE" or "autoSpaceDN":
                        if(Boolean(id,false))Issue(id,"LINE_WORD_WRAP_PARTIAL","Option Word de coupure ou d’espacement hors profil.");
                        break;
                    case "numPr":
                        if(!nativeNumbering&&numbered.Contains(paragraph.Id))
                            Issue(id,"LINE_NUMBERING_PENDING","Forme et retrait du libellé de numérotation à composer.");
                        break;
                    case "spacing":
                        if(Attr(id,"line") is not null||Attr(id,"lineRule") is not null)
                            Issue(id,"LINE_SPACING_PENDING","La ligne fournit sa hauteur naturelle. Interligne OOXML à appliquer par le compositeur de blocs.");
                        break;
                }
            }
        if(settings is {} s)
        {
            if(model.ChildId(s,"defaultTabStop") is {} tab)
            {
                if(Number(tab,"val",out long value)&&value>0){defaultTab=value;defaultSource=model.Source(tab);}
                else Issue(tab,"LINE_DEFAULT_TAB_VALUE","Taquet documentaire invalide. Intervalle de repli de 720 twips.");
            }
            if(model.ChildId(s,"autoHyphenation") is {} hyphen)
            {
                autoHyphen=Boolean(hyphen,false);
                if(autoHyphen&&!suppressHyphen)Issue(hyphen,"LINE_AUTO_HYPHENATION_UNQUALIFIED","Césure automatique demandée, dictionnaire absent du profil.");
            }
            if(model.ChildId(s,"compat") is {} compat&&model.ChildId(compat,"noTabHangIndent") is {} n)
                noHangTab=Boolean(n,false);
        }
        if(first<0&&!noHangTab)
        {
            long position=paragraph.RightToLeft?right:left;
            stops.TryAdd(position,new(position,paragraph.RightToLeft?LineTabKind.Right:LineTabKind.Left,"none",origins.GetValueOrDefault("first")));
        }
        if(model.ParentId(paragraph.Id) is {} parent&&Local(parent)=="tc"&&model.ChildId(parent,"tcPr") is {} cell&&
            model.ChildId(cell,"noWrap") is {} nowrap&&Boolean(nowrap,false))
            Issue(nowrap,"LINE_CELL_WRAP_PENDING","Contrainte de largeur de cellule à résoudre avec la table.");
        if(paragraph.DirectionOrigin is {} direction)origins["direction"]=direction;
        return new(alignment,left,right,first,[..stops.Values],defaultTab,defaultSource,keepNext,keepLines,widow,pageBefore,
            paragraph.RightToLeft,origins.ToImmutable(),issues.ToImmutable());

        string? Attr(string id,string key)=>model.Attribute(id,key);
        void Set(string key,string id)=>origins[key]=model.Source(id);
        void Issue(string id,string code,string text)=>issues.Add(new(code,model.Source(id).Location,text,false));
        bool Number(string id,string key,out long emu)
        {
            emu=0;
            if(Attr(id,key) is not {} raw)return false;
            if(!int.TryParse(raw,NumberStyles.Integer,CultureInfo.InvariantCulture,out int n)||n is < -129600 or >129600)
            {Issue(id,"LINE_PROPERTY_VALUE","Valeur géométrique non qualifiée : "+key+".");return false;}
            emu=n*EmuPerTwip;return true;
        }
        bool Boolean(string id,bool prior)
        {
            switch(Attr(id,"val"))
            {
                case null or "true" or "on" or "1":return true;
                case "false" or "off" or "0":return false;
                default:Issue(id,"LINE_PROPERTY_VALUE","Valeur booléenne non qualifiée.");return prior;
            }
        }
    }

    (string[] Properties,FlowIssue[] Issues) TableProperties(string paragraph)
    {
        string? table=model.ParentId(paragraph);
        while(table is not null&&Local(table)!="tbl")table=model.ParentId(table);
        if(table is null)return ([],[]);
        if(tableLayers.TryGetValue(table,out var cached))return cached;
        var layers=new List<string>();
        var issues=new List<FlowIssue>();
        string? use=model.ChildId(table,"tblPr") is {} pr?model.ChildId(pr,"tblStyle"):null;
        use??=model.Styles.Where(s=>s.Type=="table"&&s.IsDefault).SingleOrDefaultIfUnique()?.Id;
        var chain=use is null?null:model.StyleUse(use);
        if(use is not null&&chain?.Resolved!=true)
            issues.Add(new("LINE_STYLE_UNRESOLVED",model.Source(use).Location,"Chaîne de style de tableau incomplète.",false));
        if(chain is not null)
            foreach(string definition in chain.BaseToDerivedIds)
            {
                if(model.ChildId(definition,"pPr") is {} p)layers.Add(p);
                foreach(var condition in model.BlocksOf(definition).Where(e=>Local(e.Id)=="tblStylePr"))
                {
                    if(model.ChildId(condition.Id,"pPr") is not {} properties)continue;
                    if(model.Attribute(condition.Id,"type")=="wholeTable")layers.Add(properties);
                    else issues.Add(new("LINE_TABLE_CONDITION_PARTIAL",model.Source(condition.Id).Location,
                        "Propriétés conditionnelles de tableau conservées pour la composition des cellules.",false));
                }
            }
        var result=(layers.ToArray(),issues.ToArray());
        if(tableLayers.Count<512)tableLayers.Add(table,result);
        return result;
    }

    string Local(string id)
    {
        string name=model.Name(id);
        return name.StartsWith("{"+W+"}",StringComparison.Ordinal)?name[(W.Length+2)..]:"";
    }

}
