using System.Net;
using System.Text.Json;

namespace LecteurWord.Platform;

internal sealed record CatalogEntry(ExtensionManifest Manifest, string PackageUrl, string PackageSha256);
internal sealed record CatalogIndex(int Schema, CatalogEntry[] Entries);

internal sealed class ExtensionCatalog
{
    static readonly HttpClient Web = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseDefaultCredentials = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    readonly HttpClient http;
    readonly Uri address;
    readonly Version reader;

    internal ExtensionCatalog(string url, Version reader, HttpClient? transport = null)
    {
        ExtensionPackage.Require(ExtensionPackage.IsHttps(url), "Le catalogue exige une adresse HTTPS valide.");
        address = new Uri(url);
        this.reader = reader;
        http = transport ?? Web;
    }

    internal async Task<IReadOnlyList<CatalogEntry>> ReadAsync(CancellationToken cancellation)
    {
        CatalogIndex index;
        try { index = ExtensionPackage.Decode<CatalogIndex>(await FetchAsync(address,512 * 1024,cancellation)); }
        catch (JsonException error) { throw new InvalidDataException("Index du catalogue invalide.",error); }
        ExtensionPackage.Require(index is { Schema: 1, Entries.Length: <= 256 }, "Version ou taille du catalogue incompatible.");
        var seen = new HashSet<(string Id,string Version)>();
        foreach (var entry in index.Entries)
        {
            ExtensionPackage.Require(entry?.Manifest is not null && ExtensionPackage.IsHttps(entry.PackageUrl) &&
                ExtensionPackage.IsHash(entry.PackageSha256), "Entrée du catalogue invalide.");
            ExtensionPackage.ValidateManifest(entry.Manifest);
            ExtensionPackage.Require(seen.Add((entry.Manifest.Id,entry.Manifest.Version)),"Version dupliquée dans le catalogue.");
        }
        return index.Entries.Where(entry => ExtensionPackage.IsCompatible(entry.Manifest,reader))
            .GroupBy(entry => entry.Manifest.Id,StringComparer.Ordinal)
            .Select(group => group.MaxBy(entry => ExtensionPackage.StableVersion(entry.Manifest.Version))!)
            .OrderBy(entry => entry.Manifest.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal async Task<ExtensionPackage> DownloadAsync(CatalogEntry entry, CancellationToken cancellation)
    {
        ExtensionPackage.Require(ExtensionPackage.IsHttps(entry.PackageUrl),"Adresse du paquet invalide.");
        var bytes = await FetchAsync(new Uri(entry.PackageUrl),ExtensionPackage.ArchiveLimit,cancellation);
        ExtensionPackage.Require(ExtensionPackage.HashMatches(bytes,entry.PackageSha256),"Empreinte du paquet différente du catalogue.");
        var package = ExtensionPackage.Read(bytes,reader);
        ExtensionPackage.Require(JsonSerializer.Serialize(package.Manifest,PlatformFiles.Json) ==
            JsonSerializer.Serialize(entry.Manifest,PlatformFiles.Json),"Le manifeste du paquet diffère de la fiche du catalogue.");
        return package;
    }

    async Task<byte[]> FetchAsync(Uri uri, int limit, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var response = await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            response.EnsureSuccessStatusCode();
            ExtensionPackage.Require(response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength <= limit,
                "Ressource du catalogue trop volumineuse.");
            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await PlatformFiles.ReadBoundedAsync(content,limit,timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Le service de catalogue n’a pas répondu dans le délai prévu.");
        }
        catch (HttpRequestException error)
        {
            throw new InvalidDataException("La ressource du catalogue est inaccessible.",error);
        }
    }
}
