using System.Xml.Linq;
using static LecteurWord.Flow.StructuredPublication;

namespace LecteurWord.Flow;

internal static class PublicationSvg
{
    internal static string Encode(InlineImage image)
    {
        var root=new XElement(Svg+"svg",new XAttribute("viewBox",$"0 0 {Num(image.Width)} {Num(image.Height)}"),
            new XAttribute("width",Pt(image.Width)),new XAttribute("height",Pt(image.Height)));
        if(image.VmlVector is {} vector)
            foreach(var polygon in vector.Polygons)
                root.Add(new XElement(Svg+"polygon",new XAttribute("points",string.Join(' ',
                    polygon.Vertices.Select(vertex=>Point(vertex.X,vertex.Y)))),new XAttribute("fill",Color(polygon.Rgb))));
        if(image.Diagram is not {} diagram)return Xml(root);
        foreach(var connector in diagram.Connectors)
        {
            var pts=connector.Points;
            string data="M "+Point(pts[0].X,pts[0].Y)+(connector.Route=="curve"?" C ":" L ")+
                string.Join(' ',pts.Skip(1).Select(p=>Point(p.X,p.Y)));
            string color=Color(connector.Stroke.Color);
            root.Add(new XElement(Svg+"path",new XAttribute("d",data),new XAttribute("fill","none"),
                new XAttribute("stroke",color),new XAttribute("stroke-width",Num(connector.Stroke.Width))));
            if(connector.ArrowAtStart)Arrow(pts[0],pts.Skip(1).First(p=>p!=pts[0]));
            if(connector.ArrowAtEnd)Arrow(pts[^1],pts.Reverse().Skip(1).First(p=>p!=pts[^1]));
            void Arrow(System.Numerics.Vector2 tip,System.Numerics.Vector2 previous)
            {
                var direction=System.Numerics.Vector2.Normalize(tip-previous);
                float length=Math.Min(connector.Stroke.Width*4,System.Numerics.Vector2.Distance(tip,previous));
                var basePoint=tip-direction*length;
                var normal=new System.Numerics.Vector2(-direction.Y,direction.X)*length*.45f;
                root.Add(new XElement(Svg+"polygon",new XAttribute("points",string.Join(' ',
                    new[]{tip,basePoint+normal,basePoint-normal}.Select(p=>Point(p.X,p.Y)))),new XAttribute("fill",color)));
            }
        }
        foreach(var shape in diagram.Shapes)
        {
            var box=shape.Box;
            float cx=box.Left+box.Width/2,cy=box.Top+box.Height/2;
            var group=new XElement(Svg+"g");
            if(shape.Rotation!=0)group.SetAttributeValue("transform",$"rotate({Num(shape.Rotation)} {Num(cx)} {Num(cy)})");
            XElement geometry;
            if(!shape.Outline.IsDefaultOrEmpty)
                geometry=new(Svg+"polygon",new XAttribute("points",string.Join(' ',shape.Outline.Select(p=>Point(p.X,p.Y)))));
            else if(shape.Geometry=="ellipse")
                geometry=new(Svg+"ellipse",new XAttribute("cx",Num(cx)),new XAttribute("cy",Num(cy)),
                    new XAttribute("rx",Num(box.Width/2)),new XAttribute("ry",Num(box.Height/2)));
            else if(shape.Geometry=="rightArrow")
            {
                float shoulder=box.Left+box.Width-Math.Min(box.Width,Math.Min(box.Width,box.Height)*shape.SecondAdjustment/100_000);
                float pad=box.Height*(1-Math.Clamp(shape.FirstAdjustment,0,100_000)/100_000)/2;
                float right=box.Left+box.Width,bottom=box.Top+box.Height;
                geometry=new(Svg+"polygon",new XAttribute("points",string.Join(' ',
                    new[]{(box.Left,box.Top+pad),(shoulder,box.Top+pad),(shoulder,box.Top),(right,cy),(shoulder,bottom),
                        (shoulder,bottom-pad),(box.Left,bottom-pad)}.Select(p=>Point(p.Item1,p.Item2)))));
            }
            else
            {
                geometry=new(Svg+"rect",new XAttribute("x",Num(box.Left)),new XAttribute("y",Num(box.Top)),
                    new XAttribute("width",Num(box.Width)),new XAttribute("height",Num(box.Height)));
                if(shape.Geometry=="roundRect")
                    geometry.SetAttributeValue("rx",Num(Math.Min(box.Width,box.Height)*Math.Clamp(shape.FirstAdjustment,0,50_000)/100_000));
            }
            geometry.SetAttributeValue("fill",shape.Fill is {} fill?Color(fill):"none");
            if(shape.Stroke is {} pen)
            {
                geometry.SetAttributeValue("stroke",Color(pen.Color));
                geometry.SetAttributeValue("stroke-width",Num(pen.Width));
            }
            group.Add(geometry);
            foreach(var line in shape.Text)
                group.Add(new XElement(Svg+"text",new XAttribute("x",Num(line.X)),new XAttribute("y",Num(line.Baseline)),
                    new XAttribute("style",CssStyle(line.Fragment.Style,line.Fragment.Face?.UsedFamily)+
                        ";fill:"+(line.Fragment.Style.Color=="auto"?"#000000":"#"+line.Fragment.Style.Color)),line.Fragment.Text));
            root.Add(group);
        }
        return Xml(root);
    }

    static string Point(float x,float y)=>Num(x)+","+Num(y);
    static string Color(uint rgb)=>"#"+(rgb&0xffffff).ToString("X6",System.Globalization.CultureInfo.InvariantCulture);
}
