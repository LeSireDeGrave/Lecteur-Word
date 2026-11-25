using System.Text;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static class SmartArtDataCases
{
    const string Dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram";
    const string A="http://schemas.openxmlformats.org/drawingml/2006/main";
    const string Wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

    internal static void Run(string root)
    {
        using var measured=PageComposer.Compose(Inspect("linear-data","lin",1));
        var result=measured.Snapshot;
        Check(result.Coverage==PageCoverage.Complete&&result.Pages.Length==1,
            "Disposition sans cache. "+string.Join(", ",result.Composition.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code+" "+i.Message)));
        var page=result.Composition.Pages.Single();
        var image=page.Lines.SelectMany(l=>l.Images).Single();
        Check(image.Image.Diagram is {LayoutProfile:"linear-direct",Shapes.Length:2}&&
            image.Image.Diagram.Shapes.Select(s=>s.Text.Single().Fragment.Text).SequenceEqual(["Alpha","Bêta"])&&
            image.Image.Diagram.Shapes[0].Box.Left>image.Image.Diagram.Shapes[1].Box.Left,
            "Le graphe impose l’ordre des libellés et l’algorithme le sens de placement.");
        using var raster=SKBitmap.Decode(Composer.PagePng(page,2));
        Check(raster.GetPixel((int)(2*(image.X+20)),(int)(2*(image.Y+7)))==SKColor.Parse("#225599"),
            "Le dessin calculé utilise la couleur déclarée.");
        using var unsupported=PageComposer.Compose(Inspect("cycle-data","cycle",1));
        Check(unsupported.Snapshot.Coverage!=PageCoverage.Complete&&unsupported.Snapshot.Composition.Issues.Any(i=>
            i.Code=="LAYOUT_SMARTART_ALGORITHM_UNSUPPORTED"&&i.Message.Contains("cycle")),"Un paramètre linDir dans un cycle reste hors profil.");
        using var invalid=PageComposer.Compose(Inspect("invalid-data","lin",0));
        Check(invalid.Snapshot.Coverage!=PageCoverage.Complete&&invalid.Snapshot.Composition.Issues.Any(i=>
            i.Code=="LAYOUT_SMARTART_ALGORITHM_UNSUPPORTED"),"Deux enfants de même rang ambigu ne sont pas dessinés.");

        RunFamily("processus","snake",90,60,4,
            Algorithm("snake",("flowDir","row"),("contDir","revDir"))+Each("ch",Entry("entry"))+
            Link("stra",null,null,"arr"),drawing=>
            {
                var boxes=drawing.Shapes.Select(s=>s.Box).ToArray();
                Check(boxes[0].Top==boxes[1].Top&&boxes[2].Top==boxes[3].Top&&boxes[0].Top<boxes[2].Top&&
                    boxes[0].Left<boxes[1].Left&&boxes[2].Left>boxes[3].Left&&drawing.Connectors.Length==3&&
                    drawing.Connectors.All(c=>c.Points.Length==2&&c.ArrowAtEnd),"Serpentin sur deux rangées et flèches.");
            });
        RunFamily("cycle","cycle",120,80,4,
            Algorithm("cycle",("stAng","-90"),("spanAng","360"))+Each("ch",Entry("entry","ellipse"))+
            Link("curve",null,null,"arr"),drawing=>
            {
                var boxes=drawing.Shapes.Select(s=>s.Box).ToArray();
                Check(boxes[0].Top<boxes[1].Top&&boxes[1].Left>boxes[0].Left&&boxes[2].Top>boxes[3].Top&&
                    drawing.Connectors.Length==4&&drawing.Connectors.All(c=>c.Points.Length==4&&c.Route=="curve")&&
                    drawing.Connectors[3].From=="d"&&drawing.Connectors[3].To=="a","Cycle fermé et points de contrôle.");
            });
        RunFamily("organigramme","hierRoot",120,60,3,Algorithm("hierRoot")+
            Node("tree",Algorithm("hierChild")+Each("des",Entry("entry"))+Link("bend",null,null,"noArr")),drawing=>
            {
                var boxes=drawing.Shapes.Select(s=>s.Box).ToArray();
                Check(boxes[0].Top<boxes[1].Top&&boxes[1].Top==boxes[2].Top&&boxes[1].Left<boxes[0].Left&&
                    boxes[0].Left<boxes[2].Left&&drawing.Connectors.Length==2&&drawing.Connectors.All(c=>c.From=="a"&&
                        c.Route=="bend"&&c.Points.Length==4&&!c.ArrowAtEnd)&&
                    drawing.Groups.Single().Children.Single().Algorithm=="hierChild","Niveaux du graphe et raccords orthogonaux.");
            },hierarchy:true);
        RunFamily("relation","composite",120,60,2,
            Algorithm("composite")+Each("ch",Entry("left",fixedSize:false),1)+Each("ch",Entry("right",fixedSize:false),2)+
            Link("stra","left","right","arr")+Node("badge",Algorithm("composite")+
                """<dgm:layoutNode name="badgeShape" styleLbl="tone"><dgm:alg type="sp"/><dgm:shape type="ellipse" rot="10"/></dgm:layoutNode>"""+
                Constraints(("badgeShape",.5f,.5f,.55f,.55f)))+
            Constraints(("left",.2f,.5f,.25f,.3f),("right",.8f,.5f,.25f,.3f),("badge",.5f,.82f,.2f,.2f)),drawing=>
            {
                var boxes=drawing.Shapes.Select(s=>s.Box).ToArray();
                Check(boxes[0].Left<boxes[1].Left&&boxes[0].Top==boxes[1].Top&&
                    drawing.Connectors is [var connector]&&connector.From=="a"&&connector.To=="b"&&
                    Near(connector.Points[0].X,boxes[0].Left+boxes[0].Width)&&Near(connector.Points[^1].X,boxes[1].Left)&&
                    drawing.Shapes[^1] is {Geometry:"ellipse",Rotation:10}&&
                    drawing.Groups.Single().Children[^1].Children.Single().Algorithm=="sp",
                    "Relations par nom de disposition et points sur les contours.");
            });
        RunFamily("matrice","composite",120,60,4,
            Algorithm("composite")+"""
                <dgm:choose><dgm:if func="cnt" axis="ch" ptType="node" op="equ" val="4">
                """+
            Each("ch",Entry("nw","roundRect",fixedSize:false),1)+Each("ch",Entry("ne",fixedSize:false),2)+Each("ch",Entry("sw",fixedSize:false),3)+
            Each("ch",Entry("se",fixedSize:false),4)+
            "</dgm:if><dgm:else>"+Each("ch",Entry("other"))+"</dgm:else></dgm:choose>"+
            Constraints(("nw",.25f,.25f,.4f,.4f),("ne",.75f,.25f,.4f,.4f),
                ("sw",.25f,.75f,.4f,.4f),("se",.75f,.75f,.4f,.4f)),drawing=>
            {
                var boxes=drawing.Shapes.Select(s=>s.Box).ToArray();
                Check(boxes[0].Left==boxes[2].Left&&boxes[1].Left==boxes[3].Left&&boxes[0].Top==boxes[1].Top&&
                    boxes[2].Top==boxes[3].Top&&boxes[2].Top>boxes[0].Top&&drawing.Connectors.IsEmpty&&
                    drawing.Shapes[0].Geometry=="roundRect","Branche de plan à quatre points et contraintes de matrice.");
            });
        RunFamily("pyramide","pyramid",120,90,3,
            Algorithm("pyramid",("pyraLvlNode","tier"))+Each("ch",Entry("tier","trapezoid",120,30)),drawing=>
            {
                var levels=drawing.Shapes;
                Check(levels[0].Outline[0]==levels[0].Outline[1]&&levels[0].Box.Width<levels[1].Box.Width&&
                    levels[1].Box.Width<levels[2].Box.Width&&levels[1].Outline[0]==levels[0].Outline[3],
                    "Apex triangulaire et étages trapézoïdaux continus.");
            });

        void RunFamily(string name,string profile,int mmWidth,int mmHeight,int nodes,string layout,
            Action<DiagramDrawing> verify,bool hierarchy=false)
        {
            var labels=new[]{"Alpha","Bêta","Gamma","Delta"}.Take(nodes).ToArray();
            using var lease=PageComposer.Compose(Inspect(name,profile,1,layout,labels,mmWidth,mmHeight,hierarchy));
            var snapshot=lease.Snapshot;
            Check(snapshot.Coverage==PageCoverage.Complete&&snapshot.Composition.Pages.Count==1,
                name+" : "+string.Join(", ",snapshot.Composition.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code+" "+i.Message)));
            var sheet=snapshot.Composition.Pages.Single();
            var item=sheet.Lines.SelectMany(l=>l.Images).Single();
            var diagram=item.Image.Diagram!;
            Check(diagram.LayoutProfile==profile&&diagram.Model is {} model&&model.Points.Count==nodes+1&&
                model.Edges.Length==nodes&&diagram.Shapes.Where(s=>!s.Text.IsEmpty).Select(s=>s.Text.Single().Fragment.Text).SequenceEqual(labels),
                "Le modèle, les liens et les libellés de "+name+" sont conservés.");
            verify(diagram);
            using var png=SKBitmap.Decode(Composer.PagePng(sheet,2));
            var box=diagram.Shapes[^1].Box;
            Check(png.GetPixel((int)(2*(item.X+box.Left+box.Width*.55f)),(int)(2*(item.Y+box.Top+box.Height*.8f)))==SKColor.Parse("#225599"),
                "La surface de "+name+" est peinte.");
            foreach(var connection in diagram.Connectors.Where(c=>c.Route=="stra"))
            {
                var point=(connection.Points[0]+connection.Points[^1])/2;
                Check(png.GetPixel((int)(2*(item.X+point.X)),(int)(2*(item.Y+point.Y)))==SKColor.Parse("#C32230"),
                    "Le trajet calculé de "+name+" est peint.");
            }
        }

        static bool Near(float a,float b)=>Math.Abs(a-b)<.01f;
        static string Algorithm(string name,params (string Type,string Value)[] args)=>
            $"""<dgm:alg type="{name}">"""+string.Concat(args.Select(a=>$"""<dgm:param type="{a.Type}" val="{a.Value}"/>"""))+"</dgm:alg>";
        static string Node(string name,string content)=>$"""<dgm:layoutNode name="{name}">{content}</dgm:layoutNode>""";
        static string Entry(string name,string shape="rect",int width=32,int height=20,bool fixedSize=true)=>
            $"""
            <dgm:layoutNode name="{name}" styleLbl="tone"><dgm:alg type="tx"/><dgm:presOf axis="self" ptType="node"/>
            <dgm:shape type="{shape}"/><dgm:constrLst>
            {(fixedSize?$"""<dgm:constr type="w" val="{width}"/><dgm:constr type="h" val="{height}"/>""":"")}
            <dgm:constr type="primFontSz" val="12"/></dgm:constrLst></dgm:layoutNode>
            """;
        static string Each(string axis,string template,int? index=null)=>
            $"""<dgm:forEach axis="{axis}" ptType="node" """+(index is {} i?$"""st="{i}" cnt="1" """:"")+">"+template+"</dgm:forEach>";
        static string Link(string route,string? src,string? dst,string arrow)=>
            """<dgm:layoutNode name="link" styleLbl="link">"""+
            (src is null?Algorithm("conn",("connRout",route),("endSty",arrow),("dim","1D")):
                Algorithm("conn",("connRout",route),("endSty",arrow),("dim","1D"),("srcNode",src),("dstNode",dst!)))+
            """<dgm:shape type="line"/></dgm:layoutNode>""";
        static string Constraints(params (string Name,float X,float Y,float W,float H)[] nodes)=>
            "<dgm:constrLst>"+string.Concat(nodes.Select(n=>C(n.Name,"ctrX","w",n.X)+C(n.Name,"ctrY","h",n.Y)+
                C(n.Name,"w","w",n.W)+C(n.Name,"h","h",n.H)))+"</dgm:constrLst>";
        static string C(string node,string type,string reference,float ratio)=>
            FormattableString.Invariant($"""<dgm:constr type="{type}" for="ch" forName="{node}" refFor="self" refType="{reference}" fact="{ratio}"/>""");

        InspectionResult Inspect(string name,string algorithm,int order,string? layout=null,string[]? labels=null,
            int mmWidth=80,int mmHeight=20,bool hierarchy=false)
        {
            string body=$"""
                <w:p><w:r><w:drawing xmlns:wp="{Wp}" xmlns:a="{A}" xmlns:dgm="{Dgm}">
                <wp:inline><wp:extent cx="{mmWidth*36000}" cy="{mmHeight*36000}"/><wp:docPr id="1" name="Diagram"/>
                <a:graphic><a:graphicData uri="{Dgm}"><dgm:relIds r:dm="data" r:lo="layout" r:qs="style" r:cs="colors"/></a:graphicData></a:graphic>
                </wp:inline></w:drawing></w:r></w:p>
                """;
            string Point(string id,string label)=>$"""<dgm:pt modelId="{id}"><dgm:t><a:bodyPr lIns="25400" rIns="25400" tIns="25400" bIns="25400" anchor="ctr" wrap="square"><a:noAutofit/></a:bodyPr><a:p><a:pPr algn="ctr"/><a:r><a:t>{label}</a:t></a:r></a:p></dgm:t></dgm:pt>""";
            string points=labels is null?Point("b","Bêta")+Point("a","Alpha"):
                string.Concat(labels.Select((label,i)=>Point(((char)('a'+i)).ToString(),label)));
            string edges=labels is null?Edge("root","b",order)+Edge("root","a",0):
                string.Concat(labels.Select((_,i)=>Edge(hierarchy&&i>0?"a":"root",((char)('a'+i)).ToString(),hierarchy?Math.Max(0,i-1):i)));
            static string Edge(string from,string to,int position)=>
                $"""<dgm:cxn modelId="edge-{to}" type="parOf" srcId="{from}" destId="{to}" srcOrd="{position}" destOrd="0"/>""";
            layout??=$"""
                <dgm:alg type="{algorithm}"><dgm:param type="linDir" val="fromR"/></dgm:alg>
                {Each("ch",Entry("entry","rect",40,20))}
                """;
            var xml=new Dictionary<string,(string Type,string Xml)>
            {
                ["data"]=("diagramData",$"""
                    <dgm:dataModel xmlns:dgm="{Dgm}" xmlns:a="{A}">
                    <dgm:ptLst><dgm:pt modelId="root" type="doc"/>{points}</dgm:ptLst>
                    <dgm:cxnLst>{edges}</dgm:cxnLst>
                    </dgm:dataModel>
                    """),
                ["layout"]=("diagramLayout",$"""
                    <dgm:layoutDef xmlns:dgm="{Dgm}">
                    <dgm:layoutNode name="base">{layout}</dgm:layoutNode></dgm:layoutDef>
                    """),
                ["style"]=("diagramStyle",$"""
                    <dgm:styleDef xmlns:dgm="{Dgm}" xmlns:a="{A}"><dgm:styleLbl name="tone"><dgm:style>
                    <a:lnRef idx="0"><a:schemeClr val="phClr"/></a:lnRef><a:fillRef idx="1"><a:schemeClr val="phClr"/></a:fillRef>
                    <a:effectRef idx="0"><a:schemeClr val="phClr"/></a:effectRef><a:fontRef idx="minor"><a:schemeClr val="phClr"/></a:fontRef>
                    </dgm:style></dgm:styleLbl>
                    <dgm:styleLbl name="link"><dgm:style>
                    <a:lnRef idx="1"><a:schemeClr val="phClr"/></a:lnRef><a:fillRef idx="0"><a:schemeClr val="phClr"/></a:fillRef>
                    <a:effectRef idx="0"><a:schemeClr val="phClr"/></a:effectRef><a:fontRef idx="minor"><a:schemeClr val="phClr"/></a:fontRef>
                    </dgm:style></dgm:styleLbl></dgm:styleDef>
                    """),
                ["colors"]=("diagramColors",$"""
                    <dgm:colorsDef xmlns:dgm="{Dgm}" xmlns:a="{A}"><dgm:styleLbl name="tone">
                    <dgm:fillClrLst meth="repeat"><a:schemeClr val="accent1"/></dgm:fillClrLst>
                    <dgm:txFillClrLst><a:srgbClr val="FFFFFF"/></dgm:txFillClrLst>
                    </dgm:styleLbl><dgm:styleLbl name="link"><dgm:linClrLst><a:srgbClr val="C32230"/></dgm:linClrLst>
                    </dgm:styleLbl></dgm:colorsDef>
                    """),
                ["theme"]=("theme",$"""
                    <a:theme xmlns:a="{A}"><a:themeElements>
                    <a:clrScheme><a:accent1><a:srgbClr val="225599"/></a:accent1></a:clrScheme>
                    <a:fontScheme><a:minorFont><a:latin typeface="DejaVu Serif"/></a:minorFont></a:fontScheme>
                    <a:fmtScheme><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w="25400"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst></a:fmtScheme>
                    </a:themeElements></a:theme>
                    """)
            };
            var package=Fixtures.Parts(body,relationships:string.Concat(xml.Select(p=>Fixtures.Relationship(p.Key,
                p.Key=="style"?"diagramQuickStyle":p.Value.Type,p.Key+".xml"))));
            var manifest=Encoding.UTF8.GetString(package["[Content_Types].xml"]);
            foreach(var p in xml)
            {
                package["word/"+p.Key+".xml"]=Encoding.UTF8.GetBytes(p.Value.Xml);
                manifest=manifest.Replace("</Types>",$"""<Override PartName="/word/{p.Key}.xml" ContentType="application/vnd.openxmlformats-officedocument.{(p.Key=="theme"?"":"drawingml.")}{p.Value.Type}+xml"/></Types>""");
            }
            package["[Content_Types].xml"]=Encoding.UTF8.GetBytes(manifest);
            return DocumentInspector.InspectDocument(Fixtures.Write(root,name+".docx",package));
        }
    }

    static void Check(bool value,string detail)=>LineCases.Check(value,detail);
}
