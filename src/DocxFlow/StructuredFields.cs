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
    IReadOnlyDictionary<string,string>? documentProperties;

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
            else return false;
            return true;
        }
        if(tocOperands.Contains(field.Id))return true;
        if(field.Command=="TOC"&&snapshot.FieldStability=="Stable"&&format!=ExportFormat.Nova)
        {
            if(knownContents.Add(field.Id))
                Issue("EXPORT_TOC_RETAINED_ENTRIES",origin,
                    Odt?"Les entrées et renvois sont conservés sous forme éditable avec les titres et marques de sommaire.":
                    "Les entrées du sommaire sont conservées dans la lecture et la navigation est recalculée à partir des titres.",false);
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
        if(format==ExportFormat.Epub3&&folio&&syntax is not null&&
            (reflowStory||field.Command=="PAGEREF"&&field.ParentFieldId is {} parent&&model.Fields.Any(f=>f.Id==parent&&f.Command=="TOC")))
        {
            replaced.UnionWith(field.StoredResultIds);
            Issue("EXPORT_EPUB_REFLOW_FOLIO",origin,"Folio de la source retiré de la publication redistribuable.",false);
            return true;
        }
        if(field.Command=="DOCPROPERTY"&&field.ParentFieldId is null&&
            PagePropertyName(field) is {} property&&
            (documentProperties??=model.TextDocumentProperties(token.ThrowIfCancellationRequested)).TryGetValue(property,out var value))
        {
            Add(null,value);
            Issue("EXPORT_FIELD_VALUE_SNAPSHOT",origin,"La valeur vérifiée de la propriété est reprise comme texte éditable.",false);
            return true;
        }
        if(field.Command is not ("REF" or "PAGEREF")||syntax is not {Relative:false,DecimalPlaces:0}||field.BookmarkId is not {} target)
            return false;
        string? text=snapshot.FieldStability=="Stable"?calculatedFields.GetValueOrDefault(field.Id)?.Text:null;
        text??=field.StoredResultIds.IsEmpty?null:model.StoredText(field);
        if(text is null)return false;
        if(Odt)
        {
            Add(field.Command,text,target);
            Feature("fields",PublicationRetention.Native,origin.Location,"Champs ODT de page et de référence.");
            return true;
        }
        if(format==ExportFormat.Epub3||field.Command=="REF")
        {
            if(field.Command=="PAGEREF")
            {
                // Retain a useful EPUB link and its historical display value when it is outside the running matter.
                Issue("EXPORT_EPUB_PAGE_REFERENCE",origin,"Le renvoi garde sa destination et son libellé d’origine, selon la mise en page source.",false);
            }
            else Issue("EXPORT_FIELD_VALUE_SNAPSHOT",origin,"Le renvoi garde sa destination et le texte résolu. Le libellé reste éditable.",false);
            Add(null,text,target);
            return true;
        }
        return false;

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
        return item.Style is {} style?new XElement(Text+"span",
            new XAttribute(Text+"style-name",Register("text",OdtStyle(style,Family(item)))),result):result;
    }
}
