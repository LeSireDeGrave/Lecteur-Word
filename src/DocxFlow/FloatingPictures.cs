using System.Collections.Immutable;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal sealed record FloatingPicture(string Id,InlineImage Image,string HorizontalReference,long XOffset,
    bool YFromMargin,long YOffset,bool BehindText,uint Order)
{
    internal FlowExclusion? Exclusion {get;init;}
    internal SideExclusion? SideExclusion {get;init;}
}
internal sealed record PlacedFloatingPicture(FloatingPicture Picture,string ParagraphId,float X,float Y)
{
    internal PageRegion? ExcludedRegion {get;init;}
}

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        const string WP14="http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing";

        FloatingPicture? ReadFloatingPicture(InventoryNode drawing,InventoryNode anchor)
        {
            int before=issues.Count;
            Attributes(anchor,"{}distT","{}distB","{}distL","{}distR","{}simplePos","{}relativeHeight",
                "{}behindDoc","{}locked","{}layoutInCell","{}allowOverlap","{}hidden",
                "{"+WP14+"}anchorId","{"+WP14+"}editId");
            var parts=Elements(anchor).ToArray();
            if(parts.GroupBy(n=>n.Name).Any(g=>g.Count()!=1)||
                !Bool(anchor,"simplePos",false,false)||!Flag(anchor,"allowOverlap",out bool overlap)||
                !Bool(anchor,"hidden",false,false)||!ValidFlag(anchor,"locked")||!ValidFlag(anchor,"layoutInCell")||
                !Flag(anchor,"behindDoc",out bool behind)||
                !uint.TryParse(Value(anchor,"relativeHeight"),NumberStyles.None,CultureInfo.InvariantCulture,out uint order)||
                new[]{"distT","distB","distL","distR"}.Any(k=>Value(anchor,k) is {} value&&
                    !uint.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out _)))
                return Refuse();
            var handled=new HashSet<string>(StringComparer.Ordinal){anchor.Id};
            var coordinates=Part(WP,"simplePos");
            var wrap=Part(WP,"wrapNone")??Part(WP,"wrapTopAndBottom")??Part(WP,"wrapSquare")??Part(WP,"wrapTight")??Part(WP,"wrapThrough");
            bool topBottom=wrap?.Name=="{"+WP+"}wrapTopAndBottom";
            bool square=wrap?.Name=="{"+WP+"}wrapSquare";
            bool through=wrap?.Name=="{"+WP+"}wrapThrough";
            bool polygon=through||wrap?.Name=="{"+WP+"}wrapTight";
            bool side=square||polygon;
            if(coordinates is null||wrap is null||!Leaf(coordinates)||!polygon&&!Leaf(wrap)||!side&&wrap.Attributes.Count!=0||
                !overlap&&!side||
                coordinates.Attributes.Count!=2||!Offset(Value(coordinates,"x"),out _)||!Offset(Value(coordinates,"y"),out _)||
                !Position("positionH","column",out string hReference,out long x)||
                !Position("positionV",topBottom||side?"paragraph":null,out string vReference,out long y))
                return Refuse();
            if(topBottom&&(vReference!="paragraph"||y!=0||hReference!="column"||behind||
                !Distance("distT",out long _)||!Distance("distB",out long _)))
                return Refuse();
            long left=0,right=0,bottom=0;
            string? text=Value(wrap,"wrapText");
            ImmutableArray<WrapPoint> outline=[];
            if(side)
            {
                if(square)Attributes(wrap,"{}wrapText","{}distL","{}distR","{}distT","{}distB");
                else Attributes(wrap,"{}wrapText","{}distL","{}distR");
                if(hReference!="column"||vReference!="paragraph"||y<0||!square&&y!=0||behind||
                    text is not ("left" or "right" or "largest")&&!(square&&text=="bothSides")||
                    !Distance("distL",out left,wrap)||!Distance("distR",out right,wrap)||
                    !Distance("distB",out bottom,wrap)||!Distance("distT",out long top,wrap)||top!=0)
                    return Refuse();
                if(polygon)
                {
                    outline=ReadPolygon();
                    if(outline.IsEmpty)return Refuse();
                }
            }
            handled.Add(coordinates.Id);handled.Add(wrap.Id);
            var width=Part(WP14,"sizeRelH");
            var height=Part(WP14,"sizeRelV");
            if(width is not null||height is not null)
            {
                if(!InactiveSize(width,"pctWidth")||!InactiveSize(height,"pctHeight")||
                    !Descendants(anchor).Any(n=>n.Name=="{"+D+"}xfrm"&&Elements(n).Any(e=>e.Name=="{"+D+"}ext")))
                    return Refuse();
                Issue("LAYOUT_FLOATING_ZERO_RELATIVE_SIZE",anchor,
                    "Pourcentages nuls appariés. Taille absolue concordante du dessin et de sa transformation utilisée.",false);
            }
            if(issues.Skip(before).Any(i=>i.BlocksRendering))return null;
            var image=ReadInlineImage(drawing,handled);
            if(image is null)return null;
            var picture=new FloatingPicture(drawing.Id,image,hReference,x,vReference=="margin",y,behind,order);
            if(side)
            {
                if(image.Effects.Top!=0)return Refuse();
                long collisionLeft=x-PageComposer.Emu(image.Effects.Left);
                long collisionRight=x+PageComposer.Emu(image.Width+image.Effects.Right);
                long collisionHeight=PageComposer.Emu(image.Height+image.Effects.Bottom);
                if(polygon)
                {
                    if(image.FlipX||image.FlipY||image.Effects!=default)return Refuse();
                    long w=PageComposer.Emu(image.Width),h=PageComposer.Emu(image.Height);
                    outline=outline.Select(p=>new WrapPoint(x+p.X*w/21600,p.Y*h/21600)).ToImmutableArray();
                    return picture with{SideExclusion=new(outline.Min(p=>p.X)-left,outline.Max(p=>p.X)+right,h+bottom,text!,overlap)
                    {ThroughPolygon=through?outline:[],DistanceLeft=left,DistanceRight=right,DistanceBottom=bottom,
                        CollisionLeft=collisionLeft,CollisionRight=collisionRight,CollisionHeight=collisionHeight}};
                }
                return picture with{SideExclusion=new(collisionLeft-left,collisionRight+right,y+collisionHeight+bottom,text!,overlap)
                {Top=y,CollisionLeft=collisionLeft,CollisionRight=collisionRight,CollisionHeight=y+collisionHeight}};
            }
            if(!topBottom)return picture;
            Distance("distT",out long above);Distance("distB",out long below);
            return picture with{Exclusion=new(drawing.Id,above+PageComposer.Emu(image.Effects.Top),
                PageComposer.Emu(image.Height),below+PageComposer.Emu(image.Effects.Bottom))};

            bool Distance(string key,out long distance,InventoryNode? local=null)=>
                long.TryParse((local is null?null:Value(local,key))??Value(anchor,key)??"0",
                    NumberStyles.None,CultureInfo.InvariantCulture,out distance)&&distance<=1_828_800;

            ImmutableArray<WrapPoint> ReadPolygon()
            {
                if(Elements(wrap).Take(2).ToArray() is not [var path]||path.Name!="{"+WP+"}wrapPolygon"||
                    !ValidFlag(path,"edited")||path.Attributes.Keys.Any(k=>k!="{}edited"))return [];
                var points=Elements(path).Take(66).ToArray();
                if(points.Length is <3 or >65)return [];
                var vertices=ImmutableArray.CreateBuilder<WrapPoint>(points.Length);
                for(int i=0;i<points.Length;i++)
                {
                    var point=points[i];
                    if(point.Name!="{"+WP+"}"+(i==0?"start":"lineTo")||point.Attributes.Count!=2||!Leaf(point)||
                        !Offset(Value(point,"x"),out long px)||!Offset(Value(point,"y"),out long py)||
                        px is <0 or >21600||py is <0 or >21600)return [];
                    vertices.Add(new(px,py));handled.Add(point.Id);
                }
                if(vertices[0]==vertices[^1])vertices.RemoveAt(vertices.Count-1);
                if(vertices.Count<3||vertices.Distinct().Count()!=vertices.Count||
                    vertices.Min(p=>p.Y)!=0||vertices.Max(p=>p.Y)!=21600)return [];
                // For a simple convex path every other vertex stays on one side of each directed edge.
                // Bound the quadratic check by the vertex limit above.
                int sign=0;
                for(int i=0;i<vertices.Count;i++)
                {
                    var a=vertices[i];var b=vertices[(i+1)%vertices.Count];
                    foreach(var c in vertices)
                    {
                        int sideOfEdge=Math.Sign((b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X));
                        if(sideOfEdge==0)continue;
                        if(sign!=0&&sideOfEdge!=sign)return [];
                        sign=sideOfEdge;
                    }
                }
                if(sign==0)return [];
                handled.Add(path.Id);
                return vertices.ToImmutable();
            }

            bool Position(string name,string? extraReference,out string result,out long offset)
            {
                result="";offset=0;
                var n=Part(WP,name);
                if(n is null||n.Attributes.Count!=1||Value(n,"relativeFrom") is not {} reference||
                    reference is not ("page" or "margin")&&reference!=extraReference||
                    Elements(n).Take(2).ToArray() is not [var coordinate]||coordinate.Name!="{"+WP+"}posOffset"||
                    coordinate.Attributes.Count!=0||!Leaf(coordinate)||!Offset(Text(coordinate),out offset))return false;
                result=reference;
                handled.UnionWith(Descendants(n).Select(c=>c.Id));
                return true;
            }
            bool InactiveSize(InventoryNode? n,string percentage)
            {
                if(n is null||n.Attributes.Count!=1||Value(n,"relativeFrom") is not ("margin" or "page")||
                    Elements(n).Take(2).ToArray() is not [var number]||number.Name!="{"+WP14+"}"+percentage||
                    number.Attributes.Count!=0||!Leaf(number)||Text(number) is not ("0" or "0%"))return false;
                handled.UnionWith(Descendants(n).Select(c=>c.Id));
                return true;
            }
            FloatingPicture? Refuse()
            {
                Issue("LAYOUT_FLOATING_IMAGE_UNQUALIFIED",drawing,
                    "Image flottante hors du profil des offsets et exclusions de tête de paragraphe.");
                return null;
            }
            InventoryNode? Part(string ns,string name)=>parts.FirstOrDefault(n=>n.Name=="{"+ns+"}"+name);
            string Text(InventoryNode n)=>string.Concat(children[n.Id].Where(c=>c.Kind=="textValue").Select(c=>c.Text)).Trim();
            bool Leaf(InventoryNode n)=>!Elements(n).Any();
        }

        static string? Value(InventoryNode n,string name)=>n.Attributes.GetValueOrDefault("{}"+name);
        static bool Offset(string? raw,out long value)=>long.TryParse(raw,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value)&&
            value is >=-18_288_000 and <=18_288_000;
        static bool Flag(InventoryNode n,string name,out bool value)
        {
            string? raw=Value(n,name);
            value=raw is "1" or "true";
            return value||raw is "0" or "false";
        }
        static bool Bool(InventoryNode n,string name,bool wanted,bool required=true)=>
            Flag(n,name,out bool value)?value==wanted:!required&&Value(n,name) is null;
        static bool ValidFlag(InventoryNode n,string name)=>Value(n,name) is null||Flag(n,name,out _);
    }
}
