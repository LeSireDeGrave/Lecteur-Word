using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LecteurWord.Platform;

internal enum SupportKind { Bug, Feature, Extension }
internal sealed record CommunityConfiguration(string? SupportRepository, string? ReleasesRepository, string? CatalogUrl)
{
    internal static CommunityConfiguration Empty { get; } = new(null, null, null);
    public bool CatalogOffline { get; init; }
}

internal sealed class GitHubSupportService(CommunityConfiguration configuration)
{
    internal Uri? IssueUrl(SupportKind kind)
    {
        string template = kind switch
        {
            SupportKind.Bug => "bug.yml", SupportKind.Feature => "feature.yml", SupportKind.Extension => "extension.yml",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return RepoUrl(configuration.SupportRepository, "/issues/new?template=" + template);
    }

    internal Uri? VersionsUrl => RepoUrl(configuration.ReleasesRepository ?? configuration.SupportRepository, "/releases");
    internal Uri? ContributeUrl => RepoUrl(configuration.SupportRepository, "");

    static Uri? RepoUrl(string? slug, string suffix) =>
        slug is { Length: <= 150 } && Regex.IsMatch(slug, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) && slug.Split('/')[1] is not ("." or "..")
            ? new Uri("https://github.com/" + slug + suffix) : null;

    internal static CommunityConfiguration Load(PlatformPaths paths) =>
        PlatformFiles.ReadOptional<CommunityConfiguration>(Path.Combine(paths.Config, "community.json")) ?? CommunityConfiguration.Empty;

    internal static void Save(PlatformPaths paths, CommunityConfiguration configuration) =>
        PlatformFiles.Write(Path.Combine(paths.Config, "community.json"), configuration);

    internal static string Draft(SupportKind kind, string steps, string extra, Incident? incident, bool english)
    {
        var build = BuildReference.Current;
        return string.Join(Environment.NewLine,
            $"Lecteur Word {build.Version ?? (english ? "unavailable" : "indisponible")}",
            (english ? "Build (UTC): " : "Build (UTC) : ") + (build.CompilationUtc?.ToString("O") ?? "—"),
            (english ? "Git commit: " : "Commit Git : ") + (build.Commit ?? "—"),
            (english ? "Operating system: " : "Système : ") + build.RuntimePlatform,
            (english ? "Type: " : "Type : ") + KindLabel(kind, english), "",
            english ? "Reproduction steps" : "Étapes de reproduction", steps.Trim(), "",
            english ? "Additional information" : "Informations supplémentaires", extra.Trim(), "",
            english ? "Technical diagnostic" : "Diagnostic technique", incident?.TechnicalText() ?? "—").TrimEnd();
    }

    internal static string KindLabel(SupportKind kind, bool english) => (kind, english) switch
    {
        (SupportKind.Bug, false) => "Problème",
        (SupportKind.Feature, false) => "Amélioration",
        (SupportKind.Extension, false) => "Extension",
        (SupportKind.Bug, true) => "Bug",
        (SupportKind.Feature, true) => "Feature request",
        _ => "Extension"
    };

    internal static void OpenBrowser(Uri address)
    {
        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }
}
