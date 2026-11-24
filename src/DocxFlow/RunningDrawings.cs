using System.Collections.Immutable;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal readonly record struct RelativeDrawingValue(string Reference,float Fraction);
internal sealed record RunningDrawingNode(SourceLocation Source,SourceLocation? Fallback,PageRect Box,
    PageRect? ChildSpace,ImmutableArray<RunningDrawingNode> Children,string? StoryId,ImageInsets Insets,
    string TextAnchor,uint? Fill,FrameStroke? Stroke);
internal sealed record RunningDrawingWrap(string Kind,string Side,ImageInsets Distances);
internal sealed record RunningDrawing(RunningDrawingNode Root,RelativeDrawingValue? X,RelativeDrawingValue? Y,
    RelativeDrawingValue? Width,RelativeDrawingValue? Height,RunningDrawingWrap? Wrap,ImageInsets Effects)
{
    internal (PageRect Box,FloatingTextFrame[] Leaves) Place(FloatingTextFrame anchor,PageGeometry page,int physicalPage)
    {
        float WidthOf(RelativeDrawingValue value)=>FloatingTextFrame.ReferenceSpan(page,physicalPage,value.Reference,true).Size;
        float HeightOf(RelativeDrawingValue value)=>FloatingTextFrame.ReferenceSpan(page,physicalPage,value.Reference,false).Size;
        var sized=anchor with
        {
            X=X is {} x?WidthOf(x)*x.Fraction:anchor.X,
            Y=Y is {} y?HeightOf(y)*y.Fraction:anchor.Y,
            Width=Width is {} width?WidthOf(width)*width.Fraction:anchor.Width,
            Height=Height is {} height?HeightOf(height)*height.Fraction:anchor.Height
        };
        var (left,top)=sized.PageOrigin(page,physicalPage,0);
        int order=0;
        var target=new PageRect(left,top,sized.Width,sized.Height);
        return (target,Leaves(Root,target).ToArray());

        IEnumerable<FloatingTextFrame> Leaves(RunningDrawingNode node,PageRect target)
        {
            if(node.ChildSpace is {} space)
            {
                float sx=target.Width/space.Width,sy=target.Height/space.Height;
                foreach(var child in node.Children)
                {
                    var rect=child.Box;
                    foreach(var leaf in Leaves(child,new PageRect(target.Left+(rect.Left-space.Left)*sx,
                        target.Top+(rect.Top-space.Top)*sy,rect.Width*sx,rect.Height*sy)))yield return leaf;
                }
                yield break;
            }

            yield return anchor with
            {
                Drawing=node.Source,VmlSource=node.Fallback,StoryId=node.StoryId??"",
                X=target.Left,Y=target.Top,Width=target.Width,Height=target.Height,
                Left=node.Insets.Left,Top=node.Insets.Top,Right=node.Insets.Right,Bottom=node.Insets.Bottom,
                Center=node.TextAnchor=="ctr",BottomAligned=node.TextAnchor=="b",Fill=node.Fill,Stroke=node.Stroke,
                ChildOrder=order++
            };
        }
    }

    internal PageRect WrapBounds(PageRect anchor,IEnumerable<FloatingTextFrame> leaves)
    {
        PageRect bounds=Expand(anchor,Effects);
        foreach(var leaf in leaves)
        {
            float half=(leaf.Stroke?.Width??0)/2;
            var box=Expand(new(leaf.X,leaf.Y,leaf.Width,leaf.Height),new(half,half,half,half));
            float left=Math.Min(bounds.Left,box.Left),top=Math.Min(bounds.Top,box.Top);
            bounds=new(left,top,Math.Max(bounds.Left+bounds.Width,box.Left+box.Width)-left,
                Math.Max(bounds.Top+bounds.Height,box.Top+box.Height)-top);
        }
        return Wrap is {} wrap?Expand(bounds,wrap.Distances):bounds;

        static PageRect Expand(PageRect box,ImageInsets inset)=>new(box.Left-inset.Left,box.Top-inset.Top,
            box.Width+inset.Left+inset.Right,box.Height+inset.Top+inset.Bottom);
    }
}

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        const string Wpg="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
        const string McChoice="{"+SemanticIndex.Mc+"}Choice",McFallback="{"+SemanticIndex.Mc+"}Fallback";

        bool IsRunningDrawing(InventoryNode alternative)
        {
            if(!runningStory||!SemanticIndex.IsAlternative(alternative))return false;
            var choice=Elements(alternative).FirstOrDefault(n=>n.Name==McChoice);
            if(choice is null||Value(choice,"Requires") is not {} prefix)return false;
            return Namespace(choice,prefix)==Wpg&&Descendants(choice).Any(n=>n.Name=="{"+WP+"}anchor")||
                Namespace(choice,prefix)==Wps&&Descendants(choice).Any(n=>n.Name is
                    "{"+WP14+"}pctPosHOffset" or "{"+WP14+"}pctPosVOffset"||
                    n.Name is ("{"+WP14+"}pctWidth" or "{"+WP14+"}pctHeight")&&DrawingValue(n)!="0"||
                    n.Name=="{"+Wps+"}bodyPr"&&Value(n,"anchor")=="b");
        }

        FloatingTextFrame? ReadRunningDrawing(InventoryNode alternative)
        {
            if(Elements(alternative).ToArray() is not [var choice,var fallback]||choice.Name!=McChoice||fallback.Name!=McFallback||
                Value(choice,"Requires") is not {} prefix||Namespace(choice,prefix) is not (Wps or Wpg)||
                !Single(choice,W,"drawing",out var drawing)||!Single(drawing,WP,"anchor",out var anchor)||
                !Single(fallback,W,"pict",out var pict)||semantic!.Element(pict.Id).Coverage==SemanticCoverage.InactiveAlternative)
                return null;
            InventoryNode[] items=Elements(anchor).ToArray();
            if(!OnlyAttributes(anchor,"{}simplePos","{}distL","{}distR","{}distT","{}distB","{}behindDoc","{}relativeHeight",
                    "{}layoutInCell","{}locked","{}allowOverlap","{"+WP14+"}anchorId","{"+WP14+"}editId")||
                Value(anchor,"simplePos")!="0"||!ValidFlag(anchor,"layoutInCell")||!ValidFlag(anchor,"locked")||
                !Flag(anchor,"behindDoc",out bool behind)||!Bool(anchor,"allowOverlap",true)||
                !uint.TryParse(Value(anchor,"relativeHeight"),NumberStyles.None,CultureInfo.InvariantCulture,out uint order)||
                items.Length is <9 or >11||items[0].Name!="{"+WP+"}simplePos"||
                Value(items[0],"x")!="0"||Value(items[0],"y")!="0"||
                !Axis(items[1],true,out string h,out var ha,out float x,out var xp)||
                !Axis(items[2],false,out string v,out var va,out float y,out var yp)||
                items[3].Name!="{"+WP+"}extent"||!ReadEmu(items[3],"cx",out float width)||!ReadEmu(items[3],"cy",out float height)||
                width<=0||height<=0||!Effects(items[4],out var effects)||!ReadDrawingWrap(items[5],anchor,out var wrap)||
                items[6].Name!="{"+WP+"}docPr"||Elements(items[6]).Any()||
                items[7].Name!="{"+WP+"}cNvGraphicFramePr"||Elements(items[7]).Any()||
                items[8].Name!="{"+D+"}graphic"||!Single(items[8],D,"graphicData",out var data))
                return null;
            RelativeDrawingValue? widthRel=null,heightRel=null;
            bool sawHeight=false;
            for(int i=9;i<items.Length;i++)
            {
                bool horizontal=items[i].Name=="{"+WP14+"}sizeRelH";
                if(horizontal&&i!=9||!horizontal&&(sawHeight||items[i].Name!="{"+WP14+"}sizeRelV")||
                    !Reference(Value(items[i],"relativeFrom"),horizontal)||!OnlyAttributes(items[i],"{}relativeFrom")||
                    !Single(items[i],WP14,horizontal?"pctWidth":"pctHeight",out var percent)||
                    !Percentage(percent,out float amount)||amount<0)return null;
                if(!horizontal)sawHeight=true;
                if(amount==0)continue;
                var spec=new RelativeDrawingValue(Value(items[i],"relativeFrom")!,amount);
                if(horizontal)widthRel=spec;
                else heightRel=spec;
            }

            string ns=Namespace(choice,prefix)!;
            int remaining=256;
            var legacy=Elements(pict).Where(n=>n.Name!=VmlName+"shapetype").ToArray();
            if(Value(data,"uri")!=ns||!Single(data,ns,ns==Wpg?"wgp":"wsp",out var graphic)||
                legacy is not [var fallbackShape]||!DrawingStyle(fallbackShape,out var style)||
                !Length(style.GetValueOrDefault("width"),out float fallbackWidth)||!Near(width,fallbackWidth)||
                !Length(style.GetValueOrDefault("height"),out float fallbackHeight)||!Near(height,fallbackHeight)||
                !int.TryParse(style.GetValueOrDefault("z-index"),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int z)||
                behind!=(z<0)||order!=(z<0?unchecked((uint)(z-int.MinValue)):(uint)z)||
                !FallbackAxis(style,true,h,ha,x,xp)||!FallbackAxis(style,false,v,va,y,yp)||
                !FallbackSize(style,true,widthRel)||!FallbackSize(style,false,heightRel)||
                !CompareLegacyWrap(fallbackShape,style,wrap)||
                !ReadNode(graphic,fallbackShape,null,0,ref remaining,out var root)||
                root.Box.Left!=0||root.Box.Top!=0||!Near(root.Box.Width,width)||!Near(root.Box.Height,height))
                return null;

            return new(pict.Id,drawing.Source,"",x,y,width,height,0,0,0,0,false,order)
            {
                HorizontalReference=h,VerticalReference=v,HorizontalAlignment=ha,VerticalAlignment=va,BehindText=behind,
                VmlSource=fallbackShape.Source,RunningDrawing=new(root,xp,yp,widthRel,heightRel,wrap,effects)
            };
        }

        bool ReadNode(InventoryNode node,InventoryNode fallback,(float X,float Y)? lattice,int depth,ref int remaining,out RunningDrawingNode result)
        {
            token.ThrowIfCancellationRequested();
            result=null!;
            if(depth>8||--remaining<0)return false;
            if(node.Name is "{"+Wpg+"}wgp" or "{"+Wpg+"}grpSp")
            {
                if(fallback.Name!=VmlName+"group"||!OnlyAttributes(fallback,"{}style","{}id","{}coordsize","{}coordorigin",
                        Office+"spid",Office+"gfxdata","{"+WP14+"}anchorId","{"+TextStyleResolver.W14+"}anchorId")||
                    Member(node,Wpg,"grpSpPr") is not {} properties||
                    !Single(properties,D,"xfrm",out var transform)||!ReadBox(transform,out var box,true)||
                    !FallbackBox(fallback,lattice,box)||Member(transform,D,"chOff") is not {} origin||
                    Member(transform,D,"chExt") is not {} extent||!ReadEmu(origin,"x",out float x)||!ReadEmu(origin,"y",out float y)||
                    !ReadEmu(extent,"cx",out float w)||!ReadEmu(extent,"cy",out float h)||w<=0||h<=0||
                    !Pair(Value(fallback,"coordsize"),out int lw,out int lh)||lw<=0||lh<=0||
                    !Pair(Value(fallback,"coordorigin")??"0,0",out int lx,out int ly))return false;
                float sx=w/lw,sy=h/lh;
                if(!Near(lx*sx,x,sx)||!Near(ly*sy,y,sy))return false;
                var members=Elements(node).Where(n=>n.Name is not ("{"+Wpg+"}cNvPr" or "{"+Wpg+"}cNvGrpSpPr" or "{"+Wpg+"}grpSpPr")).Take(65).ToArray();
                var old=Elements(fallback).Where(n=>n.Name!=VmlName+"shapetype"&&n.Name!=VmlWord+"wrap").Take(65).ToArray();
                if(members.Length is <1 or >64||members.Length!=old.Length)return false;
                var list=ImmutableArray.CreateBuilder<RunningDrawingNode>(members.Length);
                for(int i=0;i<members.Length;i++)
                {
                    if(!ReadNode(members[i],old[i],(sx,sy),depth+1,ref remaining,out var child))return false;
                    list.Add(child);
                }
                result=new(node.Source,fallback.Source,box,new(x,y,w,h),list.ToImmutable(),null,default,"t",null,null);
                return true;
            }

            if(node.Name!="{"+Wps+"}wsp"||!Rectangle(fallback)||
                !Elements(node).All(n=>n.Name is "{"+Wps+"}cNvPr" or "{"+Wps+"}cNvSpPr" or "{"+Wps+"}spPr" or
                    "{"+Wps+"}txbx" or "{"+Wps+"}bodyPr" or "{"+Wps+"}style")||
                Member(node,Wps,"spPr") is not {} shape||Member(shape,D,"xfrm") is not {} position||!ReadBox(position,out var rect)||
                !FallbackBox(fallback,lattice,rect)||!DrawingStyle(fallback,out var style)||
                !OnlyAttributes(fallback,"{}id",Office+"spid","{}type","{}style","{}alt","{}title",Office+"gfxdata",
                    Office+"allowincell","{}filled","{}stroked","{}strokeweight","{}fillcolor","{}strokecolor",
                    "{"+WP14+"}anchorId","{"+TextStyleResolver.W14+"}anchorId")||
                Member(shape,D,"prstGeom") is not {} geometry||Value(geometry,"prst")!="rect"||
                !Single(geometry,D,"avLst",out var adjustments)||Elements(adjustments).Any()||
                !Elements(shape).All(n=>n.Name is "{"+D+"}xfrm" or "{"+D+"}prstGeom" or "{"+D+"}solidFill" or
                    "{"+D+"}noFill" or "{"+D+"}ln"||n.Name=="{"+D+"}effectLst"&&!Elements(n).Any())||
                !OnlyAttributes(shape,"{}bwMode")||Value(shape,"bwMode") is not (null or "auto")||
                !DrawingPaint(shape,out uint? fill)||!LegacyPaint(fallback,"filled","fillcolor",out uint? oldFill)||fill!=oldFill||
                Member(shape,D,"ln") is not {} line||!OnlyAttributes(line,"{}w")||Elements(line).Count()!=1||
                !DrawingPaint(line,out uint? strokeColor)||
                !LegacyPaint(fallback,"stroked","strokecolor",out uint? oldStroke)||strokeColor!=oldStroke||
                Member(node,Wps,"bodyPr") is not {} body||!ReadDrawingBody(body,out var insets,out string alignment)||
                !Elements(fallback).All(n=>n.Name==VmlName+"textbox"||n.Name==VmlWord+"wrap"))return false;
            var format=Member(node,Wps,"style");
            if(format is not null&&Value(Member(format,D,"effectRef")??format,"idx")!="0")return false;
            FrameStroke? stroke=null;
            if(strokeColor is {} color)
            {
                if(!StrokeWidth(line,format,out float weight)||!Length(Value(fallback,"strokeweight")??".75pt",out float fallbackWeight)||
                    !Near(weight,fallbackWeight))return false;
                stroke=new(color,weight);
            }

            string? story=null;
            var modernText=Member(node,Wps,"txbx");
            var legacyText=Member(fallback,Vml,"textbox");
            if(modernText is not null||legacyText is not null)
            {
                if(modernText is null||legacyText is null||
                    !Single(modernText,W,"txbxContent",out var primary)||!Single(legacyText,W,"txbxContent",out var backup)||
                    !SameDrawingText(primary,backup,0)||!OnlyAttributes(legacyText,"{}inset")||!Insets(Value(legacyText,"inset"),out var backupInsets)||insets!=backupInsets||
                    (style.GetValueOrDefault("v-text-anchor")??"top")!=(alignment switch{"b"=>"bottom","ctr"=>"middle",_=>"top"}))
                    return false;
                story=backup.Id;
            }
            result=new(node.Source,fallback.Source,rect,null,[],story,insets,alignment,fill,stroke);
            return true;
        }

        bool ReadDrawingBody(InventoryNode node,out ImageInsets insets,out string alignment)
        {
            insets=default;
            alignment=Value(node,"anchor")??"t";
            if(alignment is not ("b" or "t" or "ctr")||!OnlyAttributes(node,"{}rot","{}spcFirstLastPara","{}vertOverflow",
                    "{}horzOverflow","{}vert","{}wrap","{}lIns","{}tIns","{}rIns","{}bIns","{}numCol","{}spcCol",
                    "{}rtlCol","{}fromWordArt","{}anchor","{}anchorCtr","{}forceAA","{}compatLnSpc")||
                Value(node,"wrap") is not (null or "square")||Value(node,"vert") is not (null or "horz")||
                Value(node,"vertOverflow") is not (null or "overflow")||Value(node,"horzOverflow") is not (null or "overflow")||
                Value(node,"numCol") is not (null or "1")||Value(node,"compatLnSpc") is not (null or "1")||
                new[]{"rot","spcFirstLastPara","spcCol","rtlCol","fromWordArt","anchorCtr","forceAA"}
                    .Any(k=>Value(node,k) is not (null or "0"))||
                !Inset("lIns",7.2f,out float left)||!Inset("rIns",7.2f,out float right)||
                !Inset("tIns",3.6f,out float top)||!Inset("bIns",3.6f,out float bottom)||
                Member(node,D,"noAutofit") is not {} fit||Elements(fit).Any()||
                !Elements(node).All(n=>n.Id==fit.Id||n.Name=="{"+D+"}prstTxWarp"&&Value(n,"prst")=="textNoShape"&&
                    Single(n,D,"avLst",out var adjustments)&&!Elements(adjustments).Any()))return false;
            insets=new(left,top,right,bottom);
            return true;

            bool Inset(string key,float standard,out float value)
            {
                value=standard;
                return Value(node,key) is null||ReadEmu(node,key,out value)&&value>=0;
            }
        }

        bool StrokeWidth(InventoryNode line,InventoryNode? style,out float width)
        {
            if(Value(line,"w") is not null)return ReadEmu(line,"w",out width)&&width is >0 and <=10;
            width=0;
            if(style is null||Member(style,D,"lnRef") is not {} reference||
                !Number(Value(reference,"idx"),1,64,out int index)||
                semantic!.RelatedPart("theme") is not {} uri||
                SemanticIndex.Unique(nodes.Values.Where(n=>n.Source.PartUri==uri&&n.Name=="{"+D+"}lnStyleLst")) is not {} list||
                Elements(list).ElementAtOrDefault(index-1) is not {} themeLine||
                Value(themeLine,"cap") is not (null or "flat")||Value(themeLine,"cmpd") is not (null or "sng")||
                Value(themeLine,"algn") is not (null or "ctr")||
                !Elements(themeLine).All(n=>n.Name=="{"+D+"}solidFill"||n.Name=="{"+D+"}prstDash"&&Value(n,"val")=="solid"))
                return false;
            return ReadEmu(themeLine,"w",out width)&&width is >0 and <=10;
        }

        bool DrawingPaint(InventoryNode node,out uint? color)
        {
            color=null;
            if(Member(node,D,"noFill") is {} none)return !Elements(none).Any()&&Member(node,D,"solidFill") is null;
            if(Member(node,D,"solidFill") is not {} fill||Elements(fill).ToArray() is not [var ink]||Elements(ink).Any())return false;
            string? value=Value(ink,"val");
            if(ink.Name=="{"+D+"}schemeClr"&&ReadDiagramTheme(out var colors,out _))
            {
                var settings=semantic!.RelatedPart("settings");
                var mapping=SemanticIndex.Unique(nodes.Values.Where(n=>n.Source.PartUri==settings&&Is(n,"clrSchemeMapping")));
                string theme=value switch{"tx1"=>"text1","bg1"=>"background1","tx2"=>"text2","bg2"=>"background2",
                    "hlink"=>"hyperlink","folHlink"=>"followedHyperlink",_=>value??""};
                string mapped=mapping is null?"":A(mapping,WordColor.MappingKey(theme));
                value=colors.GetValueOrDefault(WordColor.ThemeSlot(mapped.Length==0?theme:mapped));
            }
            else if(ink.Name!="{"+D+"}srgbClr")return false;
            if(value is not {Length:6}||!uint.TryParse(value,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint rgb))return false;
            color=rgb;
            return true;
        }

        static bool LegacyPaint(InventoryNode node,string enabled,string key,out uint? color)
        {
            color=null;
            if(Value(node,enabled) is "f" or "0" or "false")return true;
            if(Value(node,enabled) is not (null or "t" or "1" or "true"))return false;
            string? value=Value(node,key);
            if(value is "black" or "black [3213]"){color=0;return true;}
            if(value is not {Length:7}||value[0]!='#'||!uint.TryParse(value.AsSpan(1),NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,out uint rgb))return false;
            color=rgb;
            return true;
        }

        bool SameDrawingText(InventoryNode first,InventoryNode second,int depth)
        {
            token.ThrowIfCancellationRequested();
            if(depth>64||first.Name!=second.Name||first.Text!=second.Text||
                !first.Attributes.Where(a=>!a.Key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal))
                    .OrderBy(a=>a.Key,StringComparer.Ordinal).SequenceEqual(second.Attributes
                    .Where(a=>!a.Key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal))
                    .OrderBy(a=>a.Key,StringComparer.Ordinal)))return false;
            var a=children[first.Id].Where(c=>!c.Name.StartsWith('#')||c.Kind=="textValue").ToArray();
            var b=children[second.Id].Where(c=>!c.Name.StartsWith('#')||c.Kind=="textValue").ToArray();
            return a.Length==b.Length&&a.Zip(b).All(p=>SameDrawingText(p.First,p.Second,depth+1));
        }

        bool Axis(InventoryNode node,bool horizontal,out string reference,out string? alignment,out float offset,
            out RelativeDrawingValue? relative)
        {
            reference="";alignment=null;offset=0;relative=null;
            string name=horizontal?"positionH":"positionV";
            if(SemanticIndex.IsAlternative(node))
            {
                if(Elements(node).ToArray() is not [var choice,var fallback]||choice.Name!=McChoice||fallback.Name!=McFallback||
                    Value(choice,"Requires") is not {} prefix||Namespace(choice,prefix)!=WP14||
                    !Single(choice,WP,name,out var current)||!Single(fallback,WP,name,out var old)||
                    Value(old,"relativeFrom")!="page"||!Single(old,WP,"posOffset",out var saved)||
                    !Offset(DrawingValue(saved),out _))return false;
                node=current;
            }
            if(node.Name!="{"+WP+"}"+name||!OnlyAttributes(node,"{}relativeFrom")||
                Value(node,"relativeFrom") is not {} basis||!Reference(basis,horizontal)||
                Elements(node).ToArray() is not [var operand]||!OnlyAttributes(operand)||Elements(operand).Any())return false;
            reference=basis;
            string raw=DrawingValue(operand);
            if(operand.Name=="{"+WP+"}align"&&
                (horizontal?raw is "left" or "right" or "center" or "inside" or "outside":raw is "top" or "bottom" or "center"))
            {alignment=raw;return true;}
            if(operand.Name=="{"+WP+"}posOffset"&&Offset(raw,out long value)){offset=PageComposer.Points(value);return true;}
            if(operand.Name!="{"+WP14+"}"+(horizontal?"pctPosHOffset":"pctPosVOffset")||!Percentage(operand,out var pct))return false;
            relative=new(basis,pct);
            return true;
        }

        static bool Reference(string? basis,bool horizontal)=>basis is "page" or "margin"||
            (horizontal?basis is "leftMargin" or "rightMargin" or "insideMargin" or "outsideMargin":
            basis is "topMargin" or "bottomMargin" or "insideMargin" or "outsideMargin");

        static bool FallbackAxis(Dictionary<string,string> css,bool horizontal,string basis,string? align,float offset,RelativeDrawingValue? percent)
        {
            string axis=horizontal?"horizontal":"vertical";
            if(ReadLegacyReference(css.GetValueOrDefault("mso-position-"+axis+"-relative"),horizontal)!=basis)return false;
            string edge=horizontal?"left":"top";
            if(percent is {} ratio)
                return css.GetValueOrDefault("mso-position-"+axis) is (null or "absolute")&&
                    Length(css.GetValueOrDefault("margin-"+edge)??"0",out float shift)&&shift==0&&LegacyPercent(css.GetValueOrDefault("mso-"+edge+"-percent"),ratio.Fraction);
            return align is not null?css.GetValueOrDefault("mso-position-"+axis)==align:
                css.GetValueOrDefault("mso-position-"+axis) is (null or "absolute")&&
                Length(css.GetValueOrDefault("margin-"+edge)??"0",out float length)&&Near(offset,length);
        }

        static bool FallbackSize(Dictionary<string,string> css,bool horizontal,RelativeDrawingValue? value)
        {
            string dimension=horizontal?"width":"height";
            return value is {} ratio?ReadLegacyReference(css.GetValueOrDefault("mso-"+dimension+"-relative"),horizontal)==ratio.Reference&&
                LegacyPercent(css.GetValueOrDefault("mso-"+dimension+"-percent"),ratio.Fraction):
                css.GetValueOrDefault("mso-"+dimension+"-percent") is (null or "0");
        }

        static string? ReadLegacyReference(string? basis,bool horizontal)=>basis switch
        {
            "left-margin" or "left-margin-area" when horizontal=>"leftMargin",
            "right-margin" or "right-margin-area" when horizontal=>"rightMargin",
            "top-margin" or "top-margin-area" when !horizontal=>"topMargin",
            "bottom-margin" or "bottom-margin-area" when !horizontal=>"bottomMargin",
            "inner-margin-area"=>"insideMargin","outer-margin-area" or "outer-margin area"=>"outsideMargin",
            "page" or "margin"=>basis,_=>null
        };

        static bool LegacyPercent(string? value,float percent)=>Number(value,-10_000,10_000,out int raw)&&
            Math.Abs(raw/1000f-percent)<.00051f;

        bool Percentage(InventoryNode node,out float percent)
        {
            percent=0;
            if(!OnlyAttributes(node)||Elements(node).Any())return false;
            string raw=DrawingValue(node);
            if(raw.EndsWith('%'))
            {
                if(!float.TryParse(raw.AsSpan(0,raw.Length-1),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,out float value)||value is < -1000 or >1000)return false;
                percent=value/100;
                return true;
            }
            if(!Number(raw,-1000000,1000000,out int units))return false;
            percent=units/100000f;
            return true;
        }

        string DrawingValue(InventoryNode node)=>string.Concat(children[node.Id].Where(c=>c.Kind=="textValue").Select(c=>c.Text)).Trim();

        bool ReadDrawingWrap(InventoryNode node,InventoryNode anchor,out RunningDrawingWrap? result)
        {
            result=null;
            if(node.Name=="{"+WP+"}wrapNone")return OnlyAttributes(node)&&!Elements(node).Any();
            bool square=node.Name=="{"+WP+"}wrapSquare";
            if(!square&&node.Name!="{"+WP+"}wrapTopAndBottom")return false;
            string side=Value(node,"wrapText")??"bothSides";
            if(!OnlyAttributes(node,square?["{}wrapText","{}distL","{}distR","{}distT","{}distB"]:["{}distT","{}distB"])||
                Elements(node).Any()||side is not ("bothSides" or "left" or "right" or "largest")||
                !Distance("distL",out float l)||!Distance("distT",out float t)||
                !Distance("distR",out float r)||!Distance("distB",out float b))return false;
            result=new(square?"square":"topAndBottom",side,new(l,t,r,b));
            return true;

            bool Distance(string name,out float length)
            {
                length=0;
                if(!Offset(Value(node,name)??Value(anchor,name)??"0",out long raw)||raw is <0 or >1_828_800)return false;
                length=PageComposer.Points(raw);
                return true;
            }
        }

        bool CompareLegacyWrap(InventoryNode node,Dictionary<string,string> css,RunningDrawingWrap? wrap)
        {
            var legacyWrap=Elements(node).LastOrDefault(n=>n.Name==VmlWord+"wrap");
            if(legacyWrap is null)return wrap is null&&css.GetValueOrDefault("mso-wrap-style") is (null or "none");
            if(Elements(node).Last()!=legacyWrap||!OnlyAttributes(legacyWrap,"{}type","{}side","{}anchorx","{}anchory")||Elements(legacyWrap).Any())return false;
            if(wrap is null)return Value(legacyWrap,"type") is (null or "none");
            string side=Value(legacyWrap,"side")??"both";
            if(Value(legacyWrap,"type")!=(wrap.Kind=="square"?"square":"topAndBottom")||
                (side=="both"?"bothSides":side)!=wrap.Side)return false;
            return Match("left",wrap.Distances.Left)&&Match("top",wrap.Distances.Top)&&
                Match("right",wrap.Distances.Right)&&Match("bottom",wrap.Distances.Bottom);

            bool Match(string edge,float expected)=>Length(css.GetValueOrDefault("mso-wrap-distance-"+edge)??"0",out float actual)&&Near(actual,expected);
        }

        static bool Effects(InventoryNode node,out ImageInsets rectangle)
        {
            rectangle=default;
            if(node.Name!="{"+WP+"}effectExtent"||!OnlyAttributes(node,"{}l","{}t","{}r","{}b")||
                !ReadEmu(node,"l",out float l)||!ReadEmu(node,"t",out float t)||!ReadEmu(node,"r",out float r)||
                !ReadEmu(node,"b",out float b)||new[]{l,t,r,b}.Any(value=>value is <0 or >10))return false;
            rectangle=new(l,t,r,b);
            return true;
        }

        static bool FallbackBox(InventoryNode node,(float X,float Y)? lattice,PageRect box)
        {
            if(!DrawingStyle(node,out var css))return false;
            if(lattice is not {} scale)return true;
            return Coordinate(css.GetValueOrDefault("left")??"0",out int x)&&Coordinate(css.GetValueOrDefault("top")??"0",out int y)&&
                Coordinate(css.GetValueOrDefault("width"),out int w)&&Coordinate(css.GetValueOrDefault("height"),out int h)&&
                Near(box.Left,x*scale.X,scale.X)&&Near(box.Top,y*scale.Y,scale.Y)&&
                Near(box.Width,w*scale.X,scale.X)&&Near(box.Height,h*scale.Y,scale.Y);
        }

        static bool DrawingStyle(InventoryNode node,out Dictionary<string,string> values)
        {
            values=new(StringComparer.Ordinal);
            if(Value(node,"style") is not {Length:>0 and <=2048} raw)return false;
            foreach(string entry in raw.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
            {
                string[] pair=entry.Split(':',2,StringSplitOptions.TrimEntries);
                if(pair is not [var key,var value]||key is not ("position" or "left" or "top" or "text-align" or "margin-left" or
                    "margin-top" or "width" or "height" or "z-index" or "visibility" or "mso-wrap-style" or "v-text-anchor" or
                    "mso-width-percent" or "mso-height-percent" or "mso-left-percent" or "mso-top-percent" or "mso-width-relative" or
                    "mso-height-relative" or "mso-position-horizontal" or "mso-position-vertical" or
                    "mso-position-horizontal-relative" or "mso-position-vertical-relative" or "mso-wrap-distance-left" or
                    "mso-wrap-distance-right" or "mso-wrap-distance-top" or "mso-wrap-distance-bottom")||
                    !values.TryAdd(key,value)&&values[key]!=value)return false;
            }
            return values.GetValueOrDefault("position")=="absolute"&&values.GetValueOrDefault("visibility") is (null or "visible");
        }

        bool Single(InventoryNode node,string ns,string name,out InventoryNode child)
        {
            if(Elements(node).Take(2).ToArray() is [var item]&&item.Name=="{"+ns+"}"+name){child=item;return true;}
            child=null!;
            return false;
        }

        static bool Near(float a,float b,float rounding=.01f)=>Math.Abs(a-b)<=rounding+.0001f;
    }
}
