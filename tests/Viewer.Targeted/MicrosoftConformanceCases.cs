using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class PageCases
{
    internal static void MicrosoftStrict(string root)
    {
        const string strictWord="http://purl.oclc.org/ooxml/wordprocessingml/main";
        const string strictRel="http://purl.oclc.org/ooxml/officeDocument/relationships";
        const string headerReference="""<w:headerReference w:type="default" r:id="titre"/>""";
        var body=Paragraph(Run("I")+"""<w:r><w:tab/></w:r>"""+Run("Suite"),"""
            <w:tabs><w:tab w:val="start" w:pos="-100"/></w:tabs><w:ind w:start="-220"/>
            """)+Paragraph(SimpleField("PAGE"))+Section(margins:720).Replace("<w:sectPr>",
                "<w:sectPr>"+headerReference,StringComparison.Ordinal);
        var parts=Fixtures.Parts(body,new(){["word/header1.xml"]=Fixtures.Xml("hdr",P("En-tête Strict"))},
            Fixtures.Relationship("titre","header","header1.xml"));
        foreach(var key in new[]{"word/document.xml","word/header1.xml","word/_rels/document.xml.rels","_rels/.rels"})
            parts[key]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(parts[key])
                .Replace(Fixtures.W,strictWord,StringComparison.Ordinal)
                .Replace(Fixtures.R,strictRel,StringComparison.Ordinal));
        string source=Fixtures.Write(root,"strict-wordprocessing.docx",parts);
        using(var sdk=WordprocessingDocument.Open(source,false))
        {
            var errors=new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(sdk).ToArray();
            Check(errors.Length==0,"Validation SDK du témoin Strict. "+string.Join(" / ",errors.Select(e=>e.Description)));
        }
        var original=File.ReadAllBytes(source);
        var input=DocumentInspector.InspectDocument(source);
        Check(input.Manifest is {Format:"docx",Conformance:"strict",InspectionComplete:true}&&
            input.Manifest.Relationships.Single(r=>r.SourcePart=="/").SourceType==strictRel+"/officeDocument"&&
            input.Manifest.Nodes.Any(n=>n.SourceName=="{"+strictWord+"}pgSz"&&
                n.SourceAttributes?.GetValueOrDefault("{"+strictWord+"}w")=="3200")&&
            input.Manifest.Nodes.Any(n=>n.SourceName=="{"+strictWord+"}headerReference"&&
                n.SourceAttributes?.GetValueOrDefault("{"+strictRel+"}id")=="titre"),
            "La relation, les noms et attributs Strict gardent leur forme et leur source.");
        using(var lease=PageComposer.Compose(input))
        {
            var snapshot=lease.Snapshot;
            Complete(snapshot,1);
            var page=snapshot.Pages.Single().Geometry;
            Check(Close(page.Geometry.Width,160)&&Close(page.Geometry.Height,180)&&
                page.Lines[0].Native!.Tabs.Single().Stop is {Position:-63500,Kind:LineTabKind.Left}&&
                snapshot.FieldPasses[^1].Fields.Single().Text=="1"&&Composer.PagePng(page).Length>0&&
                page.RunningStories.Single().Lines.Single().Text=="En-tête Strict",
                "La source Strict utilise la même mesure de taquet, le calcul de champs et la même feuille.");
        }
        Check(original.AsSpan().SequenceEqual(File.ReadAllBytes(source))&&Enum.GetValues<ExportFormat>()
            .All(format=>!DocumentExports.Preflight(input,format).Allowed),
            "La projection Strict conserve l’original et les contrats d’export.");
        parts["word/document.xml"]=Encoding.UTF8.GetBytes(Fixtures.Document(body));
        string mixed=Fixtures.Write(root,"strict-racine-incoherente.docx",parts);
        var refused=DocumentInspector.InspectDocument(mixed);
        Check(!refused.Manifest.InspectionComplete&&refused.Manifest.Diagnostics.Any(d=>d.Code=="DOCX_MAIN_NAMESPACE_MISMATCH"),
            "Le namespace de la racine doit concorder avec le profil de relation OPC.");
    }

    internal static void MicrosoftCompatibility(string root)
    {
        const string declarations="""
            xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" xmlns:e="urn:lecteur:test-extension"
            xmlns:p="urn:lecteur:test-extension" mc:Ignorable="e" mc:ProcessContent="e:conteneur"
            """;
        string wrapper="<p:conteneur xmlns:e=\"urn:lecteur:autre\">"+P("Dans le conteneur")+
            "</p:conteneur>";
        string choice="<mc:Choice Requires=\"e\">"+P("Inactive")+"</mc:Choice>";
        string fallback="<mc:Fallback>"+P("Branche de repli")+"</mc:Fallback>";
        string alternative="<mc:AlternateContent>"+choice+fallback+"</mc:AlternateContent>";
        using(var admitted=Open("mc-portee",wrapper+alternative))
        {
            var snapshot=admitted.Snapshot;
            Complete(snapshot,1);
            Check(snapshot.Pages.Single().Geometry.Lines.Select(l=>l.Text)
                .SequenceEqual(new[]{"Début","Dans le conteneur","Branche de repli"})&&
                snapshot.Semantic!.Elements.Any(e=>e.Coverage==SemanticCoverage.InactiveAlternative&&
                    snapshot.Semantic.RawText(e.Id)=="Inactive"),
                "Le namespace hérité gouverne ProcessContent et la branche inactive reste inventoriée.");
        }
        using(var invalid=Open("mc-ordre-invalide","<mc:AlternateContent>"+fallback+choice+"</mc:AlternateContent>"))
            Check(invalid.Snapshot.Coverage!=PageCoverage.Complete&&invalid.Snapshot.Composition.Issues
                .Any(issue=>issue.Code=="SEMANTIC_ALTERNATIVE_UNRESOLVED"&&issue.BlocksRendering),
                "Un repli qui précède le choix reste indéterminé.");
        using(var required=Open("mc-namespace-requis",wrapper.Replace("<p:conteneur ",
            "<p:conteneur mc:MustUnderstand=\"p\" ",StringComparison.Ordinal)))
            Check(required.Snapshot.Coverage!=PageCoverage.Complete&&required.Snapshot.Composition.Issues
                .Any(issue=>issue.Code=="SEMANTIC_MUST_UNDERSTAND"&&issue.BlocksRendering),
                "Ignorable et ProcessContent ne satisfont pas MustUnderstand.");
        using(var inline=Open("mc-repli-dans-run",Paragraph("""
            <w:r><mc:AlternateContent><mc:Choice Requires="e"><w:t>Inactif</w:t></mc:Choice>
            <mc:Fallback><w:t>Repli inline</w:t></mc:Fallback></mc:AlternateContent></w:r>
            """)))
        {
            Complete(inline.Snapshot,1);
            Check(Line(inline.Snapshot,"Repli inline").Native is not null&&
                inline.Snapshot.Semantic!.Elements.Any(x=>x.Coverage==SemanticCoverage.InactiveAlternative&&
                    inline.Snapshot.Semantic.RawText(x.Id)=="Inactif"),"Projection MCE des caractères dans la page.");
        }

        PageCompositionLease Open(string name,string content)
        {
            var parts=Fixtures.Parts(P("Début")+content+Section());
            parts["word/document.xml"]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(parts["word/document.xml"])
                .Replace("<w:document ","<w:document "+declarations+" ",StringComparison.Ordinal));
            return Compose(Fixtures.Write(root,name+".docx",parts));
        }
    }
}
