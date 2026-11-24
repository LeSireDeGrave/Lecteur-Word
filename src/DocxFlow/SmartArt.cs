using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal sealed record SmartArtShape(InventoryNode Drawing,InventoryNode ContentPoint,InventoryNode PresentationPoint,
    InventoryNode LayoutNode,InventoryNode QuickStyle,InventoryNode Colors,InventoryNode? TextLayout);
internal sealed record SmartArtPresentation(SourceLocation Source,InventoryNode Data,InventoryNode Layout,
    InventoryNode Styles,InventoryNode Colors,InventoryNode Drawing,IReadOnlyList<SmartArtShape> Shapes);

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        const string Diagram="http://schemas.openxmlformats.org/drawingml/2006/diagram";
        const string DiagramDrawing="http://schemas.microsoft.com/office/drawing/2008/diagram";
        const string DrawingRelation="http://schemas.microsoft.com/office/2007/relationships/diagramDrawing";
        const string DiagramContentType="application/vnd.openxmlformats-officedocument.drawingml.";
        readonly List<SmartArtPresentation> smartArt=[];
        ILookup<string,InventoryNode>? diagramRoots;
        ILookup<(string SourcePart,string Id),Relationship>? diagramLinks;
        ILookup<string,PartInventory>? diagramParts;

        bool ReadSmartArt(InventoryNode drawing)
        {
            var anchor=SemanticIndex.Unique(Elements(drawing));
            if(anchor is null||anchor.Name is not ("{"+WP+"}inline" or "{"+WP+"}anchor")||
                Member(anchor,D,"graphic") is not {} graphic||Member(graphic,D,"graphicData") is not {} data||
                Value(data,"uri")!=Diagram)return false;
            if(ResolveSmartArt(data) is {} presentation)
            {
                smartArt.Add(presentation);
                Issue("LAYOUT_SMARTART_RENDER_UNQUALIFIED",data,
                    $"Modèle, disposition, styles et couleurs reliés à {presentation.Shapes.Count} formes. Composition visuelle hors profil.");
            }
            return true;
        }

        SmartArtPresentation? ResolveSmartArt(InventoryNode graphic)
        {
            const int limit=1024;
            string from=graphic.Source.PartUri;
            if(Member(graphic,Diagram,"relIds") is not {} refs||Elements(graphic).Count()!=1||Elements(refs).Any()||
                Related(Ref("dm"),R+"/diagramData",DiagramContentType+"diagramData+xml",Diagram,"dataModel") is not {} data||
                Related(Ref("lo"),R+"/diagramLayout",DiagramContentType+"diagramLayout+xml",Diagram,"layoutDef") is not {} layout||
                Related(Ref("qs"),R+"/diagramQuickStyle",DiagramContentType+"diagramStyle+xml",Diagram,"styleDef") is not {} styles||
                Related(Ref("cs"),R+"/diagramColors",DiagramContentType+"diagramColors+xml",Diagram,"colorsDef") is not {} colors)
                return Refuse(graphic,"Parties SmartArt de données, disposition, styles ou couleurs manquantes ou ambiguës.");
            if(Member(data,Diagram,"extLst") is not {} extensions||
                SemanticIndex.Unique(Elements(extensions).Where(e=>e.Name=="{"+D+"}ext"&&Value(e,"uri")==DiagramDrawing)) is not {} extension||
                Member(extension,DiagramDrawing,"dataModelExt") is not {} reference||
                Related(Value(reference,"relId"),DrawingRelation,"application/vnd.ms-office.drawingml.diagramDrawing+xml",
                    DiagramDrawing,"drawing") is not {} cached||Member(cached,DiagramDrawing,"spTree") is not {} tree)
                return Refuse(data,"Dernière disposition SmartArt enregistrée absente ou non interprétable.");
            if(Member(data,Diagram,"ptLst") is not {} pointList)
                return Refuse(data,"Liste de points du modèle SmartArt absente.");

            var points=Elements(pointList).Where(n=>n.Name=="{"+Diagram+"}pt").Take(limit+1).ToArray();
            var drawings=Descendants(tree).Where(n=>n.Name=="{"+DiagramDrawing+"}sp").Take(limit+1).ToArray();
            var layoutNodes=Descendants(layout).Where(n=>n.Name=="{"+Diagram+"}layoutNode").Take(limit+1).ToArray();
            if(points.Length>limit||drawings.Length is 0 or >limit||layoutNodes.Length>limit)
                return Refuse(graphic,"Budget de points, de formes ou de nœuds de disposition SmartArt dépassé.");
            var byId=points.ToLookup(p=>Value(p,"modelId"),StringComparer.OrdinalIgnoreCase);
            if(byId.Any(g=>string.IsNullOrEmpty(g.Key)||g.Count()!=1))
                return Refuse(pointList,"Identifiant de point SmartArt absent ou dupliqué.");
            var pres=points.Select(p=>(Point:p,Properties:Member(p,Diagram,"prSet"))).Where(p=>p.Properties is not null)
                .ToLookup(p=>Value(p.Properties!,"presAssocID"),StringComparer.OrdinalIgnoreCase);
            var layoutNames=layoutNodes.ToLookup(n=>Value(n,"name"),StringComparer.Ordinal);
            var styleNames=Labels(styles);
            var colorNames=Labels(colors);
            var resolved=new List<SmartArtShape>(drawings.Length);
            foreach(var shape in drawings)
            {
                token.ThrowIfCancellationRequested();
                if(Value(shape,"modelId") is not {Length:>0} id||SemanticIndex.Unique(byId[id]) is not {} presentation||
                    Member(presentation,Diagram,"prSet") is not {} properties||Value(properties,"presAssocID") is not {Length:>0} associatedId||
                    SemanticIndex.Unique(byId[associatedId]) is not {} associated||Value(properties,"presName") is not {Length:>0} name||
                    SemanticIndex.Unique(layoutNames[name]) is not {} position||Value(properties,"presStyleLbl") is not {Length:>0} label||
                    SemanticIndex.Unique(styleNames[label]) is not {} style||SemanticIndex.Unique(colorNames[label]) is not {} color)
                    return Refuse(shape,"Association de la forme SmartArt à ses données, sa disposition ou son style ambiguë.");
                InventoryNode? textLayout=null;
                if(Member(shape,DiagramDrawing,"txBody") is {} body&&Descendants(body).Any(n=>n.Name=="{"+D+"}t"&&
                    children[n.Id].Any(c=>c.Kind=="textValue"&&!string.IsNullOrEmpty(c.Text))))
                {
                    textLayout=SemanticIndex.Unique(pres[associatedId].SelectMany(p=>layoutNames[Value(p.Properties!,"presName")])
                        .Where(n=>Member(n,Diagram,"alg") is {} algorithm&&Value(algorithm,"type")=="tx").DistinctBy(n=>n.Id));
                    if(textLayout is null)
                        return Refuse(shape,"Algorithme de mise en forme du libellé SmartArt absent ou ambigu.");
                }
                resolved.Add(new(shape,associated,presentation,position,style,color,textLayout));
            }
            return new(graphic.Source,data,layout,styles,colors,cached,resolved.ToArray());

            string? Ref(string name)=>Member(graphic,Diagram,"relIds")?.Attributes.GetValueOrDefault("{"+R+"}"+name);
            ILookup<string?,InventoryNode> Labels(InventoryNode root)=>Elements(root).Where(n=>n.Name=="{"+Diagram+"}styleLbl")
                .ToLookup(n=>Value(n,"name"),StringComparer.Ordinal);
            SmartArtPresentation? Refuse(InventoryNode n,string detail)
            {
                Issue("LAYOUT_SMARTART_DEPENDENCY_UNQUALIFIED",n,detail);
                return null;
            }
            InventoryNode? Related(string? id,string type,string contentType,string ns,string element)
            {
                if(string.IsNullOrEmpty(id))return null;
                diagramLinks??=input.Manifest.Relationships.ToLookup(r=>(r.SourcePart,r.Id));
                if(SemanticIndex.Unique(diagramLinks[(from,id)]) is not {Mode:"Internal",ResolvedPart:{} part} relation||
                    relation.Type!=type)return null;
                diagramParts??=input.Manifest.Parts.ToLookup(p=>p.PartUri,StringComparer.Ordinal);
                if(SemanticIndex.Unique(diagramParts[part])?.ContentType!=contentType)return null;
                diagramRoots??=nodes.Values.Where(n=>n.ParentId is null&&!n.Name.StartsWith('#'))
                    .ToLookup(n=>n.Source.PartUri,StringComparer.Ordinal);
                var root=SemanticIndex.Unique(diagramRoots[part]);
                return root?.Name=="{"+ns+"}"+element?root:null;
            }
        }

        InventoryNode? Member(InventoryNode parent,string ns,string local)=>
            SemanticIndex.Unique(Elements(parent).Where(n=>n.Name=="{"+ns+"}"+local));
    }
}
