using System.Collections.Immutable;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        bool IsRunningVmlGroup(InventoryNode pict)=>Is(pict,"pict")&&Elements(pict).Any(n=>n.Name==VmlName+"group"&&
            DrawingStyle(n,out var style)&&
            ReadLegacyReference(style.GetValueOrDefault("mso-position-horizontal-relative"),true) is not null&&
            ReadLegacyReference(style.GetValueOrDefault("mso-position-vertical-relative"),false) is not null);

        FloatingTextFrame? ReadRunningVmlGroup(InventoryNode pict)
        {
            int remaining=256;
            var groups=Elements(pict).Where(n=>n.Name!=VmlName+"shapetype").Take(2).ToArray();
            if(groups is not [var group]||group.Name!=VmlName+"group"||!DrawingStyle(group,out var css)||
                !Length(css.GetValueOrDefault("width"),out float width)||width<=0||
                !Length(css.GetValueOrDefault("height"),out float height)||height<=0||
                !int.TryParse(css.GetValueOrDefault("z-index")??"0",NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int z)||
                !Axis(true,out string h,out string? ha,out float x,out var xp)||
                !Axis(false,out string v,out string? va,out float y,out var yp)||
                !Size(true,out var widthRelative)||!Size(false,out var heightRelative)||
                !LegacyWrap(group,css,out var wrap)||!ReadVmlNode(group,new(0,0,width,height),0,ref remaining,out var node))
                return null;
            return new(pict.Id,group.Source,"",x,y,width,height,0,0,0,0,false,
                z<0?unchecked((uint)(z-int.MinValue)):(uint)z)
            {
                HorizontalReference=h,VerticalReference=v,HorizontalAlignment=ha,VerticalAlignment=va,BehindText=z<0,
                RunningDrawing=new(node,xp,yp,widthRelative,heightRelative,wrap,default)
            };

            bool Axis(bool horizontal,out string reference,out string? alignment,out float shift,out RelativeDrawingValue? ratio)
            {
                string axis=horizontal?"horizontal":"vertical",edge=horizontal?"left":"top";
                reference=ReadLegacyReference(css.GetValueOrDefault("mso-position-"+axis+"-relative"),horizontal)??"";
                alignment=css.GetValueOrDefault("mso-position-"+axis);
                ratio=null;
                shift=0;
                if(reference.Length==0||!Length(css.GetValueOrDefault("margin-"+edge)??"0",out shift))return false;
                string? raw=css.GetValueOrDefault("mso-"+edge+"-percent");
                if(raw is not (null or "0"))
                {
                    if(alignment is not (null or "absolute")||shift!=0||!Number(raw,-10000,10000,out int percent))return false;
                    ratio=new(reference,percent/1000f);
                }
                if(alignment is null or "absolute"){alignment=null;return true;}
                if(horizontal?alignment is not ("left" or "center" or "right" or "inside" or "outside"):
                    alignment is not ("top" or "center" or "bottom"))return false;
                shift=0;
                return true;
            }

            bool Size(bool horizontal,out RelativeDrawingValue? ratio)
            {
                ratio=null;
                string dimension=horizontal?"width":"height";
                string? raw=css.GetValueOrDefault("mso-"+dimension+"-percent");
                if(raw is null or "0")return true;
                if(ReadLegacyReference(css.GetValueOrDefault("mso-"+dimension+"-relative"),horizontal) is not {} basis||
                    !Number(raw,1,10000,out int percent))return false;
                ratio=new(basis,percent/1000f);
                return true;
            }
        }

        bool ReadVmlNode(InventoryNode node,PageRect box,int depth,ref int remaining,out RunningDrawingNode result)
        {
            token.ThrowIfCancellationRequested();
            result=null!;
            if(depth>8||--remaining<0||!DrawingStyle(node,out var css))return false;
            if(node.Name==VmlName+"group")
            {
                if(!OnlyAttributes(node,"{}style","{}id","{}coordsize","{}coordorigin",Office+"spid",Office+"gfxdata",
                    "{"+WP14+"}anchorId","{"+TextStyleResolver.W14+"}anchorId")||
                    !Pair(Value(node,"coordsize"),out int w,out int h)||w<=0||h<=0||
                    !Pair(Value(node,"coordorigin")??"0,0",out int x,out int y))return false;
                var entries=Elements(node).Where(n=>n.Name!=VmlName+"shapetype"&&n.Name!=VmlWord+"wrap").Take(65).ToArray();
                if(entries.Length is <1 or >64)return false;
                var members=ImmutableArray.CreateBuilder<RunningDrawingNode>();
                foreach(var child in entries)
                {
                    if(!DrawingStyle(child,out var style)||style.Keys.Any(k=>k is not ("position" or "left" or "top" or
                        "width" or "height" or "visibility" or "mso-wrap-style" or "v-text-anchor"))||
                        !Coordinate(style.GetValueOrDefault("left")??"0",out int lx)||!Coordinate(style.GetValueOrDefault("top")??"0",out int ly)||
                        !Coordinate(style.GetValueOrDefault("width"),out int lw)||lw<=0||
                        !Coordinate(style.GetValueOrDefault("height"),out int lh)||lh<=0||
                        !ReadVmlNode(child,new(lx,ly,lw,lh),depth+1,ref remaining,out var parsed))return false;
                    members.Add(parsed);
                }
                result=new(node.Source,null,box,new(x,y,w,h),members.ToImmutable(),null,default,"t",null,null);
                return true;
            }
            if(!Rectangle(node)||!OnlyAttributes(node,"{}id",Office+"spid","{}type","{}style","{}alt","{}title","{}filled",
                "{}stroked","{}strokeweight","{}fillcolor","{}strokecolor")||
                !LegacyPaint(node,"filled","fillcolor",out uint? fill)||!LegacyPaint(node,"stroked","strokecolor",out uint? line)||
                Elements(node).Count()>1)return false;
            FrameStroke? stroke=null;
            if(line is {} color)
            {
                if(!Length(Value(node,"strokeweight")??".75pt",out float size)||size is <=0 or >10)return false;
                stroke=new(color,size);
            }
            string? story=null;
            ImageInsets inset=default;
            string anchor="t";
            if(Elements(node).Any())
            {
                if(!Single(node,Vml,"textbox",out var text)||!OnlyAttributes(text,"{}inset")||
                    !Single(text,W,"txbxContent",out var content)||!Insets(Value(text,"inset"),out inset))return false;
                anchor=css.GetValueOrDefault("v-text-anchor") switch{null or "top"=>"t","middle"=>"ctr","bottom"=>"b",_=>""};
                if(anchor.Length==0)return false;
                story=content.Id;
            }
            result=new(node.Source,null,box,null,[],story,inset,anchor,fill,stroke);
            return true;
        }

        bool LegacyWrap(InventoryNode node,Dictionary<string,string> css,out RunningDrawingWrap? wrap)
        {
            wrap=null;
            var specific=Elements(node).Where(n=>n.Name==VmlWord+"wrap").Take(2).ToArray();
            if(specific.Length==0)return css.GetValueOrDefault("mso-wrap-style") is (null or "none");
            if(specific is not [var rule]||!OnlyAttributes(rule,"{}type","{}side","{}anchorx","{}anchory")||Elements(rule).Any())return false;
            string? kind=Value(rule,"type");
            if(kind is null or "none")return true;
            string side=Value(rule,"side")??"both";
            if(kind is not ("square" or "topAndBottom")||side is not ("left" or "right" or "both" or "largest")||
                !Distance("left",out float left)||!Distance("top",out float top)||
                !Distance("right",out float right)||!Distance("bottom",out float bottom))return false;
            wrap=new(kind,side=="both"?"bothSides":side,new(left,top,right,bottom));
            return true;

            bool Distance(string edge,out float points)=>Length(css.GetValueOrDefault("mso-wrap-distance-"+edge)??"0",out points)&&points>=0;
        }
    }
}
