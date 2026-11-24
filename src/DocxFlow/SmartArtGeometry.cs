using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        DiagramSlot[]? FlowSlots(DiagramPlan node,DiagramPlan[] children,PageRect area,DiagramGraph graph)
        {
            var args=node.Parameters;
            if(node.Constraints is not null)return null;
            if(node.Algorithm=="hierRoot")
                return args.Count==0&&children is [var child]&&child.Algorithm=="hierChild"?[new(child,area)]:null;
            var dimensions=new (float Width,float Height)[children.Length];
            for(int i=0;i<children.Length;i++)
            {
                if(!DirectMetrics(children[i],out var metrics)||!metrics.TryGetValue("w",out float w)||
                    !metrics.TryGetValue("h",out float h))return null;
                dimensions[i]=(w,h);
            }
            if(dimensions.Distinct().Count()!=1)return null;
            var (width,height)=dimensions[0];
            if(width>area.Width+.02f||height>area.Height+.02f)return null;
            int count=children.Length;
            DiagramSlot Slot(int i,float x,float y)=>new(children[i],new(area.Left+x,area.Top+y,width,height));

            switch(node.Algorithm)
            {
                case "lin":
                {
                    string dir=args.GetValueOrDefault("linDir","fromL");
                    if(args.Keys.Any(k=>k!="linDir")||dir is not ("fromL" or "fromR" or "fromT" or "fromB"))return null;
                    bool horizontal=dir is "fromL" or "fromR",reverse=dir is "fromR" or "fromB";
                    float axis=horizontal?area.Width:area.Height,size=horizontal?width:height;
                    if(size*count>axis+.02f)return null;
                    return Enumerable.Range(0,count).Select(i=>
                    {
                        float gap=count==1?(axis-size)/2:(axis-size)*(reverse?count-1-i:i)/(count-1);
                        return horizontal?Slot(i,gap,(area.Height-height)/2):Slot(i,(area.Width-width)/2,gap);
                    }).ToArray();
                }
                case "snake":
                {
                    string flow=args.GetValueOrDefault("flowDir","row"),grow=args.GetValueOrDefault("grDir","tL"),
                        continuation=args.GetValueOrDefault("contDir","sameDir");
                    if(args.Keys.Any(k=>k is not ("flowDir" or "grDir" or "contDir" or "off" or "bkpt" or "bkPtFixedVal"))||
                        flow is not ("row" or "col")||grow is not ("tL" or "tR" or "bL" or "bR")||
                        continuation is not ("revDir" or "sameDir")||args.GetValueOrDefault("off","off")!="off")return null;
                    bool row=flow=="row";
                    string breakpoint=args.GetValueOrDefault("bkpt","endCnv");
                    int capacity=(int)MathF.Floor(((row?area.Width:area.Height)+.02f)/(row?width:height));
                    if(breakpoint=="fixed")
                    {
                        if(!Number(args.GetValueOrDefault("bkPtFixedVal","2"),1,Math.Max(1,capacity),out capacity))return null;
                    }
                    else if(breakpoint!="endCnv"||args.ContainsKey("bkPtFixedVal"))return null;
                    if(capacity<=0)return null;
                    capacity=Math.Min(capacity,count);
                    int rows=(count+capacity-1)/capacity;
                    if(rows*(row?height:width)>(row?area.Height:area.Width)+.02f)return null;
                    return Enumerable.Range(0,count).Select(i=>
                    {
                        int major=i%capacity,minor=i/capacity;
                        if(continuation=="revDir"&&minor%2==1)major=capacity-1-major;
                        int x=row?major:minor,y=row?minor:major,nx=row?capacity:rows,ny=row?rows:capacity;
                        if(grow.EndsWith('R'))x=nx-1-x;
                        if(grow.StartsWith('b'))y=ny-1-y;
                        return Slot(i,Along(area.Width,width,nx,x),Along(area.Height,height,ny,y));
                    }).ToArray();
                }
                case "cycle":
                {
                    if(count<2||args.Keys.Any(k=>k is not ("stAng" or "spanAng" or "rotPath" or "ctrShpMap"))||
                        args.GetValueOrDefault("rotPath","none")!="none"||args.GetValueOrDefault("ctrShpMap","none")!="none"||
                        !DiagramNumber(args.GetValueOrDefault("stAng","0"),-360,360,out float start)||
                        !DiagramNumber(args.GetValueOrDefault("spanAng","360"),-360,360,out float span)||span==0)return null;
                    var slots=Enumerable.Range(0,count).Select(i=>
                    {
                        float angle=(start+span*i/(Math.Abs(span)==360?count:count-1))*MathF.PI/180;
                        return Slot(i,(area.Width-width)*(1+MathF.Cos(angle))/2,(area.Height-height)*(1+MathF.Sin(angle))/2);
                    }).ToArray();
                    return slots.Where((a,i)=>slots.Take(i).Any(b=>!a.Box.Intersection(b.Box).Empty)).Any()?null:slots;
                }
                case "hierChild":
                {
                    if(args.Keys.Any(k=>k is not ("hierAlign" or "chDir"))||args.GetValueOrDefault("hierAlign","tCtrCh")!="tCtrCh"||
                        args.GetValueOrDefault("chDir","horz")!="horz"||
                        children.Select(n=>n.Point).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=count)return null;
                    var byPoint=children.Select((n,i)=>(n.Point,i)).ToDictionary(p=>p.Point,p=>p.i,StringComparer.OrdinalIgnoreCase);
                    var positions=new (float Column,int Depth)[count];
                    var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int columns=0,maxDepth=0;
                    var top=graph.Children.GetValueOrDefault(node.Point,[]);
                    if(top is not [var tree]||Visit(tree,0) is null||visited.Count!=count||
                        columns*width>area.Width+.02f||(maxDepth+1)*height>area.Height+.02f)return null;
                    return positions.Select((p,i)=>Slot(i,Along(area.Width,width,columns,p.Column),
                        Along(area.Height,height,maxDepth+1,p.Depth))).ToArray();

                    float? Visit(string id,int depth)
                    {
                        if(!byPoint.TryGetValue(id,out int i)||!visited.Add(id))return null;
                        var childrenIds=graph.Children.GetValueOrDefault(id,[]);
                        float col;
                        if(childrenIds.IsEmpty)col=columns++;
                        else
                        {
                            var descendants=childrenIds.Select(child=>Visit(child,depth+1)).ToArray();
                            if(descendants.Any(c=>c is null))return null;
                            col=(descendants[0]!.Value+descendants[^1]!.Value)/2;
                        }
                        maxDepth=Math.Max(maxDepth,depth);
                        positions[i]=(col,depth);
                        return col;
                    }
                }
                case "pyramid":
                {
                    if(args.Keys.Any(k=>k!="pyraLvlNode")||!args.TryGetValue("pyraLvlNode",out var level)||
                        children.Any(child=>child.Name!=level||child.Geometry!="trapezoid"||child.Rotation!=0)||
                        Math.Abs(area.Height-height*count)>.02f)return null;
                    float cx=area.Left+area.Width/2;
                    return children.Select((child,i)=>
                    {
                        float a=width*i/count/2,b=width*(i+1)/count/2;
                        float top=area.Top+height*i,bottom=top+height;
                        return new DiagramSlot(child,new(cx-b,top,2*b,height),
                            [new(cx-a,top),new(cx+a,top),new(cx+b,bottom),new(cx-b,bottom)]);
                    }).ToArray();
                }
                default:return null;
            }

            static float Along(float total,float size,int count,float index)=>count==1?(total-size)/2:
                (total-size)*index/(count-1);
        }

        bool Connect(DiagramPlan parent,DiagramPlan plan,DiagramSlot[] slots,DiagramGraph graph,SmartArtParts parts,
            Dictionary<string,string> theme,ImmutableArray<DiagramConnector>.Builder output)
        {
            var args=plan.Parameters;
            string route=args.GetValueOrDefault("connRout","stra"),begin=args.GetValueOrDefault("begSty","noArr"),
                end=args.GetValueOrDefault("endSty","arr");
            string? beginSite=args.GetValueOrDefault("begPts"),endSite=args.GetValueOrDefault("endPts");
            if(args.Keys.Any(k=>k is not ("connRout" or "begSty" or "endSty" or "begPts" or "endPts" or "srcNode" or "dstNode" or "dim"))||
                args.GetValueOrDefault("dim","1D")!="1D"||route is not ("stra" or "bend" or "curve")||
                begin is not ("arr" or "noArr")||end is not ("arr" or "noArr")||
                !KnownSite(beginSite)||!KnownSite(endSite)||plan.Style is null||!ConnectorStyle(parts,plan.Style,theme,out var stroke))return false;
            var pairs=new List<(DiagramSlot From,DiagramSlot To)>();
            if(args.TryGetValue("srcNode",out var from)&&args.TryGetValue("dstNode",out var to))
            {
                var sources=slots.Where(s=>s.Plan.Name==from).Take(2).ToArray();
                var destinations=slots.Where(s=>s.Plan.Name==to).Take(2).ToArray();
                if(sources is not [var first]||destinations is not [var last]||first==last)return false;
                pairs.Add((first,last));
            }
            else if(args.ContainsKey("srcNode")||args.ContainsKey("dstNode"))return false;
            else if(parent.Algorithm=="hierChild")
            {
                var byPoint=slots.ToDictionary(s=>s.Plan.Point,StringComparer.OrdinalIgnoreCase);
                foreach(var edge in graph.Edges)
                    if(byPoint.TryGetValue(edge.From,out var first)&&byPoint.TryGetValue(edge.To,out var last))pairs.Add((first,last));
            }
            else if(parent.Algorithm is "lin" or "snake" or "cycle")
            {
                for(int i=1;i<slots.Length;i++)pairs.Add((slots[i-1],slots[i]));
                if(parent.Algorithm=="cycle"&&slots.Length>2&&Math.Abs(float.Parse(parent.Parameters.GetValueOrDefault("spanAng","360"),
                    CultureInfo.InvariantCulture))==360)pairs.Add((slots[^1],slots[0]));
            }
            else return false;
            foreach(var (first,last) in pairs)
            {
                token.ThrowIfCancellationRequested();
                if(output.Count>=256||first.Plan.Rotation!=0||last.Plan.Rotation!=0||
                    first.Plan.Geometry is not ("rect" or "roundRect" or "ellipse")||
                    last.Plan.Geometry is not ("rect" or "roundRect" or "ellipse"))return false;
                var (a,ad)=Port(first.Box,last.Box,beginSite);
                var (b,bd)=Port(last.Box,first.Box,endSite);
                if(Vector2.DistanceSquared(a,b)<.01f)return false;
                Vector2[] points=[a,b];
                if(route=="bend")
                {
                    Vector2 between=(a+b)/2;
                    points=ad.X!=0&&bd.X!=0?[a,new(between.X,a.Y),new(between.X,b.Y),b]:
                        ad.Y!=0&&bd.Y!=0?[a,new(a.X,between.Y),new(b.X,between.Y),b]:
                        [a,ad.X!=0?new(b.X,a.Y):new(a.X,b.Y),b];
                }
                if(route=="curve")
                {
                    float handle=Vector2.Distance(a,b)/2;
                    points=[a,a+ad*handle,b+bd*handle,b];
                }
                output.Add(new(plan.Source.Source,first.Plan.Point,last.Plan.Point,route,[..points],stroke,begin=="arr",end=="arr"));
            }
            return true;

            static bool KnownSite(string? site)=>site is null or "midL" or "midR" or "midT" or "midB";

            static (Vector2 Point,Vector2 Direction) Port(PageRect shape,PageRect other,string? site)
            {
                var center=new Vector2(shape.Left+shape.Width/2,shape.Top+shape.Height/2);
                Vector2 delta=new(other.Left+other.Width/2-center.X,other.Top+other.Height/2-center.Y);
                var direction=site switch
                {
                    "midL"=>-Vector2.UnitX,"midR"=>Vector2.UnitX,"midT"=>-Vector2.UnitY,"midB"=>Vector2.UnitY,
                    _=>MathF.Abs(delta.X)*shape.Height>=MathF.Abs(delta.Y)*shape.Width?
                        (delta.X<0?-Vector2.UnitX:Vector2.UnitX):(delta.Y<0?-Vector2.UnitY:Vector2.UnitY)
                };
                return (center+direction*new Vector2(shape.Width/2,shape.Height/2),direction);
            }
        }

        bool ConnectorStyle(SmartArtParts parts,string label,Dictionary<string,string> theme,out FrameStroke stroke)
        {
            stroke=default;
            var style=SemanticIndex.Unique(Elements(parts.Styles).Where(n=>n.Name==Dgm+"styleLbl"&&Value(n,"name")==label));
            var colors=SemanticIndex.Unique(Elements(parts.Colors).Where(n=>n.Name==Dgm+"styleLbl"&&Value(n,"name")==label));
            if(style is null||!Single(style,Diagram,"style",out var format)||Value(Member(format,D,"effectRef")??format,"idx")!="0"||
                Value(Member(format,D,"fillRef")??format,"idx")!="0"||!StrokeWidth(format,format,out float width)||
                colors is null||!Single(colors,Diagram,"linClrLst",out var list)||
                Value(list,"meth") is not (null or "repeat")||Elements(list).ToArray() is not [var ink]||Elements(ink).Any())return false;
            string? rgb=ink.Name switch
            {
                "{"+D+"}srgbClr"=>Value(ink,"val"),"{"+D+"}schemeClr"=>theme.GetValueOrDefault(Value(ink,"val")??""),_=>null
            };
            if(rgb is not {Length:6}||!uint.TryParse(rgb,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint color))return false;
            stroke=new(color,width);
            return true;
        }
    }
}
