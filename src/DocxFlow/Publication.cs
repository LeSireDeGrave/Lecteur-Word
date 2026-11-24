using System.Collections.Immutable;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal enum PublicationRetention { Native, Structured, Vector, Raster, Unsupported }
internal enum PublicationPolicy { Compatible, Complete }
internal enum PublicationStatus { Ready, Converted, Reserved, Blocked }
internal sealed record PublicationFeature(string Kind,PublicationRetention Retention,SourceLocation? Source,string Detail);
internal enum PublicationRepresentation { Native, Compatible, Presentation, Residual }
internal sealed record PublicationContent(SemanticKind Kind,SourceLocation Source,PublicationRepresentation Representation);
internal sealed record PublicationRequest(ExportFormat Format,int? FirstPage=null,int? LastPage=null,bool UseCoverImage=false,
    int PdfImageQuality=101,bool IncludePdfProvenance=false,PublicationCover? Cover=null,
    PublicationPolicy Policy=PublicationPolicy.Compatible);
internal sealed class PublicationCover
{
    internal const int MaxBytes=8*1024*1024;
    internal string Mime {get;}
    internal ReadOnlyMemory<byte> Bytes {get;}

    PublicationCover(string mime,byte[] bytes){Mime=mime;Bytes=bytes;}

    internal static PublicationCover Read(byte[] input,CancellationToken token)
    {
        if(input.Length>MaxBytes)throw new InvalidDataException("EXPORT_COVER_SIZE");
        if(SafePng.TryGetRenderableBytes(input,out var png,out _,token))
            return new("image/png",png);
        if(SafeJpeg.TryGetRenderableBytes(input,out var jpeg,out _,out _,out int orientation,out _,token)&&orientation==1)
            return new("image/jpeg",jpeg);
        throw new InvalidDataException("EXPORT_COVER_IMAGE");
    }
}
internal sealed record PublicationProgress(string Phase,int Completed,int Total);
internal sealed record PublicationPlan(string Profile,ImmutableArray<PublicationFeature> Features,ImmutableArray<FlowIssue> Issues)
{
    internal bool Allowed=>!Issues.Any(i=>i.BlocksExport);
    internal ImmutableArray<FlowIssue> Reservations {get;init;}=[];
    internal ImmutableArray<PublicationContent> Contents {get;init;}=[];
    internal PublicationStatus Status=>!Allowed?PublicationStatus.Blocked:!Reservations.IsEmpty?PublicationStatus.Reserved:
        !Issues.IsEmpty||Features.Any(f=>f.Retention is PublicationRetention.Vector or PublicationRetention.Raster)?
            PublicationStatus.Converted:PublicationStatus.Ready;
}
internal sealed record PublicationMetadata(string Title,string? Author,string? Subject,string? Description,
    string? Keywords,string? Language,DateTimeOffset? Created,DateTimeOffset? Modified)
{
    internal SourceLocation? Origin {get;init;}

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
            Value(cp,"keywords"),Value(dc,"language"),Date("created"),Date("modified")){Origin=root?.Source};
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
        if(Invalid(source,request,token) is {} invalid)return invalid;
        return request.Format==ExportFormat.Pdf?PdfPublication.Prepare(source,request,token):
            StructuredPublication.Compile(source,request,token).Plan;
    }

    static PublicationPlan? Invalid(PublicationSource source,PublicationRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!source.Inspection.Manifest.InspectionComplete||source.Semantic?.SnapshotId!=source.SourceHash)
            return Blocked("EXPORT_SOURCE_UNAVAILABLE","Le modèle de publication du document est indisponible.");
        if(request.Format!=ExportFormat.Pdf&&(request.FirstPage is not null||request.LastPage is not null))
            return Blocked("EXPORT_RANGE_FORMAT","La sélection de feuilles est réservée au PDF.");
        if(request.Format!=ExportFormat.Pdf&&(request.PdfImageQuality!=101||request.IncludePdfProvenance)||
            request.PdfImageQuality is <1 or >101)
            return Blocked("EXPORT_PDF_OPTIONS","Les options de qualité et de provenance demandées sont hors profil.");
        if((request.UseCoverImage||request.Cover is not null)&&request.Format!=ExportFormat.Epub3)
            return Blocked("EXPORT_COVER_FORMAT","Le choix d’une couverture est réservé à l’EPUB.");
        if(!Enum.IsDefined(request.Format)||!Enum.IsDefined(request.Policy))
            return Blocked("EXPORT_FORMAT_UNSUPPORTED","Format ou politique de publication inconnus.");
        return null;
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
        if(Invalid(source,request,token) is {} invalid)throw new PublicationRefusal(invalid.Issues);
        var package=request.Format==ExportFormat.Pdf?null:StructuredPublication.Compile(source,request,token);
        var plan=package?.Plan??PdfPublication.Prepare(source,request,token);
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
            else PublicationPackages.WriteEntries(stage,package!.Entries,budget,progress,token);
            progress?.Report(new("verify",0,1));
            if(request.Format==ExportFormat.Pdf)PdfPublication.Verify(stage,source,request,token);
            else PublicationPackages.Verify(stage,package!.Entries,source.Inspection.Manifest.Budgets.MaxOutputBytes,token);
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
