using System.Collections.Immutable;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        InlineImage? ReadWpsVector(InventoryNode drawing,InventoryNode shape,InventoryNode fallback,float width,float height,out string detail)
        {
            string wps="{"+Wps+"}",a="{"+D+"}";
            detail="Forme libre WPS : structure hors profil.";
            if(!OnlyAttributes(shape)||!Elements(shape).Select(n=>n.Name).SequenceEqual(
                    new[]{wps+"cNvSpPr",wps+"spPr",wps+"bodyPr"})||
                Member(shape,Wps,"spPr") is not {} properties||
                !Elements(properties).Select(n=>n.Name).SequenceEqual(new[]{a+"xfrm",a+"custGeom",a+"solidFill",a+"ln"}))
                return null;
            detail="Forme libre WPS : transformation différente de l’étendue de l’ancre.";
            if(Member(properties,D,"xfrm") is not {} transform||!ReadBox(transform,out var box)||
                box.Left!=0||box.Top!=0||Math.Abs(box.Width-width)>.01f||Math.Abs(box.Height-height)>.01f)return null;
            detail="Forme libre WPS : couleurs ou contour hors profil.";
            if(Member(properties,D,"ln") is not {} stroke||!OnlyAttributes(stroke)||
                Elements(stroke).Take(2).ToArray() is not [var noStroke]||noStroke.Name!=a+"noFill"||
                Member(properties,D,"solidFill") is not {} fill||
                Elements(fill).Take(2).ToArray() is not [var paint]||paint.Name!=a+"srgbClr"||Elements(paint).Any()||
                Value(paint,"val") is not {Length:6} hex||!uint.TryParse(hex,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint color))
                return null;
            detail="Forme libre WPS : référentiel du chemin hors profil.";
            if(Member(properties,D,"custGeom") is not {} geometry||
                !Elements(geometry).All(n=>n.Name==a+"pathLst"||n.Name==a+"rect"&&
                    Value(n,"l")=="0"&&Value(n,"t")=="0"&&Value(n,"r")=="r"&&Value(n,"b")=="b"&&!Elements(n).Any()||
                    n.Name is "{"+D+"}avLst" or "{"+D+"}ahLst"&&!Elements(n).Any()||
                    n.Name is "{"+D+"}gdLst" or "{"+D+"}cxnLst")||
                !ReadConnectionGuides(geometry)||
                Member(geometry,D,"pathLst") is not {} paths||Elements(paths).Take(2).ToArray() is not [var path]||path.Name!=a+"path"||
                !OnlyAttributes(path,"{}w","{}h")||!Coordinate(Value(path,"w"),out int latticeWidth)||
                !Coordinate(Value(path,"h"),out int latticeHeight)||latticeWidth<=0||latticeHeight<=0)
                return null;
            detail="Forme libre WPS : chemin polygonal fermé ou coordonnées hors profil.";
            var nodes=Elements(path).Take(514).ToArray();
            if(nodes.Length is <4 or >513||nodes[^1].Name!=a+"close"||Elements(nodes[^1]).Any())return null;
            var drawingPoints=ImmutableArray.CreateBuilder<(int X,int Y)>();
            for(int i=0;i<nodes.Length-1;i++)
            {
                token.ThrowIfCancellationRequested();
                var command=nodes[i];
                if(command.Name!=a+(i==0?"moveTo":"lnTo")||!OnlyAttributes(command)||
                    Elements(command).Take(2).ToArray() is not [var point]||point.Name!=a+"pt"||
                    !OnlyAttributes(point,"{}x","{}y")||Elements(point).Any()||
                    !Coordinate(Value(point,"x"),out int x)||!Coordinate(Value(point,"y"),out int y)||
                    x<0||x>latticeWidth||y<0||y>latticeHeight)return null;
                drawingPoints.Add((x,y));
            }
            detail="Forme libre WPS : chemin ou remplissage différent dans la représentation VML.";
            if(Elements(fallback).Take(2).ToArray() is not [var vml]||vml.Name!=VmlName+"shape"||
                Value(vml,"fillcolor") is not {Length:7} oldColor||!oldColor.Equals("#"+hex,StringComparison.OrdinalIgnoreCase)||
                Value(vml,"stroked") is not ("f" or "false" or "0")||
                !Pair(Value(vml,"coordsize"),out int legacyWidth,out int legacyHeight)||legacyWidth!=latticeWidth||legacyHeight!=latticeHeight||
                Value(vml,"coordorigin") is not (null or "0,0")||
                !PolygonPoints(Value(vml,"path"),out var oldPath)||
                !ClosePath(drawingPoints.ToImmutable()).SequenceEqual(ClosePath(oldPath))||
                Descendants(fallback).Any(n=>n.Name==VmlName+"textbox"||n.Kind=="textValue"&&!string.IsNullOrWhiteSpace(n.Text)))
                return null;
            var points=drawingPoints.Select(point=>new VmlVertex(width*point.X/latticeWidth,height*point.Y/latticeHeight)).ToImmutableArray();
            return new(drawing.Id,drawing.Source,width,height,0,0,semantic!.SnapshotId){VmlVector=new([new(path.Source,color,points)])};

            static ImmutableArray<(int X,int Y)> ClosePath(ImmutableArray<(int X,int Y)> points)=>
                points.Length>3&&points[0]==points[^1]?points[..^1]:points;
        }

        bool ReadConnectionGuides(InventoryNode geometry)
        {
            var known=new HashSet<string>(StringComparer.Ordinal);
            if(Member(geometry,D,"gdLst") is {} guideList)
                foreach(var guide in Elements(guideList))
                {
                    token.ThrowIfCancellationRequested();
                    if(known.Count>=512||guide.Name!="{"+D+"}gd"||!OnlyAttributes(guide,"{}name","{}fmla")||
                        Value(guide,"name") is not {Length:>0 and <=32} name||!known.Add(name)||Elements(guide).Any()||
                        Value(guide,"fmla") is not {Length:>0 and <=96} formula)
                        return false;
                    var parts=formula.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                    if(parts is ["val",var literal]&&Integer(literal))continue;
                    if(parts is ["*/",var multiple,var factor,var divisor]&&Integer(multiple)&&
                        (factor is "w" or "h"||Integer(factor))&&
                        Number(divisor,1,1_000_000,out _))continue;
                    return false;
                }
            if(Member(geometry,D,"cxnLst") is not {} connections)return true;
            int count=0;
            foreach(var connection in Elements(connections))
            {
                token.ThrowIfCancellationRequested();
                if(++count>256||connection.Name!="{"+D+"}cxn"||!OnlyAttributes(connection,"{}ang")||
                    !GuideOrInteger(Value(connection,"ang"))||
                    Elements(connection).Take(2).ToArray() is not [var pos]||pos.Name!="{"+D+"}pos"||
                    !OnlyAttributes(pos,"{}x","{}y")||Elements(pos).Any()||
                    !GuideOrInteger(Value(pos,"x"))||!GuideOrInteger(Value(pos,"y")))return false;
            }
            return true;

            bool GuideOrInteger(string? v)=>v is not null&&(known.Contains(v)||Integer(v));
            static bool Integer(string v)=>Number(v,-21_600_000,21_600_000,out _);
        }
    }
}
