using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

internal static class FlowEngineCases
{
    internal static void TableFootnote(string root)
    {
        const string note="""
            <w:r><w:rPr><w:u w:val="single" w:color="A23344"/><w:vertAlign w:val="superscript"/></w:rPr>
            <w:footnoteReference w:id="2"/></w:r>
            """;
        const string table="""
            <w:tbl><w:tblPr><w:tblW w:w="5000" w:type="pct"/><w:tblLayout w:type="autofit"/></w:tblPr>
            <w:tblGrid><w:gridCol w:w="2300"/><w:gridCol w:w="2000"/></w:tblGrid>
            <w:tr><w:trPr><w:tblHeader/></w:trPr>
            <w:tc><w:p><w:r><w:t>Nom</w:t></w:r></w:p></w:tc>
            <w:tc><w:p><w:r><w:t>Valeur</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:trPr><w:cantSplit/></w:trPr>
            <w:tc><w:p><w:r><w:t>Cellule avec appel</w:t></w:r>NOTE</w:p></w:tc>
            <w:tc><w:p><w:r><w:t>12</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """;
        var parts=new Dictionary<string,string>
        {
            ["word/styles.xml"]=Fixtures.Xml("styles","""
                <w:style w:type="paragraph" w:styleId="Note"><w:pPr><w:spacing w:beforeAutospacing="1" w:afterAutospacing="1"/></w:pPr></w:style>
                """),
            ["word/footnotes.xml"]=Fixtures.Xml("footnotes","""
                <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
                <w:footnote w:id="2"><w:p><w:pPr><w:pStyle w:val="Note"/>
                  <w:spacing w:before="0" w:after="0" w:beforeAutospacing="0" w:afterAutospacing="0"/></w:pPr>
                  <w:r><w:footnoteRef/><w:t xml:space="preserve"> Source du tableau</w:t></w:r></w:p></w:footnote>
                """)
        };
        string path=Fixtures.Write(root,"note-de-cellule.docx",Fixtures.Parts(
            P(R("Bloc qui occupe la première feuille"),"""<w:spacing w:line="3730" w:lineRule="exact"/>""")+
            table.Replace("NOTE",note,StringComparison.Ordinal)+Section(""),parts,
            Fixtures.Relationship("note","footnotes","footnotes.xml")+Fixtures.Relationship("styles","styles","styles.xml")));
        using(var sdk=WordprocessingDocument.Open(path,false))
        {
            var errors=new OpenXmlValidator().Validate(sdk).Select(e=>e.Description).ToArray();
            Check(errors.Length==0,"Le témoin de note de tableau doit être valide. "+string.Join(" ",errors));
        }
        using var lease=PageComposer.Compose(DocumentInspector.InspectDocument(path));
        var snapshot=Complete(lease);
        Check(snapshot.Pages.Length==2&&snapshot.Pages[0].Geometry.Footnotes.Count==0&&snapshot.Pages[0].Geometry.Cells.Count==0,
            "L’en-tête de tableau, le premier groupe et la note doivent changer de feuille ensemble.");
        var page=snapshot.Pages[1].Geometry;
        var cell=page.Lines.Single(l=>l.Text=="Cellule avec appel");
        var marker=cell.Native!.Generated.Single(n=>n.Kind=="noteNumber");
        var printed=page.Footnotes.Single(n=>n.Call is not null);
        Check(cell.CellId is not null&&printed.Call==marker.Cause&&marker.Fragment.Style.BaselineShift>0&&
            cell.Native.Underlines.Single() is {Color:"A23344",Ink.Bottom:<0}&&
            page.Footnotes[0].Call is null&&printed.Lines.Single().Text.Contains("Source du tableau",StringComparison.Ordinal)&&
            PageComposer.Points(page.Footnotes[0].Region.Top)>=page.Cells.Max(c=>c.Y+c.Height),
            "Le même appel doit fournir le numéro de la cellule et la note réservée hors du tableau.");
    }

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        const string equal="""<w:cols w:num="2" w:space="400"/>""";
        const string unequal="""<w:cols w:equalWidth="0"><w:col w:w="1800" w:space="400"/><w:col w:w="3000"/></w:cols>""";
        const string column="""<w:r><w:br w:type="column"/></w:r>""";
        const string noBalance="<w:compat><w:noColumnBalance/></w:compat>";
        using var pixels=new SKBitmap(40,40);
        pixels.Erase(SKColors.Blue);
        using(var canvas=new SKCanvas(pixels))
        using(var paint=new SKPaint{Color=SKColors.Red})
        {
            canvas.DrawRect(new SKRect(0,0,20,20),paint);
            paint.Color=SKColors.Lime;
            canvas.DrawRect(new SKRect(20,0,40,20),paint);
        }
        using var encoded=pixels.Encode(SKEncodedImageFormat.Png,100);
        byte[] media=encoded.ToArray();

        const string reference="""
        <w:p><w:r><w:t xml:space="preserve">Voir </w:t></w:r><w:fldSimple w:instr="PAGEREF cible"/></w:p>
        """;
        const string marked="""
        <w:bookmarkStart w:id="3" w:name="cible"/><w:r><w:t>Cible</w:t></w:r><w:bookmarkEnd w:id="3"/>
        """;
        string combined=reference+P(R("Titre pleine largeur"),Section(""))+
            P(R("Colonne de gauche")+column+R("Colonne de droite"))+
            P(Picture(floating:true)+marked)+Table(7)+P(R("Fin"),Section(equal,"continuous"))+
            P(R("Section impaire"))+Section("", "oddPage");
        using(var lease=Fixture("flux-combine",combined,noBalance,withImage:true))
        {
            var s=Complete(lease);
            Check(s.Pages.Length==3&&s.Pages[^1].Geometry.Lines.Single().Text=="Section impaire","Columns, table spill and odd section");
            var first=s.Pages[0].Geometry;
            var text=first.Lines.Where(l=>l.Text.StartsWith("Colonne",StringComparison.Ordinal)).ToArray();
            Check(text.Length==2&&text[0].FlowRegion is {Band:1,Column:0}&&text[1].FlowRegion is {Band:1,Column:1}&&
                Close(text[0].NativeOffsetX,30)&&Close(text[1].NativeOffsetX,170)&&
                Close(text[0].NativeTop,text[1].NativeTop)&&text[0].NativeTop>first.Lines[1].NativeTop,"Two-column band under the title");
            var leading=first.Lines.Single(l=>l.Text=="Cible");
            var anchor=first.FloatingPictures.Single();
            Check(anchor.ParagraphId==leading.ParagraphId&&Close(anchor.X,180)&&Close(anchor.Y,90),"Floating picture uses its actual column");
            var label=first.Lines[0].Native!.Generated.Single(g=>g.Kind=="pageField");
            Check(label.Fragment.Text=="1"&&s.FieldBookmarks.Values.Single() is {Page:1,Left:>=170},"PAGEREF follows physical bookmark, with column coordinates");
            Check(s.Pages.SelectMany(p=>p.Geometry.Cells).Any(c=>c.IsRepeated)&&
                s.Pages.SelectMany(p=>p.Geometry.Cells).All(c=>c.FlowRegion is {} r&&c.X>=PageComposer.Points(r.Bounds.Left)-.01f),
                "Repeated table title positioned in its new column or sheet");
            foreach(var page in s.Pages)
                File.WriteAllBytes(Path.Combine(root,$"flux-{page.Id.PhysicalIndex}.png"),Composer.PagePng(page.Geometry,1.5f));
        }
        using(var lease=Fixture("largeurs-inegales",
            P(R("Milieu")+column+R("Suite"),"<w:jc w:val=\"center\"/>")+Section(unequal),noBalance))
        {
            var s=Complete(lease);
            var lines=s.Pages.Single().Geometry.Lines;
            Check(lines[0].FlowRegion?.Bounds.Width==PageComposer.Emu(90)&&lines[1].FlowRegion?.Bounds.Width==PageComposer.Emu(150)&&
                Close(lines[1].NativeOffsetX,140)&&PageCoordinates.Clusters(lines[1]).First().Rect.Left>190,"Unequal widths remeasure centered line positions");
        }
        using(var lease=Fixture("section-colonne-suivante",
            P(R("Avant"),Section(equal))+P(R("Après"))+Section(equal,"nextColumn"),noBalance))
        {
            var l=Complete(lease).Pages.Single().Geometry.Lines;
            Check(l[0].FlowRegion is {Column:0,Section:1}&&l[1].FlowRegion is {Column:1,Section:2}&&Close(l[0].NativeTop,l[1].NativeTop),
                "Section starts on the next column on the same sheet");
        }
        using(var lease=Fixture("equilibre-puis-bande",
            string.Concat(Enumerable.Range(0,6).Select(i=>Fixtures.Paragraph("Ligne "+i)))+
            P(R("Ligne 6"),Section(equal))+Fixtures.Paragraph("Après la bande")+Section("","continuous"),""))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var body=page.Lines.Where(l=>l.FlowRegion is not null).ToArray();
            Check(body.Count(l=>l.FlowRegion?.Column==0)==4&&body.Count(l=>l.FlowRegion?.Column==1)==3&&
                page.Lines[^1].NativeTop>=body.Max(l=>l.NativeTop+l.Height)-.01f,"Balanced 4/3 and following full-width band");
        }
        var multipage=Enumerable.Range(1,23).Select(i=>$"Paragraphe {i:00}").ToArray();
        const string fixedLine="""<w:spacing w:line="500" w:lineRule="exact"/>""";
        using(var lease=Fixture("equilibre-dernière-feuille",
            string.Concat(multipage.Take(22).Select(s=>P(R(s),fixedLine)))+
            P(R(multipage[^1]),fixedLine+Section(equal))+P(R("Après les colonnes"))+Section("","continuous"),""))
        {
            var pages=Complete(lease).Pages;
            var final=pages[^1].Geometry;
            var columns=final.Lines.Where(l=>l.FlowRegion is not null).ToArray();
            Check(pages.Length==2&&pages[0].Geometry.Lines.Count==14&&columns.Count(l=>l.FlowRegion!.Value.Column==0)==5&&
                columns.Count(l=>l.FlowRegion!.Value.Column==1)==4&&
                final.Lines[^1].NativeTop>=columns.Max(l=>l.NativeTop+l.Height)-.01f&&
                pages.SelectMany(p=>p.Geometry.Lines.Where(l=>l.FlowRegion is not null)).Select(l=>l.Text).SequenceEqual(multipage),
                "Full preceding sheet, height-balanced final sheet and continuous full-width band");
        }
        using(var lease=Fixture("image-rognage-retournement",P(Picture(floating:false))+Section(""),"",withImage:true))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var line=page.Lines.Single();
            var image=line.Images.Single();
            Check(Close(image.Image.ReservedHeight,23)&&
                Close(PageComposer.Points(line.Native!.Boxes.Single().Width),20),"Effect extent consumed by line measure");
            using var raster=SKBitmap.Decode(Composer.PagePng(page));
            Check(raster.GetPixel((int)image.X+4,(int)image.Y+8)==SKColors.Lime&&
                raster.GetPixel((int)image.X+16,(int)image.Y+8)==SKColors.Red,"Source crop and horizontal flip painted without the cropped blue area");
        }
        string scaled=Picture(floating:false)
            .Replace("""<a:ext cx="254000" cy="254000"/>""","""<a:ext cx="508000" cy="508000"/>""");
        using(var lease=Fixture("image-echelles-coherentes",P(scaled)+Section(""),"",withImage:true))
        {
            var snapshot=Complete(lease);
            var image=snapshot.Pages.Single().Geometry.Lines.Single().Images.Single();
            Check(Close(image.Image.Width,20)&&Close(image.Image.Height,20)&&Close(image.Image.ReservedHeight,23)&&image.Image.FlipX&&
                snapshot.Composition.Issues.Any(i=>i.Code=="LAYOUT_INLINE_PICTURE_RESCALED"&&!i.BlocksRendering),
                "The uniform inline resize preserves crop, reflection and the final effect extent.");
        }
        using(var lease=Fixture("image-echelles-incompatibles",
            P(scaled.Replace("cy=\"508000\"","cy=\"507000\"",StringComparison.Ordinal))+Section(""),"",withImage:true))
            Refused(lease,"LAYOUT_IMAGE_SIZE_CONFLICT");
        LegacyPreviews(root,media);
        VectorPreviews(root);
        using(var lease=Fixture("taquet-signé",P(R("I")+"""<w:r><w:tab/></w:r>"""+R("Suite"),"""
            <w:tabs><w:tab w:val="left" w:pos="-200"/></w:tabs><w:ind w:left="-400"/>
            """)+Section(""),""))
        {
            var line=Complete(lease).Pages.Single().Geometry.Lines.Single();
            var tab=line.Native!.Tabs.Single();
            Check(tab.Stop.Position==PageComposer.Emu(-10)&&Close(line.Tabs.Single().To,20),
                "Le taquet signé est mesuré par rapport à la région et reste dans le papier.");
        }
        using(var lease=Fixture("marges-de-rangée",
            Table(2).Replace("<w:tr><w:trPr><w:trHeight w:val=\"880\"",
                """
                <w:tr><w:tblPrEx><w:tblCellMar><w:left w:type="dxa" w:w="300"/><w:right w:type="dxa" w:w="100"/></w:tblCellMar></w:tblPrEx><w:trPr><w:trHeight w:val="880"
                """,StringComparison.Ordinal)+Section(""),""))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            Check(page.Lines.Count==2&&page.Cells.Count==2&&Close(page.Cells[0].Width,page.Cells[1].Width)&&
                Close(page.Lines[1].NativeOffsetX-page.Cells[1].X,15)&&page.Lines[0].NativeOffsetX-page.Cells[0].X<15,
                "L’exception de rangée modifie la marge interne sans modifier la grille commune.");
        }
        using(var lease=Fixture("signet-entre-blocs",
            """<w:bookmarkStart w:id="4" w:name="ensemble"/>"""+P(R("Avant"))+
            """<w:bookmarkEnd w:id="4"/>"""+P(R("Après"))+
            Section("").Replace("</w:sectPr>","<w:noEndnote/></w:sectPr>",StringComparison.Ordinal),""))
        {
            var snapshot=Complete(lease);
            Check(snapshot.Pages.Single().Geometry.Lines.Select(l=>l.Text).SequenceEqual(new[]{"Avant","Après"})&&
                snapshot.Semantic!.References.Any(r=>r.Kind=="bookmarkEnd"&&r.Status==ReferenceStatus.Resolved)&&
                snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_ENDNOTE_SUPPRESSION_INACTIVE"&&!i.BlocksRendering),
                "Le signet de corps garde sa cible et ne crée aucune ligne. La suppression est inactive sans appel de note de fin.");
        }
        using(var lease=Fixture("tableau-trait-union-insecable",Table(1)
            .Replace("Rangée 0</w:t></w:r>","A</w:t><w:noBreakHyphen/><w:t>B</w:t></w:r>",StringComparison.Ordinal)+
            Section(""),""))
        {
            var line=Complete(lease).Pages.Single().Geometry.Lines.Single();
            Check(line.CellId is not null&&line.Text=="A\u2011B"&&line.Native!.Generated
                .Single(g=>g.Kind=="noBreakHyphen") is {Fragment.Measure.Metrics.Width:>0},
                "Le trait d’union insécable participe à la largeur intrinsèque et au dessin de la cellule.");
        }
        string exclusion=Picture(floating:true)
            .Replace("simplePos=\"0\"", "distT=\"38100\" distB=\"63500\" simplePos=\"0\"")
            .Replace("relativeFrom=\"page\"><wp:posOffset>1143000", "relativeFrom=\"paragraph\"><wp:posOffset>0")
            .Replace("wrapNone","wrapTopAndBottom");
        const string nearEnd="<w:spacing w:line=\"3500\" w:lineRule=\"exact\"/>";
        using(var lease=Fixture("flottant-avec-exclusion",P(R("Avant"),nearEnd)+
            P(exclusion+R("Après l’image"))+Section(equal),noBalance,withImage:true))
        {
            var s=Complete(lease);
            var first=s.Pages.Single().Geometry;
            var p=first.FloatingPictures.Single();
            var l=first.Lines.Single(x=>x.Text=="Après l’image");
            Check(l.FlowRegion is {Column:1}&&Close(p.X,180)&&Close(p.Y,23)&&
                Close(l.NativeTop,48)&&p.ExcludedRegion is {} r&&Close(PageComposer.Points(r.Height),28),
                "The wrap reserve moves with the anchor and bars text on both sides");
        }
        const string longWords="La ligne change de largeur et garde tous ses caractères. "+
            "Le texte doit poursuivre dans la colonne étroite puis retourner dans la colonne large avec les mêmes mots. "+
            "Une autre phrase poursuit la mesure, avec une césure et des espaces visibles. "+
            "Dans cet exemple le même paragraphe traverse plusieurs régions avant de finir.";
        const string wideFirst="""<w:cols w:equalWidth="0"><w:col w:w="3000" w:space="400"/><w:col w:w="1800"/></w:cols>""";
        const string noWidow="""<w:widowControl w:val="0"/>""";
        string square=Picture(floating:true)
            .Replace("relativeFrom=\"page\"><wp:posOffset>1143000","relativeFrom=\"paragraph\"><wp:posOffset>0")
            .Replace("<wp:wrapNone/>","<wp:wrapSquare wrapText=\"right\" distL=\"12700\" distR=\"38100\" distB=\"190500\"/>");
        using(var lease=Fixture("habillage-carre",P(square+R(longWords),noWidow)+Section(wideFirst),noBalance,withImage:true))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var box=page.FloatingPictures.Single().ExcludedRegion!.Value;
            var side=page.Lines.Where(l=>l.FlowRegion is {Column:0}&&PageComposer.Emu(l.NativeTop)<box.Top+box.Height).ToArray();
            var full=page.Lines.Skip(side.Length).ToArray();
            Check(side.Length>1&&full.Length>0&&Close(page.FloatingPictures.Single().Y,side[0].NativeTop)&&
                Close(PageComposer.Points(box.Width),24)&&Close(PageComposer.Points(box.Height),35),"Square uses the placed anchor and wrap distances");
            Check(side.All(l=>l.Native!.Region.Left==PageComposer.Emu(33)&&
                PageCoordinates.Clusters(l).All(c=>PageComposer.Emu(c.Rect.Left)>=box.Left+box.Width-PageComposer.Emu(.01f)))&&
                full.All(l=>l.Native!.Region.Left==0)&&
                string.Concat(page.Lines.SelectMany(l=>l.Native!.Parts).Select(p=>p.Text))==longWords,
                "One paragraph resumes from the same source at both sides of the exclusion's bottom");
            File.WriteAllBytes(Path.Combine(root,"habillage-carre.png"),Composer.PagePng(page,1.5f));
        }
        using(var lease=Fixture("habillage-deux-cotes",P(square.Replace("wrapText=\"right\"","wrapText=\"bothSides\"")+R(longWords),noWidow)+
            Section(""),"",withImage:true))
            Refused(lease,"PAGE_FLOW_SIDE_TWO_CORRIDORS_UNQUALIFIED");
        string squareFromMargin=square.Replace("wrapText=\"right\"","wrapText=\"bothSides\"")
            .Replace(">127000</wp:posOffset>",">-127000</wp:posOffset>")
            .Replace(">0</wp:posOffset>",">635</wp:posOffset>");
        using(var lease=Fixture("habillage-deux-cotes-depuis-marge",P(squareFromMargin+R(longWords),
            """<w:widowControl/>""")+Section(""),"",withImage:true))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var image=page.FloatingPictures.Single();
            var first=page.Lines.First();
            var box=image.ExcludedRegion!.Value;
            Check(Close(image.X,20)&&Close(image.Y,first.NativeTop+.05f)&&
                Close(PageComposer.Points(box.Left),30)&&Close(PageComposer.Points(box.Width),13)&&
                first.Native!.Region.Left==PageComposer.Emu(13)&&page.Lines.Last().Native!.Region.Left==0&&
                string.Concat(page.Lines.SelectMany(l=>l.Native!.Parts).Select(p=>p.Text))==longWords,
                "The part of the drawing in the margin and the sole surviving text lane share the anchor offset.");
        }
        const string marginTab="""<w:tabs><w:tab w:val="right" w:pos="5207" w:leader="dot"/></w:tabs>""";
        string tabbed=R("Repère")+"<w:r><w:tab/></w:r>"+R("14");
        using(var lease=Fixture("taquet-dans-marge",P(tabbed,marginTab)+Section(""),""))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var line=page.Lines.Single();
            var tab=line.Native!.Tabs.Single();
            Check(Close(PageComposer.Points(tab.Stop.Position),260.35f)&&
                line.Native.Occupied==PageComposer.Emu(260.35f)&&
                line.Native.Generated.Any(d=>d.Kind=="leader"),
                "The authored right stop stays in the paper margin with measured leaders.");
        }
        using(var lease=Fixture("taquet-hors-feuille",P(tabbed,marginTab.Replace("5207","6200",StringComparison.Ordinal))+Section(""),""))
            Refused(lease,"LINE_OVERFLOW");
        using(var lease=Fixture("taquet-et-image-en-marge",P(squareFromMargin+"<w:r><w:tab/></w:r>",
            """<w:tabs><w:tab w:val="right" w:pos="5000"/></w:tabs>""")+Section(""),"",withImage:true))
        {
            var line=Complete(lease).Pages.Single().Geometry.Lines.Single();
            var native=line.Native!;
            Check(native.Region.Left==PageComposer.Emu(13)&&native.Tabs.Single().To==PageComposer.Emu(250),
                "A fixed tab retains its margin origin when a float narrows the line.");
        }
        const string sideIndent="""<w:ind w:left="40"/>""";
        using(var lease=Fixture("groupe-solidaire-pres-image",
            P(squareFromMargin+R("Ancre"),sideIndent)+P(R("Titre solidaire"),"<w:keepNext/><w:keepLines/>"+sideIndent)+
            P(R("Paragraphe qui suit le titre."),sideIndent)+Section(""),"",withImage:true))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            Check(page.Lines.Select(l=>l.Text).SequenceEqual(new[]{"Ancre","Titre solidaire","Paragraphe qui suit le titre."})&&
                page.Lines[1].Native!.Region.Left==PageComposer.Emu(13),
                "A kept group is measured in the corridor before committing its first line.");
        }
        const string polygon="""<wp:wrapPolygon edited="1"><wp:start x="0" y="0"/><wp:lineTo x="21600" y="21600"/><wp:lineTo x="0" y="21600"/><wp:lineTo x="0" y="0"/></wp:wrapPolygon>""";
        string polygonPicture=Picture(floating:true)
            .Replace("relativeFrom=\"page\"><wp:posOffset>1143000","relativeFrom=\"paragraph\"><wp:posOffset>0")
            .Replace("cx=\"254000\" cy=\"254000\"","cx=\"508000\" cy=\"762000\"")
            .Replace(" flipH=\"1\"","").Replace("<a:srcRect b=\"50000\"/>","");
        using var triangle=new SKBitmap(40,40);
        triangle.Erase(SKColors.Transparent);
        for(int y=0;y<40;y++)
            for(int x=0;x<=y;x++)triangle.SetPixel(x,y,SKColors.Navy);
        using var trianglePng=triangle.Encode(SKEncodedImageFormat.Png,100);
        foreach(string kind in new[]{"wrapTight","wrapThrough"})
        using(var lease=Fixture("contour-"+kind,P(polygonPicture.Replace("<wp:wrapNone/>",
            $"<wp:{kind} wrapText=\"right\" distR=\"25400\">{polygon}</wp:{kind}>")+R(longWords),
            noWidow+"""<w:spacing w:line="280" w:lineRule="exact"/>""")+Section(wideFirst),noBalance,
            withImage:true,pictureBytes:trianglePng.ToArray()))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var offsets=page.Lines.TakeWhile(l=>l.FlowRegion is {Column:0}&&l.NativeTop<80).Select(l=>l.Native!.Region.Left).ToArray();
            Check(offsets.Length==5&&offsets[^1]==PageComposer.Emu(52)&&
                (kind=="wrapTight"?offsets.All(x=>x==offsets[0]):offsets.Distinct().Count()==5)&&
                string.Concat(page.Lines.SelectMany(l=>l.Native!.Parts).Select(p=>p.Text))==longWords,
                "The projected contour determines the exact horizontal region of each recomposed line");
            File.WriteAllBytes(Path.Combine(root,kind+".png"),Composer.PagePng(page,1.5f));
        }
        string squareSecond=square.Replace("docPr id=\"1\"","docPr id=\"2\"");
        using(var lease=Fixture("collision-interdite",P(square.Replace("allowOverlap=\"1\"","allowOverlap=\"0\"")+R("Premier"),noWidow)+
            P(squareSecond+R("Second"),noWidow)+Section(""),"",withImage:true))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var first=page.FloatingPictures[0];
            var second=page.FloatingPictures[1];
            Check(Close(first.Y+first.Picture.Image.Height,second.Y)&&page.Lines.Count==2,
                "The second picture starts below the first picture, with its paragraph");
        }
        using(var lease=Fixture("collision-autorisee",P(square+R("Premier"),noWidow)+
            P(squareSecond+R("Second"),noWidow)+Section(""),"",withImage:true))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var first=page.FloatingPictures[0];
            var second=page.FloatingPictures[1];
            Check(second.Y<first.Y+first.Picture.Image.Height&&
                page.Lines.All(l=>l.Native!.Region.Left==PageComposer.Emu(33)),
                "Overlapping permitted pictures keep their anchors and text clears both");
        }
        using(var lease=Fixture("equilibre-inegal",
            P(R(longWords))+P(R("Après la bande"),Section(wideFirst))+P(R("Une colonne"))+Section("","continuous"),""))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var split=page.Lines.Where(l=>l.FlowRegion is not null).ToArray();
            Check(split.Select(l=>l.Native!.Region.Width).Distinct().Count()==2&&
                string.Concat(split.Select(l=>l.Text)).Replace(" ","").Contains(longWords.Replace(" ",""))&&
                page.Lines[^1].NativeTop>=split.Max(l=>l.NativeTop+l.Height)-.01f,"Different measured widths balance before the next band");
        }
        foreach(var widths in new[]{wideFirst,unequal})
        using(var lease=Fixture(widths==wideFirst?"large-puis-etroite":"etroite-puis-large",
            P(R(longWords),"""<w:spacing w:line="720" w:lineRule="exact"/>""")+Section(widths),noBalance))
        {
            var s=Complete(lease);
            var actual=s.Pages.SelectMany(p=>p.Geometry.Lines).ToArray();
            Check(actual.Select(l=>l.FlowRegion!.Value.Bounds.Width).Distinct().Count()==2,
                "One paragraph must advance into both widths");
            Check(actual.All(l=>l.Native!.Region.Width==l.FlowRegion!.Value.Bounds.Width)&&
                string.Concat(actual.SelectMany(l=>l.Native!.Parts).Select(p=>p.Text))==longWords,
                "Committed lines carry the current width and reconstruct the original UTF-16 text");
            Check(actual.Select(l=>l.Native!).Zip(actual.Select(l=>l.Native!).Skip(1)).All(pair=>
                pair.First.ContextRange.EndExclusive==pair.Second.ContextRange.Start&&pair.First.Index+1==pair.Second.Index),
                "Contiguous intervals and line ordinals across automatic region advances");
        }
        const string astral="\U0001D434";
        const string joined="e\u0301";
        using(var lease=Fixture("clusters-et-taquet",
            P(R("Page")+"""<w:r><w:tab/></w:r>"""+R(longWords)+$"""<w:r><w:t xml:space="preserve"> {astral} </w:t></w:r>"""+
                R("e")+R("\u0301")+"""<w:r><w:t xml:space="preserve"> fin</w:t></w:r>""",
                """<w:tabs><w:tab w:val="left" w:pos="900"/></w:tabs><w:spacing w:line="660" w:lineRule="exact"/>""")+
            Section(unequal),noBalance))
        {
            var lines=Complete(lease).Pages.SelectMany(p=>p.Geometry.Lines).Select(l=>l.Native!).ToArray();
            string context="Page\uFFFC"+longWords+" "+astral+" "+joined+" fin";
            Check(lines.Select(l=>l.Region.Width).Distinct().Count()==2&&
                lines.SelectMany(l=>l.Tabs).Single().Stop.Position==900*635&&
                string.Concat(lines.SelectMany(l=>l.Parts).Select(p=>p.Text))==context.Replace("\uFFFC","")&&
                lines.All(l=>l.ContextRange.EndExclusive!=context.IndexOf(astral,StringComparison.Ordinal)+1&&
                    l.ContextRange.EndExclusive!=context.IndexOf(joined,StringComparison.Ordinal)+1),
                "The tab and grapheme boundaries survive a source-based column reflow");
        }
        string widowSource=string.Join(" ",Enumerable.Repeat("Étiquette",11));
        using(var lease=Fixture("veuve-après-recomposition",P(R(widowSource),
            """<w:widowControl/><w:spacing w:line="400" w:lineRule="exact"/>""")+Section(unequal),noBalance))
        {
            var lines=Complete(lease).Pages.Single().Geometry.Lines;
            Check(lines.Count(l=>l.FlowRegion is {Column:0})==8&&lines.Count(l=>l.FlowRegion is {Column:1})==2&&
                string.Concat(lines.SelectMany(l=>l.Native!.Parts).Select(p=>p.Text))==widowSource,
                "The tentative cut preserves two remaining lines after recomposition in the wider column");
        }
        using(var lease=Fixture("redecoupe-avec-champ",P(R("Première colonne")+column+
            R("Quatre petits mots tiennent ici et encore ici"))+
            reference+P(marked)+Section(unequal),noBalance))
        {
            var s=Complete(lease);
            Check(s.FieldStability=="Stable"&&s.FieldBookmarks.Values.Single() is {Page:1,Left:>=140}&&
                s.Pages[0].Geometry.Lines.SelectMany(l=>l.Native!.Generated).Single(g=>g.Kind=="pageField").Fragment.Text=="1",
                "The field settles on the reflowed geometry with its logical bookmark");
        }
        using(var lease=Fixture("checkpoint-feuille-et-champ",Table(2)+
            reference.Replace("<w:p>","<w:p><w:pPr><w:pageBreakBefore/></w:pPr>")+P(marked)+Section(""),""))
        {
            var s=Complete(lease);
            Check(s.Pages.Length==2&&s.Pages[0].Geometry.Cells.Count==2&&s.FieldStability=="Stable"&&
                s.FieldBookmarks.Values.Single().Page==2&&s.FieldPasses[^1].Fields.Single().Text=="2"&&
                s.ReplayedFieldSheets[0]==0&&s.ReplayedFieldSheets.Skip(1).Contains(1),
                "A later field resumes after a committed sheet and preserves the preceding table");
        }
        using(var lease=Fixture("equilibre-hauteurs-et-table",
            P(R("Énoncé solidaire"),"""<w:keepNext/><w:spacing w:line="470" w:lineRule="exact"/>""")+
            P(R("Ce paragraphe doit prendre plusieurs lignes dans la colonne et contribue à la hauteur mesurée."))+
            Table(2)+P(R("Dernier paragraphe"),"""<w:spacing w:before="320" w:after="100"/>""")+Section(equal),""))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            Check(page.Cells.Count==2&&page.Lines.Select(l=>l.FlowRegion!.Value.Column).Distinct().Count()==2&&
                page.Lines.First().FlowRegion==page.Lines[1].FlowRegion,
                "Balance uses paragraph heights, keeps and table constraints");
        }
        string regionalTable=Table(7)
            .Replace("w:w=\"1600\" w:type=\"dxa\"","w:w=\"5000\" w:type=\"pct\"")
            .Replace("w:type=\"fixed\"","w:type=\"autofit\"")
            .Replace("Rangée 3","Un contenu de cellule dont la hauteur varie avec la largeur de la colonne");
        using(var lease=Fixture("autofit-deux-largeurs",regionalTable+P(marked)+reference+Section(unequal),noBalance))
        {
            var s=Complete(lease);
            var cells=s.Pages.SelectMany(pg=>pg.Geometry.Cells).ToArray();
            var headers=cells.Where(c=>c.IsRepeated).ToArray();
            Check(cells.Count(c=>!c.IsRepeated)==7&&headers.Length>0&&
                cells.Where(c=>!c.IsRepeated).Select(c=>c.Width).Distinct().Count()==2&&
                cells.All(c=>c.FlowRegion is {} region&&c.Width<=PageComposer.Points(region.Bounds.Width)+.01f)&&s.FieldStability=="Stable",
                "AutoFit solves and measures cells at both occurrence widths and repeats a resized header");
        }
        using(var lease=Fixture("largeurs-impossibles",Fixtures.Paragraph("Corps")+
            Section("""<w:cols w:num="2" w:space="6000"/>"""),noBalance))
            Refused(lease,"PAGE_COLUMN_GEOMETRY_UNQUALIFIED");
        using(var lease=Fixture("crénage-position-bordure",P("""
            <w:r><w:rPr><w:kern w:val="20"/></w:rPr><w:t>AV</w:t></w:r>
            ""","""<w:pBdr><w:bottom w:val="single" w:sz="8" w:space="4" w:color="325678"/></w:pBdr><w:spacing w:after="120"/>""")+
            P("""<w:r><w:rPr><w:kern w:val="28"/></w:rPr><w:t>AV</w:t></w:r>""")+
            P("""<w:r w:rsidDel="1234ABCD"><w:rPr><w:position w:val="12"/></w:rPr><w:t>AV</w:t></w:r>""")+Section(""),""))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var lines=page.Lines;
            var fragments=lines.Select(l=>l.Native!.Parts.Single().Fragment!).ToArray();
            var border=lines[0].ParagraphBorder!;
            Check(fragments[0].Style.KernAt==10&&fragments[1].Style.KernAt==14&&fragments[0].PointSize==fragments[1].PointSize&&
                fragments[0].Measure!.Metrics.Width<fragments[1].Measure!.Metrics.Width&&
                fragments[2].Measure!.Metrics.Ink!.Value.Top<=fragments[1].Measure!.Metrics.Ink!.Value.Top-PageComposer.Emu(5.8f),
                "Le seuil de crénage et le déplacement du texte agissent sur la mesure.");
            Check(Close(border.Top,lines[0].NativeTop+lines[0].Height+4)&&
                Close(lines[1].NativeTop,lines[0].NativeTop+lines[0].Height+6),"La bordure tient dans l’espacement déclaré.");
            var png=Composer.PagePng(page,2);
            using var bitmap=SKBitmap.Decode(png);
            Check(bitmap.GetPixel(80,(int)(border.Top*2+1))==new SKColor(0x32,0x56,0x78),
                "La bordure sourcée est dessinée sur la feuille.");
            File.WriteAllBytes(Path.Combine(root,"bordure-paragraphe.png"),png);
        }
        string TallRow(bool atomic)
        {
            var paragraphs=string.Concat(Enumerable.Range(0,23).Select(i=>P(R($"Fragment {i:00}"),
                """<w:spacing w:line="500" w:lineRule="exact"/>""")));
            return """
                <w:tbl><w:tblPr><w:tblW w:w="4300" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr><w:tblGrid><w:gridCol w:w="2300"/><w:gridCol w:w="2000"/></w:tblGrid>
                <w:tr><w:trPr><w:tblHeader/></w:trPr><w:tc><w:p><w:r><w:t>En-tête</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Valeur</w:t></w:r></w:p></w:tc></w:tr>
                <w:tr>
                """+(atomic?"<w:trPr><w:cantSplit/></w:trPr>":"")+"<w:tc>"+paragraphs+
                "</w:tc><w:tc>"+Fixtures.Paragraph("Valeur courte")+"</w:tc></w:tr></w:tbl>";
        }
        using(var lease=Fixture("rangée-fragmentée",P(R("Introduction"))+TallRow(false)+Section(""),""))
        {
            var s=Complete(lease);
            var fragments=s.Pages.SelectMany(p=>p.Geometry.Cells).Where(c=>c.RowFragment is not null).GroupBy(c=>c.RowFragment!).ToArray();
            Check(fragments.Length>=3&&fragments.Select(f=>f.Key.RowId).Distinct().Count()==1&&
                fragments[0].Key.First&&fragments[^1].Key.Last&&fragments.All(f=>f.Count()==2),
                "Both cell boxes retain their source across distinct row fragments");
            Check(s.Pages.Skip(1).All(p=>p.Geometry.Cells.Count(c=>c.IsRepeated)==2),
                "Header repeated on every continuation");
            Check(s.Pages.SelectMany(p=>p.Geometry.Lines).Where(l=>l.Text.StartsWith("Fragment")).Select(l=>l.Text)
                .SequenceEqual(Enumerable.Range(0,23).Select(i=>$"Fragment {i:00}"))&&
                s.Pages.SelectMany(p=>p.Geometry.Lines).Count(l=>l.Text=="Valeur courte")==1,
                "Cell text remains in order and appears once");
            foreach(var page in s.Pages)
                File.WriteAllBytes(Path.Combine(root,$"rangée-{page.Id.PhysicalIndex}.png"),Composer.PagePng(page.Geometry));
        }
        using(var lease=Fixture("rangée-atomique-trop-haute",TallRow(true)+Section(""),""))
            Refused(lease,"PAGE_ROW_CANT_SPLIT_OVERFLOW");
        {
            var fragments=Enumerable.Range(1,12).Select(i=>$"Fusion {i:00}").ToArray();
            const string spacing="""<w:spacing w:before="0" w:after="0" w:line="400" w:lineRule="exact"/>""";
            string paragraphs=string.Concat(fragments.Select(value=>P(R(value),spacing)));
            string merged="""
                <w:tbl><w:tblPr><w:tblW w:w="4300" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr>
                <w:tblGrid><w:gridCol w:w="2300"/><w:gridCol w:w="2000"/></w:tblGrid>
                <w:tr><w:trPr><w:tblHeader/></w:trPr><w:tc><w:p><w:r><w:t>En-tête A</w:t></w:r></w:p></w:tc>
                <w:tc><w:p><w:r><w:t>En-tête B</w:t></w:r></w:p></w:tc></w:tr>
                """+string.Concat(Enumerable.Range(0,9).Select(i=>$"""
                <w:tr><w:trPr><w:trHeight w:val="800" w:hRule="exact"/></w:trPr>
                <w:tc><w:tcPr><w:vMerge{(i==0?" w:val=\"restart\"":"")}/><w:shd w:val="clear" w:fill="E7EFFF"/></w:tcPr>
                {(i==0?paragraphs:"<w:p/>")}</w:tc>
                <w:tc><w:p><w:r><w:t>Rangée {i+1}</w:t></w:r></w:p></w:tc></w:tr>
                """))+"</w:tbl>";
            using var lease=Fixture("fusion-verticale-sur-feuilles",merged+Section(""),"");
            var s=Complete(lease);
            var cells=s.Pages.SelectMany(p=>p.Geometry.Cells).Where(c=>c.CellFragment is not null).ToArray();
            Check(cells.Length>=2&&cells.Select(c=>c.CellId).Distinct().Count()==1&&
                cells[0].CellFragment!.First&&cells[^1].CellFragment!.Last&&
                s.Pages.Skip(1).All(p=>p.Geometry.Cells.Count(c=>c.IsRepeated)==2)&&
                s.Pages.SelectMany(p=>p.Geometry.Lines).Where(l=>l.CellId==cells[0].CellId).Select(l=>l.Text).SequenceEqual(fragments),
                "La fusion répartit ses paragraphes et garde ses occurrences de cellule et les en-têtes.");
            foreach(var page in s.Pages)
                File.WriteAllBytes(Path.Combine(root,$"fusion-verticale-{page.Id.PhysicalIndex}.png"),Composer.PagePng(page.Geometry));
        }
        foreach(string position in new[]{"pageBottom","beneathText","sectEnd"})
        {
            const string notesXml="""
                <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:t>——</w:t></w:r></w:p></w:footnote>
                <w:footnote w:id="1"><w:p><w:r><w:t>* Première ligne de note.</w:t></w:r></w:p>
                <w:p><w:r><w:t>La seconde ligne suit le même appel.</w:t></w:r></w:p></w:footnote>
                """;
            const string sourceCall="""
                <w:p><w:r><w:t>Passage avec note</w:t><w:footnoteReference w:id="1" w:customMarkFollows="1"/><w:t>*</w:t></w:r></w:p>
                """;
            var file=Fixtures.Write(root,"note-"+position+".docx",
                Fixtures.Parts(P(R("Avant le déplacement"),"""<w:spacing w:line="3500" w:lineRule="exact"/>""")+
                    sourceCall+Section(""),new(){
                    ["word/footnotes.xml"]=Fixtures.Xml("footnotes",notesXml),
                    ["word/settings.xml"]=Fixtures.Xml("settings",$"""<w:footnotePr><w:pos w:val="{position}"/><w:footnote w:id="-1"/></w:footnotePr>""")
                },Fixtures.Relationship("options","settings","settings.xml")+Fixtures.Relationship("notes","footnotes","footnotes.xml")));
            using(var sdk=WordprocessingDocument.Open(file,false))
                Check(!new OpenXmlValidator().Validate(sdk).Any(),"The note source must validate");
            using var lease=PageComposer.Compose(DocumentInspector.InspectDocument(file));
            var s=Complete(lease);
            Check(s.Pages.Length==2&&s.Pages[0].Geometry.Footnotes.Count==0,"The call and its reservation must move together");
            var page=s.Pages[1].Geometry;
            Check(page.Footnotes.Count==2&&page.Footnotes[0].Call is null&&page.Footnotes[1].Call is not null,
                "Source separator and call association appear on the same sheet");
            Check(page.Footnotes[1].Lines.Select(l=>l.Text).SequenceEqual(
                new[]{"* Première ligne de note.","La seconde ligne suit le même appel."}),"All paragraphs of the note survived");
            float bodyEnd=page.Lines.Max(l=>l.NativeTop+l.Height);
            float noteStart=PageComposer.Points(page.Footnotes[0].Region.Top);
            var final=page.Footnotes[^1].Region;
            Check(position is "beneathText" or "sectEnd"?Close(noteStart,bodyEnd):
                noteStart>bodyEnd&&Close(PageComposer.Points(final.Top+final.Height),page.Geometry.Height-page.Geometry.Bottom),
                "The same reservation respects the declared placement");
            File.WriteAllBytes(Path.Combine(root,"placement-"+position+".png"),Composer.PagePng(page));
        }
        {
            const string body="""
                <w:p><w:r><w:t>Appel en bas de page</w:t><w:footnoteReference w:id="2"/></w:r></w:p>
                <w:p><w:r><w:t>Appel en fin de document</w:t><w:endnoteReference w:id="2"/></w:r></w:p>
                """;
            const string local="""
                <w:endnotePr><w:numFmt w:val="lowerRoman"/></w:endnotePr>
                """;
            var parts=new Dictionary<string,string>
            {
                ["word/footnotes.xml"]=Fixtures.Xml("footnotes","""
                    <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:t>—</w:t></w:r></w:p></w:footnote>
                    <w:footnote w:id="2"><w:p><w:r><w:footnoteRef/><w:t> Bas de page</w:t></w:r></w:p></w:footnote>
                    """),
                ["word/endnotes.xml"]=Fixtures.Xml("endnotes","""
                    <w:endnote w:id="2"><w:p><w:r><w:endnoteRef/><w:t> Fin de document</w:t></w:r></w:p></w:endnote>
                    """)
            };
            string relationships=Fixtures.Relationship("foot","footnotes","footnotes.xml")+
                Fixtures.Relationship("end","endnotes","endnotes.xml");
            using var lease=PageComposer.Compose(DocumentInspector.InspectDocument(Fixtures.Write(root,"appels-de-notes-mixtes.docx",
                Fixtures.Parts(body+Section("").Replace("<w:sectPr>","<w:sectPr>"+local,StringComparison.Ordinal),parts,relationships))));
            var snapshot=lease.Snapshot;
            Check(snapshot.Coverage==PageCoverage.Partial&&
                snapshot.Pages.Single().Geometry.Footnotes.Any(n=>n.Call is not null&&n.Lines.Single().Text.Contains("Bas de page"))&&
                snapshot.Semantic!.References.Any(r=>r.Kind=="endnote"&&r.Status==ReferenceStatus.Resolved)&&
                snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_SECTION_ENDNOTE_PROPERTIES_EQUIVALENT"&&!i.BlocksRendering)&&
                snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_ENDNOTE_PLACEMENT_UNQUALIFIED"&&i.BlocksRendering),
                "Les formats indépendants des notes Word permettent la réserve de bas de page avec une note de fin encore hors profil.");
        }
        {
            const string bodyWithNote="""
                <w:p><w:r><w:t>Nombre de pages </w:t></w:r><w:fldSimple w:instr="NUMPAGES"/></w:p>
                <w:p><w:r><w:t>Appel de la note</w:t><w:footnoteReference w:id="2" w:customMarkFollows="1"/><w:t>*</w:t></w:r></w:p>
                """;
            const string separators="""
                <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:t>——</w:t></w:r></w:p></w:footnote>
                <w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:t>—— suite ——</w:t></w:r></w:p></w:footnote>
                """;
            var expected=Enumerable.Range(1,22).Select(i=>(i==1?"* ":"")+$"Paragraphe de note {i:00}").ToArray();
            var note=string.Concat(expected.Select(s=>P(R(s),"""<w:spacing w:line="500" w:lineRule="exact"/>""")));
            string file=Fixtures.Write(root,"note-continue-et-champ.docx",
                Fixtures.Parts(bodyWithNote+Section(""),new(){
                    ["word/footnotes.xml"]=Fixtures.Xml("footnotes",separators+"<w:footnote w:id=\"2\">"+note+"</w:footnote>"),
                    ["word/settings.xml"]=Fixtures.Xml("settings","""<w:footnotePr><w:footnote w:id="-1"/><w:footnote w:id="0"/></w:footnotePr>""")
                },Fixtures.Relationship("options","settings","settings.xml")+Fixtures.Relationship("notes","footnotes","footnotes.xml")));
            using(var sdk=WordprocessingDocument.Open(file,false))
                Check(!new OpenXmlValidator().Validate(sdk).Any(),"The continued note source must validate");
            using var lease=PageComposer.Compose(DocumentInspector.InspectDocument(file));
            var s=Complete(lease);
            Check(s.Pages.Length>=3&&s.FieldStability=="Stable"&&s.FieldPasses[^1].Fields.Single().Text==s.Pages.Length.ToString(),
                "NUMPAGES includes continuation sheets and converges through the same placement");
            Check(s.Pages.SelectMany(p=>p.Geometry.Footnotes.Where(f=>f.Call is not null)).SelectMany(f=>f.Lines.Select(l=>l.Text))
                .SequenceEqual(expected),"All original note paragraphs are present once, in source order");
            Check(!s.Pages[0].Geometry.Footnotes[1].Continued&&s.Pages.Skip(1).All(p=>p.Geometry.Footnotes is [var sep,var part]&&
                sep.Lines.Single().Text=="—— suite ——"&&part.Continued&&part.Call==s.Pages[0].Geometry.Footnotes[1].Call),
                "Actual source continuation separator and same call on each dependent sheet");
            foreach(var page in s.Pages)
            {
                byte[] png=Composer.PagePng(page.Geometry);
                using var bitmap=SKBitmap.Decode(png);
                foreach(var part in page.Geometry.Footnotes)
                {
                    int top=(int)Math.Floor(PageComposer.Points(part.Region.Top));
                    int bottom=(int)Math.Ceiling(PageComposer.Points(part.Region.Top+part.Region.Height));
                    Check(Enumerable.Range(top,bottom-top).Any(y=>
                        Enumerable.Range(0,bitmap.Width).Any(x=>bitmap.GetPixel(x,y).Red<160)),
                        "The separator and the note are painted on every sheet, including sheets without body text");
                }
                File.WriteAllBytes(Path.Combine(root,$"note-continue-{page.Id.PhysicalIndex}.png"),png);
            }
            var internalLines=Enumerable.Range(1,22).Select(i=>(i==1?"* ":"")+$"Ligne de note {i:00}").ToArray();
            string singleParagraph=P(string.Join("<w:r><w:br/></w:r>",internalLines.Select(R)),
                """<w:widowControl/><w:spacing w:line="500" w:lineRule="exact"/>""");
            string combinedFile=Fixtures.Write(root,"note-paragraphe-et-table.docx",
                Fixtures.Parts(bodyWithNote+Table(4)+Section(""),new(){
                    ["word/footnotes.xml"]=Fixtures.Xml("footnotes",separators+"<w:footnote w:id=\"2\">"+singleParagraph+"</w:footnote>"),
                    ["word/settings.xml"]=Fixtures.Xml("settings","""<w:footnotePr><w:footnote w:id="-1"/><w:footnote w:id="0"/></w:footnotePr>""")
                },Fixtures.Relationship("options","settings","settings.xml")+Fixtures.Relationship("notes","footnotes","footnotes.xml")));
            using(var sdk=WordprocessingDocument.Open(combinedFile,false))
                Check(!new OpenXmlValidator().Validate(sdk).Any(),"The combined note and table source must validate");
            using var combinedLease=PageComposer.Compose(DocumentInspector.InspectDocument(combinedFile));
            var combinedPages=Complete(combinedLease);
            var fragments=combinedPages.Pages.SelectMany(pg=>pg.Geometry.Footnotes.Where(f=>f.Call is not null)).ToArray();
            Check(fragments.Length>1&&fragments.All(f=>f.Lines.Count>=2)&&
                fragments.SelectMany(f=>f.Lines).Select(l=>l.ParagraphId).Distinct().Count()==1&&
                fragments.SelectMany(f=>f.Lines).Select(l=>l.Text).SequenceEqual(internalLines)&&
                combinedPages.Pages[0].Geometry.Cells.Count==0&&combinedPages.Pages.Sum(pg=>pg.Geometry.Cells.Count(c=>!c.IsRepeated))==4&&
                combinedPages.FieldPasses[^1].Fields.Single().Text==combinedPages.Pages.Length.ToString(),
                "Internal note cuts preserve widow protection and the reservation displaces the following table");

            const string anchorCall="""<w:r><w:t>Appel avec dessin</w:t><w:footnoteReference w:id="2" w:customMarkFollows="1"/><w:t>*</w:t></w:r>""";
            string allBody=P(R("Colonne A")+column+R("Colonne B"),noWidow)+Table(2)+
                P(R("Fin des colonnes"),noWidow+Section(unequal))+P(square+anchorCall,noWidow+"""<w:ind w:left="20"/>""")+Table(3)+
                P("""<w:fldSimple w:instr="NUMPAGES"/>""","<w:pageBreakBefore/>"+noWidow)+Section("");
            string graphicSeparators=separators.Replace("<w:t>—— suite ——</w:t>","<w:continuationSeparator/>");
            string allFile=Fixtures.Write(root,"flux-colonnes-dessin-note-table-champ.docx",
                Fixtures.Parts(allBody,new(){
                    ["word/footnotes.xml"]=Fixtures.Xml("footnotes",graphicSeparators+"<w:footnote w:id=\"2\">"+singleParagraph+"</w:footnote>"),
                    ["word/settings.xml"]=Fixtures.Xml("settings","""<w:footnotePr><w:footnote w:id="-1"/><w:footnote w:id="0"/></w:footnotePr>"""+noBalance),
                },Fixtures.Relationship("options","settings","settings.xml")+Fixtures.Relationship("notes","footnotes","footnotes.xml")+
                    Fixtures.Relationship("img","image","media/couleurs.png"),new(){["word/media/couleurs.png"]=media}));
            using(var sdk=WordprocessingDocument.Open(allFile,false))
                Check(!new OpenXmlValidator().Validate(sdk).Any(),"The integrated source must validate");
            using var allLease=PageComposer.Compose(DocumentInspector.InspectDocument(allFile));
            var all=Complete(allLease);
            var drawn=all.Pages[1].Geometry;
            Check(all.Pages[0].Geometry.Lines.Any(l=>l.FlowRegion is {Column:1})&&drawn.FloatingPictures.Count==1&&
                drawn.Footnotes.Count==2&&drawn.Footnotes[0].Region.Top>=drawn.FloatingPictures[0].ExcludedRegion!.Value.Top+
                    drawn.FloatingPictures[0].ExcludedRegion!.Value.Height&&
                all.Pages.Sum(pg=>pg.Geometry.Cells.Count(c=>!c.IsRepeated))==5&&all.FieldStability=="Stable"&&
                all.FieldPasses[^1].Fields.Single().Text==all.Pages.Length.ToString()&&all.ReplayedFieldSheets.Any(n=>n>0),
                "The columns, floating anchor, note reservation, table and field share physical sheet boundaries");
            foreach(var page in all.Pages)
            {
                byte[] png=Composer.PagePng(page.Geometry,1.5f);
                foreach(var continuationLine in page.Geometry.Footnotes.SelectMany(n=>n.Lines).Where(l=>l.NoteRule is not null))
                {
                    var rule=continuationLine.NoteRule!;
                    Check(rule.Source==continuationLine.Native!.Parts.Single().Control!.Value.Source&&Close(rule.Width,260),
                        "The full-width continuation rule preserves its OOXML source");
                    using var bitmap=SKBitmap.Decode(png);
                    int y=(int)(continuationLine.Baseline*1.5f);
                    Check(new[]{55,410}.All(x=>Enumerable.Range(y-1,3).Any(row=>bitmap.GetPixel(x,row).Red<225)),
                        "The rule is drawn across the body at the reserved continuation position");
                }
                File.WriteAllBytes(Path.Combine(root,$"flux-intégré-{page.Id.PhysicalIndex}.png"),png);
            }
            Check(all.Pages.Count(p=>p.Geometry.Footnotes.SelectMany(n=>n.Lines).Any(l=>l.NoteRule is not null))>=2,
                "Continued notes produce repeated graphic separators");
        }
        {
            string firstSection=P(R("Premier")+NumberedCall(2))+
                P(R("Personnalisée")+"""<w:r><w:footnoteReference w:id="3" w:customMarkFollows="1"/><w:t>*</w:t></w:r>""")+
                P(R("Deuxième")+NumberedCall(4),Section(""));
            var numberingParts=new Dictionary<string,string>
            {
                ["word/footnotes.xml"]=Fixtures.Xml("footnotes","""
                    <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:t>——</w:t></w:r></w:p></w:footnote>
                    <w:footnote w:id="2"><w:p><w:r><w:footnoteRef/><w:t xml:space="preserve"> Un</w:t></w:r></w:p></w:footnote>
                    <w:footnote w:id="3"><w:p><w:r><w:t>* Lettre</w:t></w:r></w:p></w:footnote>
                    <w:footnote w:id="4"><w:p><w:r><w:footnoteRef/><w:t xml:space="preserve"> Deux</w:t></w:r></w:p></w:footnote>
                    <w:footnote w:id="5"><w:p><w:r><w:footnoteRef/><w:t xml:space="preserve"> Section</w:t></w:r></w:p></w:footnote>
                    """),
                ["word/settings.xml"]=Fixtures.Xml("settings","""
                    <w:footnotePr><w:numFmt w:val="upperRoman"/><w:numStart w:val="3"/><w:numRestart w:val="eachSect"/>
                    <w:footnote w:id="-1"/></w:footnotePr>
                    """),
            };
            string source=Fixtures.Write(root,"notes-numéros-et-sections.docx",Fixtures.Parts(
                firstSection+P(R("Section suivante")+NumberedCall(5))+Section(""),numberingParts,
                Fixtures.Relationship("options","settings","settings.xml")+Fixtures.Relationship("notes","footnotes","footnotes.xml")));
            using(var sdk=WordprocessingDocument.Open(source,false))
                Check(!new OpenXmlValidator().Validate(sdk).Any(),"The numbered notes source must validate");
            using var lease=PageComposer.Compose(DocumentInspector.InspectDocument(source));
            var pages=Complete(lease).Pages;
            var calls=Numbers(pages.SelectMany(p=>p.Geometry.Lines));
            var marks=Numbers(pages.SelectMany(p=>p.Geometry.Footnotes).SelectMany(n=>n.Lines));
            Check(pages.Length==2&&calls.Select(n=>n.Fragment.Text).SequenceEqual(new[]{"III","IV","III"})&&
                calls.Select(n=>n.Fragment.Text).SequenceEqual(marks.Select(n=>n.Fragment.Text))&&
                calls.Zip(marks).All(pair=>pair.First.Cause!=pair.Second.Cause)&&
                pages[0].Geometry.Footnotes.Any(n=>n.Call is not null&&n.Lines[0].Text=="* Lettre"),
                "The same measured numbers appear in the body and the notes, restart per section and skip a custom mark");
            File.WriteAllBytes(Path.Combine(root,"notes-numéros.png"),Composer.PagePng(pages[0].Geometry,1.5f));

            static string NumberedCall(int id)=>$"""<w:r><w:footnoteReference w:id="{id}"/></w:r>""";
            static IEnumerable<LineDecoration> Numbers(IEnumerable<PageLine> lines)=>
                lines.SelectMany(l=>l.Native!.Generated.Where(d=>d.Kind=="noteNumber"));
        }
        const string noteCall="""<w:r><w:footnoteReference w:id="1"/></w:r>""";
        var sectionNoteOptions=Section("").Replace("<w:sectPr>",
            """<w:sectPr><w:footnotePr><w:pos w:val="pageBottom"/><w:numFmt w:val="decimal"/><w:numStart w:val="1"/></w:footnotePr>""",
            StringComparison.Ordinal);
        const string unsupportedSeparator="""
            <w:p><w:r><w:rPr><w:color w:val="AA0000"/></w:rPr><w:separator/></w:r></w:p>
            """;
        string noteDocument=Fixtures.Write(root,"note-hors-profil-après-section.docx",Fixtures.Parts(
            P(R("Première section"),sectionNoteOptions)+P(R("Section avec appel")+noteCall)+Section(""),
            new(){["word/footnotes.xml"]=Fixtures.Xml("footnotes",
                """<w:footnote w:type="separator" w:id="-1">"""+unsupportedSeparator+
                """</w:footnote><w:footnote w:id="1"><w:p><w:r><w:footnoteRef/><w:t>Note</w:t></w:r></w:p></w:footnote>""")},
            Fixtures.Relationship("notes","footnotes","footnotes.xml")));
        using(var lease=PageComposer.Compose(DocumentInspector.InspectDocument(noteDocument)))
        {
            var snapshot=lease.Snapshot;
            Check(snapshot.Coverage==PageCoverage.Partial&&snapshot.Pages.Single().Geometry.Lines.Single().Text=="Première section"&&
                snapshot.Gaps.Any(g=>g.Reason=="PAGE_FOOTNOTE_RULE_UNQUALIFIED")&&
                snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_SECTION_FOOTNOTE_PROPERTIES_EQUIVALENT"&&!i.BlocksRendering),
                "L’échec de préparation d’une note se rattache à son appel et préserve la première feuille.");
        }
        string notePath=Fixtures.Write(root,"note-dans-colonne.docx",
            Fixtures.Parts(P(R("Passage portant un appel")+noteCall)+Section(equal),new(){
                ["word/settings.xml"]=Fixtures.Xml("settings",noBalance),
                ["word/footnotes.xml"]=Fixtures.Xml("footnotes","""<w:footnote w:id="1"><w:p><w:r><w:t>Texte de la note</w:t></w:r></w:p></w:footnote>""")
            },Fixtures.Relationship("options","settings","settings.xml")+Fixtures.Relationship("notes","footnotes","footnotes.xml")));
        var noteInput=DocumentInspector.InspectDocument(notePath);
        using(var lease=PageComposer.Compose(noteInput))
        {
            var s=lease.Snapshot;
            Check(s.Coverage!=PageCoverage.Complete&&s.Semantic!.References.Any(r=>r.Kind=="footnote"&&r.Status==ReferenceStatus.Resolved)&&
                ReadableReader.Read(noteInput).Presentation.Notes.Values.Single().Verified,"An unplaced note must retain its relationship and block publication");
        }

        PageCompositionLease Fixture(string name,string body,string settings,bool withImage=false,byte[]? pictureBytes=null)
        {
            var part=new Dictionary<string,string>{["word/settings.xml"]=Fixtures.Xml("settings",settings)};
            var link=Fixtures.Relationship("s","settings","settings.xml");
            Dictionary<string,byte[]>? binary=null;
            if(withImage)
            {
                link+=Fixtures.Relationship("img","image","media/couleurs.png");
                binary=new(){["word/media/couleurs.png"]=pictureBytes??media};
            }
            var path=Fixtures.Write(root,name+".docx",Fixtures.Parts(body,part,link,binary));
            using(var sdk=WordprocessingDocument.Open(path,false))
            {
                var errors=new OpenXmlValidator().Validate(sdk).ToArray();
                Check(errors.Length==0,"OpenXmlValidator "+string.Join(" / ",errors.Select(x=>x.Description)));
            }
            var input=DocumentInspector.InspectDocument(path);
            return PageComposer.Compose(input);
        }
    }

    static void VectorPreviews(string root)
    {
        byte[] wmf=WriteWmf(),emf=WriteEmf();
        string inline=Picture(floating:false).Replace("""<a:srcRect b="50000"/>""","",StringComparison.Ordinal)
            .Replace(""" flipH="1" """.TrimEnd(),"",StringComparison.Ordinal);
        foreach(var (kind,data) in new[]{("wmf",wmf),("emf",emf)})
        {
            var input=Inspect(kind,data);
            using var lease=PageComposer.Compose(input);
            var snapshot=Complete(lease);
            var page=snapshot.Pages.Single().Geometry;
            var image=page.Lines.Single().Images.Single();
            var path=image.Image.Metafile?.Strokes.Single().Points??[];
            Check(path.SequenceEqual(new MetafilePoint[]{new(10,20),new(80,20),new(80,80)})&&
                input.SafeMedia.Count==0&&input.MetafileParts.Count==1&&
                Enum.GetValues<ExportFormat>().All(format=>!DocumentExports.Preflight(input,format).Allowed),
                $"Tracé {kind} et isolation des octets hors de l’export.");
            const int scale=3;
            using var raster=SKBitmap.Decode(Composer.PagePng(page,scale));
            Check(DarkAt(.45f,.205f)&&DarkAt(.805f,.55f)&&!DarkAt(.2f,.7f),
                $"Trajet et limites du dessin {kind} à son emplacement DOCX.");

            bool DarkAt(float u,float v)
            {
                int x=(int)Math.Floor((image.X+image.Image.Width*u)*scale);
                int y=(int)Math.Floor((image.Y+image.Image.Height*v)*scale);
                return Enumerable.Range(y-1,3).Any(row=>Enumerable.Range(x-1,3).Any(col=>raster.GetPixel(col,row).Red<218));
            }
        }
        var embedded=Inspect("emf",emf,embedded:true);
        using(var lease=PageComposer.Compose(embedded))
        {
            var snapshot=Complete(lease);
            Check(snapshot.Pages.Single().Geometry.Lines.Single().Images.Single().Image.Metafile?.Strokes.Length==1&&
                snapshot.Semantic!.References.Any(r=>r.Relation?.Id=="ole"&&r.Status==ReferenceStatus.Inert),
                "Le même tracé EMF est placé comme aperçu VML et la relation OLE demeure inerte.");
        }
        using(var lease=PageComposer.Compose(Inspect("emf",WriteFilledEmf(),"surface-découpée")))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var image=page.Lines.Single().Images.Single();
            var fill=image.Image.Metafile!.Fills.Single();
            Check(fill is {Rgb:0xA02040,Clip:not null}&&fill.Path.Commands.Any(c=>c.Verb==MetafilePathVerb.Cubic),
                "Le pinceau, le Bézier et le clip proviennent des enregistrements EMF.");
            using var raster=SKBitmap.Decode(Composer.PagePng(page,3));
            SKColor At(float x,float y)=>raster.GetPixel((int)(3*(image.X+x*image.Image.Width)),(int)(3*(image.Y+y*image.Image.Height)));
            Check(At(.5f,.5f)==new SKColor(160,32,64)&&At(.15f,.5f)==SKColors.White,
                "Le remplissage est limité par le tracé du clip et l’alternative n’est pas lue.");
        }
        using(var denied=PageComposer.Compose(Inspect("emf",WriteFilledEmf(invalidComment:true),"alternative-invalide")))
            Refused(denied,"PAGE_METAFILE_PROFILE_UNQUALIFIED");
        using(var lease=PageComposer.Compose(Inspect("wmf",WriteWmf(coloredPen:true),"stylet",embedded:true)))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var preview=page.Lines.Single().Images.Single().Image.Metafile;
            using var raster=SKBitmap.Decode(Composer.PagePng(page,3));
            Check(preview?.Strokes.Single().Pen is {Width:4,Rgb:0xB02030}&&
                raster.Pixels.Any(p=>p is {Red:176,Green:32,Blue:48}),
                "Le stylet sélectionné par identifiant est peint dans l’aperçu OLE.");
        }
        using(var lease=PageComposer.Compose(Inspect("wmf",WriteWmfText(),"texte",embedded:true)))
        {
            var page=Complete(lease).Pages.Single().Geometry;
            var image=page.Lines.Single().Images.Single();
            var vector=image.Image.Metafile!;
            Check(vector.BaselineSixteenths==32&&vector.Texts is [{Rgb:0xB02030}]&&vector.Strokes.IsEmpty,
                "La ligne de base MathType et le dessin EXTTEXTOUT sont conservés.");
            const int scale=3;
            using var raster=SKBitmap.Decode(Composer.PagePng(page,scale));
            var redColumns=Enumerable.Range((int)(image.X*scale),(int)(image.Image.Width*scale))
                .Where(x=>Enumerable.Range((int)(image.Y*scale),(int)(image.Image.Height*scale))
                        .Any(y=>raster.GetPixel(x,y) is {Red:>=176 and <230,Green:<175})).ToArray();
            Check(redColumns.Any(x=>x<image.X*scale+18)&&redColumns.Any(x=>x>=image.X*scale+27)&&
                !redColumns.Any(x=>x>=image.X*scale+18&&x<image.X*scale+26),
                "Les deux glyphes reprennent les positions EXTTEXTOUT et une avance explicite de 40 unités.");
        }
        using(var denied=PageComposer.Compose(Inspect("wmf",WriteWmfText(omitLastAdvance:true),"texte-incomplet")))
            Refused(denied,"PAGE_METAFILE_PROFILE_UNQUALIFIED");
        using(var plain=PageComposer.Compose(Inspect("wmf",WriteWmfText(),"sans-enveloppe")))
        using(var commented=PageComposer.Compose(Inspect("wmf",WriteWmfText(applicationComments:true),"avec-enveloppe")))
        {
            var reference=Complete(plain).Composition.Pages.Single();
            var withComments=Complete(commented).Composition.Pages.Single();
            Check(Composer.PagePng(reference).AsSpan().SequenceEqual(Composer.PagePng(withComments)),
                "Les commentaires applicatifs qualifiés laissent les glyphes et les pixels intacts.");
        }
        using(var denied=PageComposer.Compose(Inspect("wmf",WriteWmfText(applicationComments:true,brokenGroup:true),"enveloppe-ouverte")))
            Refused(denied,"PAGE_METAFILE_PROFILE_UNQUALIFIED");
        const string frame="""
            <v:shapetype id="PictureFrame" coordsize="21600,21600" o:spt="75" o:preferrelative="t"
                path="m@4@5l@4@11@9@11@9@5xe" filled="f" stroked="f">
              <v:stroke joinstyle="miter"/><v:formulas>
              <v:f eqn="if lineDrawn pixelLineWidth 0"/><v:f eqn="sum @0 1 0"/><v:f eqn="sum 0 0 @1"/><v:f eqn="prod @2 1 2"/>
              <v:f eqn="prod @3 21600 pixelWidth"/><v:f eqn="prod @3 21600 pixelHeight"/><v:f eqn="sum @0 0 1"/><v:f eqn="prod @6 1 2"/>
              <v:f eqn="prod @7 21600 pixelWidth"/><v:f eqn="sum @8 21600 0"/><v:f eqn="prod @7 21600 pixelHeight"/><v:f eqn="sum @10 21600 0"/>
              </v:formulas><v:path o:extrusionok="f" gradientshapeok="t" o:connecttype="rect"/>
              <o:lock v:ext="edit" aspectratio="t"/>
            </v:shapetype>
            """;
        using(var typed=PageComposer.Compose(Inspect("emf",emf,"modele",embedded:true,template:frame)))
        {
            var snapshot=Complete(typed);
            Check(snapshot.Pages.Single().Geometry.Lines.Single().Images.Single().Image.Metafile?.Strokes.Length==1,
                "Le type documenté 75 hérite du rectangle et émet le tracé passif de l’aperçu.");
        }
        using(var denied=PageComposer.Compose(Inspect("emf",emf,"modele-altere",embedded:true,
            template:frame.Replace("sum @8 21600 0","sum @8 10000 0",StringComparison.Ordinal))))
            Refused(denied,"PAGE_VML_PICTURE_UNQUALIFIED");
        byte[] escape=wmf.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(escape.AsSpan(44),0x0626);
        using(var denied=PageComposer.Compose(Inspect("wmf",escape,"échappement")))
        {
            Refused(denied,"PAGE_METAFILE_PROFILE_UNQUALIFIED");
            Check(denied.Snapshot.Composition.Issues.Any(i=>i.Message.Contains("0626",StringComparison.Ordinal)),
                "Un enregistrement d’échappement reste hors du sous-système graphique.");
        }
        byte[] truncated=emf[..^4];
        using(var denied=PageComposer.Compose(Inspect("emf",truncated,"tronqué")))
            Refused(denied,"PAGE_METAFILE_PROFILE_UNQUALIFIED");

        InspectionResult Inspect(string kind,byte[] data,string suffix="statique",bool embedded=false,string? template=null)
        {
            string target="media/trait."+kind;
            string picture=embedded?"""
                <w:r><w:object xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office">
                <v:shape id="Preview" coordsize="21600,21600" path="m0,0l0,21600,21600,21600,21600,0xe"
                  style="width:20pt;height:20pt" filled="f" stroked="f"><v:imagedata r:id="img"/></v:shape>
                <o:OLEObject Type="Embed" ProgID="Sample.Object" ShapeID="Preview" DrawAspect="Content" ObjectID="_1" r:id="ole"/>
                </w:object></w:r>
                """:inline;
            if(template is not null)
                picture=picture.Replace("""<v:shape id="Preview" coordsize="21600,21600" path="m0,0l0,21600,21600,21600,21600,0xe" """.TrimEnd(),
                    template+"""<v:shape id="Preview" type="#PictureFrame" """.TrimEnd(),StringComparison.Ordinal)
                    .Replace(""" filled="f" stroked="f"><v:imagedata""","""><v:imagedata""",StringComparison.Ordinal);
            string links=Fixtures.Relationship("img","image",target);
            var binary=new Dictionary<string,byte[]>{["word/"+target]=data};
            if(embedded)
            {
                links+=Fixtures.Relationship("ole","oleObject","embeddings/object.bin");
                binary["word/embeddings/object.bin"]=Fixtures.Compound();
            }
            var parts=Fixtures.Parts(P(picture)+Section(""),relationships:links,binary:binary);
            parts["[Content_Types].xml"]=System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(parts["[Content_Types].xml"]).Replace("</Types>",
                    $"""<Default Extension="{kind}" ContentType="image/x-{kind}"/></Types>""",StringComparison.Ordinal)
                    .Replace("application/octet-stream","application/vnd.openxmlformats-officedocument.oleObject",StringComparison.Ordinal));
            var path=Fixtures.Write(root,kind+"-"+(embedded?"ole-"+suffix:suffix)+".docx",parts);
            using var sdk=WordprocessingDocument.Open(path,false);
            var errors=new OpenXmlValidator().Validate(sdk).ToArray();
            Check(errors.Length==0,"Paquet de témoin vectoriel : "+string.Join(" / ",errors.Select(e=>e.Description)));
            return DocumentInspector.InspectDocument(path);
        }

        static byte[] WriteWmf(bool coloredPen=false)
        {
            using var bytes=new MemoryStream();
            using var writer=new BinaryWriter(bytes);
            writer.Write(0x9AC6CDD7U);writer.Write((ushort)0);
            writer.Write((short)0);writer.Write((short)0);writer.Write((short)100);writer.Write((short)100);
            writer.Write((ushort)100);writer.Write(0U);
            ushort checksum=0;
            var header=bytes.ToArray();
            for(int i=0;i<header.Length;i+=2)
                checksum^=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(i));
            writer.Write(checksum);
            writer.Write((ushort)1);writer.Write((ushort)9);writer.Write((ushort)0x300);
            writer.Write(coloredPen?34U:22U);writer.Write((ushort)(coloredPen?1:0));writer.Write(10U);writer.Write((ushort)0);
            if(coloredPen)
            {
                writer.Write(8U);writer.Write((ushort)0x02fa);
                writer.Write((ushort)0);writer.Write((short)4);writer.Write((short)0);
                writer.Write(0x003020B0U);
                writer.Write(4U);writer.Write((ushort)0x012d);writer.Write((ushort)0);
            }
            writer.Write(10U);writer.Write((ushort)0x0325);writer.Write((ushort)3);
            foreach(short coord in new short[]{10,20,80,20,80,80})writer.Write(coord);
            writer.Write(3U);writer.Write((ushort)0);
            return bytes.ToArray();
        }

        static byte[] WriteWmfText(bool omitLastAdvance=false,bool applicationComments=false,bool brokenGroup=false)
        {
            using var bytes=new MemoryStream();
            using var writer=new BinaryWriter(bytes);
            writer.Write(WriteWmf().AsSpan(0,40));
            Record(0x0102,()=>writer.Write((ushort)1),2);
            Record(0x012e,()=>writer.Write((ushort)25),2);
            Record(0x0209,()=>writer.Write(0x003020B0U),4);
            Record(0x02fb,()=>
            {
                foreach(short number in new short[]{-22,0,0,0,400})writer.Write(number);
                writer.Write(new byte[8]);
                byte[] face=new byte[32];
                "DejaVu Serif"u8.CopyTo(face);
                writer.Write(face);
            },50);
            Record(0x012d,()=>writer.Write((ushort)0),2);
            Record(0x0214,()=>{writer.Write((short)50);writer.Write((short)10);},4);
            Record(0x0626,()=>
            {
                writer.Write((ushort)15);writer.Write((ushort)12);writer.Write("MathType"u8);
                writer.Write((ushort)0);writer.Write((ushort)32);
            },16);
            if(applicationComments)Comment(26,()=>
            {
                writer.Write(uint.MaxValue);writer.Write((ushort)0);writer.Write(16U);
                foreach(int coordinate in new[]{-10,-20,90,80})writer.Write(coordinate);
            });
            Record(0x0a32,()=>
            {
                writer.Write(0U);writer.Write((ushort)2);writer.Write((ushort)0);
                writer.Write("AA"u8);writer.Write((short)40);
                if(!omitLastAdvance)writer.Write((short)20);
            },omitLastAdvance?12:14);
            if(applicationComments)
            {
                Comment(40,()=>
                {
                    writer.Write("AppsMFCC"u8);writer.Write((ushort)1);writer.Write(1U);writer.Write(1U);
                    writer.Write("Design Science, Inc.\0"u8);writer.Write((byte)0x51);
                });
                if(!brokenGroup)Comment(10,()=>
                {
                    writer.Write(uint.MaxValue);writer.Write((ushort)1);writer.Write(0U);
                });
            }
            Record(0,()=>{},0);
            uint words=(uint)(bytes.Length-22)/2;
            bytes.Position=28;
            writer.Write(words);writer.Write((ushort)1);writer.Write(28U);
            return bytes.ToArray();

            void Comment(ushort length,Action write)=>Record(0x0626,()=>
            {
                writer.Write((ushort)15);writer.Write(length);write();
            },4+length);

            void Record(ushort function,Action body,int size)
            {
                writer.Write((uint)(3+size/2));
                writer.Write(function);
                body();
            }
        }

        static byte[] WriteEmf()
        {
            using var bytes=new MemoryStream();
            using var writer=new BinaryWriter(bytes);
            writer.Write(1U);writer.Write(108U);
            foreach(int value in new[]{0,0,99,99,0,0,2540,2540})writer.Write(value);
            writer.Write(0x464d4520U);writer.Write(0x10000U);writer.Write(180U);writer.Write(3U);
            writer.Write((ushort)1);writer.Write((ushort)0);
            foreach(int value in new[]{0,0,0,100,100,25,25})writer.Write(value);
            foreach(int value in new[]{0,0,0,25_400,25_400})writer.Write(value);
            writer.Write(4U);writer.Write(52U);
            foreach(int value in new[]{10,20,80,80,3,10,20,80,20,80,80})writer.Write(value);
            writer.Write(14U);writer.Write(20U);writer.Write(0U);writer.Write(16U);writer.Write(20U);
            return bytes.ToArray();
        }

        static byte[] WriteFilledEmf(bool invalidComment=false)
        {
            const int headerSize=108;
            using var bytes=new MemoryStream();
            using var writer=new BinaryWriter(bytes);
            writer.Write(WriteEmf(),0,headerSize);
            int records=1;
            Record(70,()=>{writer.Write(49);writer.Write(0x43494447);writer.Write(0x40000004);
                foreach(int v in new[]{0,0,100,100,1,0x50444620,1,5,invalidComment?40:44})writer.Write(v);
                writer.Write("%PDF-"u8);writer.Write(new byte[3]);});
            Integers(17,8);
            Integers(10,0,0);Integers(9,100,100);Integers(12,0,0);Integers(11,100,100);
            Integers(59);
            Integers(27,25,20);Points(89,75,20,75,80,25,80);
            Integers(61);Integers(60);
            Integers(67,5);
            Integers(39,1,0,0x4020A0,0);Integers(37,1);Integers(19,2);
            Integers(59);Integers(27,5,5);
            Points(88,25,5,70,5,95,5);Points(89,95,95,5,95);
            Integers(61);Integers(60);Integers(62,5,5,95,95);
            Integers(14,0,16,20);
            long length=bytes.Length;
            bytes.Position=48;
            writer.Write((uint)length);writer.Write(records);writer.Write((ushort)2);
            return bytes.ToArray();

            void Integers(uint kind,params int[] values)=>Record(kind,()=>
            {
                foreach(int value in values)writer.Write(value);
            });
            void Points(uint kind,params short[] coords)=>Record(kind,()=>
            {
                foreach(int v in new[]{0,0,-1,-1,coords.Length/2})writer.Write(v);
                foreach(short coord in coords)writer.Write(coord);
            });
            void Record(uint kind,Action body)
            {
                long start=bytes.Position;
                writer.Write(kind);writer.Write(0U);body();
                long end=bytes.Position;
                bytes.Position=start+4;
                writer.Write((uint)(end-start));
                bytes.Position=end;
                records++;
            }
        }
    }

    static void LegacyPreviews(string root,byte[] png)
    {
        const string namespaces="""
            xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office"
            """;
        const string picture="""
            <v:shape id="Preview" coordsize="21600,21600" path="m0,0l0,21600,21600,21600,21600,0xe"
              style="width:20pt;height:20pt;flip:x" filled="f" stroked="f">
              <v:imagedata r:id="preview"/>
            </v:shape>
            """;
        const string objectNode="""
            <o:OLEObject Type="Embed" ProgID="Sample.Object" ShapeID="Preview" DrawAspect="Content" ObjectID="_1" r:id="ole"/>
            """;
        string wrapper=$"""<w:r><w:object {namespaces}>{picture}{objectNode}</w:object></w:r>""";
        string visualOnly=$"""<w:r><w:pict {namespaces}>{picture.Replace("Preview","ImageUnique",StringComparison.Ordinal)}</w:pict></w:r>""";
        var original=Inspect("objets-apercus-passifs",wrapper,visualOnly);
        using(var lease=PageComposer.Compose(original))
        {
            var snapshot=Complete(lease);
            var page=snapshot.Pages.Single().Geometry;
            var images=page.Lines.SelectMany(l=>l.Images).ToArray();
            var hits=page.Lines.SelectMany(PageCoordinates.Images).ToArray();
            Check(images.Length==2&&images[0].RunId!=images[1].RunId&&
                images.All(i=>Close(i.Image.Width,20)&&Close(i.Image.Height,20)&&i.Image.FlipX)&&
                hits.Select(h=>h.Sources.Single().NodeId).SequenceEqual(images.Select(i=>i.RunId))&&
                snapshot.Semantic!.References.Any(r=>r.Relation?.Id=="ole"&&r.Status==ReferenceStatus.Inert)&&
                original.SafeMedia.Count==1,"Vignettes distinctes, sélection des occurrences et relation OLE passive.");
            using var raster=SKBitmap.Decode(Composer.PagePng(page));
            Check(images.All(i=>raster.GetPixel((int)i.X+4,(int)i.Y+4)==SKColors.Lime),
                "Les pixels VML sont dessinés avec le retournement déclaré.");
            Check(Enum.GetValues<ExportFormat>().All(f=>!DocumentExports.Preflight(original,f).Allowed),
                "Le profil d’export garde ses contrôles propres.");
        }
        using var mismatched=PageComposer.Compose(Inspect("objet-apercu-incoherent",
            wrapper.Replace("ShapeID=\"Preview\"","ShapeID=\"Other\"",StringComparison.Ordinal),invalidShapeId:true));
        Refused(mismatched,"PAGE_VML_PICTURE_UNQUALIFIED");
        Check(!mismatched.Snapshot.Composition.Pages.SelectMany(p=>p.Lines).SelectMany(l=>l.Images).Any(),
            "Une liaison contradictoire ne produit aucune vignette présentée comme qualifiée.");

        InspectionResult Inspect(string name,string embedded,string second="",bool invalidShapeId=false)
        {
            var parts=Fixtures.Parts(P(embedded)+(second.Length==0?"":P(second))+Section(""),
                relationships:Fixtures.Relationship("preview","image","media/preview.png")+
                    Fixtures.Relationship("ole","oleObject","embeddings/object.bin"),
                binary:new(){["word/media/preview.png"]=png,["word/embeddings/object.bin"]=Fixtures.Compound()});
            parts["[Content_Types].xml"]=System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(parts["[Content_Types].xml"]).Replace("application/octet-stream",
                    "application/vnd.openxmlformats-officedocument.oleObject",StringComparison.Ordinal));
            var path=Fixtures.Write(root,name+".docx",parts);
            using var sdk=WordprocessingDocument.Open(path,false);
            var errors=new OpenXmlValidator().Validate(sdk).ToArray();
            Check(invalidShapeId?errors.Length==1&&errors[0].Description.Contains("ShapeID",StringComparison.Ordinal):errors.Length==0,
                "Aperçu OLE invalide dans le SDK. "+string.Join(" / ",errors.Select(e=>e.Description)));
            return DocumentInspector.InspectDocument(path);
        }
    }

    static void Refused(PageCompositionLease lease,string code)=>
        Check(lease.Snapshot.Coverage!=PageCoverage.Complete&&lease.Snapshot.Composition.Issues.Any(i=>i.Code==code),code);
    static PageCompositionSnapshot Complete(PageCompositionLease lease)
    {
        var s=lease.Snapshot;
        Check(s.Coverage==PageCoverage.Complete,$"{s.Coverage} {s.Pages.Length} "+
            string.Join(", ",s.Composition.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code+"@"+i.Source?.Path)));
        return s;
    }
    static bool Close(float a,float b)=>Math.Abs(a-b)<.02f;
    static void Check(bool yes,string message){if(!yes)throw new InvalidOperationException(message);}
    static string R(string text)=>"<w:r><w:t>"+text+"</w:t></w:r>";
    static string P(string runs,string props="")=>"<w:p>"+(props.Length==0?"":"<w:pPr>"+props+"</w:pPr>")+runs+"</w:p>";
    static string Section(string columns,string mode="nextPage")=>$"""
    <w:sectPr><w:type w:val="{mode}"/><w:pgSz w:w="6400" w:h="4800"/>
    <w:pgMar w:left="600" w:top="400" w:right="600" w:bottom="600" w:header="180" w:footer="180"/>{columns}</w:sectPr>
    """;
    static string Table(int count)=>"""
    <w:tbl><w:tblPr><w:tblW w:w="1600" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr><w:tblGrid><w:gridCol w:w="1600"/></w:tblGrid>
    """+string.Concat(Enumerable.Range(0,count).Select(i=>$"""
    <w:tr><w:trPr><w:trHeight w:val="{(i==0?400:880)}" w:hRule="atLeast"/>{(i==0?"<w:tblHeader/>":"")}</w:trPr>
    <w:tc><w:p><w:r><w:t>Rangée {i}</w:t></w:r></w:p></w:tc></w:tr>
    """))+"</w:tbl>";
    static string Picture(bool floating)
    {
        const string header="""
    <w:r><w:drawing xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
     xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
    """;
        string frame=floating?"""
    <wp:anchor simplePos="0" relativeHeight="1" behindDoc="0" locked="0" layoutInCell="1" allowOverlap="1">
    <wp:simplePos x="0" y="0"/><wp:positionH relativeFrom="column"><wp:posOffset>127000</wp:posOffset></wp:positionH>
    <wp:positionV relativeFrom="page"><wp:posOffset>1143000</wp:posOffset></wp:positionV><wp:extent cx="254000" cy="254000"/><wp:wrapNone/>
    """:"""<wp:inline><wp:extent cx="254000" cy="254000"/><wp:effectExtent l="12700" t="12700" r="25400" b="25400"/>""";
        return header+frame+"""
    <wp:docPr id="1" name="Image"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
    <pic:pic><pic:nvPicPr><pic:cNvPr id="1" name="Ressource"/><pic:cNvPicPr/></pic:nvPicPr>
    <pic:blipFill><a:blip r:embed="img"/><a:srcRect b="50000"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
    <pic:spPr><a:xfrm flipH="1"><a:off x="0" y="0"/><a:ext cx="254000" cy="254000"/></a:xfrm>
    <a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln></pic:spPr>
    </pic:pic></a:graphicData></a:graphic>
    """+(floating?"</wp:anchor>":"</wp:inline>")+"</w:drawing></w:r>";
    }
}
