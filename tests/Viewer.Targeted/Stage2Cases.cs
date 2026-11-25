using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    internal static void CellTypography(string root)
    {
        const string smallTracking="<w:spacing w:val=\"-2\"/>";
        string normal=Paragraph(Run("ABC"));
        string condensed=Paragraph(CharacterRun("ABC",smallTracking+"<w:u w:val=\"single\"/><w:color w:val=\"A80000\"/>"));
        using var result=Compose(Write(root,"piste-et-trait",normal+condensed+Section()));
        Complete(result.Snapshot,1);
        var page=result.Snapshot.Composition.Pages[0];
        var lines=page.Lines;
        Check(lines[0].Native!.Advance-lines[1].Native!.Advance==3*PageComposer.Emu(.1f),
            "L’espacement signé participe à l’avance de chaque cluster.");
        Check(lines[1].Native!.Underlines.Length==3&&lines[1].Native!.Underlines.All(rule=>
            rule.Ink.Right>rule.Ink.Left&&rule.Ink.Bottom>rule.Ink.Top&&rule.Source.Span is null),
            "Les traits mesurés gardent leur source de propriété.");
        using var raster=SKBitmap.Decode(Composer.PagePng(page,3));
        var stroke=lines[1].Native!.Underlines[0].Ink;
        var pixel=raster.GetPixel((int)Math.Round(3*(lines[1].NativeOffsetX+PageComposer.Points((stroke.Left+stroke.Right)/2))),
            (int)Math.Floor(3*(lines[1].Baseline+PageComposer.Points((stroke.Top+stroke.Bottom)/2))));
        Check(pixel.Green<240&&pixel.Blue<240&&pixel.Red>pixel.Green,"Le trait est peint au même emplacement que sa mesure.");

        using var fitted=Compose(Write(root,"espacement-autofit",Table(normal)+Table(condensed)+Section()));
        Complete(fitted.Snapshot,1);
        var tables=fitted.Snapshot.RulerModel!.Tables;
        Check(tables.All(table=>table.WidthResult is {ContentVerified:true})&&
            Math.Abs(tables[0].WidthResult!.Preferred-tables[1].WidthResult!.Preferred-.3f)<.01f,
            "AutoFit doit prendre en compte le même espacement que la ligne peinte.");

        string scaled=Paragraph(CharacterRun("NNNN","<w:w w:val=\"90\"/>"));
        string unscaled=Paragraph(CharacterRun("NNNN","<w:w w:val=\"100\"/>"));
        using var scaling=Compose(Write(root,"echelle-horizontale",scaled+unscaled+Table(scaled)+Table(unscaled)+Section()));
        Complete(scaling.Snapshot,1);
        var scaledPage=scaling.Snapshot.Composition.Pages.Single();
        var scaledLine=scaledPage.Lines[0];
        var referenceLine=scaledPage.Lines[1];
        Check(scaledLine.Native!.Advance is {} scaledAdvance&&referenceLine.Native!.Advance is {} referenceAdvance&&
            Math.Abs((double)scaledAdvance/referenceAdvance-.9)<.001&&
            Close(scaledLine.Height,referenceLine.Height)&&
            PageCoordinates.Clusters(scaledLine).First().Rect.Width<PageCoordinates.Clusters(referenceLine).First().Rect.Width,
            "w:w règle l’avance et les clusters en conservant la hauteur de ligne.");
        var scaleTables=scaling.Snapshot.RulerModel!.Tables;
        Check(scaleTables.All(table=>table.WidthResult is {ContentVerified:true})&&
            scaleTables[0].WidthResult!.Preferred<scaleTables[1].WidthResult!.Preferred,
            "AutoFit intègre la mise à l’échelle du même texte.");

        string inherited=Paragraph(CharacterRun("Taille héritée","<w:szCs w:val=\"32\"/>"));
        string explicitSize=Paragraph(CharacterRun("Taille directe","<w:sz w:val=\"20\"/><w:szCs w:val=\"32\"/>"));
        using var provenance=Compose(Write(root,"taille-latine-et-script-complexe",inherited+explicitSize+Section()));
        Complete(provenance.Snapshot,1);
        var sizeLines=provenance.Snapshot.Composition.Pages.Single().Lines;
        var fallback=sizeLines[0].Native!.Parts.First(p=>p.Fragment is not null).Fragment!;
        var direct=sizeLines[1].Native!.Parts.First(p=>p.Fragment is not null).Fragment!;
        Check(fallback.PointSize==12&&!fallback.Style.Origins.ContainsKey("size")&&fallback.Style.Origins.ContainsKey("complexSize")&&
            direct.PointSize==10&&direct.Style.Origins["size"].Location.PartUri=="/word/document.xml",
            "Le défaut latin et la déclaration directe gardent des provenances distinctes de la taille de script complexe.");

        using var complex=Compose(Write(root,"espacement-sur-cluster-compose",Paragraph(CharacterRun("e\u0301",smallTracking))+Section()));
        Check(complex.Snapshot.Coverage!=PageCoverage.Complete&&complex.Snapshot.Composition.Issues.Any(i=>
            i.Code=="LINE_TRACKING_CLUSTER_UNQUALIFIED"),"Le cluster complexe conserve son diagnostic.");
        using var doubled=Compose(Write(root,"soulignement-double",Paragraph(CharacterRun("A",
            """<w:u w:val="double" w:color="A01010"/>"""))+Section()));
        Complete(doubled.Snapshot,1);
        var rules=Line(doubled.Snapshot,"A").Native!.Underlines;
        Check(rules.Length==2&&rules.All(r=>r.Color=="A01010"&&r.Source.Location.PartUri=="/word/document.xml")&&
            rules[0].Ink.Bottom<rules[1].Ink.Top&&rules[0].Ink.Left==rules[1].Ink.Left,
            "Deux traits distincts de même origine et de même avance.");
        using var pattern=Compose(Write(root,"soulignement-ondule",Paragraph(CharacterRun("ABC","<w:u w:val=\"wave\"/>"))+Section()));
        Check(pattern.Snapshot.Coverage!=PageCoverage.Complete,"Le trait ondulé reste hors profil.");

        static string Table(string paragraph)=>"""
            <w:tbl><w:tblPr><w:tblLayout w:type="autofit"/></w:tblPr><w:tblGrid><w:gridCol/></w:tblGrid>
            <w:tr><w:tc>
            """+paragraph+"</w:tc></w:tr></w:tbl>";
    }

    internal static void FloatingPictures(string root)
    {
        using var image=new SKBitmap(20,20);
        image.Erase(SKColors.OrangeRed);
        using var encoded=image.Encode(SKEncodedImageFormat.Png,100);
        const string anchor="""
            <w:r><w:drawing xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
              xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
              <wp:anchor distL="114300" simplePos="0" relativeHeight="10" behindDoc="1" locked="1" layoutInCell="1" allowOverlap="1">
                <wp:simplePos x="0" y="0"/>
                <wp:positionH relativeFrom="column"><wp:posOffset>-698500</wp:posOffset></wp:positionH>
                <wp:positionV relativeFrom="page"><wp:posOffset>-12700</wp:posOffset></wp:positionV>
                <wp:extent cx="1270000" cy="1270000"/>
                <wp:effectExtent l="0" t="0" r="0" b="0"/>
                <wp:wrapNone/><wp:docPr id="1" name="Témoin"/><wp:cNvGraphicFramePr/>
                <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                  <pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="Image"/><pic:cNvPicPr/></pic:nvPicPr>
                    <pic:blipFill><a:blip r:embed="image"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                    <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="1270000" cy="1270000"/></a:xfrm>
                      <a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                  </pic:pic></a:graphicData></a:graphic>
              </wp:anchor></w:drawing></w:r>
            """;
        string path=Package("flottant",anchor);
        var input=DocumentInspector.InspectDocument(path);
        using var lease=PageComposer.Compose(input);
        Complete(lease.Snapshot,2);
        var pages=lease.Snapshot.Composition.Pages;
        Check(pages[1].Lines.Single().Text=="Témoin"&&pages[1].Lines.Single().Images.Count==0,
            "L’image reste distincte de la ligne de texte d’ancrage.");
        byte[] png=Composer.PagePng(pages[1]);
        File.WriteAllBytes(Path.Combine(root,"flottant.png"),png);
        using var raster=SKBitmap.Decode(png);
        using var earlier=SKBitmap.Decode(Composer.PagePng(pages[0]));
        Check(raster.GetPixel(2,2)==SKColors.OrangeRed&&earlier.GetPixel(2,2)==SKColors.White,
            "Position sur la page du paragraphe et écrêtage au papier.");
        using var front=Compose(Package("premier-plan",anchor.Replace("behindDoc=\"1\"","behindDoc=\"0\"",StringComparison.Ordinal)));
        Complete(front.Snapshot,2);
        using var above=SKBitmap.Decode(Composer.PagePng(front.Snapshot.Composition.Pages[1]));
        int covered=0;
        for(int y=12;y<35;y++)
        for(int x=12;x<57;x++)
            if(raster.GetPixel(x,y).Red<130&&above.GetPixel(x,y)==SKColors.OrangeRed)covered++;
        Check(covered>0,"La couche arrière laisse le texte visible et la couche avant le recouvre.");
        RefuseHiddenExports(input,root);
        Reject("habillage",anchor.Replace("wrapNone","wrapSquare",StringComparison.Ordinal));
        Reject("collision",anchor.Replace("allowOverlap=\"1\"","allowOverlap=\"0\"",StringComparison.Ordinal));
        Reject("liaison",anchor.Replace("r:embed","r:link",StringComparison.Ordinal));
        Reject("origine",anchor.Replace("relativeFrom=\"page\"","relativeFrom=\"paragraph\"",StringComparison.Ordinal));
        string relative="<wp14:sizeRelH relativeFrom=\"margin\"><wp14:pctWidth>0</wp14:pctWidth></wp14:sizeRelH>"+
            "<wp14:sizeRelV relativeFrom=\"margin\"><wp14:pctHeight>0</wp14:pctHeight></wp14:sizeRelV>";
        string sizing=anchor.Replace("<wp:anchor ","<wp:anchor xmlns:wp14=\"http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing\" ",StringComparison.Ordinal)
            .Replace("</wp:anchor>",relative+"</wp:anchor>",StringComparison.Ordinal);
        using var zero=Compose(Package("taille-relative-nulle",sizing));
        Complete(zero.Snapshot,2);
        Check(zero.Snapshot.Composition.Pages[1].FloatingPictures.Single().Picture.Image.Width==100,
            "Étendue absolue concordante des deux tailles relatives nulles.");
        Reject("taille-relative-active",sizing.Replace(">0</wp14:pctWidth>",">10000</wp14:pctWidth>",StringComparison.Ordinal));
        const string decorative="""
            <a:extLst><a:ext uri="{C183D7F6-B498-43B3-948B-1728B52AA6E4}">
              <adec:decorative xmlns:adec="http://schemas.microsoft.com/office/drawing/2017/decorative" val="1"/>
            </a:ext></a:extLst>
            """;
        var inline=anchor[..anchor.IndexOf("<wp:anchor",StringComparison.Ordinal)]+"<wp:inline>"+
            anchor[anchor.IndexOf("<wp:extent",StringComparison.Ordinal)..]
                .Replace("<wp:wrapNone/>","",StringComparison.Ordinal)
                .Replace("</wp:anchor>","</wp:inline>",StringComparison.Ordinal)
                .Replace("<wp:docPr id=\"1\" name=\"Témoin\"/>","<wp:docPr id=\"1\" name=\"Témoin\">"+decorative+"</wp:docPr>",StringComparison.Ordinal);
        using var ornament=Compose(Package("attribut-decoratif",inline));
        Complete(ornament.Snapshot,2);
        Check(ornament.Snapshot.Composition.Pages[1].Lines.SelectMany(l=>l.Images).Single().Image.Decorative==true,
            "Le dessin décoratif reste visible et garde sa propriété.");
        using var badOrnament=Compose(Package("attribut-decoratif-invalide",inline.Replace("val=\"1\"","val=\"invalide\"",StringComparison.Ordinal)));
        Check(badOrnament.Snapshot.Coverage==PageCoverage.Partial&&badOrnament.Snapshot.Composition.Issues.Any(i=>
            i.Code=="LAYOUT_IMAGE_DECORATIVE_UNQUALIFIED"),"Déclaration d’accessibilité invalide diagnostiquée.");

        void Reject(string name,string xml)
        {
            using var refused=Compose(Package(name,xml));
            Check(refused.Snapshot.Coverage!=PageCoverage.Complete&&refused.Snapshot.Composition.Issues.Any(i=>
                i.BlocksRendering&&i.Code is "LAYOUT_FLOATING_IMAGE_UNQUALIFIED" or "LAYOUT_LINKED_IMAGE_REFUSED"),"Flottant hors profil diagnostiqué.");
        }

        string Package(string name,string xml)=>Fixtures.Write(root,name+".docx",Fixtures.Parts(
            P("Première feuille")+Paragraph(xml+Run("Témoin"),"<w:pageBreakBefore/>")+Section(5500,6400),
            new(),Fixtures.Relationship("image","image","media/image.png"),new(){["word/media/image.png"]=encoded.ToArray()}));
    }

    internal static void FloatingTextFrames(string root)
    {
        string text=Paragraph(CharacterRun("Témoin","<w:color w:val=\"A80000\"/>"),"<w:jc w:val=\"center\"/>");
        const string hiddenLineUri="{91240B29-F687-4F45-9708-019B960494DF}";
        string frame=$"""
            <mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
              xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"
              xmlns:a14="http://schemas.microsoft.com/office/drawing/2010/main"
              xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
              xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
              xmlns:v="urn:schemas-microsoft-com:vml">
              <mc:Choice Requires="wps"><w:drawing>
                <wp:anchor simplePos="0" relativeHeight="2" behindDoc="0" allowOverlap="1">
                    <wp:simplePos x="0" y="0"/>
                    <wp:positionH relativeFrom="page"><wp:posOffset>254000</wp:posOffset></wp:positionH>
                    <wp:positionV relativeFrom="page"><wp:posOffset>254000</wp:posOffset></wp:positionV>
                    <wp:extent cx="1270000" cy="508000"/>
                    <wp:effectExtent l="0" t="0" r="0" b="0"/>
                    <wp:wrapNone/><wp:docPr id="2" name="Témoin"/><wp:cNvGraphicFramePr/>
                    <a:graphic><a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
                      <wps:wsp><wps:cNvSpPr txBox="1"/>
                      <wps:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="1270000" cy="508000"/></a:xfrm>
                        <a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/><a:ln w="6350"><a:noFill/></a:ln>
                        <a:extLst><a:ext uri="{hiddenLineUri}">
                          <a14:hiddenLine w="6350"><a:solidFill><a:prstClr val="black"/></a:solidFill></a14:hiddenLine>
                        </a:ext></a:extLst>
                      </wps:spPr><wps:txbx><w:txbxContent>{text}</w:txbxContent></wps:txbx>
                      <wps:bodyPr rot="0" spcFirstLastPara="0" vertOverflow="overflow" horzOverflow="overflow"
                        vert="horz" wrap="square" lIns="91440" rIns="91440" tIns="0" bIns="0" numCol="1" spcCol="0"
                        rtlCol="0" fromWordArt="0" anchor="ctr" anchorCtr="0" forceAA="0" compatLnSpc="1">
                          <a:prstTxWarp prst="textNoShape"><a:avLst/></a:prstTxWarp><a:noAutofit/>
                      </wps:bodyPr></wps:wsp>
                    </a:graphicData></a:graphic>
                </wp:anchor>
              </w:drawing></mc:Choice>
              <mc:Fallback><w:pict><v:shape><v:textbox><w:txbxContent>{text}</w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback>
            </mc:AlternateContent>
            """;
        string path=Write(root,"texte-flottant",Paragraph("<w:r>"+frame+"</w:r>")+Section(5500,6400));
        using var composed=Compose(path);
        Complete(composed.Snapshot,1);
        var page=composed.Snapshot.Composition.Pages[0];
        var placed=page.FloatingTextFrames.Single();
        var displayed=placed.Lines.Single();
        Check(displayed.Text=="Témoin"&&page.Lines.Single().Text.Length==0&&
            Close(placed.Frame.X,20)&&Close(placed.Frame.Y,20)&&displayed.NativeTop>30&&displayed.Baseline<55,
            "Texte de zone flottante centré et distinct du flux du corps.");
        using var drawn=SKBitmap.Decode(Composer.PagePng(page,2));
        int red=0;
        for(int y=40;y<120;y++)
        for(int x=40;x<240;x++)
            if(drawn.GetPixel(x,y) is {Red:>140 and <195,Green:<45,Blue:<45})red++;
        Check(red>10,"Le raster rend la couleur et la position du texte flottant.");

        string inside=frame.Replace("""<wp:positionH relativeFrom="page"><wp:posOffset>254000</wp:posOffset></wp:positionH>""",
            """<wp:positionH relativeFrom="margin"><wp:align>inside</wp:align></wp:positionH>""",StringComparison.Ordinal)
            .Replace("""<wp:positionV relativeFrom="page">""","""<wp:positionV relativeFrom="topMargin">""",StringComparison.Ordinal);
        using var facing=Compose(Write(root,"ancre-interieure",
            Paragraph("<w:r>"+inside+"</w:r>")+
            Paragraph("<w:r>"+inside.Replace("id=\"2\"","id=\"3\"")+"</w:r>","<w:pageBreakBefore/>")+Section(5500,6400)));
        Complete(facing.Snapshot,2);
        var odd=facing.Snapshot.Composition.Pages[0];
        var even=facing.Snapshot.Composition.Pages[1];
        Check(Close(odd.FloatingTextFrames.Single().Frame.X,odd.Geometry.Left)&&
            Close(even.FloatingTextFrames.Single().Frame.X,even.Geometry.Width-even.Geometry.Right-100),
            "L’alignement intérieur s’inverse sur le verso en gardant la largeur de la zone.");

        string byCharacter=frame.Replace("""<wp:positionH relativeFrom="page">""",
            """<wp:positionH relativeFrom="character">""",StringComparison.Ordinal)
            .Replace("""<wp:positionV relativeFrom="page">""","""<wp:positionV relativeFrom="line">""",StringComparison.Ordinal);
        using var anchored=Compose(Write(root,"ancre-caractere",
            Paragraph("<w:r>"+byCharacter+"</w:r>"+Run("Corps"),"<w:jc w:val=\"center\"/>")+Section(5500,6400)));
        Complete(anchored.Snapshot,1);
        var byCharacterPage=anchored.Snapshot.Composition.Pages.Single();
        var byCharacterClause=byCharacterPage.Lines.Single();
        var byCharacterFrame=byCharacterPage.FloatingTextFrames.Single().Frame;
        var centeredLine=byCharacterClause.Native!;
        float expectedCharacter=byCharacterClause.NativeOffsetX+
            PageComposer.Points(centeredLine.Region.Left+(centeredLine.Region.Width-centeredLine.Occupied!.Value)/2);
        Check(Close(byCharacterFrame.X,expectedCharacter+20)&&Close(byCharacterFrame.Y,byCharacterClause.NativeTop+20),
            "Références au caractère et à la ligne à partir de la position mesurée du contrôle d’ancrage. "+
            FormattableString.Invariant($"X={byCharacterFrame.X}, caractère={expectedCharacter}, Y={byCharacterFrame.Y}, ligne={byCharacterClause.NativeTop}."));

        string table="""
            <w:tbl><w:tblPr><w:tblW w:w="1400" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr>
              <w:tblGrid><w:gridCol w:w="1400"/></w:tblGrid>
              <w:tr><w:tc><w:tcPr><w:tcW w:w="1400" w:type="dxa"/><w:shd w:val="clear" w:fill="DDEEFF"/></w:tcPr>
                <w:p><w:r><w:t>Cellule</w:t></w:r></w:p>
              </w:tc></w:tr>
            </w:tbl>
            """;
        string content=Paragraph(Run("Avant"))+table+Paragraph(Run("Après"));
        string mixed=frame.Replace(text,content,StringComparison.Ordinal).Replace("508000","1270000",StringComparison.Ordinal);
        using var story=Compose(Write(root,"texte-flottant-histoire",Paragraph("<w:r>"+mixed+"</w:r>")+Section(5500,6400)));
        Complete(story.Snapshot,1);
        var sheet=story.Snapshot.Composition.Pages.Single();
        var composedFrame=sheet.FloatingTextFrames.Single();
        var cell=composedFrame.Cells.Single();
        var lines=composedFrame.Lines;
        Check(lines.Select(l=>l.Text).SequenceEqual(["Avant","Cellule","Après"])&&
            lines[0].NativeTop>=20&&lines[1].CellId==cell.CellId&&lines[2].Baseline>cell.Y+cell.Height&&
            sheet.DisplayCells.Single()==cell&&PageCoordinates.Clusters(lines[1]).Any(c=>c.Rect.Left>=cell.X),
            "Paragraphes et tableau appartiennent à la même histoire flottante et au même repère physique.");
        using var mixedRaster=SKBitmap.Decode(Composer.PagePng(sheet,2));
        Check(mixedRaster.GetPixel((int)((cell.X+cell.Width-2)*2),(int)((cell.Y+3)*2))==SKColor.Parse("#DDEEFF"),
            "Le fond de cellule est peint dans le calque de la zone de texte.");

        string vertical=frame.Replace("vert=\"horz\"","vert=\"vert270\"",StringComparison.Ordinal)
            .Replace("<wps:cNvSpPr txBox=\"1\"/>","<wps:cNvSpPr txBox=\"true\"><a:spLocks noChangeArrowheads=\"1\"/></wps:cNvSpPr>",StringComparison.Ordinal)
            .Replace("cx=\"1270000\" cy=\"508000\"","cx=\"508000\" cy=\"1270000\"",StringComparison.Ordinal);
        using var rotated=Compose(Write(root,"texte-flottant-vertical",Paragraph("<w:r>"+vertical+"</w:r>")+Section(5500,6400)));
        Complete(rotated.Snapshot,1);
        var rotatedPage=rotated.Snapshot.Composition.Pages.Single();
        var verticalFrame=rotatedPage.FloatingTextFrames.Single();
        var glyphs=PageCoordinates.Clusters(verticalFrame.Lines.Single()).ToArray();
        Check(verticalFrame.Frame.Rotation==-90&&glyphs.Length==6&&glyphs[0].Rect.Top>glyphs[^1].Rect.Top&&
            glyphs.All(g=>g.Rect.Left>=20&&g.Rect.Left+g.Rect.Width<=60&&g.Rect.Top>=20&&g.Rect.Top+g.Rect.Height<=120),
            "Le texte vertical se lit vers le haut dans le repère physique de sélection.");

        string narrow=vertical.Replace("anchor=\"ctr\"","anchor=\"t\"",StringComparison.Ordinal)
            .Replace("cx=\"508000\" cy=\"1270000\"","cx=\"190500\" cy=\"1270000\"",StringComparison.Ordinal);
        using var overflowing=Compose(Write(root,"texte-vertical-debordement",Paragraph("<w:r>"+narrow+"</w:r>")+Section(5500,6400)));
        Complete(overflowing.Snapshot,1);
        var overflowSheet=overflowing.Snapshot.Composition.Pages.Single();
        var overflow=overflowSheet.FloatingTextFrames.Single();
        Check(overflow.HasTextOverflow&&Close(overflow.Frame.Width,15)&&
            PageCoordinates.Clusters(overflow.Lines.Single()).Any(c=>c.Rect.Left+c.Rect.Width>overflow.Frame.X+overflow.Frame.Width),
            "Le débordement en vert270 conserve la boîte déclarée et les glyphes sélectionnables dans le repère de la feuille.");
        string outside=narrow.Replace("<wp:posOffset>254000</wp:posOffset>","<wp:posOffset>3429000</wp:posOffset>",StringComparison.Ordinal);
        using var offSheet=Compose(Write(root,"texte-vertical-hors-feuille",Paragraph("<w:r>"+outside+"</w:r>")+Section(5500,6400)));
        Complete(offSheet.Snapshot,1);
        var edge=offSheet.Snapshot.Composition.Pages.Single().FloatingTextFrames.Single();
        Check(Close(edge.Frame.X,270)&&!edge.OutsideSheet&&edge.Lines.SelectMany(PageCoordinates.Clusters)
            .All(c=>c.Rect.Left+c.Rect.Width<=275)&&offSheet.Snapshot.Composition.Issues.Any(i=>
                i is {Code:"PAGE_FLOATING_TEXTBOX_CLIPPED_BY_SHEET",BlocksRendering:false}),
            "L’ancre reste à 270 pt et la sélection s’arrête au bord de feuille.");
        using var hidden=Compose(Write(root,"texte-vertical-entierement-hors-feuille",Paragraph("<w:r>"+
            outside.Replace(">3429000</wp:posOffset>",">3810000</wp:posOffset>",StringComparison.Ordinal)+
            "</w:r>")+Section(5500,6400)));
        Complete(hidden.Snapshot,1);
        var hiddenPage=hidden.Snapshot.Composition.Pages.Single();
        Check(hiddenPage.FloatingTextFrames.Single() is { OutsideSheet:true, Frame.X:300 } preserved&&
            preserved.Content.Lines.Count>0&&!hiddenPage.DisplayLines.Any(l=>l.FrameTransform is not null),
            "Le dessin décentré garde sa géométrie et son contenu source sans cible visible.");
        using var hiddenRaster=SKBitmap.Decode(Composer.PagePng(hiddenPage));
        Check(hiddenRaster.Pixels.All(pixel=>pixel==SKColors.White),"La peinture d'une zone au-delà du papier est entièrement découpée.");

        string grow=frame.Replace("relativeFrom=\"page\"><wp:posOffset>254000","relativeFrom=\"column\"><wp:posOffset>0",StringComparison.Ordinal)
            .Replace("<wp:positionV relativeFrom=\"column\"","<wp:positionV relativeFrom=\"paragraph\"",StringComparison.Ordinal)
            .Replace("cx=\"1270000\" cy=\"508000\"","cx=\"317500\" cy=\"101600\"",StringComparison.Ordinal)
            .Replace("<wp:wrapNone/>","<wp:wrapSquare wrapText=\"bothSides\" distR=\"127000\"/>",StringComparison.Ordinal)
            .Replace("anchor=\"ctr\"","anchor=\"t\"",StringComparison.Ordinal)
            .Replace("wrap=\"square\"","wrap=\"none\"",StringComparison.Ordinal)
            .Replace("<a:noAutofit/>","<a:spAutoFit/>",StringComparison.Ordinal);
        using var grown=Compose(Write(root,"texte-flottant-auto-ajuste",Paragraph("<w:r>"+grow+"</w:r>"+Run("Texte du corps"))+Section(5500,6400)));
        Complete(grown.Snapshot,1);
        var grownPage=grown.Snapshot.Composition.Pages.Single();
        var grownFrame=grownPage.FloatingTextFrames.Single();
        var bodyGlyphs=PageCoordinates.Clusters(grownPage.Lines.Single()).ToArray();
        Check(grownFrame.Frame.Width>25&&grownFrame.Frame.Height>8&&grownFrame.Lines.Single().Text=="Témoin"&&
            bodyGlyphs.Min(g=>g.Rect.Left)>=grownFrame.Frame.X+grownFrame.Frame.Width+9.99f,
            "Le carré d’habillage occupe les dimensions finales de la zone auto-ajustée.");
        const string border="""
            <w:pPr><w:pBdr><w:top w:val="single" w:sz="8" w:space="1" w:color="101010"/>
            <w:left w:val="single" w:sz="8" w:space="1" w:color="101010"/>
            <w:bottom w:val="single" w:sz="8" w:space="1" w:color="101010"/>
            <w:right w:val="single" w:sz="8" w:space="1" w:color="101010"/>
            <w:between w:val="single" w:sz="8" w:space="18" w:color="AA2222"/></w:pBdr>
            <w:spacing w:before="140" w:after="20"/></w:pPr>
            """;
        string framedText="<w:p>"+border+Run("Haut")+"</w:p><w:p>"+border+Run("Bas")+"</w:p>";
        string framed=grow.Replace(text,framedText,StringComparison.Ordinal)
            .Replace("cx=\"317500\" cy=\"101600\"","cx=\"1143000\" cy=\"101600\"",StringComparison.Ordinal);
        using var borders=Compose(Write(root,"texte-auto-ajuste-bordures-jointes",Paragraph("<w:r>"+framed+"</w:r>")+Section(5500,6400)));
        Complete(borders.Snapshot,1);
        var borderSheet=borders.Snapshot.Composition.Pages.Single();
        var joined=borderSheet.FloatingTextFrames.Single().Lines;
        Check(joined.Count==2&&joined[0].ParagraphTopBorder is not null&&joined[0].ParagraphBorder is {Border.Color:"AA2222"} middle&&
            Close(middle.Top,joined[0].NativeTop+joined[0].Height+1)&&joined[1].ParagraphTopBorder is null&&
            joined[1].ParagraphBorder is {Border.Color:"101010"}&&joined[1].LeftBorder is {ExtendedTop:>0},
            "Le trait entre paragraphes prend l'espacement de paragraphe et les bords latéraux couvrent le raccord.");
        string mismatch=frame[..frame.IndexOf("<mc:Fallback>",StringComparison.Ordinal)]+
            frame[frame.IndexOf("<mc:Fallback>",StringComparison.Ordinal)..].Replace(">Témoin<",">Autre<",StringComparison.Ordinal);
        using var refused=Compose(Write(root,"texte-flottant-branches-divergentes",Paragraph("<w:r>"+mismatch+"</w:r>")+Section()));
        Check(refused.Snapshot.Coverage!=PageCoverage.Complete&&refused.Snapshot.Composition.Issues.Any(i=>
            i.Code=="LAYOUT_FLOATING_TEXTBOX_UNQUALIFIED"&&i.BlocksRendering),"Divergence de contenu entre les branches refusée.");
        RunningTextFrames(root);
    }

    static void RunningTextFrames(string root)
    {
        const string sectionHeader="""<w:headerReference w:type="default" r:id="h"/>""";
        string section=Section(5500,6400,margins:720).Replace("<w:sectPr>","<w:sectPr>"+sectionHeader,StringComparison.Ordinal)
            .Replace("w:header=\"120\"","w:header=\"720\"",StringComparison.Ordinal);
        string front=Rect("avant",7,"A80000","center","page","top","page",0);
        string back=Rect("arrière",-7,"0000A8","center","page","top","page",0);
        using var placed=Compose(Document("formes-entete-superposees",front+back));
        Complete(placed.Snapshot,1);
        var page=placed.Snapshot.Composition.Pages.Single();
        Check(page.RunningStories.Single().Region.Height==0&&page.FloatingTextFrames.Count==2&&
            page.FloatingTextFrames.All(f=>Close(f.Frame.X,(275-612)/2f)&&Close(f.Frame.Y,0)&&Close(f.Frame.Width,612)&&
                Close(f.Frame.Height,34.85f)&&f.Frame.VmlSource is not null&&f.Lines.Single().Text=="Repère")&&
            Close(page.Geometry.Top,36),
            "Les zones absolues gardent leurs dimensions et leur propre histoire malgré une marge d’en-tête nulle.");
        using var raster=SKBitmap.Decode(Composer.PagePng(page,2));
        Check(raster.Pixels.Any(p=>p is {Red:168,Green:0,Blue:0})&&
            !raster.Pixels.Any(p=>p is {Red:0,Green:0,Blue:168}),"Le calque avant peint au-dessus du calque arrière.");

        const string border="""<w:pBdr><w:top w:val="single" w:sz="8" w:space="1" w:color="007000"/></w:pBdr>""";
        using var decorated=Compose(Document("formes-entete-regions",Rect("marge",5,"A80000","left","margin","absolute","text",2,
            "180pt","filled=\"t\" fillcolor=\"#EEF5FF\" stroked=\"t\" strokecolor=\"#007000\" strokeweight=\"1pt\""),border));
        Complete(decorated.Snapshot,1);
        var decoratedPage=decorated.Snapshot.Composition.Pages.Single();
        var header=decoratedPage.RunningStories.Single();
        var frame=decoratedPage.FloatingTextFrames.Single();
        Check(Close(decoratedPage.Geometry.Top,37)&&Close(frame.Frame.X,36)&&
            Close(frame.Frame.Y,header.Lines.Single().NativeTop+2)&&
            frame.Frame.Fill==0xEEF5FF&&frame.Frame.Stroke is {Color:0x007000,Width:1}&&
            header.Lines.Single().ParagraphTopBorder is {Top:36}&&decoratedPage.Lines.Single().NativeTop>=37,
            "Les références de marge et de texte sont résolues dans le repère de la feuille. La bordure réserve sa hauteur.");

        using var outside=Compose(Document("forme-entete-hors-papier",Rect("hors-page",3,"A80000","center","page","absolute","page",400)));
        Complete(outside.Snapshot,1);
        Check(outside.Snapshot.Composition.Pages.Single().FloatingTextFrames.Single() is {OutsideSheet:true,Frame.Y:400}&&
            outside.Snapshot.Composition.Issues.Any(i=>i is {Code:"PAGE_FLOATING_TEXTBOX_CLIPPED_BY_SHEET",BlocksRendering:false}),
            "Un ancrage absolu d’en-tête hors papier garde son décalage et sa provenance.");

        const string vectors="""
            <w:r><w:pict xmlns:v="urn:schemas-microsoft-com:vml" xmlns:w10="urn:schemas-microsoft-com:office:word">
              <v:group coordsize="100,100" coordorigin="-50,-50"
                style="width:30pt;height:18pt;mso-position-horizontal-relative:char;mso-position-vertical-relative:line">
                <v:shape coordsize="50,100" style="position:absolute;left:-50;top:-50;width:50;height:100"
                  path="m0,l50,0r0,100l0,100xe" fillcolor="#A82B12" stroked="f"/>
                <v:shape coordsize="50,100" style="position:absolute;left:0;top:-50;width:50;height:100"
                  path="m0,0l50,0,50,100,0,100xe" fillcolor="#2455CD" stroked="f"/>
                <w10:anchorlock/>
              </v:group>
            </w:pict></w:r>
            """;
        using var drawnVectors=Compose(Document("formes-vml-lineaires",vectors));
        Complete(drawnVectors.Snapshot,1);
        var drawnPage=drawnVectors.Snapshot.Composition.Pages.Single();
        var image=drawnPage.DisplayLines.Single(l=>l.Images.Count>0).Images.Single();
        Check(image.Image.VmlVector is {Polygons.Length:2} group&&group.Polygons[0].Vertices[1]==new VmlVertex(15,0)&&
            group.Polygons[1].Vertices[2]==new VmlVertex(30,18)&&drawnPage.Geometry.Top>=54,
            "Le repère du groupe dimensionne le dessin et la réserve d’en-tête.");
        using var vectorPng=SKBitmap.Decode(Composer.PagePng(drawnPage,2));
        Check(vectorPng.GetPixel((int)((image.X+4)*2),(int)((image.Y+4)*2))==SKColor.Parse("#A82B12")&&
            vectorPng.GetPixel((int)((image.X+24)*2),(int)((image.Y+4)*2))==SKColor.Parse("#2455CD"),
            "Les sommets absolus et relatifs donnent les couleurs attendues sur la même feuille.");
        using var unknownPath=Compose(Document("forme-vml-chemin-non-qualifie",vectors.Replace("r0,100","c0,100",StringComparison.Ordinal)));
        Check(unknownPath.Snapshot.Coverage!=PageCoverage.Complete&&unknownPath.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_VML_PICTURE_UNQUALIFIED"&&i.BlocksRendering),"Un chemin incompris conserve un diagnostic.");

        string Document(string name,string drawings,string properties="")=>Fixtures.Write(root,name+".docx",
            Fixtures.Parts(P("Corps")+section,new(){["word/header1.xml"]=Fixtures.Xml("hdr",Paragraph(drawings,properties))},
                Fixtures.Relationship("h","header","header1.xml")));

        static string Rect(string id,int z,string color,string h,string hBasis,string v,string vBasis,int y,string width="612pt",
            string paint="filled=\"f\" stroked=\"f\"")=>$"""
            <w:r><w:pict xmlns:v="urn:schemas-microsoft-com:vml" xmlns:w10="urn:schemas-microsoft-com:office:word">
              <v:rect id="{id}" {paint} alt="Height:9999999.0"
                style="position:absolute;width:{width};height:34.85pt;z-index:{z};mso-position-horizontal:{h};mso-position-horizontal-relative:{hBasis};mso-position-vertical:{v};mso-position-vertical-relative:{vBasis};margin-top:{y}pt;v-text-anchor:middle">
                <v:textbox inset="0,0,0,0"><w:txbxContent>{Paragraph(CharacterRun("Repère",$"""<w:color w:val="{color}"/>"""),"<w:jc w:val=\"center\"/>")}</w:txbxContent></v:textbox>
                <w10:wrap type="none"/>
              </v:rect>
            </w:pict></w:r>
            """;
    }
}
