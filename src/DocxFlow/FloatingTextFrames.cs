using System.Collections.Immutable;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal sealed record FloatingTextFrame(string Id,SourceLocation Drawing,string StoryId,
    float X,float Y,float Width,float Height,float Left,float Top,float Right,float Bottom,bool Center,uint Order)
{
    internal PageGeometry Geometry=>Rotation switch
    {
        90=>new(Height,Width,Top,Right,Bottom,Left),
        -90=>new(Height,Width,Bottom,Left,Top,Right),
        _=>new(Width,Height,Left,Top,Right,Bottom)
    };
    internal string HorizontalReference {get;init;}="page";
    internal string VerticalReference {get;init;}="page";
    internal FrameSquareWrap? Square {get;init;}
    internal bool AutoFit {get;init;}
    internal bool WrapText {get;init;}=true;
    internal int Rotation {get;init;}
    internal uint? Fill {get;init;}
    internal FrameStroke? Stroke {get;init;}
    internal bool BehindText {get;init;}
    internal string? HorizontalAlignment {get;init;}
    internal string? VerticalAlignment {get;init;}
    internal SourceLocation? VmlSource {get;init;}
    internal SideExclusion? Exclusion=>Square?.At(X,Y,Width,Height);
    internal (float X,float Y) PageOrigin(PageGeometry page,float paragraphTop)
    {
        float left=HorizontalReference=="page"?0:page.Left;
        float top=VerticalReference switch{"margin"=>page.Top,"paragraph"=>paragraphTop,_=>0};
        float width=HorizontalReference=="page"?page.Width:page.Width-page.Left-page.Right;
        float height=VerticalReference=="margin"?page.Height-page.Top-page.Bottom:page.Height;
        return (X+left+(HorizontalAlignment switch{"center"=>(width-Width)/2,"right"=>width-Width,_=>0}),
            Y+top+(VerticalAlignment switch{"center"=>(height-Height)/2,"bottom"=>height-Height,_=>0}));
    }
}
internal readonly record struct FrameStroke(uint Color,float Width);
internal sealed record FrameSquareWrap(string Side,long Left,long Right,long Bottom,long EffectRight,long EffectBottom,bool AllowOverlap)
{
    internal SideExclusion At(float x,float y,float width,float height)
    {
        long left=PageComposer.Emu(x),right=PageComposer.Emu(x+width)+EffectRight;
        long top=PageComposer.Emu(y),bottom=PageComposer.Emu(y+height)+EffectBottom;
        return new(left-Left,right+Right,bottom+Bottom,Side,AllowOverlap)
        {Top=top,CollisionLeft=left,CollisionRight=right,CollisionHeight=bottom};
    }
}
internal sealed record PlacedFloatingTextFrame(FloatingTextFrame Frame,ComposedPage Content)
{
    internal FrameRotation? Transform=>Frame.Rotation switch
    {
        90=>new(Frame.X+Frame.Width,Frame.Y,90),
        -90=>new(Frame.X,Frame.Y+Frame.Height,-90),
        _=>null
    };
    internal IReadOnlyList<PageLine> Lines=>Transform is {} transform?
        Content.Lines.Select(l=>l with{FrameTransform=transform}).ToArray():Content.Lines;
    internal IReadOnlyList<PageCell> Cells=>Transform is {} transform?Content.Cells.Select(c=>
    {
        var rect=transform.Bounds(new(c.X,c.Y,c.Width,c.Height));
        return c with{X=rect.Left,Y=rect.Top,Width=rect.Width,Height=rect.Height};
    }).ToArray():Content.Cells;

    internal bool HasTextOverflow=>Frame.Rotation!=0&&Content.Geometry.Height>Frame.Geometry.Height+.01f;

    internal bool OverflowFits(PageGeometry sheet)
    {
        if(!HasTextOverflow)return true;
        var rectangles=Lines.SelectMany(l=>PageCoordinates.Clusters(l).Concat(PageCoordinates.Images(l)).Select(c=>c.Rect)).ToArray();
        return rectangles.Length>0&&rectangles.All(rect=>
                rect.Left>=-.01f&&rect.Top>=-.01f&&rect.Left+rect.Width<=sheet.Width+.01f&&
                rect.Top+rect.Height<=sheet.Height+.01f);
    }
}

internal readonly record struct FrameRotation(float X,float Y,int Degrees)
{
    internal PageRect Bounds(PageRect rect)=>Degrees==90?
        new(X-rect.Top-rect.Height,Y+rect.Left,rect.Height,rect.Width):
        new(X+rect.Top,Y-rect.Left-rect.Width,rect.Height,rect.Width);
}

internal static partial class PageComposer
{
    static (FloatingTextFrame Frame,ComposedPage Page)? PrepareFrame(FloatingTextFrame frame,SemanticStory story,
        LayoutReader.Reader reader,LineComposer painter,Dictionary<string,TextParagraph> textParagraphs,
        Dictionary<string,InlineImage> imageRuns,IEnumerable<TextParagraph> ordered,Action Check,Action<FlowIssue> Add,
        CancellationToken token,float? adjustedWidth=null)
    {
        const float maximum=1584;
        if(frame.Width>maximum||frame.Height>maximum||frame.AutoFit&&frame.Center)return null;
        var region=frame.Geometry;
        // bodyPr vertOverflow=overflow (also the DrawingML default). Only the
        // cross axis grows; the original frame still supplies the rotation origin.
        bool allowOverflow=frame.Rotation!=0&&!frame.Center&&frame.Square is null;
        if(allowOverflow)region=region with{Height=maximum};
        if(frame.AutoFit)region=region with{Height=maximum,Width=adjustedWidth??region.Width};
        var frameLayout=reader.ReadStory(story,region);
        foreach(var issue in frameLayout.Issues)Add(issue);
        if(frameLayout.Issues.Any(i=>i.BlocksRendering))return null;
        if(!frame.WrapText&&frameLayout.Tables.Count>0)return null;
        if(!frame.WrapText&&frame.AutoFit&&adjustedWidth is null)
        {
            try
            {
                var listMarkers=painter.MeasureListMarkers(frameLayout.Paragraphs,textParagraphs,token);
                var preferred=frameLayout.Paragraphs.Select(p=>Intrinsic(p,textParagraphs.GetValueOrDefault(p.Id),
                        imageRuns,Check,listMarkers.GetValueOrDefault(p.Id)).Preferred)
                    .DefaultIfEmpty(0).Max()+frame.Left+frame.Right;
                if(preferred>maximum)return null;
                if(preferred>frame.Width+.01f)return PrepareFrame(frame,story,reader,painter,textParagraphs,imageRuns,ordered,Check,Add,token,preferred);
            }
            catch(InvalidDataException error) when(!error.Message.EndsWith("_LIMIT",StringComparison.Ordinal))
            {
                Add(new(error.Message,frame.Drawing,"Largeur sans habillage de la zone de texte non mesurable.",true));
                return null;
            }
        }
        var measuredFrame=frame with{Width=region.Width,Height=region.Height,Left=region.Left,Top=region.Top,
            Right=region.Right,Bottom=region.Bottom,Rotation=0};
        if(MeasureFrame(measuredFrame,story,frameLayout) is not {} placed)return null;
        if(allowOverflow)
        {
            float height=Math.Max(frame.Geometry.Height,FrameBottom(placed,frameLayout,region.Top)+region.Bottom);
            if(height>maximum||height>frame.Geometry.Height+.01f&&
                (frameLayout.Tables.Count>0||frameLayout.Paragraphs.Any(p=>p.Properties.HasBorders||
                    p.Properties.Shading is not null||p.List is not null)))return null;
            placed=placed with{Geometry=placed.Geometry with{Height=height}};
        }
        if(!frame.WrapText&&placed.Lines.Any(l=>l.Native?.Cut is (LineCutKind.Allowed or LineCutKind.SoftHyphen or LineCutKind.AutomaticHyphen)))
            return null;
        if(frame.AutoFit)
        {
            float bottom=FrameBottom(placed,frameLayout,frame.Top);
            measuredFrame=measuredFrame with{Height=Math.Max(frame.Height,bottom+frame.Bottom)};
            if(measuredFrame.Height>maximum)return null;
        }
        return (frame.AutoFit?measuredFrame:frame,placed);

        ComposedPage? MeasureFrame(FloatingTextFrame frame,SemanticStory story,PaginatedDocument frameLayout)
        {
            if(frameLayout.Tables.Count==0&&frameLayout.Paragraphs is [var paragraph]&&
                textParagraphs.GetValueOrDefault(paragraph.Id) is {RightToLeft:false,Controls.IsEmpty:true} text&&
                !paragraph.Properties.PageBreakBefore&&!paragraph.Properties.HasBorders)
            {
                var single=painter.Compose(_=>new(frame.Id,0,Emu(frame.Width-frame.Left-frame.Right)),
                    token,orderedParagraphs:[text]);
                if(single.Lines is [var line]&&line.Occupied is not null&&!line.Diagnostics.Any(BlocksLine)&&
                    !line.HasUnsupportedUnderline&&Composer.PlaceFloatingTextLine(frame,paragraph,line) is {} positioned)
                    return new(1,[positioned]){NativePainter=painter,Geometry=frame.Geometry};
            }
            foreach(var run in frameLayout.Paragraphs.SelectMany(p=>p.Runs).Where(r=>r.Image is not null))
                imageRuns.TryAdd(run.Id,run.Image!);
            var diagnostics=ImmutableArray.CreateBuilder<FlowIssue>();
            try
            {
                var result=ComposeAuxiliaryLayout(story,frameLayout,frameLayout.Paragraphs.ToDictionary(p=>p.Id),
                    painter,imageRuns,textParagraphs,id=>textParagraphs.GetValueOrDefault(id),ordered,
                    null,diagnostics,Check,token);
                foreach(var issue in diagnostics)Add(issue);
                if(result.Page is not {} page)
                {
                    if(result.Failure is {} reason)Add(new(reason,result.Location??frame.Drawing,
                        "Contenu de zone de texte hors profil.",true));
                    return null;
                }
                float bottom=FrameBottom(page,frameLayout,frame.Top);
                float slack=frame.Height-frame.Bottom-bottom;
                if(slack<-.01f)return null;
                return frame.Center?MoveFrame(page,0,Math.Max(0,slack)/2,page.Section,page.Geometry):page;
            }
            catch(TableWidthFailure error)
            {
                Add(new(error.Message,error.Location??frame.Drawing,error.Detail,true));
                return null;
            }
        }
    }

    static float FrameBottom(ComposedPage page,PaginatedDocument frameLayout,float top)
    {
        float bottom=Math.Max(page.Lines.Select(l=>Math.Max(l.NativeTop+l.Height,
                l.ParagraphBorder is {} border?border.Top+border.Border.Width:0)).DefaultIfEmpty(top).Max(),
            page.Cells.Select(c=>c.Y+c.Height+(c.Borders.Bottom?.Width??0)/2).DefaultIfEmpty(top).Max());
        if(frameLayout.Paragraphs.LastOrDefault() is {TableId:null,CellOwner:null} tail)bottom+=tail.Properties.After;
        return bottom;
    }

    static ComposedPage MoveFrame(ComposedPage content,float x,float y,int section,PageGeometry physical)=>content with
    {
        Geometry=physical,Section=section,
        Cells=content.Cells.Select(c=>c with{X=c.X+x,Y=c.Y+y}).ToImmutableArray(),
        Lines=content.Lines.Select(line=>line with
        {
            Section=section,X=line.X+x,NativeOffsetX=line.NativeOffsetX+x,NativeTop=line.NativeTop+y,
            Baseline=line.Baseline+y,
            Images=line.Images.Select(i=>i with{X=i.X+x,Y=i.Y+y}).ToImmutableArray(),
            Tabs=line.Tabs.Select(tab=>tab with{From=tab.From+x,To=tab.To+x}).ToImmutableArray(),
            LeftBorder=line.LeftBorder is {} left?left with{X=left.X+x}:null,
            RightBorder=line.RightBorder is {} right?right with{X=right.X+x}:null,
            ParagraphTopBorder=line.ParagraphTopBorder is {} top?top with{Left=top.Left+x,Right=top.Right+x,Top=top.Top+y}:null,
            ParagraphBorder=line.ParagraphBorder is {} bottom?bottom with{Left=bottom.Left+x,Right=bottom.Right+x,Top=bottom.Top+y}:null
        }).ToImmutableArray()
    };
}

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        const string Wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        const string Draw14="http://schemas.microsoft.com/office/drawing/2010/main";
        const string Vml="urn:schemas-microsoft-com:vml";

        bool IsFloatingTextAlternative(InventoryNode n)=>SemanticIndex.IsAlternative(n)&&Elements(n).Any(c=>
            c.Name=="{"+SemanticIndex.Mc+"}Choice"&&Value(c,"Requires") is {} prefix&&Namespace(c,prefix)==Wps);

        FloatingTextFrame? ReadFloatingTextFrame(InventoryNode alternative)
        {
            string mc=SemanticIndex.Mc;
            var alternatives=Elements(alternative).ToArray();
            if(alternatives is not [var choice,var fallback]||choice.Name!="{"+mc+"}Choice"||fallback.Name!="{"+mc+"}Fallback"||
                Value(choice,"Requires") is not {} prefix||prefix.IndexOfAny([' ','\t','\n','\r'])>=0||Namespace(choice,prefix)!=Wps||
                Only(choice,W,"drawing") is not {} drawing||Only(fallback,W,"pict") is not {} pict||
                semantic!.Element(pict.Id).Coverage==SemanticCoverage.InactiveAlternative||
                Properties(drawing).Any()||Elements(pict).Any(n=>n.Name is not ("{"+Vml+"}shapetype" or "{"+Vml+"}shape"))||
                Only(drawing,WP,"anchor") is not {} anchor)return null;
            if(!Anchor(anchor,out var placement))return Refuse(anchor,"Ancre ou habillage extérieur de la zone hors profil.");
            if(Part(anchor,D,"graphic") is not {} graphic||Properties(graphic).Any()||Only(graphic,D,"graphicData") is not {} data||
                !Attrs(data,("uri",Wps))||Only(data,Wps,"wsp") is not {} shape||Properties(shape).Any()||
                !Parts(shape,Wps,"cNvSpPr","spPr","txbx","bodyPr")||
                Part(shape,Wps,"cNvSpPr") is not {} nv||!Flag(nv,"txBox",out bool textBox)||!textBox||
                Properties(nv).Count()!=1||!ShapeLocks(nv))
                return Refuse(anchor,"Structure du dessin WPS hors profil.");
            if(Part(shape,Wps,"spPr") is not {} properties||!Shape(properties,placement.Width,placement.Height,out uint? fill,out var stroke))
                return Refuse(shape,"Géométrie, trait ou remplissage de la forme hors profil.");
            if(Part(shape,Wps,"bodyPr") is not {} body||!Body(body,out var insets,out bool center,out bool autoFit,out bool wrapText,out int rotation))
                return Refuse(shape,"Corps du texte ou mode d’ajustement hors profil.");
            if(Part(shape,Wps,"txbx") is not {} text||Properties(text).Any()||Only(text,W,"txbxContent") is not {} wpsContent||
                Properties(wpsContent).Any()||
                Part(pict,Vml,"shape") is not {} vml||Part(vml,Vml,"textbox") is not {} vmlText||
                Only(vmlText,W,"txbxContent") is not {} fallbackContent||
                !Elements(fallbackContent).All(n=>Is(n,"p")||Is(n,"tbl"))||
                !SameTextTree(wpsContent,fallbackContent))
                return Refuse(shape,"Histoire WPS et repli VML non équivalents ou hors profil.");
            if(placement.Width-insets.Left-insets.Right<=0||placement.Height-insets.Top-insets.Bottom<=0)return null;
            if(runningStory&&(ReadVmlTextFrame(pict,paired:true) is not {} legacy||
                legacy.Width!=placement.Width||legacy.Height!=placement.Height||legacy.X!=placement.X||legacy.Y!=placement.Y||
                legacy.HorizontalReference!=placement.Horizontal||legacy.VerticalReference!=placement.Vertical||
                legacy.HorizontalAlignment!=placement.HorizontalAlignment||legacy.VerticalAlignment!=placement.VerticalAlignment||
                legacy.Order!=placement.Order||legacy.BehindText!=placement.Behind||legacy.Center!=center||rotation!=0||autoFit||
                !wrapText||legacy.Fill!=fill||legacy.Stroke!=stroke||legacy.Left!=insets.Left||legacy.Top!=insets.Top||
                legacy.Right!=insets.Right||legacy.Bottom!=insets.Bottom))
                return Refuse(vml,"Dimensions, ancrage ou mise en forme du repli VML incompatibles avec la zone WPS.");
            return new(pict.Id,drawing.Source,fallbackContent.Id,placement.X,placement.Y,placement.Width,placement.Height,
                insets.Left,insets.Top,insets.Right,insets.Bottom,center,placement.Order)
            {HorizontalReference=placement.Horizontal,VerticalReference=placement.Vertical,Square=placement.Square,
                AutoFit=autoFit,WrapText=wrapText,Rotation=rotation,Fill=fill,Stroke=stroke,BehindText=placement.Behind,
                HorizontalAlignment=placement.HorizontalAlignment,VerticalAlignment=placement.VerticalAlignment,
                VmlSource=runningStory?vml.Source:null};

            FloatingTextFrame? Refuse(InventoryNode node,string detail)
            {
                Issue("LAYOUT_FLOATING_TEXTBOX_DETAIL",node,detail);
                return null;
            }

            bool ShapeLocks(InventoryNode nonVisual)
            {
                if(!Elements(nonVisual).Any())return true;
                return Only(nonVisual,D,"spLocks") is {} locks&&!Elements(locks).Any()&&
                    Properties(locks).All(a=>a.Key is ("{}noAdjustHandles" or "{}noChangeArrowheads" or "{}noChangeAspect" or
                        "{}noChangeShapeType" or "{}noEditPoints" or "{}noGrp" or "{}noMove" or "{}noResize" or "{}noRot" or
                        "{}noSelect" or "{}noTextEdit")&&a.Value is ("0" or "1" or "false" or "true"));
            }

            bool Anchor(InventoryNode n,out (float X,float Y,float Width,float Height,string Horizontal,string Vertical,uint Order,
                FrameSquareWrap? Square,bool Behind,string? HorizontalAlignment,string? VerticalAlignment) result)
            {
                result=default;
                Attributes(n,"{}simplePos","{}distL","{}distR","{}distT","{}distB","{}behindDoc","{}relativeHeight",
                    "{}layoutInCell","{}locked","{}allowOverlap","{"+WP14+"}anchorId","{"+WP14+"}editId");
                var parts=Elements(n).ToArray();
                bool square=Part(n,WP,"wrapSquare") is not null;
                var expected=new[]{"simplePos","positionH","positionV","extent","effectExtent",square?"wrapSquare":"wrapNone","docPr","cNvGraphicFramePr"}
                    .Select(v=>"{"+WP+"}"+v).Append("{"+D+"}graphic").ToArray();
                if(!parts.Take(expected.Length).Select(c=>c.Name).SequenceEqual(expected)||
                    !ValidRelativeSize(parts.Skip(expected.Length))||
                    !Bool(n,"simplePos",false)||!Flag(n,"behindDoc",out bool behind)||behind&&!runningStory||
                    !Flag(n,"allowOverlap",out bool overlap)||
                    !ValidFlag(n,"layoutInCell")||!ValidFlag(n,"locked")||
                    !uint.TryParse(Value(n,"relativeHeight"),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out uint order)||
                    Part(n,WP,"simplePos") is not {} simple||!Offset(Value(simple,"x"),out _)||!Offset(Value(simple,"y"),out _)||
                    simple.Attributes.Count!=2||Elements(simple).Any()||
                    !Axis(n,"positionH",out float x,out string horizontal,out string? hAlign)||
                    !Axis(n,"positionV",out float y,out string vertical,out string? vAlign)||
                    Part(n,WP,"extent") is not {} size||!Size(size,out float width,out float height)||
                    Part(n,WP,"effectExtent") is not {} effects||!Offset(Value(effects,"b"),out long bottom)||bottom is <0 or >127000||
                    !Offset(Value(effects,"r"),out long right)||right is <0 or >127000||
                    !Attrs(effects,("l","0"),("t","0"),("r",Value(effects,"r")!),("b",Value(effects,"b")!))||Elements(effects).Any()||
                    Part(n,WP,"cNvGraphicFramePr") is not {} nonVisual||Properties(nonVisual).Any()||
                    Elements(nonVisual).Any()&&!Empty(Only(nonVisual,D,"graphicFrameLocks"))||
                    Part(n,WP,"docPr") is not {} info||Elements(info).Any()||
                    info.Attributes.Keys.Any(k=>k is not ("{}id" or "{}name" or "{}descr" or "{}title")))
                    return false;
                FrameSquareWrap? exclusion=null;
                if(runningStory&&square)return false;
                if(square)
                {
                    var wrap=parts[5];
                    string? side=Value(wrap,"wrapText");
                    if(horizontal!="column"||vertical!="paragraph"||y<0||side is not ("bothSides" or "left" or "right" or "largest")||
                        Elements(wrap).Any()||Properties(wrap).Any(p=>p.Key is not ("{}wrapText" or "{}distL" or "{}distR" or "{}distT" or "{}distB"))||
                        !Distance(wrap,"distL",out long dl)||!Distance(wrap,"distR",out long dr)||
                        !Distance(wrap,"distB",out long db)||!Distance(wrap,"distT",out long dt)||dt!=0)
                        return false;
                    exclusion=new(side,dl,dr,db,right,bottom,overlap);
                }
                else if(!overlap||!Empty(Part(n,WP,"wrapNone")))return false;
                result=(x,y,width,height,horizontal,vertical,order,exclusion,behind,hAlign,vAlign);
                return true;

                bool Distance(InventoryNode local,string name,out long value)=>
                    Offset(Value(local,name)??Value(n,name)??"0",out value)&&value is >=0 and <=1_828_800;
                bool ValidRelativeSize(IEnumerable<InventoryNode> remaining)
                {
                    int last=0;
                    foreach(var node in remaining)
                    {
                        int axis=node.Name=="{"+WP14+"}sizeRelH"?1:node.Name=="{"+WP14+"}sizeRelV"?2:0;
                        if(axis<=last||Value(node,"relativeFrom") is not ("margin" or "page")||
                            Properties(node).Count()!=1||Only(node,WP14,axis==1?"pctWidth":"pctHeight") is not {} value||
                            Properties(value).Any()||Elements(value).Any()||
                            !Offset(string.Concat(children[value.Id].Where(c=>c.Kind=="textValue").Select(c=>c.Text)).Trim(),out long pct)||pct!=0)
                            return false;
                        last=axis;
                    }
                    return true;
                }
            }
            bool Axis(InventoryNode n,string key,out float shift,out string reference,out string? align)
            {
                shift=0;reference="";align=null;
                bool horizontal=key=="positionH";
                if(Part(n,WP,key) is not {} axis||Value(axis,"relativeFrom") is not {} basis||
                    (horizontal?basis is not ("page" or "margin" or "column"):basis is not ("page" or "margin" or "paragraph"))||
                    !Attrs(axis,("relativeFrom",basis))||Elements(axis).Take(2).ToArray() is not [var value]||
                    Properties(value).Any()||Elements(value).Any())return false;
                string raw=string.Concat(children[value.Id].Where(c=>c.Kind=="textValue").Select(c=>c.Text)).Trim();
                reference=basis;
                if(runningStory&&basis!="paragraph"&&value.Name=="{"+WP+"}align"&&
                    (horizontal?raw is "left" or "center" or "right":raw is "top" or "center" or "bottom"))
                {align=raw;return true;}
                if(value.Name!="{"+WP+"}posOffset"||!Offset(raw,out long offset))return false;
                shift=PageComposer.Points(offset);
                return true;
            }
            bool Shape(InventoryNode n,float width,float height,out uint? fill,out FrameStroke? stroke)
            {
                fill=null;
                stroke=null;
                string paint=Part(n,D,"solidFill") is null?"noFill":"solidFill";
                return (!Properties(n).Any()||Attrs(n,("bwMode","auto")))&&
                (Parts(n,D,"xfrm","prstGeom",paint,"ln")||Parts(n,D,"xfrm","prstGeom",paint,"ln","extLst"))&&
                Part(n,D,"xfrm") is {} transform&&!Properties(transform).Any()&&Parts(transform,D,"off","ext")&&
                Part(transform,D,"off") is {} org&&Attrs(org,("x","0"),("y","0"))&&!Elements(org).Any()&&
                Part(transform,D,"ext") is {} dim&&Size(dim,out float w,out float h)&&w==width&&h==height&&
                Part(n,D,"prstGeom") is {} geom&&Attrs(geom,("prst","rect"))&&Empty(Only(geom,D,"avLst"))&&
                Part(n,D,paint) is {} brush&&(paint=="noFill"?Empty(brush):SolidFill(brush,out fill))&&
                Part(n,D,"ln") is {} line&&
                (!Properties(line).Any()||Attrs(line,("w",Value(line,"w")??""))&&Offset(Value(line,"w"),out long widthEmu)&&widthEmu>=0)&&
                (NoStroke(line)||runningStory&&ReadSolidStroke(line,out stroke))&&
                (Part(n,D,"extLst") is not {} extList||HiddenLine(extList));
            }
            bool ReadSolidStroke(InventoryNode line,out FrameStroke? stroke)
            {
                stroke=null;
                if(!Offset(Value(line,"w"),out long width)||width is <=0 or >127000||
                    Only(line,D,"solidFill") is not {} brush||!SolidFill(brush,out uint? color)||color is null)
                    return false;
                stroke=new(color.Value,PageComposer.Points(width));
                return true;
            }
            bool NoStroke(InventoryNode line)
            {
                if(!Empty(Part(line,D,"noFill")))return false;
                if(Parts(line,D,"noFill"))return true;
                return Parts(line,D,"noFill","miter","headEnd","tailEnd")&&
                    Part(line,D,"miter") is {} miter&&Attrs(miter,("lim",Value(miter,"lim")??""))&&
                    Offset(Value(miter,"lim"),out long limit)&&limit>=0&&!Elements(miter).Any()&&
                    Empty(Part(line,D,"headEnd"))&&Empty(Part(line,D,"tailEnd"));
            }
            bool SolidFill(InventoryNode n,out uint? fill)
            {
                fill=null;
                if(Properties(n).Any()||Elements(n).Take(2).ToArray() is not [var color])return false;
                string? source=Value(color,"val");
                if(color.Name=="{"+D+"}sysClr"&&source=="window")
                {
                    source=Value(color,"lastClr");
                    if(source is null||!Attrs(color,("val","window"),("lastClr",source)))return false;
                }
                else if(color.Name!="{"+D+"}srgbClr"||source is null||!Attrs(color,("val",source)))return false;
                if(source.Length!=6||!uint.TryParse(source,System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,out uint rgb))return false;
                var transforms=Elements(color).Take(2).ToArray();
                if(transforms.Length==0){fill=rgb;return true;}
                if(transforms is not [var luminance]||luminance.Name!="{"+D+"}lumMod"||Elements(luminance).Any()||
                    !Attrs(luminance,("val",Value(luminance,"val")??""))||
                    !Offset(Value(luminance,"val"),out long factor)||factor is <0 or >100000)
                    return false;
                fill=WordColor.ModulateDrawingLuminance(rgb,factor);
                return true;
            }
            bool HiddenLine(InventoryNode extList)=>
                !Properties(extList).Any()&&Only(extList,D,"ext") is {} ext&&
                Attrs(ext,("uri","{91240B29-F687-4F45-9708-019B960494DF}"))&&Only(ext,Draw14,"hiddenLine") is {} hidden&&
                Attrs(hidden,("w","6350"))&&Only(hidden,D,"solidFill") is {} fill&&!Properties(fill).Any()&&
                Only(fill,D,"prstClr") is {} color&&Attrs(color,("val","black"))&&!Elements(color).Any();
            bool Body(InventoryNode n,out ImageInsets insets,out bool center,out bool autoFit,out bool wrapText,out int rotation)
            {
                insets=default;center=Value(n,"anchor")=="ctr";
                autoFit=Part(n,D,"spAutoFit") is not null;
                wrapText=Value(n,"wrap")=="square";
                string? direction=Value(n,"vert");
                rotation=direction switch{"vert"=>90,"vert270"=>-90,_=>0};
                string mode=autoFit?"spAutoFit":"noAutofit";
                if(Value(n,"anchor") is not ("ctr" or "t")||Value(n,"wrap") is not ("square" or "none")||
                    direction is not ("horz" or "vert" or "vert270")||autoFit&&rotation!=0||
                    !Offset(Value(n,"lIns"),out long l)||!Offset(Value(n,"rIns"),out long r)||
                    !Offset(Value(n,"tIns"),out long t)||!Offset(Value(n,"bIns"),out long b)||Math.Min(Math.Min(l,r),Math.Min(t,b))<0||
                    !Attrs(n,("rot","0"),("spcFirstLastPara","0"),("vertOverflow","overflow"),("horzOverflow","overflow"),("vert",direction),
                         ("wrap",Value(n,"wrap")!),("lIns",Value(n,"lIns")!),("rIns",Value(n,"rIns")!),("tIns",Value(n,"tIns")!),("bIns",Value(n,"bIns")!),
                         ("numCol","1"),("spcCol","0"),("rtlCol","0"),("fromWordArt","0"),("anchor",Value(n,"anchor")!),
                         ("anchorCtr","0"),("forceAA","0"),("compatLnSpc","1"))&&
                    !Attrs(n,("rot","0"),("vert",direction),("wrap",Value(n,"wrap")!),("lIns",Value(n,"lIns")!),
                        ("rIns",Value(n,"rIns")!),("tIns",Value(n,"tIns")!),("bIns",Value(n,"bIns")!),("anchor",Value(n,"anchor")!),("anchorCtr","0"))||
                    !Empty(Part(n,D,mode))||
                    !(Parts(n,D,mode)||Parts(n,D,"prstTxWarp",mode)&&Part(n,D,"prstTxWarp") is {} warp&&
                        Attrs(warp,("prst","textNoShape"))&&Empty(Only(warp,D,"avLst"))))
                    return false;
                insets=new(PageComposer.Points(l),PageComposer.Points(t),PageComposer.Points(r),PageComposer.Points(b));
                return true;
            }
            bool Size(InventoryNode n,out float w,out float h)
            {
                w=h=0;
                if(n.Attributes.Count!=2||Elements(n).Any()||!Offset(Value(n,"cx"),out long cx)||!Offset(Value(n,"cy"),out long cy)||cx<=0||cy<=0)return false;
                w=PageComposer.Points(cx);h=PageComposer.Points(cy);return true;
            }
            bool SameTextTree(InventoryNode a,InventoryNode b,int depth=0)
            {
                token.ThrowIfCancellationRequested();
                if(depth>64||a.Name!=b.Name||!a.Name.StartsWith("{"+W+"}",StringComparison.Ordinal)&&
                    !a.Name.StartsWith("{http://schemas.microsoft.com/office/word/2010/wordml}",StringComparison.Ordinal)&&a.Kind!="textValue"||
                    (a.Kind=="textValue"&&a.Text!=b.Text)||
                    !Properties(a).OrderBy(p=>p.Key,StringComparer.Ordinal).SequenceEqual(Properties(b).OrderBy(p=>p.Key,StringComparer.Ordinal)))
                    return false;
                var first=children[a.Id].Where(c=>!c.Name.StartsWith('#')||c.Kind=="textValue").ToArray();
                var second=children[b.Id].Where(c=>!c.Name.StartsWith('#')||c.Kind=="textValue").ToArray();
                return first.Length==second.Length&&first.Zip(second).All(t=>SameTextTree(t.First,t.Second,depth+1));
            }
            InventoryNode? Part(InventoryNode n,string ns,string name)=>SemanticIndex.Unique(Elements(n).Where(c=>c.Name=="{"+ns+"}"+name));
            InventoryNode? Only(InventoryNode n,string ns,string name)=>Elements(n).Take(2).ToArray() is [var child]&&child.Name=="{"+ns+"}"+name?child:null;
            bool Parts(InventoryNode n,string ns,params string[] names)=>Elements(n).Select(c=>c.Name).SequenceEqual(names.Select(k=>"{"+ns+"}"+k));
            bool Empty(InventoryNode? n)=>n is not null&&!Properties(n).Any()&&!Elements(n).Any();
            bool Attrs(InventoryNode n,params (string Key,string Value)[] values)=>Properties(n).Count()==values.Length&&values.All(t=>Value(n,t.Key)==t.Value);
            static IEnumerable<KeyValuePair<string,string>> Properties(InventoryNode n)=>
                n.Attributes.Where(p=>!p.Key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal));
        }

        string? Namespace(InventoryNode n,string name)
        {
            for(InventoryNode? current=n;current is not null;current=current.ParentId is {} id?nodes[id]:null)
                if(current.Attributes.TryGetValue("{http://www.w3.org/2000/xmlns/}"+name,out string? ns))return ns;
            return null;
        }
    }
}
