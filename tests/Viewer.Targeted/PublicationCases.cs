using System.IO.Compression;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class PublicationCases
{
    internal static void Run(string root)
    {
        const string content="""
            <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:bookmarkStart w:id="1" w:name="chapter"/>
            <w:r><w:t>Chapitre de contrôle</w:t></w:r><w:bookmarkEnd w:id="1"/></w:p>
            <w:p><w:r><w:rPr><w:b/><w:i/><w:color w:val="265478"/></w:rPr>
            <w:t xml:space="preserve">Café —  deux espaces. </w:t></w:r>
            <w:hyperlink r:id="uri"><w:r><w:t>Site externe</w:t></w:r></w:hyperlink>
            <w:r><w:t xml:space="preserve"> et </w:t></w:r>
            <w:hyperlink w:anchor="chapter"><w:r><w:t>retour au chapitre</w:t></w:r></w:hyperlink></w:p>
            <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Second chapitre</w:t></w:r></w:p>
            <w:p><w:hyperlink w:anchor="chapter"><w:r><w:t>Premier chapitre</w:t></w:r></w:hyperlink></w:p>
            <w:sectPr><w:pgSz w:w="10000" w:h="12000"/>
            <w:pgMar w:top="900" w:bottom="900" w:left="900" w:right="900"/></w:sectPr>
            """;
        string input=Fixtures.Write(root,"publication.docx",Fixtures.Parts(content,relationships:
            Fixtures.Relationship("uri","hyperlink","https://example.invalid/lecteur","External")));
        var inspected=DocumentInspector.InspectDocument(input);
        using var lease=PageComposer.Compose(inspected);
        var source=new PublicationSource(inspected,lease.Snapshot,"Publication contrôlée");
        foreach(var format in new[]{ExportFormat.Pdf,ExportFormat.Odt,ExportFormat.Epub3})
        {
            var plan=Publications.Prepare(source,new(format),default);
            Check(plan.Allowed,format+" refusé "+string.Join(" | ",plan.Issues.Select(i=>i.Code+" "+i.Message)));
            string output=Path.Combine(root,"publication."+Publications.Extension(format));
            Publications.Write(source,new(format),output);
            Check(File.Exists(output)&&new FileInfo(output).Length>120,format+" sans sortie.");
            if(format==ExportFormat.Pdf)continue;
            using var package=ZipFile.OpenRead(output);
            var part=package.GetEntry(format==ExportFormat.Odt?"content.xml":"EPUB/content.xhtml")!;
            using var stream=part.Open();
            var tree=XDocument.Load(stream);
            XNamespace text=StructuredPublication.Text;
            foreach(var space in tree.Descendants(text+"s").ToArray())
                space.ReplaceWith(new XText(new string(' ',(int?)space.Attribute(text+"c")??1)));
            Check(tree.Root!.Value.Contains("Chapitre de contrôle")&&tree.Root.Value.Contains("retour au chapitre")&&
                tree.Descendants().Attributes().Any(a=>a.Value=="https://example.invalid/lecteur"),
                format+" a perdu une structure ou un lien.");
            if(format==ExportFormat.Epub3)
            {
                using var chapter=package.GetEntry("EPUB/chapter-002.xhtml")!.Open();
                var second=XDocument.Load(chapter);
                Check(second.Root!.Value.Contains("Second chapitre")&&second.Descendants().Attributes("href")
                    .Any(a=>a.Value.StartsWith("content.xhtml#",StringComparison.Ordinal)),"Lien interchapitre EPUB perdu.");
            }
        }
        const string illustration="""
            <w:drawing xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <wp:inline><wp:extent cx="457200" cy="228600"/><wp:docPr id="9" name="Illustration" descr="Rectangle de contrôle"/>
                <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <pic:pic><pic:blipFill><a:blip r:embed="pic"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="457200" cy="228600"/></a:xfrm><a:prstGeom prst="rect"/></pic:spPr></pic:pic>
                </a:graphicData></a:graphic></wp:inline>
            </w:drawing>
            """;
        const string table="""
            <w:tbl><w:tblPr><w:tblW w:w="3200" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr>
            <w:tblGrid><w:gridCol w:w="1600"/><w:gridCol w:w="1600"/></w:tblGrid>
            <w:tr><w:tc><w:tcPr><w:tcW w:w="3200" w:type="dxa"/><w:gridSpan w:val="2"/></w:tcPr>
            <w:p><w:r><w:t>Cellule fusionnée</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:p><w:r><w:t>Un</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Deux</w:t></w:r></w:p></w:tc></w:tr>
            </w:tbl>
            """;
        string illustratedFile=Fixtures.Write(root,"illustration.docx",Fixtures.Parts("<w:p><w:r>"+illustration+"</w:r></w:p>"+table,
            relationships:Fixtures.Relationship("pic","image","media/illustration.png"),
            binary:new(){["word/media/illustration.png"]=Fixtures.Png}));
        var illustratedInput=DocumentInspector.InspectDocument(illustratedFile);
        using var illustratedLease=PageComposer.Compose(illustratedInput);
        var illustrated=new PublicationSource(illustratedInput,illustratedLease.Snapshot,"Illustration et tableau");
        foreach(var format in new[]{ExportFormat.Odt,ExportFormat.Epub3})
        {
            var request=new PublicationRequest(format,UseCoverImage:format==ExportFormat.Epub3);
            var plan=Publications.Prepare(illustrated,request,default);
            Check(plan.Allowed,format+" avec image/tableau refusé "+string.Join(" | ",plan.Issues.Select(i=>i.Code+" "+i.Source?.Path)));
            string output=Path.Combine(root,"illustration."+Publications.Extension(format));
            Publications.Write(illustrated,request,output);
            using var zip=ZipFile.OpenRead(output);
            Check(zip.Entries.Any(e=>e.FullName.EndsWith(".png",StringComparison.Ordinal)),format+" a perdu l’image.");
            using var contentPart=zip.GetEntry(format==ExportFormat.Odt?"content.xml":"EPUB/content.xhtml")!.Open();
            var body=XDocument.Load(contentPart);
            Check(body.Descendants().Attributes().Any(a=>a.Name.LocalName==(format==ExportFormat.Odt?"number-columns-spanned":"colspan")&&a.Value=="2"),
                format+" a perdu la fusion.");
        }

        var novaDoc=Fixtures.Write(root,"projet.docx",Fixtures.Parts("""
            <w:p><w:r><w:t>Première entrée.</w:t></w:r></w:p>
            <w:p><w:r><w:rPr><w:b/><w:i/><w:color w:val="265478"/></w:rPr>
            <w:t>Été 2026 — projet éditable.</w:t></w:r></w:p>
            """));
        var novaInput=DocumentInspector.InspectDocument(novaDoc);
        using var novaComposition=PageComposer.Compose(novaInput);
        var nova=new PublicationSource(novaInput,novaComposition.Snapshot,"Projet de contrôle");
        var novaPlan=Publications.Prepare(nova,new(ExportFormat.Nova),default);
        Check(novaPlan.Allowed,"NOVA refusé "+string.Join(" | ",novaPlan.Issues.Select(i=>i.Code+" "+i.Message)));
        Publications.Write(nova,new(ExportFormat.Nova),Path.Combine(root,"projet.nova"));
        string reservedFile=Fixtures.Write(root,"publication-reserve.docx",Fixtures.Parts("""
            <w:p><w:r><w:t>Numéro source</w:t></w:r>
              <w:fldSimple w:instr="SEQ Exemplaires"><w:r><w:t>32</w:t></w:r></w:fldSimple>
            </w:p>
            """));
        var reservedInput=DocumentInspector.InspectDocument(reservedFile);
        using var reservedLease=PageComposer.Compose(reservedInput);
        var reserved=new PublicationSource(reservedInput,reservedLease.Snapshot,"Champ signalé");
        var compatible=new PublicationRequest(ExportFormat.Epub3);
        var accepted=Publications.Prepare(reserved,compatible,default);
        var integral=Publications.Prepare(reserved,compatible with{Policy=PublicationPolicy.Complete},default);
        Check(accepted.Status==PublicationStatus.Reserved&&accepted.Reservations.Any(i=>i.Code=="EXPORT_FIELD_UNSUPPORTED")&&
            integral.Status==PublicationStatus.Blocked,"La politique intégrale doit refuser la désactivation d’un champ.");
        string reservedOutput=Path.Combine(root,"publication-reserve.epub");
        Publications.Write(reserved,compatible,reservedOutput);
        using(var zip=ZipFile.OpenRead(reservedOutput))
        using(var markup=zip.GetEntry("EPUB/content.xhtml")!.Open())
            Check(XDocument.Load(markup).Root!.Value.Contains("Numéro source32",StringComparison.Ordinal),
                "Résultat de champ perdu dans la conversion compatible.");

        string unsupportedFile=Fixtures.Write(root,"publication-dessin-incomplet.docx",Fixtures.Parts("""
            <w:p><w:r><w:t>Corps témoin.</w:t></w:r></w:p>
            <w:p><w:r><w:pict xmlns:v="urn:schemas-microsoft-com:vml">
              <v:shape type="#non-resolu" style="width:60pt;height:30pt">
                <v:textbox><w:txbxContent><w:p><w:r><w:t>Texte de la zone à conserver.</w:t></w:r></w:p></w:txbxContent></v:textbox>
              </v:shape>
            </w:pict></w:r></w:p>
            """));
        var unsupportedInput=DocumentInspector.InspectDocument(unsupportedFile);
        using var unsupportedLease=PageComposer.Compose(unsupportedInput);
        var unsupported=new PublicationSource(unsupportedInput,unsupportedLease.Snapshot,"Zone de texte hors profil");
        foreach(var format in new[]{ExportFormat.Odt,ExportFormat.Epub3,ExportFormat.Nova})
        {
            var request=new PublicationRequest(format,Policy:PublicationPolicy.Compatible);
            var plan=Publications.Prepare(unsupported,request,default);
            Check(plan.Status==PublicationStatus.Reserved&&plan.Reservations.Any(i=>i.Code=="EXPORT_TEXT_FRAME_TEXT"&&
                !i.BlocksExport&&i.Source?.PartUri=="/word/document.xml"),format+" refuse la reprise vérifiée du texte de la zone.");
            string output=Path.Combine(root,"publication-dessin-incomplet."+Publications.Extension(format));
            Check(!Publications.Prepare(unsupported,request with{Policy=PublicationPolicy.Complete},default).Allowed,
                format+" accepte la perte du cadre en conservation intégrale.");
            Publications.Write(unsupported,request,output);
            using var package=ZipFile.OpenRead(output);
            var member=format switch{ExportFormat.Odt=>package.GetEntry("content.xml")!,
                ExportFormat.Epub3=>package.GetEntry("EPUB/content.xhtml")!,
                _=>package.Entries.Single(entry=>entry.FullName.StartsWith("documents/",StringComparison.Ordinal))};
            using var payload=member.Open();
            string visible;
            if(format==ExportFormat.Odt)
            {
                var document=XDocument.Load(payload);
                foreach(var space in document.Descendants(StructuredPublication.Text+"s").ToArray())
                    space.ReplaceWith(new XText(new string(' ',(int?)space.Attribute(StructuredPublication.Text+"c")??1)));
                visible=document.Root!.Value;
            }
            else
            {
                using var text=new StreamReader(payload);
                visible=text.ReadToEnd();
            }
            Check(visible.Split("Texte de la zone à conserver.",StringSplitOptions.None).Length==2,
                format+" ne contient pas exactement une reprise du texte.");
        }

        string unrepresentable=Fixtures.Write(root,"dessin-sans-alternative.docx",Fixtures.Parts("""
            <w:p><w:r><w:pict xmlns:v="urn:schemas-microsoft-com:vml">
              <v:shape type="#non-resolu" style="width:60pt;height:30pt"/>
            </w:pict></w:r></w:p>
            """));
        var unrepresentableInput=DocumentInspector.InspectDocument(unrepresentable);
        using var unrepresentableLease=PageComposer.Compose(unrepresentableInput);
        var noFallback=new PublicationSource(unrepresentableInput,unrepresentableLease.Snapshot,"Dessin sans alternative");
        foreach(var format in new[]{ExportFormat.Odt,ExportFormat.Epub3,ExportFormat.Nova})
            Check(Publications.Prepare(noFallback,new(format),default).Issues.Any(issue=>
                issue is {Code:"EXPORT_DRAWING_UNSUPPORTED",BlocksExport:true}),format+" omet un dessin sans alternative.");

        MeasuredHeaderFrame(root);

        string sharedNote=Fixtures.Write(root,"publication-note-partagee.docx",Fixtures.Parts("""
            <w:p><w:r><w:t>Premier appel</w:t><w:footnoteReference w:id="2"/></w:r></w:p>
            <w:p><w:r><w:t>Second appel</w:t><w:footnoteReference w:id="2"/></w:r></w:p>
            """,new(){["word/footnotes.xml"]=Fixtures.Xml("footnotes","""
                <w:footnote w:id="2"><w:p><w:r><w:t>Une note unique</w:t></w:r></w:p></w:footnote>
                """)},Fixtures.Relationship("note","footnotes","footnotes.xml")));
        var sharedInput=DocumentInspector.InspectDocument(sharedNote);
        using var sharedLease=PageComposer.Compose(sharedInput);
        var shared=new PublicationSource(sharedInput,sharedLease.Snapshot,"Note partagée");
        foreach(var format in new[]{ExportFormat.Odt,ExportFormat.Epub3})
        {
            string output=Path.Combine(root,"publication-note-partagee."+Publications.Extension(format));
            Publications.Write(shared,new(format),output);
            using var package=ZipFile.OpenRead(output);
            using var payload=package.GetEntry(format==ExportFormat.Odt?"content.xml":"EPUB/content.xhtml")!.Open();
            var xml=XDocument.Load(payload);
            if(format==ExportFormat.Odt)
            {
                XNamespace ns=StructuredPublication.Text;
                var note=xml.Descendants(ns+"note").Single();
                var reference=xml.Descendants(ns+"note-ref").Single();
                Check((string?)reference.Attribute(ns+"ref-name")== (string?)note.Attribute(ns+"id"),"L’appel ODT partagé perd sa cible.");
            }
            else
            {
                var references=xml.Descendants(StructuredPublication.Html+"a").Where(a=>(string?)a.Attribute("role")=="doc-noteref").ToArray();
                Check(references.Length==2&&references[0].Attribute("href")?.Value==references[1].Attribute("href")?.Value,
                    "Les deux appels EPUB doivent mener au même corps de note.");
            }
        }

        string annotatedFile=Fixtures.Write(root,"publication-commentaire.docx",Fixtures.Parts("""
            <w:p><w:commentRangeStart w:id="2"/><w:r><w:t>Le texte annoté.</w:t></w:r><w:commentRangeEnd w:id="2"/>
              <w:r><w:commentReference w:id="2"/></w:r></w:p>
            """,new()
            {
                ["word/comments.xml"]=Fixtures.Xml("comments","""
                    <w:comment w:id="2" w:author="Rédacteur de contrôle" w:date="2026-11-20T15:00:00Z">
                      <w:p><w:r><w:t>Remarque éditable.</w:t></w:r></w:p>
                    </w:comment>
                    """)
            },Fixtures.Relationship("comments","comments","comments.xml")));
        var annotatedInput=DocumentInspector.InspectDocument(annotatedFile);
        using var annotatedLease=PageComposer.Compose(annotatedInput);
        var annotated=new PublicationSource(annotatedInput,annotatedLease.Snapshot,"Commentaire source");
        string annotatedOutput=Path.Combine(root,"publication-commentaire.odt");
        Publications.Write(annotated,new(ExportFormat.Odt),annotatedOutput);
        using(var zip=ZipFile.OpenRead(annotatedOutput))
        using(var markup=zip.GetEntry("content.xml")!.Open())
        {
            var xml=XDocument.Load(markup);
            var field=xml.Descendants(StructuredPublication.Office+"annotation").Single();
            var end=xml.Descendants(StructuredPublication.Office+"annotation-end").Single();
            Check(field.Value.Contains("Remarque",StringComparison.Ordinal)&&
                field.Attribute(StructuredPublication.Office+"name")?.Value==end.Attribute(StructuredPublication.Office+"name")?.Value,
                "Le commentaire ou sa plage ODF a été perdu.");
        }
        Publications.Write(source,new(ExportFormat.Nova),Path.Combine(root,"publication.nova"));
        Publications.Write(illustrated,new(ExportFormat.Nova),Path.Combine(root,"illustration.nova"));
        var chosenCover=PublicationCover.Read(Fixtures.Png,default);
        Publications.Write(source,new(ExportFormat.Epub3,Cover:chosenCover),Path.Combine(root,"couverture.epub"));
        using(var coverZip=ZipFile.OpenRead(Path.Combine(root,"couverture.epub")))
            Check(coverZip.GetEntry("EPUB/cover.xhtml") is not null&&coverZip.GetEntry("EPUB/images/cover.png") is not null,
                "Couverture externe absente de la lecture EPUB.");
        using(var native=ZipFile.OpenRead(Path.Combine(root,"publication.nova")))
        using(var manifest=System.Text.Json.JsonDocument.Parse(native.GetEntry("manifest.json")!.Open()))
        {
            var structure=manifest.RootElement;
            var references=structure.GetProperty("extensions").GetProperty("org.opusnova.references");
            var document=references.GetProperty("documents").EnumerateObject().Single().Value;
            Check(document.GetProperty("links").GetArrayLength()==3&&document.GetProperty("bookmarks").GetArrayLength()==1,
                "Renvois et signet natifs NOVA.");
        }
        const string paper="""<w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:bottom="1440" w:left="1440" w:right="1440"/>""";
        string manuscriptFile=Fixtures.Write(root,"manuscrit.docx",Fixtures.Parts(
            """
            <w:p><w:pPr><w:pStyle w:val="Partie"/></w:pPr><w:r><w:t>Partie source</w:t></w:r></w:p>
            <w:p><w:pPr><w:pStyle w:val="Chapitre"/></w:pPr><w:r><w:t>Chapitre source</w:t></w:r></w:p>
            <w:p><w:pPr><w:pStyle w:val="Scene"/></w:pPr><w:r><w:t>Scène source</w:t></w:r></w:p>
            <w:p><w:r><w:t>Le manuscrit garde sa structure.</w:t></w:r><w:r><w:footnoteReference w:id="2"/></w:r></w:p>
            """+"<w:p><w:pPr><w:sectPr>"+paper+"</w:sectPr></w:pPr><w:r><w:t>Fin de section</w:t></w:r></w:p>"+
            Fixtures.Paragraph("Nouvelle section éditable.")+"<w:sectPr>"+paper+"</w:sectPr>",
            new()
            {
                ["word/styles.xml"]=Fixtures.Xml("styles",
                    """
                    <w:style w:type="paragraph" w:styleId="Partie"><w:name w:val="Partie"/><w:pPr><w:outlineLvl w:val="0"/></w:pPr></w:style>
                    <w:style w:type="paragraph" w:styleId="Chapitre"><w:name w:val="Chapitre"/><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
                    <w:style w:type="paragraph" w:styleId="Scene"><w:name w:val="Scène"/><w:pPr><w:outlineLvl w:val="2"/></w:pPr></w:style>
                    """),
                ["word/footnotes.xml"]=Fixtures.Xml("footnotes",
                    """
                    <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
                    <w:footnote w:id="2"><w:p><w:r><w:footnoteRef/><w:t>Note source</w:t></w:r></w:p></w:footnote>
                    """),
                ["word/settings.xml"]=Fixtures.Xml("settings",
                    """<w:footnotePr><w:footnote w:id="-1"/></w:footnotePr>""")
            },Fixtures.Relationship("styles","styles","styles.xml")+Fixtures.Relationship("notes","footnotes","footnotes.xml")+
                Fixtures.Relationship("settings","settings","settings.xml")));
        var manuscriptInput=DocumentInspector.InspectDocument(manuscriptFile);
        using var manuscriptLease=PageComposer.Compose(manuscriptInput);
        var manuscript=new PublicationSource(manuscriptInput,manuscriptLease.Snapshot,"Manuscrit structuré");
        var manuscriptPlan=Publications.Prepare(manuscript,new(ExportFormat.Nova),default);
        Check(manuscriptPlan.Allowed,"Manuscrit NOVA refusé "+string.Join(" | ",manuscriptPlan.Issues.Where(i=>i.BlocksExport).Select(i=>i.Message)));
        Publications.Write(manuscript,new(ExportFormat.Nova),Path.Combine(root,"manuscrit.nova"));
        using(var native=ZipFile.OpenRead(Path.Combine(root,"manuscrit.nova")))
        using(var manifest=System.Text.Json.JsonDocument.Parse(native.GetEntry("manifest.json")!.Open()))
        {
            Check(manifest.RootElement.GetProperty("manuscript").GetProperty("nodes").GetArrayLength()==3&&
                native.GetEntry("notes.json") is not null,"Arborescence manuscrite ou note native NOVA absente.");
        }

        string final=Path.Combine(root,"existant.pdf");
        byte[] unchanged=[1,42,3];
        File.WriteAllBytes(final,unchanged);
        using var stop=new CancellationTokenSource();
        try
        {
            Publications.Write(source,new(ExportFormat.Pdf),final,true,new ImmediateProgress(p=>
            {
                if(p.Phase=="write")stop.Cancel();
            }),stop.Token);
            throw new InvalidOperationException("Annulation PDF ignorée.");
        }
        catch(OperationCanceledException){}
        Check(File.ReadAllBytes(final).SequenceEqual(unchanged)&&!Directory.EnumerateFiles(root,".lecteur-export-*").Any(),
            "Un export annulé a modifié la destination ou laissé une sortie partielle.");
        var quota=inspected with{Manifest=inspected.Manifest with{Budgets=inspected.Manifest.Budgets with{MaxOutputBytes=64}}};
        var limited=new PublicationSource(quota,lease.Snapshot,"Budget");
        try
        {
            Publications.Write(limited,new(ExportFormat.Pdf),Path.Combine(root,"incomplet.pdf"));
            throw new InvalidOperationException("Budget PDF ignoré.");
        }
        catch(OutputLimitException){}
        Check(!File.Exists(Path.Combine(root,"incomplet.pdf"))&&!Directory.EnumerateFiles(root,".lecteur-export-*").Any(),
            "Une sortie partielle a survécu à un dépassement de budget.");
        Check(!Publications.Prepare(source,new(ExportFormat.Pdf,2,1),default).Allowed,"Plage inverse proposée.");

        string crossPage=Fixtures.Write(root,"publication-lien-hors-plage.docx",Fixtures.Parts("""
            <w:p><w:hyperlink w:anchor="destination"><w:r><w:t>Destination vers la deuxième feuille</w:t></w:r></w:hyperlink></w:p>
            <w:p><w:pPr><w:pageBreakBefore/></w:pPr><w:bookmarkStart w:id="1" w:name="destination"/>
            <w:r><w:t>Destination</w:t></w:r><w:bookmarkEnd w:id="1"/></w:p>
            """));
        var crossPageInput=DocumentInspector.InspectDocument(crossPage);
        using var crossPageLease=PageComposer.Compose(crossPageInput);
        var pdfRange=new PublicationSource(crossPageInput,crossPageLease.Snapshot,"Lien hors plage");
        var range=new PublicationRequest(ExportFormat.Pdf,1,1);
        Check(Publications.Prepare(pdfRange,range,default) is {Allowed:true} available&&
            available.Issues.Any(issue=>issue is {Code:"EXPORT_PDF_LINK_UNREPRESENTABLE",BlocksExport:false})&&
            Publications.Prepare(pdfRange,range with{Policy=PublicationPolicy.Complete},default).Reservations
                .Any(issue=>issue is {Code:"EXPORT_PDF_LINK_UNREPRESENTABLE",BlocksExport:true}),
            "Un lien hors plage doit conserver son libellé et bloquer uniquement en politique intégrale.");

        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var reader=new ReaderWindow(new ThemePreference(Path.Combine(root,"settings.json")));
        reader.Show();
        try
        {
            AboutCases.Complete(reader.OpenDocumentAsync(input));
            reader.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="OpenPublication")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(()=>reader.ActivePublicationDialog is not null);
            var dialog=reader.ActivePublicationDialog!;
            var button=dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="PublicationWrite");
            Wait(()=>button.IsEnabled);
            string gui=Path.Combine(root,"interface.pdf");
            dialog.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="PublicationDestination").Text=gui;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(()=>dialog.GetVisualDescendants().OfType<Button>().Any(b=>b.Name=="PublicationOpenFile"&&b.IsVisible));
            Check(File.Exists(gui)&&File.Exists(Path.Combine(root,"publication.json")),"Le flux d’export intégré ou les préférences sont incomplets.");
            dialog.Close();
            Wait(()=>reader.ActivePublicationDialog is null);

            AboutCases.Complete(reader.OpenDocumentAsync(reservedFile));
            reader.GetVisualDescendants().OfType<Button>().Single(control=>control.Name=="OpenPublication")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(()=>reader.ActivePublicationDialog is not null);
            dialog=reader.ActivePublicationDialog!;
            dialog.GetVisualDescendants().OfType<RadioButton>().Single(control=>control.Name=="PublicationFormatEpub3").IsChecked=true;
            button=dialog.GetVisualDescendants().OfType<Button>().Single(control=>control.Name=="PublicationWrite");
            Wait(()=>button.IsEnabled);
            var advanced=dialog.GetVisualDescendants().OfType<Expander>().Single(control=>control.Name=="PublicationAdvanced");
            var summary=dialog.GetVisualDescendants().OfType<TextBlock>().Single(control=>control.Name=="PublicationDiagnostics");
            Check(!advanced.IsExpanded&&button.Content as string=="Exporter"&&summary.Text?.Contains("EXPORT_")==false,
                "La conversion compatible doit rester accessible et les codes internes masqués.");
            advanced.IsExpanded=true;
            Wait(()=>dialog.GetVisualDescendants().OfType<CheckBox>().Any(control=>control.Name=="PublicationPolicy"));
            var policy=dialog.GetVisualDescendants().OfType<CheckBox>().Single(control=>control.Name=="PublicationPolicy");
            Check(policy.IsChecked==false,"La conservation intégrale doit être désactivée à l’ouverture.");
            policy.IsChecked=true;
            Wait(()=>summary.Text?.StartsWith("Export impossible",StringComparison.Ordinal)==true);
            Check(!button.IsEnabled,"La politique demandée doit désactiver l’export avec réserves.");
            var technical=dialog.GetVisualDescendants().OfType<Expander>().Single(control=>control.Name=="PublicationTechnicalDetails");
            Check(!technical.IsExpanded,"Les diagnostics restent repliés dans les options avancées.");
            technical.IsExpanded=true;
            Wait(()=>dialog.GetVisualDescendants().OfType<Button>().Any(control=>control.Name=="PublicationCopyDiagnostics"));
            dialog.GetVisualDescendants().OfType<Button>().Single(control=>control.Name=="PublicationCopyDiagnostics")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string? clipboard=AboutCases.Complete(dialog.Clipboard!.TryGetTextAsync());
            Check(clipboard?.Contains("EXPORT_FIELD_UNSUPPORTED",StringComparison.Ordinal)==true&&
                clipboard.Contains("/word/document.xml",StringComparison.Ordinal),"La copie doit garder le code et l’emplacement source.");
            policy.IsChecked=false;
            Wait(()=>button.IsEnabled);
            dialog.Close();
        }
        finally{reader.ActivePublicationDialog?.Close();reader.Close();}

        Console.WriteLine("Publications : "+root);

        static void Check(bool valid,string detail)=>LineCases.Check(valid,detail);
        static void Wait(Func<bool> done)
        {
            var timeout=System.Diagnostics.Stopwatch.StartNew();
            while(!done()&&timeout.Elapsed.TotalSeconds<12)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(2);
            }
            Check(done(),"Délai pendant la recette Avalonia de l’export.");
        }
    }

    static void MeasuredHeaderFrame(string root)
    {
        const string label="Étiquette éditable de l’en-tête pair";
        const string header="""
            <w:p><w:r><w:pict xmlns:v="urn:schemas-microsoft-com:vml" xmlns:w10="urn:schemas-microsoft-com:office:word">
              <v:rect style="position:absolute;width:245pt;height:42pt;margin-left:36pt;margin-top:12pt;z-index:1;mso-position-horizontal:absolute;mso-position-horizontal-relative:page;mso-position-vertical:absolute;mso-position-vertical-relative:page"
                      filled="t" fillcolor="#123456" stroked="f">
                <v:textbox inset="2pt,2pt,2pt,2pt"><w:txbxContent>
                  <w:p><w:r><w:rPr><w:b/></w:rPr><w:t>{label}</w:t></w:r></w:p>
                </w:txbxContent></v:textbox>
                <w10:wrap type="none" anchorx="page" anchory="page"/>
              </v:rect>
            </w:pict></w:r></w:p>
            """;
        string input=Fixtures.Write(root,"publication-en-tete-pair.docx",Fixtures.Parts(Fixtures.Paragraph("Une seule feuille.")+"""
            <w:sectPr><w:headerReference w:type="even" r:id="head"/><w:headerReference w:type="default" r:id="head2"/><w:pgSz w:w="12240" w:h="15840"/>
            <w:pgMar w:top="1080" w:bottom="1080" w:left="1080" w:right="1080" w:header="240" w:footer="240"/></w:sectPr>
            """,new()
            {
                ["word/header1.xml"]=Fixtures.Xml("hdr",header.Replace("{label}",label,StringComparison.Ordinal)),
                ["word/header2.xml"]=Fixtures.Xml("hdr",header.Replace("{label}",label,StringComparison.Ordinal)
                    .Replace("margin-left:36pt","margin-left:45pt",StringComparison.Ordinal)),
                ["word/settings.xml"]=Fixtures.Xml("settings","<w:evenAndOddHeaders/>")
            },Fixtures.Relationship("head","header","header1.xml")+Fixtures.Relationship("head2","header","header2.xml")+
                Fixtures.Relationship("settings","settings","settings.xml")));
        var inspection=DocumentInspector.InspectDocument(input);
        using var lease=PageComposer.Compose(inspection);
        var source=new PublicationSource(inspection,lease.Snapshot,"Variante d’en-tête");
        LineCases.Check(source.Pages.Count==1&&lease.Snapshot.StoryRegions.Any(region=>region is
            {Kind:"header",Variant:"even",Status:"Measured",PhysicalPages.IsEmpty:true,FrameTemplate:not null}),
            "Le témoin doit mesurer le cadre pair sans le peindre.");
        foreach(var format in new[]{ExportFormat.Odt,ExportFormat.Epub3,ExportFormat.Nova})
        {
            var request=new PublicationRequest(format);
            var plan=Publications.Prepare(source,request,default);
            LineCases.Check(plan.Allowed,format+" refuse le cadre mesuré : "+string.Join(" | ",plan.Issues.Where(i=>i.BlocksExport).Select(i=>i.Message)));
            LineCases.Check(plan.Contents.Any(i=>i.Source.PartUri=="/word/header1.xml"&&i.Kind==SemanticKind.Drawing&&
                i.Representation==(format==ExportFormat.Odt?PublicationRepresentation.Native:PublicationRepresentation.Presentation)),
                format+" ne relie pas la représentation au dessin source.");
            string final=Path.Combine(root,"publication-en-tete-pair."+Publications.Extension(format));
            Publications.Write(source,request,final);
            using var package=ZipFile.OpenRead(final);
            if(format==ExportFormat.Nova)
            {
                using var manuscript=new StreamReader(package.Entries.Single(e=>e.FullName.StartsWith("documents/")&&e.Name.EndsWith(".txt")).Open());
                LineCases.Check(manuscript.ReadToEnd().Split('\n').Count(line=>line==label)==1,"Le texte du cadre NOVA doit rester éditable et unique.");
                continue;
            }
            using var entry=package.GetEntry(format==ExportFormat.Odt?"styles.xml":"EPUB/content.xhtml")!.Open();
            var markup=XDocument.Load(entry);
            if(format==ExportFormat.Odt)
            {
                var frame=markup.Descendants(StructuredPublication.Style+"header-left").Descendants(StructuredPublication.Draw+"frame").Single();
                XNamespace text=StructuredPublication.Text;
                foreach(var space in frame.Descendants(text+"s").ToArray())
                    space.ReplaceWith(new XText(new string(' ',(int?)space.Attribute(text+"c")??1)));
                LineCases.Check(frame.Element(StructuredPublication.Draw+"text-box")?.Value==label&&
                    (string?)frame.Attribute(StructuredPublication.SvgOdt+"x")=="36pt"&&
                    (string?)frame.Attribute(StructuredPublication.SvgOdt+"width")=="245pt",
                    "Le cadre pair ODT doit rester éditable avec sa position et sa largeur.");
            }
            else LineCases.Check(markup.Descendants(StructuredPublication.Html+"aside").Count(e=>
                (string?)e.Attribute("class")=="text-frame"&&e.Value==label)==1,"Le texte du cadre EPUB doit apparaître une fois dans le flux.");
        }
    }

    sealed class ImmediateProgress(Action<PublicationProgress> receive):IProgress<PublicationProgress>
    {
        public void Report(PublicationProgress value)=>receive(value);
    }
}
