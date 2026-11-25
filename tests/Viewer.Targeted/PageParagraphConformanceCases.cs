using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    internal static void ParagraphConformance(string root)
    {
        const string align = """
            <w:tabs><w:tab w:val="right" w:pos="2100" w:leader="dot"/></w:tabs><w:jc w:val="center"/>
            """;
        const string box = """
            <w:pBdr><w:top w:val="single" w:sz="8" w:space="2" w:color="B01010"/>
              <w:left w:val="single" w:sz="8" w:space="2" w:color="B01010"/>
              <w:bottom w:val="single" w:sz="8" w:space="2" w:color="B01010"/>
              <w:right w:val="single" w:sz="8" w:space="2" w:color="B01010"/>
            </w:pBdr><w:shd w:val="clear" w:fill="C6DCF2"/><w:ind w:left="140" w:right="140"/>
            """;
        const string template = """
            <w:endnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:endnote>
            <w:endnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:endnote>
            <w:endnote w:type="continuationNotice" w:id="1"><w:p><w:r><w:t>Non instancié</w:t></w:r></w:p></w:endnote>
            """;
        string body = Paragraph("<w:r><w:rPr><w:caps/></w:rPr><w:t>Été</w:t><w:tab/></w:r>"+Run("Bord"),align)
            + Paragraph("<w:r><w:rPr><w:smallCaps/></w:rPr><w:t>Az</w:t></w:r>",box)
            + P("Suite") + Section(width:6200,height:5800,margins:800);
        using var normal=Open("tab-capitales-cadre",false);
        using var compat=Open("tab-capitales-ignore-dernier",true);
        Complete(normal.Snapshot,1);
        Complete(compat.Snapshot,1);
        var a=Line(normal.Snapshot,"Été	Bord").Native!;
        var b=Line(compat.Snapshot,"Été	Bord").Native!;
        var stop=a.Tabs.Single();
        var compatStop=b.Tabs.Single();
        long width=stop.To-stop.From;
        Check(a.Occupied==b.Occupied&&width>0&&Math.Abs(compatStop.From-stop.From-width/2)<=1&&
            Math.Abs(compatStop.To-stop.To-width/2)<=1&&
            Math.Abs(b.Generated.Single(d=>d.Kind=="leader").X-a.Generated.Single(d=>d.Kind=="leader").X-width/2)<=1,
            "L’option modifie l’alignement et transporte le taquet, sa suite et les glyphes ensemble.");
        var line = Line(normal.Snapshot,"Az");
        var fragments=line.Native!.Parts.Select(p=>p.Fragment).OfType<TextFragment>().ToArray();
        var capitals=a.Parts.Select(p=>p.Fragment).OfType<TextFragment>().Where(f=>f.Style.Caps).ToArray();
        Check(fragments is [{Text:"A",PointSize:12},{Text:"z",PointSize:10}]&&
            fragments[1].Sources.Single().Source.Span is {Start:1,EndExclusive:2}&&
            string.Concat(capitals.Select(f=>f.Text))=="Été"&&capitals.All(f=>f.PointSize==12),
            "Les tailles et les étendues source restent distinctes de la casse peinte. " +
            string.Join(", ",fragments.Select(f=>$"{f.Text} à {f.PointSize} pt")));
        Check(line is {ParagraphTopBorder.Border.Color:"B01010",ParagraphBorder.Border.Color:"B01010",
            LeftBorder:not null,RightBorder:not null,ParagraphShading.Shading.Color:"C6DCF2"}&&
            normal.Snapshot.Semantic!.Stories.Any(n=>n.Kind=="endnote"&&n.DeclaredId=="1")&&
            normal.Snapshot.Pages.Single().Geometry.Footnotes.Count==0&&
            normal.Snapshot.Composition.Issues.Any(i=>i.Code=="LAYOUT_EMPTY_NOTE_PART"&&!i.BlocksRendering),
            "Les bordures sont placées. Les modèles de notes sans appel restent inventoriés.");
        using var img=SKBitmap.Decode(Composer.PagePng(normal.Snapshot.Pages.Single().Geometry,2));
        Check(img.Pixels.Any(c=>c is {Red:198,Green:220,Blue:242})&&img.Pixels.Any(c=>c is {Red:176,Green:16,Blue:16}),
            "La trame et la bordure sont réellement dessinées.");

        PageCompositionLease Open(string name,bool forget) => Compose(Fixtures.Write(root,name+".docx",
            Fixtures.Parts(body,new()
            {
                ["word/settings.xml"]=Fixtures.Xml("settings",forget?"<w:compat><w:forgetLastTabAlignment/></w:compat>":""),
                ["word/endnotes.xml"]=Fixtures.Xml("endnotes",template)
            },Fixtures.Relationship("st","settings","settings.xml")+Fixtures.Relationship("ed","endnotes","endnotes.xml"))));
    }
}
