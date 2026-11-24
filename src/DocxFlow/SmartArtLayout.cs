using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal sealed record DiagramEdge(string From,string To,SourceLocation Source);
internal sealed record DiagramConnector(SourceLocation Source,string From,string To,string Route,
    ImmutableArray<Vector2> Points,FrameStroke Stroke,bool ArrowAtStart,bool ArrowAtEnd);
internal sealed record DiagramGroup(SourceLocation Source,string Algorithm,PageRect Region,
    ImmutableArray<DiagramGroup> Children);
internal sealed record DiagramModel(SourceLocation Data,SourceLocation Layout,SourceLocation Styles,SourceLocation Colors,
    ImmutableDictionary<string,SourceLocation> Points,ImmutableArray<DiagramEdge> Edges);

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        const string Dgm="{"+Diagram+"}";
        sealed record DiagramPlan(InventoryNode Source,string Name,string Point,string Algorithm,
            Dictionary<string,string> Parameters,ImmutableArray<DiagramPlan> Children,InventoryNode? Constraints,InventoryNode? Rules,
            string? Geometry,string? Style,bool HasText,float Rotation);
        sealed record DiagramSlot(DiagramPlan Plan,PageRect Box,ImmutableArray<Vector2> Outline=default);

        DiagramDrawing? ArrangeDiagram(InventoryNode layout,DiagramGraph graph,SmartArtParts parts,float width,float height)
        {
            if(!ReadDiagramTheme(out var theme,out var fonts))return null;
            var shapes=ImmutableArray.CreateBuilder<DiagramShape>();
            var connectors=ImmutableArray.CreateBuilder<DiagramConnector>();
            var mapped=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int expanded=0,instructionsRead=0;
            if(!ReadPlan(layout,graph.RootId,1,1,0,out var plan)||
                !Place(plan,new(0,0,width,height),default,out var root)||
                !mapped.SetEquals(graph.Nodes.Keys.Where(id=>id!=graph.RootId)))return null;
            return new(shapes.ToImmutable())
            {
                LayoutProfile=plan.Algorithm,Connectors=connectors.ToImmutable(),Groups=[root],Model=DiagramOrigins(graph,parts)
            };

            bool ReadPlan(InventoryNode node,string point,int position,int count,int depth,out DiagramPlan plan)
            {
                token.ThrowIfCancellationRequested();
                checkDiagramBudget?.Invoke();
                plan=null!;
                if(depth>12||++expanded>512||!OnlyAttributes(node,"{}name","{}styleLbl")||
                    Member(node,Diagram,"alg") is not {} algorithm||!OnlyAttributes(algorithm,"{}type")||
                    Value(algorithm,"type") is not ("lin" or "snake" or "cycle" or "hierRoot" or "hierChild" or "composite" or
                        "pyramid" or "sp" or "tx" or "conn")||
                    !ReadParameters(algorithm,out var parameters))return false;
                string type=Value(algorithm,"type")!;
                var childPlans=ImmutableArray.CreateBuilder<DiagramPlan>();
                if(!Expand(Elements(node),point,position,count,depth,childPlans,true))return false;
                var shape=Member(node,Diagram,"shape");
                var presentation=Member(node,Diagram,"presOf");
                var constraints=Member(node,Diagram,"constrLst");
                var rules=Member(node,Diagram,"ruleLst");
                if(new[]{"shape","presOf","constrLst","ruleLst"}.Any(n=>Elements(node).Count(c=>c.Name==Dgm+n)>1)||
                    presentation is not null&&(!OnlyAttributes(presentation,"{}axis","{}ptType")||Elements(presentation).Any()||
                        Value(presentation,"axis") is not (null or "self")||Value(presentation,"ptType") is not (null or "node"))||
                    shape is not null&&(!OnlyAttributes(shape,"{}type","{}rot","{}hideGeom")||Elements(shape).Any()||
                        Value(shape,"hideGeom") is not (null or "false" or "0" or "true" or "1")))return false;
                float rotation=0;
                if(shape is not null&&Value(shape,"rot") is {} r&&!DiagramNumber(r,-360,360,out rotation))return false;
                string? geometry=shape is null||Value(shape,"hideGeom") is ("true" or "1")?null:Value(shape,"type");
                bool self=presentation is not null&&Value(presentation,"axis")=="self";
                if(type=="conn"&&(constraints is not null||rules is not null||childPlans.Count>0||self||rotation!=0||
                    geometry is not (null or "line")))return false;
                plan=new(node,Value(node,"name")??"",point,type,parameters,childPlans.ToImmutable(),constraints,rules,
                    geometry,Value(node,"styleLbl"),self,rotation);
                return true;
            }

            bool Expand(IEnumerable<InventoryNode> instructions,string point,int pos,int count,int depth,
                ImmutableArray<DiagramPlan>.Builder output,bool directives)
            {
                if(depth>12)return false;
                foreach(var instruction in instructions)
                {
                    token.ThrowIfCancellationRequested();
                    checkDiagramBudget?.Invoke();
                    if(++instructionsRead>4096)return false;
                    if(instruction.Name==Dgm+"layoutNode")
                    {
                        if(!ReadPlan(instruction,point,pos,count,depth+1,out var plan))return false;
                        output.Add(plan);
                    }
                    else if(instruction.Name==Dgm+"forEach")
                    {
                        if(!OnlyAttributes(instruction,"{}name","{}axis","{}ptType","{}st","{}step","{}cnt")||
                            Value(instruction,"ptType") is not (null or "node")||
                            Select(point,Value(instruction,"axis")) is not {} axis||
                            !Number(Value(instruction,"st")??"1",1,64,out int start)||
                            !Number(Value(instruction,"step")??"1",1,64,out int step)||
                            !Number(Value(instruction,"cnt")??"64",0,64,out int limit))return false;
                        if(limit==0)limit=64;
                        for(int i=start-1,taken=0;i<axis.Length&&taken<limit;i+=step,taken++)
                            if(!Expand(Elements(instruction),axis[i],i+1,axis.Length,depth+1,output,false))return false;
                    }
                    else if(instruction.Name==Dgm+"choose")
                    {
                        if(!OnlyAttributes(instruction,"{}name"))return false;
                        InventoryNode? selected=null;
                        bool sawElse=false,sawIf=false;
                        foreach(var branch in Elements(instruction))
                        {
                            if(sawElse)return false;
                            if(branch.Name==Dgm+"else"&&sawIf&&OnlyAttributes(branch,"{}name"))
                            {selected??=branch;sawElse=true;continue;}
                            if(branch.Name!=Dgm+"if"||!Condition(branch,point,pos,count,out bool matches))return false;
                            sawIf=true;
                            if(matches)selected??=branch;
                        }
                        if(!sawIf||selected is not null&&!Expand(Elements(selected),point,pos,count,depth+1,output,false))return false;
                    }
                    else if(!directives||instruction.Name is not (Dgm+"alg" or Dgm+"shape" or Dgm+"presOf" or Dgm+"constrLst" or Dgm+"ruleLst"))
                        return false;
                }
                return true;
            }

            string[]? Select(string point,string? axis)
            {
                if(axis=="self")return [point];
                if(axis=="ch")return graph.Children.GetValueOrDefault(point,[]).ToArray();
                if(axis!="des")return null;
                var descendants=new List<string>();
                Visit(point);
                return descendants.ToArray();

                void Visit(string parent)
                {
                    foreach(string child in graph.Children.GetValueOrDefault(parent,[]))
                    {descendants.Add(child);Visit(child);}
                }
            }

            bool Condition(InventoryNode node,string point,int pos,int count,out bool matches)
            {
                matches=false;
                if(!OnlyAttributes(node,"{}name","{}func","{}op","{}val","{}axis","{}ptType")||
                    Value(node,"ptType") is not (null or "node")||!Number(Value(node,"val"),0,64,out int target))return false;
                int? value=Value(node,"func") switch
                {
                    "cnt" when Select(point,Value(node,"axis")??"ch") is {} axis=>axis.Length,
                    "pos" when Value(node,"axis") is null=>pos,
                    "revPos" when Value(node,"axis") is null=>count-pos+1,
                    _=>null
                };
                if(value is null)return false;
                string? op=Value(node,"op");
                matches=op switch{"equ"=>value==target,"neq"=>value!=target,"lt"=>value<target,"lte"=>value<=target,
                    "gt"=>value>target,"gte"=>value>=target,_=>false};
                return op is "equ" or "neq" or "lt" or "lte" or "gt" or "gte";
            }

            bool Place(DiagramPlan node,PageRect region,ImmutableArray<Vector2> outline,out DiagramGroup group)
            {
                token.ThrowIfCancellationRequested();
                group=null!;
                if(!Fits(region,new(0,0,width,height)))return false;
                if(node.Algorithm is "tx" or "sp")
                {
                    if(node.Children.Length!=0||node.Parameters.Count!=0||node.Style is null||
                        node.Geometry is not ("rect" or "roundRect" or "ellipse" or "trapezoid" or null)||
                        node.Geometry=="trapezoid"&&outline.IsDefaultOrEmpty||
                        !TryStyle(parts,node.Style,theme,fonts,out uint fill,out uint color,out string? family,out var fontSource)||
                        !DirectMetrics(node,out var metrics)||outline.IsDefaultOrEmpty&&
                        (metrics.TryGetValue("w",out float w)&&!Near(w,region.Width,.02f)||
                         metrics.TryGetValue("h",out float h)&&!Near(h,region.Height,.02f)))return false;
                    if(node.Rotation!=0)
                    {
                        float angle=MathF.Abs(node.Rotation)*MathF.PI/180;
                        float rw=MathF.Abs(MathF.Cos(angle))*region.Width+MathF.Abs(MathF.Sin(angle))*region.Height;
                        float rh=MathF.Abs(MathF.Sin(angle))*region.Width+MathF.Abs(MathF.Cos(angle))*region.Height;
                        if(!Fits(new(region.Left+(region.Width-rw)/2,region.Top+(region.Height-rh)/2,rw,rh),new(0,0,width,height)))return false;
                    }
                    if(node.Algorithm=="sp"&&node.Geometry is null)return false;
                    PageRect? textBox=null;
                    ImmutableArray<DiagramTextLine> label=[];
                    var source=graph.Nodes[node.Point];
                    if(node.HasText&&node.Algorithm=="tx")
                    {
                        if(!metrics.TryGetValue("primFontSz",out float size)||size is <1 or >96)return false;
                        if(!FontRule(node,size,out float minimum))return false;
                        var labelRegion=region;
                        if(!outline.IsDefaultOrEmpty)
                            labelRegion=new(region.Left+region.Width*.2f,region.Top+region.Height*.45f,region.Width*.6f,region.Height*.55f);
                        else if(node.Geometry=="ellipse")
                            labelRegion=new(region.Left+region.Width*.15f,region.Top+region.Height*.15f,region.Width*.7f,region.Height*.7f);
                        var request=new FontRequest(family!,false,fontSource,"typeface");
                        while(!ReadLinearLabel(source,labelRegion,size,request,color,out textBox,out label))
                        {
                            if(size<=minimum)return false;
                            size=Math.Max(minimum,size-.5f);
                        }
                        mapped.Add(node.Point);
                    }
                    else if(node.Rules is not null)return false;
                    if(node.Geometry is not null||!label.IsEmpty)
                        shapes.Add(new(source.Source,region,node.Geometry??"rect",16_667,0,node.Geometry is null?null:fill,null,textBox,label)
                            {LayoutSource=node.Source.Source,Rotation=node.Rotation,Outline=outline.IsDefault?[]:outline});
                    group=new(node.Source.Source,node.Algorithm,region,[]);
                    return true;
                }

                if(node.Geometry is not null||node.HasText||node.Rotation!=0||node.Rules is not null||node.Style is not null)return false;
                var children=node.Children.Where(n=>n.Algorithm!="conn").ToArray();
                if(children.Length==0)return false;
                DiagramSlot[]? slots=node.Algorithm=="composite"?CompositeSlots(node,children,region):
                    FlowSlots(node,children,region,graph);
                if(slots is null||slots.Length==0)return false;
                var groups=ImmutableArray.CreateBuilder<DiagramGroup>();
                foreach(var slot in slots)
                {
                    if(!Fits(slot.Box,region)||!Place(slot.Plan,slot.Box,slot.Outline,out var placed))return false;
                    groups.Add(placed);
                }
                foreach(var connector in node.Children.Where(p=>p.Algorithm=="conn"))
                    if(!Connect(node,connector,slots,graph,parts,theme,connectors))return false;
                group=new(node.Source.Source,node.Algorithm,region,groups.ToImmutable());
                return true;
            }
        }

        static DiagramModel DiagramOrigins(DiagramGraph graph,SmartArtParts parts)=>new(parts.Data.Source,parts.Layout.Source,
            parts.Styles.Source,parts.Colors.Source,graph.Nodes.ToImmutableDictionary(p=>p.Key,p=>p.Value.Source,
                StringComparer.OrdinalIgnoreCase),graph.Edges);

        bool ReadParameters(InventoryNode node,out Dictionary<string,string> result)
        {
            result=new(StringComparer.Ordinal);
            foreach(var parameter in Elements(node))
                if(parameter.Name!=Dgm+"param"||!OnlyAttributes(parameter,"{}type","{}val")||Elements(parameter).Any()||
                    Value(parameter,"type") is not {Length:>0 and <=32} key||Value(parameter,"val") is not {Length:>0 and <=128} value||
                    result.Count>=16||!result.TryAdd(key,value))return false;
            return true;
        }

        bool DirectMetrics(DiagramPlan plan,out Dictionary<string,float> result)
        {
            result=new(StringComparer.Ordinal);
            if(plan.Constraints is not {} list)return true;
            if(!OnlyAttributes(list))return false;
            foreach(var constraint in Elements(list))
                if(constraint.Name!=Dgm+"constr"||!OnlyAttributes(constraint,"{}type","{}op","{}val")||Elements(constraint).Any()||
                    Value(constraint,"type") is not ("w" or "h" or "primFontSz")||
                    Value(constraint,"op") is not (null or "equ")||!DiagramNumber(Value(constraint,"val"),.01f,558,out float val)||
                    !result.TryAdd(Value(constraint,"type")!,Value(constraint,"type")=="primFontSz"?val:val*72/25.4f))return false;
            return true;
        }

        bool FontRule(DiagramPlan node,float size,out float minimum)
        {
            minimum=size;
            if(node.Rules is not {} list)return true;
            return OnlyAttributes(list)&&Elements(list).Take(2).ToArray() is [var rule]&&rule.Name==Dgm+"rule"&&
                OnlyAttributes(rule,"{}type","{}val","{}forName","{}fact","{}max")&&Elements(rule).Count()==0&&
                Value(rule,"type")=="primFontSz"&&Value(rule,"forName") is (null or "")&&
                Value(rule,"fact") is (null or "NaN")&&Value(rule,"max") is (null or "NaN")&&
                DiagramNumber(Value(rule,"val"),1,size,out minimum);
        }

        DiagramSlot[]? CompositeSlots(DiagramPlan node,DiagramPlan[] children,PageRect region)
        {
            if(node.Parameters.Count!=0||node.Constraints is not {} constraints||!OnlyAttributes(constraints)||
                children.Any(c=>c.Name.Length==0)||children.Select(c=>c.Name).Distinct(StringComparer.Ordinal).Count()!=children.Length)
                return null;
            var values=children.ToDictionary(c=>c.Name,_=>new Dictionary<string,float>(StringComparer.Ordinal),StringComparer.Ordinal);
            var completed=new Dictionary<string,PageRect>(StringComparer.Ordinal);
            var local=new PageRect(0,0,region.Width,region.Height);
            foreach(var rule in Elements(constraints))
            {
                if(rule.Name!=Dgm+"constr"||!OnlyAttributes(rule,"{}type","{}for","{}forName","{}refFor","{}refForName",
                        "{}refType","{}op","{}val","{}fact")||Elements(rule).Any()||
                    Value(rule,"type") is not ("l" or "r" or "ctrX" or "t" or "b" or "ctrY" or "w" or "h")||
                    Value(rule,"for")!="ch"||Value(rule,"op") is not (null or "equ")||
                    Value(rule,"forName") is not {} name||!values.TryGetValue(name,out var box))return null;
                float value;
                if(Value(rule,"refType") is {} reference)
                {
                    PageRect referenceBox=local;
                    if(Value(rule,"refFor")=="ch")
                    {
                        if(Value(rule,"refForName") is not {} other||!values.TryGetValue(other,out var previous)||
                            !completed.TryGetValue(other,out referenceBox)&&!Complete(previous,out referenceBox))return null;
                    }
                    else if(Value(rule,"refFor") is not (null or "self")||Value(rule,"refForName") is not (null or ""))return null;
                    if(!DiagramNumber(Value(rule,"fact")??"1",-10,10,out float factor)||Value(rule,"val") is not null||
                        Property(referenceBox,reference) is not {} component)return null;
                    value=factor*component;
                }
                else
                {
                    if(Value(rule,"refFor") is not null||Value(rule,"refForName") is not null||Value(rule,"fact") is not null||
                        !DiagramNumber(Value(rule,"val"),-558,558,out float literal))return null;
                    value=literal*72/25.4f;
                }
                if(!box.TryAdd(Value(rule,"type")!,value))return null;
            }
            foreach(var (name,box) in values)
            {
                if(!Complete(box,out var rect))return null;
                completed.Add(name,rect);
            }
            return children.Select(c=>new DiagramSlot(c,completed[c.Name] with
                {Left=region.Left+completed[c.Name].Left,Top=region.Top+completed[c.Name].Top})).ToArray();

            static float? Property(PageRect rect,string field)=>field switch
            {
                "l"=>rect.Left,"t"=>rect.Top,"w"=>rect.Width,"h"=>rect.Height,"r"=>rect.Left+rect.Width,
                "b"=>rect.Top+rect.Height,"ctrX"=>rect.Left+rect.Width/2,"ctrY"=>rect.Top+rect.Height/2,_=>null
            };

            static bool Complete(Dictionary<string,float> values,out PageRect box)
            {
                box=default;
                if(!Axis("l","r","ctrX","w",out float left,out float w)||!Axis("t","b","ctrY","h",out float top,out float h))return false;
                box=new(left,top,w,h);
                return true;

                bool Axis(string start,string end,string middle,string size,out float origin,out float length)
                {
                    origin=length=0;
                    float? a=values.TryGetValue(start,out float l)?l:null,b=values.TryGetValue(end,out float r)?r:null;
                    float? c=values.TryGetValue(middle,out float m)?m:null,s=values.TryGetValue(size,out float w)?w:null;
                    if(s is null)s=b-a??(c-a)*2??(b-c)*2;
                    if(a is null)a=b-s??c-s/2;
                    if(a is null||s is not >0||b is not null&&!Near(a.Value+s.Value,b.Value)||
                        c is not null&&!Near(a.Value+s.Value/2,c.Value))return false;
                    origin=a.Value;length=s.Value;
                    return true;
                }
            }
        }

        static bool DiagramNumber(string? raw,float low,float high,out float value)=>float.TryParse(raw,
            NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)&&value>=low&&value<=high;
        static bool Fits(PageRect a,PageRect b)=>!a.Empty&&a.Left>=b.Left-.02f&&a.Top>=b.Top-.02f&&
            a.Left+a.Width<=b.Left+b.Width+.02f&&a.Top+a.Height<=b.Top+b.Height+.02f;
    }
}
