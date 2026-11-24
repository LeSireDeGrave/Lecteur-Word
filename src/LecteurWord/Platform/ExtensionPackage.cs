using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LecteurWord.Platform;

internal sealed record ExtensionCompatibility(string Minimum, string MaximumExclusive);
internal sealed record ExtensionProvenance(string Publisher, string? Source);
internal sealed record ExtensionManifest(string Id, string Name, string Description, string Author, string Version,
    ExtensionCompatibility Compatibility, int ApiVersion, string[] Features, string[] Permissions, string License,
    ExtensionProvenance Provenance, string PayloadSha256)
{
    internal ExtensionIdentity Identity => new(Id, Version);
}

internal sealed record ThemeColors(string Surface, string Accent);
internal sealed record ThemeContribution(ThemeColors Default, ThemeColors Alternate);
internal sealed record MetricExpression(string Label, string[] Expression);
internal sealed record ReadingContribution(MetricExpression[] Metrics);
internal sealed record ExtensionPackage(ExtensionManifest Manifest, ThemeContribution? Theme, ReadingContribution? Reading, byte[] Archive)
{
    internal const int ArchiveLimit = 128 * 1024;
    internal const int Api = 1;
    internal const string ThemeFeature = "appearance.theme";
    internal const string ReadingFeature = "reading.metrics";
    internal const string MetricsPermission = "document.metrics.read";

    internal static ExtensionPackage Read(byte[] archive, Version reader, bool checkCompatibility = true)
    {
        Require(archive.Length <= ArchiveLimit, "Paquet trop volumineux.");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
            Require(zip.Entries.Count == 2 && zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() == 2, "Le paquet doit contenir manifest.json et payload.json.");
            var manifest = Decode<ExtensionManifest>(Entry(zip, "manifest.json"));
            ValidateManifest(manifest);
            if (checkCompatibility) Require(IsCompatible(manifest, reader),
                $"Extension incompatible avec Lecteur Word {reader} ou son API {Api}.");
            var payload = Entry(zip, "payload.json");
            Require(HashMatches(payload, manifest.PayloadSha256), "Intégrité du contenu du paquet invalide.");
            if (!checkCompatibility && manifest.ApiVersion != Api) return new ExtensionPackage(manifest, null, null, archive);
            if (manifest.Features[0] == ThemeFeature)
            {
                var theme = Decode<ThemeContribution>(payload);
                ValidateColors(theme.Default);
                ValidateColors(theme.Alternate);
                return new ExtensionPackage(manifest, theme, null, archive);
            }
            var reading = Decode<ReadingContribution>(payload);
            ReadingProtocol.Validate(reading);
            return new ExtensionPackage(manifest, null, reading, archive);
        }
        catch (JsonException error) { throw new InvalidDataException("JSON du paquet invalide.", error); }
    }

    internal static bool IsCompatible(ExtensionManifest manifest, Version reader) =>
        manifest.ApiVersion == Api && reader >= StableVersion(manifest.Compatibility.Minimum)
        && reader < StableVersion(manifest.Compatibility.MaximumExclusive);

    static byte[] Entry(ZipArchive zip, string name)
    {
        var entry = zip.Entries.SingleOrDefault(e => e.FullName == name);
        Require(entry is not null && entry.Length <= 32 * 1024 && ((entry.ExternalAttributes >> 16) & 0xF000) != 0xA000,
            $"Entrée {name} absente ou invalide.");
        using var input = entry!.Open();
        return PlatformFiles.ReadBounded(input, 32 * 1024);
    }

    internal static T Decode<T>(ReadOnlySpan<byte> bytes) where T : class =>
        JsonSerializer.Deserialize<T>(bytes, PlatformFiles.Json) ?? throw new InvalidDataException("Objet JSON absent.");

    internal static bool IsId(string? id) => id is { Length: <= 80 } && Regex.IsMatch(id,
        @"\A[a-z][a-z0-9-]*(?:\.[a-z][a-z0-9-]*)+\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static Version StableVersion(string? value)
    {
        if (value is { Length: <= 32 } && Version.TryParse(value, out var version) &&
            version.Build >= 0 && version.Revision < 0 && value == version.ToString(3)) return version;
        throw new InvalidDataException("Version attendue au format majeur.mineur.correctif.");
    }

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool HashMatches(byte[] bytes, string value) =>
        IsHash(value) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(value));
    internal static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.HostNameType == UriHostNameType.Dns && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    internal static void ValidateManifest(ExtensionManifest manifest)
    {
        Require(IsId(manifest.Id) && TextField(manifest.Name, 90) && TextField(manifest.Description, 600) &&
            TextField(manifest.Author, 120) && TextField(manifest.License, 100), "Identité ou fiche de l’extension invalide.");
        _ = StableVersion(manifest.Version);
        Require(manifest.Compatibility is not null && manifest.ApiVersion > 0 &&
            StableVersion(manifest.Compatibility.Minimum) < StableVersion(manifest.Compatibility.MaximumExclusive),
            "Intervalle de compatibilité invalide.");
        Require(manifest.Provenance is not null && TextField(manifest.Provenance.Publisher, 120) &&
            (manifest.Provenance.Source is null || IsHttps(manifest.Provenance.Source)) && IsHash(manifest.PayloadSha256),
            "Provenance ou empreinte absente.");
        Require(manifest.Features is { Length: 1 } && TextField(manifest.Features[0],80) && manifest.Permissions is { Length: <= 8 } &&
            manifest.Permissions.All(p => TextField(p,80)), "Fonctionnalités ou permissions invalides.");
        if (manifest.ApiVersion != Api) return;
        Require(manifest.Features switch
        {
            [ThemeFeature] => manifest.Permissions.Length == 0,
            [ReadingFeature] => manifest.Permissions is [MetricsPermission],
            _ => false
        }, "Fonctionnalité ou permission inconnue pour cette version d’API.");
    }

    internal static bool TextField(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit &&
        !value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);

    static void ValidateColors(ThemeColors? colors)
    {
        Require(colors is not null && Hex(colors.Surface) && Hex(colors.Accent), "Couleur attendue au format #RRGGBB.");
        Require(Luminance(colors!.Surface) >= .62 && Luminance(colors.Accent) <= .14,
            "Le thème exige une surface claire et un accent foncé pour conserver le contraste.");

        static bool Hex(string? text) => text is { Length: 7 } && text[0] == '#' && text[1..].All(Uri.IsHexDigit);
        static double Luminance(string hex)
        {
            var rgb = Convert.FromHexString(hex[1..]);
            return .2126 * Linear(rgb[0]) + .7152 * Linear(rgb[1]) + .0722 * Linear(rgb[2]);
        }
        static double Linear(byte value)
        {
            double channel = value / 255.0;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
    }

    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
