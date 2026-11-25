using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;

static class AuditCorpusCase
{
    sealed record Reference(string Name, string Ref, string Sha);

    internal static void Run(string root)
    {
        string manifest = RequirePath("LECTEUR_WORD_AUDIT_MANIFEST");
        string sources = RequirePath("LECTEUR_WORD_AUDIT_INPUTS");
        var references = JsonSerializer.Deserialize<Reference[]>(File.ReadAllBytes(manifest), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Manifeste de recette vide.");
        LineCases.Check(references is { Length: 15 }, "Recette locale attendue sur quinze originaux désignés.");
        LineCases.Check(references.Select(entry => entry.Sha).Distinct(StringComparer.Ordinal).Count() == references.Length,
            "Les quinze empreintes doivent être distinctes.");

        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var reader = new ReaderWindow(new ThemePreference(Path.Combine(root, "settings.json")));
        using var process = Process.GetCurrentProcess();
        (string Format, string Extension)[] formats = [("Pdf", "pdf"), ("Odt", "odt"), ("Epub3", "epub"), ("Nova", "nova")];
        int total = 0;
        reader.Show();
        try
        {
            foreach (var entry in references)
            {
                LineCases.Check(entry.Ref.Length == 2 && entry.Ref.All(char.IsAsciiDigit) && entry.Name == Path.GetFileName(entry.Name),
                    "Nom de référence hors périmètre.");
                string input = Path.Combine(sources, entry.Ref, entry.Name);
                LineCases.Check(Hash(input) == entry.Sha, "Empreinte initiale altérée · " + entry.Ref);
                var watch = Stopwatch.StartNew();
                AboutCases.Complete(reader.OpenDocumentAsync(input));
                int pages = reader.CurrentComposition?.Pages.Count ?? 0;
                LineCases.Check(pages > 0, "Le document ne présente aucune feuille · " + entry.Ref);
                total += pages;
                reader.SetReadingView(ReadingView.Continuous);
                LineCases.Check(reader.IsSimplifiedReading, "Lecture continue indisponible · " + entry.Ref);
                reader.SetReadingView(ReadingView.SinglePage);
                reader.GoToPage(pages - 1);
                LineCases.Check(reader.CurrentPage == pages, "Dernière feuille inaccessible · " + entry.Ref);
                long openedMs = watch.ElapsedMilliseconds;
                foreach (var (format, extension) in formats)
                {
                    Click(Find<Button>(reader, "OpenPublication"));
                    Wait(() => reader.ActivePublicationDialog is not null, $"{entry.Ref} {format} · ouverture export");
                    var dialog = reader.ActivePublicationDialog!;
                    Find<RadioButton>(dialog, "PublicationFormat" + format).IsChecked = true;
                    string output = Path.Combine(root, entry.Ref + "." + extension);
                    Find<TextBox>(dialog, "PublicationDestination").Text = output;
                    var publish = Find<Button>(dialog, "PublicationWrite");
                    Wait(() => publish.IsEnabled, $"{entry.Ref} {format} · précontrôle compatible");
                    Click(publish);
                    Wait(() => Find<Button>(dialog, "PublicationOpenFile").IsVisible, $"{entry.Ref} {format} · écriture");
                    LineCases.Check(new FileInfo(output).Length > 0, "Sortie absente ou vide · " + entry.Ref);
                    dialog.Close();
                    Wait(() => reader.ActivePublicationDialog is null, $"{entry.Ref} {format} · fermeture export");
                }
                LineCases.Check(Hash(input) == entry.Sha, "L’original a été modifié · " + entry.Ref);
                process.Refresh();
                Console.WriteLine($"{entry.Ref}\t{pages}\t{openedMs}\t{watch.ElapsedMilliseconds}\t{process.WorkingSet64 / 1024}");
            }
            LineCases.Check(total == 273, $"273 feuilles de référence attendues, obtenu {total}.");
        }
        finally { reader.ActivePublicationDialog?.Close(); reader.Close(); }
    }

    static string RequirePath(string name) => Environment.GetEnvironmentVariable(name) is { } path && Path.IsPathFullyQualified(path)
        ? path : throw new InvalidOperationException("Fournir le chemin de recette local explicite " + name);

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static T Find<T>(TopLevel window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void Wait(Func<bool> done, string action)
    {
        var time = Stopwatch.StartNew();
        while (!done() && time.Elapsed < TimeSpan.FromSeconds(40))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(3);
        }
        LineCases.Check(done(), "Délai de recette dépassé · " + action);
    }
}
