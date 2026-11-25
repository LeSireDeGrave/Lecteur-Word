using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    internal static void StoryStyles(string root)
    {
        const string style="""
            <w:style w:type="paragraph" w:styleId="Employe">
              <w:name w:val="Employé"/><w:pPr><w:spacing w:after="120"/></w:pPr><w:rPr><w:sz w:val="24"/></w:rPr>
            </w:style>
            """;
        string body=P("Paragraphe avec style","""<w:pStyle w:val="Employe"/>""")+P("Paragraphe suivant")+Section();
        using var equivalent=Compose(Fixture("styles-equivalent",style.Replace("Employé","Étiquette",StringComparison.Ordinal)+
            """<w:style w:type="character" w:styleId="Inutilise"><w:rPr><w:sz w:val="40"/></w:rPr></w:style>"""));
        Complete(equivalent.Snapshot,1);
        Check(equivalent.Snapshot.StyleDecision?.EffectsImpact==DependencyImpact.NoLayoutEffect,"Collection auxiliaire équivalente classée A.");
        Check(equivalent.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_EQUIVALENT"&&!i.BlocksRendering),"Décision explicite des styles équivalents.");
        using var divergent=Compose(Fixture("styles-divergent",style.Replace("120","300",StringComparison.Ordinal)));
        Complete(divergent.Snapshot,1);
        Check(divergent.Snapshot.StyleDecision is
            {Selection:PageStyleSelection.OrdinaryWithPlainCopy,EffectsImpact:DependencyImpact.NoLayoutEffect}&&
            divergent.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_DIVERGENT"&&
                i.Source?.PartUri=="/word/aux.xml"&&!i.BlocksRendering),
            "Divergence de géométrie conservée sans remplacer la collection ordinaire.");
        using var missing=Compose(Fixture("styles-missing",""));
        Complete(missing.Snapshot,1);
        Check(missing.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_MISSING_STYLE"&&!i.BlocksRendering),
            "Style actif absent de la copie auxiliaire.");
        Refused(Fixture("styles-wrong-content",style,correctType:false));
        Refused(Fixture("styles-external",style,mode:"External"));

        static void Refused(string file)
        {
            var input=DocumentInspector.InspectDocument(file);
            using var invalid=PageComposer.Compose(input);
            Check(invalid.Snapshot.Coverage!=PageCoverage.Complete&&
                (invalid.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_INVALID")||
                 !input.Manifest.InspectionComplete&&input.Manifest.Diagnostics.Any(d=>d.Code=="SDK_PACKAGE_REFUSED")),
                "La relation de styles invalide doit être refusée par OPC ou le profil natif.");
        }

        string Fixture(string name,string effects,bool correctType=true,string mode="Internal")
        {
            string target=mode=="Internal"?"aux.xml":"https://example.invalid/a.xml";
            var parts=Fixtures.Parts(body,new()
                {
                    ["word/styles.xml"]=Fixtures.Xml("styles",style),
                    ["word/aux.xml"]=Fixtures.Xml("styles",effects),
                    ["word/settings.xml"]=Fixtures.Xml("settings","<w:compat>"+CompatibilityMode(14)+"</w:compat>")
                },
                Fixtures.Relationship("styles","styles","styles.xml")+
                Fixtures.Relationship("settings","settings","settings.xml")+
                $"""<Relationship Id="aux" Type="{LayoutReader.StylesEffectsRelation}" Target="{target}" TargetMode="{mode}"/>""");
            if(correctType)parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(parts["[Content_Types].xml"])
                .Replace("application/vnd.openxmlformats-officedocument.wordprocessingml.aux+xml","application/vnd.ms-word.stylesWithEffects+xml",
                    StringComparison.Ordinal));
            return Fixtures.Write(root,name+".docx",parts);
        }
    }

    internal static void RunningStories(string root)
    {
        const string first= """<w:headerReference w:type="first" r:id="h1"/>""";
        const string odd= """<w:headerReference w:type="default" r:id="h2"/>""";
        const string even= """<w:headerReference w:type="even" r:id="h3"/>""";
        const string foot= """<w:footerReference w:type="default" r:id="f1"/>""";
        const string evenFoot= """<w:footerReference w:type="even" r:id="f1"/>""";
        const string locked= """<w:fldSimple w:instr="NUMPAGES" w:fldLock="1"><w:r><w:t>99</w:t></w:r></w:fldSimple>""";
        const string explicitBreak="""<w:r><w:br w:type="page"/></w:r>""";
        const string tab="""<w:r><w:tab/></w:r>""";
        const string inline="""
            <w:r><w:drawing xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <wp:inline><wp:extent cx="228600" cy="114300"/><wp:docPr id="8" name="Image"/>
                <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <pic:pic><pic:blipFill><a:blip r:embed="pic"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="228600" cy="114300"/></a:xfrm><a:prstGeom prst="rect"/></pic:spPr></pic:pic>
                </a:graphicData></a:graphic></wp:inline></w:drawing></w:r>
            """;
        string table="""
            <w:tbl><w:tblPr><w:tblW w:w="2160" w:type="dxa"/><w:tblLayout w:type="fixed"/></w:tblPr>
              <w:tblGrid><w:gridCol w:w="2160"/></w:tblGrid>
            """+"<w:tr><w:tc>"+P("Table paire")+"</w:tc></w:tr></w:tbl>";
        string initial=Format(first+odd+even+foot+evenFoot,"""<w:titlePg/><w:pgNumType w:start="4"/>""");
        string next=Format("", """<w:pgNumType w:start="10"/>""");
        string source=Fixture("story-pages",Paragraph(Run("Corps un")+explicitBreak+Run("Corps deux")+explicitBreak+Run("Corps trois"),initial)+
            P("Corps quatre")+next,locked);
        using var lease=Compose(source);
        var s=lease.Snapshot;
        Complete(s,4);
        var pages=s.Composition.Pages;
        Check(s.Pages.Select(p=>p.SectionPageNumber).SequenceEqual(new int?[]{4,5,6,10})&&
            pages.Select(p=>p.RunningStories.First().Selection.Variant).SequenceEqual(new[]{"first","default","even","even"}),
            "Priorité first, parité de la numérotation déclarée et redémarrage.");
        Check(pages.Select(p=>p.Lines.Count).All(n=>n==1)&&pages[3].RunningStories.All(r=>r.Selection.DeclaringSection==1)&&
            pages[3].RunningStories.First().Selection.Reference.Relation?.Id=="h3","Corps et héritage des relations.");
        var region=pages[1].RunningStories[0];
        var tabbed=region.Lines.Single();
        Check(tabbed.Tabs.Count==1&&tabbed.Tabs[0].To>tabbed.Tabs[0].From&&tabbed.Source.PartUri=="/word/header2.xml"&&
            pages[2].RunningStories[0].Cells.Count==1&&pages[2].RunningStories[1].Lines.Single().Images.Count==1&&
            pages[1].RunningStories[1].Lines.Single().Text.Contains("99",StringComparison.Ordinal),"Taquet, tableau, image et résultat stocké.");
        foreach(var page in pages)
        foreach(var running in page.RunningStories)
        {
            Check(running.Selection.Kind=="header"?running.Region.Top+running.Region.Height<=PageComposer.Emu(page.Geometry.Top):
                running.Region.Top>=PageComposer.Emu(page.Geometry.Height-page.Geometry.Bottom),"Une région auxiliaire recouvre le corps.");
        }
        Check(s.Semantic!.Fields.Single() is {Locked:true,Command:"NUMPAGES"}&&s.Semantic.Fields.Select(s.Semantic.StoredText).Single()=="99"&&
            s.Semantic.References.Any(r=>r.Kind=="hyperlink"&&r.Status==ReferenceStatus.External),"Identité des champs et liens.");
        VerifyPlaced(s);
        File.WriteAllBytes(Path.Combine(root,"en-tete-pair.png"),Composer.PagePng(pages[2],1.5f));
        using var raster=SKBitmap.Decode(Composer.PagePng(pages[2]));
        var image=pages[2].RunningStories[1].Lines.Single().Images.Single();
        Check(raster.GetPixel((int)image.X+4,(int)image.Y+4) is {Red:>170,Green:<130},"Image réellement dessinée dans le pied.");

        using var blank=Compose(Fixture("story-blank",Paragraph(Run("Avant")+explicitBreak+Run("Après"))+
            Format(odd,"<w:titlePg/>"),locked,false));
        Complete(blank.Snapshot,2);
        Check(blank.Snapshot.Composition.Pages[0].RunningStories.Count==0&&
            blank.Snapshot.Composition.Pages[1].RunningStories.Single().Selection.Variant=="default","Variante absente et en-tête pair désactivé.");
        using var dynamic=Compose(Fixture("story-dynamic",P("Corps")+Format(foot,""),locked.Replace(" w:fldLock=\"1\"","",StringComparison.Ordinal)));
        Check(dynamic.Snapshot.Coverage==PageCoverage.Partial&&dynamic.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STORY_FIELD_DYNAMIC_PENDING")&&
            dynamic.Snapshot.Composition.Pages[0].RunningStories.Single().Lines[0].Text.EndsWith("99",StringComparison.Ordinal),
            "Champ dynamique diagnostiqué et copie stockée préservée.");
        using var absent=Compose(Fixture("story-no-result",P("Corps")+Format(foot,""),"""<w:fldSimple w:instr="PAGE"/>"""));
        Complete(absent.Snapshot,1);
        Check(absent.Snapshot.Composition.Pages[0].RunningStories.Single().Lines.Single().Native!.Generated
            .Single(g=>g.Kind=="pageField").Fragment.Text=="1",
            "Champ PAGE sans cache calculé dans son paragraphe auxiliaire.");
        string inlinePage=BeginDirty+Instruction(" PAGE ")+Separator+
            CharacterRun("99","<w:w w:val=\"90\"/>")+EndField+tab+Run("Titre");
        using var replaced=Compose(Fixture("story-inline-cache",P("Corps")+Format(foot,"<w:pgNumType w:start=\"7\"/>"),
            inlinePage));
        Complete(replaced.Snapshot,1);
        var replacedLine=replaced.Snapshot.Composition.Pages[0].RunningStories.Single().Lines.Single();
        var inlineNumber=replacedLine.Native!.Generated.Single(g=>g.Kind=="pageField");
        Check(inlineNumber.Fragment.Text=="7"&&inlineNumber.Fragment.Style.HorizontalScale==90&&
            inlineNumber.X<replacedLine.Native.Tabs.Single().From&&replacedLine.Text.Contains("Titre",StringComparison.Ordinal)&&
            !replacedLine.Text.Contains("99",StringComparison.Ordinal)&&
            replaced.Snapshot.Semantic!.Fields.Any(f=>replaced.Snapshot.Semantic.StoredText(f)=="99"),
            "Un PAGE au milieu d’une ligne conserve sa police, la tabulation et le texte suivant. Le cache reste dans le modèle.");
        string countedBody=P("Début")+Paragraph(explicitBreak+Run("Suite"),Format(foot,""))+
            P("Autre section")+Format("","");
        using var counted=Compose(Fixture("story-section-pages",countedBody,
            SimpleField("SECTIONPAGES",Run("99"),"""w:dirty="true" """),oddEven:false));
        Complete(counted.Snapshot,3);
        Check(counted.Snapshot.Composition.Pages.Select(p=>p.RunningStories.Single().Lines.Single().Native!.Generated
            .Single(g=>g.Kind=="pageField").Fragment.Text).SequenceEqual(new[]{"2","2","1"}),
            "Le même pied hérité affiche le total physique de sa section.");
        using var unsupported=Compose(Fixture("story-unsupported-field",P("Corps")+Format(foot,""),
            """<w:fldSimple w:instr="PAGE \p"/>"""));
        Check(unsupported.Snapshot.Coverage==PageCoverage.Partial&&
            unsupported.Snapshot.Gaps.Any(g=>g.Reason=="PAGE_STORY_FIELD_UNRESOLVED"),
            "Instruction auxiliaire hors profil diagnostiquée.");
        using(var prefix=Compose(Fixture("story-page-prefix",P("Préfixe composé")+
            Paragraph("<w:r><w:rPr><w:emboss/></w:rPr><w:t>Suite hors profil</w:t></w:r>")+Format(foot,""),
            SimpleField("PAGE",Run("99")))))
        {
            var candidate=prefix.Snapshot.Composition.Pages.Single();
            Check(prefix.Snapshot.Coverage==PageCoverage.Partial&&candidate.RunningStories.Single().Lines.Single().Native!
                .Generated.Single(g=>g.Kind=="pageField").Fragment.Text=="1",
                "Le PAGE utilise le numéro de l’occurrence candidate même si le corps est incomplet.");
        }
        using var overlap=Compose(Fixture("story-overlap",P("Corps")+Format(odd,"",margins:200),locked));
        Complete(overlap.Snapshot,1);
        var expanded=overlap.Snapshot.Composition.Pages.Single();
        var expandedHeader=expanded.RunningStories.Single();
        Check(expanded.Geometry.Top>10&&
            PageComposer.Emu(expanded.Geometry.Top)==expandedHeader.Region.Top+expandedHeader.Region.Height&&
            expanded.Lines.Single().NativeTop>=expanded.Geometry.Top-.02f,"La hauteur mesurée de l’en-tête agrandit la marge positive.");
        using var conflict=Compose(Fixture("story-ambiguous",P("Corps")+Format(odd+odd.Replace("h2","h1",StringComparison.Ordinal),""),locked));
        Check(conflict.Snapshot.Coverage==PageCoverage.Partial&&conflict.Snapshot.StoryRegions.Any(r=>r.Status=="Unresolved"),
            "Une référence ambiguë ne devient pas une variante blanche.");
        using var budget=Compose(Fixture("story-budget",P("Corps")+Format(odd,""),locked,
            headerOverride:string.Concat(Enumerable.Repeat("<w:p/>",2049))));
        Check(budget.Snapshot.Coverage==PageCoverage.Partial&&budget.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STORY_PARAGRAPH_LIMIT"),
            "Quota de paragraphes auxiliaires.");
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        bool interruption=false;
        try{using var unused=PageComposer.Compose(DocumentInspector.InspectDocument(source),cancelled.Token);}
        catch(OperationCanceledException){interruption=true;}
        Check(interruption,"Annulation de la composition auxiliaire.");

        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow();
        window.Show();
        try
        {
            AboutCases.Complete(window.OpenDocumentAsync(source));
            Check(window.NativePageStatus=="Complete"&&window.CurrentPage==1,"Adoption par Avalonia du témoin complet.");
            window.GetVisualDescendants().OfType<Button>().First(b=>b.Name?.StartsWith("Action_search_",StringComparison.Ordinal)==true)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="SearchBox").Text="Table paire";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content is string t&&t.Contains("Table paire",StringComparison.Ordinal))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.CurrentPage==3&&window.SelectedNativeSources.All(v=>v.Location.PartUri=="/word/header3.xml"),
                "Navigation d’une occurrence auxiliaire vers sa source.");
        }
        finally{window.Close();}

        static string Format(string refs,string extra,int margins=900)=>
            Section(5000,5000,margins:margins).Replace("<w:sectPr>","<w:sectPr>"+refs,StringComparison.Ordinal)
                .Replace("</w:sectPr>",extra+"</w:sectPr>",StringComparison.Ordinal);
        string Fixture(string name,string body,string field,bool oddEven=true,string? headerOverride=null)
        {
            string links=Fixtures.Relationship("h1","header","header1.xml")+Fixtures.Relationship("h2","header","header2.xml")+
                Fixtures.Relationship("h3","header","header3.xml")+Fixtures.Relationship("f1","footer","footer1.xml")+
                Fixtures.Relationship("settings","settings","settings.xml");
            using var bitmap=new SKBitmap(16,8);
            bitmap.Erase(SKColors.OrangeRed);
            using var encoded=bitmap.Encode(SKEncodedImageFormat.Png,100);
            var parts=Fixtures.Parts(body,new()
            {
                ["word/header1.xml"]=Fixtures.Xml("hdr",P("Première")),
                ["word/header2.xml"]=Fixtures.Xml("hdr",headerOverride??Paragraph(Run("Gauche")+tab+
                    """<w:hyperlink r:id="web"><w:r><w:t>Droite</w:t></w:r></w:hyperlink>""",
                    """<w:tabs><w:tab w:val="right" w:pos="2100"/></w:tabs>""")),
                ["word/header3.xml"]=Fixtures.Xml("hdr",table),
                ["word/footer1.xml"]=Fixtures.Xml("ftr",Paragraph(inline+Run(" Valeur ")+field)),
                ["word/settings.xml"]=Fixtures.Xml("settings",oddEven?"<w:evenAndOddHeaders/>":"")
            },links,new(){["word/media/picture.png"]=encoded.ToArray()});
            parts["word/_rels/footer1.xml.rels"]=Encoding.UTF8.GetBytes(Fixtures.Relations(Fixtures.Relationship("pic","image","media/picture.png")));
            parts["word/_rels/header2.xml.rels"]=Encoding.UTF8.GetBytes(Fixtures.Relations(Fixtures.Relationship("web","hyperlink","https://example.invalid","External")));
            return Fixtures.Write(root,name+".docx",parts);
        }
    }
}
