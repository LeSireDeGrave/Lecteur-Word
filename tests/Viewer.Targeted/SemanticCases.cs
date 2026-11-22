using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class SemanticCases
{
    public static void Alternatives(string root)
    {
        string body=Fixtures.Paragraph("Avant")+
            "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:ext=\"urn:non-pris-en-charge\">"+
            "<mc:Choice Requires=\"ext\">"+Fixtures.Paragraph("BRANCHE-INACTIVE")+"</mc:Choice>"+
            "<mc:Fallback>"+Fixtures.Paragraph("BRANCHE-CHOISIE")+"</mc:Fallback></mc:AlternateContent>"+
            "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:c=\"" + Fixtures.W + "\">"+
            "<mc:Choice xmlns:c=\"urn:autre\" Requires=\"c\">"+Fixtures.Paragraph("PREFIXE-REDEFINI")+"</mc:Choice>"+
            "<mc:Choice Requires=\"c\">"+Fixtures.Paragraph("PREMIER-CHOIX")+"</mc:Choice>"+
            "<mc:Choice Requires=\"w\">"+Fixtures.Paragraph("SECOND-CHOIX")+"</mc:Choice>"+
            "<mc:Fallback>"+Fixtures.Paragraph("REPLI-INACTIF")+"</mc:Fallback></mc:AlternateContent>"+
            "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\">"+
            "<mc:Choice Requires=\"absent\">"+Fixtures.Paragraph("PREFIXE-ABSENT")+"</mc:Choice></mc:AlternateContent>"+
            Fixtures.Paragraph("Après");
        var path=Fixtures.Write(root,"semantic-alternatives.docx",Fixtures.Parts(body));
        var inspected=DocumentInspector.InspectDocument(path);
        Check(inspected.Manifest.InspectionComplete,"Inspection de la fixture refusée");
        var read=ReadableReader.Read(inspected);
        Check(read.Available&&read.PlainText.Contains("BRANCHE-CHOISIE",StringComparison.Ordinal)&&
            !read.PlainText.Contains("BRANCHE-INACTIVE",StringComparison.Ordinal),"Deux branches alternatives sont projetées");
        Check(inspected.Manifest.Nodes.Any(n=>n.Text=="BRANCHE-INACTIVE"),"La branche inactive a disparu de l’inventaire");
        Check(read.PlainText.Contains("PREMIER-CHOIX", StringComparison.Ordinal) &&
            !new[] { "PREFIXE-REDEFINI", "SECOND-CHOIX", "REPLI-INACTIF", "PREFIXE-ABSENT" }.Any(read.PlainText.Contains),
            "Portée des préfixes, ordre des choix ou absence de branche ignorés.");
        var result = SemanticReader.Build(inspected);
        var model = result.Document ?? throw new InvalidOperationException("Modèle indisponible.");
        Check(model.Alternatives.Length == 3 && model.Alternatives[2].SelectedBranchId is null &&
            result.Issues.Any(i => i.Code == "SEMANTIC_ALTERNATIVE_UNRESOLVED"), "Alternative irrésolue sans diagnostic.");
        var inactive = model.Elements.Single(e => model.RawText(e.Id) == "BRANCHE-INACTIVE");
        Check(inactive.Coverage == SemanticCoverage.InactiveAlternative &&
            model.Children(model.Alternatives[0].SourceId, includeInactive: true).Count() == 2 &&
            model.Children(model.Alternatives[0].SourceId).Count() == 1,
            "Branche inactive inaccessible ou projetée avec le repli.");
        Check(model.BodyBlocks.Select(model.Element).All(e => e.Kind == SemanticKind.Paragraph) &&
            model.BodyBlocks.Length == 4, "Ordre logique des blocs projetés incorrect.");
    }

    public static void Model(string root)
    {
        const string unicode = " e\u0301 👩\u200d💻 \uf0e8 ";
        string first = "<w:p><w:pPr><w:pStyle w:val=\"Derived\"/></w:pPr><w:bookmarkStart w:id=\"3\" w:name=\"target\"/>" +
            "<w:r><w:t xml:space=\"preserve\">" + unicode + "</w:t><w:footnoteReference w:id=\"4\"/></w:r><w:bookmarkEnd w:id=\"3\"/>" +
            "<w:hyperlink w:anchor=\"target\"><w:r><w:t>Lien interne</w:t></w:r></w:hyperlink>" +
            "<w:hyperlink r:id=\"ext\"><w:r><w:t>Lien externe</w:t></w:r></w:hyperlink></w:p>";
        string command = "<w:r><w:fldChar w:fldCharType=\"begin\" w:dirty=\"true\"/></w:r>" +
            "<w:r><w:instrText xml:space=\"preserve\"> PAGE</w:instrText></w:r>" +
            "<w:r><w:instrText xml:space=\"preserve\">REF target </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>";
        string compactField = "<w:fldSimple w:instr=\"PAGE\" w:fldLock=\"true\"><w:r><w:t>17</w:t></w:r></w:fldSimple>";
        string drawing = "<w:r><w:drawing xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><wp:anchor><wp:positionH><wp:posOffset>0</wp:posOffset></wp:positionH>" +
            "<a:blip r:embed=\"img\"/><w:txbxContent>" + Fixtures.Paragraph("Texte de boîte") +
            "</w:txbxContent></wp:anchor></w:drawing></w:r>";
        string cell(string merge, string paragraph) =>
            "<w:tc><w:tcPr><w:gridSpan w:val=\"2\"/>" + merge + "</w:tcPr>" + paragraph + "</w:tc>";
        string table = "<w:tbl><w:tblGrid><w:gridCol w:w=\"1000\"/><w:gridCol w:w=\"1000\"/></w:tblGrid>" +
            "<w:tr>" + cell("<w:vMerge w:val=\"restart\"/>", "<w:p>" + drawing + "</w:p>") + "</w:tr>" +
            "<w:tr>" + cell("<w:vMerge/>", Fixtures.Paragraph("Cellule de continuation")) + "</w:tr></w:tbl>";
        string header = "<w:headerReference w:type=\"default\" r:id=\"hdr\"/>";
        string body = first + "<w:p>" + command + "</w:p><w:p>" + compactField +
            "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" + table +
            "<w:p><w:pPr><w:sectPr>" + header + "</w:sectPr></w:pPr><w:del w:id=\"1\"><w:r><w:delText>Effacé</w:delText></w:r></w:del>" +
            "<w:r><w:commentReference w:id=\"7\"/></w:r></w:p>" + Fixtures.Paragraph("Dernier") + "<w:sectPr/>";
        var parts = Fixtures.Parts(body, new()
        {
            ["word/styles.xml"] = Fixtures.Xml("styles",
                "<w:style w:type=\"paragraph\" w:styleId=\"Base\"><w:pPr><w:numPr><w:ilvl w:val=\"8\"/><w:numId w:val=\"9\"/></w:numPr></w:pPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Derived\"><w:basedOn w:val=\"Base\"/><w:rPr><w:b/></w:rPr></w:style>"),
            ["word/numbering.xml"] = Fixtures.Xml("numbering",
                "<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"2\"><w:pStyle w:val=\"Derived\"/><w:lvlText w:val=\"%3\"/></w:lvl></w:abstractNum>" +
                "<w:num w:numId=\"9\"><w:abstractNumId w:val=\"1\"/><w:lvlOverride w:ilvl=\"2\"><w:startOverride w:val=\"3\"/></w:lvlOverride></w:num>"),
            ["word/footnotes.xml"] = Fixtures.Xml("footnotes", "<w:footnote w:id=\"4\">" + Fixtures.Paragraph("Texte de note") + "</w:footnote>"),
            ["word/header1.xml"] = Fixtures.Xml("hdr", "<w:p><w:fldSimple w:instr=\"NUMPAGES\"><w:r><w:t>41</w:t></w:r></w:fldSimple></w:p>" +
                "<w:p><w:hyperlink r:id=\"ext\"><w:r><w:t>Lien en-tête</w:t></w:r></w:hyperlink></w:p>"),
            ["word/comments.xml"] = Fixtures.Xml("comments", "<w:comment w:id=\"7\">" + Fixtures.Paragraph("Commentaire") + "</w:comment>")
        }, Fixtures.Relationship("typ", "styles", "styles.xml") + Fixtures.Relationship("num", "numbering", "numbering.xml") +
            Fixtures.Relationship("notes", "footnotes", "footnotes.xml") + Fixtures.Relationship("hdr", "header", "header1.xml") +
            Fixtures.Relationship("notes-c", "comments", "comments.xml") + Fixtures.Relationship("img", "image", "media/t.png") +
            Fixtures.Relationship("ext", "hyperlink", "https://example.invalid/never", "External"), new() { ["word/media/t.png"] = Fixtures.Png });
        parts["word/_rels/header1.xml.rels"] = System.Text.Encoding.UTF8.GetBytes(Fixtures.Relations(
            Fixtures.Relationship("ext", "hyperlink", "https://example.invalid/header", "External")));
        var path = Fixtures.Write(root, "semantic-model.docx", parts);
        var inspected = DocumentInspector.InspectDocument(path);
        Check(inspected.Manifest.InspectionComplete, "Inspection de la fixture refusée.");
        var before = ManifestJson.Serialize(inspected.Manifest);
        var model = SemanticReader.Build(inspected).Document ?? throw new InvalidOperationException("Modèle refusé.");
        SemanticCorpusCases.VerifyInventory(inspected.Manifest, model);

        Check(model.Sections.Length == 2 && model.Sections[0].StartBlock == 0 && model.Sections[0].EndBlockExclusive == 5 &&
            model.Sections[1].RunningStories.Single().DeclaringSection == 1, "Section ou héritage de région incorrect.");
        Check(model.Sections[1].RunningStories.Single().Reference.Relation?.Id == "hdr", "Référence héritée sans source.");
        var declared = model.Numbering.Single();
        Check(declared is { Level: 2, InstanceSourceId: not null, LevelSourceId: not null, OverrideId: not null },
            "Origine et niveau hérité de numérotation incorrects.");
        var style = model.StyleUses.Single(u => model.Name(u.Id).EndsWith("}pStyle", StringComparison.Ordinal) &&
            u.BaseToDerivedIds.Length == 2 && model.Name(model.ParentId(u.Id)!).EndsWith("}pPr", StringComparison.Ordinal));
        Check(style.Resolved && style.BaseToDerivedIds.SequenceEqual(model.Styles.Select(s => s.Id)), "Chaîne de styles inexacte.");
        var declarations = model.PresentationDeclarations(model.BodyBlocks[0]).ToArray();
        Check(declarations.Length == 2 && model.ParentId(declarations[0]) == style.BaseToDerivedIds[0] &&
            model.ParentId(declarations[1]) == model.BodyBlocks[0], "Provenance de déclaration héritée perdue.");
        var cells = model.Tables.Single().Rows.SelectMany(r => r.Cells).ToArray();
        Check(cells.Length == 2 && cells.All(c => c.Column == 0 && c.ColumnSpan == 2 && c.VerticalOriginId == cells[0].Id),
            "Fusion verticale et span non conservés.");
        Check(model.BlocksOf(cells[1].Id).Single().Kind == SemanticKind.Paragraph, "Contenu de cellule perdu.");
        Check(model.References.Any(r => r is { Kind: "image", Media: not null, Status: ReferenceStatus.Resolved }) &&
            model.References.Any(r => r is { Kind: "hyperlink", Status: ReferenceStatus.External }) &&
            model.References.Any(r => r is { Kind: "footnote", TargetId: not null, Status: ReferenceStatus.Resolved }) &&
            model.References.Any(r => r is { Kind: "comment", TargetId: not null, Status: ReferenceStatus.Resolved }),
            "Associations d’image, de lien, de note ou de commentaire perdues.");
        var links = model.References.Where(r => r is { Kind: "hyperlink", DeclaredId: "ext" }).ToArray();
        Check(links.Length == 2 &&
            links.Single(r => model.Source(r.SourceId).Location.PartUri == "/word/header1.xml").Relation?.Target ==
                "https://example.invalid/header" &&
            links.Single(r => model.Source(r.SourceId).Location.PartUri == "/word/document.xml").Relation?.Target ==
                "https://example.invalid/never", "Deux relations de parties différentes partagent le même identifiant local.");
        Check(model.Stories.Any(s => s.Kind == "txbxContent") &&
            model.Elements.Any(e => model.RawText(e.Id) == "0" && e.Coverage == SemanticCoverage.Technical) &&
            model.Elements.Any(e => model.RawText(e.Id) == "Effacé" && e.Kind == SemanticKind.DeletedText),
            "Confusion entre géométrie, révision et contenu.");
        var cross = model.Fields.Single(f => f.Command == "PAGEREF");
        Check(cross.StructureVerified && cross.BookmarkId is not null && cross.Dirty &&
            cross.InstructionIds.Length == 2 && model.StoredText(cross) == "17", "Résultat ou instruction du champ complexe altéré.");
        var simple = model.Fields.Single(f => f.Command == "PAGE");
        Check(simple.Locked && simple.ParentFieldId == cross.Id && model.StoredText(simple) == "17" &&
            model.StoredText(model.Fields.Single(f => f.Command == "NUMPAGES")) == "41", "Champs imbriqués ou séparés confondus.");
        string text = model.Elements.Single(n => model.RawText(n.Id) == unicode).Id;
        int supplementary = unicode.IndexOf("👩", StringComparison.Ordinal);
        Check(model.Source(text, new(supplementary, supplementary + 2)).Span is not null, "Plage UTF-16 valide refusée.");
        try { model.Source(text, new(supplementary + 1, supplementary + 2)); throw new InvalidOperationException("Demi-scalaire accepté."); }
        catch (ArgumentOutOfRangeException) {}
        var again = SemanticReader.Build(DocumentInspector.InspectDocument(path)).Document!;
        Check(model.Elements.SequenceEqual(again.Elements), "Identités instables.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { SemanticReader.Build(inspected, cancelled.Token); throw new InvalidOperationException("Annulation ignorée."); }
        catch (OperationCanceledException) {}
        Check(SemanticReader.Build(new(new(), new Dictionary<string, byte[]>())).Document is null, "Inspection refusée acceptée.");
        Check(before.SequenceEqual(ManifestJson.Serialize(inspected.Manifest)), "Manifeste v1 modifié.");
    }

    static void Check(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }
}
