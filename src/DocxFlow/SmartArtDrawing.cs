using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

internal sealed record DiagramTextLine(float X,float Baseline,TextFragment Fragment);
internal sealed record DiagramShape(SourceLocation Source,PageRect Box,string Geometry,float FirstAdjustment,
    float SecondAdjustment,uint? Fill,FrameStroke? Stroke,PageRect? TextBox,ImmutableArray<DiagramTextLine> Text)
{
    internal SourceLocation? LayoutSource {get;init;}
    internal float Rotation {get;init;}
    internal ImmutableArray<Vector2> Outline {get;init;}=[];
}
internal sealed record DiagramDrawing(ImmutableArray<DiagramShape> Shapes)
{
    internal string LayoutProfile {get;init;}="cached";
    internal ImmutableArray<DiagramConnector> Connectors {get;init;}=[];
    internal ImmutableArray<DiagramGroup> Groups {get;init;}=[];
    internal DiagramModel? Model {get;init;}
}

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        TypographySession? diagramTypography;
        Action? checkDiagramBudget;
        int diagramGlyphs;

        internal void UseDiagramTypography(TypographySession session,Action check)
        {
            diagramTypography=session;
            checkDiagramBudget=check;
        }

        InlineImage? ReadDiagramDrawing(InventoryNode drawing,InventoryNode inline,SmartArtPresentation presentation)
        {
            var parts=Elements(inline).Select(n=>n.Name).ToArray();
            string[] ordered=["{"+WP+"}extent","{"+WP+"}effectExtent","{"+WP+"}docPr",
                "{"+WP+"}cNvGraphicFramePr","{"+D+"}graphic"];
            if(diagramTypography is null||inline.Name!="{"+WP+"}inline"||
                !OnlyAttributes(inline,"{}distT","{}distB","{}distL","{}distR","{"+WP14+"}anchorId","{"+WP14+"}editId")||
                inline.Attributes.Any(a=>a.Key.StartsWith("{}dist",StringComparison.Ordinal)&&a.Value!="0")||
                !parts.SequenceEqual(ordered.Where(n=>parts.Contains(n,StringComparer.Ordinal)))||
                Member(inline,WP,"extent") is not {} extent||
                !OnlyAttributes(extent,"{}cx","{}cy")||Elements(extent).Any()||
                !ReadEmu(extent,"cx",out float width)||!ReadEmu(extent,"cy",out float height)||width<=0||height<=0||
                Member(inline,WP,"docPr") is not {} info||!DiagramCreationMetadata(info)||
                Member(presentation.Drawing,DiagramDrawingNamespace,"spTree") is not {} tree||
                Member(tree,DiagramDrawingNamespace,"grpSpPr") is not {} rootProperties||
                Elements(rootProperties).Any()||presentation.Shapes.Count>128)
                return null;
            ImageInsets effects=default;
            if(Member(inline,WP,"effectExtent") is {} effect)
            {
                if(!OnlyAttributes(effect,"{}l","{}t","{}r","{}b")||Elements(effect).Any()||
                    !ReadEmu(effect,"l",out float effectLeft)||!ReadEmu(effect,"t",out float effectTop)||
                    !ReadEmu(effect,"r",out float effectRight)||!ReadEmu(effect,"b",out float effectBottom)||
                    effectLeft is <0 or >12||effectTop is <0 or >12||effectRight is <0 or >12||effectBottom is <0 or >12)
                    return null;
                effects=new(effectLeft,effectTop,effectRight,effectBottom);
            }
            if(Elements(tree).Any(n=>n.Name is not ("{"+DiagramDrawingNamespace+"}nvGrpSpPr" or
                "{"+DiagramDrawingNamespace+"}grpSpPr" or "{"+DiagramDrawingNamespace+"}sp" or "{"+DiagramDrawingNamespace+"}grpSp")))
                return null;
            if(!ReadDiagramTheme(out var themeColors,out var fonts))return null;
            var shapes=ImmutableArray.CreateBuilder<DiagramShape>();
            foreach(var shape in presentation.Shapes)
            {
                Check();
                if(ReadShape(shape) is not {} placed)
                {
                    Issue("LAYOUT_SMARTART_SHAPE_UNQUALIFIED",shape.Drawing,
                        "Forme, couleur, géométrie ou libellé du dessin SmartArt enregistré hors profil.");
                    return null;
                }
                shapes.Add(placed);
            }
            return new(drawing.Id,drawing.Source,width,height,0,0,semantic!.SnapshotId)
                {Diagram=new(shapes.ToImmutable()),Effects=effects};

            DiagramShape? ReadShape(SmartArtShape source)
            {
                var node=source.Drawing;
                if(!Elements(node).All(n=>n.Name.StartsWith("{"+DiagramDrawingNamespace+"}",StringComparison.Ordinal)&&
                    n.Name.Split('}')[1] is "nvSpPr" or "spPr" or "style" or "txBody" or "txXfrm")||
                    Member(node,DiagramDrawingNamespace,"spPr") is not {} properties||
                    !Elements(properties).All(n=>n.Name.StartsWith("{"+D+"}",StringComparison.Ordinal)&&
                        n.Name.Split('}')[1] is "xfrm" or "prstGeom" or "solidFill" or "noFill" or "ln" or "effectLst")||
                    Member(properties,D,"effectLst") is {} effects&&Elements(effects).Any()||
                    Member(properties,D,"xfrm") is not {} xfrm||!ReadBox(xfrm,out var native)||
                    Member(properties,D,"prstGeom") is not {} preset||Value(preset,"prst") is not {} geometry||
                    !ReadAdjustments(preset,geometry,out float first,out float second)||
                    !ReadPaint(properties,out uint? fill)||!ReadStroke(properties,out var stroke)||
                    !ReadGroupPlacement(node,tree,out var transform))
                    return null;
                var box=transform.Map(native);
                if(!Inside(box,width,height)||stroke is {} pen&&transform.XScale!=transform.YScale)return null;
                if(stroke is {} outline)stroke=outline with{Width=outline.Width*transform.XScale};
                PageRect? textBox=null;
                ImmutableArray<DiagramTextLine> text=[];
                if(source.TextLayout is not null)
                {
                    if(Member(node,DiagramDrawingNamespace,"txBody") is not {} textBody||
                        Member(node,DiagramDrawingNamespace,"txXfrm") is not {} tx||
                        !ReadBox(tx,out var nativeTextBox)||Member(node,DiagramDrawingNamespace,"style") is not {} style||
                        Member(style,D,"fontRef") is not {} fontRef||
                        !ReadLabel(source,textBody,fontRef,nativeTextBox,out var labelBox,out text))
                        return null;
                    if(transform.XScale!=1||transform.YScale!=1)return null;
                    textBox=transform.Map(labelBox);
                    if(!Inside(textBox.Value,width,height))return null;
                    text=[..text.Select(line=>line with{X=line.X+transform.Left,Baseline=line.Baseline+transform.Top})];
                }
                else if(Member(node,DiagramDrawingNamespace,"txBody") is {} emptyText&&
                    Descendants(emptyText).Any(n=>n.Name is "{"+D+"}r" or "{"+D+"}fld" or "{"+D+"}br"))
                    return null;
                return new(node.Source,box,geometry,first,second,fill,stroke,textBox,text);
            }

            bool ReadLabel(SmartArtShape shape,InventoryNode text,InventoryNode fontRef,PageRect box,out PageRect inner,
                out ImmutableArray<DiagramTextLine> lines)
            {
                inner=default;
                lines=[];
                if(Member(text,D,"bodyPr") is not {} body||
                    !OnlyAttributes(body,"{}spcFirstLastPara","{}vert","{}wrap","{}lIns","{}tIns","{}rIns","{}bIns",
                        "{}numCol","{}spcCol","{}anchor","{}anchorCtr")||
                    Value(body,"vert") is not (null or "horz")||Value(body,"wrap") is not (null or "square")||
                    Value(body,"numCol") is not (null or "1")||Value(body,"anchorCtr") is not (null or "0")||
                    Value(body,"spcFirstLastPara") is not (null or "0")||
                    Elements(body).Take(2).ToArray() is not [var fitting]||fitting.Name!="{"+D+"}noAutofit"||
                    Value(body,"anchor") is not (null or "t" or "ctr" or "b")||
                    Elements(text).Where(n=>n.Name=="{"+D+"}p").Take(2).ToArray() is not [var paragraph]||
                    !Elements(text).All(n=>n.Id==paragraph.Id||n.Id==body.Id||n.Name=="{"+D+"}lstStyle"&&!Elements(n).Any())||
                    Member(paragraph,D,"pPr") is not {} pp||
                    !OnlyAttributes(pp,"{}marL","{}marR","{}lvl","{}indent","{}algn","{}defTabSz")||
                    Value(pp,"lvl") is not (null or "0")||Value(pp,"indent") is not (null or "0")||
                    Value(pp,"marL") is not (null or "0")||Value(pp,"marR") is not (null or "0")||
                    Value(pp,"algn") is not (null or "l" or "ctr" or "r")||
                    !Elements(pp).All(n=>n.Name is "{"+D+"}lnSpc" or "{"+D+"}spcBef" or "{"+D+"}spcAft" or "{"+D+"}buNone")||
                    !ReadPercent(pp,"lnSpc",100_000,out float lineSpacing)||lineSpacing<=0||
                    !ReadPercent(pp,"spcBef",0,out float before)||!ReadPercent(pp,"spcAft",0,out float after)||
                    Elements(paragraph).Where(n=>n.Name=="{"+D+"}r").Take(2).ToArray() is not [var run]||
                    !Elements(paragraph).All(n=>n.Id==pp.Id||n.Id==run.Id||n.Name=="{"+D+"}endParaRPr")||
                    Member(run,D,"rPr") is not {} rp||Member(run,D,"t") is not {} content||Elements(content).Any()||
                    Elements(run).Count()!=2||
                    !OnlyAttributes(rp,"{}lang","{}sz","{}b","{}i","{}kern","{}dirty")||
                    !ValidFlag(rp,"b")||!ValidFlag(rp,"i")||!ValidFlag(rp,"dirty")||
                    !Number(Value(rp,"sz"),100,9600,out int size)||
                    !Number(Value(rp,"kern")??"0",0,40000,out int kern))
                    return false;
                string value=DiagramText(content);
                var semanticText=Member(shape.ContentPoint,"http://schemas.openxmlformats.org/drawingml/2006/diagram","t");
                if(value.Length is 0 or >1024||semanticText is null||
                    string.Concat(Descendants(semanticText).Where(n=>n.Name=="{"+D+"}t").Select(DiagramText))!=value)
                    return false;
                string language=Value(rp,"lang")??"und";
                if(language.Length>24||language.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-')||
                    TextUnicode.Instance.Levels(value,rightToLeft:false).Any(level=>level%2!=0)||
                    value.EnumerateRunes().Any(r=>TextUnicode.Instance.Script(r.Value) is not ("Latn" or "Zyyy" or "Zinh")))
                    return false;
                if(Elements(rp).Any(n=>n.Name is not ("{"+D+"}latin" or "{"+D+"}solidFill")))return false;
                var explicitFamily=Member(rp,D,"latin");
                string? family=explicitFamily is null?fonts.GetValueOrDefault(Value(fontRef,"idx")??""):
                    Value(explicitFamily,"typeface");
                if(family is not {Length:>0 and <=128})return false;
                var alpha=Member(rp,D,"solidFill")??fontRef;
                if(Elements(alpha).Take(2).ToArray() is not [var pigment]||!ReadColor(pigment,out uint color))return false;
                if(!Inset(body,"lIns",out float left)||!Inset(body,"tIns",out float top)||
                    !Inset(body,"rIns",out float right)||!Inset(body,"bIns",out float bottom))return false;
                inner=new(box.Left+left,box.Top+top,box.Width-left-right,box.Height-top-bottom);
                if(inner.Empty)return false;
                var request=new FontRequest(family,false,explicitFamily?.Source??fontRef.Source,"typeface");
                var style=new TextStyle {Language=language,Size=size/100f,Bold=Bool(rp,"b",true),Italic=Bool(rp,"i",true),
                    KernAt=kern/100f,Color=(color&0xffffff).ToString("X6",CultureInfo.InvariantCulture),
                    Slots=new(request,request)};
                if((color>>24)!=255)return false;
                return WrapLabel(value,style,semantic!.Source(content.Id),inner,Value(pp,"algn")??"l",
                    Value(body,"anchor")??"t",lineSpacing,before,after,out lines);
            }

            bool ReadStroke(InventoryNode parent,out FrameStroke? stroke)
            {
                stroke=null;
                if(Member(parent,D,"ln") is not {} line)return false;
                if(!OnlyAttributes(line,"{}w","{}cap","{}cmpd","{}algn")||
                    Value(line,"cap") is not (null or "flat")||Value(line,"cmpd") is not (null or "sng")||
                    Value(line,"algn") is not (null or "ctr")||
                    !Elements(line).All(n=>n.Name is "{"+D+"}solidFill" or "{"+D+"}noFill"||
                        n.Name=="{"+D+"}prstDash"&&Value(n,"val")=="solid"&&!Elements(n).Any())||
                    !ReadPaint(line,out uint? rgb))return false;
                if(rgb is not {} color)return true;
                if(!ReadEmu(line,"w",out float weight)||weight is <=0 or >12||(color>>24)!=255)return false;
                stroke=new(color&0xffffff,weight);
                return true;
            }

            bool ReadPaint(InventoryNode parent,out uint? fill)
            {
                fill=null;
                var children=Elements(parent).Where(n=>n.Name is "{"+D+"}noFill" or "{"+D+"}solidFill").Take(2).ToArray();
                if(children is not [var paint]||!OnlyAttributes(paint))return false;
                if(paint.Name=="{"+D+"}noFill")return !Elements(paint).Any();
                if(Elements(paint).Take(2).ToArray() is not [var color]||!ReadColor(color,out uint rgb))return false;
                fill=rgb;
                return true;
            }

            bool ReadColor(InventoryNode color,out uint result)
            {
                result=0;
                string? raw=Value(color,"val");
                if(color.Name=="{"+D+"}schemeClr")raw=themeColors.GetValueOrDefault(raw??"");
                else if(color.Name!="{"+D+"}srgbClr")return false;
                if(raw is not {Length:6}||!uint.TryParse(raw,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint rgb)||
                    !OnlyAttributes(color,"{}val"))return false;
                var offsets=Elements(color).ToArray();
                string[] names=["hueOff","satOff","lumOff","alphaOff"];
                if(offsets.Length!=0&&!offsets.Select(n=>n.Name).SequenceEqual(names.Select(n=>"{"+D+"}"+n)))return false;
                var values=new int[4];
                for(int i=0;i<offsets.Length;i++)
                    if(!OnlyAttributes(offsets[i],"{}val")||Elements(offsets[i]).Any()||
                        !Number(Value(offsets[i],"val"),i==0?-21_600_000:-100_000,i==0?21_600_000:100_000,out values[i]))
                        return false;
                if(values.All(v=>v==0))
                {
                    result=0xff000000|rgb;
                    return true;
                }
                var sk=new SKColor(0xff000000|rgb);
                sk.ToHsl(out float hue,out float saturation,out float lightness);
                hue=(hue+values[0]/60_000f+360)%360;
                var adjusted=SKColor.FromHsl(hue,Math.Clamp(saturation+values[1]/1000f,0,100),
                    Math.Clamp(lightness+values[2]/1000f,0,100),(byte)Math.Round(Math.Clamp(1+values[3]/100_000d,0,1)*255));
                result=(uint)adjusted;
                return true;
            }

            void Check()
            {
                token.ThrowIfCancellationRequested();
                checkDiagramBudget?.Invoke();
            }
        }

        bool WrapLabel(string value,TextStyle style,SemanticSource origin,PageRect inner,string align,string anchor,
            float spacing,float before,float after,out ImmutableArray<DiagramTextLine> lines)
        {
            lines=[];
            if(diagramTypography is not {} typography)return false;
            void Check(){token.ThrowIfCancellationRequested();checkDiagramBudget?.Invoke();}
            var boundaries=TextUnicode.Instance.Boundaries(value,false,style.Language,Check);
            var graphemes=StringInfo.ParseCombiningCharacters(value);
            var measured=new List<TextFragment>();
            int start=0,boundary=0;
            while(start<value.Length)
            {
                TextFragment? chosen=null;
                int end=start;
                while(boundary<boundaries.Count)
                {
                    int next=boundaries[boundary].Offset;
                    string candidate=value[start..next].Trim(' ');
                    if(candidate.Length==0){end=next;boundary++;continue;}
                    var fragment=Measure(candidate);
                    if(fragment is null)break;
                    chosen=fragment;end=next;boundary++;
                    if(boundaries[boundary-1].Mandatory)break;
                }
                if(chosen is null&&end==value.Length)break;
                if(chosen is null&&boundary<boundaries.Count)
                {
                    // A single word wider than the text rectangle can break at a grapheme boundary.
                    int nextWord=boundaries[boundary].Offset;
                    foreach(int next in graphemes)
                    {
                        if(next<=start||next>=nextWord)continue;
                        string candidate=value[start..next].Trim(' ');
                        if(candidate.Length==0)continue;
                        if(Measure(candidate) is not {} fragment)break;
                        chosen=fragment;
                        end=next;
                    }
                }
                if(chosen is null||end<=start||measured.Count>=128)return false;
                measured.Add(chosen);
                start=end;
            }
            if(measured.Count==0)return false;
            var offsets=new List<float>(measured.Count);
            float y=style.Size*before;
            foreach(var fragment in measured)
            {
                var m=fragment.Measure!.Metrics;
                float ascent=Points(m.Ascent);
                offsets.Add(y+ascent);
                y+=Points(m.Height)*spacing;
            }
            var last=measured[^1].Measure!.Metrics;
            y=Math.Max(y,offsets[^1]+Points(last.Descent))+style.Size*after;
            float remaining=inner.Height-y;
            if(remaining<-.01f)return false;
            float shift=anchor=="ctr"?remaining/2:anchor=="b"?remaining:0;
            lines=[..measured.Select((fragment,i)=>
            {
                float space=inner.Width-Points(fragment.Measure!.Metrics.Width);
                return new DiagramTextLine(inner.Left+(align=="ctr"?space/2:align=="r"?space:0),
                    inner.Top+shift+offsets[i],fragment);
            })];
            return true;

            TextFragment? Measure(string candidate)
            {
                var fragment=typography.ShapeLineDecoration(origin.NodeId,candidate,style,origin,0,Check,count=>
                {
                    diagramGlyphs+=count;
                    if(diagramGlyphs>32_768)throw new InvalidDataException("LAYOUT_SMARTART_GLYPH_LIMIT");
                });
                return fragment?.Measure is {} size&&Points(size.Metrics.Width)<=inner.Width+.01f?fragment:null;
            }
        }

        const string DiagramDrawingNamespace="http://schemas.microsoft.com/office/drawing/2008/diagram";

        bool DiagramCreationMetadata(InventoryNode info)
        {
            var metadata=Elements(info).ToArray();
            if(metadata.Length==0)return true;
            return metadata is [var list]&&list.Name=="{"+D+"}extLst"&&OnlyAttributes(list)&&
                Elements(list).Take(2).ToArray() is [var extension]&&extension.Name=="{"+D+"}ext"&&
                OnlyAttributes(extension,"{}uri")&&Guid.TryParse(Value(extension,"uri"),out var uri)&&uri==CreationExtension&&
                Elements(extension).Take(2).ToArray() is [var id]&&id.Name=="{"+A16+"}creationId"&&
                OnlyAttributes(id,"{}id")&&!Elements(id).Any()&&Guid.TryParse(Value(id,"id"),out _);
        }

        bool ReadDiagramTheme(out Dictionary<string,string> colors,out Dictionary<string,string> fonts)
        {
            colors=new(StringComparer.Ordinal);
            fonts=new(StringComparer.Ordinal);
            if(semantic!.RelatedPart("theme") is not {} part||
                SemanticIndex.Unique(nodes.Values.Where(n=>n.Source.PartUri==part&&n.Name=="{"+D+"}theme")) is not {} theme||
                Member(theme,D,"themeElements") is not {} elements||Member(elements,D,"clrScheme") is not {} colorsNode||
                Member(elements,D,"fontScheme") is not {} fontsNode)
                return false;
            foreach(var slot in Elements(colorsNode))
            {
                if(Elements(slot).Take(2).ToArray() is not [var color])return false;
                var rgb=color.Name switch{"{"+D+"}srgbClr"=>Value(color,"val"),"{"+D+"}sysClr"=>Value(color,"lastClr"),_=>null};
                if(rgb is not {Length:6}||!colors.TryAdd(slot.Name.Split('}')[1],rgb))return false;
            }
            foreach(string name in new[]{"minor","major"})
                if(Member(fontsNode,D,name+"Font") is {} font&&Member(font,D,"latin") is {} latin&&Value(latin,"typeface") is {} face)
                    fonts.Add(name,face);
            return true;
        }

        readonly record struct DiagramTransform(float Left,float Top,float XScale,float YScale)
        {
            internal PageRect Map(PageRect r)=>new(Left+r.Left*XScale,Top+r.Top*YScale,r.Width*XScale,r.Height*YScale);
        }

        bool ReadGroupPlacement(InventoryNode shape,InventoryNode tree,out DiagramTransform transform)
        {
            transform=new(0,0,1,1);
            for(string? id=shape.ParentId;id!=tree.Id;id=id is null?null:nodes[id].ParentId)
            {
                token.ThrowIfCancellationRequested();
                if(id is null||nodes[id] is not {} group||group.Name!="{"+DiagramDrawingNamespace+"}grpSp"||
                    Elements(group).Any(n=>n.Name is not ("{"+DiagramDrawingNamespace+"}nvGrpSpPr" or
                        "{"+DiagramDrawingNamespace+"}grpSpPr" or "{"+DiagramDrawingNamespace+"}sp" or "{"+DiagramDrawingNamespace+"}grpSp"))||
                    Member(group,DiagramDrawingNamespace,"grpSpPr") is not {} props||
                    Elements(props).Take(2).ToArray() is not [var xfrm]||xfrm.Name!="{"+D+"}xfrm"||
                    !ReadBox(xfrm,out var target,true)||
                    Member(xfrm,D,"chOff") is not {} childOrigin||Member(xfrm,D,"chExt") is not {} childSize||
                    !ReadEmu(childOrigin,"x",out float x)||!ReadEmu(childOrigin,"y",out float y)||
                    !ReadEmu(childSize,"cx",out float cx)||!ReadEmu(childSize,"cy",out float cy)||cx<=0||cy<=0)
                    return false;
                float sx=target.Width/cx,sy=target.Height/cy;
                transform=new(target.Left+(transform.Left-x)*sx,target.Top+(transform.Top-y)*sy,
                    transform.XScale*sx,transform.YScale*sy);
                if(transform.XScale is <.01f or >100||transform.YScale is <.01f or >100)return false;
            }
            return true;
        }

        bool ReadBox(InventoryNode node,out PageRect box,bool group=false)
        {
            box=default;
            if(!OnlyAttributes(node)||!Elements(node).Select(n=>n.Name).SequenceEqual(
                    (group?new[]{"off","ext","chOff","chExt"}:new[]{"off","ext"}).Select(n=>"{"+D+"}"+n))||
                Member(node,D,"off") is not {} origin||Member(node,D,"ext") is not {} extent||
                !OnlyAttributes(origin,"{}x","{}y")||!OnlyAttributes(extent,"{}cx","{}cy")||
                !ReadEmu(origin,"x",out float x)||!ReadEmu(origin,"y",out float y)||
                !ReadEmu(extent,"cx",out float w)||!ReadEmu(extent,"cy",out float h)||w<=0||h<=0)
                return false;
            box=new(x,y,w,h);
            return true;
        }

        bool ReadAdjustments(InventoryNode node,string geometry,out float first,out float second)
        {
            first=geometry=="roundRect"?16667:50_000;
            second=50_000;
            if(geometry is not ("rect" or "roundRect" or "rightArrow")||
                Member(node,D,"avLst") is not {} list||Elements(node).Count()!=1)return false;
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var guide in Elements(list))
            {
                string? name=Value(guide,"name"),formula=Value(guide,"fmla");
                if(guide.Name!="{"+D+"}gd"||!OnlyAttributes(guide,"{}name","{}fmla")||Elements(guide).Any()||
                    name is null||!seen.Add(name)||formula is null||!formula.StartsWith("val ",StringComparison.Ordinal)||
                    !Number(formula[4..],0,500_000,out int value))return false;
                if(geometry=="roundRect"&&name=="adj"||geometry=="rightArrow"&&name=="adj1")first=value;
                else if(geometry=="rightArrow"&&name=="adj2")second=value;
                else return false;
            }
            return true;
        }

        bool ReadPercent(InventoryNode paragraph,string key,int fallback,out float percent)
        {
            percent=fallback/100_000f;
            if(Member(paragraph,D,key) is not {} node)return true;
            if(Elements(node).Take(2).ToArray() is not [var spacing]||spacing.Name!="{"+D+"}spcPct"||
                !OnlyAttributes(spacing,"{}val")||Elements(spacing).Any()||
                !Number(Value(spacing,"val"),0,200_000,out int factor))return false;
            percent=factor/100_000f;
            return true;
        }

        static bool ReadEmu(InventoryNode node,string attribute,out float points)
        {
            points=0;
            if(!Offset(Value(node,attribute),out long amount))return false;
            points=PageComposer.Points(amount);
            return true;
        }

        static bool Inset(InventoryNode node,string attribute,out float points)
        {
            points=0;
            return Value(node,attribute) is null||ReadEmu(node,attribute,out points)&&points>=0;
        }

        static bool Number(string? raw,int min,int max,out int value)=>
            int.TryParse(raw,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value)&&value>=min&&value<=max;

        string DiagramText(InventoryNode content)=>string.Concat(children[content.Id].Where(n=>n.Kind=="textValue").Select(n=>n.Text));

        static bool Inside(PageRect box,float width,float height)=>!box.Empty&&box.Left>=-.01f&&box.Top>=-.01f&&
            box.Left+box.Width<=width+.01f&&box.Top+box.Height<=height+.01f;

        static float Points(long emu)=>PageComposer.Points(emu);
    }
}

public static partial class Composer
{
    static void DrawDiagram(SKCanvas canvas,DiagramDrawing diagram,float x,float y,LineComposer painter)
    {
        int save=canvas.Save();
        try
        {
            canvas.Translate(x,y);
            foreach(var connector in diagram.Connectors)DrawConnector(canvas,connector);
            foreach(var shape in diagram.Shapes)
            {
                if(shape.Rotation==0)DrawDiagramShape(canvas,shape,painter);
                else
                {
                    int shapeSave=canvas.Save();
                    try
                    {
                        canvas.RotateDegrees(shape.Rotation,shape.Box.Left+shape.Box.Width/2,shape.Box.Top+shape.Box.Height/2);
                        DrawDiagramShape(canvas,shape,painter);
                    }
                    finally{canvas.RestoreToCount(shapeSave);}
                }
            }
        }
        finally{canvas.RestoreToCount(save);}
    }

    static void DrawDiagramShape(SKCanvas canvas,DiagramShape shape,LineComposer painter)
    {
        var box=shape.Box;
        using var path=new SKPath();
        var rect=new SKRect(box.Left,box.Top,box.Left+box.Width,box.Top+box.Height);
        if(!shape.Outline.IsDefaultOrEmpty)
        {
            path.MoveTo(shape.Outline[0].X,shape.Outline[0].Y);
            foreach(var point in shape.Outline.Skip(1))path.LineTo(point.X,point.Y);
            path.Close();
        }
        else if(shape.Geometry=="ellipse")path.AddOval(rect);
        else if(shape.Geometry=="roundRect")
        {
            float radius=Math.Min(box.Width,box.Height)*Math.Clamp(shape.FirstAdjustment,0,50_000)/100_000;
            path.AddRoundRect(rect,radius,radius);
        }
        else if(shape.Geometry=="rightArrow")
        {
            float shoulder=rect.Right-Math.Min(box.Width,Math.Min(box.Width,box.Height)*shape.SecondAdjustment/100_000);
            float pad=box.Height*(1-Math.Clamp(shape.FirstAdjustment,0,100_000)/100_000)/2;
            path.MoveTo(rect.Left,rect.Top+pad);
            path.LineTo(shoulder,rect.Top+pad);
            path.LineTo(shoulder,rect.Top);
            path.LineTo(rect.Right,rect.MidY);
            path.LineTo(shoulder,rect.Bottom);
            path.LineTo(shoulder,rect.Bottom-pad);
            path.LineTo(rect.Left,rect.Bottom-pad);
            path.Close();
        }
        else path.AddRect(rect);
        using var paint=new SKPaint{IsAntialias=true};
        if(shape.Fill is {} fill)
        {
            paint.Color=new(fill);
            canvas.DrawPath(path,paint);
        }
        if(shape.Stroke is {} stroke)
        {
            paint.IsStroke=true;
            paint.StrokeWidth=stroke.Width;
            paint.Color=new(0xff000000|stroke.Color);
            canvas.DrawPath(path,paint);
        }
        if(shape.TextBox is not {} content)return;
        int textSave=canvas.Save();
        try
        {
            canvas.ClipRect(new(content.Left,content.Top,content.Left+content.Width,content.Top+content.Height));
            foreach(var line in shape.Text)painter.PaintDiagramLabel(canvas,line);
        }
        finally{canvas.RestoreToCount(textSave);}
    }

    static void DrawConnector(SKCanvas canvas,DiagramConnector connector)
    {
        var points=connector.Points;
        var start=points[0];
        var end=points[^1];
        using var brush=new SKPaint{Color=new(0xff000000|connector.Stroke.Color),IsStroke=true,IsAntialias=true,
            StrokeWidth=connector.Stroke.Width};
        using var path=new SKPath();
        path.MoveTo(start.X,start.Y);
        if(connector.Route=="curve")path.CubicTo(points[1].X,points[1].Y,points[2].X,points[2].Y,end.X,end.Y);
        else foreach(var point in points.Skip(1))path.LineTo(point.X,point.Y);
        canvas.DrawPath(path,brush);
        brush.IsStroke=false;
        if(connector.ArrowAtStart)Arrow(start,points.Skip(1).First(p=>p!=start));
        if(connector.ArrowAtEnd)Arrow(end,points.Reverse().Skip(1).First(p=>p!=end));

        void Arrow(Vector2 tip,Vector2 from)
        {
            var direction=Vector2.Normalize(tip-from);
            float length=Math.Min(connector.Stroke.Width*4,Vector2.Distance(tip,from));
            var basePoint=tip-direction*length;
            var normal=new Vector2(-direction.Y,direction.X)*length*.45f;
            path.Reset();
            path.AddPoly([new(tip.X,tip.Y),new(basePoint.X+normal.X,basePoint.Y+normal.Y),
                new(basePoint.X-normal.X,basePoint.Y-normal.Y)],true);
            canvas.DrawPath(path,brush);
        }
    }
}
