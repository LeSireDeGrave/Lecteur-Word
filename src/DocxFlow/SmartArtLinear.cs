using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        sealed record DiagramGraph(string RootId,IReadOnlyDictionary<string,InventoryNode> Nodes,
            IReadOnlyDictionary<string,ImmutableArray<string>> Children,ImmutableArray<DiagramEdge> Edges);
        readonly record struct LinearPlan(string Shape,string Direction,float Width,float Height,float FontSize,string Label,SourceLocation Source);

        InlineImage? ComposeSmartArtData(InventoryNode drawing,InventoryNode anchor,SmartArtParts parts,out string reason)
        {
            reason="Le dessin SmartArt est absent. La géométrie inline ou le graphe de données est hors profil.";
            if(anchor.Name!="{"+WP+"}inline"||
                anchor.Attributes.Any(p=>p.Key.StartsWith("{}dist",StringComparison.Ordinal)&&p.Value!="0")||
                !OnlyAttributes(anchor,"{}distL","{}distR","{}distT","{}distB","{"+WP14+"}anchorId","{"+WP14+"}editId")||
                !Elements(anchor).All(n=>n.Name is "{"+WP+"}extent" or "{"+WP+"}effectExtent" or "{"+WP+"}docPr" or
                    "{"+WP+"}cNvGraphicFramePr" or "{"+D+"}graphic")||
                Member(anchor,WP,"extent") is not {} extent||!ReadEmu(extent,"cx",out float width)||
                !ReadEmu(extent,"cy",out float height)||width<=0||height<=0||width>1584||height>1584||
                Member(anchor,WP,"effectExtent") is {} effect&&
                    (Elements(effect).Any()||effect.Attributes.Any(p=>p.Value!="0"))||
                Member(anchor,WP,"docPr") is not {} info||!DiagramCreationMetadata(info)||ReadDiagramGraph(parts.Data) is not {} graph)
                return null;
            var layout=Member(parts.Layout,Diagram,"layoutNode");
            string? algorithm=layout is null?null:Value(Member(layout,Diagram,"alg")??layout,"type");
            DiagramDrawing? composed=null;
            if(layout is not null&&algorithm=="lin"&&ReadLinearPlan(layout) is {} plan)
            {
                var shapes=ArrangeLinear(graph,plan,width,height,parts,token);
                if(!shapes.IsDefaultOrEmpty)composed=new(shapes)
                {
                    LayoutProfile="linear-direct",Model=DiagramOrigins(graph,parts),
                    Groups=[new(layout.Source,"lin",new(0,0,width,height),[..shapes.Select(shape=>new DiagramGroup(
                        plan.Source,"tx",shape.Box,[]))])]
                };
            }
            if(layout is not null&&composed is null)composed=ArrangeDiagram(layout,graph,parts,width,height);
            if(composed is null)
            {
                reason="Disposition SmartArt sans dessin enregistré hors profil ("+(algorithm??"algorithme absent")+
                    "). Vérifier le graphe, les contraintes, les styles et les libellés.";
                return null;
            }
            return new(drawing.Id,drawing.Source,width,height,0,0,semantic!.SnapshotId){Diagram=composed};
        }

        DiagramGraph? ReadDiagramGraph(InventoryNode data)
        {
            string dgm="{"+Diagram+"}";
            if(Member(data,Diagram,"ptLst") is not {} points||Member(data,Diagram,"cxnLst") is not {} connections||
                !Elements(data).All(n=>n.Name is var name&&(name==dgm+"ptLst"||name==dgm+"cxnLst"||
                    name==dgm+"bg"&&!Elements(n).Any()||name==dgm+"whole"&&!Elements(n).Any())))
                return null;
            var pointNodes=new Dictionary<string,InventoryNode>(StringComparer.OrdinalIgnoreCase);
            foreach(var point in Elements(points))
            {
                token.ThrowIfCancellationRequested();
                if(pointNodes.Count>=65||point.Name!=dgm+"pt"||!OnlyAttributes(point,"{}modelId","{}type")||
                    Value(point,"modelId") is not {Length:>0 and <=72} id||
                    Value(point,"type") is not (null or "node" or "doc")||!pointNodes.TryAdd(id,point))
                    return null;
            }
            if(pointNodes.Values.Where(p=>Value(p,"type")=="doc").Take(2).ToArray() is not [var root]||
                Elements(root).Any())return null;
            string rootId=Value(root,"modelId")!;
            var linked=new Dictionary<string,Dictionary<int,string>>(StringComparer.OrdinalIgnoreCase);
            var targets=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var edges=ImmutableArray.CreateBuilder<DiagramEdge>();
            foreach(var connection in Elements(connections))
            {
                token.ThrowIfCancellationRequested();
                if(connection.Name!=dgm+"cxn"||!OnlyAttributes(connection,"{}modelId","{}type","{}srcId","{}destId","{}srcOrd","{}destOrd")||
                    Elements(connection).Any()||Value(connection,"type") is not (null or "parOf")||
                    Value(connection,"modelId") is not {Length:>0 and <=72} id||!ids.Add(id)||
                    Value(connection,"srcId") is not {} parent||!pointNodes.ContainsKey(parent)||
                    Value(connection,"destId") is not {} target||!pointNodes.TryGetValue(target,out var node)||node==root||!targets.Add(target)||
                    !Number(Value(connection,"srcOrd"),0,63,out int order)||Value(connection,"destOrd") is not (null or "0"))
                    return null;
                if(!linked.TryGetValue(parent,out var ordered))linked.Add(parent,ordered=[]);
                if(!ordered.TryAdd(order,target))return null;
                edges.Add(new(parent,target,connection.Source));
            }
            if(targets.Count is 0 or >64||targets.Count+1!=pointNodes.Count||
                linked.Values.Any(v=>!v.Keys.Order().SequenceEqual(Enumerable.Range(0,v.Count))))return null;
            var children=linked.ToDictionary(p=>p.Key,p=>p.Value.OrderBy(x=>x.Key).Select(x=>x.Value).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visit=new Stack<string>();
            visit.Push(rootId);
            while(visit.TryPop(out string? id))
            {
                token.ThrowIfCancellationRequested();
                if(!seen.Add(id))return null;
                if(children.TryGetValue(id,out var descendants))
                    foreach(var child in descendants)visit.Push(child);
            }
            return seen.Count==pointNodes.Count?new(rootId,pointNodes,children,edges.ToImmutable()):null;
        }

        LinearPlan? ReadLinearPlan(InventoryNode layout)
        {
            string dgm="{"+Diagram+"}";
            if(!OnlyAttributes(layout,"{}name")||Member(layout,Diagram,"alg") is not {} algorithm||
                !OnlyAttributes(algorithm,"{}type")||Member(layout,Diagram,"forEach") is not {} each||
                !Elements(layout).All(x=>x.Id==algorithm.Id||x.Id==each.Id||
                    (x.Name==dgm+"presOf"||x.Name==dgm+"shape")&&OnlyAttributes(x)&&!Elements(x).Any())||
                !OnlyAttributes(each,"{}axis","{}ptType")||Value(each,"axis")!="ch"||Value(each,"ptType")!="node"||
                Elements(each).Take(2).ToArray() is not [var node]||node.Name!=dgm+"layoutNode"||
                !OnlyAttributes(node,"{}name","{}styleLbl")||Value(node,"styleLbl") is not {Length:>0 and <=128} label||
                Member(node,Diagram,"alg") is not {} text||Value(text,"type")!="tx"||!OnlyAttributes(text,"{}type")||Elements(text).Any()||
                Member(node,Diagram,"presOf") is not {} self||!OnlyAttributes(self,"{}axis","{}ptType")||
                Value(self,"axis")!="self"||Value(self,"ptType") is not (null or "node")||Elements(self).Any()||
                Member(node,Diagram,"shape") is not {} shape||!OnlyAttributes(shape,"{}type")||
                Value(shape,"type") is not ("rect" or "roundRect")||Elements(shape).Any()||
                Member(node,Diagram,"constrLst") is not {} constraints||
                Elements(node).Count()!=4)return null;
            var parameters=Elements(algorithm).ToArray();
            string? direction=parameters.Length==0?"fromL":
                parameters is [var p]&&p.Name==dgm+"param"&&OnlyAttributes(p,"{}type","{}val")&&
                    Value(p,"type")=="linDir"&&!Elements(p).Any()?Value(p,"val"):null;
            if(direction is not ("fromL" or "fromR" or "fromT" or "fromB"))return null;
            var values=new Dictionary<string,float>(StringComparer.Ordinal);
            foreach(var constraint in Elements(constraints))
            {
                if(constraint.Name!=dgm+"constr"||!OnlyAttributes(constraint,"{}type","{}val","{}op")||
                    Value(constraint,"type") is not ("w" or "h" or "primFontSz")||
                    Value(constraint,"op") is not (null or "equ")||Elements(constraint).Any()||
                    !float.TryParse(Value(constraint,"val"),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out float measure)||
                    measure is <1 or >558||!values.TryAdd(Value(constraint,"type")!,measure))return null;
            }
            return values.Count==3&&values.TryGetValue("primFontSz",out float pointSize)&&pointSize<=96&&
                values.TryGetValue("w",out float w)&&values.TryGetValue("h",out float h)?
                new(Value(shape,"type")!,direction,w*72/25.4f,h*72/25.4f,pointSize,label,node.Source):null;
        }

        ImmutableArray<DiagramShape> ArrangeLinear(DiagramGraph graph,LinearPlan plan,float width,float height,
            SmartArtParts parts,CancellationToken cancellation)
        {
            if(graph.Children.Count!=1||!graph.Children.TryGetValue(graph.RootId,out var siblings))return [];
            int count=siblings.Length;
            bool horizontal=plan.Direction is "fromL" or "fromR";
            bool backward=plan.Direction is "fromR" or "fromB";
            // This profile has no separator geometry or size rules. Its fixed dimensions tile the declared drawing.
            if(Math.Abs(width-plan.Width*(horizontal?count:1))>.02f||Math.Abs(height-plan.Height*(horizontal?1:count))>.02f||
                !ReadDiagramTheme(out var theme,out var fonts)||
                !TryStyle(parts,plan.Label,theme,fonts,out uint fill,out uint textColor,out string? family,out var fontSource))
                return [];
            var request=new FontRequest(family!,false,fontSource,"typeface");
            var shapes=ImmutableArray.CreateBuilder<DiagramShape>(count);
            for(int i=0;i<count;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                checkDiagramBudget?.Invoke();
                var point=graph.Nodes[siblings[i]];
                int slot=backward?count-1-i:i;
                PageRect box=new(horizontal?slot*plan.Width:0,horizontal?0:slot*plan.Height,plan.Width,plan.Height);
                if(!ReadLinearLabel(point,box,plan.FontSize,request,textColor,out var textBox,out var label))return [];
                shapes.Add(new(point.Source,box,plan.Shape,16_667,0,fill,null,textBox,label){LayoutSource=plan.Source});
            }
            return shapes.MoveToImmutable();
        }

        bool TryStyle(SmartArtParts parts,string key,Dictionary<string,string> theme,Dictionary<string,string> fonts,
            out uint fill,out uint textColor,out string? family,out SourceLocation source)
        {
            fill=textColor=0;family=null;source=parts.Styles.Source;
            string tag="{"+Diagram+"}styleLbl";
            if(SemanticIndex.Unique(Elements(parts.Styles).Where(n=>n.Name==tag&&Value(n,"name")==key)) is not {} style||
                Elements(style).Take(2).ToArray() is not [var styleRefs]||styleRefs.Name!="{"+Diagram+"}style"||
                Member(styleRefs,D,"lnRef") is not {} line||Value(line,"idx")!="0"||
                Member(styleRefs,D,"fillRef") is not {} brush||Value(brush,"idx")!="1"||
                Member(styleRefs,D,"effectRef") is not {} effect||Value(effect,"idx")!="0"||
                Member(styleRefs,D,"fontRef") is not {} font||Elements(styleRefs).Count()!=4||
                !fonts.TryGetValue(Value(font,"idx")??"",out family)||
                !UsesSolidThemeFill()||
                SemanticIndex.Unique(Elements(parts.Colors).Where(n=>n.Name==tag&&Value(n,"name")==key)) is not {} colors||
                !Elements(colors).All(n=>n.Name is "{"+Diagram+"}fillClrLst" or "{"+Diagram+"}txFillClrLst")||
                !ReadColor("fillClrLst",out fill)||!ReadColor("txFillClrLst",out textColor))return false;
            source=font.Source;
            return true;

            bool UsesSolidThemeFill()
            {
                if(semantic!.RelatedPart("theme") is not {} uri||
                    SemanticIndex.Unique(nodes.Values.Where(n=>n.Source.PartUri==uri&&n.Name=="{"+D+"}theme")) is not {} root||
                    Member(root,D,"themeElements") is not {} elements||Member(elements,D,"fmtScheme") is not {} style||
                    Member(style,D,"fillStyleLst") is not {} fills||Elements(fills).FirstOrDefault() is not {} first||
                    first.Name!="{"+D+"}solidFill"||Elements(first).Take(2).ToArray() is not [var placeholder])
                    return false;
                return placeholder.Name=="{"+D+"}schemeClr"&&Value(placeholder,"val")=="phClr"&&!Elements(placeholder).Any();
            }

            bool ReadColor(string name,out uint value)
            {
                value=0;
                if(Member(colors!,Diagram,name) is not {} list||!OnlyAttributes(list,"{}meth","{}hueDir")||
                    Value(list,"meth") is not (null or "repeat")||Elements(list).Take(2).ToArray() is not [var solid]||
                    Elements(solid).Any()||!OnlyAttributes(solid,"{}val"))return false;
                string? rgb=solid.Name switch
                {
                    "{"+D+"}srgbClr"=>Value(solid,"val"),
                    "{"+D+"}schemeClr"=>theme.GetValueOrDefault(Value(solid,"val")??""),
                    _=>null
                };
                if(rgb is not {Length:6}||!uint.TryParse(rgb,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint parsed))return false;
                value=0xff000000|parsed;
                return true;
            }
        }

        bool ReadLinearLabel(InventoryNode point,PageRect box,float size,FontRequest request,uint color,
            out PageRect? textBox,out ImmutableArray<DiagramTextLine> label)
        {
            textBox=null;label=[];
            if(!Elements(point).Any())return true;
            if(Elements(point).Take(2).ToArray() is not [var text]||text.Name!="{"+Diagram+"}t"||
                Member(text,D,"bodyPr") is not {} body||!OnlyAttributes(body,"{}lIns","{}tIns","{}rIns","{}bIns","{}anchor","{}wrap")||
                Value(body,"anchor") is not ("t" or "ctr" or "b")||Value(body,"wrap")!="square"||
                Elements(body).Take(2).ToArray() is not [var fitting]||fitting.Name!="{"+D+"}noAutofit"||
                Member(text,D,"p") is not {} paragraph||Elements(text).Count()!=2||
                Member(paragraph,D,"pPr") is not {} properties||!OnlyAttributes(properties,"{}algn")||
                Value(properties,"algn") is not ("l" or "ctr" or "r")||Elements(properties).Any()||
                Member(paragraph,D,"r") is not {} run||Elements(paragraph).Count()!=2||
                Member(run,D,"t") is not {} content||Elements(run).Count()!=1||Elements(content).Any()||
                !Inset(body,"lIns",out float left)||!Inset(body,"tIns",out float top)||
                !Inset(body,"rIns",out float right)||!Inset(body,"bIns",out float bottom))return false;
            string value=DiagramText(content);
            if(value.Length is 0 or >1024||value.EnumerateRunes().Any(r=>
                TextUnicode.Instance.Script(r.Value) is not ("Latn" or "Zyyy" or "Zinh")||Rune.IsControl(r)))return false;
            var inner=new PageRect(box.Left+left,box.Top+top,box.Width-left-right,box.Height-top-bottom);
            if(inner.Empty)return false;
            var style=new TextStyle{Language="und",Size=size,Color=(color&0xffffff).ToString("X6",CultureInfo.InvariantCulture),
                Slots=new(request,request)};
            textBox=inner;
            return WrapLabel(value,style,semantic!.Source(content.Id),inner,Value(properties,"algn")!,Value(body,"anchor")!,1,0,0,out label);
        }
    }
}
