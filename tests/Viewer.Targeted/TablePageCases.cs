using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static class TablePageCases
{
    const string Section="""
        <w:sectPr><w:pgSz w:w="8000" w:h="7600"/><w:pgMar w:top="400" w:bottom="400" w:left="1000" w:right="1000"/></w:sectPr>
        """;
    static void Check(bool value,string message)=>LineCases.Check(value,message);
    static string Paragraph(string text,string properties="",string character="")=>
        "<w:p>"+properties+"<w:r>"+character+"<w:t xml:space=\"preserve\">"+text+"</w:t></w:r></w:p>";
    static string Cell(string text,string properties="",string para="",string character="")=>
        "<w:tc><w:tcPr>"+properties+"</w:tcPr>"+Paragraph(text,para,character)+"</w:tc>";
    static string Row(string cells)=>"<w:tr>"+cells+"</w:tr>";
    static string Width(string value,string unit="dxa")=>$"""<w:tcW w:w="{value}" w:type="{unit}"/>""";
    static string Table(string rows,string width="",string unit="dxa",bool fixedGrid=true,int[]? grid=null,string extras="")=>
        "<w:tbl><w:tblPr>"+(width.Length==0?"":$"""<w:tblW w:w="{width}" w:type="{unit}"/>""")+
        (fixedGrid?"""<w:tblLayout w:type="fixed"/>""":"")+extras+"</w:tblPr>"+
        (grid is null?"":"<w:tblGrid>"+string.Concat(grid.Select(w=>$"""<w:gridCol w:w="{w}"/>"""))+"</w:tblGrid>")+rows+"</w:tbl>";
    static InspectionResult Input(string root,string id,string body,Dictionary<string,string>? parts=null,string relationships="",
        Dictionary<string,byte[]>? binary=null)=>
        DocumentInspector.InspectDocument(Fixtures.Write(root,id+".docx",Fixtures.Parts(body+Section,parts,relationships,binary)));
    static bool Close(float a,float b)=>Math.Abs(a-b)<.04f;
    static LayoutTable Solved(PageCompositionLease lease)
    {
        var s=lease.Snapshot;
        Check(s.Coverage==PageCoverage.Complete&&s.Composition.Available,
            "Tableau synthétique non qualifié. "+string.Join(" | ",s.Composition.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code+" "+i.Message).Take(7)));
        PageCases.VerifyPlaced(s);
        return s.RulerModel!.Tables.Single();
    }

    internal static void Widths(string root)
    {
        using var over=PageComposer.Compose(Input(root,"declared-overhang",Table(Row(Cell("Alpha",Width("3100"))+Cell("Bêta",Width("3100"))),
            "6200",grid:[3100,3100],extras:"""<w:jc w:val="center"/>""")));
        var resolved=Solved(over);
        var positions=over.Snapshot.Composition.Pages[0].Cells;
        Check(resolved.WidthResult is {Overhang:true,ContentVerified:true}&&Close(positions.Min(c=>c.X),45)&&
            Close(positions.Max(c=>c.X+c.Width),355)&&resolved.Columns.SequenceEqual(new float[]{155,155}),
            "Une largeur fixe 310 sur 300 pt doit conserver ses dimensions et son centrage.");
        using var signed=PageComposer.Compose(Input(root,"declared-negative-indent",Table(Row(Cell("Retrait")),
            "6000",grid:[6000],extras:"""<w:tblInd w:w="-5" w:type="dxa"/>""")));
        Check(Close(Solved(signed).Indent,-.25f)&&Close(signed.Snapshot.Composition.Pages[0].Cells[0].X,49.75f),"Retrait signé.");

        string margin=Table(Row(Cell("Repère de cellule","""
            <w:tcMar><w:left w:w="180" w:type="dxa"/></w:tcMar>
            """)), "4800",grid:[4800],extras:"""<w:tblInd w:w="40" w:type="dxa"/>""");
        foreach(int mode in new[]{12,14,15})
        {
            using var aligned=PageComposer.Compose(Input(root,"word-table-indent-"+mode,margin,new()
            {
                ["word/settings.xml"]=Fixtures.Xml("settings",$"""
                    <w:compat><w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="{mode}"/></w:compat>
                    """)
            },Fixtures.Relationship("settings","settings","settings.xml")));
            var solved=Solved(aligned);
            var page=aligned.Snapshot.Pages.Single().Geometry;
            Check(Close(solved.LeadingCellPadding,mode==14?9:0)&&Close(page.Cells.Single().Width,240)&&
                Close(page.Cells.Single().X,mode==14?43:52)&&Close(page.Lines.Single().X,mode==14?52:61),
                "Mode 14 explicite : le retrait détermine l’origine du texte de la cellule, grille et largeur conservées.");
        }

        using var pref=PageComposer.Compose(Input(root,"fixed-natural",Table(Row(Cell("Préférence",Width("6000"))),grid:[5600])));
        Check(Close(Solved(pref).Columns.Single(),300),"La préférence de cellule agrandit la grille fixe sans tblW.");
        using var conflict=PageComposer.Compose(Input(root,"fixed-contradictory",Table(Row(Cell("Un",Width("3500"))+Cell("Deux",Width("1000"))),
            "4000",grid:[2000,2000])));
        var b=Solved(conflict);
        Check(b.Columns.SequenceEqual(new float[]{100,100})&&b.WidthResult!.Cells[0] is {Preferred:175,Effective:100},
            "Préférence contradictoire résolue avec grille et tblW concordants.");

        using var percent=PageComposer.Compose(Input(root,"fixed-pct-grid",
            Table(Row(Cell("Un",Width("3000","pct"))+Cell("Deux",Width("40%","pct"))),"75%","pct",grid:[0])));
        var p=Solved(percent);
        Check(p.Columns.Count==2&&Close(p.Columns[0],135)&&Close(p.Columns[1],90)&&
            percent.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_TABLE_GRID_EXTENDED"),"Grille incomplète et unités pct.");
        using var auto=PageComposer.Compose(Input(root,"auto-pct-grid",
            Table(Row(Cell("mot",Width("60%","pct"))+Cell("suite",Width("2000","pct"))),"2500","pct",false)));
        var a=Solved(auto);
        Check(Close(a.WidthResult!.Effective,150)&&Close(a.Columns[0],90)&&Close(a.Columns[1],60),"Pourcentages AutoFit relatifs à 150 pt.");

        const string noWrap="""<w:noWrap/>""";
        using var protectedCell=PageComposer.Compose(Input(root,"auto-nowrap-dxa",Table(Row(
            Cell("Un",Width("3200")+noWrap)+Cell("I",Width("2400"))),"4000",fixedGrid:false,grid:[2000,2000])));
        var protectedFit=Solved(protectedCell);
        Check(Close(protectedFit.Columns[0],160)&&Close(protectedFit.Columns[1],40),
            "AutoFit protège la préférence dxa avant de distribuer le surplus aux autres cellules. "+
            string.Join(", ",protectedFit.Columns));
        using var contiguous=PageComposer.Compose(Input(root,"auto-nowrap-pct",Table(Row(
            Cell("Quatre mots sur une seule ligne",Width("20%","pct")+noWrap)+Cell("Fin",Width("80%","pct"))),
            "4000",fixedGrid:false,grid:[2000,2000])));
        var measured=contiguous.Snapshot;
        var contiguousTable=Solved(contiguous);
        var unwrappedCell=contiguousTable.Cells[0];
        Check(contiguousTable.WidthResult!.Cells[0].Minimum>100&&
            measured.Composition.Pages.Single().Lines.Count(l=>l.CellId==unwrappedCell.Id)==1,
            "La largeur minimale inclut tous les mots lorsque la préférence de cellule est un pourcentage.");

        string multi=Row(Cell(string.Join(' ',Enumerable.Repeat("long",20)),Width("10000"))+Cell("Fin",Width("1200")));
        using var longText=PageComposer.Compose(Input(root,"auto-wrapping",Table(multi,"11000",fixedGrid:false,grid:[1000,1000])));
        var fit=Solved(longText);
        Check(Close(fit.Columns.Sum(),300)&&fit.WidthResult!.Minimum<300&&fit.WidthResult.Preferred>300&&
            longText.Snapshot.Composition.Pages[0].Lines.Count>2,"Une préférence sécable plus large que le corps permet le retour à la ligne.");

        string merged=Row(Cell("Origine",Width("5000")+"""<w:gridSpan w:val="2"/>""")+Cell("Droit",Width("1000")))+
            Row(Cell("Gauche",Width("1000"))+Cell("Fusion",Width("5000")+"""<w:gridSpan w:val="2"/>"""));
        using var merge=PageComposer.Compose(Input(root,"auto-spans",Table(merged,"6000",fixedGrid:false,grid:[2000,2000,2000])));
        var spans=Solved(merge);
        Check(spans.Cells.Count(c=>c.ColumnSpan==2)==2&&Close(spans.Columns.Sum(),300)&&spans.WidthResult!.ContentVerified,"Contraintes de spans partagés.");

        Expect("paper-impossible",Table(Row(Cell("Visible")),"10000",grid:[10000]),"PAGE_TABLE_OUTSIDE_PAPER");
        Expect("fixed-unsplittable",Table(Row(Cell(new string('W',40))),"2000",grid:[2000]),"PAGE_TABLE_FIXED_CONTENT_TOO_WIDE");
        Expect("auto-unsplittable",Table(Row(Cell(new string('W',40))),fixedGrid:false,grid:[4000]),"LAYOUT_AUTOFIT_MINIMUM_TOO_WIDE");
        Expect("columns-budget",Table(Row(string.Concat(Enumerable.Repeat(Cell("A"),33)))),"PAGE_TABLE_COLUMN_LIMIT");
        Expect("floating-table",Table(Row(Cell("Ancre")),"2000",grid:[2000],
            extras:"""<w:tblpPr w:horzAnchor="margin" w:tblpX="60"/>"""),"PAGE_TABLE_FLOAT_PENDING");
        using var canceled=new CancellationTokenSource();
        canceled.Cancel();
        bool stopped=false;
        try{using var late=PageComposer.Compose(Input(root,"cancel-table",Table(Row(Cell("A")),grid:[2000])),canceled.Token);}
        catch(OperationCanceledException){stopped=true;}
        Check(stopped,"Annulation de la composition des tableaux.");
        using var running=new CancellationTokenSource();
        var paragraphs=over.Snapshot.RulerModel!.Paragraphs.ToDictionary(p=>p.Id);
        int calls=0;
        stopped=false;
        try
        {
            Composer.FitPageTable(resolved,paragraphs,()=>
            {
                if(++calls==4)running.Cancel();
                running.Token.ThrowIfCancellationRequested();
            },_ => (10,20));
        }
        catch(OperationCanceledException){stopped=true;}
        Check(stopped&&calls==4,"Annulation pendant l’arbitrage des contraintes.");

        void Expect(string name,string body,string code)
        {
            using var failure=PageComposer.Compose(Input(root,name,body));
            var s=failure.Snapshot;
            Check(s.Coverage==PageCoverage.Fallback&&s.Composition.Issues.Any(i=>i.Code==code&&i.Source is not null)&&
                s.Typography!.Tokens.Any(t=>t.Original is {Length:>0}),"Refus attendu "+code);
        }
    }

    internal static void Styles(string root)
    {
        string styleXml=Fixtures.Xml("styles","""
            <w:style w:type="table" w:styleId="Base">
              <w:tblPr><w:tblStyleRowBandSize w:val="1"/><w:tblStyleColBandSize w:val="1"/></w:tblPr>
              <w:rPr><w:sz w:val="20"/></w:rPr>
              <w:tblStylePr w:type="firstRow"><w:rPr><w:sz w:val="32"/><w:b/></w:rPr>
                <w:pPr><w:spacing w:before="160"/></w:pPr>
                <w:trPr><w:trHeight w:val="760"/></w:trPr>
                <w:tcPr><w:tcMar><w:left w:w="240" w:type="dxa"/></w:tcMar></w:tcPr>
              </w:tblStylePr>
              <w:tblStylePr w:type="firstCol"><w:rPr><w:sz w:val="24"/><w:b/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="lastCol"><w:rPr><w:i/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="band1Horz"><w:tcPr><w:shd w:fill="FFEEEE" w:val="clear"/></w:tcPr></w:tblStylePr>
              <w:tblStylePr w:type="band2Horz"><w:rPr><w:sz w:val="26"/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="band1Vert"><w:rPr><w:i/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="band2Vert"><w:rPr><w:color w:val="305070"/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="lastRow"><w:rPr><w:sz w:val="28"/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="seCell"><w:rPr><w:sz w:val="34"/></w:rPr></w:tblStylePr>
            </w:style>
            <w:style w:type="table" w:styleId="Styled"><w:basedOn w:val="Base"/>
              <w:tblStylePr w:type="nwCell"><w:rPr><w:sz w:val="36"/><w:color w:val="224466"/></w:rPr></w:tblStylePr>
            </w:style>
            """);
        string extras="""
            <w:tblStyle w:val="Styled"/><w:tblLook w:val="01E0" w:firstRow="1" w:lastRow="1" w:firstColumn="1" w:lastColumn="1"/>
            """;
        string rows=Row(Cell("Coin")+Cell("Entete")+Cell("Direct",character:"""<w:rPr><w:sz w:val="22"/><w:b w:val="0"/></w:rPr>""")+Cell("Finr"))+
            Row(Cell("Gauche")+Cell("Fond")+Cell("Teinte")+Cell("Droite"))+
            Row(Cell("Masque", """<w:cnfStyle w:val="000001000000"/>""")+Cell("Paire")+Cell("Paragr",para:
                """<w:pPr><w:cnfStyle w:firstRow="1"/><w:spacing w:before="300"/></w:pPr>""")+Cell("Troisieme"))+
            Row(Cell("Bas")+Cell("Basdeux")+Cell("Bastrois")+Cell("Coinfin"));
        using var lease=PageComposer.Compose(Input(root,"conditional",Table(rows,"6800",grid:[1700,1700,1700,1700],extras:extras),
            new(){["word/styles.xml"]=styleXml},Fixtures.Relationship("s","styles","styles.xml")));
        var table=Solved(lease);
        var set=lease.Snapshot.Typography!.Tokens.Where(t=>t.Original is not null).ToDictionary(t=>t.Original!,t=>t.Style!);
        Check(set["Coin"] is {Size:18,Bold:true,Color:"224466"}&&set["Coin"].Origins["size"].Location.PartUri=="/word/styles.xml"&&
            set["Entete"].Size==16&&set["Direct"] is {Size:11,Bold:false}&&set["Coinfin"].Size==17,
            "Cascade du coin, de la rangée, des colonnes et surcharge directe.");
        Check(set["Gauche"].Size==12&&set["Fond"].Italic&&set["Teinte"].Color=="305070"&&set["Droite"].Italic&&
            set["Masque"] is {Bold:false,Color:"305070"}&&set["Paire"].Size==13&&set["Paragr"].Size==16&&set["Bas"].Size==14,
            "Bandes horizontales/verticales et masques de cellule/paragraphe.");
        var geometry=lease.Snapshot.Composition.Pages[0].Cells;
        Check(table.Cells[0].Margins.Left==12&&table.Rows[0].Height==38&&table.Cells[5].Fill=="FFEEEE"&&
            table.Cells[0].AppliedTableStyles.Length>=3&&geometry[0].Height>=38,
            "Résolution des styles géométriques avant les mesures et placement.");
        Check(WordColor.ApplyLuminance("FF0000","80",null)=="FF7F7F"&&WordColor.ApplyLuminance("808080",null,"80")=="404040",
            "Luminance de thème.");
        File.WriteAllBytes(Path.Combine(root,"table-style-page.png"),Composer.PagePng(lease.Snapshot.Composition.Pages[0],1.5f));
    }

    internal static void Image(string root)
    {
        using var bitmap=new SKBitmap(12,8);bitmap.Erase(SKColors.OrangeRed);
        using var image=SKImage.FromBitmap(bitmap);using var file=image.Encode(SKEncodedImageFormat.Png,100);
        const string drawing="""
            <w:drawing xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
              <wp:inline><wp:extent cx="508000" cy="254000"/><wp:docPr id="1" name="Témoin"/>
              <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>
              <pic:blipFill><a:blip r:embed="p"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
              <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="508000" cy="254000"/></a:xfrm><a:prstGeom prst="rect"/></pic:spPr>
              </pic:pic></a:graphicData></a:graphic></wp:inline>
            </w:drawing>
            """;
        string table=Table(Row("<w:tc><w:p><w:r>"+drawing+"</w:r></w:p></w:tc>"+Cell("Texte")),"4000",fixedGrid:false,grid:[2000,2000]);
        using var lease=PageComposer.Compose(Input(root,"cell-image",table,relationships:Fixtures.Relationship("p","image","media/p.png"),
            binary:new(){["word/media/p.png"]=file.ToArray()}));
        var resolved=Solved(lease);
        Check(resolved.WidthResult!.Cells[0].Minimum>=50&&
            lease.Snapshot.Composition.Pages[0].Lines.SelectMany(l=>l.Images).Single().Image is {Width:40,Height:20},"Image de cellule mesurée sans redimensionnement.");
        File.WriteAllBytes(Path.Combine(root,"cell-image-page.png"),Composer.PagePng(lease.Snapshot.Composition.Pages[0],1.5f));
    }
}
