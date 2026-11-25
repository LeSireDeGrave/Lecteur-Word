using System.IO.Compression;
using System.Xml.Linq;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    internal static void RunningDrawingGroups(string root)
    {
        const string header="""
            <w:p><w:r><w:pict xmlns:v="urn:schemas-microsoft-com:vml" xmlns:w10="urn:schemas-microsoft-com:office:word">
            <v:group style="position:absolute;width:160pt;height:45pt;margin-top:25pt;mso-position-horizontal:left;mso-position-horizontal-relative:margin;mso-position-vertical-relative:page;mso-wrap-distance-bottom:3pt" coordsize="1600,450">
              <v:group style="position:absolute;left:300;top:20;width:800;height:420" coordsize="800,420" coordorigin="10,20">
                <v:rect style="position:absolute;left:10;top:20;width:800;height:210;v-text-anchor:middle" fillcolor="#197281" stroked="f">
                  <v:textbox inset="2pt,1pt,2pt,1pt"><w:txbxContent>
                    <w:p><w:fldSimple w:instr="NUMPAGES \* Roman"/></w:p>
                  </w:txbxContent></v:textbox>
                </v:rect>
                <v:rect style="position:absolute;left:10;top:230;width:800;height:210;v-text-anchor:bottom" filled="f" stroked="f">
                  <v:textbox inset="2pt,1pt,2pt,1pt"><w:txbxContent>
                    <w:p><w:fldSimple w:instr="SECTIONPAGES \# &quot;00&quot;"/></w:p>
                  </w:txbxContent></v:textbox>
                </v:rect>
              </v:group>
              <w10:wrap type="topAndBottom"/>
            </v:group>
            </w:pict></w:r></w:p>
            """;
        string authoredPath=Create("nested-running-vml",header);
        using var authored=Compose(authoredPath);
        var s=authored.Snapshot;
        Complete(s,2);
        foreach(var page in s.Composition.Pages)
        {
            var leaves=page.FloatingTextFrames.OrderBy(f=>f.Frame.ChildOrder).ToArray();
            Check(page.Geometry.Top==73&&leaves is [var first,var second]&&
                first.Frame.RunningDrawing?.Root.Children.Single().Children.Length==2&&
                first.Frame.RunningDrawing.Root.ChildSpace is {Width:1600,Height:450}&&
                first.Frame.X==75&&first.Frame.Y==27&&first.Frame.Width==80&&first.Frame.Height==21&&second.Frame.Y==48&&
                first.Frame.Center&&second.Frame.BottomAligned&&
                first.Content.Lines.Single().Native!.Generated.Single(f=>f.Kind=="pageField").Fragment.Text=="II"&&
                second.Content.Lines.Single().Native!.Generated.Single(f=>f.Kind=="pageField").Fragment.Text=="02",
                "Le groupe imbriqué maintient ses coordonnées locales et réserve le corps. Les deux totaux sont formatés.");
            using var bitmap=SKBitmap.Decode(Composer.PagePng(page,2));
            Check(bitmap.GetPixel(152,56)==SKColor.Parse("#197281"),"Rectangle VML peint dans la région réservée.");
        }
        var source=new PublicationSource(DocumentInspector.InspectDocument(authoredPath),s,"Groupe auxiliaire");
        var request=new PublicationRequest(ExportFormat.Odt);
        var plan=Publications.Prepare(source,request,default);
        Check(plan.Allowed&&plan.Reservations.Any(i=>i.Code=="EXPORT_ODT_RUNNING_FRAME_PAGE_FIELD"),
            "Le champ de pagination d’un cadre auxiliaire doit rester dynamique avec une adaptation localisée.");
        string output=Path.Combine(root,"nested-running-vml.odt");
        Publications.Write(source,request,output);
        using(var file=ZipFile.OpenRead(output))
        using(var styles=file.GetEntry("styles.xml")!.Open())
        {
            XNamespace text=StructuredPublication.Text,draw=StructuredPublication.Draw;
            var number=XDocument.Load(styles).Descendants(text+"page-count").Single();
            Check(!number.Ancestors(draw+"text-box").Any(),"Writer doit pouvoir conserver le champ de total.");
        }
        using var obstructed=Compose(Create("running-vml-wrap-side",header.Replace("topAndBottom","square",StringComparison.Ordinal).Replace("width:160pt","width:140pt",StringComparison.Ordinal)));
        Check(obstructed.Snapshot.Coverage!=PageCoverage.Complete&&obstructed.Snapshot.Composition.Issues.Any(
            i=>i.Code=="PAGE_RUNNING_DRAWING_WRAP_UNQUALIFIED"&&i.BlocksRendering),"Habillage latéral au milieu du corps signalé.");

        string Create(string name,string story)
        {
            string format=Section(5000,5000,margins:900).Replace("<w:sectPr>","""
                <w:sectPr><w:headerReference w:type="default" r:id="head"/>
                """,StringComparison.Ordinal);
            string body=Paragraph(Run("Première feuille")+"""<w:r><w:br w:type="page"/></w:r>"""+Run("Deuxième feuille"))+format;
            return Fixtures.Write(root,name+".docx",Fixtures.Parts(body,new(){["word/header1.xml"]=Fixtures.Xml("hdr",story)},
                Fixtures.Relationship("head","header","header1.xml")));
        }
    }
}
