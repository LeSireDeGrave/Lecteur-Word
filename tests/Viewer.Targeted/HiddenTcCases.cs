using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class PageCases
{
    const string HiddenSecret="SECRET_à_masquer_𝄞";

    static string CharacterRun(string value,string properties)=>
        $"""<w:r><w:rPr>{properties}</w:rPr><w:t xml:space="preserve">{value}</w:t></w:r>""";

    internal static string HiddenTextDocument(string root)
    {
        const string styles="""
            <w:style w:type="character" w:styleId="Hide"><w:rPr><w:vanish/></w:rPr></w:style>
            <w:style w:type="character" w:styleId="KeepHidden"><w:basedOn w:val="Hide"/><w:rPr><w:vanish w:val="off"/></w:rPr></w:style>
            <w:style w:type="character" w:styleId="Toggle"><w:basedOn w:val="KeepHidden"/><w:rPr><w:vanish w:val="true"/></w:rPr></w:style>
            <w:style w:type="paragraph" w:styleId="PHidden"><w:rPr><w:vanish/></w:rPr></w:style>
            """;
        string document=Paragraph(Run("visible ")+CharacterRun(HiddenSecret,"<w:vanish/>")+Run("ensemble"))+
            Paragraph(CharacterRun(HiddenSecret,"<w:rStyle w:val=\"KeepHidden\"/>")+
                CharacterRun("visible par bascule ","<w:rStyle w:val=\"Toggle\"/>")+
                CharacterRun("visible par remise à zéro","<w:rStyle w:val=\"Hide\"/><w:vanish w:val=\"0\"/>"))+
            Paragraph(CharacterRun(HiddenSecret,"<w:vanish w:val=\"1\"/>"),
                "<w:spacing w:before=\"240\" w:after=\"120\" w:line=\"400\" w:lineRule=\"exact\"/>")+
            Paragraph(CharacterRun("Web visible ","<w:webHidden/>")+CharacterRun("run visible","<w:specVanish/>"))+
            Paragraph(Run(HiddenSecret)+CharacterRun("visible malgré le style de paragraphe","<w:vanish w:val=\"false\"/>"),
                "<w:pStyle w:val=\"PHidden\"/><w:rPr><w:vanish w:val=\"0\"/></w:rPr>")+Section(7200,7200);
        return Fixtures.Write(root,"masquage-texte.docx",Fixtures.Parts(document,
            new(){["word/styles.xml"]=Fixtures.Xml("styles",styles)},Fixtures.Relationship("styles","styles","styles.xml")));
    }

    internal static void HiddenVisibility(string root)
    {
        string path=HiddenTextDocument(root);
        var input=DocumentInspector.InspectDocument(path);
        using var lease=PageComposer.Compose(input);
        var s=lease.Snapshot;
        Complete(s,1);
        string[] expected=["visible ensemble","visible par bascule visible par remise à zéro","",
            "Web visible run visible","visible malgré le style de paragraphe"];
        var lines=s.Composition.Pages.Single().Lines;
        Check(lines.Select(l=>l.Text).SequenceEqual(expected),"Texte visible après la cascade.");
        Check(lines[2].Height==20&&lines[2].Native!.Advance==0&&lines[2].Native!.Visual.IsEmpty,
            "La marque visible garde l’interligne de la ligne vide sans glyphes.");
        var hidden=s.Typography!.Tokens.Where(t=>t.Style?.Hidden==true&&t.Original==HiddenSecret).ToArray();
        Check(hidden.Length==4&&hidden.All(t=>t.Source.Span==new Utf16Span(0,HiddenSecret.Length)),
            "Les quatre chaînes UTF-16 masquées restent dans le modèle.");
        Check(hidden[1].Style!.Origins["hidden"].NodeId!=hidden[0].Style!.Origins["hidden"].NodeId,"Provenances directe et héritée.");
        var reading=ReadableReader.Read(input);
        Check(reading.Presentation.BodyBlocks.Where(b=>b.Kind=="paragraph").Select(reading.Presentation.TextFor).SequenceEqual(expected),
            "La lecture continue et la composition partagent la visibilité.");
        RefuseHiddenExports(input,root);

        foreach(string property in new[]{"<w:vanish/>","<w:specVanish/>","<w:vanish/><w:specVanish/>"})
        {
            var mark="<w:rPr>"+property+"</w:rPr>";
            using var merged=Compose(Write(root,"marque-"+property.Length,P("Avant",mark)+P("Après")+Section()));
            Check(merged.Snapshot.Coverage!=PageCoverage.Complete&&
                merged.Snapshot.Composition.Issues.Any(i=>i.Code=="TEXT_HIDDEN_PARAGRAPH_MARK"),"Géométrie d’une marque masquée.");
        }
        using var invalid=Compose(Write(root,"vanish-invalide",Paragraph(CharacterRun(HiddenSecret,"<w:vanish w:val=\"incorrect\"/>"))+Section()));
        Check(invalid.Snapshot.Coverage!=PageCoverage.Complete&&invalid.Snapshot.Lines!.Lines.All(l=>l.Parts.IsEmpty),
            "Une valeur invalide garde un diagnostic sans divulguer le texte.");
        using var control=Compose(Write(root,"controle-masque",Paragraph(Run("Avant")+
            """<w:r><w:rPr><w:vanish/></w:rPr><w:br w:type="page"/></w:r>"""+Run("Après"))+Section()));
        Check(control.Snapshot.Coverage!=PageCoverage.Complete&&control.Snapshot.Composition.Issues.Any(i=>i.Code=="TEXT_HIDDEN_CONTROL_UNQUALIFIED"),
            "Géométrie d’un saut masqué hors profil.");
    }

    static void RefuseHiddenExports(InspectionResult input,string root)
    {
        foreach(var format in Enum.GetValues<ExportFormat>())
        {
            Check(!DocumentExports.Preflight(input,format).Allowed,"Précontrôle du contenu masqué.");
            string path=Path.Combine(root,"refus-masquage-"+format);
            try { DocumentExports.Export(input,format,path);throw new InvalidOperationException("L’export a été publié."); }
            catch(InvalidDataException error) when(error.Message=="EXPORT_PREFLIGHT_BLOCKED") {}
            Check(!Path.Exists(path),"Aucune sortie créée après le refus.");
        }
    }
}

static partial class PageCases
{
    internal static void HiddenTcFields(string root)
    {
        const string footer="""<w:footerReference w:type="default" r:id="foot"/>""";
        string section=Section(7200,7200,margins:900).Replace("<w:sectPr>","<w:sectPr>"+footer,StringComparison.Ordinal);
        string table="""
            <w:tbl><w:tblPr><w:tblW w:w="3500" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr>
            <w:tblGrid><w:gridCol w:w="3500"/></w:tblGrid>
            <w:tr><w:tc><w:tcPr><w:tcW w:w="3500" w:type="dxa"/></w:tcPr>{CELL}</w:tc></w:tr></w:tbl>
            """.Replace("{CELL}",Paragraph(Run("Cellule")+CharacterRun(HiddenSecret,"<w:vanish/>")+
                SimpleField("""TC &quot;Non sélectionnée cellule&quot; \f D""")));
        string operand=BeginDirty+Instruction(@" SEQ Number \r 1 \* ARABIC ")+Separator+
            CharacterRun("1","<w:vanish/>")+EndField;
        string complex=BeginDirty+Instruction(" T")+Instruction("C &quot;")+operand+
            Instruction(""" Entrée composée&quot; \f O \l 2 """)+EndField;
        string toc=BeginDirty+Instruction(@" TOC \f O \l 1-2 \h ")+Separator+Run("Sommaire inscrit ")+
            SimpleField(@"PAGEREF cible \h")+EndField;
        string direct=SimpleField("""TC &quot;Entrée sélectionnée&quot; \f O \l 1 \n""");
        string body=Paragraph(toc)+Paragraph(Bookmark("1","cible")+direct+complex+Run("Destination"))+table+section;
        string path=Fixtures.Write(root,"tc-mixte.docx",Fixtures.Parts(body,
            new(){["word/footer1.xml"]=Fixtures.Xml("ftr",Paragraph(Run("Pied")+CharacterRun(HiddenSecret,"<w:vanish/>")+
                SimpleField("""TC &quot;Entrée de pied&quot; \f O""")))},Fixtures.Relationship("foot","footer","footer1.xml")));
        var input=DocumentInspector.InspectDocument(path);
        using var lease=PageComposer.Compose(input);
        var s=lease.Snapshot;
        Complete(s,1);
        Check(s.FieldStability=="Stable"&&s.FieldPasses[^1].Fields.Single().Text=="1","PAGEREF voisin de TC.");
        Check(s.Composition.Pages.Single().DisplayLines.Select(l=>l.Text).SequenceEqual(new[]{"Sommaire inscrit ","Destination","Cellule","Pied"}),
            "Les quatre lignes visibles évitent le texte caché et les entrées TC.");
        Check(s.Semantic!.Fields.Count(f=>f.Command=="TC")==4&&s.Semantic.Fields.Any(f=>f.Command=="SEQ"&&f.ParentFieldId is not null),
            "TC et opérande imbriqué dans le modèle.");
        var entries=s.Semantic.TocEntries.ToDictionary(e=>e.Label);
        string tableOfContents=s.Semantic.Fields.Single(f=>f.Command=="TOC").Id;
        Check(entries["Entrée sélectionnée"] is {Level:1,Group:"O",OmitPageNumber:true} directEntry&&
            directEntry.SelectingTocIds.SequenceEqual([tableOfContents])&&entries["1 Entrée composée"] is {Level:2} nestedEntry&&
            nestedEntry.OperandFieldIds.Length==1&&nestedEntry.SelectingTocIds.SequenceEqual([tableOfContents])&&
            entries["Non sélectionnée cellule"].SelectingTocIds.IsEmpty&&entries["Entrée de pied"].SelectingTocIds.IsEmpty,
            "Les groupes, niveaux et histoires sélectionnent des métadonnées sans modifier le TOC enregistré.");
        var reading=ReadableReader.Read(input);
        Check(reading.Presentation.Fields.Values.Count(f=>f.Name=="TC")==4&&
            reading.Presentation.BodyBlocks.All(p=>!reading.Presentation.TextFor(p).Contains("Entrée",StringComparison.Ordinal)),
            "Le sommaire stocké reste la seule projection visible.");

        string malformed=Paragraph(BeginDirty+Instruction(@" TC &quot;Entrée incomplète&quot; \f O ") +Run("Après"));
        using var bad=Compose(Write(root,"tc-incomplet",P("Préfixe")+malformed+Section()));
        Check(bad.Snapshot.Coverage!=PageCoverage.Complete&&bad.Snapshot.Semantic!.Fields.Any(f=>f.Command=="TC"&&!f.StructureVerified),
            "Les champs TC corrompus restent inventoriés.");
        using var unsupported=Compose(Write(root,"tc-option",Paragraph(SimpleField("""TC &quot;Entrée&quot; \q"""))+Section()));
        Check(unsupported.Snapshot.Coverage!=PageCoverage.Complete&&unsupported.Snapshot.Semantic!.TocEntries.IsEmpty&&
            unsupported.Snapshot.Composition.Issues.Any(i=>i.Code=="SEMANTIC_TC_UNQUALIFIED"),"Options hors profil conservées.");
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        bool observed=false;
        try { using var unused=PageComposer.Compose(input,cancelled.Token); }
        catch(OperationCanceledException) { observed=true; }
        Check(observed,"Annulation de la composition contenant des TC.");
    }

    internal static void HiddenReadingGui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow{Width=1280,Height=900};
        window.Show();
        try
        {
            AboutCases.Complete(window.OpenDocumentAsync(HiddenTextDocument(root)));Layout();
            Check(window.CurrentPage==1,"Ouverture automatique en Page simple.");
            Click("Action_search_",prefix:true);
            var search=window.GetVisualDescendants().OfType<TextBox>().Single(c=>c.Name=="SearchBox");
            search.Text=HiddenSecret;Layout();
            Check(!Result(HiddenSecret).Any(),"La recherche paginée ignore le masqué.");
            search.Text="visible ensemble";Layout();
            Result("visible ensemble").Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
            Check(window.SelectedNativeSources.Count>0,"La recherche sélectionne le texte visible.");
            Click("ViewContinuous");Layout();
            search.Text=HiddenSecret;Layout();
            Check(!Result(HiddenSecret).Any(),"La recherche continue ignore le masqué.");
            search.Text="visible ensemble";Layout();
            Check(Result("visible ensemble").Any(),"La recherche continue retrouve la phrase visible.");
            string id=window.CurrentReading!.Presentation.BodyBlocks.First(b=>b.Kind=="paragraph").Id;
            Check(window.NavigateToBlock(id),"Navigation au passage visible.");Layout();
            var text=window.GetVisualDescendants().OfType<TextBlock>().Single(c=>Equals(c.Tag,id));
            text.ContextMenu!.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,"Copier le texte du passage"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult()=="visible ensemble","La copie respecte la visibilité.");
        }
        finally{window.Close();}

        IEnumerable<Button> Result(string text)=>window.GetVisualDescendants().OfType<Button>()
            .Where(b=>b.Content is string caption&&caption.StartsWith(text,StringComparison.Ordinal));
        void Click(string name,bool prefix=false)=>window.GetVisualDescendants().OfType<Button>()
            .Single(b=>prefix?b.Name?.StartsWith(name,StringComparison.Ordinal)==true:b.Name==name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
