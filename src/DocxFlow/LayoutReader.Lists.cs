using System.Globalization;
using System.Text.RegularExpressions;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        readonly List<LayoutTable> tables=[];
        readonly Dictionary<string,int?[]> counters=[];
        sealed record NumberLevel(InventoryNode? Node,int Start,int Restart,string Format,string Text,string Alignment,string Suffix,bool Valid=true);
        readonly Dictionary<(string Id,int Level),NumberLevel> levels=[];
        ILookup<string,InventoryNode>? numberingInstances,numberingAbstracts;
        bool checkingListDefinition,invalidListDefinition;
        string defaultParagraphStyle="";
        void ReadSettings()
        {
            var settings=semantic is null?input.Manifest.Relationships.FirstOrDefault(r=>r.Type==R+"/settings"&&r.Mode=="Internal")?.ResolvedPart:
                semantic.RelatedPart("settings");
            var tab=input.Manifest.Nodes.FirstOrDefault(n=>n.Source.PartUri==settings&&Is(n,"defaultTabStop"));
            if(tab is not null)paragraphDefaults=paragraphDefaults with{DefaultTab=Number(tab,"val",36,1,1440,20)};
            foreach(var setting in input.Manifest.Nodes.Where(n=>n.Source.PartUri==settings&&(Is(n,"autoHyphenation")||Is(n,"mirrorMargins"))))
                if(Toggle(setting))Issue("LAYOUT_SETTING_UNQUALIFIED",setting,"Option de composition active non qualifiée : "+setting.Name);
            if(semantic is not null)defaultParagraphStyle=semantic.DefaultStyle("paragraph")?.DeclaredId??"";
            else defaultParagraphStyle=styles.Values.FirstOrDefault(n=>A(n,"type")=="paragraph"&&A(n,"default") is "1" or "true") is {} ds?A(ds,"styleId"):"";
        }
        ParagraphLayout ReadNumPr(InventoryNode n,ParagraphLayout p)
        {
            WAttributes(n);
            foreach(var c in children[n.Id])
            {
                WAttributes(c,"val");
                if(Is(c,"numId"))p=p with{NumId=A(c,"val")};
                else if(Is(c,"ilvl"))p=p with{ListLevel=(int)Number(c,"val",0,0,8)};
                else if(!c.Name.StartsWith('#'))Issue("LAYOUT_NUMBERING_PROPERTY_UNQUALIFIED",c,"Propriété de numérotation non qualifiée.");
            }
            return p;
        }
        ParagraphLayout ReadTabs(InventoryNode n,ParagraphLayout p)
        {
            WAttributes(n);var tabs=p.Tabs.ToDictionary(t=>t.Position);
            foreach(var c in children[n.Id].Where(c=>!c.Name.StartsWith('#')))
            {
                if(!Is(c,"tab")){Issue("LAYOUT_TAB_UNQUALIFIED",c,"Arrêt de tabulation inconnu.");continue;}
                WAttributes(c,"val","pos","leader");float pos=Number(c,"pos",0,0,1440,20);string align=A(c,"val");
                if(A(c,"leader") is not ("" or "none")&&
                    (semantic is null||A(c,"leader") is not ("dot" or "hyphen" or "underscore" or "middleDot")))
                    Issue("LAYOUT_TAB_LEADER_UNQUALIFIED",c,"Points de suite non qualifiés.");
                if(align=="clear")tabs.Remove(pos);
                else if(align=="num"||semantic is not null&&align=="list")tabs[pos]=new(pos,"numbering");
                else if(align is "left" or "right" or "center" or "decimal")tabs[pos]=new(pos,align){Leader=A(c,"leader")};
                else Issue("LAYOUT_TAB_ALIGNMENT_UNQUALIFIED",c,"Alignement de tabulation non qualifié : "+align);
            }
            return p with{Tabs=tabs.Values.OrderBy(t=>t.Position).ToArray()};
        }
        NumberLevel Level(string id,int level)
        {
            if(levels.TryGetValue((id,level),out var cached))return cached;
            token.ThrowIfCancellationRequested();
            checkingListDefinition=true;invalidListDefinition=false;
            try
            {
                var resolved=ReadLevel(id,level);
                return levels[(id,level)]=resolved with{Valid=!invalidListDefinition};
            }
            finally{checkingListDefinition=false;}
        }
        NumberLevel ReadLevel(string id,int level)
        {
            if(numberingInstances is null)
            {
                var numbering=semantic is not null?semantic.RelatedPart("numbering"):
                    input.Manifest.Relationships.FirstOrDefault(r=>r.Type==R+"/numbering"&&r.Mode=="Internal")?.ResolvedPart;
                var declarations=input.Manifest.Nodes.Where(n=>n.Source.PartUri==numbering).ToArray();
                numberingInstances=declarations.Where(n=>Is(n,"num")).ToLookup(n=>A(n,"numId"));
                numberingAbstracts=declarations.Where(n=>Is(n,"abstractNum")).ToLookup(n=>A(n,"abstractNumId"));
            }
            var instances=numberingInstances[id].Take(2).ToArray();
            InventoryNode? lvl=null,over=null,baseLevel=null;int? startOverride=null;
            if(instances.Length==1)
            {
                var instance=instances[0];WAttributes(instance,"numId");
                var abstractId=Child(instance,"abstractNumId");
                var definitions=numberingAbstracts![abstractId is null?"":A(abstractId,"val")].Take(2).ToArray();
                if(definitions.Length==1)
                {
                    var definition=definitions[0];WAttributes(definition,"abstractNumId");
                    foreach(var c in children[definition.Id].Where(c=>!c.Name.StartsWith('#')))
                        if(!new[]{"lvl","nsid","multiLevelType","tmpl","name"}.Any(local=>Is(c,local)))Issue("LAYOUT_NUMBERING_LINK_UNQUALIFIED",c,"Définition de liste liée ou spécialisée non qualifiée.");
                    var matches=children[definition.Id].Where(c=>Is(c,"lvl")&&A(c,"ilvl")==level.ToString(CultureInfo.InvariantCulture)).ToArray();
                    if(matches.Length==1)lvl=baseLevel=matches[0];
                    else if(matches.Length>1)Issue("LAYOUT_NUMBERING_AMBIGUOUS",definition,"Niveaux de liste contradictoires.");
                }
                else if(semantic is not null)Issue("LAYOUT_NUMBERING_MISSING",instance,"Définition abstraite absente ou ambiguë.");
                foreach(var c in children[instance.Id].Where(c=>!c.Name.StartsWith('#')))
                    if(!Is(c,"abstractNumId")&&!Is(c,"lvlOverride"))Issue("LAYOUT_NUMBERING_INSTANCE_UNQUALIFIED",c,"Instance de liste hors profil.");
                var overrides=children[instance.Id].Where(c=>Is(c,"lvlOverride")&&A(c,"ilvl")==level.ToString(CultureInfo.InvariantCulture)).ToArray();
                if(overrides.Length>1)Issue("LAYOUT_NUMBERING_AMBIGUOUS",instance,"Surcharges de liste contradictoires.");
                if(overrides.Length==1)
                {
                    over=overrides[0];WAttributes(over,"ilvl");
                    foreach(var c in children[over.Id].Where(c=>!c.Name.StartsWith('#')))
                        if(!Is(c,"lvl")&&!Is(c,"startOverride"))Issue("LAYOUT_NUMBERING_OVERRIDE_UNQUALIFIED",c,"Surcharge de niveau hors profil.");
                    lvl=Child(over,"lvl")??lvl;if(Child(over,"startOverride") is {} so)startOverride=(int)Number(so,"val",1,0,999999);
                }
            }
            if(lvl is null){Issue("LAYOUT_NUMBERING_MISSING",instances.FirstOrDefault(),"Instance/niveau de numérotation absent ou ambigu : "+id+" / "+level);return new(null,1,level,"decimal","","left","tab");}
            WAttributes(lvl,"ilvl","tentative","tplc");
            int start=semantic is null?1:0,restart=level;string format="decimal",text="",alignment="left",suffix="tab";
            if(semantic is not null&&over is not null&&lvl!=baseLevel&&baseLevel is not null)
                foreach(var c in children[baseLevel.Id])
                {
                    if(Is(c,"start"))start=(int)Number(c,"val",start,0,999999);
                    if(Is(c,"lvlRestart"))restart=Restart(c,level);
                }
            bool overrideLevel=semantic is not null&&over is not null&&lvl!=baseLevel;
            foreach(var c in children[lvl.Id].Where(c=>!c.Name.StartsWith('#')))
            {
                if(Is(c,"start")){WAttributes(c,"val");if(!overrideLevel)start=(int)Number(c,"val",start,0,999999);}
                else if(Is(c,"lvlRestart")){WAttributes(c,"val");if(!overrideLevel)restart=Restart(c,level);}
                else if(Is(c,"numFmt")){WAttributes(c,"val");format=A(c,"val");}
                else if(Is(c,"lvlText"))
                {
                    if(semantic is null){WAttributes(c,"val");text=A(c,"val");}
                    else
                    {
                        WAttributes(c,"val","null");text=A(c,"val");
                        if(A(c,"null") is "1" or "true" or "on")
                            Issue("LAYOUT_LIST_NULL_LABEL",c,"Libellé nul spécialisé hors profil.");
                    }
                }
                else if(Is(c,"lvlJc")){WAttributes(c,"val");alignment=A(c,"val");}
                else if(Is(c,"suff")){WAttributes(c,"val");suffix=A(c,"val");}
                else if(semantic is not null&&Is(c,"pStyle"))WAttributes(c,"val");
                else if(semantic is not null&&Is(c,"pPr"))_=Paragraph(c,paragraphDefaults);
                else if(semantic is not null&&Is(c,"rPr"))_=Character(c,defaults);
                else if(!Is(c,"pPr")&&!Is(c,"rPr"))Issue("LAYOUT_NUMBERING_LEVEL_UNQUALIFIED",c,"Propriété de niveau hors profil.");
            }
            if(format is not ("decimal" or "upperLetter" or "lowerLetter" or "upperRoman" or "lowerRoman" or "bullet" or "none"))Issue("LAYOUT_NUMBER_FORMAT_UNQUALIFIED",lvl,"Format numérique hors profil : "+format);
            if(semantic is not null)alignment=alignment switch{"start"=>"left","end"=>"right",_=>alignment};
            if(alignment is not ("left" or "right" or "center")||suffix is not ("tab" or "space" or "nothing"))Issue("LAYOUT_NUMBER_SUFFIX_UNQUALIFIED",lvl,"Alignement ou suffixe de liste hors profil.");
            if(text.Length>128||format=="bullet"&&semantic is null&&text is not ("•" or "·" or "-" or "–"))
                Issue("LAYOUT_LIST_SYMBOL_UNQUALIFIED",lvl,"Libellé ou glyphe de puce hors profil.");
            return new(lvl,startOverride??start,restart,format,text,alignment,suffix);
        }
        int Restart(InventoryNode node,int level)
        {
            int value=(int)Number(node,"val",level,0,semantic is null?level:7);
            return value>level?level:value;
        }
        ListLabel? PrepareList(InventoryNode n,InventoryNode? pp,ref ParagraphLayout p,LayoutStyle character,string styleId,ParagraphLayout? inherited=null)
        {
            SemanticNumbering? selection=semanticNumbering?.GetValueOrDefault(n.Id);
            if(semantic is not null)
            {
                if(selection is null)
                {
                    if(p.NumId is not (null or "" or "0"))
                        Issue("LINE_NUMBERING_UNRESOLVED",n,"Numérotation héritée hors du profil de sélection des listes.");
                    return null;
                }
                if(selection.Suppressed)return null;
                if(selection.InstanceSourceId is not {} source||selection.InstanceId is null||selection.LevelId is null||selection.Level is not {} selected)
                {Issue("LINE_NUMBERING_UNRESOLVED",n,"Instance ou niveau de liste non résolu.");return null;}
                string requested=A(nodes[source],"val");
                if(!int.TryParse(requested,NumberStyles.Integer,CultureInfo.InvariantCulture,out int numericId)||numericId<=0)
                {Issue("LINE_NUMBERING_UNRESOLVED",n,"Identifiant d’instance de liste invalide.");return null;}
                p=p with{NumId=requested,ListLevel=selected};
            }
            if(p.NumId is null or "0" or "")
            {if(p.Tabs.Any(t=>t.Alignment=="numbering"))Issue("LAYOUT_NUMBER_TAB_WITHOUT_LIST",n,"Tabulation de numérotation sans liste.");return null;}
            string id=p.NumId;int index=p.ListLevel;var level=Level(id,index);
            if(semantic is not null&&(!level.Valid||level.Node?.Id!=selection!.LevelId))
            {Issue("LINE_NUMBERING_UNRESOLVED",n,"Définition de liste hors profil ou ambiguë.");return null;}
            if(level.Node is null)return null;
            if(semantic is null&&level.Suffix!="tab")Issue("LAYOUT_LIST_SUFFIX_UNQUALIFIED",n,"Suffixe autre qu’une tabulation non qualifié dans ce profil.");
            // Numbering properties precede paragraph styles and direct properties.
            p=Paragraph(Child(level.Node,"pPr"),inherited??paragraphDefaults);
            foreach(var style in Chain(styleId))p=Paragraph(Child(style,"pPr"),p);
            p=Paragraph(pp,p) with{NumId=id,ListLevel=index};
            if(semantic is null&&p.Alignment is not ("left" or "both"))Issue("LAYOUT_LIST_ALIGNMENT_UNQUALIFIED",n,"Alignement du paragraphe de liste hors profil.");
            if(semantic is null&&Child(level.Node,"pPr") is null&&p.Left==0&&p.FirstLine==0)
            {p=p with{Left=18*(index+1),FirstLine=-18};Issue("LAYOUT_LIST_DEFAULT_INDENT",n,"Retrait de liste absent : retrait de lecture explicite de 18 points par niveau.",false);}
            float textLeft=p.Left;
            if(semantic is null&&p.Tabs.Any(t=>t.Alignment=="numbering"&&Math.Abs(t.Position-textLeft)>.01f))Issue("LAYOUT_LIST_TAB_POSITION_UNQUALIFIED",n,"Tabulation de numérotation différente du retrait de texte hors profil.");
            if(!counters.TryGetValue(id,out var state))counters[id]=state=new int?[9];
            state[index]=state[index].HasValue?state[index]!.Value+1:level.Start;
            for(int i=index+1;i<9;i++)
            {
                // Only inspect defined descendants, avoiding fabricated missing-level errors.
                if(!state[i].HasValue)continue;var child=Level(id,i);if(child.Restart>0&&child.Restart-1>=index)state[i]=null;
            }
            var origins=new List<string>();
            bool formatError=false;
            string Failure(string code,string message)
            {
                formatError=true;Issue(code,n,message);return "?";
            }
            string Formatted(int i)
            {
                if(semantic is not null&&i>index)return Failure("LINE_NUMBERING_TEMPLATE","Référence à un niveau inférieur.");
                var item=Level(id,i);
                if(semantic is not null)
                {
                    if(!item.Valid||item.Node is null)return Failure("LINE_NUMBERING_REFERENCE","Niveau référencé absent ou hors profil.");
                    if(!origins.Contains(item.Node.Id))origins.Add(item.Node.Id);
                }
                int value=state[i]??item.Start;if(value>999999)return Failure("LAYOUT_LIST_COUNTER_LIMIT","Compteur de liste hors budget.");
                if(item.Format=="decimal")return value.ToString(CultureInfo.InvariantCulture);
                if(item.Format is "upperLetter" or "lowerLetter")
                {
                    if(value<1)return Failure("LAYOUT_LIST_COUNTER_INVALID","Compteur alphabétique invalide.");
                    if(semantic is not null)
                    {
                        int repeats=(value-1)/26+1;
                        if(repeats>128)return Failure("LAYOUT_LIST_COUNTER_LIMIT","Compteur alphabétique hors budget.");
                        char first=item.Format=="lowerLetter"?'a':'A';
                        return new string((char)(first+(value-1)%26),repeats);
                    }
                    string label="";while(value>0){value--;label=(char)('A'+value%26)+label;value/=26;}return item.Format=="lowerLetter"?label.ToLowerInvariant():label;
                }
                if(item.Format is "upperRoman" or "lowerRoman")
                {
                    if(value is <1 or >3999)return Failure("LAYOUT_ROMAN_COUNTER_UNQUALIFIED","Valeur romaine hors profil.");
                    string label="";foreach(var (number,symbol) in new[]{(1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I")})while(value>=number){label+=symbol;value-=number;}return item.Format=="lowerRoman"?label.ToLowerInvariant():label;
                }
                return Failure("LAYOUT_LIST_REFERENCE_UNQUALIFIED","Référence à un niveau non numérique.");
            }
            string label=level.Format=="none"?"":level.Format=="bullet"?level.Text:Regex.Replace(level.Text,"%([1-9])",match=>Formatted(int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture)-1));
            if(semantic is not null&&formatError)return null;
            if(semantic is null&&label.Contains('%'))Issue("LAYOUT_LIST_TEMPLATE_UNQUALIFIED",n,"Code de libellé non interprété.");
            if(semantic is not null&&(label.Length>128||label.Any(char.IsControl)))
            {Issue("LAYOUT_LIST_SYMBOL_UNQUALIFIED",n,"Libellé développé hors du profil borné.");return null;}
            return new ListLabel(id,index,label,level.Alignment,level.Suffix,level.Node.Source,Character(Child(level.Node,"rPr"),character))
            {Numbering=selection,ReferencedLevels=origins.ToArray()};
        }
    }
}
