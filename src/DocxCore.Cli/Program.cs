using System.Text.Json;
using LecteurWord.DocxCore;

if (args.SequenceEqual(new[] { "capabilities" }))
{
    Console.WriteLine("{\"protocol\":\"lecteur-word-inspect\",\"schemaVersions\":[1],\"engineVersion\":\"0.1.0\",\"importReady\":false,\"pagination\":\"notTested\",\"exports\":[]}");
    return 0;
}
if (args.Length < 5 || args[0] != "inspect") return Usage();
var flags = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--input" or "--output" or "--options" or "--schema-version") || !flags.TryAdd(args[i], args[i + 1]))
        return Usage();
}
if (!flags.TryGetValue("--input", out string? input) || !flags.TryGetValue("--output", out string? output)) return Usage();
if (flags.GetValueOrDefault("--schema-version", "1") != "1") { Console.Error.WriteLine("UNSUPPORTED_SCHEMA_VERSION"); return 65; }
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    InspectionOptions options = new();
    if (flags.TryGetValue("--options", out string? optionPath))
    {
        if (new FileInfo(optionPath).Length > 16384) throw new ArgumentException("Options too large.");
        options = JsonSerializer.Deserialize(File.ReadAllText(optionPath), ContractJson.Default.InspectionOptions) ?? throw new ArgumentException("Invalid options.");
    }
    var result = DocumentInspector.InspectDocument(input, options, cancellation.Token);
    if (cancellation.IsCancellationRequested) return 130;
    if (!result.Manifest.InspectionComplete) Console.Error.WriteLine(result.Manifest.Diagnostics.Last().Code);
    AtomicOutput.Write(result, output, cancellation.Token);
    Console.WriteLine("{\"schemaVersion\":1,\"status\":\"" + result.Manifest.Status + "\",\"inspectionComplete\":" +
        (result.Manifest.InspectionComplete ? "true" : "false") + ",\"importReady\":false}");
    if (result.Manifest.InspectionComplete) return 2; // Complete inventory, unqualified import/layout.
    string code = result.Manifest.Diagnostics.Last().Code;
    return code switch
    {
        "CANCELLED" => 130,
        "IO_ERROR" or "ACCESS_REFUSED" => 74,
        _ when code.EndsWith("LIMIT", StringComparison.Ordinal) => 75,
        _ => 3
    };
}
catch (OperationCanceledException) { Console.Error.WriteLine("CANCELLED"); return 130; }
catch (ArgumentException) { Console.Error.WriteLine("INVALID_OPTIONS"); return 64; }
catch (JsonException) { Console.Error.WriteLine("INVALID_OPTIONS_JSON"); return 64; }
catch (OutputLimitException e) { Console.Error.WriteLine(e.Message); return 75; }
catch (IOException) { Console.Error.WriteLine("OUTPUT_OR_IO_REFUSED"); return 74; }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("ACCESS_REFUSED"); return 74; }
catch (Exception) { Console.Error.WriteLine("INTERNAL_ERROR"); return 70; }
static int Usage()
{
    Console.Error.WriteLine("docx-core capabilities | inspect --input <file> --output <new-temporary-directory> [--options <json>] [--schema-version 1]");
    return 64;
}
