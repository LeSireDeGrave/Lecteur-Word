using System.Collections.Immutable;
using LecteurWord.DocxCore;
using static LecteurWord.Flow.SemanticIndex;

namespace LecteurWord.Flow;

internal static partial class SemanticReader
{
    internal const string CustomXmlRelation=R+"/customXml";

    sealed partial class Builder
    {
        const string PropertiesRelation=R+"/customXmlProps";
        const string PropertiesType="application/vnd.openxmlformats-officedocument.customXmlProperties+xml";
        const string Bibliography="http://schemas.openxmlformats.org/officeDocument/2006/bibliography";
        const string Store="http://schemas.openxmlformats.org/officeDocument/2006/customXml";
        const string BibliographyName="{"+Bibliography+"}";
        const string StoreName="{"+Store+"}";
        const string RelationshipName="{"+R+"}";
        const string NamespaceAttribute="{http://www.w3.org/2000/xmlns/}";

        ImmutableArray<SemanticCustomXml> BuildCustomXml(ImmutableArray<SemanticField> fields)
        {
            var links=manifest.Relationships.Where(r=>r.Type==CustomXmlRelation).ToArray();
            if(links.Length==0)return [];

            var bySource=manifest.Relationships.ToLookup(r=>r.SourcePart,StringComparer.Ordinal);
            var byTarget=manifest.Relationships.Where(r=>r.ResolvedPart is not null)
                .ToLookup(r=>r.ResolvedPart!,StringComparer.Ordinal);
            var types=manifest.Parts.ToDictionary(p=>p.PartUri,p=>p.ContentType,StringComparer.Ordinal);
            var rootNodes=index.Ordered.Where(n=>n.ParentId is null&&!n.Name.StartsWith('#'))
                .ToLookup(n=>n.Source.PartUri,StringComparer.Ordinal);
            var roots=links.Select(r=>Root(r.ResolvedPart)).ToArray();
            int bibliographies=roots.Count(n=>n?.Name==BibliographyName+"Sources");
            var allStores=index.Ordered.Where(n=>n.ParentId is null&&n.Name==StoreName+"datastoreItem")
                .ToLookup(n=>ParseGuid(Get(n,"itemID",Store)));

            InventoryNode? possibleConsumer=null;
            string? bibliographyConsumer=fields.FirstOrDefault(f=>f.StructureVerified&&f.Command is ("CITATION" or "BIBLIOGRAPHY"))?.Id;
            var explicitReferences=new Dictionary<(string Part,string Id),string>();
            var tracked=links.Concat(manifest.Relationships.Where(r=>r.Type==PropertiesRelation))
                .Select(r=>(r.SourcePart,r.Id)).ToHashSet();
            var bindings=new Dictionary<Guid,string>();

            if(bibliographies>0)
            foreach(var node in index.Ordered)
            {
                Check();
                if(node.Name.StartsWith('#'))continue;
                foreach(var (name,value) in node.Attributes)
                    if(name.StartsWith(RelationshipName,StringComparison.Ordinal)&&tracked.Contains((node.Source.PartUri,value)))
                        explicitReferences.TryAdd((node.Source.PartUri,value),node.Id);

                switch(node.Name.AsSpan(node.Name.IndexOf('}')+1))
                {
                    case "dataBinding":
                        if(Active(node)&&index.Parent(node) is {} parent&&Word(parent,"sdtPr")&&
                            (Word(node,"dataBinding")||node.Name=="{http://schemas.microsoft.com/office/word/2012/wordml}dataBinding")&&
                            ParseGuid(Get(node,"storeItemID",Word(node,"dataBinding")?W:"http://schemas.microsoft.com/office/word/2012/wordml")) is {} storeId)
                            bindings.TryAdd(storeId,node.Id);
                        possibleConsumer??=node;
                        break;
                    case "bibliography":
                        if(Active(node)&&Word(node,"bibliography")&&index.Parent(node) is {} control&&Word(control,"sdtPr"))
                            bibliographyConsumer??=node.Id;
                        possibleConsumer??=node;
                        break;
                    case "fldSimple":
                    case "fldChar":
                    case "instrText":
                    case "delInstrText":
                    case "docPartObj":
                    case "docPartList":
                    case "docPartGallery":
                    case "customXml":
                    case "customXmlPr":
                    case "altChunk":
                    case "subDoc":
                    case "mailMerge":
                    case "attachedTemplate":
                        possibleConsumer??=node;
                        break;
                }
            }

            var decisions=ImmutableArray.CreateBuilder<SemanticCustomXml>(links.Length);
            for(int i=0;i<links.Length;i++)
            {
                Check();
                var link=links[i];
                var target=roots[i];
                bool uniqueTarget=target is not null&&byTarget[target.Source.PartUri].Count()==1;
                var relations=uniqueTarget?bySource[target!.Source.PartUri].ToImmutableArray():[];
                var firstProps=Unique(relations.Where(r=>r.Type==PropertiesRelation));
                var properties=Root(firstProps?.ResolvedPart);
                var store=properties is null?null:ParseGuid(Get(properties,"itemID",Store));
                ImmutableArray<string> entries=uniqueTarget&&target!.Name==BibliographyName+"Sources"?
                    [..index.RawChildren(target).Where(n=>n.Name==BibliographyName+"Source").Select(n=>n.Id)]:[];
                var decision=new SemanticCustomXml(link,target?.Id,relations,properties?.Id,store,entries,
                    DependencyImpact.Undetermined,"CustomXml hors du profil bibliographique.");

                if(link.SourcePart!=index.Main||link.Mode!="Internal"||target is null||
                    !uniqueTarget||index.Relation(link.SourcePart,link.Id)!=link)
                    decision=decision with{Reason="Relation customXml externe, ambiguë ou hors du document principal."};
                else if(target.Name==BibliographyName+"Sources")
                    decision=Classify(decision,target,firstProps,properties);
                decisions.Add(decision);
            }
            return decisions.ToImmutable();

            InventoryNode? Root(string? part)=>part is null?null:Unique(rootNodes[part]);

            SemanticCustomXml Classify(SemanticCustomXml value,InventoryNode target,Relationship? propertiesLink,InventoryNode? properties)
            {
                if(bibliographies!=1||types.GetValueOrDefault(target.Source.PartUri) is not ("application/xml" or "text/xml")||
                    !ValidBibliography(target,value.Entries))
                    return value with{Reason="Collection bibliographique ambiguë ou structure hors profil."};
                if(value.PartRelations.Length!=1||propertiesLink is not {Mode:"Internal"}||properties is null||
                    value.StoreId is not {} id||types.GetValueOrDefault(properties.Source.PartUri)!=PropertiesType||
                    byTarget[properties.Source.PartUri].Count()!=1||bySource.Contains(properties.Source.PartUri)||
                    allStores[id].Count()!=1||!ValidProperties(properties))
                    return value with{Reason="Propriétés, schéma ou identifiant du magasin bibliographique non établis."};

                if(bindings.TryGetValue(id,out var binding))
                    return value with{Impact=DependencyImpact.AffectsLayout,Reason="Contrôle de contenu lié au magasin bibliographique.",ConsumerId=binding};
                if(bibliographyConsumer is not null)
                    return value with{Impact=DependencyImpact.AffectsLayout,Reason="Champ ou contrôle bibliographique présent.",ConsumerId=bibliographyConsumer};
                if(explicitReferences.TryGetValue((value.Relation.SourcePart,value.Relation.Id),out var reference)||
                    explicitReferences.TryGetValue((propertiesLink.SourcePart,propertiesLink.Id),out reference))
                    return value with{Reason="Relation consommée par une référence XML explicite.",ConsumerId=reference};
                if(possibleConsumer is not null)
                    return value with{Reason="Champ ou liaison sur lequel l’indépendance du magasin reste indéterminée.",ConsumerId=possibleConsumer.Id};
                return value with{Impact=DependencyImpact.NoLayoutEffect,
                    Reason="Collection et magasin identifiés, sans champ, contrôle mappé ni référence consommatrice dans l’inventaire XML."};
            }
        }

        bool ValidBibliography(InventoryNode root,ImmutableArray<string> entries)
        {
            if(!root.Attributes.Keys.All(k=>k.StartsWith(NamespaceAttribute,StringComparison.Ordinal)||
                k is "{}SelectedStyle" or "{}StyleName" or "{}Version"))return false;
            foreach(var node in index.ByPart[root.Source.PartUri])
            {
                Check();
                if(!node.Name.StartsWith('#')&&!node.Name.StartsWith(BibliographyName,StringComparison.Ordinal))return false;
            }
            if(!ChildrenAre(root,BibliographyName+"Source"))return false;

            var tags=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids=new HashSet<Guid>();
            foreach(string entry in entries)
            {
                Check();
                var source=index.Nodes[entry];
                var tag=Unique(index.RawChildren(source).Where(n=>n.Name==BibliographyName+"Tag"));
                if(tag is null||LeafText(tag) is not {Length:>0} name||!tags.Add(name))return false;
                var guids=index.RawChildren(source).Where(n=>n.Name==BibliographyName+"Guid").Take(2).ToArray();
                if(guids.Length>1||guids.Length==1&&(ParseGuid(LeafText(guids[0])) is not {} guid||!ids.Add(guid)))return false;
            }
            return true;
        }

        bool ValidProperties(InventoryNode properties)
        {
            if(properties.Name!=StoreName+"datastoreItem"||!ChildrenAre(properties,StoreName+"schemaRefs"))return false;
            var schemas=Unique(index.RawChildren(properties).Where(n=>n.Name==StoreName+"schemaRefs"));
            if(schemas is null||!ChildrenAre(schemas,StoreName+"schemaRef"))return false;
            var schema=Unique(index.RawChildren(schemas).Where(n=>n.Name==StoreName+"schemaRef"));
            return schema is not null&&Get(schema,"uri",Store)==Bibliography&&ChildrenAre(schema,null)&&
                AttributesAre(properties,StoreName+"itemID")&&AttributesAre(schemas,null)&&AttributesAre(schema,StoreName+"uri");
        }

        bool ChildrenAre(InventoryNode node,string? name)
        {
            foreach(var child in index.RawChildren(node))
            {
                Check();
                if(child.Name==name)continue;
                if(!child.Name.StartsWith('#')||child.Kind=="textValue"&&!string.IsNullOrWhiteSpace(child.Text))return false;
            }
            return true;
        }

        string? LeafText(InventoryNode node)
        {
            var children=index.RawChildren(node);
            if(children.Any(n=>!n.Name.StartsWith('#')))return null;
            return string.Concat(children.Where(n=>n.Kind=="textValue").Select(n=>n.Text)).Trim();
        }

        static bool AttributesAre(InventoryNode node,string? name) =>
            node.Attributes.Keys.All(k=>k==name||k.StartsWith(NamespaceAttribute,StringComparison.Ordinal));

        static Guid? ParseGuid(string? value) =>
            (Guid.TryParseExact(value,"B",out var guid)||Guid.TryParseExact(value,"D",out guid))&&guid!=Guid.Empty?guid:null;
    }
}
