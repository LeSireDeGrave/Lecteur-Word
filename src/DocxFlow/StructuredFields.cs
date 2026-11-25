using System.Globalization;
using System.Xml.Linq;

namespace LecteurWord.Flow;

internal sealed partial class StructuredPublication
{
    readonly IReadOnlyDictionary<string,SemanticTocEntry> tocEntries=source.Semantic!.TocEntries
        .ToDictionary(entry=>entry.FieldId,StringComparer.Ordinal);
    readonly IReadOnlySet<string> tocOperands=source.Semantic!.TocEntries.SelectMany(entry=>entry.OperandFieldIds)
        .ToHashSet(StringComparer.Ordinal);
    readonly IReadOnlyDictionary<string,PageFieldValue> calculatedFields=(source.Pagination?.FieldPasses.LastOrDefault()?.Fields??[])
        .Where(value=>value.Diagnostic is null&&value.Text is not null).GroupBy(value=>value.FieldId,StringComparer.Ordinal)
        .Where(values=>values.Count()==1).ToDictionary(values=>values.Key,values=>values.Single(),StringComparer.Ordinal);
    readonly HashSet<string> knownContents=new(StringComparer.Ordinal);
    readonly HashSet<string> mainBlocks=source.Semantic!.BodyBlocks.ToHashSet(StringComparer.Ordinal);
    readonly HashSet<string> visibleFieldResults=(source.Pagination?.Typography?.Tokens??[])
        .Where(item=>item.Role==TextRole.StoredField&&Visible(item)&&!string.IsNullOrWhiteSpace(item.Original))
        .Select(item=>item.Source.NodeId).ToHashSet(StringComparer.Ordinal);
    internal SortedDictionary<string,string> OdtDocumentProperties {get;}=new(StringComparer.Ordinal);
    IReadOnlyDictionary<string,string>? documentProperties;

    bool HasVisibleFieldResult(SemanticField field)=>field.StoredResultIds.Any(visibleFieldResults.Contains);

    bool TryStructuredField(SemanticField field,TextToken[] items,List<(int Index,Inline Item)> parts,HashSet<string> replaced)
    {
        if(!field.StructureVerified||field.Locked)return false;
        var origin=model.Source(field.Id);
        int order=model.Element(field.Id).Order;
        if(tocEntries.TryGetValue(field.Id,out var entry))
        {
            if(entry.SelectingTocIds.IsEmpty)return false;
            if(Odt)
                parts.Add((order,new(origin,entry.Label,null,null,Field:"TC",NumberFormat:entry.Level.ToString(CultureInfo.InvariantCulture))));
            else if(format==ExportFormat.Epub3)
            {
                string destination=Id(field.Id);
                parts.Add((order,new(origin,null,null,null,Anchor:destination)));
                Headings.Add((destination,entry.Level,entry.Label));
            }
            else if(ActiveNovaContents is not {} toc||!entry.SelectingTocIds.Contains(toc.Field))
                Reserve("EXPORT_NOVA_TOC_MARK",origin,"Le titre et le résultat du sommaire restent lisibles. La marque Word n’est plus dynamique.");
            return true;
        }
        if(tocOperands.Contains(field.Id))return true;
        if(nativeContents?.Field==field.Id)return true;
        if(field.Command=="TOC"&&snapshot.FieldStability=="Stable")
        {
            if(format==ExportFormat.Nova&&!HasVisibleFieldResult(field))return false;
            if(knownContents.Add(field.Id))
                Reserve("EXPORT_TOC_RETAINED_ENTRIES",origin,
                    Odt?"Les entrées et renvois sont conservés sous forme éditable avec les titres et marques de sommaire.":
                    format==ExportFormat.Nova?"Les entrées du sommaire sont conservées sous forme éditable.":"Les entrées du sommaire sont conservées dans la lecture et la navigation est recalculée à partir des titres.");
            Feature("contents",PublicationRetention.Structured,origin.Location,"Sommaire et destinations conservés.");
            return true;
        }

        var syntax=PageFieldSyntax.Read(model,field,allowCachedPageStyle:true);
        if(Odt&&field.Command is ("PAGE" or "NUMPAGES")&&syntax is {DecimalPlaces:0})
        {
            Add(field.Command,model.StoredText(field)??"",numbering:OdtNumberFormat(syntax.Format));
            Feature("fields",PublicationRetention.Native,origin.Location,"Champs ODT de page et de référence.");
            return true;
        }
        bool folio=field.Command is "PAGE" or "NUMPAGES" or "SECTIONPAGES" or "PAGEREF";
        if(!Odt&&folio&&syntax is not null&&
            (reflowStory||format==ExportFormat.Nova&&IsRunningStory(field)||format==ExportFormat.Epub3&&
                field.Command=="PAGEREF"&&field.ParentFieldId is {} parent&&model.Fields.Any(f=>f.Id==parent&&f.Command=="TOC")))
        {
            replaced.UnionWith(field.StoredResultIds);
            Issue("EXPORT_REFLOW_FOLIO",origin,"Le folio papier est écarté de la lecture redistribuable.",false);
            return true;
        }
        if(field.Command=="DOCPROPERTY"&&field.ParentFieldId is null&&
            PagePropertyName(field) is {} property&&
            (documentProperties??=model.TextDocumentProperties(token.ThrowIfCancellationRequested)).TryGetValue(property,out var value))
        {
            if(Odt)
            {
                string name=OdtDocumentProperties.Keys.FirstOrDefault(
                    key=>StringComparer.OrdinalIgnoreCase.Equals(key,property))??property;
                OdtDocumentProperties[name]=value;
                Add("DOCPROPERTY",value,numbering:name);
                Feature("fields",PublicationRetention.Native,origin.Location,"Propriété personnalisée et champ ODT actualisable.");
                return true;
            }
            Add(null,value);
            Reserve("EXPORT_FIELD_VALUE_SNAPSHOT",origin,"La valeur vérifiée de la propriété est reprise comme texte éditable sans actualisation.");
            return true;
        }
        if(folio&&!Odt&&snapshot.FieldStability=="Stable"&&calculatedFields.GetValueOrDefault(field.Id) is {Text:{} folioText}&&
            field.Command!="PAGEREF")
        {
            Add(null,folioText);
            Reserve("EXPORT_FIELD_VALUE_SNAPSHOT",origin,"Le numéro calculé dans la source est conservé comme texte, sans actualisation.");
            return true;
        }
        if(field.Command is not ("REF" or "PAGEREF")||syntax is not {Relative:false,DecimalPlaces:0}||field.BookmarkId is not {} target)
            return false;
        if(format==ExportFormat.Nova&&field.Command=="PAGEREF"&&field.ParentFieldId is null&&
            syntax.Format is null or "decimal"&&
            ParagraphOf(model,field.Id) is {} owner&&mainBlocks.Contains(owner)&&
            ParagraphOf(model,target) is {} bookmarkParagraph&&mainBlocks.Contains(bookmarkParagraph)&&
            marks[bookmarkParagraph].Any(mark=>mark.Id==target)&&
            model.Attribute(target,"name") is {} bookmarkName&&bookmarkName!="_GoBack"&&
            NovaPublication.ValidBookmarkName(bookmarkName))
        {
            Add("NOVA_PAGEREF",null,target);
            Reserve("EXPORT_NOVA_DYNAMIC_PAGE_REFERENCE",origin,
                "Le renvoi s’actualise dans OpusNova. Son libellé natif et la pagination peuvent changer.");
            Feature("nova-editorial-fields",PublicationRetention.Native,origin.Location,"Renvoi de page dynamique à un signet NOVA.");
            return true;
        }
        string? text=snapshot.FieldStability=="Stable"?calculatedFields.GetValueOrDefault(field.Id)?.Text:null;
        text??=format==ExportFormat.Nova&&!HasVisibleFieldResult(field)||field.StoredResultIds.IsEmpty?null:model.StoredText(field);
        if(text is null)return false;
        if(Odt)
        {
            Add(field.Command,text,target);
            Feature("fields",PublicationRetention.Native,origin.Location,"Champs ODT de page et de référence.");
            return true;
        }
        Reserve("EXPORT_FIELD_VALUE_SNAPSHOT",origin,"Le renvoi garde sa destination et le libellé d’origine sans actualisation.");
        Add(null,text,target);
        return true;

        void Add(string? command,string? value,string? destination=null,string? numbering=null)
        {
            var cache=field.StoredResultIds.ToHashSet(StringComparer.Ordinal);
            var styled=items.FirstOrDefault(i=>cache.Contains(i.Source.NodeId));
            parts.Add((order,new(origin,value,styled?.Style,null,Field:command,Target:destination,NumberFormat:numbering)));
            replaced.UnionWith(cache);
        }
    }

    string? PagePropertyName(SemanticField field)
    {
        string[] words=(PageFieldSyntax.Instruction(model,field)??"").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        return words is [_,var name]&&name.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_')?name:null;
    }

    static string? OdtNumberFormat(string? format)=>format switch
    {
        "decimal"=>"1","upperRoman"=>"I","lowerRoman"=>"i","upperLetter"=>"A","lowerLetter"=>"a",_=>null
    };

    XElement WriteOdtField(Inline item,string field)
    {
        if(field=="DOCPROPERTY")
            return new XElement(Text+"user-defined",new XAttribute(Text+"name",item.NumberFormat!),item.Value??"");
        if(field=="TC")
            return new XElement(Text+"toc-mark",new XAttribute(Text+"outline-level",item.NumberFormat??"1"),
                new XAttribute(Text+"string-value",item.Value??""));
        XElement result;
        if(item.Target is {} target)
            result=new XElement(Text+"bookmark-ref",new XAttribute(Text+"ref-name",Id(target)),
                new XAttribute(Text+"reference-format",field=="PAGEREF"?"page":"text"),item.Value??"");
        else
        {
            result=new XElement(Text+(field=="PAGE"?"page-number":"page-count"),item.Value??"");
            if(field=="PAGE")result.SetAttributeValue(Text+"select-page","current");
            if(item.NumberFormat is {} number)result.SetAttributeValue(Style+"num-format",number);
        }
        return result;
    }
}
