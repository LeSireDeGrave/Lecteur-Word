using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class HistoricalFontCases
{
    const string Missing="LW Typographic Witness Missing";
    const int Scalar=0xf0e8;
    static string Create(string root)=>Fixtures.Write(root,"glyphe-historique.docx",Fixtures.Parts($"""
        <w:p><w:r><w:t>Avant</w:t></w:r></w:p>
        <w:p><w:r><w:rPr><w:rFonts w:ascii="{Missing}" w:hAnsi="{Missing}"/></w:rPr><w:t>{(char)Scalar}</w:t></w:r></w:p>
        <w:p><w:pPr><w:pageBreakBefore/></w:pPr><w:r><w:t>Après</w:t></w:r></w:p>
        """));

    internal static void Run(string root)
    {
        string path=Create(root);
        var input=DocumentInspector.InspectDocument(path);
        using var measured=PageComposer.Compose(input);
        var s=measured.Snapshot;
        Check(s.Coverage==PageCoverage.Partial&&s.Rendering is {Pagination:PageCoverage.Complete,MissingGlyphs:1,ContentComplete:false}&&
            s.Pages.Length==2&&s.Gaps.IsEmpty&&s.Composition.Available,"Le glyphe absent doit garder les deux pages consultables.");
        var marker=s.Composition.Pages[0].Lines.SelectMany(l=>l.Native!.Parts).Single(p=>p.Fragment?.LegacySymbol is not null).Fragment!;
        Check(marker.Text=="\uf0e8"&&marker.LegacySymbol is {Method:"missing-marker",DrawingCode:null}&&marker.Measure?.Glyphs.Length==1&&
            s.Composition.Issues.Any(i=>i.Code=="TEXT_HISTORICAL_GLYPH_MISSING"&&!i.BlocksRendering),
            "Le repère visible garde le caractère source et son diagnostic.");

        const string face="DejaVu Serif";
        var choice=new HistoricalFontChoice(Missing,Scalar,false,face,'A');
        Check(TypographySession.PreviewHistoricalGlyph(face,'A',false,false) is {Length:>0}&&
            TypographySession.PreviewHistoricalGlyph(face,Scalar,false,false) is null,"Aperçu de la vraie face, sans couverture de secours.");
        var preferences=new HistoricalFontPreferences(Path.Combine(root,"config","historical-fonts.json"));
        preferences.Set(input.Manifest.SourceSha256!,choice,choice);
        var saved=preferences.Read().Single();
        Check(saved.Choice==choice&&saved.Snapshot==input.Manifest.SourceSha256,"Le choix persistant reste associé au SHA du fichier.");
        using var manual=PageComposer.Compose(input,default,false,null,[saved.Choice]);
        var manualSnapshot=manual.Snapshot;
        var replaced=manualSnapshot.Composition.Pages[0].Lines.SelectMany(l=>l.Native!.Parts).Single(p=>p.Fragment?.LegacySymbol is not null).Fragment!;
        Check(manualSnapshot.Coverage==PageCoverage.Partial&&manualSnapshot.Rendering is
            {Pagination:PageCoverage.Complete,MissingGlyphs:0,UserGlyphChoices:1,ContentComplete:true}&&
            replaced.Text=="\uf0e8"&&replaced.LegacySymbol is {Method:"user",DrawingCode:'A'}&&replaced.Face?.UsedFamily==face,
            "La décision manuelle a sa provenance et garde la notation historique.");
        Check(!Composer.PagePng(s.Composition.Pages[0]).SequenceEqual(Composer.PagePng(manualSnapshot.Composition.Pages[0]))&&
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))==input.Manifest.SourceSha256,
            "Le texte peint change dans la vue et le fichier source garde son SHA.");
        preferences.Set(input.Manifest.SourceSha256!,choice,null);
        Check(preferences.Read().Length==0,"Le rétablissement automatique supprime la règle persistante.");
    }

    internal static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        string document=Create(root);
        var saved=new ThemePreference(Path.Combine(root,"settings.json"));
        var window=new ReaderWindow(saved);
        window.Show();
        try
        {
            AboutCases.Complete(window.OpenDocumentAsync(document));
            Check(window.NativePageStatus=="Partial"&&window.CurrentPage==1,"Pagination consultable avec un repère manquant.");
            Layout();
            Click(Find<Button>(window,"DocumentProperties"));
            Layout();
            window.GoToPage(1);
            Layout();
            Check(window.CurrentPage==2,"La deuxième page est consultable avec un repère manquant.");
            Click(Find<Button>(window,"ManageHistoricalFonts"));
            var dialog=window.OwnedWindows.Single(w=>w.IsVisible);
            Find<ComboBox>(dialog,"HistoricalFontFamily").SelectedItem="DejaVu Serif";
            Find<TextBox>(dialog,"HistoricalFontCode").Text="0041";
            Find<CheckBox>(dialog,"PersistHistoricalFont").IsChecked=true;
            Dispatcher.UIThread.RunJobs();
            var apply=Find<Button>(dialog,"ApplyHistoricalFont");
            Check(apply.IsEnabled&&Find<Image>(dialog,"HistoricalGlyphPreview").Source is not null,"Aperçu avant confirmation.");
            Click(apply);
            AboutCases.Complete(WaitForChoice());
            Check(window.CurrentReadingView==ReadingView.SinglePage&&window.CurrentPage==2&&
                window.CurrentComposition?.Pages.Count==2&&window.ReadingTableSnapshot?.Rendering.MissingGlyphs==0,
                "Le remplacement recompose les deux pages et conserve la page et le mode de lecture.");
            var preferences=new HistoricalFontPreferences(Path.Combine(root,"historical-fonts.json"));
            Check(preferences.Read().Single().Choice.SelectedCode=='A',"La case de mémorisation enregistre le choix effectivement affiché.");
        }
        finally{window.Close();}

        void Layout()
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(1240,850));
            window.Arrange(new Rect(0,0,1240,850));
            Dispatcher.UIThread.RunJobs();
        }

        async Task WaitForChoice()
        {
            var start=System.Diagnostics.Stopwatch.StartNew();
            while(window.ReadingTableSnapshot?.Rendering.UserGlyphChoices!=1)
            {
                Check(start.Elapsed.TotalSeconds<8,"Recomposition de la substitution expirée.");
                await Task.Delay(20);
            }
        }
        static T Find<T>(Window w,string name) where T:Control=>w.GetVisualDescendants().OfType<T>().Single(e=>e.Name==name);
        static void Click(Button b){b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();}
    }

    static void Check(bool value,string message)=>LineCases.Check(value,message);
}
