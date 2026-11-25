using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LecteurWord.Platform;

internal static class ExtensionTool
{
    internal const string Argument = "--extension-tool-v1";

    internal static int Run(string[] arguments, TextWriter output)
    {
        try
        {
            var host = ExtensionHostVersion.Parse(BuildReference.Current.Version);
            switch (arguments)
            {
                case ["validate", var package]:
                    var checkedPackage = ReadPackage(package,host);
                    Report(new { valid = true, manifest = checkedPackage.Manifest,
                        compatibleWithThisBuild = ExtensionPackage.IsCompatible(checkedPackage.Manifest,host) });
                    break;
                case ["pack", var manifest, var payload, var destination]:
                    Pack(manifest,payload,destination,host);
                    Report(new { packed = Path.GetFullPath(destination) });
                    break;
                case ["init", var category, var id, var destination]:
                    Initialize(category,id,destination);
                    Report(new { initialized = Path.GetFullPath(destination) });
                    break;
                case ["catalog", var baseUrl, var directory, var destination]:
                    int entries = CreateCatalog(baseUrl,directory,destination,host);
                    Report(new { entries, catalog = Path.GetFullPath(destination) });
                    break;
                case ["validate-catalog", var index]:
                    var catalog = ExtensionCatalog.ValidateIndex(ReadFile(index,ExtensionCatalog.IndexLimit));
                    Report(new { valid = true, entries = catalog.Entries.Length });
                    break;
                default:
                    Report(new { usage = new[] { "init <theme|language|statistics|navigation|accessibility> <id> <directory>",
                        "pack <manifest.json> <payload.json> <package.lwext>", "validate <package.lwext>",
                        "catalog <https-base-url/> <package-directory> <index.json>", "validate-catalog <index.json>" } });
                    return arguments is ["--help"] ? 0 : 64;
            }
            return 0;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException)
        {
            Report(new { code = "EXTENSION_INVALID", detail = error.Message });
            return 65;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report(new { code = "EXTENSION_IO", detail = error.Message });
            return 74;
        }

        void Report(object value) => output.WriteLine(JsonSerializer.Serialize(
            new { protocol = "lecteur-word-extension-tool", apiVersion = ExtensionPackage.Api, result = value },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    static void Initialize(string category, string id, string path)
    {
        ExtensionPackage.Require(ExtensionPackage.IsId(id),"Identifiant d’extension invalide.");
        (string Feature, string[] Permissions, object Payload) definition = category switch
        {
            "theme" => (ExtensionPackage.ThemeFeature, [], new ThemeContribution(
                new("#E5F4E9","#214D3D"),new("#E9F2FC","#263C70"))),
            "language" => (ExtensionPackage.TranslationFeature, [], new TranslationContribution(
                "fr-CA","Français canadien",new() { ["document.words.other"] = "{0:N0} mots" })),
            "statistics" => (ExtensionPackage.ReadingFeature, [ExtensionPackage.MetricsPermission],
                new ReadingContribution([new("Titres",["headings"])])),
            "navigation" => (ExtensionPackage.NavigationFeature, [ExtensionPackage.NavigationPermission],
                new NavigationContribution([new("marks","Repères de lecture","Reading marks",["sidebar"],5)])),
            "accessibility" => (ExtensionPackage.AccessibilityFeature, [],new AccessibilityContribution("high-contrast",115)),
            _ => throw new InvalidDataException("Catégorie d’extension inconnue.")
        };
        string directory = Path.GetFullPath(path);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Le dossier de création existe déjà.");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(definition.Payload,PlatformFiles.Json);
        var manifest = new ExtensionManifest(id,id,"Extension à décrire","Auteur à renseigner","1.0.0",
            new("0.1.0","2.0.0"),ExtensionPackage.Api,[definition.Feature],definition.Permissions,
            "LicenseRef-ToBeChosen",new("Auteur à renseigner",null),Sha256(payload));
        PlatformFiles.WriteBytes(Path.Combine(directory,"manifest.json"),JsonSerializer.SerializeToUtf8Bytes(manifest,PlatformFiles.Json),replace:false);
        PlatformFiles.WriteBytes(Path.Combine(directory,"payload.json"),payload,replace:false);
    }

    static void Pack(string manifestPath, string payloadPath, string destination, ExtensionHostVersion host)
    {
        ExtensionPackage.Require(Path.GetExtension(destination).Equals(ExtensionManagerService.FileExtension,StringComparison.OrdinalIgnoreCase),
            "Le paquet doit utiliser l’extension .lwext.");
        var manifest = ExtensionPackage.Decode<ExtensionManifest>(ReadFile(manifestPath,ExtensionPackage.ManifestLimit));
        byte[] payload = ReadFile(payloadPath,ExtensionPackage.TranslationPayloadLimit);
        manifest = manifest with { PayloadSha256 = Sha256(payload) };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:true))
        {
            Add("manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest,PlatformFiles.Json));
            Add("payload.json",payload);

            void Add(string name, byte[] bytes)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(1980,1,1,0,0,0,TimeSpan.Zero);
                using var item = entry.Open();
                item.Write(bytes);
            }
        }
        byte[] archive = output.ToArray();
        InspectPackage(archive,host);
        PlatformFiles.WriteBytes(destination,archive,replace:false);
    }

    static int CreateCatalog(string baseUrl, string directory, string destination, ExtensionHostVersion host)
    {
        ExtensionPackage.Require(ExtensionPackage.IsHttps(baseUrl) && baseUrl.EndsWith('/') &&
            new Uri(baseUrl).Query.Length == 0,"Le catalogue exige une adresse HTTPS terminée par /.");
        var sources = Directory.EnumerateFiles(directory,"*.lwext").Take(ExtensionCatalog.EntryLimit+1).Order(StringComparer.Ordinal).ToArray();
        ExtensionPackage.Require(sources.Length <= ExtensionCatalog.EntryLimit,"Version ou taille du catalogue incompatible.");
        var address = new Uri(baseUrl);
        var entries = sources.Select(path =>
        {
            var package = ReadPackage(path,host);
            var published = new Uri(address,Uri.EscapeDataString(Path.GetFileName(path)));
            return new CatalogEntry(package.Manifest,published.AbsoluteUri,Sha256(package.Archive));
        }).ToArray();
        byte[] result = JsonSerializer.SerializeToUtf8Bytes(new CatalogIndex(1,entries),PlatformFiles.Json);
        ExtensionCatalog.ValidateIndex(result);
        PlatformFiles.WriteBytes(destination,result,replace:false);
        return entries.Length;
    }

    static ExtensionPackage ReadPackage(string path, ExtensionHostVersion host) =>
        InspectPackage(ReadFile(path,ExtensionPackage.ArchiveLimit),host);

    static ExtensionPackage InspectPackage(byte[] bytes, ExtensionHostVersion host)
    {
        var package = ExtensionPackage.Read(bytes,host,checkCompatibility:false);
        ExtensionPackage.Require(package.Manifest.ApiVersion == ExtensionPackage.Api,"Version d’API d’extension incompatible.");
        return package;
    }

    static byte[] ReadFile(string path, int limit)
    {
        long length = new FileInfo(path).Length;
        ExtensionPackage.Require(length > 0 && length <= limit,"Taille maximale dépassée.");
        using var input = File.OpenRead(path);
        return PlatformFiles.ReadBounded(input,limit);
    }

    static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
