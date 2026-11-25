using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    static string LineSpacing(string rule,int line)=>$"""<w:spacing w:lineRule="{rule}" w:line="{line}" w:before="0" w:after="0"/>""";

    internal static void ExactLineBoxes(string root)
    {
        const string image="""<w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><wp:extent cx="254000" cy="254000"/><wp:docPr id="1" name="Témoin"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="Test.png"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="image"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="254000" cy="254000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>""";
        using var picture=new SKBitmap(16,16);
        picture.Erase(SKColors.OrangeRed);
        using var encoded=picture.Encode(SKEncodedImageFormat.Png,100);
        string doc=Paragraph("",LineSpacing("exact",1))+
            Paragraph(CharacterRun("gyp","<w:color w:val=\"0055AA\"/>"),LineSpacing("exact",100))+
            P("gyp",LineSpacing("exact",600))+P("gyp",LineSpacing("atLeast",600))+P("gyp",LineSpacing("auto",360))+
            Paragraph(image,LineSpacing("exact",100))+Section(5600,6400);
        var input=DocumentInspector.InspectDocument(Fixtures.Write(root,"boites-exactes.docx",
            Fixtures.Parts(doc,new(),Fixtures.Relationship("image","image","media/image.png"),
                new(){["word/media/image.png"]=encoded.ToArray()})));
        using var lease=PageComposer.Compose(input);
        var s=lease.Snapshot;
        Complete(s,1);
        var lines=s.Composition.Pages[0].Lines;
        Check(Close(lines[0].Height,.05f)&&lines[0].Native!.Visual.IsEmpty,"La marque seule occupe exactement un twip.");
        Check(Close(lines[1].Height,5)&&Close(lines[1].Baseline+PageComposer.Points(lines[1].Native!.Descent!.Value),lines[1].NativeTop+5),
            "Le texte trop haut s’aligne sur le bas de sa boîte.");
        Check(Close(lines[2].Height,30)&&Close(lines[3].Height,30)&&
            Close(lines[4].Height,1.5f*PageComposer.Points(lines[4].Native!.Height!.Value)),
            "Exact agrandi, minimum et multiple automatique.");
        var tall=lines[2];
        Check(!tall.ClipToLineBox&&Close(tall.Baseline-tall.NativeTop,PageComposer.Points(tall.Native!.Ascent!.Value)+
            (tall.Height-PageComposer.Points(tall.Native.Height!.Value))/2),"La boîte naturelle est centrée dans la ligne exacte agrandie.");
        Check(PageCoordinates.Clusters(lines[1]).All(c=>c.Rect.Top>=lines[1].NativeTop&&c.Rect.Top+c.Rect.Height<=lines[1].NativeTop+5.01f),
            "Les rectangles de sélection restent dans la ligne écrêtée.");
        var bitmap=Composer.PagePng(s.Composition.Pages[0],3f);
        using var raster=SKBitmap.Decode(bitmap);
        int orange=0,blue=0;
        for(int y=0;y<raster.Height;y++)
        for(int x=0;x<raster.Width;x++)
            if(raster.GetPixel(x,y)==SKColors.OrangeRed)
            {
                orange++;
                Check(y>=Math.Floor(lines[5].NativeTop*3)&&y<Math.Ceiling((lines[5].NativeTop+5)*3),"L’image peint hors de la boîte.");
            }
            else if(raster.GetPixel(x,y) is {Red:<90,Blue:>100})
            {
                blue++;
                Check(y>=Math.Floor(lines[1].NativeTop*3)&&y<Math.Ceiling((lines[1].NativeTop+5)*3),"Le glyphe peint hors de la boîte.");
            }
        Check(blue>0,"La partie inférieure du texte écrêté doit être visible.");
        Check(orange>0&&lines[5].Images.Single().Y<lines[5].NativeTop,"La portion inférieure de l’image reste visible.");
        File.WriteAllBytes(Path.Combine(root,"interligne-exact.png"),bitmap);
        Check(Enum.GetValues<ExportFormat>().All(f=>!DocumentExports.Preflight(input,f).Allowed),"Document hors profil export.");

        var settings=new Dictionary<string,string>
        {
            ["word/settings.xml"]=Fixtures.Xml("settings","<w:compat><w:suppressBottomSpacing/></w:compat>")
        };
        using var incompatible=Compose(Fixtures.Write(root,"interligne-compatibilite.docx",Fixtures.Parts(
            P("Dernière ligne",LineSpacing("exact",600))+Section(),settings,Fixtures.Relationship("set","settings","settings.xml"))));
        Check(incompatible.Snapshot.Coverage!=PageCoverage.Complete&&incompatible.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_EXACT_BOTTOM_SPACING_UNQUALIFIED"&&i.BlocksRendering),"La règle de compatibilité conserve son refus.");
    }

    internal static void TablePageFields004(string root)
    {
        const string footers="""
            <w:footerReference w:type="first" r:id="first"/><w:footerReference w:type="default" r:id="odd"/>
            <w:footerReference w:type="even" r:id="even"/><w:headerReference w:type="first" r:id="header"/>
            """;
        string section=Section(5400,6400,margins:1400).Replace("<w:sectPr>","<w:sectPr>"+footers,StringComparison.Ordinal);
        string firstSection=PageNumbering(section,9).Replace("</w:sectPr>","<w:titlePg/></w:sectPr>",StringComparison.Ordinal);
        string body=Paragraph(Run("Neuf")+PaperBreak+Run("Dix")+PaperBreak+Run("Onze"),firstSection)+
            P("Reprise")+PageNumbering(section,4);
        string resultRun=CharacterRun("","<w:sz w:val=\"18\"/>");
        string complex=BeginDirty+Instruction(" PA")+Instruction(@"GE \* Arabic ")+Separator+resultRun+EndField;
        string controls=Paragraph(complex,"<w:jc w:val=\"center\"/>")+
            Paragraph(Run("P ")+BodyTab+SimpleField("PAGE",Run("99")),
                """<w:tabs><w:tab w:val="right" w:pos="1400" w:leader="dot"/></w:tabs>""")+
            Paragraph(SimpleField("PAGE",Run("77"),"w:fldLock=\"1\""));
        string path=Make("pieds-calcules",body,controls,2100);
        var input=DocumentInspector.InspectDocument(path);
        using var computed=PageComposer.Compose(input);
        var s=computed.Snapshot;
        Complete(s,4);
        Check(s.Pages.Select(p=>p.SectionPageNumber).SequenceEqual(new int?[]{9,10,11,4}),"Reprise et continuation.");
        Check(s.Pages.Select(p=>p.Geometry.RunningStories.Single(r=>r.Selection.Kind=="footer").Selection.Variant)
            .SequenceEqual(new[]{"first","even","default","even"}),"Première page, paire et impaire.");
        Check(Close(s.Pages[0].Geometry.RunningStories.Single(r=>r.Selection.Kind=="header").Lines.Single().Height,.05f),
            "L’en-tête à marque seule conserve sa hauteur.");
        foreach(var (page,number) in s.Pages.Zip(new[]{"9","10","11","4"}))
        {
            var footer=page.Geometry.RunningStories.Single(r=>r.Selection.Kind=="footer");
            var fields=footer.Lines.SelectMany(l=>l.Native!.Generated).Where(d=>d.Kind=="pageField").ToArray();
            var cell=footer.Cells.Single();
            Check(fields.Length==2&&fields.All(f=>f.Fragment.Text==number&&f.Cause.Span is null)&&fields[0].Fragment.PointSize==9,
                "Mesure et provenance des deux PAGE.");
            Check(footer.Lines[1].Text=="P \t"&&footer.Lines[2].Text=="77"&&footer.Lines.Take(2).All(l=>l.CellId==cell.CellId),
                "Cache verrouillé, cache remplacé et appartenance à la cellule.");
            Check(footer.Region.Top>=page.Origin.Body.Top+page.Origin.Body.Height&&
                Close(cell.Height,54)&&Close(cell.Width,105),"Pied, marges et rangée exacte.");
        }
        Check(s.Semantic!.Fields.Any(f=>s.Semantic.StoredText(f)=="99"),"Le cache d’origine reste dans le modèle.");
        RefuseHiddenExports(input,root);

        using var narrow=Compose(Make("pied-trop-etroit",P("Corps")+PageNumbering(section,10),
            Paragraph(SimpleField("PAGE",CharacterRun("","<w:sz w:val=\"50\"/>"))),250));
        Check(narrow.Snapshot.Coverage==PageCoverage.Partial&&
            narrow.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_TABLE_FIXED_CONTENT_TOO_WIDE"),"Numéro trop large.");
        using var overlap=Compose(Make("pied-chevauchant",P("Corps")+Section(5400,6400,margins:600)
            .Replace("<w:sectPr>","<w:sectPr>"+footers,StringComparison.Ordinal),Paragraph(SimpleField("PAGE")),2100));
        Check(overlap.Snapshot.Coverage==PageCoverage.Partial&&overlap.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STORY_OVERLAP"),
            "Collision avec le corps refusée.");
        using var incomplete=Compose(Make("page-malformee",P("Corps")+section,Paragraph(BeginDirty+Instruction(" PAGE ")),2100));
        Check(incomplete.Snapshot.Coverage==PageCoverage.Partial,"Délimitation de champ indispensable.");
        string many=Paragraph(string.Join(PaperBreak,Enumerable.Range(1,129).Select(i=>Run(i.ToString()))) )+section;
        using var budget=Compose(Make("quota-valeurs",many,Paragraph(SimpleField("PAGE")),2100));
        Check(budget.Snapshot.Coverage==PageCoverage.Partial&&budget.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STORY_FIELD_VALUE_LIMIT"),
            "Le nombre de variantes mesurées reste borné.");
        using var cancel=new CancellationTokenSource();
        cancel.Cancel();
        try { using var unused=PageComposer.Compose(input,cancel.Token); throw new InvalidOperationException("Annulation ignorée."); }
        catch(OperationCanceledException) {}

        string Make(string name,string content,string cell,int width)
        {
            string row=$"""
                <w:tbl><w:tblPr><w:tblW w:w="{width}" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr>
                  <w:tblGrid><w:gridCol w:w="{width}"/></w:tblGrid>
                  <w:tr><w:trPr><w:trHeight w:hRule="exact" w:val="1080"/></w:trPr><w:tc><w:tcPr>
                    <w:tcW w:w="{width}" w:type="dxa"/><w:tcMar><w:left w:w="40" w:type="dxa"/><w:right w:w="40" w:type="dxa"/></w:tcMar>
                  </w:tcPr>{cell}</w:tc></w:tr>
                </w:tbl>
                """;
            var parts=new Dictionary<string,string>{
                ["word/footer-first.xml"]=Fixtures.Xml("ftr",row),["word/footer-even.xml"]=Fixtures.Xml("ftr",row),
                ["word/footer-odd.xml"]=Fixtures.Xml("ftr",row),
                ["word/header.xml"]=Fixtures.Xml("hdr",Paragraph("",LineSpacing("exact",1))),
                ["word/settings.xml"]=Fixtures.Xml("settings","<w:evenAndOddHeaders/>")};
            return Fixtures.Write(root,name+".docx",Fixtures.Parts(content,parts,
                Fixtures.Relationship("first","footer","footer-first.xml")+Fixtures.Relationship("even","footer","footer-even.xml")+
                Fixtures.Relationship("odd","footer","footer-odd.xml")+Fixtures.Relationship("header","header","header.xml")+
                Fixtures.Relationship("set","settings","settings.xml")));
        }
    }

    internal static void StoryDiagnosticBudget(string root)
    {
        string proof=string.Concat(Enumerable.Repeat("""<w:proofErr w:type="spellStart"/><w:proofErr w:type="spellEnd"/>""",2050));
        string prefix=Paragraph(proof+Run("Texte"));
        using var complete=Compose(Write(root,"plafond-avis",prefix+Section()));
        Complete(complete.Snapshot,1);
        Check(complete.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_INFORMATION_LIMIT"&&!i.BlocksRendering)&&
            complete.Snapshot.RulerModel!.Issues.Count(i=>i.Code=="LAYOUT_INFORMATIONAL_MARKER")==4100,
            "Le plafond des avis conserve l’inventaire.");
        using var refused=Compose(Write(root,"plafond-avis-erreur",prefix+
            Paragraph(CharacterRun("Après","<w:strike/>"))+Section()));
        Check(refused.Snapshot.Coverage==PageCoverage.Partial&&refused.Snapshot.Composition.Issues.Any(i=>
            i.Code=="LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED"&&i.BlocksRendering),"Un blocage après le plafond reste qualifié.");
    }
}
