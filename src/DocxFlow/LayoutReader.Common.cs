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
            if(theme.Length>0&&theme!="none")
            {
                string? settings=RelatedPart("settings");
                var mapping=input.Manifest.Nodes.FirstOrDefault(x=>x.Source.PartUri==settings&&Is(x,"clrSchemeMapping"));
                string slot=WordColor.ThemeSlot(mapping is not null&&A(mapping,WordColor.MappingKey(theme)) is {Length:>0} mapped?mapped:theme);
                string? part=RelatedPart("theme");
                var scheme=input.Manifest.Nodes.FirstOrDefault(x=>x.Source.PartUri==part&&x.Name=="{"+D+"}clrScheme");
                var entry=scheme is null?null:children[scheme.Id].FirstOrDefault(x=>x.Name=="{"+D+"}"+slot);
                var colors=entry is null?[]:Elements(entry).ToArray();var rgb=colors.Length==1?colors[0]:null;
                color=rgb?.Name=="{"+D+"}srgbClr"?rgb.Attributes.GetValueOrDefault("{}val")??"":rgb?.Name=="{"+D+"}sysClr"?rgb.Attributes.GetValueOrDefault("{}lastClr")??"":"";
                if(rgb is null||Elements(rgb).Any())Issue("LAYOUT_THEME_COLOR_UNQUALIFIED",n,"Couleur de thème absente ou transformée.");
                var transformed=WordColor.ApplyLuminance(color,n.Attributes.GetValueOrDefault("{"+W+"}"+tintKey),
                    n.Attributes.GetValueOrDefault("{"+W+"}"+shadeKey));
                if(transformed is null)Issue("LAYOUT_COLOR_TRANSFORM_UNQUALIFIED",n,"Teinte ou ombre OOXML invalide.");
                else color=transformed;
            }
            return color=="auto"?automatic:color;
        }

        string? RelatedPart(string type)
        {
            if(semantic is not null)return semantic.RelatedPart(type);
            var main=SemanticIndex.Unique(input.Manifest.Relationships.Where(r=>r.SourcePart=="/"&&r.Type==R+"/officeDocument"));
            return SemanticIndex.Unique(input.Manifest.Relationships.Where(r=>r.SourcePart==main?.ResolvedPart&&
                r.Type==R+"/"+type&&r.Mode=="Internal"))?.ResolvedPart;
        }
        bool ReadEquivalentSectionNoteProperties(InventoryNode properties,bool endnote)
        {
            var settings=semantic!.RelatedRoot("settings","settings");
            var document=settings is null?null:Child(nodes[settings],endnote?"endnotePr":"footnotePr");
            var declared=Elements(properties).ToArray();
            var global=document is null?[]:Elements(document).ToArray();
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var option in declared)
            {
                token.ThrowIfCancellationRequested();
                string? fallback=option.Name switch
                {
                    "{"+W+"}pos"=>endnote?"":"pageBottom",
                    "{"+W+"}numFmt"=>endnote?"lowerRoman":"decimal",
                    "{"+W+"}numStart"=>"1",
                    "{"+W+"}numRestart"=>"continuous",
                    _=>null
                };
                if(fallback is null||!seen.Add(option.Name)||Elements(option).Any()||A(option,"val").Length==0)return false;
                var inherited=global.Where(n=>n.Name==option.Name).Take(2).ToArray();
                if(inherited.Length>1||inherited.Length==1&&A(inherited[0],"val").Length==0)return false;
                string effective=inherited.Length==0?fallback:A(inherited[0],"val");
                string local=A(option,"val");
                if(local!=effective&&(endnote||!Is(option,"pos")||
                    local is not ("sectEnd" or "beneathText")||effective is not ("sectEnd" or "beneathText")))return false;
                WAttributes(option,"val");
            }
            WAttributes(properties);
            Issue(endnote?"PAGE_SECTION_ENDNOTE_PROPERTIES_EQUIVALENT":"PAGE_SECTION_FOOTNOTE_PROPERTIES_EQUIVALENT",properties,
                "Les dérogations de la section reproduisent la position et la numérotation du document.",false);
            return true;
        }

        bool ReadSectionEndnoteProperties(InventoryNode properties)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var option in Elements(properties))
            {
                if(!names.Add(option.Name)||Elements(option).Any()||
                    !(Is(option,"numFmt")||Is(option,"numStart")||Is(option,"numRestart")||
                        Is(option,"pos")&&A(option,"val") is ("sectEnd" or "docEnd")))
                    return false;
                WAttributes(option,"val");
            }
            WAttributes(properties);
            return true;
        }

        internal HashSet<string> InactiveNoteParts {get;}=new(StringComparer.Ordinal);

        bool EmptyNoteDependency(Relationship relation)
        {
            if(relation.Type is not (R+"/footnotes" or R+"/endnotes")||relation.Mode!="Internal"||relation.ResolvedPart is null)return false;
            string item=relation.Type==R+"/footnotes"?"footnote":"endnote";
            var roots=input.Manifest.Nodes.Where(n=>n.Source.PartUri==relation.ResolvedPart&&Is(n,item+"s")).ToArray();if(roots.Length!=1)return false;
            var notes=Elements(roots[0]).ToArray();
            if(nativeComposition&&semantic is not null)
            {
                // Separator, continuation separator and continuation notice are templates.
                // With no regular entries or calls, no page can instantiate these stories.
                if(semantic.References.Any(r=>r.Kind==item)||notes.Any(n=>!Is(n,item)||
                    A(n,"type") is not ("separator" or "continuationSeparator" or "continuationNotice")||
                    !int.TryParse(A(n,"id"),System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,out _))||
                    notes.Select(n=>A(n,"type")).Distinct(StringComparer.Ordinal).Count()!=notes.Length||
                    notes.Select(n=>int.Parse(A(n,"id"),System.Globalization.CultureInfo.InvariantCulture)).Distinct().Count()!=notes.Length)
                    return false;
                InactiveNoteParts.Add(relation.ResolvedPart);
                Issue("LAYOUT_EMPTY_NOTE_PART",roots[0],"Modèles de notes sans aucun appel ni entrée ordinaire. Sources conservées.",false);
                return true;
            }
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
                else if(nativeComposition&&semantic!.ProcessesContent(n.Id))
                {
                    foreach(var run in InlineRuns(n,inLink))yield return run;
                }
                else if(Is(n,"fldSimple")&&(readingStory||IsNativeFieldToken(n)))
                {
                    WAttributes(n,"instr","fldLock","dirty");
                    foreach(var run in InlineRuns(n,inLink))yield return run;
                }
                else yield return n;
            }
        }

        IEnumerable<InventoryNode> RunContents(InventoryNode run,bool preserveFloatingAlternatives)
        {
            foreach(var leaf in children[run.Id].Where(c=>!Is(c,"rPr")))
            {
                if(nativeComposition&&SemanticIndex.IsAlternative(leaf)&&
                    !(preserveFloatingAlternatives&&IsFloatingTextAlternative(leaf)))
                {
                    foreach(var selected in semantic!.StylePropertyChildren(leaf.Id))yield return selected;
                }
                else yield return leaf;
            }
        }
    }
}
