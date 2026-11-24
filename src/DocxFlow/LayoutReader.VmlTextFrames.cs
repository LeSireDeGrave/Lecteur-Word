using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        const string VmlWord="{urn:schemas-microsoft-com:office:word}";

        bool HasVmlTextFrame(InventoryNode picture)=>Elements(picture).Any(node=>
            node.Name is VmlName+"shape" or VmlName+"rect"&&Elements(node).Any(c=>c.Name==VmlName+"textbox"));

        FloatingTextFrame? ReadVmlTextFrame(InventoryNode picture,bool paired=false)
        {
            var members=Elements(picture).Where(n=>n.Name!=VmlName+"shapetype").Take(2).ToArray();
            if(members is not [var shape]||!OnlyAttributes(picture,"{}anchorId","{"+WP14+"}anchorId")||
                !OnlyAttributes(shape,"{}id",Office+"spid","{}type","{}style","{}alt","{}title",Office+"gfxdata",
                    Office+"allowincell","{}filled","{}stroked","{}strokeweight","{}fillcolor","{}strokecolor")||
                !PaintColor(shape,"filled","fillcolor",out var fill)||!PaintColor(shape,"stroked","strokecolor",out var stroke)||
                shape.Attributes.GetValueOrDefault(Office+"allowincell") is not (null or "f" or "false" or "0")||
                !Rectangle(shape)||Elements(shape).Take(3).ToArray() is not [var textbox,var wrap]||
                textbox.Name!=VmlName+"textbox"||!OnlyAttributes(textbox,"{}inset")||
                Elements(textbox).Take(2).ToArray() is not [var content]||!Is(content,"txbxContent")||
                !OnlyAttributes(content)||!Elements(content).All(n=>Is(n,"p")||Is(n,"tbl"))||
                wrap.Name!=VmlWord+"wrap"||Elements(wrap).Any()||!OnlyAttributes(wrap,"{}type","{}anchorx","{}anchory")||
                (Value(wrap,"type")!="none"&&!(paired&&Value(wrap,"type") is null))||
                !ReadStyle(Value(shape,"style"),out var style)||style.GetValueOrDefault("position")!="absolute"||
                style.GetValueOrDefault("visibility") is not (null or "visible")||
                style.GetValueOrDefault("mso-wrap-style") is not (null or "square")||
                !ReadAxis("horizontal","left",true,out float x,out string? hAlign,out string horizontal)||
                !ReadAxis("vertical","top",false,out float y,out string? vAlign,out string vertical)||
                !MatchingAnchor(wrap,"anchorx",horizontal)||!MatchingAnchor(wrap,"anchory",vertical)||
                !Length(style.GetValueOrDefault("width"),out float width)||width<=0||
                !Length(style.GetValueOrDefault("height"),out float height)||height<=0||
                !int.TryParse(style.GetValueOrDefault("z-index"),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int z)||
                !Insets(Value(textbox,"inset"),out var inset)||
                width<=inset.Left+inset.Right||height<=inset.Top+inset.Bottom||
                style.GetValueOrDefault("v-text-anchor") is not (null or "top" or "middle"))
                return null;
            foreach(string edge in new[]{"left","right","top","bottom"})
                if(style.TryGetValue("mso-wrap-distance-"+edge,out string? raw)&&(!Length(raw,out float distance)||distance<0))
                    return null;
            FrameStroke? outline=null;
            if(stroke is {} color)
            {
                if(!Length(Value(shape,"strokeweight")??".75pt",out float weight)||weight is <=0 or >10)return null;
                outline=new(color,weight);
            }
            return new(picture.Id,shape.Source,content.Id,x,y,width,height,inset.Left,inset.Top,inset.Right,inset.Bottom,
                style.GetValueOrDefault("v-text-anchor")=="middle",z<0?unchecked((uint)(z-int.MinValue)):(uint)z)
            {HorizontalReference=horizontal,VerticalReference=vertical,HorizontalAlignment=hAlign,VerticalAlignment=vAlign,
                BehindText=z<0,VmlSource=shape.Source,Fill=fill,Stroke=outline};

            bool ReadAxis(string axis,string margin,bool horizontal,out float shift,out string? align,out string basis)
            {
                shift=0;align=null;
                basis=style.GetValueOrDefault("mso-position-"+axis+"-relative") switch
                {
                    "page"=>"page","margin"=>"margin","text" when horizontal=>"column","text"=>"paragraph",_=>""
                };
                if(basis.Length==0)return false;
                string? placement=style.GetValueOrDefault("mso-position-"+axis);
                if(!Length(style.GetValueOrDefault("margin-"+margin)??"0",out float offset))return false;
                if(placement=="absolute"){shift=offset;return true;}
                if(offset!=0||basis=="paragraph"||(horizontal?placement is not ("left" or "center" or "right"):
                    placement is not ("top" or "center" or "bottom")))return false;
                align=placement;
                return true;
            }
        }

        static bool MatchingAnchor(InventoryNode wrap,string attribute,string basis)=>Value(wrap,attribute) is not {} anchor||
            anchor==(basis switch{"column" or "paragraph"=>"text",_=>basis});

        static bool PaintColor(InventoryNode shape,string enabled,string attribute,out uint? color)
        {
            color=null;
            if(Value(shape,enabled) is "f" or "false" or "0")return true;
            if(Value(shape,enabled) is not ("t" or "true" or "1")||Value(shape,attribute) is not {Length:7} rgb||
                rgb[0]!='#'||!uint.TryParse(rgb.AsSpan(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint value))
                return false;
            color=value;
            return true;
        }

        bool Rectangle(InventoryNode shape)
        {
            if(shape.Name==VmlName+"rect")return Value(shape,"type") is null;
            return shape.Name==VmlName+"shape"&&Value(shape,"type") is {Length:>1 and <=129} type&&type[0]=='#'&&
                VmlDefinition(shape.Source.PartUri,type[1..]) is {} template&&template.Name==VmlName+"shapetype"&&
                OnlyAttributes(template,"{}id","{}coordsize",Office+"spt","{}path","{"+TextStyleResolver.W14+"}anchorId")&&
                Value(template,"coordsize")=="21600,21600"&&
                template.Attributes.GetValueOrDefault(Office+"spt")=="202"&&Value(template,"path")=="m,l,21600r21600,l21600,xe"&&
                Elements(template).Take(3).ToArray() is [var stroke,var path]&&stroke.Name==VmlName+"stroke"&&
                OnlyAttributes(stroke,"{}joinstyle")&&Value(stroke,"joinstyle")=="miter"&&!Elements(stroke).Any()&&
                path.Name==VmlName+"path"&&OnlyAttributes(path,"{}gradientshapeok",Office+"connecttype")&&
                path.Attributes.GetValueOrDefault(Office+"connecttype")=="rect"&&!Elements(path).Any();
        }

        static bool OnlyAttributes(InventoryNode node,params string[] allowed)=>node.Attributes.Keys.All(key=>
            key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal)||allowed.Contains(key,StringComparer.Ordinal));

        static bool ReadStyle(string? raw,out Dictionary<string,string> style)
        {
            style=new(StringComparer.Ordinal);
            if(raw is null||raw.Length>1024)return false;
            foreach(string entry in raw.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
            {
                var pair=entry.Split(':',2,StringSplitOptions.TrimEntries);
                if(pair.Length!=2||pair[0] is not ("position" or "visibility" or "width" or "height" or "z-index" or "margin-left" or
                    "margin-top" or "mso-position-horizontal" or "mso-position-horizontal-relative" or "mso-position-vertical" or
                    "mso-position-vertical-relative" or "mso-wrap-style" or "mso-wrap-distance-left" or "mso-wrap-distance-right" or
                    "mso-wrap-distance-top" or "mso-wrap-distance-bottom" or "v-text-anchor")||!style.TryAdd(pair[0],pair[1]))
                    return false;
            }
            return true;
        }

        static bool Insets(string? raw,out ImageInsets inset)
        {
            inset=default;
            string[] values=raw?.Split(',',StringSplitOptions.TrimEntries)??[];
            if(values.Length>4)return false;
            float[] distances=[7.2f,3.6f,7.2f,3.6f];
            for(int i=0;i<values.Length;i++)
                if(values[i].Length>0&&(!Length(values[i],out distances[i])||distances[i]<0))return false;
            inset=new(distances[0],distances[1],distances[2],distances[3]);
            return true;
        }

        static bool Length(string? raw,out float points)
        {
            points=0;
            if(raw=="0")return true;
            if(raw is not {Length:>=3 and <=24})return false;
            float scale=raw[^2..] switch{"pt"=>1,"in"=>72,"cm"=>72/2.54f,"mm"=>72/25.4f,_=>0};
            if(scale==0||!float.TryParse(raw.AsSpan(0,raw.Length-2),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,out float value))return false;
            points=value*scale;
            return points is >=-1584 and <=1584;
        }
    }
}
