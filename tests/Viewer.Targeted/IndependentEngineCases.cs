using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class IndependentEngineCases
{
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    public static void Corpus(string root)
    {
        string inputs=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_MANIFEST")??throw new InvalidOperationException("Select the frozen private input manifest explicitly.");
        string reference=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_CORPUS_REFERENCE")??throw new InvalidOperationException("Select the qualified C# baseline explicitly.");
        using var manifest=JsonDocument.Parse(File.ReadAllText(inputs));
        using var baseline=JsonDocument.Parse(File.ReadAllText(reference));
        Check(manifest.RootElement.GetArrayLength()==11,"Expected eleven frozen inputs");
        var results=new List<object>();
        foreach(var item in manifest.RootElement.EnumerateArray())
        {
            int id=item.GetProperty("id").GetInt32();string path=Path.GetFullPath(item.GetProperty("copy").GetString()!);
            string hash=item.GetProperty("sha256").GetString()!;
            var expected=baseline.RootElement.EnumerateArray().Single(b=>b.GetProperty("id").GetInt32()==id);
            Check(expected.GetProperty("sha256").GetString()==hash,"Reference/input identity mismatch");
            Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))==hash,"Frozen copy changed before inspection");
            var inspected=DocumentInspector.InspectDocument(path);
            Check(inspected.Manifest.InspectionComplete,"C# inventory refused input "+id);
            var composition=Composer.Compose(inspected);var reading=ReadableReader.Read(inspected);
            Check(reading.Available,"C# reading unavailable for input "+id);
            Check(composition.Available==expected.GetProperty("A_paginated").GetBoolean()&&composition.Pages.Count==expected.GetProperty("A_pages").GetInt32(),"C# pagination changed for input "+id);
            Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))==hash,"Frozen copy changed during reading");
            results.Add(new{id,sha256=hash,inspection=true,reading=reading.Available,paginated=composition.Available,pages=composition.Pages.Count,issues=composition.Issues.Select(i=>i.Code).Distinct().ToArray()});
        }
        File.WriteAllText(Path.Combine(root,"independent-corpus.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Eleven frozen copies: C# inventory and reading available; pagination matches the independent RENDER-001 baseline; input hashes unchanged.");
    }
    public static void Run(string root)
    {
        string[] settings=["LECTEUR_WORD_EXPERIMENTAL_RUNTIME","LECTEUR_WORD_EXPERIMENTAL_ELIXIR"];
        var previous=settings.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            // Previously valid activation must have no effect, even when requested.
            Environment.SetEnvironmentVariable(settings[0],Path.Combine(root,"retired-runtime"));
            Environment.SetEnvironmentVariable(settings[1],"1");
            var assembly=typeof(ReaderWindow).Assembly;
            Check(assembly.GetReferencedAssemblies().All(a=>a.Name!="DocxEngine.Experimental"),"Retired worker assembly is referenced");
            Check(typeof(ReaderWindow).GetMethod("ConfigureExperimentalEngine") is null,"Retired activation API remains public");
            AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();
            var window=new ReaderWindow();window.Show();
            try
            {
                Check(!window.GetVisualDescendants().OfType<CheckBox>().Any(c=>c.Name=="ExperimentalElixir"),"Retired engine switch remains available");
                string path=Fixtures.Write(root,"independent-reader.docx",Fixtures.Parts(Fixtures.Paragraph("Lecture indépendante : été, notes et provenance.")));
                string before=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                AboutCases.Complete(window.OpenDocumentAsync(path));
                Check(window.CurrentComposition?.Available==true&&window.CurrentPage==1,"C# pagination unavailable with legacy settings");
                using var report=JsonDocument.Parse(window.DetailedDiagnosticReport());
                Check(!report.RootElement.TryGetProperty("experimentalEngine",out _),"Retired engine remains in active diagnostics");
                Check(window.Inspection!.Manifest.SourceSha256==before&&Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))==before,"Original changed");
                Check(AppDomain.CurrentDomain.GetAssemblies().All(a=>a.GetName().Name!="DocxEngine.Experimental"),"Retired assembly was loaded");
                File.WriteAllBytes(Path.Combine(root,"independent-diagnostics.json"),window.DetailedDiagnosticReport());
                Console.WriteLine("Legacy activation ignored; C# page composed; retired API, switch and assembly absent; original unchanged.");
            }
            finally{window.Close();}
        }
        finally{for(int i=0;i<settings.Length;i++)Environment.SetEnvironmentVariable(settings[i],previous[i]);}
    }
}
