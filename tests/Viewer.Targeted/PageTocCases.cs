using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class PageCases
{
    const string BeginDirty="""<w:r><w:fldChar w:fldCharType="begin" w:dirty="1"/></w:r>""";
    const string Separator="""<w:r><w:fldChar w:fldCharType="separate"/></w:r>""";
    const string EndField="""<w:r><w:fldChar w:fldCharType="end"/></w:r>""";
    const string BodyTab="""<w:r><w:tab/></w:r>""";
    const string PaperBreak="""<w:r><w:br w:type="page"/></w:r>""";
    const string ExactLine="""<w:spacing w:lineRule="exact" w:line="400"/><w:widowControl w:val="0"/>""";

    static string Instruction(string value)=>$"""<w:r><w:instrText xml:space="preserve">{value}</w:instrText></w:r>""";
    static string SimpleField(string value,string result="",string attributes="")=>
        $"""<w:fldSimple w:instr="{value}" {attributes}>{result}</w:fldSimple>""";
    static string BookmarkStart(string id,string name)=>$"""<w:bookmarkStart w:id="{id}" w:name="{name}"/>""";
    static string BookmarkEnd(string id)=>$"""<w:bookmarkEnd w:id="{id}"/>""";
    static string Bookmark(string id,string name)=>BookmarkStart(id,name)+BookmarkEnd(id);
    static string PageNumbering(string section,int start,string format="decimal")=>
        section.Replace("</w:sectPr>",$"""<w:pgNumType w:start="{start}" w:fmt="{format}"/></w:sectPr>""",StringComparison.Ordinal);

    internal static string TocDocument(string root)
    {
        const string tabs="""<w:tabs><w:tab w:val="right" w:leader="dot" w:pos="2400"/></w:tabs>""";
        const string resultRun="""<w:r><w:rPr><w:sz w:val="18"/></w:rPr><w:t/></w:r>""";
        string Link(string text,string name)=>$"""<w:hyperlink w:anchor="{name}">{Run(text)}</w:hyperlink>""";
        string first=BeginDirty+Instruction(@" TOC \h ")+Separator+
            Link("Premier chapitre","_TocPremier")+BodyTab+BeginDirty+Instruction(" PAGE")+Instruction(@"REF _TocPremier \h ")+
            Separator+resultRun+EndField;
        string second=Link("Second chapitre","_TocSecond")+BodyTab+SimpleField(@"PAGEREF _TocSecond \h")+EndField;
        string section=Section(4200,4600);
        string body=Paragraph(first,tabs)+Paragraph(second,tabs+"<w:pageBreakBefore/>")+
            P("Fin du sommaire",section)+
            Paragraph(BookmarkStart("7","_TocPremier")+Bookmark("8","Interne")+Run("Destination première")+BookmarkEnd("7"))+
            Paragraph(Run("Avant")+PaperBreak+Bookmark("9","_TocSecond")+Run("Destination seconde"))+
            PageNumbering(section,9,"lowerRoman");
        return Write(root,"sommaire-multipage",body);
    }

    internal static void BodyFieldConvergence(string root)
    {
        string tocPath=TocDocument(root);
        using var lease=Compose(tocPath);
        var s=lease.Snapshot;
        Complete(s,4);
        Check(s.FieldStability=="Stable"&&s.FieldPasses.Length is >=2 and <=PageComposer.MaxFieldPasses,"Point fixe du sommaire.");
        var semantic=s.Semantic!;
        var fields=semantic.Fields.Where(f=>f.Command=="PAGEREF").ToArray();
        Check(fields.Length==2&&fields.All(f=>f.ParentFieldId is not null&&f.BookmarkId is not null&&f.StoredResultIds.IsEmpty)&&
            fields[0].InstructionIds.Length==2&&fields[0].Dirty,"Codes distribués, TOC englobant et cache absent.");
        var entries=s.Composition.Pages.Take(2).Select(p=>p.Lines.First()).ToArray();
        Check(entries.Select(e=>e.Text.TrimEnd('\t')).SequenceEqual(new[]{"Premier chapitre","Second chapitre"}),
            "Texte source des entrées : "+string.Join(" / ",entries.Select(e=>e.Text)));
        Check(entries.All(e=>e.Native!.Generated.Any(g=>g.Kind=="leader"&&g.Count>0)),"Points de suite mesurés.");
        var labels=entries.Select(e=>e.Native!.Generated.Single(g=>g.Kind=="pageField")).ToArray();
        Check(labels.Select(g=>g.Fragment.Text).SequenceEqual(new[]{"ix","x"})&&
            labels[0].Fragment.PointSize==9&&labels.All(g=>g.Cause.Span is null&&g.Fragment.Measure!.Metrics.Width>0),
            "Numéros des cibles en chiffres romains et style du run résultat.");
        Check(entries.Zip(labels).All(pair=>pair.First.Native!.Tabs.Single() is {Stop.Kind:LineTabKind.Right} tab&&
            pair.Second.X+pair.Second.Fragment.Measure!.Metrics.Width==pair.First.Native.Region.Left+tab.Stop.Position),
            "Le bord droit du numéro reste au taquet et la suite s’arrête avant le chiffre.");
        Check(fields.Select(f=>s.FieldBookmarks[f.BookmarkId!].Page).SequenceEqual(new[]{3,4})&&s.PageLinkTargets.Count==4,
            "Cible physique dans une autre section, signets et liens.");
        Check(s.Pages.Select(p=>p.SectionPageNumber).SequenceEqual(new int?[]{1,2,9,10}),"Reprise distincte de l’ordinal physique.");
        Check(entries.All(e=>!PageCoordinates.Clusters(e).Any(c=>c.Sources.Any(src=>fields.Any(f=>f.Id==src.NodeId)))),
            "Les mots calculés gardent leurs rectangles de lien hors de la sélection source.");
        var input=DocumentInspector.InspectDocument(tocPath);
        Check(Enum.GetValues<ExportFormat>().All(f=>!DocumentExports.Preflight(input,f).Allowed),
            "Les gardes des formats sortants refusent les champs.");
        VerifyPlaced(s);

        string stored=Paragraph(Run("Entrée")+BodyTab+BeginDirty+Instruction(@" PAGEREF cible \h ")+
            Separator+Run("99")+EndField,
            """<w:tabs><w:tab w:val="right" w:leader="dot" w:pos="2400"/></w:tabs>""")+
            Paragraph(Bookmark("11","cible")+Run("Destination"),"<w:pageBreakBefore/>")+Section();
        using(var cached=Compose(Write(root,"renvoi-resultat-stocke",stored)))
        {
            var snapshot=cached.Snapshot;
            Complete(snapshot,2);
            var cachedModel=snapshot.Semantic!;
            var field=cachedModel.Fields.Single(f=>f.Command=="PAGEREF");
            var line=snapshot.Pages[0].Geometry.Lines.Single();
            var projected=line.Native!.Generated.Single(g=>g.Kind=="pageField");
            Check(cachedModel.StoredText(field)=="99"&&projected.Fragment.Text=="2"&&
                line.Native.ContextRange.EndExclusive==snapshot.Typography!.Paragraphs.Single(p=>p.Id==line.ParagraphId).ContextLength&&
                snapshot.PageLinkTargets[field.Id]==field.BookmarkId,
                "Le résultat stocké reste sourcé et le renvoi recalculé conserve sa plage et sa navigation.");
        }

        string narrow=Section(3860,2880,margins:840);
        string widening=P("Début",ExactLine)+Paragraph(Run("Sommaire bref ")+SimpleField(@"PAGEREF cible \# 00000000"),ExactLine)+
            Paragraph(Bookmark("1","cible")+Run("Destination"),ExactLine)+narrow;
        using var changed=Compose(Write(root,"largeur-champ",widening));
        var moved=changed.Snapshot;
        Complete(moved,2);
        Check(moved.FieldPasses.Length>=3&&moved.FieldPasses[0].Fields.Single().PhysicalPage==1&&
            moved.FieldPasses[^1].Fields.Single() is {PhysicalPage:2,Text:"00000002"}&&moved.FieldPasses[0].Lines<moved.FieldPasses[^1].Lines,
            "Le numéro élargi doit déplacer réellement la cible puis être recomposé.");
        Check(moved.Composition.Pages.SelectMany(p=>p.Lines).SelectMany(l=>l.Native!.Generated).Count(g=>g.Kind=="pageField")==1,
            "Le champ doit apparaître sur une seule ligne après la coupure.");

        const string english="""<w:r><w:rPr><w:lang w:val="en-US"/></w:rPr><w:t/></w:r>""";
        string relativeField=SimpleField(@"PAGEREF cible \p",english);
        string relative=Paragraph(relativeField)+Paragraph(Bookmark("2","cible")+Run("Destination"))+Paragraph(relativeField)+
            Paragraph(PaperBreak+relativeField)+Paragraph(SimpleField("NUMPAGES"))+PageNumbering(Section(),10,"upperRoman");
        using var texts=Compose(Write(root,"renvois-relatifs",relative));
        Complete(texts.Snapshot,2);
        Check(texts.Snapshot.FieldPasses[^1].Fields.Select(f=>f.Text).SequenceEqual(new[]{"below","above","on page X","2"}),
            "Renvoi relatif, format de section, total physique.");

        string continued=P("Avant",PageNumbering(Section(),27,"upperLetter"))+
            Paragraph(Bookmark("3","cible")+Run("Destination")+PaperBreak+SimpleField("PAGE"),
                """<w:sectPr><w:type w:val="continuous"/><w:pgNumType w:fmt="upperLetter"/></w:sectPr>""")+
            Paragraph(SimpleField("PAGEREF &quot;cible&quot;"))+
            Section().Replace("</w:sectPr>","""<w:pgNumType w:fmt="upperLetter"/></w:sectPr>""",StringComparison.Ordinal);
        using var letters=Compose(Write(root,"renvois-alphabetiques",continued));
        Complete(letters.Snapshot,3);
        Check(letters.Snapshot.Pages.Select(p=>p.SectionPageNumber).SequenceEqual(new int?[]{27,28,29})&&
            letters.Snapshot.FieldPasses[^1].Fields.Select(f=>f.Text).SequenceEqual(new[]{"BB","AA"}),
            "Continuité des pages physiques et numérotation alphabétique de section.");

        string totals=Paragraph(Run("Section ")+SimpleField("SECTIONPAGES",Run("99"),"""w:dirty="true" """))+
            Paragraph(PaperBreak+SimpleField("SECTIONPAGES"),PageNumbering(Section(),27))+
            Paragraph(BeginDirty+Instruction(@" SECTIONPAGES \# 000 ")+Separator+Run("88")+EndField)+
            Paragraph(SimpleField("NUMPAGES"))+PageNumbering(Section(),50);
        using var counts=Compose(Write(root,"totaux-physiques-section",totals));
        Complete(counts.Snapshot,3);
        Check(counts.Snapshot.Pages.Select(p=>p.SectionPageNumber).SequenceEqual(new int?[]{27,28,50})&&
            counts.Snapshot.FieldPasses[^1].Fields.Select(f=>f.Text).SequenceEqual(new[]{"2","2","001","3"}),
            "Totaux physiques distincts des numéros affichés et actualisation des caches simples et complexes.");
        string crossReference=Paragraph(Run("Lire ")+BeginDirty+Instruction(@" REF identifiant \h ")+Separator+
            Run("Cache") + EndField)+Paragraph(PaperBreak+BookmarkStart("4","identifiant")+Run("Chapitre42")+BookmarkEnd("4"))+Section();
        using var textual=Compose(Write(root,"renvoi-texte-bookmark",crossReference));
        Complete(textual.Snapshot,2);
        var reference=textual.Snapshot.Semantic!.Fields.Single();
        var resolved=textual.Snapshot.FieldPasses[^1].Fields.Single();
        Check(resolved is {Text:"Chapitre42",PhysicalPage:2}&&
            textual.Snapshot.Semantic.StoredText(reference)=="Cache"&&textual.Snapshot.PageLinkTargets[reference.Id]==reference.BookmarkId,
            "REF résout et mesure le texte actuel du signet. Le cache et le lien gardent leur provenance.");
    }

    internal static void BodyFieldOscillation(string root)
    {
        string body=P("Début",ExactLine)+Paragraph(Run("Sommaire bref ")+SimpleField(@"PAGEREF cible \* roman"),ExactLine)+
            Paragraph(Bookmark("1","cible")+Run("Destination"),ExactLine)+PageNumbering(Section(3860,2880,margins:840),8);
        using var lease=Compose(Write(root,"oscillation-champ",body));
        var s=lease.Snapshot;
        Check(s.FieldStability=="PAGE_FIELD_OSCILLATION"&&s.FieldPasses.Length==PageComposer.MaxFieldPasses&&
            s.FieldPasses.Select(p=>p.Fields.Single().PhysicalPage).SequenceEqual(new int?[]{1,2,1,2}),
            "La largeur de viii et ix produit un cycle de pages détecté à la limite de travail.");
        Check(s.Coverage==PageCoverage.Partial&&!s.Composition.Available&&
            s.Composition.Pages.SelectMany(p=>p.Lines).Select(line=>line.Text.Trim())
                .SequenceEqual(new[]{"Début","Sommaire bref","Destination"})&&
            s.Composition.Pages.SelectMany(p=>p.Lines).All(line=>line.Native!.Generated.IsEmpty)&&s.FieldBookmarks.Count==0,
            "Après le rejet du cycle, les lignes candidates gardent le texte source sans résultat d’essai.");
    }

    internal static void BodyFieldRefusals(string root)
    {
        const string lockedAttribute="w:fldLock=\"1\"";
        const string dirtyAttribute="w:dirty=\"true\"";
        string target=Paragraph(Bookmark("1","cible")+Run("Destination"));
        string reference=Paragraph(SimpleField("PAGEREF cible"));
        Test("cible-absente",reference+P("Sans cible"),"PAGE_FIELD_BOOKMARK_UNRESOLVED");
        Test("id-invalide",reference+Paragraph(Bookmark("x","cible")+Run("Destination")),"PAGE_FIELD_BOOKMARK_UNRESOLVED");
        Test("sans-fin",reference+Paragraph(BookmarkStart("1","cible")+Run("Destination")),"PAGE_FIELD_BOOKMARK_UNRESOLVED");
        Test("ordre-inverse",reference+Paragraph(BookmarkEnd("1")+BookmarkStart("1","cible")+Run("Destination")),"PAGE_FIELD_BOOKMARK_UNRESOLVED");
        Test("deux-fins",reference+Paragraph(Bookmark("1","cible")+BookmarkEnd("1")+Run("Destination")),"PAGE_FIELD_BOOKMARK_UNRESOLVED");
        Test("id-double",reference+Paragraph(Bookmark("1","autre"))+target,"PAGE_FIELD_BOOKMARK_UNRESOLVED");
        Test("nom-double",reference+target+Paragraph(Bookmark("2","cible")),"PAGE_FIELD_BOOKMARK_UNRESOLVED");
        Test("cible-non-placee",reference+P("Flottant \U0010fffd")+target,"PAGE_FIELD_BOOKMARK_UNPLACED");
        Test("verrou-vide",Paragraph(SimpleField("PAGEREF cible",attributes:lockedAttribute))+target,"PAGE_FIELD_VALUE_UNRESOLVED");
        Test("cache-dynamique-non-terminal",Paragraph(SimpleField("PAGEREF cible",Run("99"),dirtyAttribute)+Run(" suite"))+
            target,"PAGE_FIELD_STORED_PENDING");
        Test("option-inconnue",Paragraph(SimpleField(@"PAGEREF cible \z"))+target,"PAGE_FIELD_SWITCH_UNQUALIFIED");
        Test("ref-mot-non-prouve",Paragraph(SimpleField("REF cible",Run("Cache")))+target,"PAGE_FIELD_REF_TEXT_UNQUALIFIED");
        Test("verrou-invalide",Paragraph(SimpleField("PAGEREF cible",attributes:"w:fldLock=\"invalid\""))+target,"PAGE_FIELD_SWITCH_UNQUALIFIED");
        Test("langue-relative-inconnue",Paragraph(SimpleField(@"PAGEREF cible \p"))+target,"PAGE_FIELD_VALUE_UNRESOLVED");
        Test("operande-imbrique",Paragraph(BeginDirty+Instruction(" REF cible ")+
            SimpleField("PAGEREF cible")+Separator+Run("cache")+EndField)+target,"PAGE_FIELD_VALUE_UNRESOLVED");
        Test("instruction-inconnue",Paragraph(SimpleField("UNKNOWN"))+target,"PAGE_FIELD_PROFILE_UNQUALIFIED");
        Test("champ-inacheve",Paragraph(BeginDirty+Instruction(" PAGEREF cible ")+Separator)+target,"LAYOUT_RUN_CONTENT_UNQUALIFIED");
        using(var parity=Compose(Write(root,"total-section-feuille-parite",P("Avant",Section())+
            Paragraph(SimpleField("SECTIONPAGES"))+Section(start:"oddPage"))))
        {
            Check(parity.Snapshot.Coverage==PageCoverage.Partial&&
                parity.Snapshot.FieldPasses.LastOrDefault() is {Pages:3,Fields:[{Diagnostic:"PAGE_FIELD_SECTION_TOTAL_UNRESOLVED"}]}&&
                parity.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_FIELD_SECTION_TOTAL_UNRESOLVED"),
                "La feuille intercalée par la parité réclame une attribution qualifiée avant le calcul du total.");
        }
        Test("quota-champs",string.Concat(Enumerable.Repeat(reference,129))+target,"PAGE_BODY_FIELD_LIMIT");

        using var locked=Compose(Write(root,"cache-verrouille",
            Paragraph(SimpleField("PAGEREF cible",Run("99"),lockedAttribute))+target+Section()));
        Complete(locked.Snapshot,1);
        Check(Line(locked.Snapshot,"99").Native!.Generated.IsEmpty&&locked.Snapshot.FieldPasses.IsEmpty,"Le cache verrouillé reste du texte source.");
        using var pending=Compose(Write(root,"cache-source-candidat",
            Paragraph(SimpleField("PAGEREF absent",Run("99"),dirtyAttribute))+
            Paragraph(PaperBreak+Run("Page suivante"))+Section()));
        Check(pending.Snapshot.Coverage==PageCoverage.Partial&&!pending.Snapshot.Composition.Available&&
            pending.Snapshot.Pages.Length==2&&Line(pending.Snapshot,"99").Native!.Generated.IsEmpty&&
            Line(pending.Snapshot,"Page suivante").Native!.Generated.IsEmpty&&
            pending.Snapshot.Gaps.Any(g=>g.Reason=="PAGE_FIELD_BOOKMARK_UNRESOLVED"&&g.ContextRange is {Start:0,EndExclusive:2}),
            "Le cache source permet de composer la suite. Le renvoi indéterminé bloque la publication native.");
        using var empty=Compose(Write(root,"renvoi-sans-cache-candidat",
            Paragraph(Run("Entrée ")+SimpleField("PAGEREF absent"))+
            Paragraph(PaperBreak+Run("Suite"))+Section()));
        Check(empty.Snapshot.Coverage==PageCoverage.Partial&&empty.Snapshot.Pages.Length==2&&
            empty.Snapshot.Pages[0].Geometry.Lines.Count(line=>line.Text.StartsWith("Entrée",StringComparison.Ordinal))==1&&
            Line(empty.Snapshot,"Suite").Native!.Generated.IsEmpty&&
            empty.Snapshot.Pages.SelectMany(p=>p.Geometry.Lines).All(line=>line.Native!.Generated.IsEmpty),
            "La mesure du texte disponible n’injecte aucun résultat d’essai pour un champ sans cache.");
        using var multiple=Compose(Write(root,"caches-et-taquet-dans-le-même-paragraphe",
            Paragraph(SimpleField("PAGE",Run("7"))+BodyTab+
                SimpleField("NUMPAGES",Run("8"))+Run(" suite"))+Paragraph(PaperBreak+Run("Après"))+Section()));
        Check(multiple.Snapshot.Coverage==PageCoverage.Partial&&multiple.Snapshot.Pages.Length==2&&
            Line(multiple.Snapshot,"7\t8 suite").Native is {Tabs.Length:1,Generated.IsEmpty:true}&&
            multiple.Snapshot.Gaps.Count(g=>g.Reason=="PAGE_FIELD_STORED_PENDING")==2,
            "Plusieurs résultats source et un taquet gardent leurs places dans un paragraphe candidat.");
        var path=Write(root,"annulation",reference+target+Section());
        using var cancellation=new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled=false;
        try { using var unused=PageComposer.Compose(DocumentInspector.InspectDocument(path),cancellation.Token); }
        catch(OperationCanceledException) { cancelled=true; }
        Check(cancelled,"Annulation du calcul.");

        void Test(string name,string body,string reason)
        {
            using var lease=Compose(Write(root,name,P("Préfixe sûr")+body+Section()));
            var s=lease.Snapshot;
            Check(s.Coverage!=PageCoverage.Complete&&s.Composition.Issues.Any(i=>i.Code==reason),
                name+" / "+reason+" / "+string.Join(", ",s.Composition.Issues.Select(i=>i.Code)));
            Check(s.Composition.Pages.SelectMany(p=>p.Lines).All(l=>l.Native!.Generated.All(g=>g.Kind!="pageField")),
                "Une composition rejetée contient une valeur de travail.");
        }
    }

    internal static void BodyFieldNavigation(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow{Width=1280,Height=900};
        window.Show();
        try
        {
            string path=TocDocument(root);
            AboutCases.Complete(window.OpenDocumentAsync(path));Layout();
            var snapshot=window.ReadingTableSnapshot!;
            Complete(snapshot,4);
            Check(window.CurrentReadingView==ReadingView.SinglePage&&window.CurrentPage==1,"Page simple automatique.");
            for(int i=0;i<2;i++)
            {
                if(i==1){Click("ViewFacingPages");Layout();}
                window.GoToPage(i);window.SetZoom(i==0?.8:1.5);Layout();
                var entry=snapshot.Pages[i].Geometry.Lines.First();
                PageRect rect=i==0?PageCoordinates.Clusters(entry).First().Rect:PageCoordinates.FieldLabels(entry).Single().Rect;
                var sheet=window.GetVisualDescendants().OfType<Image>().Single(c=>c.Name=="CalculatedPage"&&Equals(c.Tag,i+1));
                double scale=window.Zoom*96/72;
                var point=sheet.TranslatePoint(new((rect.Left+rect.Width/2)*scale,(rect.Top+rect.Height/2)*scale),window);
                Check(point is not null,"Rectangle du lien dans la feuille.");
                window.MouseDown(point!.Value,MouseButton.Left);
                window.MouseUp(point.Value,MouseButton.Left);
                Layout();
                Check(window.CurrentPage==i+3&&ReferenceEquals(snapshot,window.ReadingTableSnapshot),
                    "Le lien du titre ou du numéro atteint la page physique de son signet.");
            }

            Click("Action_search_",prefix:true);
            window.GetVisualDescendants().OfType<TextBox>().Single(c=>c.Name=="SearchBox").Text="Second chapitre";
            Layout();
            window.GetVisualDescendants().OfType<Button>().Single(c=>c.Content is string label&&label.StartsWith("Second chapitre",StringComparison.Ordinal))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            Check(window.CurrentPage==2&&window.SelectedNativeSources.All(s=>s.Span is not null)&&window.SelectedNativeSources.Count>0,
                "La recherche sélectionne la source de l’entrée.");
            Click("ViewContinuous");Layout();
            var entryText=window.GetVisualDescendants().OfType<TextBlock>().Single(c=>c.Tag is string id&&
                id==snapshot.Pages[1].Geometry.Lines.First().ParagraphId);
            entryText.ContextMenu!.Items.OfType<MenuItem>().Single(c=>Equals(c.Header,"Copier le texte du passage"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            string? copied=window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult();
            Check(copied?.TrimEnd('\t')=="Second chapitre","La copie conserve le titre et exclut le numéro de page calculé.");
        }
        finally{window.Close();}

        void Click(string name,bool prefix=false)=>window.GetVisualDescendants().OfType<Button>()
            .Single(c=>prefix?c.Name?.StartsWith(name,StringComparison.Ordinal)==true:c.Name==name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
