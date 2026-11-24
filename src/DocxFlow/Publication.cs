using System.Collections.Immutable;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal enum PublicationRetention { Native, Structured, Vector, Raster, Unsupported }
internal sealed record PublicationFeature(string Kind,PublicationRetention Retention,SourceLocation? Source,string Detail);
internal sealed record PublicationRequest(ExportFormat Format,int? FirstPage=null,int? LastPage=null,bool UseCoverImage=false);
internal sealed record PublicationProgress(string Phase,int Completed,int Total);
internal sealed record PublicationPlan(string Profile,ImmutableArray<PublicationFeature> Features,ImmutableArray<FlowIssue> Issues)
{
    internal bool Allowed=>!Issues.Any(i=>i.BlocksExport);
}
internal sealed record PublicationMetadata(string Title,string? Author,string? Subject,string? Description,
    string? Keywords,string? Language,DateTimeOffset? Created,DateTimeOffset? Modified)
{
    internal static PublicationMetadata Read(InspectionManifest manifest,string title)
    {
        const string core="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
        const string dc="http://purl.org/dc/elements/1.1/";
        const string terms="http://purl.org/dc/terms/";
        const string cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        var part=manifest.Relationships.Where(r=>r.SourcePart=="/"&&r.Type==core&&r.ResolvedPart is not null)
            .Select(r=>r.ResolvedPart!).Distinct().Take(2).ToArray();
        var nodes=manifest.Nodes.Where(n=>part is [var uri]&&n.Source.PartUri==uri).ToLookup(n=>n.ParentId);
        var root=SemanticIndex.Unique(nodes[null].Where(n=>n.Name=="{"+cp+"}coreProperties"));
        string? Value(string ns,string name)
        {
            if(root is null||SemanticIndex.Unique(nodes[root.Id].Where(n=>n.Name=="{"+ns+"}"+name)) is not {} field||
                nodes[field.Id].Any(n=>n.Kind!="textValue"))return null;
            var text=string.Concat(nodes[field.Id].Select(n=>n.Text));
            return text.Length<=32768&&!string.IsNullOrWhiteSpace(text)?text:null;
        }
        DateTimeOffset? Date(string name)=>DateTimeOffset.TryParse(Value(terms,name),CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,out var date)?date:null;
        return new(Value(dc,"title")??title,Value(dc,"creator"),Value(dc,"subject"),Value(dc,"description"),
            Value(cp,"keywords"),Value(dc,"language"),Date("created"),Date("modified"));
    }
}

/// <summary>Views of one inspected document. The caller owns the lifetime of its shaping and painting session.</summary>
internal sealed class PublicationSource(InspectionResult inspection,PageCompositionSnapshot? snapshot,string title)
{
    internal InspectionResult Inspection {get;}=inspection;
    internal PageCompositionSnapshot? Pagination {get;}=snapshot;
    internal SemanticDocument? Semantic=>Pagination?.Semantic;
    internal PublicationMetadata Metadata {get;}=PublicationMetadata.Read(inspection.Manifest,title);
    internal IReadOnlyList<ComposedPage> Pages=>Pagination?.Composition.Pages??[];
    internal string SourceHash=>Inspection.Manifest.SourceSha256??"";
}

internal static class Publications
{
    internal static string Extension(ExportFormat format)=>format switch
    {
        ExportFormat.Pdf=>"pdf",ExportFormat.Odt=>"odt",ExportFormat.Epub3=>"epub",ExportFormat.Nova=>"nova",
        _=>throw new ArgumentOutOfRangeException(nameof(format))
    };

    internal static PublicationPlan Prepare(PublicationSource source,PublicationRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!source.Inspection.Manifest.InspectionComplete||source.Semantic?.SnapshotId!=source.SourceHash)
            return Blocked("EXPORT_SOURCE_UNAVAILABLE","Le modèle de publication du document est indisponible.");
        if(request.Format!=ExportFormat.Pdf&&(request.FirstPage is not null||request.LastPage is not null))
            return Blocked("EXPORT_RANGE_FORMAT","La sélection de feuilles est réservée au PDF.");
        if(request.UseCoverImage&&request.Format!=ExportFormat.Epub3)
            return Blocked("EXPORT_COVER_FORMAT","Le choix d’une couverture est réservé à l’EPUB.");
        return request.Format switch
        {
            ExportFormat.Pdf=>PdfPublication.Prepare(source,request,token),
            ExportFormat.Odt or ExportFormat.Epub3 or ExportFormat.Nova=>StructuredPublication.Prepare(source,request.Format,token,request.UseCoverImage),
            _=>Blocked("EXPORT_FORMAT_UNSUPPORTED","Format de publication inconnu.")
        };
    }

    internal static PublicationPlan Blocked(string code,string detail)=>new("unavailable",[],
        [new(code,null,detail,false)]);

    internal static (int First,int Last)? Range(PublicationSource source,PublicationRequest request)
    {
        int first=request.FirstPage??1,last=request.LastPage??source.Pages.Count;
        return first>=1&&last>=first&&last<=source.Pages.Count?(first,last):null;
    }

    internal static string Write(PublicationSource source,PublicationRequest request,string destination,bool overwrite=false,
        IProgress<PublicationProgress>? progress=null,CancellationToken token=default)
    {
        var plan=Prepare(source,request,token);
        if(!plan.Allowed)throw new PublicationRefusal(plan.Issues);
        string path=Path.GetFullPath(destination);
        if(!Path.GetExtension(path).Equals("."+Extension(request.Format),StringComparison.OrdinalIgnoreCase))
            throw new IOException("EXPORT_DESTINATION_EXTENSION");
        string directory=Path.GetDirectoryName(path)??throw new IOException("EXPORT_DESTINATION");
        if(!Directory.Exists(directory))throw new DirectoryNotFoundException(directory);
        for(DirectoryInfo? d=new(directory);d is not null;d=d.Parent)
            if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("EXPORT_SYMLINK");
        var target=new FileInfo(path);
        if(target.LinkTarget is not null||Directory.Exists(path))throw new IOException("EXPORT_DESTINATION");
        if(target.Exists&&!overwrite)throw new IOException("EXPORT_DESTINATION_EXISTS");
        var original=target.Exists?(target.Length,target.LastWriteTimeUtc,target.CreationTimeUtc):((long,DateTime,DateTime)?)null;
        string stage=Path.Combine(directory,".lecteur-export-"+Guid.NewGuid().ToString("N")+".tmp");
        var budget=new ExportBudget(source.Inspection.Manifest.Budgets,token);
        try
        {
            if(request.Format==ExportFormat.Pdf)PdfPublication.Write(stage,source,request,budget,progress,token);
            else StructuredPublication.Write(stage,source,request.Format,budget,progress,token,request.UseCoverImage);
            progress?.Report(new("verify",0,1));
            if(request.Format==ExportFormat.Pdf)PdfPublication.Verify(stage,source,request,token);
            else StructuredPublication.Verify(stage,source,request.Format,token,request.UseCoverImage);
            budget.Check(0);
            target.Refresh();
            if(target.LinkTarget is not null||original is {} known&&
                (!target.Exists||known!=(target.Length,target.LastWriteTimeUtc,target.CreationTimeUtc)))
                throw new IOException("EXPORT_DESTINATION_CHANGED");
            File.Move(stage,path,overwrite:original is not null);
            return path;
        }
        finally
        {
            if(File.Exists(stage))File.Delete(stage);
        }
    }

    internal static string? SafeUrl(string? target)=>target is { Length:>0 and <=4096 }&&
        Uri.TryCreate(target,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http" or "mailto"?
        uri.AbsoluteUri:null;
}

internal sealed class PublicationRefusal(IReadOnlyList<FlowIssue> issues):IOException("EXPORT_PREFLIGHT_BLOCKED")
{
    internal IReadOnlyList<FlowIssue> Issues {get;}=issues;
}
