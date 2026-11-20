using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LecteurWord;

/// <summary>Immutable identity captured by MSBuild; no Git or source directory is needed at runtime.</summary>
public sealed class BuildReference
{
    const string Prefix="LecteurWord.Build.";
    public static BuildReference Current { get; }=FromMetadata(typeof(BuildReference).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Where(a=>a.Value is not null)
        .GroupBy(a=>a.Key).ToDictionary(g=>g.Key,g=>g.Last().Value!));

    public string? Version { get; }
    public DateTimeOffset? CompilationUtc { get; }
    public string? Commit { get; }
    public bool? HasLocalChanges { get; }
    public string? BuildTarget { get; }
    public string RuntimePlatform=>RuntimeInformation.OSDescription+" / "+RuntimeInformation.ProcessArchitecture;
    public string RuntimeFramework=>RuntimeInformation.FrameworkDescription;

    BuildReference(string? version,DateTimeOffset? utc,string? commit,bool? dirty,string? target)
        =>(Version,CompilationUtc,Commit,HasLocalChanges,BuildTarget)=(version,utc,commit,dirty,target);

    public static BuildReference FromMetadata(IReadOnlyDictionary<string,string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        string? Get(string key)=>metadata.TryGetValue(Prefix+key,out var value)&&!string.IsNullOrWhiteSpace(value)&&value!="unavailable"?value:null;
        string? version=Get("Version"),commit=Get("Commit");
        const string numeric="(?:0|[1-9][0-9]*)";
        const string prerelease="(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)";
        if(version is not null&&!Regex.IsMatch(version,@"\A"+numeric+@"\."+numeric+@"\."+numeric+"(?:-"+prerelease+"(?:\\."+prerelease+")*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z",RegexOptions.CultureInvariant))version=null;
        if(commit is not null&&!Regex.IsMatch(commit,@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z",RegexOptions.CultureInvariant))commit=null;
        DateTimeOffset? utc=null;
        // Explicit UTC is required: a missing offset must not be silently interpreted in the machine timezone.
        string? date=Get("Utc");
        if(date is not null&&DateTimeOffset.TryParseExact(date,["yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var parsed))utc=parsed;
        bool? dirty=bool.TryParse(Get("Dirty"),out var changes)?changes:null;
        return new BuildReference(version,utc,commit,dirty,Get("Target"));
    }

    public string CompactReference(bool english=false)
    {
        string version=Version is null?(english?"version unavailable":"version indisponible"):"v"+Version;
        string commit=Commit is null?(english?"commit unavailable":"commit indisponible"):Commit[..12];
        string dirty=HasLocalChanges==true?" +":HasLocalChanges is null?" ?":"";
        return version+" · "+commit+dirty;
    }

    public string FullReference(bool english=false,TimeZoneInfo? displayZone=null)
    {
        string Missing()=>english?"unavailable":"indisponible";
        string utc=CompilationUtc?.ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'",CultureInfo.InvariantCulture)??Missing();
        string local=CompilationUtc is { } date?TimeZoneInfo.ConvertTime(date,displayZone??TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture):Missing();
        string dirty=HasLocalChanges switch{true=>english?"yes":"oui",false=>english?"no":"non",_=>Missing()};
        return string.Join(Environment.NewLine,
            "Lecteur Word "+(Version??Missing()),
            (english?"Build (UTC): ":"Compilation (UTC) : ")+utc,
            (english?"Build (local): ":"Compilation (locale) : ")+local,
            (english?"Git commit: ":"Commit Git : ")+(Commit??Missing()),
            (english?"Uncommitted local changes: ":"Modifications locales non committées : ")+dirty,
            (english?"Build target: ":"Cible de compilation : ")+(BuildTarget??Missing()),
            (english?"Runtime platform: ":"Plateforme d’exécution : ")+RuntimePlatform,
            (english?".NET runtime: ":"Runtime .NET : ")+RuntimeFramework);
    }
}
