using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class OpusUxCases
{
    const string W="{http://schemas.openxmlformats.org/wordprocessingml/2006/main}";
    static void Assert(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    static void Setup()=>AppBuilder.Configure<ReaderApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
    static void Layout()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
    static T Control<T>(ReaderWindow w,string name) where T:Control =>
        w.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
    static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();}
    static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    static void Open(ReaderWindow win,string file){AboutCases.Complete(win.OpenDocumentAsync(file));Layout();}
    static void Capture(ReaderWindow window,string path,double scale)
    {
        Layout();
        using var shot=new RenderTargetBitmap(new PixelSize((int)Math.Round(window.ClientSize.Width*scale),
            (int)Math.Round(window.ClientSize.Height*scale)),new Vector(96*scale,96*scale));
        shot.Render(window);shot.Save(path,PngBitmapEncoderOptions.Default);
    }

    static string Paginated(string root)
    {
        string Section(int width)=>$"<w:sectPr><w:type w:val=\"nextPage\"/><w:pgSz w:w=\"{width}\" w:h=\"9600\"/><w:pgMar w:left=\"800\" w:right=\"900\" w:top=\"680\" w:bottom=\"700\"/></w:sectPr>";
        string first="<w:p><w:pPr><w:ind w:left=\"200\" w:right=\"120\" w:firstLine=\"100\"/><w:tabs><w:tab w:val=\"left\" w:pos=\"1900\"/></w:tabs>"+
            Section(11000)+"</w:pPr><w:r><w:t>Alpha bêta&#160;gamma</w:t><w:tab/><w:t>Tabulation</w:t><w:br/><w:t>Retour forcé</w:t></w:r></w:p>";
        string table="<w:tbl><w:tblPr><w:tblW w:type=\"dxa\" w:w=\"1900\"/><w:tblLayout w:type=\"fixed\"/></w:tblPr>"+
            "<w:tblGrid><w:gridCol w:w=\"1900\"/></w:tblGrid><w:tr><w:tc><w:tcPr><w:tcW w:type=\"dxa\" w:w=\"1900\"/></w:tcPr>"+
            Fixtures.Paragraph("Cellule placée")+"</w:tc></w:tr></w:tbl>";
        string second="<w:p><w:r><w:t>Saut de page</w:t><w:br w:type=\"page\"/><w:t>Après le saut</w:t></w:r></w:p>"+table+Section(12400);
        return Fixtures.Write(root,"opus-regle.docx",Fixtures.Parts(first+second));
    }

    static string Specialized(string root)
    {
        string area="<w:p><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr><w:bookmarkStart w:id=\"8\" w:name=\"abracadabra\"/><w:r><w:t>Repère principal</w:t></w:r><w:bookmarkEnd w:id=\"8\"/></w:p>"+
            "<w:p><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr><w:r><w:t>Sous-titre</w:t></w:r></w:p>"+
            "<w:p><w:r><w:t xml:space=\"preserve\">Alpha&#160;bêta </w:t><w:tab/><w:t>fin</w:t><w:br/><w:t>saut</w:t><w:br w:type=\"page\"/><w:t>texte</w:t><w:footnoteReference w:id=\"4\"/><w:commentReference w:id=\"7\"/></w:r></w:p>"+
            "<w:tbl><w:tblGrid><w:gridCol w:w=\"1900\"/></w:tblGrid><w:tr><w:tc>"+Fixtures.Paragraph("Cellule témoin")+"</w:tc></w:tr></w:tbl>"+
            "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:x=\"urn:unavailable\"><mc:Choice Requires=\"x\">"+
            "<w:tbl><w:tr><w:tc>"+Fixtures.Paragraph("Branche inactive")+"</w:tc></w:tr></w:tbl></mc:Choice><mc:Fallback>"+Fixtures.Paragraph("Branche active")+"</mc:Fallback></mc:AlternateContent>"+
            "<w:p><w:r><w:drawing><a:blip xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" r:embed=\"inconnu\"/></w:drawing></w:r></w:p>"+
            "<w:p><w:r><w:pict><v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\" id=\"figure1\"/></w:pict></w:r></w:p>"+
            "<w:p><w:r><w:drawing><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"inconnu\"/></w:drawing></w:r></w:p>"+
            "<w:p><m:oMath xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"><m:r><m:t>a</m:t></m:r></m:oMath></w:p>"+
            "<w:p><w:r><w:pict><w:txbxContent>"+Fixtures.Paragraph("Zone de texte")+"</w:txbxContent></w:pict></w:r></w:p>"+
            "<w:p><w:fldSimple w:instr=\"TOC\"><w:r><w:t>Valeur conservée</w:t></w:r></w:fldSimple>"+
            "<w:ins w:id=\"9\"><w:r><w:t>Insertion</w:t></w:r></w:ins><w:r><w:object/></w:r></w:p>"+
            Fixtures.Paragraph(char.ConvertFromUtf32(0x10fffd))+
            "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"hdr\"/><w:footerReference w:type=\"default\" r:id=\"ftr\"/></w:sectPr>";
        return Fixtures.Write(root,"opus-inventaire.docx",Fixtures.Parts(area,new()
        {
            ["word/footnotes.xml"]=Fixtures.Xml("footnotes","<w:footnote w:id=\"4\">"+Fixtures.Paragraph("Note vérifiée")+"</w:footnote>"),
            ["word/header1.xml"]=Fixtures.Xml("hdr",Fixtures.Paragraph("En-tête vérifié")),
            ["word/footer1.xml"]=Fixtures.Xml("ftr",Fixtures.Paragraph("Pied vérifié")),
            ["word/styles.xml"]=Fixtures.Xml("styles","<w:docDefaults><w:rPrDefault><w:rPr><w:lang w:val=\"fr-FR\"/></w:rPr></w:rPrDefault></w:docDefaults>")
        },Fixtures.Relationship("fn","footnotes","footnotes.xml")+Fixtures.Relationship("hdr","header","header1.xml")+
          Fixtures.Relationship("ftr","footer","footer1.xml")+Fixtures.Relationship("st","styles","styles.xml")));
    }

    public static void Gui(string root)
    {
        Setup();
        var window=new ReaderWindow{Width=1280,Height=850};
        window.Show();
        try
        {
            string paginated=Paginated(root),specialized=Specialized(root);
            string firstHash=Hash(paginated),secondHash=Hash(specialized);
            Open(window,paginated);
            var language=Control<Button>(window,"LanguageToggle");
            Assert(language.Content is Viewbox&&Equals(ToolTip.GetTip(language),"Passer en anglais"),"L’icône planète ou son infobulle manque.");
            Click(language);
            Assert(window.Language=="en"&&Equals(ToolTip.GetTip(language),"Switch to French"),"Le changement de langue ne met pas à jour l’infobulle.");
            Click(language);
            var image=Control<Image>(window,"CalculatedPage");
            Assert(window.CurrentComposition is {Available:true,Pages.Count:3},"Fixture pages hors profil.");
            Assert(window.CurrentComposition!.Pages[1].Lines.Any(l=>l.HasExplicitPageBreak),"Provenance du saut de page absente.");
            Assert(!Control<Border>(window,"DocumentElementsPanel").IsVisible,"Le panneau droit doit être fermé à l’ouverture.");
            Key(PhysicalKey.F7);
            var label=Control<TextBlock>(window,"RulerGeometry");
            Assert(window.RulerVisible&&label.Text!.Contains("Section 1")&&label.Text.Contains("1 tabulations"),"Retraits, tabulation ou section non affichés.");
            Control<ComboBox>(window,"RulerUnits").SelectedIndex=1;Layout();
            Assert(label.Text!.Contains("mm"),"Unité mm non actualisée.");
            window.GoToPage(1);Layout();
            Assert(label.Text!.Contains("Section 2")&&window.CurrentComposition.Pages[0].Geometry.Width!=window.CurrentComposition.Pages[1].Geometry.Width,
                "La règle doit suivre le changement de section et de géométrie.");
            window.GoToPage(0);
            Key(PhysicalKey.F8);
            Control<ComboBox>(window,"ElementCategory").SelectedIndex=(int)DocumentCategory.Section+1;
            Layout();
            var pagedElements=Control<ListBox>(window,"DocumentElements");
            pagedElements.SelectedItem=((IEnumerable<DocumentElementRow>)pagedElements.ItemsSource!).Single(e=>e.Section==2);
            Click(Control<Button>(window,"LocateElement"));
            Assert(window.CurrentPage==2,"La localisation de section n’ouvre pas sa première page calculée.");
            Control<ComboBox>(window,"ElementCategory").SelectedIndex=(int)DocumentCategory.Cell+1;Layout();
            var placedCell=((IEnumerable<DocumentElementRow>)pagedElements.ItemsSource!).Single();
            Assert(placedCell is {Page:3,Cell:{Row:1,Column:1}},"La cellule placée n’a pas sa géométrie réelle.");
            Click(Control<Button>(window,"LocateElement"));
            Assert(window.CurrentPage==3&&window.CurrentBlockId==placedCell.Source.NodeId,"Localisation de la cellule paginée incorrecte.");
            Click(Control<Button>(window,"ToggleElements"));
            window.GoToPage(0);Layout();
            var raster=image.Source;
            Click(Control<Button>(window,"ToggleNonprinting"));
            Assert(Control<Control>(window,"PageNonprintingMarks").IsVisible&&ReferenceEquals(raster,image.Source),"Les marques doivent être superposées au raster.");
            Assert(window.PageMarkSources.Count()>=6&&window.PageMarkSources.Any(s=>s.Span is not null)&&
                window.PageMarkSources.Any(s=>window.CurrentDocumentIndex!.Breaks.ContainsKey(s.NodeId)),
                "Le raster superposé a perdu les identités de caractères ou de saut.");
            window.SetTheme(1);
            Capture(window,Path.Combine(root,"pages-clair-100.png"),1);

            Open(window,specialized);
            Assert(window.IsSimplifiedReading&&window.CurrentPage==0&&!window.RulerVisible,"Mesure physique ou pagination indue en flux simplifié.");
            Assert(Control<TextBlock>(window,"ReadingBanner").Text!.Contains("règle indisponible")&&
                Control<TextBlock>(window,"FooterDocument").Text==Path.GetFileName(specialized),"L’état de la règle ou le document courant manque.");
            var index=window.CurrentDocumentIndex!;
            var categories=index.Rows.Select(r=>r.Category).ToHashSet();
            Assert(Enum.GetValues<DocumentCategory>().All(categories.Contains),"Catégories absentes : "+string.Join(", ",Enum.GetValues<DocumentCategory>().Except(categories)));
            Assert(index.Rows.Count(r=>r.Category==DocumentCategory.Table)==1,"Branche inactive affichée ou tableau actif perdu.");
            Assert(index.Language=="fr-FR"&&index.Words>0&&index.Characters>index.Words,"Statistiques ou langue non renseignées.");
            Assert(index.Rows.Any(r=>r.Target==DocumentTarget.None)&&index.Rows.Any(r=>r.Target==DocumentTarget.Running),"États de localisation indiscernables.");
            Assert(index.Rows.All(r=>r.Page is null),"Page inventée pour un document sans pagination.");
            const string content="Alpha\u00a0bêta \tfin\nsaut";
            var reading=window.CurrentReading!;
            var passage=ReadablePresentation.Walk(reading.Presentation.BodyBlocks).First(b=>b.Kind=="paragraph"&&reading.Presentation.TextFor(b).Contains(content,StringComparison.Ordinal));
            Assert(window.NavigateToBlock(passage.Id),"Passage introuvable.");Layout();
            window.SetZoom(1.2);Layout();
            Assert(window.CurrentPage==0&&Math.Abs(window.Zoom-1.2)<.01,"Zoom du flux ou état de pagination incorrect.");
            window.SetZoom(1);Layout();
            var find=window.GetVisualDescendants().OfType<Button>().First(b=>b.Name?.StartsWith("Action_search_")==true);
            Click(find);var query=Control<TextBox>(window,"SearchBox");query.Text="Alpha";Layout();
            string Hits()=>ReadingUxCases.VisibleText(window).Split('\n').FirstOrDefault(s=>s.Contains("Résultats affichés"))??"";
            string hits=Hits();
            Assert(hits.EndsWith("1"),"Recherche du passage non démontrée.");
            CheckCopy();
            Click(Control<Button>(window,"ToggleNonprinting"));
            Assert(!window.GetVisualDescendants().OfType<Control>().Any(c=>c.Name=="NonprintingMark"),"Marques encore affichées après masquage.");
            Assert(Hits()==hits,"Résultats de recherche modifiés par les marques.");
            CheckCopy();
            Click(Control<Button>(window,"ToggleNonprinting"));Layout();
            var marks=window.GetVisualDescendants().OfType<ReaderWindow.ReadingMark>().ToArray();
            Assert(new[]{"·","°","→","↵","↡","¶"}.All(k=>marks.Any(m=>m.Tag as string==k)),"Marques du flux absentes ou incomplètes.");
            Assert(marks.All(m=>m.Source.SnapshotId==secondHash&&index.Semantic!.Source(m.Source.NodeId,m.Source.Span)==m.Source)&&
                marks.Where(m=>m.Tag as string is "·" or "°").All(m=>m.Source.Span is {Start:>=0}),"Une marque a perdu sa provenance UTF-16.");
            Assert(ReferenceEquals(reading,window.CurrentReading)&&ReferenceEquals(index,window.CurrentDocumentIndex),"Bascule ayant reconstruit un modèle.");
            var outline=window.GetVisualDescendants().OfType<Button>().First(b=>b.Content as string=="Sommaire");
            Click(outline);
            var branch=Control<Button>(window,"OutlineBranch");
            Assert(branch.Focus(),"Branche du sommaire inaccessible au clavier.");
            window.KeyPressQwerty(PhysicalKey.Enter,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Enter,RawInputModifiers.None);Layout();
            Assert(branch.Content as string=="▸","Repli clavier du sommaire non déclenché.");
            Click(branch);

            Click(Control<Button>(window,"ToggleElements"));
            var filter=Control<ComboBox>(window,"ElementCategory");
            var filterText=Control<TextBox>(window,"ElementSearch");
            var list=Control<ListBox>(window,"DocumentElements");
            var splitter=Control<GridSplitter>(window,"ElementsSplitter");
            var drawer=Control<Border>(window,"DocumentElementsPanel");
            double drawerWidth=drawer.Bounds.Width;
            Assert(splitter.Focus(),"Le séparateur droit n’est pas accessible au clavier.");
            Key(PhysicalKey.ArrowLeft);
            Assert(Math.Abs(drawer.Bounds.Width-drawerWidth)>=1,"Le séparateur droit ne redimensionne pas le tiroir.");
            filter.SelectedIndex=(int)DocumentCategory.Table+1;Layout();
            Assert(((IEnumerable<DocumentElementRow>)list.ItemsSource!).Single().Type=="tbl","Filtre de tableaux incorrect.");
            filterText.Text="inexistant";Layout();
            Assert(!((IEnumerable<DocumentElementRow>)list.ItemsSource!).Any(),"Filtre recherche ignoré.");
            filterText.Text="";Layout();
            var selected=(DocumentElementRow)list.SelectedItem!;
            Click(Control<Button>(window,"LocateElement"));
            Assert(window.CurrentBlockId==selected.Anchor,"Localisation du tableau simplifié non ancrée.");
            filter.SelectedIndex=(int)DocumentCategory.Cell+1;filterText.Text="Cellule témoin";Layout();
            var realCell=((IEnumerable<DocumentElementRow>)list.ItemsSource!).Single();
            Assert(realCell is {Page:null,Target:DocumentTarget.Cell,Cell:{Row:1,Column:1}},"Index ou recherche de cellule incorrects.");
            Click(Control<Button>(window,"LocateElement"));
            Assert(window.CurrentBlockId==realCell.Source.NodeId&&window.GetVisualDescendants().OfType<Border>()
                .Any(c=>c.Name=="ReadingCell"&&c.Tag as string==realCell.Source.NodeId&&c.IsFocused),"La cellule n’est pas atteinte dans le flux.");
            filterText.Text="";Layout();
            Click(Control<Button>(window,"ElementProperties"));
            Assert(ReferenceEquals(index,window.CurrentDocumentIndex),"Le panneau reconstruit l’index.");
            window.KeyPressQwerty(PhysicalKey.Escape,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Escape,RawInputModifiers.None);Layout();
            filter.SelectedIndex=(int)DocumentCategory.Equation+1;Layout();
            Assert(((IEnumerable<DocumentElementRow>)list.ItemsSource!).Single().Target!=DocumentTarget.Page,"Localisation d’équation inventée.");
            filter.SelectedIndex=(int)DocumentCategory.Table+1;Layout();
            Capture(window,Path.Combine(root,"elements-clair-100.png"),1);
            window.SetTheme(2);
            Capture(window,Path.Combine(root,"elements-sombre-150.png"),1.5);
            window.Width=720;window.Height=570;Layout();
            Assert(Control<Border>(window,"DocumentElementsPanel").IsVisible,"Le tiroir choisi a disparu en petite fenêtre.");
            var readingView=Control<ListBox>(window,"SimplifiedReading");
            Assert(readingView.Bounds.Width>=400,"Zone de lecture trop étroite.");
            Assert(Control<Button>(window,"ToggleNavigation").IsEffectivelyVisible,"Le panneau masqué n’est plus accessible.");
            Capture(window,Path.Combine(root,"petite-sombre-150.png"),1.5);
            Click(Control<Button>(window,"ToggleNavigation"));
            Click(window.GetVisualDescendants().OfType<Button>().First(b=>b.Content as string=="Diagnostics"));
            Control<Expander>(window,"TypographyDiagnostics").IsExpanded=true;Layout();
            Click(Control<Button>(window,"AnalyzeTypography"));
            var deadline=DateTime.UtcNow.AddSeconds(18);
            while(!window.GetVisualDescendants().OfType<ListBox>().Any(b=>b.Name=="UnmeasuredTypography")&&DateTime.UtcNow<deadline)
            {Dispatcher.UIThread.RunJobs();Thread.Sleep(5);}
            var missing=Control<ListBox>(window,"UnmeasuredTypography");
            var unmeasured=((IEnumerable<UnmeasuredText>)missing.ItemsSource!).Single();
            Assert(unmeasured.Text==char.ConvertFromUtf32(0x10fffd)&&unmeasured.Sources is [{Span:{Start:0,EndExclusive:2}}],
                "Le diagnostic n’expose pas le texte privé et son étendue.");
            missing.SelectedItem=unmeasured;Layout();
            Click(Control<Button>(window,"UnmeasuredTypographyDetails"));
            Assert(window.ActiveReadingContext?.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Any(b=>b.Text==unmeasured.Text)==true,"Le passage source n’est pas consultable dans les détails.");
            Assert(ReferenceEquals(reading,window.CurrentReading)&&window.CurrentPage==0,"L’analyse a changé l’état de lecture.");
            Assert(Hash(paginated)==firstHash&&Hash(specialized)==secondHash,"Fixture DOCX modifiée.");
            Console.WriteLine("Rule, cells, source marks, clipboard, search, text coverage and fourteen categories verified.");

            void Key(PhysicalKey key)
            {
                window.KeyPressQwerty(key,RawInputModifiers.None);
                window.KeyReleaseQwerty(key,RawInputModifiers.None);
                Layout();
            }

            void CheckCopy()
            {
                var text=window.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Name=="ReadingParagraph"&&t.Tag as string==passage.Id);
                var item=text.ContextMenu!.Items.OfType<MenuItem>().Single();
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Layout();
                Assert(AboutCases.Complete(window.Clipboard!.TryGetTextAsync())==reading.Presentation.TextFor(passage),"Copie du passage altérée.");
            }
        }
        finally{window.Close();}
    }

    public static void Corpus(string root)
    {
        Setup();
        string path=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_MANIFEST")??
            throw new InvalidOperationException("Sélectionner le manifeste privé des onze copies.");
        using var manifest=JsonDocument.Parse(File.ReadAllText(path));
        using var reference=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_REFERENCE")!));
        var files=manifest.RootElement.EnumerateArray().ToArray();
        Assert(files.Select(f=>f.GetProperty("id").GetInt32()).Order().SequenceEqual(Enumerable.Range(0,11)),"Onze copies attendues.");
        var recorded=new List<object>();
        var win=new ReaderWindow{Width=1280,Height=850};win.Show();
        try
        {
            foreach(var item in files)
            {
                int id=item.GetProperty("id").GetInt32();
                string copy=item.GetProperty("copy").GetString()!,hash=item.GetProperty("sha256").GetString()!;
                var baseline=reference.RootElement.EnumerateArray().Single(b=>b.GetProperty("id").GetInt32()==id);
                Assert(Hash(copy)==hash,"Copie modifiée.");
                Open(win,copy);
                Assert(win.Inspection?.Manifest.SourceSha256==hash&&win.CurrentReading?.Available==true,"Inspection ou lecture non affichée.");
                var index=win.CurrentDocumentIndex!;
                Assert(index.Limitation is null,"Index sémantique incomplet.");
                var semantic=SemanticReader.Build(win.Inspection!).Document!;
                var active=semantic.Elements.Where(e=>e.Coverage!=SemanticCoverage.InactiveAlternative).ToDictionary(e=>e.Id);
                Assert(index.Rows.All(r=>active.ContainsKey(r.Source.NodeId)&&r.Source.SnapshotId==hash),"Provenance absente ou branche inactive visible.");
                int n=index.Rows.Count(r=>r.Category==DocumentCategory.Table);
                Assert(n==semantic.Tables.Length,"Tableaux du modèle absents du tiroir.");
                int cells=index.Rows.Count(r=>r.Category==DocumentCategory.Cell);
                Assert(cells==active.Values.Count(e=>e.Kind==SemanticKind.Cell),"Cellules du modèle absentes du tiroir.");
                if(id==4)Assert(n==33,"Le guide doit inventorier 33 tableaux.");
                if(id==5)Assert(n==16&&index.Rows.Any(r=>r.Category==DocumentCategory.Field)&&index.Rows.Any(r=>r.Category is DocumentCategory.Drawing or DocumentCategory.Image),"Stage 2 incomplet.");
                if(id is 2 or 3)Assert(index.Rows.Any(r=>r.Target==DocumentTarget.Note),"Aucun appel de note localisable.");
                if(id==0)Assert(index.Rows.Any(r=>r.Category==DocumentCategory.Running)&&index.Rows.Any(r=>r.Category==DocumentCategory.Section),"En-têtes/pieds ou sections absents.");
                Assert(win.CurrentComposition!.Pages.Count==baseline.GetProperty("A_pages").GetInt32(),"Régression de pagination.");
                Assert(win.CurrentPage==0?index.Rows.All(r=>r.Page is null):index.Rows.All(r=>r.Page is null||r.Page<=win.CurrentComposition!.Pages.Count),"Page proposée sans correspondance.");
                var typography=AboutCases.Complete(win.AnalyzeTypographyAsync());
                int expectedMissing=id is 2 or 3?12:id is 5 or 7?5:0;
                Assert(typography is {Refusal:null}&&typography.SnapshotId==hash&&typography.Unmeasured.Length==expectedMissing,
                    "Couverture TEXT-001 différente sur la copie "+id);
                foreach(var fragment in typography!.Unmeasured)
                {
                    Assert(string.Concat(fragment.Sources.Select(s=>semantic.RawText(s.NodeId)![s.Span!.Value.Start..s.Span.Value.EndExclusive]))==fragment.Text,
                        "Le texte du diagnostic ne reproduit pas sa source.");
                    Assert(typography.Diagnostics.Any(d=>d.Code=="TEXT_MISSING_FACE"),"Diagnostic de couverture disparu.");
                }
                if(id is 1 or 5)
                {
                    win.SetTheme(1);Capture(win,Path.Combine(root,$"apres-{id:00}-1.png"),1);
                    win.SetTheme(2);Capture(win,Path.Combine(root,$"apres-{id:00}-2.png"),1);
                }
                if(id is 4 or 5)
                {
                    if(!Control<Border>(win,"DocumentElementsPanel").IsVisible)Click(Control<Button>(win,"ToggleElements"));
                    Control<ComboBox>(win,"ElementCategory").SelectedIndex=(int)DocumentCategory.Table+1;Layout();
                    var list=Control<ListBox>(win,"DocumentElements");
                    Assert(((IEnumerable<DocumentElementRow>)list.ItemsSource!).Count()==n,"Tiroir et inventaire incohérents.");
                    Capture(win,Path.Combine(root,$"tableaux-{id:00}-sombre.png"),1.5);
                    Click(Control<Button>(win,"ToggleElements"));
                }
                Assert(Hash(copy)==hash,"Le DOCX a été modifié pendant la recette.");
                recorded.Add(new {id,tables=n,cells,unmeasured=typography.Unmeasured.Length,pages=win.CurrentComposition.Pages.Count,index.Words,index.Characters,index.Language,
                    categories=index.Rows.GroupBy(r=>r.Category.ToString()).ToDictionary(g=>g.Key,g=>g.Count()),
                    targets=index.Rows.GroupBy(r=>r.Target.ToString()).ToDictionary(g=>g.Key,g=>g.Count())});
                Console.WriteLine($"OK UX {id:00} · {n} tableaux · {index.Rows.Count} éléments · {win.CurrentComposition.Pages.Count} pages");
            }
        }
        finally{win.Close();}
        File.WriteAllText(Path.Combine(root,"ux-corpus.json"),JsonSerializer.Serialize(recorded,new JsonSerializerOptions{WriteIndented=true}));
    }

    public static async Task Native(ReaderWindow win,string output)
    {
        string manifest=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_MANIFEST")??
            throw new InvalidOperationException("Manifeste local explicite requis.");
        using var files=JsonDocument.Parse(File.ReadAllText(manifest));
        double expected=double.Parse(Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_EXPECTED_SCALE")??"1",CultureInfo.InvariantCulture);
        var handle=win.TryGetPlatformHandle();
        bool x11=handle is {HandleDescriptor:"XID"} && handle.Handle!=IntPtr.Zero;
        Assert(x11,"La qualification native exige X11.");
        win.Width=1120;win.Height=690;
        win.SetTheme(1);
        var captures=new List<object>();
        var keys=new List<object>();
        foreach(int id in new[]{1,5})
        {
            var input=files.RootElement.EnumerateArray().Single(f=>f.GetProperty("id").GetInt32()==id);
            string copy=input.GetProperty("copy").GetString()!,hash=input.GetProperty("sha256").GetString()!;
            Assert(Hash(copy)==hash,"Copie native hors référence.");
            await win.OpenDocumentAsync(copy);await Task.Delay(300);
            Assert(Math.Abs(win.RenderScaling-expected)<.01,"Échelle Avalonia observée différente de l’échelle de recette.");
            if(id==1)
            {
                Assert(win.CurrentPage==1,"La lettre native n’a pas de page calculée.");
                await SendKey("F7","ToggleRuler",()=>win.RulerVisible);
                Assert(win.CurrentPage==1&&win.RulerVisible,"Règle ou commande F7 non qualifiée.");
                await ClickOn("ToggleNonprinting");
                Assert(Control<Control>(win,"PageNonprintingMarks").IsVisible,"Superposition de marques absente en X11.");
                await CaptureNative("lettre-clair");
                await ClickOn("ThemeMicrosoftBlue");
                Assert(win.ActualThemeVariant==ReaderPalette.BlueVariant&&win.RulerVisible&&win.CurrentPage==1,"La bascule native perd la règle ou la page.");
                await CaptureNative("lettre-bleu");
                await ClickOn("ThemeChocolate");
            }
            else
            {
                Assert(win.IsSimplifiedReading&&win.CurrentPage==0&&!win.RulerVisible,"Règle indue au passage en lecture continue.");
                await SendKey("F8","ToggleElements",()=>Control<Border>(win,"DocumentElementsPanel").IsVisible);
                Assert(Control<Border>(win,"DocumentElementsPanel").IsVisible,"Commande F8 ou panneau inaccessible.");
                Control<ComboBox>(win,"ElementCategory").SelectedIndex=(int)DocumentCategory.Table+1;
                await Task.Delay(150);
                Assert(((IEnumerable<DocumentElementRow>)Control<ListBox>(win,"DocumentElements").ItemsSource!).Count()==16,"Les tableaux de Stage 2 ne sont pas tous visibles dans le filtre.");
                await ClickOn("LocateElement");
                Assert(win.CurrentBlockId is not null,"Le bouton ne localise pas le tableau.");
                await ClickOn("ThemeMicrosoftBlue");
                await CaptureNative("stage2-bleu");
                Control<ComboBox>(win,"ElementCategory").SelectedIndex=(int)DocumentCategory.Cell+1;
                var cells=Control<ListBox>(win,"DocumentElements");
                var cell=((IEnumerable<DocumentElementRow>)cells.ItemsSource!).First(c=>c.Target==DocumentTarget.Cell);
                cells.SelectedItem=cell;
                await ClickOn("LocateElement");
                Assert(win.GetVisualDescendants().OfType<Border>()
                    .Any(b=>b.Name=="ReadingCell"&&b.Tag as string==cell.Source.NodeId&&b.IsFocused),
                    "La cellule native sélectionnée n’a pas reçu le focus.");
                await CaptureNative("stage2-cellules-bleu");
                await ClickOn("LanguageToggle");
                Assert(Equals(ToolTip.GetTip(Control<Button>(win,"ThemeMicrosoftBlue")),"Microsoft Blue"),"Libellé anglais natif incorrect.");
                win.Width=720;win.Height=570;await Task.Delay(280);
                Assert(Control<Border>(win,"DocumentElementsPanel").IsVisible&&Control<ListBox>(win,"SimplifiedReading").Bounds.Width>=400,
                    "Le tiroir réduit trop le passage en fenêtre étroite.");
                await CaptureNative("stage2-compact-bleu-en");
                await ClickOn("ThemeSystem");
                Assert(Control<Button>(win,"ThemeSystem").Classes.Contains("active")&&win.IsSimplifiedReading,
                    "Le mode Système n’est pas accessible dans le lecteur natif.");
            }
            Assert(Hash(copy)==hash,"Copie native modifiée.");
        }
        File.WriteAllText(Path.Combine(output,"native-opus-results.json"),JsonSerializer.Serialize(new
        { scale=win.RenderScaling,native=x11,captures,keys },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("X11 UX-OPUS vérifié à "+win.RenderScaling.ToString(CultureInfo.InvariantCulture));

        async Task ClickOn(string name)
        {
            Control<Button>(win,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
        }
        async Task SendKey(string key,string action,Func<bool> actionObserved)
        {
            var modifiers=new List<string>();
            void Observe(object? sender,KeyEventArgs evt){if(evt.Key.ToString()==key)modifiers.Add(evt.KeyModifiers.ToString());}
            win.AddHandler(InputElement.KeyDownEvent,Observe,RoutingStrategies.Tunnel,true);
            Control<Button>(win,action).Focus();
            var info=new ProcessStartInfo("xdotool"){UseShellExecute=false};
            string xid=handle!.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
            foreach(string arg in new[]{"windowactivate","--sync",xid,"windowfocus","--sync",xid,"key","--clearmodifiers",key})
                info.ArgumentList.Add(arg);
            using var process=Process.Start(info)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert(process.ExitCode==0,"Le raccourci XTEST n’a pas été transmis.");
            await Task.Delay(250);
            win.RemoveHandler(InputElement.KeyDownEvent,Observe);
            bool verified=actionObserved();
            keys.Add(new{key,received=modifiers.Count>0,verified,modifiers,active=win.IsActive});
            if(!verified)
            {
                Console.WriteLine("Action non confirmée sur injection XTEST pour "+key+" ; commande de barre testée sur X11.");
                await ClickOn(action);
            }
        }
        async Task CaptureNative(string name)
        {
            string png=Path.Combine(output,name+".png");
            var info=new ProcessStartInfo("import"){UseShellExecute=false};
            foreach(string arg in new[]{"-window",handle!.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),png})info.ArgumentList.Add(arg);
            using var process=Process.Start(info)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert(process.ExitCode==0&&new FileInfo(png).Length>1024,"Capture X11 absente.");
            captures.Add(new {name,width=win.ClientSize.Width,height=win.ClientSize.Height});
        }
    }
}
