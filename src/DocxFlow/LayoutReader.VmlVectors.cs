using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal readonly record struct VmlVertex(float X,float Y);
internal sealed record VmlPolygon(SourceLocation Source,uint Rgb,ImmutableArray<VmlVertex> Vertices);
internal sealed record VmlVectorPicture(ImmutableArray<VmlPolygon> Polygons);

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        static readonly Regex VmlCoordinates=new(@"\s*,\s*|\s+",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking);

        InlineImage? ReadInlineVmlGroup(InventoryNode picture)
        {
            string anchor="{"+TextStyleResolver.W14+"}anchorId";
            if(Elements(picture).Take(2).ToArray() is not [var group]||group.Name!=VmlName+"group"||
                !OnlyAttributes(picture,anchor)||!OnlyAttributes(group,"{}id","{}style","{}coordsize","{}coordorigin",
                    anchor,Office+"spid",Office+"gfxdata")||
                !TryStyle(group,out var box)||!Length(box.GetValueOrDefault("width"),out float width)||
                !Length(box.GetValueOrDefault("height"),out float height)||width<=0||height<=0||
                box.Any(p=>p.Key is not ("width" or "height")&&p is not
                    ({Key:"mso-position-horizontal-relative",Value:"char"} or {Key:"mso-position-vertical-relative",Value:"line"}))||
                !Pair(Value(group,"coordsize"),out int gw,out int gh)||gw<=0||gh<=0||
                !Pair(Value(group,"coordorigin")??"0,0",out int gx,out int gy))return null;

            var polygons=ImmutableArray.CreateBuilder<VmlPolygon>();
            int vertices=0;
            bool locked=false;
            foreach(var shape in Elements(group))
            {
                token.ThrowIfCancellationRequested();
                if(shape.Name==VmlWord+"anchorlock"&&!locked&&OnlyAttributes(shape)&&!Elements(shape).Any())
                {
                    locked=true;
                    continue;
                }
                if(locked||polygons.Count>=64||shape.Name!=VmlName+"shape"||
                    !OnlyAttributes(shape,"{}id","{}style","{}coordsize","{}coordorigin","{}path","{}fillcolor","{}filled","{}stroked",
                        Office+"spid",Office+"gfxdata")||
                    Value(shape,"filled") is not (null or "t" or "true" or "1")||
                    Value(shape,"stroked") is not ("f" or "false" or "0")||
                    Value(shape,"fillcolor") is not {Length:7} fill||fill[0]!='#'||
                    !uint.TryParse(fill.AsSpan(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint color)||
                    !TryStyle(shape,out var local)||!LocalBox(local,out int sx,out int sy,out int sw,out int sh)||
                    !Pair(Value(shape,"coordsize"),out int cw,out int ch)||cw<=0||ch<=0||
                    !Pair(Value(shape,"coordorigin")??"0,0",out int cx,out int cy)||
                    !Elements(shape).All(node=>node.Name==VmlName+"path"&&
                        OnlyAttributes(node,"{}arrowok",Office+"connecttype",Office+"connectlocs",Office+"connectangles")&&!Elements(node).Any())||
                    Elements(shape).Take(2).Count()>1||
                    !PolygonPoints(Value(shape,"path"),out var points)||vertices+points.Length>4096)
                    return null;
                vertices+=points.Length;
                var physical=points.Select(p=>new VmlVertex(
                    (float)(width*(sx-gx+(p.X-cx)*(double)sw/cw)/gw),
                    (float)(height*(sy-gy+(p.Y-cy)*(double)sh/ch)/gh))).ToImmutableArray();
                if(physical.Any(p=>p.X<-.01f||p.Y<-.01f||p.X>width+.01f||p.Y>height+.01f))return null;
                polygons.Add(new(shape.Source,color,physical));
            }
            if(polygons.Count==0||Descendants(picture).Any(n=>n.Kind=="textValue"&&!string.IsNullOrWhiteSpace(n.Text)))return null;
            return new(picture.Id,picture.Source,width,height,0,0,semantic!.SnapshotId)
            {VmlVector=new(polygons.ToImmutable())};
        }

        static bool TryStyle(InventoryNode node,out Dictionary<string,string> pairs)
        {
            pairs=new(StringComparer.Ordinal);
            if(Value(node,"style") is not {Length:>0 and <=1024} raw)return false;
            foreach(string part in raw.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
            {
                var pair=part.Split(':',2,StringSplitOptions.TrimEntries);
                if(pair.Length!=2||!pairs.TryAdd(pair[0],pair[1]))return false;
            }
            return true;
        }

        static bool LocalBox(Dictionary<string,string> style,out int x,out int y,out int w,out int h)
        {
            x=y=w=h=0;
            return style.GetValueOrDefault("position") is null or "absolute" or "relative"&&
                style.All(p=>p.Key is "position" or "left" or "top" or "width" or "height"||p is
                    ({Key:"visibility",Value:"visible"} or {Key:"mso-wrap-style",Value:"square"} or {Key:"v-text-anchor",Value:"top"}))&&
                Coordinate(style.GetValueOrDefault("left")??"0",out x)&&Coordinate(style.GetValueOrDefault("top")??"0",out y)&&
                Coordinate(style.GetValueOrDefault("width"),out w)&&Coordinate(style.GetValueOrDefault("height"),out h)&&w>0&&h>0;
        }

        static bool Pair(string? raw,out int x,out int y)
        {
            x=y=0;
            return raw is {Length:<=48}&&VmlCoordinates.Split(raw.Trim()) is [var left,var top]&&
                Coordinate(left,out x)&&Coordinate(top,out y);
        }

        static bool Coordinate(string? raw,out int value)=>
            int.TryParse(raw,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value)&&value is >=-1_000_000 and <=1_000_000;

        static bool PolygonPoints(string? raw,out ImmutableArray<(int X,int Y)> points)
        {
            points=[];
            if(raw is not {Length:>=6 and <=16384})return false;
            var vertices=ImmutableArray.CreateBuilder<(int X,int Y)>();
            bool closed=false;
            int x=0,y=0;
            for(int i=0;i<raw.Length;)
            {
                if(char.IsWhiteSpace(raw[i])){i++;continue;}
                char command=raw[i++];
                if(command=='e'&&closed&&raw.AsSpan(i).Trim().IsEmpty){points=vertices.ToImmutable();return true;}
                if(command=='x'&&!closed&&vertices.Count>=3){closed=true;continue;}
                if(closed||vertices.Count==0&&command!='m'||vertices.Count!=0&&command is not ('l' or 'r'))return false;
                int start=i;
                while(i<raw.Length&&!char.IsAsciiLetter(raw[i]))i++;
                var values=VmlCoordinates.Split(raw[start..i].Trim());
                if(values.Length<2||(values.Length&1)!=0||command=='m'&&values.Length!=2||
                    vertices.Count+values.Length/2>512)return false;
                for(int k=0;k<values.Length;k+=2)
                {
                    if(!Coordinate(values[k].Length==0?"0":values[k],out int a)||
                        !Coordinate(values[k+1].Length==0?"0":values[k+1],out int b))return false;
                    x=command=='r'?x+a:a;y=command=='r'?y+b:b;
                    if(x is <-1_000_000 or >1_000_000||y is <-1_000_000 or >1_000_000)return false;
                    vertices.Add((x,y));
                }
            }
            return false;
        }
    }
}
