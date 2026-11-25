using System.Security.Cryptography;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class PageCases
{
    internal static void StoryPageMergeFormat(string root)
    {
        const string mergeFormat = """ PAGE   \* MERGEFORMAT """;
        const string properties = """<w:b/><w:color w:val="1D4570"/><w:w w:val="90"/>""";
        const string pageBreak = """<w:r><w:br w:type="page"/></w:r>""";
        const string header = """<w:headerReference w:type="default" r:id="head"/>""";
        string section = Section(5000,5000,margins:1000).Replace("<w:sectPr>","<w:sectPr>"+header,StringComparison.Ordinal)
            .Replace("</w:sectPr>","""<w:pgNumType w:start="9"/></w:sectPr>""",StringComparison.Ordinal);
        string body = Paragraph(Run("Première feuille")+pageBreak+Run("Seconde feuille"))+section;
        string right = """<w:jc w:val="right"/>""";
        string stored = CharacterRun("11",properties);
        string source = Package("page-style-conserve",Field(mergeFormat,stored));
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        var input = DocumentInspector.InspectDocument(source);
        using (var lease = PageComposer.Compose(input))
        {
            var snapshot = lease.Snapshot;
            Check(snapshot.Coverage == PageCoverage.Complete,
                "Le PAGE avec MERGEFORMAT doit être placé. " + string.Join(", ",snapshot.Composition.Issues.Select(i=>i.Code)));
            Complete(snapshot,2);
            var generated = snapshot.Composition.Pages.Select(page =>
                page.RunningStories.Single().Lines.First().Native!.Generated.Single(field => field.Kind=="pageField")).ToArray();
            Check(generated.Select(field => field.Fragment.Text).SequenceEqual(new[]{"9","10"}) &&
                generated.All(field => field.Fragment.Style is { Bold: true, Color: "1D4570", HorizontalScale: 90 } &&
                    field.Cause.Location.PartUri=="/word/header1.xml"),
                "Numéros logiques, style uniforme du cache et provenance de la source.");
            var semantic = snapshot.Semantic!;
            Check(semantic.Fields.Select(semantic.StoredText).Single()=="11" &&
                original.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),
                "Le cache et le DOCX sont conservés.");
            Check(snapshot.Composition.Pages.All(page=>Composer.PagePng(page).Length>0),"Rendu des deux feuilles.");
        }
        Refuse("cache-absent",Field(mergeFormat,""));
        Refuse("cache-mixte",Field(mergeFormat,CharacterRun("1",properties)+Run("1")));
        Refuse("commutateur-duplique",Field(mergeFormat+""" \* MERGEFORMAT """,stored));
        Refuse("cache-suivi",Field(mergeFormat,stored)+Run(" Suite"));

        string withFooter=Section(5000,5000,margins:1000)
            .Replace("<w:sectPr>","""<w:sectPr><w:footerReference w:type="default" r:id="foot"/>""",StringComparison.Ordinal)
            .Replace("w:bottom=\"1000\"","w:bottom=\"200\"",StringComparison.Ordinal)
            .Replace("w:footer=\"140\"","w:footer=\"700\"",StringComparison.Ordinal);
        using var reserved=Compose(Fixtures.Write(root,"pied-reserve.docx",Fixtures.Parts(
            Paragraph(Run("Une feuille"))+withFooter,new()
            {
                ["word/footer1.xml"]=Fixtures.Xml("ftr",Paragraph(Field(mergeFormat,stored),right))
            },Fixtures.Relationship("foot","footer","footer1.xml"))));
        Complete(reserved.Snapshot,1);
        var sheet=reserved.Snapshot.Composition.Pages.Single();
        Check(sheet.Geometry.Bottom>35&&sheet.RunningStories.Single().Lines.Single().Native!.Generated.Single().Fragment.Text=="1",
            "Réservation du pied à partir du cache, puis mesure de la valeur PAGE calculée.");

        void Refuse(string name,string field)
        {
            using var lease = Compose(Package(name,field));
            Check(lease.Snapshot.Coverage!=PageCoverage.Complete &&
                lease.Snapshot.Composition.Issues.Any(i=>i.BlocksRendering &&
                    i.Code is "PAGE_STORY_FIELD_DYNAMIC_PENDING" or "PAGE_STORY_FIELD_UNRESOLVED"),
                "Le cas hors profil doit rester diagnostiqué : "+name);
        }

        string Package(string name,string field) => Fixtures.Write(root,name+".docx",Fixtures.Parts(body,
            new(){["word/header1.xml"]=Fixtures.Xml("hdr",Paragraph(field,right)+P(""))},
            Fixtures.Relationship("head","header","header1.xml")));
        static string Field(string instruction,string result) => BeginDirty+Instruction(instruction)+Separator+result+EndField;
    }
}
