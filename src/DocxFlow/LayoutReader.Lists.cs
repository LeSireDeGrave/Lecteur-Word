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
        readonly Dictionary<(string Id,int Level),(InventoryNode? Node,int Start,int Restart,string Format,string Text,string Alignment,string Suffix)> levels=[];
        string defaultParagraphStyle="";
        void ReadSettings()
        {
            var settings=input.Manifest.Relationships.FirstOrDefault(r=>r.Type==R+"/settings"&&r.Mode=="Internal")?.ResolvedPart;
            var tab=input.Manifest.Nodes.FirstOrDefault(n=>n.Source.PartUri==settings&&Is(n,"defaultTabStop"));
            if(tab is not null)paragraphDefaults=paragraphDefaults with{DefaultTab=Number(tab,"val",36,1,1440,20)};
            defaultParagraphStyle=styles.Values.FirstOrDefault(n=>A(n,"type")=="paragraph"&&A(n,"default") is "1" or "true") is {} ds?A(ds,"styleId"):"";
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
                if(A(c,"leader") is not ("" or "none"))Issue("LAYOUT_TAB_LEADER_UNQUALIFIED",c,"Points de suite non qualifiés.");
                if(align=="clear")tabs.Remove(pos);
                else if(align=="num")tabs[pos]=new(pos,"numbering");
                else if(align is "left" or "right" or "center" or "decimal")tabs[pos]=new(pos,align);
                else Issue("LAYOUT_TAB_ALIGNMENT_UNQUALIFIED",c,"Alignement de tabulation non qualifié : "+align);
            }
            return p with{Tabs=tabs.Values.OrderBy(t=>t.Position).ToArray()};
        }
        (InventoryNode? Node,int Start,int Restart,string Format,string Text,string Alignment,string Suffix) Level(string id,int level)
        {
            if(levels.TryGetValue((id,level),out var cached))return cached;
            var numbering=input.Manifest.Relationships.FirstOrDefault(r=>r.Type==R+"/numbering"&&r.Mode=="Internal")?.ResolvedPart;
            var instances=input.Manifest.Nodes.Where(n=>n.Source.PartUri==numbering&&Is(n,"num")&&A(n,"numId")==id).ToArray();
            InventoryNode? lvl=null,over=null;int? startOverride=null;
            if(instances.Length==1)
            {
                var instance=instances[0];WAttributes(instance,"numId");
                var abstractId=Child(instance,"abstractNumId");
                var definitions=input.Manifest.Nodes.Where(n=>n.Source.PartUri==numbering&&Is(n,"abstractNum")&&A(n,"abstractNumId")== (abstractId is null?"":A(abstractId,"val"))).ToArray();
                if(definitions.Length==1)
                {
                    var definition=definitions[0];WAttributes(definition,"abstractNumId");
                    foreach(var c in children[definition.Id].Where(c=>!c.Name.StartsWith('#')))
                        if(!new[]{"lvl","nsid","multiLevelType","tmpl","name"}.Any(local=>Is(c,local)))Issue("LAYOUT_NUMBERING_LINK_UNQUALIFIED",c,"Définition de liste liée ou spécialisée non qualifiée.");
                    var matches=children[definition.Id].Where(c=>Is(c,"lvl")&&A(c,"ilvl")==level.ToString(CultureInfo.InvariantCulture)).ToArray();
                    if(matches.Length==1)lvl=matches[0];
                }
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
            if(lvl is null){Issue("LAYOUT_NUMBERING_MISSING",instances.FirstOrDefault(),"Instance/niveau de numérotation absent ou ambigu : "+id+" / "+level);return levels[(id,level)]=(null,1,level,"decimal","%"+(level+1)+".","left","tab");}
            WAttributes(lvl,"ilvl","tentative","tplc");
            int start=1,restart=level;string format="decimal",text="",alignment="left",suffix="tab";
            foreach(var c in children[lvl.Id].Where(c=>!c.Name.StartsWith('#')))
            {
                if(Is(c,"start")){WAttributes(c,"val");start=(int)Number(c,"val",1,0,999999);}
                else if(Is(c,"lvlRestart")){WAttributes(c,"val");restart=(int)Number(c,"val",level,0,level);}
                else if(Is(c,"numFmt")){WAttributes(c,"val");format=A(c,"val");}
                else if(Is(c,"lvlText")){WAttributes(c,"val");text=A(c,"val");}
                else if(Is(c,"lvlJc")){WAttributes(c,"val");alignment=A(c,"val");}
                else if(Is(c,"suff")){WAttributes(c,"val");suffix=A(c,"val");}
                else if(!Is(c,"pPr")&&!Is(c,"rPr"))Issue("LAYOUT_NUMBERING_LEVEL_UNQUALIFIED",c,"Propriété de niveau hors profil.");
            }
            if(format is not ("decimal" or "upperLetter" or "lowerLetter" or "upperRoman" or "lowerRoman" or "bullet" or "none"))Issue("LAYOUT_NUMBER_FORMAT_UNQUALIFIED",lvl,"Format numérique hors profil : "+format);
            if(alignment is not ("left" or "right" or "center")||suffix is not ("tab" or "space" or "nothing"))Issue("LAYOUT_NUMBER_SUFFIX_UNQUALIFIED",lvl,"Alignement ou suffixe de liste hors profil.");
            if(text.Length>128||format=="bullet"&&text is not ("•" or "·" or "-" or "–"))Issue("LAYOUT_LIST_SYMBOL_UNQUALIFIED",lvl,"Libellé ou glyphe de puce hors profil.");
            return levels[(id,level)]=(lvl,startOverride??start,restart,format,text,alignment,suffix);
        }
        ListLabel? PrepareList(InventoryNode n,InventoryNode? pp,ref ParagraphLayout p,LayoutStyle character,string styleId)
        {
            if(p.NumId is null or "0" or "")
            {if(p.Tabs.Any(t=>t.Alignment=="numbering"))Issue("LAYOUT_NUMBER_TAB_WITHOUT_LIST",n,"Tabulation de numérotation sans liste.");return null;}
            string id=p.NumId;int index=p.ListLevel;var level=Level(id,index);if(level.Node is null)return null;
            if(level.Suffix!="tab")Issue("LAYOUT_LIST_SUFFIX_UNQUALIFIED",n,"Suffixe autre qu’une tabulation non qualifié dans ce profil.");
            // Numbering properties precede paragraph styles and direct properties.
            p=Paragraph(Child(level.Node,"pPr"),paragraphDefaults);
            foreach(var style in Chain(styleId))p=Paragraph(Child(style,"pPr"),p);
            p=Paragraph(pp,p) with{NumId=id,ListLevel=index};
            if(p.Alignment!="left")Issue("LAYOUT_LIST_ALIGNMENT_UNQUALIFIED",n,"Alignement du paragraphe de liste hors profil.");
            if(Child(level.Node,"pPr") is null&&p.Left==0&&p.FirstLine==0)
            {p=p with{Left=18*(index+1),FirstLine=-18};Issue("LAYOUT_LIST_DEFAULT_INDENT",n,"Retrait de liste absent : retrait de lecture explicite de 18 points par niveau.",false);}
            float textLeft=p.Left;
            if(p.Tabs.Any(t=>t.Alignment=="numbering"&&Math.Abs(t.Position-textLeft)>.01f))Issue("LAYOUT_LIST_TAB_POSITION_UNQUALIFIED",n,"Tabulation de numérotation différente du retrait de texte hors profil.");
            if(!counters.TryGetValue(id,out var state))counters[id]=state=new int?[9];
            state[index]=state[index].HasValue?state[index]!.Value+1:level.Start;
            for(int i=index+1;i<9;i++)
            {
                // Only inspect defined descendants, avoiding fabricated missing-level errors.
                if(!state[i].HasValue)continue;var child=Level(id,i);if(child.Restart>0&&child.Restart-1>=index)state[i]=null;
            }
            string Formatted(int i)
            {
                var item=Level(id,i);int value=state[i]??item.Start;if(value>999999){Issue("LAYOUT_LIST_COUNTER_LIMIT",n,"Compteur de liste hors budget.");return "?";}
                if(item.Format=="decimal")return value.ToString(CultureInfo.InvariantCulture);
                if(item.Format is "upperLetter" or "lowerLetter")
                {
                    if(value<1){Issue("LAYOUT_LIST_COUNTER_INVALID",n,"Compteur alphabétique invalide.");return "?";}
                    string label="";while(value>0){value--;label=(char)('A'+value%26)+label;value/=26;}return item.Format=="lowerLetter"?label.ToLowerInvariant():label;
                }
                if(item.Format is "upperRoman" or "lowerRoman")
                {
                    if(value is <1 or >3999){Issue("LAYOUT_ROMAN_COUNTER_UNQUALIFIED",n,"Valeur romaine hors profil.");return "?";}
                    string label="";foreach(var (number,symbol) in new[]{(1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I")})while(value>=number){label+=symbol;value-=number;}return item.Format=="lowerRoman"?label.ToLowerInvariant():label;
                }
                Issue("LAYOUT_LIST_REFERENCE_UNQUALIFIED",n,"Référence à un niveau non numérique.");return "?";
            }
            string label=level.Format=="none"?"":level.Format=="bullet"?level.Text:Regex.Replace(level.Text,"%([1-9])",match=>Formatted(int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture)-1));
            if(label.Contains('%'))Issue("LAYOUT_LIST_TEMPLATE_UNQUALIFIED",n,"Code de libellé non interprété.");
            var marker=Character(Child(level.Node,"rPr"),character);
            return new(id,index,label,level.Alignment,level.Suffix,level.Node.Source,marker);
        }
    }
}
