using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LecteurWord.Platform;

internal sealed record CatalogEntry(ExtensionManifest Manifest, string PackageUrl, string PackageSha256);
internal sealed record CatalogIndex(int Schema, CatalogEntry[] Entries);

internal sealed class ExtensionCatalog
{
    internal const int IndexLimit = 512 * 1024;
    internal const int EntryLimit = 256;
    const int CachedPackages = 32;
    sealed record StoredIndex(int Schema, string Address, byte[] Content);
    static readonly HttpClient Web = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseDefaultCredentials = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    readonly HttpClient http;
    readonly Uri address;
    readonly ExtensionHostVersion reader;
    readonly string? cache;

    internal ExtensionCatalog(string url, ExtensionHostVersion reader, HttpClient? transport = null, string? cacheRoot = null)
    {
        ExtensionPackage.Require(ExtensionPackage.IsHttps(url), "Le catalogue exige une adresse HTTPS valide.");
        address = new Uri(url);
        this.reader = reader;
        http = transport ?? Web;
        if (cacheRoot is not null)
            cache = Path.Combine(cacheRoot, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address.AbsoluteUri))));
    }

    internal async Task<IReadOnlyList<CatalogEntry>> ReadAsync(CancellationToken cancellation)
    {
        var bytes = await FetchAsync(address,IndexLimit,cancellation);
        var validated = Compatible(ValidateIndex(bytes));
        if (cache is not null)
        {
            try { PlatformFiles.Write(Path.Combine(cache, "index.json"), new StoredIndex(1,address.AbsoluteUri,bytes)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return validated;
    }

    internal IReadOnlyList<CatalogEntry>? ReadCached()
    {
        if (cache is null) return null;
        try
        {
            var stored = PlatformFiles.ReadOptional<StoredIndex>(Path.Combine(cache,"index.json"),IndexLimit * 2);
            if (stored is null) return null;
            ExtensionPackage.Require(stored is { Schema: 1, Content.Length: <= IndexLimit } &&
                stored.Address == address.AbsoluteUri, "Catalogue local invalide.");
            return Compatible(ValidateIndex(stored.Content));
        }
        catch (JsonException error) { throw new InvalidDataException("Catalogue local invalide.",error); }
    }

    internal static CatalogIndex ValidateIndex(ReadOnlySpan<byte> bytes)
    {
        ExtensionPackage.Require(bytes.Length <= IndexLimit,"Version ou taille du catalogue incompatible.");
        CatalogIndex index;
        try { index = ExtensionPackage.Decode<CatalogIndex>(bytes); }
        catch (JsonException error) { throw new InvalidDataException("Index du catalogue invalide.",error); }
        ExtensionPackage.Require(index is { Schema: 1, Entries.Length: <= EntryLimit }, "Version ou taille du catalogue incompatible.");
        var seen = new HashSet<(string Id,string Version)>();
        foreach (var entry in index.Entries)
        {
            ExtensionPackage.Require(entry?.Manifest is not null && ExtensionPackage.IsHttps(entry.PackageUrl) &&
                ExtensionPackage.IsHash(entry.PackageSha256), "Entrée du catalogue invalide.");
            ExtensionPackage.ValidateManifest(entry.Manifest);
            ExtensionPackage.Require(seen.Add((entry.Manifest.Id,entry.Manifest.Version)),"Version dupliquée dans le catalogue.");
        }
        return index;
    }

    IReadOnlyList<CatalogEntry> Compatible(CatalogIndex index) => index.Entries.Where(entry => ExtensionPackage.IsCompatible(entry.Manifest,reader))
            .GroupBy(entry => entry.Manifest.Id,StringComparer.Ordinal)
            .Select(group => group.MaxBy(entry => ExtensionPackage.StableVersion(entry.Manifest.Version))!)
            .OrderBy(entry => entry.Manifest.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();

    string? CachedPackagePath(CatalogEntry entry) => cache is not null && ExtensionPackage.IsHash(entry.PackageSha256)
        ? Path.Combine(cache,entry.PackageSha256.ToLowerInvariant() + ".lwext") : null;

    internal bool HasCachedPackage(CatalogEntry entry) => File.Exists(CachedPackagePath(entry));

    internal async Task<ExtensionPackage> DownloadAsync(CatalogEntry entry, CancellationToken cancellation,
        bool offline = false, bool cacheRequired = false)
    {
        ExtensionPackage.Require(ExtensionPackage.IsHttps(entry.PackageUrl),"Adresse du paquet invalide.");
        string? path = CachedPackagePath(entry);
        cancellation.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            try
            {
                using var input = File.OpenRead(path!);
                return ValidatePackage(entry, PlatformFiles.ReadBounded(input,ExtensionPackage.ArchiveLimit));
            }
            catch (Exception error) when (!offline && error is (IOException or UnauthorizedAccessException or InvalidDataException)) { }
        }
        ExtensionPackage.Require(!offline, "Ce paquet est indisponible hors connexion.");
        var bytes = await FetchAsync(new Uri(entry.PackageUrl),ExtensionPackage.ArchiveLimit,cancellation);
        var package = ValidatePackage(entry,bytes);
        cancellation.ThrowIfCancellationRequested();
        if (path is not null)
        {
            try
            {
                PlatformFiles.WriteBytes(path,bytes);
                foreach (var old in new DirectoryInfo(cache!).EnumerateFiles("*.lwext")
                    .Where(file => file.LinkTarget is null)
                    .Where(file => file.Name != Path.GetFileName(path) && ExtensionPackage.IsHash(Path.GetFileNameWithoutExtension(file.Name)))
                    .OrderByDescending(file => file.LastWriteTimeUtc).Skip(CachedPackages - 1))
                    old.Delete();
            }
            catch (Exception error) when (!cacheRequired && error is (IOException or UnauthorizedAccessException)) { }
        }
        return package;
    }

    ExtensionPackage ValidatePackage(CatalogEntry entry, byte[] bytes)
    {
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
