using System.Security.Cryptography;
using System.Text.Json;

namespace LecteurWord.Platform;

internal sealed record ExtensionSettings(bool AlternateTheme = false, int DecimalPlaces = 1);
internal sealed record InstalledExtension(ExtensionPackage Package, bool Enabled, bool Quarantined,
    ExtensionSettings Settings, string? DownloadSource, string ArchiveSha256)
{
    internal ExtensionManifest Manifest => Package.Manifest;
}

internal sealed class ExtensionManagerService
{
    const int RegistryLimit = 64;
    sealed record StoredExtension(int Schema, byte[] Archive, string ArchiveSha256, bool Enabled, bool Quarantined,
        ExtensionSettings Settings, string? DownloadSource);

    readonly string directory;
    readonly Version readerVersion;
    internal Version ReaderVersion => readerVersion;
    internal const string FileExtension = ".lwext";

    internal ExtensionManagerService(PlatformPaths paths, Version readerVersion)
        => (directory, this.readerVersion) = (Path.Combine(paths.Data, "extensions"), readerVersion);

    internal (IReadOnlyList<InstalledExtension> Items, int Unreadable) List()
    {
        if (!Directory.Exists(directory)) return ([], 0);
        var items = new List<InstalledExtension>();
        int unreadable = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json").Take(RegistryLimit + 1))
        {
            if (items.Count + unreadable == RegistryLimit) { unreadable++; break; }
            try { items.Add(Load(Path.GetFileNameWithoutExtension(file))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                unreadable++;
            }
        }
        return (items.OrderBy(item => item.Manifest.Name,StringComparer.CurrentCultureIgnoreCase).ToArray(), unreadable);
    }

    internal ExtensionPackage InspectLocal(string path)
    {
        using var input = File.OpenRead(path);
        return ExtensionPackage.Read(PlatformFiles.ReadBounded(input,ExtensionPackage.ArchiveLimit),readerVersion);
    }

    internal InstalledExtension Install(ExtensionPackage candidate, string? downloadSource = null)
    {
        var package = ExtensionPackage.Read(candidate.Archive,readerVersion);
        using var ownership = Lock();
        string path = EntryPath(package.Manifest.Id);
        var prior = File.Exists(path) ? Load(package.Manifest.Id) : null;
        ExtensionPackage.Require(prior is null || ExtensionPackage.StableVersion(package.Manifest.Version) > ExtensionPackage.StableVersion(prior.Manifest.Version),
            "Une version égale ou plus récente est déjà installée.");
        ExtensionPackage.Require(prior is not null || Directory.EnumerateFiles(directory,"*.json").Take(RegistryLimit).Count() < RegistryLimit,
            "Nombre maximal d’extensions installées atteint.");
        bool sameFeatures = prior is not null && prior.Manifest.Features.SequenceEqual(package.Manifest.Features) &&
            prior.Manifest.Permissions.SequenceEqual(package.Manifest.Permissions);
        // Each version requires an explicit activation after installation or update.
        var next = new InstalledExtension(package, false, false, sameFeatures ? prior!.Settings : new(),
            downloadSource,Convert.ToHexStringLower(SHA256.HashData(package.Archive)));
        Save(next);
        return next;
    }

    internal InstalledExtension SetEnabled(string id, bool enabled) => Change(id, item =>
    {
        ExtensionPackage.Require(!enabled || ExtensionPackage.IsCompatible(item.Manifest,readerVersion), "Extension incompatible.");
        if (enabled && item.Package.Theme is not null)
            ExtensionPackage.Require(!List().Items.Any(other => other.Manifest.Id != id && other.Package.Theme is not null && CanRun(other)),
                "Désactivez d’abord le thème d’extension déjà actif.");
        return item with { Enabled = enabled, Quarantined = enabled ? false : item.Quarantined };
    });

    internal InstalledExtension Configure(string id, ExtensionSettings settings)
    {
        ExtensionPackage.Require(settings.DecimalPlaces is >= 0 and <= 3, "Le nombre de décimales doit être compris entre 0 et 3.");
        return Change(id,item => item with { Settings = settings });
    }

    internal void Quarantine(ExtensionIdentity identity)
    {
        using var ownership = Lock();
        if (!File.Exists(EntryPath(identity.Id))) return;
        var item = Load(identity.Id);
        if (item.Manifest.Version == identity.Version)
            Save(item with { Enabled = false, Quarantined = true });
    }

    internal void Uninstall(string id)
    {
        using var ownership = Lock();
        File.Delete(EntryPath(id));
    }

    internal bool CanRun(InstalledExtension item) => item.Enabled && !item.Quarantined && ExtensionPackage.IsCompatible(item.Manifest, readerVersion);

    InstalledExtension Change(string id, Func<InstalledExtension, InstalledExtension> change)
    {
        using var ownership = Lock();
        var result = change(Load(id));
        Save(result);
        return result;
    }

    InstalledExtension Load(string id)
    {
        var stored = PlatformFiles.Read<StoredExtension>(EntryPath(id));
        ExtensionPackage.Require(stored is { Schema: 1, Archive: not null, Settings.DecimalPlaces: >= 0 and <= 3 }
            && ExtensionPackage.HashMatches(stored.Archive,stored.ArchiveSha256), "Fiche locale de l’extension endommagée.");
        var package = ExtensionPackage.Read(stored.Archive,readerVersion,checkCompatibility:false);
        ExtensionPackage.Require(package.Manifest.Id == id, "Identifiant local incohérent.");
        return new InstalledExtension(package,stored.Enabled,stored.Quarantined,stored.Settings,stored.DownloadSource,stored.ArchiveSha256);
    }

    void Save(InstalledExtension item) => PlatformFiles.Write(EntryPath(item.Manifest.Id),
        new StoredExtension(1,item.Package.Archive,item.ArchiveSha256,item.Enabled,item.Quarantined,item.Settings,item.DownloadSource));

    string EntryPath(string id)
    {
        ExtensionPackage.Require(ExtensionPackage.IsId(id), "Identifiant d’extension invalide.");
        return Path.Combine(directory,id + ".json");
    }

    FileStream Lock()
    {
        PlatformFiles.MakePrivateDirectory(directory);
        return new FileStream(Path.Combine(directory,"registry.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
