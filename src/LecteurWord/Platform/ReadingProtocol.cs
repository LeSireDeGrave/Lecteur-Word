using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LecteurWord.Platform;

internal sealed record DocumentMetrics(int BodyBlocks, int Paragraphs, int Headings, int Tables, int Notes, int Pages);
internal sealed record ReadingRequest(int ApiVersion, string RequestId, DocumentMetrics Document, ReadingContribution Contribution);
internal sealed record MetricResult(string Label, decimal Value);
internal sealed record ReadingResponse(int ApiVersion, string RequestId, MetricResult[]? Results, string? Error);

internal static class ReadingProtocol
{
    internal const string WorkerArgument = "--extension-worker-v1";
    const int InputLimit = 40 * 1024;
    const int OutputLimit = 12 * 1024;

    internal static void Validate(ReadingContribution? contribution)
    {
        ExtensionPackage.Require(contribution?.Metrics is { Length: >= 1 and <= 12 }, "Prévoir de 1 à 12 indicateurs.");
        var metrics = contribution.Metrics;
        ExtensionPackage.Require(metrics.All(m => m is not null && ExtensionPackage.TextField(m.Label,100) &&
            m.Expression is { Length: >= 1 and <= 32 }) &&
            metrics.Select(m => m.Label).Distinct(StringComparer.Ordinal).Count() == metrics.Length, "Indicateur de lecture invalide.");
        foreach (var entry in metrics)
        {
            int values = 0;
            foreach (string token in entry.Expression)
            {
                if (IsOperator(token))
                {
                    ExtensionPackage.Require(values >= 2, "Il manque un opérande dans une expression.");
                    values--;
                }
                else
                {
                    ExtensionPackage.Require(IsCounter(token) || TryConstant(token,out _), "Opérande de lecture inconnu.");
                    values++;
                }
            }
            ExtensionPackage.Require(values == 1, "L’expression doit produire une valeur.");
        }
    }

    internal static async Task<MetricResult[]> RunAsync(ReadingContribution contribution, DocumentMetrics metrics, CancellationToken cancellation)
    {
        var request = new ReadingRequest(ExtensionPackage.Api, Guid.NewGuid().ToString("N"), metrics, contribution);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var token = timeout.Token;
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Hôte .NET introuvable."),
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        var assembly = typeof(ReadingProtocol).Assembly;
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(assembly.Location);
        else if (System.Reflection.Assembly.GetEntryAssembly() != assembly)
            throw new InvalidOperationException("Hôte du processus d’extension non reconnu.");
        start.ArgumentList.Add(WorkerArgument);

        using var worker = Process.Start(start) ?? throw new IOException("Le processus d’extension n’a pas démarré.");
        using var stop = token.Register(() =>
        {
            try { worker.Kill(entireProcessTree:false); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        try
        {
            var output = PlatformFiles.ReadBoundedAsync(worker.StandardOutput.BaseStream, OutputLimit, token);
            var stderr = PlatformFiles.ReadBoundedAsync(worker.StandardError.BaseStream, OutputLimit, token);
            await worker.StandardInput.WriteAsync(JsonSerializer.Serialize(request,PlatformFiles.Json).AsMemory(),token);
            worker.StandardInput.Close();
            await Task.WhenAll(output,stderr,worker.WaitForExitAsync(token));
            if (worker.ExitCode != 0) throw new InvalidDataException("Le processus de l’extension s’est interrompu.");
            var response = ExtensionPackage.Decode<ReadingResponse>(await output);
            ExtensionPackage.Require(response.ApiVersion == request.ApiVersion && response.RequestId == request.RequestId,
                "Réponse du processus incompatible.");
            if (response.Error == "arithmetic") throw new InvalidDataException("Calcul impossible dans l’extension.");
            ExtensionPackage.Require(response.Error is null && response.Results?.Length == contribution.Metrics.Length &&
                response.Results.Select(m => m.Label).SequenceEqual(contribution.Metrics.Select(m => m.Label)),
                "Réponse du processus invalide.");
            return response.Results!;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Délai d’exécution de l’extension dépassé.");
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree:false);
            await worker.WaitForExitAsync();
        }
    }

    internal static int RunWorker(Stream input, Stream output)
    {
        try
        {
            var request = ExtensionPackage.Decode<ReadingRequest>(PlatformFiles.ReadBounded(input,InputLimit));
            ExtensionPackage.Require(request.ApiVersion == ExtensionPackage.Api &&
                Guid.TryParseExact(request.RequestId,"N",out _) && request.Document is not null, "Requête du lecteur invalide.");
            Validate(request.Contribution);
            ReadingResponse response;
            try
            {
                response = new(request.ApiVersion,request.RequestId,request.Contribution.Metrics.Select(
                    m => new MetricResult(m.Label,Evaluate(m.Expression,request.Document))).ToArray(),null);
            }
            catch (ArithmeticException)
            {
                response = new(request.ApiVersion,request.RequestId,null,"arithmetic");
            }
            JsonSerializer.Serialize(output,response,PlatformFiles.Json);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { return 65; }
    }

    static decimal Evaluate(string[] expression, DocumentMetrics m)
    {
        var values = new Stack<decimal>();
        foreach (string token in expression)
        {
            if (IsOperator(token))
            {
                decimal b = values.Pop(), a = values.Pop();
                values.Push(token switch { "+" => a+b, "-" => a-b, "*" => a*b, "/" => a/b, "max" => Math.Max(a,b), _ => Math.Min(a,b) });
            }
            else values.Push(token switch
            {
                "bodyBlocks" => m.BodyBlocks, "paragraphs" => m.Paragraphs, "headings" => m.Headings,
                "tables" => m.Tables, "notes" => m.Notes, "pages" => m.Pages,
                _ => decimal.Parse(token,NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture)
            });
        }
        return values.Pop();
    }

    static bool IsOperator(string? token) => token is "+" or "-" or "*" or "/" or "max" or "min";
    static bool IsCounter(string? token) => token is "bodyBlocks" or "paragraphs" or "headings" or "tables" or "notes" or "pages";
    static bool TryConstant(string? token,out decimal value)
    {
        value = 0;
        return token is { Length: <= 16 } &&
            decimal.TryParse(token,NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value) &&
            Math.Abs(value) <= 1_000_000;
    }
}
