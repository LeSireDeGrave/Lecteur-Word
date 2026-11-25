using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class PageViewCases
{
    static void Check(bool valid,string message)=>NativeCases.Check(valid,message);

    static string Section(int width,int height,string orientation="portrait",bool stories=false)=>$"""
        <w:sectPr>
          {(stories?"<w:headerReference w:type=\"default\" r:id=\"hdr\"/><w:footerReference w:type=\"default\" r:id=\"ftr\"/>":"")}
          <w:type w:val="nextPage"/><w:pgSz w:w="{width}" w:h="{height}" w:orient="{orientation}"/>
          <w:pgMar w:top="1080" w:bottom="1080" w:left="850" w:right="850" w:header="300" w:footer="300"/>
        </w:sectPr>
        """;

    static string Heading(int page,string properties="")=>$"""
        <w:p><w:pPr><w:outlineLvl w:val="0"/>{properties}</w:pPr>
          <w:bookmarkStart w:id="{page}" w:name="chapitre{page}"/>
          <w:r><w:rPr><w:b/><w:sz w:val="36"/></w:rPr><w:t>Chapitre {page:00}</w:t></w:r>
          <w:bookmarkEnd w:id="{page}"/>
        </w:p>
        """;

    static string Document(string root,int pages,bool mixed=false,bool a5=false)
    {
        var body=new System.Text.StringBuilder();
        for(int n=1;n<=pages;n++)
        {
            body.Append(Heading(n,n>1&&!mixed?"<w:pageBreakBefore/>":""));
            for(int line=1;line<=7;line++)
                body.Append(Fixtures.Paragraph(n==3&&line==4?"Café sélection témoin":$"Paragraphe {line} du chapitre {n:00}. Un texte de contrôle lisible."));
            if(n==1)
                body.Append("""<w:p><w:r><w:t>Repère historique</w:t><w:lastRenderedPageBreak/><w:t> sans coupure</w:t></w:r></w:p>""");
            if(mixed&&n<pages)
                body.Append("<w:p><w:pPr>"+Format(n)+"</w:pPr></w:p>");
        }
        body.Append(Format(mixed?pages:1));
        var parts=Fixtures.Parts(body.ToString(),new(){
            ["word/header1.xml"]=Fixtures.Xml("hdr",Fixtures.Paragraph("En-tête témoin")),
            ["word/footer1.xml"]=Fixtures.Xml("ftr",Fixtures.Paragraph("Pied de page témoin"))
        },Fixtures.Relationship("hdr","header","header1.xml")+Fixtures.Relationship("ftr","footer","footer1.xml"));
        return Fixtures.Write(root,$"pages-{pages}{(mixed?"-formats":a5?"-a5":"")}.docx",parts);

        string Format(int number)=>number switch
        {
            2=>Section(8391,11906,stories:true),
            3=>Section(12240,15840,stories:true),
            4=>Section(16838,11906,"landscape",stories:true),
            _=>a5?Section(8391,11906,stories:true):Section(11906,16838,stories:true)
        };
    }

    internal static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow{Width=1480,Height=820};
        window.Show();
        try
        {
            foreach(int count in new[]{1,2,3,20})
            {
                Open(Document(root,count));
                NativeSnapshot(window,count);
                if(count==1)
                {
                    Check(window.CurrentReadingView==ReadingView.SinglePage,"La pagination complète ouvre automatiquement la page simple.");
                    Check(window.CurrentComposition!.Issues.Any(i=>i.Code=="LAYOUT_CACHED_BREAK_IGNORED"),"Saut historique absent du diagnostic.");
                    var action=Control<Button>(window,"ViewFacingPages");
                    Check(AutomationProperties.GetName(action)=="Double page"&&action.Focus(),"Nom accessible ou focus clavier absent.");
                    window.KeyPressQwerty(PhysicalKey.Enter,RawInputModifiers.None);
                    window.KeyReleaseQwerty(PhysicalKey.Enter,RawInputModifiers.None);
                    Layout();
                    Check(window.CurrentReadingView==ReadingView.FacingPages,"Activation clavier de la double page.");
                    window.SetLanguage(true);
                    Check(AutomationProperties.GetName(action)=="Facing pages"&&Equals(ToolTip.GetTip(action),"Facing pages"),
                        "Infobulle ou nom anglais absent.");
                    window.SetLanguage(false);

                }
                Click(window,"ViewFacingPages");
                Layout();
                CheckGeometry(window,count,facing:true);
                if(count==2)
                    Check(window.DisplayedPageBoxes[1].Left<window.DisplayedPageBoxes[0].Left&&window.DisplayedPageBoxes[1].Top>window.DisplayedPageBoxes[0].Bottom,
                        "Dernière paire incomplète. Le papier fictif est interdit.");
                Check(window.RealizedPageCount<=3,"Chaque page hors écran a été dessinée.");
            }
            string mixed=Document(root,4,mixed:true);
            Open(mixed);
            var snapshot=NativeSnapshot(window,4);
            Check(window.CurrentReadingView==ReadingView.SinglePage,"Chaque ouverture doit démarrer en Page simple.");
            Check(snapshot.Pages.Select(p=>p.Origin.Orientation).Last()=="landscape"&&
                snapshot.Pages.Select(p=>p.Geometry.Geometry.Width).Distinct().Count()==4,"Formats A4, A5, Letter et paysage.");
            Check(snapshot.Pages.All(p=>p.Geometry.RunningStories.Count==2&&p.Origin.Source is not null),"Stories ou provenance absentes.");
            var heading=window.CurrentReading!.Outline.Single(e=>e.Label=="Chapitre 03");
            Check(window.NavigateToBlock(heading.BlockId)&&window.CurrentPage==3,"Sommaire vers la page droite.");
            Layout();
            Click(window,"ViewContinuous");Layout();
            Check(window.IsSimplifiedReading&&window.CurrentPage==0&&window.CurrentBlockId==heading.BlockId,"Ancre perdue dans le flux.");
            Click(window,"ViewSinglePage");Layout();
            CheckGeometry(window,4,facing:false);
            Check(window.CurrentPage==3,"Le retour aux pages perd le chapitre courant.");
            Click(window,"ViewFacingPages");Layout();
            window.Width=800;
            foreach(double zoom in new[]{.8,1,1.5})
            {
                window.SetZoom(zoom);Layout();
                Check(window.CurrentPage==3&&ReferenceEquals(snapshot,window.ReadingTableSnapshot),"Zoom ou largeur de fenêtre change l’instantané.");
                CheckGeometry(window,4,facing:true);
            }
            var viewport=Control<ScrollViewer>(window,"PageViewport");
            Check(viewport.Extent.Width>viewport.Viewport.Width&&viewport.Offset.X>0,"Page droite de la paire inaccessible.");
            viewport.Offset=new Vector(0,viewport.Offset.Y);Layout();
            Check(window.GetVisualDescendants().OfType<Image>().Any(i=>i.Name=="CalculatedPage"&&i.Tag is 2),"Page gauche inaccessible au scroll horizontal.");
            window.Width=1680;window.ToggleNavigation();window.SetZoom(.8);Layout();
            window.GoToPage(2);
            var line=snapshot.Pages[2].Geometry.Lines.Single(l=>l.Text.Contains("Café sélection",StringComparison.Ordinal));
            var cluster=PageCoordinates.Clusters(line).First(c=>c.Rect.Width>0&&c.Rect.Height>0);
            var sheet=window.GetVisualDescendants().OfType<Image>().Single(i=>i.Name=="CalculatedPage"&&i.Tag is 3);
            double scale=window.Zoom*96/72;
            var pointer=sheet.TranslatePoint(new Point((cluster.Rect.Left+cluster.Rect.Width/2)*scale,
                (cluster.Rect.Top+cluster.Rect.Height/2)*scale),window);
            Check(pointer is not null,"Transformation de la feuille droite absente.");
            window.MouseDown(pointer!.Value,MouseButton.Left);
            window.MouseUp(pointer.Value,MouseButton.Left);
            Layout();
            Check(window.CurrentPage==3&&window.SelectedNativeSources.SequenceEqual(cluster.Sources),
                "Coordonnées HarfBuzz ou sélection décalées par la gouttière.");
            Click(window,"Action_search_",prefix:true);
            Control<TextBox>(window,"SearchBox").Text="Café sélection";
            Layout();
            var result=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content is string label&&label.StartsWith("Café sélection"));
            result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
            Check(window.CurrentPage==3&&window.SelectedNativeSources.Count>0,"Recherche positionnée sur la mauvaise feuille.");
            Check(window.CachedPagePixels<=16_000_000,"Cache des rasters hors budget.");

            const string words="extraordinaire extraordinaire extraordinaire";
            string hyphenated=Fixtures.Write(root,"recherche-cesure.docx",Fixtures.Parts(
                "<w:p><w:r><w:rPr><w:rFonts w:ascii=\"DejaVu Serif\" w:hAnsi=\"DejaVu Serif\"/><w:sz w:val=\"24\"/>"+
                $"<w:lang w:val=\"fr-FR\"/></w:rPr><w:t>{words}</w:t></w:r></w:p>"+Section(3900,11906),
                new(){["word/settings.xml"]=Fixtures.Xml("settings","<w:autoHyphenation/><w:hyphenationZone w:val=\"0\"/>")},
                Fixtures.Relationship("settings","settings","settings.xml")));
            Open(hyphenated);
            var hyphenPage=NativeSnapshot(window,1).Composition.Pages.Single();
            Check(hyphenPage.Lines.Any(l=>l.Native?.Cut==LineCutKind.AutomaticHyphen)&&
                string.Concat(hyphenPage.Lines.Select(l=>l.Text))==words,"Le trait généré est distinct du texte rendu.");
            Click(window,"Action_search_",prefix:true);
            Control<TextBox>(window,"SearchBox").Text=words;
            Layout();
            var completeWord=window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,words[..32]+"…"));
            completeWord.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
            var selected=window.SelectedNativeSources.Select(s=>s.Span!.Value).OrderBy(s=>s.Start);
            Check(string.Concat(selected.Select(s=>words[s.Start..s.EndExclusive])).Replace(" ","",StringComparison.Ordinal)==
                words.Replace(" ","",StringComparison.Ordinal),
                "La recherche de la phrase traverse le trait généré et sélectionne uniquement la source.");
            Click(window,"ViewContinuous");Layout();
            var passage=window.GetVisualDescendants().OfType<TextBlock>().Single(b=>b.ContextMenu?.Items.OfType<MenuItem>()
                .Any(item=>Equals(item.Header,"Copier le texte du passage"))==true);
            var copied=passage.ContextMenu!.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Copier le texte du passage"));
            copied.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Layout();
            Check(window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult()==words,"La copie conserve la phrase d’origine.");
            Click(window,"ViewFacingPages");Layout();

            string partial=Fixtures.Write(root,"pages-refusees.docx",Fixtures.Parts(Heading(1)+Fixtures.Paragraph("Source présente")+
                """<w:p><w:r><w:footnoteReference w:id="2"/></w:r></w:p>"""+Section(11906,16838),
                new(){["word/footnotes.xml"]=Fixtures.Xml("footnotes","""<w:footnote w:id="2"><w:p><w:r><w:t>Note conservée</w:t></w:r></w:p></w:footnote>""")},
                Fixtures.Relationship("note","footnotes","footnotes.xml")));
            Open(partial);
            Check(window.CurrentReadingView==ReadingView.Unavailable&&!window.IsSimplifiedReading&&window.CurrentPage==0&&
                window.DisplayedPageBoxes.Count==0&&window.CachedPagePixels==0,
                "La composition partielle doit signaler l’indisponibilité sans feuille ni lecture implicite.");
            Check(!Control<Button>(window,"ViewSinglePage").IsEnabled&&!Control<Button>(window,"ViewFacingPages").IsEnabled&&
                window.CurrentReading!.Presentation.Notes.Values.Single().Verified,"Garde du mode natif ou note perdue.");
            var headingOfPartial=window.CurrentReading.Outline.Single().BlockId;
            Check(!window.NavigateToBlock(headingOfPartial)&&!window.IsSimplifiedReading,"Une navigation ne choisit pas la lecture continue.");
            Click(window,"OpenPaginationDiagnostics");Layout();
            Check(Control<TextBlock>(window,"DiagnosticUserSummary").IsVisible&&!window.IsSimplifiedReading,"Accès aux diagnostics.");
            Click(window,"OpenContinuousReading");Layout();
            Check(window.IsSimplifiedReading&&window.NavigateToBlock(headingOfPartial),"Consultation continue choisie et navigable.");
            Check(!window.ExportAvailable(ExportFormat.Pdf)&&window.ExportAvailable(ExportFormat.Odt)&&
                window.ExportAvailable(ExportFormat.Epub3)&&window.ExportAvailable(ExportFormat.Nova),
                "La pagination incomplète doit bloquer le PDF et laisser les représentations structurées vérifiées accessibles.");
            Open(mixed);
            Check(window.CurrentReadingView==ReadingView.SinglePage&&window.CurrentPage==1&&
                window.SelectedNativeSources.Count==0&&window.CurrentBlockId is null,"Nouvelle génération ou préférence incorrecte.");
            var obsolete=window.OpenDocumentAsync(Document(root,21));
            var latest=window.OpenDocumentAsync(mixed);
            AboutCases.Complete(Task.WhenAll(obsolete,latest));
            Layout();
            Check(NativeSnapshot(window,4).SnapshotId==snapshot.SnapshotId&&window.CurrentPage==1&&
                window.DisplayedPageBoxes.Count==4,"Une opération annulée a publié ses pages après l’ouverture suivante.");

            string malformed=Path.Combine(root,"fichier-invalide.docx");
            File.WriteAllText(malformed,"Fichier hors format");
            Open(malformed);
            Check(window.CurrentReadingView==ReadingView.Unavailable&&window.CurrentPage==0&&
                !Control<Button>(window,"OpenContinuousReading").IsVisible&&
                Control<TextBlock>(window,"ReadingAvailability").Text!.Contains("DOCX valide",StringComparison.Ordinal),
                "Fichier illisible signalé, sans vue de lecture activable.");

        }
        finally{window.Close();}

        void Open(string path)
        {
            AboutCases.Complete(window.OpenDocumentAsync(path));Layout();
        }
        void Layout(){window.UpdateLayout();Dispatcher.UIThread.RunJobs();}
    }

    static PageCompositionSnapshot NativeSnapshot(ReaderWindow window,int count)
    {
        var snapshot=window.ReadingTableSnapshot;
        Check(snapshot is {Coverage:PageCoverage.Complete,Composition.Available:true}&&snapshot.Pages.Length==count&&
            ReferenceEquals(snapshot.Composition,window.CurrentComposition),
            $"Instantané natif complet attendu ({count}). "+string.Join(" / ",snapshot?.Composition.Issues
                .Where(i=>i.BlocksRendering).Select(i=>i.Code)??[]));
        return snapshot!;
    }

    static void CheckGeometry(ReaderWindow window,int count,bool facing)
    {
        var pages=NativeSnapshot(window,count).Pages;
        var boxes=window.DisplayedPageBoxes;
        Check(boxes.Count==count,"Nombre de feuilles différent de l’instantané.");
        for(int i=0;i<count;i++)
        {
            double scale=window.Zoom*96/72;
            Check(Math.Abs(boxes[i].Width-pages[i].Geometry.Geometry.Width*scale)<.01&&
                Math.Abs(boxes[i].Height-pages[i].Geometry.Geometry.Height*scale)<.01,"Dimensions physiques altérées.");
            if(i==0)continue;
            if(facing&&i%2==0)
                Check(boxes[i].Y==boxes[i-1].Y&&Math.Abs(boxes[i].X-boxes[i-1].Right-24)<.01,"Mauvais alignement ou mauvaise gouttière.");
            else
                Check(boxes[i].Top>boxes[i-1].Bottom,"Les frontières des feuilles ne sont pas séparées.");
        }
    }

    static T Control<T>(ReaderWindow window,string name) where T:Control=>
        window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);

    static void Click(ReaderWindow window,string name,bool prefix=false)=>
        window.GetVisualDescendants().OfType<Button>().Single(b=>prefix?b.Name?.StartsWith(name,StringComparison.Ordinal)==true:b.Name==name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static async Task Native(ReaderWindow window,string root)
    {
        string output=Environment.GetEnvironmentVariable("LECTEUR_WORD_PAGE_VIEW_OUTPUT")??root;
        Directory.CreateDirectory(output);
        var handle=window.TryGetPlatformHandle();
        Check(handle?.HandleDescriptor=="XID","Une fenêtre réelle X11 est requise.");
        window.SetTheme(1);
        window.Width=1470;window.Height=995;
        string longDocument=Document(root,24,a5:true);
        var memory=new List<object>();
        var steps=new List<object>();
        long start=Stopwatch.GetTimestamp();
        await window.OpenDocumentAsync(longDocument);await Layout();
        var snapshot=NativeSnapshot(window,24);
        Record("première présentation · document multipage",start);
        var heading=window.CurrentReading!.Outline.Single(e=>e.Label=="Chapitre 06");
        Check(window.NavigateToBlock(heading.BlockId)&&window.CurrentPage==6,"Titre multipage inaccessible.");
        window.GoToPage(5);await Layout();
        await Capture("page-simple-100");
        Click(window,"ViewContinuous");await Layout();
        Check(window.CurrentBlockId==heading.BlockId&&window.CurrentPage==0,"La lecture continue doit garder le chapitre.");
        await Capture("lecture-continue");
        Click(window,"ViewSinglePage");await Layout();
        Check(window.CurrentPage==6,"Retour au chapitre perdu.");
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);
            foreach(double zoom in new[]{.8,1,1.5})
            {
                window.SetZoom(zoom);
                start=Stopwatch.GetTimestamp();Click(window,"ViewFacingPages");await Layout();
                Check(window.CurrentPage==6&&ReferenceEquals(snapshot,window.ReadingTableSnapshot),"Bascule native instable.");
                CheckGeometry(window,24,facing:true);
                Record($"double page {theme} / {zoom:P0}",start);
                await Capture($"double-{theme}-{zoom*100:0}");
                if(zoom==1.5)
                {
                    var scroll=Control<ScrollViewer>(window,"PageViewport");
                    start=Stopwatch.GetTimestamp();
                    scroll.Offset=new Vector(scroll.ScrollBarMaximum.X,scroll.Offset.Y);await Layout();
                    Check(scroll.Offset.X>0&&window.GetVisualDescendants().OfType<Image>().Any(i=>i.Name=="CalculatedPage"&&i.Tag is 7),
                        "Défilement horizontal de la page droite absent.");
                    Record("défilement horizontal",start);
                    await Capture($"droite-{theme}-150");
                }
                Click(window,"ViewSinglePage");await Layout();
                Check(window.CurrentPage==6,"Retour à la page simple instable.");
            }
        }
        window.ToggleNavigation();Click(window,"ToggleElements");
        window.Width=805;await Layout();
        Check(window.CurrentPage==6,"Tiroir ou fenêtre étroite a déplacé l’ancre.");
        window.SetZoom(1);window.GoToPage(0);Click(window,"ViewFacingPages");await Layout();
        await Capture("premiere-a-droite");
        var pageViewport=Control<ScrollViewer>(window,"PageViewport");
        start=Stopwatch.GetTimestamp();
        pageViewport.Offset=new Vector(pageViewport.Offset.X,window.DisplayedPageBoxes[16].Top);
        await Layout();
        Check(window.CurrentPage is 16 or 17,"Le défilement vertical ne suit pas la paire visible.");
        Record("défilement vertical",start);
        for(int i=0;i<20;i++)
        {
            window.GoToPage(i);
            Click(window,"ViewSinglePage");await Layout(10);
            Click(window,"ViewFacingPages");await Layout(10);
            Check(window.CachedPagePixels<=16_000_000,"Cache des pages hors budget.");
            if(i%5==4)SampleMemory($"cycle {i+1}");
        }
        window.Width=1470;
        Click(window,"ToggleElements");await Layout();
        window.GoToPage(5);
        Click(window,"ViewSinglePage");await Layout();
        Click(window,"ViewFacingPages");await Layout();
        Check(window.GetVisualDescendants().OfType<Image>().Count(i=>i.Name=="CalculatedPage"&&i.Tag is 6 or 7)==2,
            "Les deux feuilles doivent être visibles pendant la mesure du cache chaud.");
        long warmPixels=window.CachedPagePixels;
        SampleMemory("paire réutilisée · début");
        for(int i=0;i<20;i++)
        {
            Click(window,"ViewSinglePage");await Layout();
            Click(window,"ViewFacingPages");await Layout();
            Check(window.CurrentPage==6&&window.CachedPagePixels==warmPixels,
                "Une paire déjà dessinée a changé d’ancre ou créé un raster supplémentaire.");
        }
        SampleMemory("paire réutilisée · fin");
        string? manuscript=Environment.GetEnvironmentVariable("LECTEUR_WORD_PAGE_VIEW_MANUSCRIPT");
        if(manuscript is not null&&Environment.GetEnvironmentVariable("LECTEUR_WORD_STORIES_004")=="1")
        {
            window.SetReadingView(ReadingView.SinglePage);
            start=Stopwatch.GetTimestamp();
            await window.OpenDocumentAsync(manuscript);await Layout();
            var a4=NativeSnapshot(window,22);
            Check(window.CurrentPage==1&&window.CurrentReadingView==ReadingView.SinglePage&&window.CurrentReading!.Outline.Count==10,
                "Le Manuscrit ouvre sa première feuille en Page simple.");
            Check(a4.Pages.Sum(p=>p.Geometry.Lines.Count)==641&&a4.Pages.All(p=>p.Geometry.RunningStories.Count==2)&&
                a4.Pages.All(p=>p.Geometry.RunningStories.Single(r=>r.Selection.Kind=="footer").Lines
                    .SelectMany(l=>l.Native!.Generated).Single(g=>g.Kind=="pageField").Fragment.Text==p.SectionPageNumber!.Value.ToString(CultureInfo.InvariantCulture)),
                "Corps, régions d’en-tête et pied, et numéros d’occurrence du Manuscrit.");
            Check(Enum.GetValues<ExportFormat>().All(f=>!window.ExportAvailable(f)),"Export du Manuscrit hors profil.");
            Record("Manuscrit A4 · première présentation native",start);
            foreach(double zoom in new[]{.8,1,1.5})
            {
                window.SetZoom(zoom);window.GoToPage(0);await Layout();
                CheckGeometry(window,22,facing:false);
                await Capture($"manuscrit-simple-{zoom*100:0}");
                Click(window,"ViewFacingPages");await Layout();
                CheckGeometry(window,22,facing:true);
                Check(window.CurrentPage==1&&window.DisplayedPageBoxes[0].Left>window.DisplayedPageBoxes[1].Left,
                    "Première feuille seule à droite.");
                await Capture($"manuscrit-double-{zoom*100:0}");
                Click(window,"ViewSinglePage");await Layout();
            }
            window.SetZoom(.8);
            Click(window,"ViewFacingPages");
            window.GoToPage(1);await Layout();
            await Capture("manuscrit-paire-2-3-80");
            Click(window,"ViewSinglePage");await Layout();
            var a4Viewport=Control<ScrollViewer>(window,"PageViewport");
            var toc=a4.Pages.SelectMany(p=>p.Geometry.Lines.Select(l=>(Page:p.Id.PhysicalIndex,Line:l)))
                .Where(x=>x.Line.Native!.Generated.Any(g=>g.Kind=="pageField")).ToArray();
            Check(toc.Length==10,"Dix entrées du sommaire placées.");
            for(int i=0;i<toc.Length;i++)
            {
                var (pageNumber,line)=toc[i];
                var number=PageCoordinates.FieldLabels(line).Single();
                string target=a4.PageLinkTargets[number.Id];
                PageRect rect=i%2==0?PageCoordinates.Clusters(line).First(cluster=>cluster.Sources.Any(s=>LinksTo(s.NodeId,target))).Rect:number.Rect;
                window.GoToPage(pageNumber-1);await Layout();
                var sheet=window.GetVisualDescendants().OfType<Image>().Single(c=>c.Name=="CalculatedPage"&&Equals(c.Tag,pageNumber));
                double scale=window.Zoom*96/72;
                var center=new Point((rect.Left+rect.Width/2)*scale,(rect.Top+rect.Height/2)*scale);
                var inViewport=sheet.TranslatePoint(center,a4Viewport);
                var point=sheet.TranslatePoint(center,window);
                Check(point is not null&&inViewport is {} visible&&new Rect(a4Viewport.Viewport).Contains(visible),
                    $"Le lien {i+1} doit être visible dans la fenêtre native.");
                if(i==0)await Capture("manuscrit-sommaire-80");
                var pointer=new Pointer(0,PointerType.Mouse,true);
                var properties=new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed);
                sheet.RaiseEvent(new PointerPressedEventArgs(sheet,pointer,window,point!.Value,
                    (ulong)Environment.TickCount64,properties,KeyModifiers.None));
                await Layout(80);
                Check(window.CurrentPage==a4.FieldBookmarks[target].Page&&window.CurrentBlockId==a4.FieldBookmarks[target].ParagraphId,
                    $"Le lien {i+1} doit atteindre sa cible physique.");
            }
            Click(window,"Action_search_",prefix:true);
            Control<TextBox>(window,"SearchBox").Text=window.CurrentReading!.Outline[0].Label;
            await Layout();
            window.GetVisualDescendants().OfType<Button>()
                .First(b=>ToolTip.GetTip(b) is string tip&&tip=="Page 3 de la composition")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout();
            Check(window.CurrentPage==3&&window.SelectedNativeSources.Count>0,"Recherche et sélection dans le chapitre.");
            await Capture("manuscrit-recherche-80");
            a4Viewport.Offset=new Vector(0,window.DisplayedPageBoxes[2].Bottom-a4Viewport.Viewport.Height+12);
            await Layout();
            await Capture("manuscrit-pied-80");
            Click(window,"ViewFacingPages");
            window.SetZoom(1.5);
            await Layout();
            a4Viewport.Offset=new Vector(a4Viewport.ScrollBarMaximum.X,window.DisplayedPageBoxes[6].Top);
            await Layout();
            Check(a4Viewport.Offset.X>0&&a4Viewport.Offset.Y>0&&window.CurrentPage is 6 or 7&&
                ReferenceEquals(a4,window.ReadingTableSnapshot),"Défilements horizontal et vertical stables.");
            await Capture("manuscrit-double-defilement-150");
            Click(window,"ViewContinuous");await Layout();
            Check(window.CurrentPage==0&&window.IsSimplifiedReading,"La lecture continue reste disponible.");

            bool LinksTo(string source,string destination)
            {
                for(string? id=source;id is not null;id=a4.Semantic!.ParentId(id))
                    if(a4.PageLinkTargets.GetValueOrDefault(id)==destination)return true;
                return false;
            }
        }
        else if(manuscript is not null)
        {
            start=Stopwatch.GetTimestamp();
            await window.OpenDocumentAsync(manuscript);await Layout();
            Check(window.CurrentReadingView==ReadingView.Unavailable,"La pagination incomplète doit présenter le diagnostic.");
            Click(window,"OpenContinuousReading");await Layout();
            Check(window.IsSimplifiedReading&&window.CurrentPage==0&&window.CurrentReading!.Outline.Count==10&&
                !Control<Button>(window,"ViewFacingPages").IsEnabled,"Le Manuscrit doit conserver dix titres et rester en flux.");
            Check(Enum.GetValues<ExportFormat>().All(f=>!window.ExportAvailable(f)),"Export du Manuscrit hors profil.");
            var reader=Control<ListBox>(window,"SimplifiedReading");
            Check(reader.GetVisualDescendants().OfType<ScrollViewer>().Single(s=>s.TemplatedParent==reader).Offset.Y==0,
                "Le premier affichage du Manuscrit reprend un défilement périmé.");
            Record("Manuscrit A4 · lecture continue",start);
            Click(window,"Action_nav_",prefix:true);await Layout();
            await Capture("manuscrit-a4-continu");
            var reading=window.CurrentReading!;
            Check(reading.Presentation.Fields.Values.Count(f=>f.Name=="PAGEREF"&&!f.HasStoredValue)==10,
                "Les dix renvois vides du Manuscrit doivent être conservés dans les diagnostics.");
            if(Environment.GetEnvironmentVariable("LECTEUR_WORD_HIDDEN_TC_REVIEW")=="1")
            {
                var tocEntries=reading.Presentation.BodyBlocks.Where(b=>ReadablePresentation.Walk([b])
                    .Any(link=>link.Kind=="link"&&link.LinkTarget?.StartsWith('#')==true)).ToArray();
                Check(tocEntries.Length==10,"Les dix titres et leurs liens sont présents dans le sommaire.");
                foreach(double zoom in new[]{1d,1.5d})
                {
                    window.SetZoom(zoom);
                    for(int i=0;i<tocEntries.Length;i++)
                    {
                        var item=tocEntries[i];
                        Check(window.NavigateToBlock(item.Id),"Entrée du sommaire inaccessible.");
                        await Layout();
                        var element=reader.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Tag as string==item.Id);
                        var action=element.ContextMenu!.Items.OfType<MenuItem>().Single(m=>
                            m.Header?.ToString()?.StartsWith("Suivre le lien",StringComparison.Ordinal)==true);
                        var visible=reading.Presentation.TextFor(item);
                        Check(visible.Contains(reading.Outline[i].Label,StringComparison.Ordinal)&&
                            !visible.Contains("PAGEREF",StringComparison.Ordinal),"Le titre du sommaire doit rester seul en lecture normale.");
                        if(i==0)await Capture($"manuscrit-sommaire-{zoom*100:0}");
                        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        Check(window.CurrentPage==0&&window.CurrentBlockId==reading.Outline[i].BlockId,
                            "L’entrée doit atteindre son chapitre en lecture continue.");
                        await Layout();
                        if(i==0)await Capture($"manuscrit-chapitre-{zoom*100:0}");
                    }
                    Record($"Dix liens du sommaire · {zoom:P0}",start);
                }
            }
            var first=reading.Outline.First();
            var entry=reading.Presentation.BodyBlocks.First(b=>ReadablePresentation.Walk([b])
                .Any(link=>link.Kind=="link"&&link.LinkTarget?.StartsWith('#')==true));
            Check(window.NavigateToBlock(entry.Id),"Entrée du sommaire inaccessible.");
            await Layout();
            var text=window.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Tag as string==entry.Id);
            var link=text.ContextMenu!.Items.OfType<MenuItem>().Single(i=>i.Header?.ToString()?.StartsWith("Suivre le lien")==true);
            link.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(window.CurrentBlockId==first.BlockId,"Le lien du sommaire n’atteint pas le premier chapitre du Manuscrit.");
            await Layout();await Capture("manuscrit-a4-chapitre");
        }
        if(Environment.GetEnvironmentVariable("LECTEUR_WORD_HISTORICAL_LETTER") is {Length:>0} letter)
        {
            start=Stopwatch.GetTimestamp();
            await window.OpenDocumentAsync(letter);await Layout();
            Check(window.ShowsHistoricalPage&&window.CurrentPage==1&&window.NativePageStatus=="Fallback"&&
                Control<Button>(window,"ViewSinglePage").IsEnabled&&Control<Button>(window,"ViewFacingPages").IsEnabled,
                "Une page historique doit être restaurée à l’ouverture, avec son propre profil.");
            Record("Lettre · profil historique",start);
            Click(window,"ViewFacingPages");await Layout();
            Check(window.CurrentReadingView==ReadingView.FacingPages&&window.CurrentPage==1&&window.DisplayedPageBoxes.Count==1,
                "La Double page reprend la feuille historique et le côté gauche reste vide.");
            Click(window,"ViewSinglePage");await Layout();
            foreach(float zoom in new[]{.8f,1f,1.5f})
            {
                window.SetZoom(zoom);await Layout();
                Check(window.DisplayedPageBoxes.Count==1&&window.CurrentPage==1,"Zoom instable de la lettre.");
                await Capture($"lettre-historique-{zoom*100:0}");
            }
            File.WriteAllBytes(Path.Combine(output,"lettre-papier-150.png"),
                Composer.PagePng(window.CurrentComposition!.Pages[0],1.5f));
            window.SetReadingView(ReadingView.Continuous);await Layout();
            Check(window.CurrentPage==0&&window.IsSimplifiedReading,"Flux de la lettre inaccessible.");
            window.SetReadingView(ReadingView.SinglePage);await Layout();
            Check(window.CurrentPage==1&&window.ShowsHistoricalPage,"Retour à la page historique.");
        }
        File.WriteAllText(Path.Combine(output,"recette.json"),JsonSerializer.Serialize(new{
            platform=handle!.HandleDescriptor,window.RenderScaling,steps,memory},new JsonSerializerOptions{WriteIndented=true}));

        async Task Layout(int delay=0)
        {
            window.UpdateLayout();
            var frame=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.RequestAnimationFrame(_=>window.RequestAnimationFrame(_=>frame.TrySetResult()));
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(delay>0)await Task.Delay(delay);
        }
        void Record(string label,long from)
        {
            steps.Add(new{label,ms=Stopwatch.GetElapsedTime(from).TotalMilliseconds,window.CurrentPage,
                view=window.CurrentReadingView.ToString(),window.RealizedPageCount,window.CachedPagePixels});
            SampleMemory(label);
        }
        void SampleMemory(string label)
        {
            string? row=File.Exists("/proc/self/smaps_rollup")?File.ReadLines("/proc/self/smaps_rollup")
                .FirstOrDefault(l=>l.StartsWith("Pss:",StringComparison.Ordinal)):null;
            long? pssKb=row is null?null:long.Parse(row.Split(' ',StringSplitOptions.RemoveEmptyEntries)[1],CultureInfo.InvariantCulture);
            memory.Add(new{label,pssKb,window.RealizedPageCount,window.CachedPagePixels});
        }
        async Task Capture(string label)
        {
            await Task.Delay(80);
            var args=new ProcessStartInfo("import"){UseShellExecute=false};
            args.ArgumentList.Add("-window");
            args.ArgumentList.Add(handle!.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
            string path=Path.Combine(output,label+".png");args.ArgumentList.Add(path);
            using var process=Process.Start(args)!;
            await process.WaitForExitAsync();
            Check(process.ExitCode==0&&new FileInfo(path).Length>10_000,"Capture native vide.");
        }
    }
}
