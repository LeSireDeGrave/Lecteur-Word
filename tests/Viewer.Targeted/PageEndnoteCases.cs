using LecteurWord.Flow;

static partial class PageCases
{
    internal static void EndnotePlacement(string root)
    {
        foreach(string position in new[]{"sectEnd","docEnd"})
        {
            using var lease=Compose(Fixture(position,continued:false));
            var s=lease.Snapshot;
            Complete(s,2);
            var first=s.Composition.Pages[0];
            var last=s.Composition.Pages[1];
            var endnotes=s.Composition.Pages.SelectMany(p=>p.Footnotes.Where(n=>n.IsEndnote&&n.Call is not null)).ToArray();
            Check(endnotes.Select(n=>n.Lines[0].Native!.Generated.Single().Fragment.Text).SequenceEqual(new[]{"iv","e"}),
                "Numérotation romaine par défaut, puis format et redémarrage de section.");
            Check(first.Footnotes.Any(n=>!n.IsEndnote&&n.Call is not null)&&
                first.Footnotes.Count(n=>n.IsEndnote&&n.Call is not null)==(position=="sectEnd"?1:0)&&
                last.Footnotes.Count(n=>n.IsEndnote&&n.Call is not null)==(position=="sectEnd"?1:2),
                "Les appels de bas de page et de fin ont leurs propres régions et destinations.");
            Check(s.FieldStability=="Stable"&&s.FieldPasses[^1].Fields.Single().Text=="2"&&
                Composer.PagePng(last).Length>0,"Le nombre de feuilles inclut les régions de notes.");
            foreach(var page in s.Composition.Pages)
            {
                long bottom=PageComposer.Emu(page.Lines.Max(l=>l.NativeTop+l.Height));
                foreach(var note in page.Footnotes)
                {
                    Check(note.Region.Top>=bottom-128,"Les textes, les séparateurs et les notes occupent des régions distinctes.");
                    bottom=note.Region.Top+note.Region.Height;
                }
            }
        }
        using(var lease=Compose(Fixture("docEnd",continued:true)))
        {
            var s=lease.Snapshot;
            Complete(s);
            var pageNotes=s.Composition.Pages.SelectMany(p=>p.Footnotes.Where(n=>n.IsEndnote)).ToArray();
            var fragments=pageNotes.Where(n=>n.Call is not null&&n.Call==pageNotes.First(n=>n.Call is not null).Call).ToArray();
            Check(fragments.Length>1&&fragments.Skip(1).All(n=>n.Continued)&&
                fragments.SelectMany(n=>n.Lines).Count()==17&&
                pageNotes.Any(n=>n.Call is null&&n.Lines.Single().Text=="Suite"),
                "La note longue garde ses dix-sept paragraphes et son séparateur de continuation.");
            Check(s.FieldPasses[^1].Fields.Single().Text==s.Pages.Length.ToString(),
                "Le champ est stabilisé avec les feuilles de continuation.");
        }
        {
            string page=Section(width:4200,height:5000,margins:800).Replace("<w:sectPr>","""
                <w:sectPr><w:headerReference w:type="default" r:id="head"/><w:footerReference w:type="default" r:id="foot"/>
                """,StringComparison.Ordinal);
            string firstSection=Paragraph(Run("Première section"),page);
            string footer=Paragraph(Run("Page ")+Complex("PAGE")+Run(" de ")+Complex("NUMPAGES"));
            string header=Paragraph(Run("En-tête bordé"),"""
                <w:pBdr><w:bottom w:val="single" w:sz="4" w:space="4" w:color="999999"/></w:pBdr>
                """);
            string package=Fixtures.Write(root,"histoires-champs-multiples.docx",Fixtures.Parts(
                firstSection+P("Section suivante")+Section(width:4200,height:5000,margins:800),new()
                {
                    ["word/header1.xml"]=Fixtures.Xml("hdr",header),["word/footer1.xml"]=Fixtures.Xml("ftr",footer)
                },Fixtures.Relationship("head","header","header1.xml")+Fixtures.Relationship("foot","footer","footer1.xml")));
            using var lease=Compose(package);
            var s=lease.Snapshot;
            Complete(s,2);
            foreach(var sheet in s.Composition.Pages)
            {
                var run=sheet.RunningStories.Single(r=>r.Selection.Kind=="footer").Lines.Single();
                Check(run.Native!.Generated.Select(g=>g.Fragment.Text).SequenceEqual(new[]{sheet.Number.ToString(),"2"})&&
                    run.Native.Generated[0].X<run.Native.Generated[1].X,"Les deux résultats gardent leur ordre dans le pied.");
                var upper=sheet.RunningStories.Single(r=>r.Selection.Kind=="header");
                var upperLine=upper.Lines.Single();
                Check(upperLine.ParagraphBorder is {} border&&border.Top>upperLine.NativeTop&&
                    Math.Abs(PageComposer.Points(upper.Region.Top+upper.Region.Height)-(border.Top+border.Border.Width))<.01f,
                    "La bordure est translatée avec l’en-tête et réservée dans sa région.");
            }

            static string Complex(string name)=>$"""
                <w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText>{name}</w:instrText></w:r>
                <w:r><w:fldChar w:fldCharType="end"/></w:r>
                """;
        }
        {
            string body=P("Feuille presque pleine","""<w:spacing w:line="2900" w:lineRule="exact"/>""")+
                P("Titre solidaire","""<w:keepNext/>""")+
                Paragraph(Run("Paragraphe avec un appel")+"""<w:r><w:footnoteReference w:id="1"/></w:r>""","""
                    <w:pBdr><w:left w:val="single" w:sz="12" w:space="8" w:color="7F9DB9"/></w:pBdr>
                    """)+Section(width:4700,height:4600,margins:500);
            string special=FontResolver.IsInstalled("Noto Color Emoji")?" 👩‍💻":"";
            var parts=Fixtures.Parts(body,new(){["word/footnotes.xml"]=Fixtures.Xml("footnotes",$"""
                <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
                <w:footnote w:id="1"><w:p><w:r><w:footnoteRef/><w:t xml:space="preserve"> Note liée{special}</w:t></w:r></w:p></w:footnote>
                """)},Fixtures.Relationship("foot","footnotes","footnotes.xml"));
            using var lease=Compose(Fixtures.Write(root,"note-reserve-titre-bordure.docx",parts));
            var s=lease.Snapshot;
            Complete(s,2);
            var page=s.Composition.Pages[1];
            Check(page.Lines.Select(l=>l.Text).SequenceEqual(new[]{"Titre solidaire","Paragraphe avec un appel"})&&
                page.Lines[1].LeftBorder is {Border.Color:"7F9DB9",Border.Space:8} border&&border.X<page.Lines[1].X&&
                page.Footnotes.Count(n=>n.Call is not null)==1,"La réserve déplace le titre et le paragraphe bordé avec leur note.");
            Check(Composer.PagePng(page).Length>0,"Le trait latéral et la note sont dessinés.");
            if(special.Length>0)
                Check(page.Footnotes.SelectMany(n=>n.Lines).SelectMany(l=>l.Native!.Parts)
                    .Any(p=>p.Fragment is {Face.UsedFamily:"Noto Color Emoji",Measure:not null} f&&
                        f.Text.Contains("👩‍💻",StringComparison.Ordinal)&&f.Measure.Glyphs.All(g=>g.Id!=0)),
                    "Le graphe emoji de la note utilise la face locale en conservant ses glyphes.");
        }

        string Fixture(string position,bool continued)
        {
            string page=Section(width:4200,height:4600,margins:500);
            string last=page.Replace("<w:sectPr>","""
                <w:sectPr><w:endnotePr><w:numFmt w:val="lowerLetter"/><w:numStart w:val="5"/><w:numRestart w:val="eachSect"/></w:endnotePr>
                """,StringComparison.Ordinal);
            string body=Paragraph("""<w:fldSimple w:instr="NUMPAGES"/>""")+
                Paragraph(Run("Premier appel")+"""<w:r><w:endnoteReference w:id="2"/></w:r>"""+Run(" et bas de page")+
                    """<w:r><w:footnoteReference w:id="2"/></w:r>""",page)+
                Paragraph(Run("Second appel")+"""<w:r><w:endnoteReference w:id="3"/></w:r>""")+last;
            string longNote=continued?string.Concat(Enumerable.Range(1,16).Select(i=>
                P($"Paragraphe {i:D2}","""<w:spacing w:line="400" w:lineRule="exact"/>"""))):"";
            var parts=new Dictionary<string,string>
            {
                ["word/settings.xml"]=Fixtures.Xml("settings",$"""
                    <w:endnotePr><w:pos w:val="{position}"/><w:numStart w:val="4"/><w:endnote w:id="-1"/><w:endnote w:id="0"/></w:endnotePr>
                    """),
                ["word/footnotes.xml"]=Fixtures.Xml("footnotes","""
                    <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
                    <w:footnote w:id="2"><w:p><w:r><w:footnoteRef/><w:t xml:space="preserve"> Bas de page</w:t></w:r></w:p></w:footnote>
                    """),
                ["word/endnotes.xml"]=Fixtures.Xml("endnotes",$"""
                    <w:endnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:endnote>
                    <w:endnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:t>Suite</w:t></w:r></w:p></w:endnote>
                    <w:endnote w:id="2"><w:p><w:r><w:endnoteRef/><w:t xml:space="preserve"> Première note</w:t></w:r></w:p>{longNote}</w:endnote>
                    <w:endnote w:id="3"><w:p><w:r><w:endnoteRef/><w:t xml:space="preserve"> Autre note</w:t></w:r></w:p></w:endnote>
                    """)
            };
            return Fixtures.Write(root,$"notes-{position}-{continued}.docx",Fixtures.Parts(body,parts,
                Fixtures.Relationship("options","settings","settings.xml")+Fixtures.Relationship("foot","footnotes","footnotes.xml")+
                Fixtures.Relationship("end","endnotes","endnotes.xml")));
        }
    }
}
