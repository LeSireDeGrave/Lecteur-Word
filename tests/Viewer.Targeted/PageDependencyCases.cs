using System.Text;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class PageCases
{
    internal static void CustomXml(string root)
    {
        const string bibliography="http://schemas.openxmlformats.org/officeDocument/2006/bibliography";
        const string store="http://schemas.openxmlformats.org/officeDocument/2006/customXml";
        const string storeId="{6D289BFC-1F16-4701-AC13-F564991BAF58}";
        const string entryGuid="{66DFC9A5-A7F1-4983-8E01-949F3190EE42}";
        string bibliographyXml=$"""
            <b:Sources xmlns:b="{bibliography}" StyleName="APA" SelectedStyle="/APA.XSL">
              <b:Source><b:Tag>ESSAI</b:Tag><b:Guid>{entryGuid}</b:Guid><b:SourceType>Book</b:SourceType>
                <b:Title>Source hors du texte</b:Title></b:Source>
            </b:Sources>
            """;
        string properties=$"""
            <ds:datastoreItem xmlns:ds="{store}" ds:itemID="{storeId}">
              <ds:schemaRefs><ds:schemaRef ds:uri="{bibliography}"/></ds:schemaRefs>
            </ds:datastoreItem>
            """;
        string body=P("Témoin sans citation.")+Section();
        var inert=Input("bibliographie-inerte",body,bibliographyXml);
        var frozen=ManifestJson.Serialize(inert.Manifest);
        using var admitted=PageComposer.Compose(inert);
        using var plain=Compose(Write(root,"corps-seul",body));
        var snapshot=admitted.Snapshot;
        Complete(snapshot,1);
        var semantic=snapshot.Semantic!;
        var decision=semantic.CustomXml.Single();
        Check(decision is {Impact:DependencyImpact.NoLayoutEffect,Relation.Id:"bib",StoreId:{} id,Entries.Length:1}&&
            id==Guid.Parse(storeId)&&decision.PartRelations.Single().Id=="props"&&
            semantic.Source(decision.RootId!).Location.PartUri=="/customXml/references.xml"&&
            semantic.Source(decision.PropertiesId!).Location.PartUri=="/customXml/store.xml"&&
            semantic.Attribute(decision.RootId!,"SelectedStyle","")=="/APA.XSL",
            "Identité, relation et propriétés du magasin bibliographique.");
        var entry=decision.Entries.Single();
        var tag=semantic.Children(entry).Single(n=>semantic.Name(n.Id)=="{"+bibliography+"}Tag");
        var guid=semantic.Children(entry).Single(n=>semantic.Name(n.Id)=="{"+bibliography+"}Guid");
        Check(semantic.RawText(semantic.Children(tag.Id).Single().Id)=="ESSAI"&&
            semantic.RawText(semantic.Children(guid.Id).Single().Id)==entryGuid&&
            ManifestJson.Serialize(inert.Manifest).AsSpan().SequenceEqual(frozen),"Perte du contenu ou des identifiants bibliographiques.");
        Check(snapshot.Typography!.Tokens.All(t=>t.Source.Location.PartUri!="/customXml/references.xml")&&
            snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_CUSTOM_XML_METADATA_ONLY"&&!i.BlocksRendering&&i.BlocksExport),
            "La bibliographie doit rester un élément éditorial du paquet.");
        var line=Line(snapshot,"Témoin sans citation.");
        var reference=Line(plain.Snapshot,line.Text);
        Check(line.X==reference.X&&line.Baseline==reference.Baseline&&line.Native?.Advance==reference.Native?.Advance&&
            Composer.PagePng(snapshot.Composition.Pages[0],2).AsSpan().SequenceEqual(Composer.PagePng(plain.Snapshot.Composition.Pages[0],2)),
            "La bibliographie inerte change la géométrie ou le raster.");
        foreach(var format in Enum.GetValues<ExportFormat>())
        {
            var preflight=DocumentExports.Preflight(inert,format);
            Check(!preflight.Allowed&&preflight.Issues.Any(i=>i.Code=="AUXILIARY_UNMAPPED"),"Export inadapté de la bibliographie.");
            string output=Path.Combine(root,"refuse-"+format);
            try{DocumentExports.Export(inert,format,output);throw new InvalidOperationException("Export bibliographique publié.");}
            catch(InvalidDataException e) when(e.Message=="EXPORT_PREFLIGHT_BLOCKED"){}
            Check(!Path.Exists(output)&&!Directory.EnumerateDirectories(root,".lecteur-export-*").Any(),"Écriture malgré refus.");
        }

        string headerSection=Section(margins:800).Replace("<w:sectPr>",
            """<w:sectPr><w:headerReference w:type="default" r:id="head"/>""",StringComparison.Ordinal);
        string citation="""<w:p><w:fldSimple w:instr=" CITATION ESSAI "><w:r><w:t>Résultat enregistré.</w:t></w:r></w:fldSimple></w:p>""";
        var header=Input("bibliographie-en-tete",P("Corps")+headerSection,bibliographyXml,
            stories:new(){["word/header1.xml"]=Fixtures.Xml("hdr",citation)},extra:Fixtures.Relationship("head","header","header1.xml"));
        CheckDecision(header,DependencyImpact.AffectsLayout,"/word/header1.xml");
        var headerModel=TypographyCases.Model(header);
        Check(headerModel.Fields.Single() is {Command:"CITATION"} field&&headerModel.StoredText(field)=="Résultat enregistré.",
            "Résultat stocké du champ conservé.");

        const string split="""
            <w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> BIBLIO</w:instrText></w:r>
            <w:r><w:instrText>GRAPHY </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r>
            <w:r><w:t>Bibliographie enregistrée</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>
            """;
        var notes=Input("bibliographie-note",Paragraph(Run("Corps")+"<w:r><w:footnoteReference w:id=\"3\"/></w:r>")+Section(),bibliographyXml,
            stories:new(){["word/footnotes.xml"]=Fixtures.Xml("footnotes","<w:footnote w:id=\"3\">"+split+"</w:footnote>")},
            extra:Fixtures.Relationship("fn","footnotes","footnotes.xml"));
        CheckDecision(notes,DependencyImpact.AffectsLayout,"/word/footnotes.xml");

        string mapping=$"""
            <w:sdt><w:sdtPr><w:dataBinding w:storeItemID="{storeId}" w:xpath="/b:Sources"
                w:prefixMappings="xmlns:b='{bibliography}'"/></w:sdtPr><w:sdtContent>{P("Valeur stockée.")}</w:sdtContent></w:sdt>
            """;
        var comments=Input("liaison-commentaire",body,bibliographyXml,stories:new()
            {["word/comments.xml"]=Fixtures.Xml("comments","<w:comment w:id=\"0\" w:author=\"Auteur\">"+mapping+"</w:comment>")},
            extra:Fixtures.Relationship("comment","comments","comments.xml"));
        CheckDecision(comments,DependencyImpact.AffectsLayout,"/word/comments.xml");

        CheckDecision(Input("champ-indetermine",P("Corps")+headerSection,bibliographyXml,
            stories:new(){["word/header1.xml"]=Fixtures.Xml("hdr",citation.Replace("CITATION","DOCVARIABLE",StringComparison.Ordinal))},
            extra:Fixtures.Relationship("head","header","header1.xml")),DependencyImpact.Undetermined,"/word/header1.xml");
        CheckDecision(Input("proprietes-absentes",body,bibliographyXml,withStore:false),DependencyImpact.Undetermined);
        CheckDecision(Input("store-ambigu",body,bibliographyXml,duplicateStore:true),DependencyImpact.Undetermined);
        CheckDecision(Input("identifiant-ambigu",body,bibliographyXml.Replace("</b:Sources>",
            "<b:Source><b:Tag>essai</b:Tag></b:Source></b:Sources>",StringComparison.Ordinal)),DependencyImpact.Undetermined);
        CheckDecision(Input("relation-consommee",body.Replace("<w:p>","<w:p r:id=\"bib\">",StringComparison.Ordinal),bibliographyXml),
            DependencyImpact.Undetermined,"/word/document.xml");
        CheckDecision(Input("xml-generique",body,"<x:business xmlns:x=\"urn:synthetic\">Contenu</x:business>"),DependencyImpact.Undetermined);
        Unavailable(Input("cible-absente",body,null,withStore:false));
        Unavailable(Input("xml-malforme",body,"<Sources>"));
        Unavailable(Input("xml-dtd",body,"<!DOCTYPE Sources [<!ELEMENT Sources ANY>]><Sources/>"));
        Unavailable(Input("xml-externe",body,bibliographyXml,external:true));
        return;

        static void Unavailable(InspectionResult input)
        {
            if(input.Manifest.InspectionComplete)CheckDecision(input,DependencyImpact.Undetermined);
            else
            {
                using var lease=PageComposer.Compose(input);
                Check(lease.Snapshot.Coverage==PageCoverage.Fallback&&input.Manifest.Diagnostics.Any(d=>
                    d.Code is "UNSAFE_OR_MALFORMED_XML" or "MISSING_RELATIONSHIP_TARGET" or "SDK_PACKAGE_REFUSED"),
                    "Une partie dangereuse doit être refusée avant calcul.");
            }
        }

        static void CheckDecision(InspectionResult input,DependencyImpact impact,string? source=null)
        {
            using var lease=PageComposer.Compose(input);
            var snapshot=lease.Snapshot;
            var model=snapshot.Semantic;
            Check(model is not null,"Inspection du témoin refusée : "+string.Join(", ",input.Manifest.Diagnostics.Select(d=>d.Code).Distinct()));
            var dependency=model!.CustomXml.Single();
            Check(dependency.Impact==impact&&snapshot.Coverage!=PageCoverage.Complete&&
                snapshot.Composition.Issues.Any(i=>i.BlocksRendering&&i.Code==(impact==DependencyImpact.AffectsLayout?
                    "PAGE_CUSTOM_XML_CONSUMED":"PAGE_CUSTOM_XML_UNDETERMINED"))&&
                (source is null||dependency.ConsumerId is {} id&&model.Source(id).Location.PartUri==source),
                "Effet de la dépendance ou provenance du consommateur incorrects.");
        }

        InspectionResult Input(string name,string content,string? data,bool withStore=true,bool duplicateStore=false,bool external=false,
            Dictionary<string,string>? stories=null,string extra="")
        {
            string target=external?"https://example.invalid/bibliographie.xml":"../customXml/references.xml";
            var parts=Fixtures.Parts(content,stories,Fixtures.Relationship("bib","customXml",target,external?"External":"Internal")+extra);
            if(data is not null)parts["customXml/references.xml"]=Encoding.UTF8.GetBytes(data);
            if(withStore)
            {
                parts["customXml/store.xml"]=Encoding.UTF8.GetBytes(properties);
                parts["customXml/_rels/references.xml.rels"]=Encoding.UTF8.GetBytes(
                    Fixtures.Relations(Fixtures.Relationship("props","customXmlProps","store.xml")));
                string overrides="""<Override PartName="/customXml/store.xml" ContentType="application/vnd.openxmlformats-officedocument.customXmlProperties+xml"/>""";
                if(duplicateStore)
                {
                    parts["customXml/store-other.xml"]=Encoding.UTF8.GetBytes(properties);
                    overrides+=overrides.Replace("/customXml/store.xml","/customXml/store-other.xml",StringComparison.Ordinal);
                }
                var types=Encoding.UTF8.GetString(parts["[Content_Types].xml"]);
                parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(types.Replace("</Types>",overrides+"</Types>",StringComparison.Ordinal));
            }
            return DocumentInspector.InspectDocument(Fixtures.Write(root,name+".docx",parts));
        }
    }
}
