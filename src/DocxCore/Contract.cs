using System.Text.Json;
using System.Text.Json.Serialization;

namespace LecteurWord.DocxCore;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InspectionOptions(
    long MaxZipBytes = 32 * 1024 * 1024,
    int MaxEntries = 2048,
    long MaxExpandedBytes = 128 * 1024 * 1024,
    int MaxPartBytes = 16 * 1024 * 1024,
    int MaxMediaBytes = 8 * 1024 * 1024,
    int MaxMedia = 256,
    long MaxImagePixels = 40_000_000,
    int MaxExpansionRatio = 200,
    int MaxDepth = 64,
    int MaxNodes = 150_000,
    int MaxAttributesPerNode = 64,
    int MaxValueChars = 256 * 1024,
    long MaxAllocatedBytes = 512L * 1024 * 1024,
    long MaxWorkingSetBytes = 768L * 1024 * 1024,
    long MaxOutputBytes = 128L * 1024 * 1024,
    int MaxDurationMilliseconds = 30_000)
{
    internal void Validate()
    {
        if (MaxZipBytes is < 1 or > int.MaxValue || MaxEntries is < 1 or > 10_000 ||
            MaxExpandedBytes < 1 || MaxPartBytes < 1 || MaxMediaBytes < 1 || MaxMedia < 1 ||
            MaxImagePixels < 1 || MaxExpansionRatio < 1 || MaxDepth is < 1 or > 128 ||
            MaxNodes < 1 || MaxAttributesPerNode < 1 || MaxValueChars < 1 || MaxAllocatedBytes < 1 ||
            MaxWorkingSetBytes < 1 || MaxOutputBytes < 1 || MaxDurationMilliseconds < 1)
            throw new ArgumentException("Invalid inspection budgets.");
    }
}

// Wire values are intentionally explicit, independent of CLR enum naming.
public static class Coverage
{
    public const string Read = "read";
    public const string Inventory = "inventoriedOnly";
    public const string Unrepresented = "notRepresented";
    public const string Refused = "refused";
    public const string Untested = "notTested";
}
public sealed record SourceLocation(string PartUri, string Path);
public sealed record Diagnostic(string Code, string Severity, string Coverage, SourceLocation? Source, string Message);
public sealed record InventoryNode(string Id, string? ParentId, SourceLocation Source, string Name,
    string Kind, string Coverage, SortedDictionary<string, string> Attributes)
{
    public string? Text { get; set; }
    public string Revision { get; set; } = "unchanged";
    public string FieldRole { get; set; } = "none";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceName { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<string,string>? SourceAttributes { get; init; }
}
public sealed record PartInventory(string PartUri, string ContentType, string Sha256, long Bytes,
    string Coverage, int NodeCount);
public sealed record Relationship(string SourcePart, string Id, string Type, string Target,
    string Mode, string? ResolvedPart)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceType { get; init; }
}
public sealed record MediaResource(string Id, string PartUri, string Sha256, long Bytes, string ContentType,
    string Coverage, int? Width, int? Height);
public sealed record StyleChain(string NodeId, string StyleId, List<string> Ancestors, string Coverage);
public sealed record InspectionManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string EngineVersion { get; init; } = "0.1.0";
    public string Format { get; set; } = "unknown";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? Conformance { get; set; }
    public string Status { get; set; } = Coverage.Refused;
    public bool InspectionComplete { get; set; }
    public bool ImportReady { get; init; } = false;
    public string Pagination { get; init; } = Coverage.Untested;
    public string? SourceSha256 { get; set; }
    public InspectionOptions Budgets { get; init; } = new();
    public List<PartInventory> Parts { get; init; } = [];
    public List<InventoryNode> Nodes { get; init; } = [];
    public List<Relationship> Relationships { get; init; } = [];
    public List<MediaResource> Media { get; init; } = [];
    public List<StyleChain> Styles { get; init; } = [];
    public List<Diagnostic> Diagnostics { get; init; } = [];
}
public sealed record InspectionResult(InspectionManifest Manifest, IReadOnlyDictionary<string, byte[]> SafeMedia)
{
    // Quarantined font parts from the inspected snapshot. Never emitted as CLI assets.
    [JsonIgnore] public IReadOnlyDictionary<string,byte[]> FontParts {get;init;} = new Dictionary<string,byte[]>();
    // Uninterpreted, bounded bytes. They never enter the CLI asset set.
    [JsonIgnore] public IReadOnlyDictionary<string,byte[]> MetafileParts {get;init;} = new Dictionary<string,byte[]>();
}

[JsonSerializable(typeof(InspectionManifest))]
[JsonSerializable(typeof(InspectionOptions))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
public partial class ContractJson : JsonSerializerContext;

public static class ManifestJson
{
    public static byte[] Serialize(InspectionManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, ContractJson.Default.InspectionManifest);
}
