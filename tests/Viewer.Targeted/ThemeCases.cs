using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.Flow;

static class ThemeCases
{
    static void Check(bool condition,string evidence)
    {if(!condition)throw new InvalidOperationException(evidence);}

    internal static void Preferences(string root)
    {
        var file=Path.Combine(root,"settings.json");
        var store=new ThemePreference(file);
        Check(store.Load()==ReaderTheme.System&&!File.Exists(file),"Chargement initial avec effet d’écriture.");
        var nested=JsonNode.Parse("""{"language":"en","pane":{"width":270,"visible":true},"pinned":[1,"x"]}""")!;
        foreach(var previous in new[]{"\"Sombre\"","\"Dark\"","\"Espresso & Lin\"","\"Espresso & Linen\"","2"})
        {
            File.WriteAllText(file,"{\"Theme\":"+previous+",\"other\":"+nested.ToJsonString()+"}");
            Check(store.Load()==ReaderTheme.MicrosoftBlue,"Migration du thème sombre manquante.");
            var migrated=JsonNode.Parse(File.ReadAllText(file))!;
            Check(migrated["Theme"]?.GetValue<string>()=="blue"&&JsonNode.DeepEquals(migrated["other"],nested),
                "La migration a perdu une préférence indépendante.");
            Check(new ThemePreference(file).Load()==ReaderTheme.MicrosoftBlue,"Le choix Bleu Microsoft n’est pas relu.");
        }
        Check(store.Save(ReaderTheme.Chocolate)&&new ThemePreference(file).Load()==ReaderTheme.Chocolate,"Clair non rétabli.");
        Check(store.Save(ReaderTheme.System)&&new ThemePreference(file).Load()==ReaderTheme.System,"Système non rétabli.");
        Check(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(file))!["other"],nested),"Préférence voisine modifiée.");
        File.WriteAllText(file,"{incomplet");
        Check(store.Load()==ReaderTheme.System&&!store.Save(ReaderTheme.MicrosoftBlue)&&File.ReadAllText(file)=="{incomplet",
            "Le fichier illisible doit être conservé.");
        Check(ThemePreference.Resolve(ReaderTheme.System,PlatformThemeVariant.Light)==ThemeVariant.Light&&
              ThemePreference.Resolve(ReaderTheme.System,PlatformThemeVariant.Dark)==ReaderPalette.BlueVariant&&
              ThemePreference.Resolve(ReaderTheme.MicrosoftBlue,PlatformThemeVariant.Light)==ReaderPalette.BlueVariant&&
              ThemePreference.Resolve(ReaderTheme.Chocolate,PlatformThemeVariant.Dark)==ThemeVariant.Light,"Modes explicites ou Système incorrects.");
        Console.WriteLine("Migration de cinq valeurs héritées, persistance, préférence voisine et modes système vérifiés.");
    }

    internal static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var preference=new ThemePreference(Path.Combine(root,"settings.json"));
        var window=new ReaderWindow(preference){Width=1280,Height=850};window.Show();Layout();
        try
        {
            var blue=Control<Button>(window,"ThemeMicrosoftBlue");
            var light=Control<Button>(window,"ThemeChocolate");
            var system=Control<Button>(window,"ThemeSystem");
            Check(AutomationProperties.GetName(blue)=="Bleu Microsoft"&&Equals(ToolTip.GetTip(blue),"Bleu Microsoft"),
                "Libellé français du thème incorrect.");
            string sample=Fixtures.Write(root,"couleurs.docx",Fixtures.Parts(
                "<w:p><w:r><w:rPr><w:color w:val=\"B00020\"/></w:rPr><w:t>Un texte rouge avec des espaces.</w:t></w:r></w:p>"));
            byte[] sourceHash=SHA256.HashData(File.ReadAllBytes(sample));
            AboutCases.Complete(window.OpenDocumentAsync(sample));Layout();
            var composition=window.CurrentComposition;
            Check(composition is {Available:true,Pages.Count:1} &&
                composition.Pages[0].Lines.SelectMany(l=>l.Fragments).Any(f=>f.Style.Color=="B00020"),
                "La couleur source n’a pas été composée.");
            var raster=Composer.PagePng(composition!.Pages[0]);
            var displayed=Control<Image>(window,"CalculatedPage").Source;
            var document=window.CurrentReading;
            var index=window.CurrentDocumentIndex;

            Click(light);Check(window.ActualThemeVariant==ThemeVariant.Light,"Thème Chocolat & Crème inaccessible.");
            Click(blue);Check(window.ActualThemeVariant==ReaderPalette.BlueVariant&&blue.Classes.Contains("active"),"Bascule bleu inactive.");
            Check(ReferenceEquals(composition,window.CurrentComposition)&&ReferenceEquals(displayed,Control<Image>(window,"CalculatedPage").Source)&&
                raster.AsSpan().SequenceEqual(Composer.PagePng(composition.Pages[0])),"Changer de palette modifie la page DOCX.");
            Check(ReferenceEquals(document,window.CurrentReading)&&ReferenceEquals(index,window.CurrentDocumentIndex),"La palette reconstruit le document.");

            Click(Control<Button>(window,"LanguageToggle"));
            Check(AutomationProperties.GetName(blue)=="Microsoft Blue"&&Equals(ToolTip.GetTip(blue),"Microsoft Blue"),
                "Libellé anglais du thème incorrect.");
            Check(window.GetVisualDescendants().OfType<Button>().Select(AutomationProperties.GetName)
                .All(name=>name?.Contains("Espresso",StringComparison.OrdinalIgnoreCase)!=true&&name?.Contains("dark theme",StringComparison.OrdinalIgnoreCase)!=true),
                "Un ancien thème reste proposé.");

            Color Resource(string key)
            {
                Check(window.TryFindResource(key,window.ActualThemeVariant,out var value) && value is ISolidColorBrush,
                    "Ressource non résolue pour la palette bleue : "+key);
                return ((ISolidColorBrush)value!).Color;
            }
            Color ColorOf(IBrush brush)=>((ISolidColorBrush)brush).Color;
            Contrast("entête",ColorOf(ReaderPalette.Blue.HeaderInk),ColorOf(ReaderPalette.Blue.Header),4.5);
            Contrast("barres",ColorOf(ReaderPalette.Blue.Ink),ColorOf(ReaderPalette.Blue.Toolbar),4.5);
            Contrast("texte secondaire",ColorOf(ReaderPalette.Blue.Secondary),ColorOf(ReaderPalette.Blue.Panel),4.5);
            Contrast("survol",Resource("ButtonForegroundPointerOver"),Resource("ButtonBackgroundPointerOver"),4.5);
            Contrast("sélection",Resource("ComboBoxItemForegroundSelected"),Resource("ComboBoxItemBackgroundSelected"),4.5);
            Contrast("désactivation",Resource("ButtonForegroundDisabled"),Resource("ButtonBackgroundDisabled"),4.5);
            Contrast("focus",Resource("SystemControlFocusVisualPrimaryBrush"),ColorOf(ReaderPalette.Blue.Toolbar),3);
            Contrast("focus entête",ColorOf(ReaderPalette.BlueLight),ColorOf(ReaderPalette.Blue.Header),3);
            Contrast("info-bulle",Resource("ToolTipForeground"),Resource("ToolTipBackground"),4.5);
            Contrast("marques",ColorOf(ReaderPalette.Blue.Accent),ColorOf(ReaderPalette.ReadingPaper),4.5);
            Check(Resource("ButtonBackground")!=Resource("ButtonBackgroundPointerOver")&&
                Resource("ButtonBackgroundPointerOver")!=Resource("ButtonBackgroundPressed")&&
                Resource("ButtonBackgroundDisabled")!=Resource("ButtonBackground"),"États des boutons confondus.");
            Check(Resource("SystemControlFocusVisualPrimaryBrush")==Color.Parse("#0F6CBD"),"Focus Fluent n’utilise pas l’accent bleu.");

            Click(Control<Button>(window,"ToggleElements"));
            var category=Control<ComboBox>(window,"ElementCategory");
            category.IsDropDownOpen=true;Layout();
            Check(category.TryFindResource("ComboBoxDropDownBackground",window.ActualThemeVariant,out var menu)
                  && menu is ISolidColorBrush {Color:var menuColor}&&menuColor==Colors.White,"Menu de sélection hors thème.");
            category.IsDropDownOpen=false;
            Click(Control<Button>(window,"AboutLecteurWord"));
            var about=window.OwnedWindows.OfType<AboutWindow>().Single();
            Check(about.ActualThemeVariant==ReaderPalette.BlueVariant&&about.Background==ReaderPalette.Blue.AboutSurface,
                "La fenêtre secondaire ne reçoit pas le thème.");
            window.SetTheme(1);Layout();
            Check(about.ActualThemeVariant==ThemeVariant.Light,"La fenêtre secondaire ne suit pas la bascule à chaud.");
            about.Close();Layout();

            Click(system);
            var actualOs=Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant??PlatformThemeVariant.Light;
            Check(window.ActualThemeVariant==ThemePreference.Resolve(ReaderTheme.System,actualOs)&&system.Classes.Contains("active"),"Mode Système incohérent.");
            Check(new ThemePreference(Path.Combine(root,"settings.json")).Load()==ReaderTheme.System,"Système non mémorisé.");
            Click(blue);
            window.Width=720;window.Height=570;Layout();
            Check(blue.IsEffectivelyVisible&&system.IsEffectivelyVisible,"Sélecteur rogné en fenêtre compacte.");
            Check(sourceHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(sample))),"Le thème a modifié le DOCX.");
        }
        finally{window.Close();}
        var restarted=new ReaderWindow(new ThemePreference(Path.Combine(root,"settings.json")));restarted.Show();Layout();
        Check(restarted.ActualThemeVariant==ReaderPalette.BlueVariant,"Le thème n’est pas appliqué au démarrage de la fenêtre suivante.");
        restarted.Close();
    }

    static T Control<T>(ReaderWindow w,string name) where T:Control=>w.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
    static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();}
    static void Layout()
    {
        Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();
    }

    static void Contrast(string state,Color a,Color b,double threshold)
    {
        static double Linear(byte x){double v=x/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
        static double Luma(Color c)=>.2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
        double first=Luma(a),second=Luma(b);
        double ratio=(Math.Max(first,second)+.05)/(Math.Min(first,second)+.05);
        Console.WriteLine(FormattableString.Invariant($"{state} · {ratio:F2}:1"));
        Check(ratio>=threshold,"Contraste insuffisant : "+state);
    }
}
