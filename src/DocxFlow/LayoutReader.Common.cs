using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        string ReadColor(InventoryNode n,string fallback="val",string themeKey="themeColor",string tintKey="themeTint",string shadeKey="themeShade",
            string automatic="000000")
        {
            string color=A(n,fallback),theme=A(n,themeKey);
            if(tableCascade is null&&(A(n,"themeTint").Length>0||A(n,"themeShade").Length>0))Issue("LAYOUT_COLOR_TRANSFORM_UNQUALIFIED",n,"Transformation de couleur de thème hors profil.");
            if(theme.Length>0&&theme!="none")
            {
                var names=new Dictionary<string,string>{{"text1","dk1"},{"text2","dk2"},{"background1","lt1"},{"background2","lt2"},{"hyperlink","hlink"},{"followedHyperlink","folHlink"},{"dark1","dk1"},{"dark2","dk2"},{"light1","lt1"},{"light2","lt2"}};
                string slot=names.GetValueOrDefault(theme)??theme;
                var mapping=input.Manifest.Nodes.FirstOrDefault(x=>Is(x,"clrSchemeMapping"));
                string mapKey=theme switch{"text1"=>"t1","text2"=>"t2","background1"=>"bg1","background2"=>"bg2",_=>theme};
                if(mapping is not null&&A(mapping,mapKey) is {Length:>0} mapped)slot=names.GetValueOrDefault(mapped)??mapped;
                string? part=input.Manifest.Relationships.FirstOrDefault(r=>r.Type==R+"/theme"&&r.Mode=="Internal")?.ResolvedPart;
                var scheme=input.Manifest.Nodes.FirstOrDefault(x=>x.Source.PartUri==part&&x.Name=="{"+D+"}clrScheme");
                var entry=scheme is null?null:children[scheme.Id].FirstOrDefault(x=>x.Name=="{"+D+"}"+slot);
                var colors=entry is null?[]:Elements(entry).ToArray();var rgb=colors.Length==1?colors[0]:null;
                color=rgb?.Name=="{"+D+"}srgbClr"?rgb.Attributes.GetValueOrDefault("{}val")??"":rgb?.Name=="{"+D+"}sysClr"?rgb.Attributes.GetValueOrDefault("{}lastClr")??"":"";
                if(rgb is null||Elements(rgb).Any())Issue("LAYOUT_THEME_COLOR_UNQUALIFIED",n,"Couleur de thème absente ou transformée.");
                if(tableCascade is not null)
                {
                    var transformed=WordColor.ApplyLuminance(color,A(n,tintKey),A(n,shadeKey));
                    if(transformed is null)Issue("LAYOUT_COLOR_TRANSFORM_UNQUALIFIED",n,"Teinte ou ombre OOXML invalide.");
                    else color=transformed;
                }
            }
            return color=="auto"?automatic:color;
        }
        bool EmptyNoteDependency(Relationship relation)
        {
            if(relation.Type is not (R+"/footnotes" or R+"/endnotes")||relation.Mode!="Internal"||relation.ResolvedPart is null)return false;
            string item=relation.Type==R+"/footnotes"?"footnote":"endnote";
            var roots=input.Manifest.Nodes.Where(n=>n.Source.PartUri==relation.ResolvedPart&&Is(n,item+"s")).ToArray();if(roots.Length!=1)return false;
            var notes=Elements(roots[0]).ToArray();
            foreach(var note in notes)
            {
                if(!Is(note,item)||A(note,"id") is not ("-1" or "0")||A(note,"type") is not ("separator" or "continuationSeparator"))return false;
                foreach(var n in Descendants(note))
                    if(n.Kind=="textValue"?!string.IsNullOrWhiteSpace(n.Text):!new[]{item,"p","pPr","spacing","r","separator","continuationSeparator"}.Any(local=>Is(n,local)))return false;
            }
            Issue("LAYOUT_EMPTY_NOTE_PART",roots[0],"Partie de notes sans note utilisateur, limitée aux séparateurs : inventaire conservé.",false);return true;
        }
        IEnumerable<InventoryNode> InlineRuns(InventoryNode container,bool inLink=false)
        {
            foreach(var n in children[container.Id].Where(c=>!Is(c,"pPr")))
            {
                token.ThrowIfCancellationRequested();
                if(Is(n,"hyperlink")&&!inLink)
                {
                    Attributes(n,"{"+R+"}id","{"+W+"}anchor","{"+W+"}history","{"+W+"}tooltip","{"+W+"}tgtFrame","{"+W+"}docLocation");
                    string? id=n.Attributes.GetValueOrDefault("{"+R+"}id");
                    if(id is not null&&!input.Manifest.Relationships.Any(r=>r.SourcePart==n.Source.PartUri&&r.Id==id&&r.Type==R+"/hyperlink"))Issue("LAYOUT_HYPERLINK_INVALID",n,"Relation du lien absente ou incohérente.");
                    Issue("LAYOUT_HYPERLINK_TEXT",n,"Texte du lien composé ; cible et structure conservées dans l’inventaire, sans chargement externe.",false);
                    foreach(var run in InlineRuns(n,true))yield return run;
                }
                else if(Is(n,"bookmarkStart")||Is(n,"bookmarkEnd")||Is(n,"proofErr"))
                {
                    if(Elements(n).Any())Issue("LAYOUT_MARKER_CONTENT_UNQUALIFIED",n,"Contenu inattendu dans une marque informative.");
                    Issue("LAYOUT_INFORMATIONAL_MARKER",n,"Marque de navigation/correction conservée dans l’inventaire et sa provenance.",false);
                }
                else if(readingStory&&Is(n,"fldSimple"))
                {
                    WAttributes(n,"instr","fldLock","dirty");
                    foreach(var run in InlineRuns(n,inLink))yield return run;
                }
                else yield return n;
            }
        }
    }
}
