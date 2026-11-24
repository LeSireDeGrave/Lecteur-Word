using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        Dictionary<string,SemanticField>? nativeFieldTokens;

        bool IsNativeFieldToken(InventoryNode node)
        {
            if(!nativeComposition||semantic is null)return false;
            if(nativeFieldTokens is null)
            {
                nativeFieldTokens=new(StringComparer.Ordinal);
                foreach(var field in semantic.Fields)
                {
                    token.ThrowIfCancellationRequested();
                    if(!field.StructureVerified)continue;
                    nativeFieldTokens[field.Id]=field;
                    foreach(string id in field.InstructionIds)
                        if(semantic.ParentId(id) is {} instruction&&Is(nodes[instruction],"instrText")&&
                            children[instruction].All(child=>child.Kind=="textValue"))
                            nativeFieldTokens[instruction]=field;
                    if(field.SeparatorId is {} separator)nativeFieldTokens[separator]=field;
                    if(field.EndId is {} end)nativeFieldTokens[end]=field;
                }
            }
            if(!nativeFieldTokens.TryGetValue(node.Id,out var match))return false;
            if(node.Id!=match.Id||readingStory)return true;
            if(calculatedBodyFields?.Contains(match.Id)==true)return true;
            if(semantic.TocMarkerFields.Contains(match.Id))return true;

            string? storedText=semantic.StoredText(match);
            bool stored=match.Command=="TOC"?!string.IsNullOrWhiteSpace(storedText):storedText is {Length:>0};
            if(match.Command!="TC"&&stored&&(match.Locked||match.Command is "TOC" or "DOCPROPERTY"))
                return true;
            if(match.Command is "PAGE" or "NUMPAGES" or "PAGEREF" or "REF" or "SECTIONPAGES")
            {
                bool missing=match.Command is ("PAGEREF" or "REF")&&match.BookmarkId is null;
                bool switchPending=!match.Locked&&!stored&&PageFieldSyntax.Read(semantic,match) is null;
                Issue(missing?"PAGE_FIELD_BOOKMARK_UNRESOLVED":
                    stored?"PAGE_FIELD_STORED_PENDING":switchPending?"PAGE_FIELD_SWITCH_UNQUALIFIED":"PAGE_FIELD_VALUE_UNRESOLVED",node,
                    missing?"Signet du renvoi absent ou ambigu.":
                    stored?"Résultat enregistré conservé, actualisation paginée indéterminée.":
                    switchPending?"Instruction ou option de numérotation hors profil.":
                    "Champ sans résultat enregistré, valeur paginée indéterminée.");
            }
            else
                Issue("PAGE_FIELD_PROFILE_UNQUALIFIED",node,"Champ préservé dont le résultat visible reste hors profil : "+match.Command+".");
            return true;
        }
    }
}
