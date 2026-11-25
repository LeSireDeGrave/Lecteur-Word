using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LecteurWord;
using LecteurWord.Cli;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

const int contractVersion=BuildReference.CliContractVersion;
const string protocol="lecteur-word-convert";
const int Usage=64, Refused=65, Internal=70, Io=74, Cancelled=130;
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
Console.OutputEncoding=new UTF8Encoding(false);
PublicationPlan? plan=null;
InspectionManifest? manifest=null;

if(args is ["capabilities"] or ["--help"] or ["--version"])
{
    Event("capabilities",new
    {
        applicationVersion=BuildReference.Current.Version,
        engineVersion=BuildReference.EngineVersion,
        build=BuildReference.Current.TechnicalIdentity(),
        sourceFormats=new[]{"docx"},
        formats=new[]{"pdf","odt","epub3","nova"},
        policies=new[]{"compatible","complete"},
        novaVersion=NovaPublication.Version,
        novaVersions=BuildReference.NovaVersions,
        usage="lecteur-word-cli convert --source <docx> --format <pdf|odt|epub3|nova> --output <file> [options]",
        options=new[]{$"--contract-version {contractVersion}",$"--nova-version {NovaPublication.Version}","--policy compatible|complete","--overwrite","--from-page n","--to-page n",
            "--pdf-image-quality 1..101","--pdf-provenance","--cover <file>","--cover-first-image"}
    });
    return 0;
}
if(args.Length==0||args[0]!="convert")return Failure(Usage,"CLI_USAGE");

using var cancellation=new CancellationTokenSource();
var token=cancellation.Token;
using var interrupt=OperatingSystem.IsWindows()?null:PosixSignalRegistration.Create(PosixSignal.SIGINT,Cancel);
using var terminate=OperatingSystem.IsWindows()?null:PosixSignalRegistration.Create(PosixSignal.SIGTERM,Cancel);
if(OperatingSystem.IsWindows())Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};

try
{
    var cli=CommandLine.Parse(args);
    if(!File.Exists(cli.Source))return Failure(Io,"CLI_SOURCE_UNAVAILABLE");
    string actualSource=PhysicalPath(new FileInfo(cli.Source));
    if(cli.Overwrite&&File.Exists(cli.Destination)&&
        string.Equals(PhysicalPath(new FileInfo(cli.Destination)),actualSource,
            OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
        return Failure(Io,"CLI_SOURCE_IS_DESTINATION");
    var request=cli.Request;
    if(cli.CoverPath is {} coverPath)
    {
        Progress("cover",0,1);
        using var cover=File.OpenRead(coverPath);
        if(cover.Length>PublicationCover.MaxBytes)return Failure(Refused,"EXPORT_COVER_SIZE");
        byte[] data=new byte[PublicationCover.MaxBytes+1];
        int length=0;
        while(length<data.Length)
        {
            token.ThrowIfCancellationRequested();
            int count=cover.Read(data.AsSpan(length,Math.Min(64*1024,data.Length-length)));
            if(count==0)break;
            length+=count;
        }
        request=request with {Cover=PublicationCover.Read(data.AsSpan(0,length).ToArray(),token)};
        Progress("cover",1,1);
    }

    Progress("inspect",0,1);
    var inspected=DocumentInspector.InspectDocument(actualSource,cancellationToken:token);
    manifest=inspected.Manifest;
    token.ThrowIfCancellationRequested();
    if(!manifest.InspectionComplete)
    {
        string code=manifest.Diagnostics.LastOrDefault()?.Code??"CLI_SOURCE_REFUSED";
        return Failure(code is "IO_ERROR" or "ACCESS_REFUSED"?Io:Refused,code,details:manifest.Diagnostics);
    }
    Progress("inspect",1,1);
    Progress("compose",0,1);
    ReadableReader.Read(inspected,token,out var reading);
    using var composition=PageComposer.Compose(inspected,token,prepareReadingTables:true,reading);
    Progress("compose",1,1);
    var input=new PublicationSource(inspected,composition.Snapshot,Path.GetFileNameWithoutExtension(cli.Source));

    Progress("preflight",0,1);
    var prepared=Publications.PrepareWrite(input,request,token);
    plan=prepared.Plan;
    Progress("preflight",1,1);
    Event("preflight",new{allowed=plan.Allowed,status=Status(plan.Status),profile=plan.Profile,
        issues=plan.Issues,reservations=plan.Reservations});
    if(!plan.Allowed)return Failure(Refused,"EXPORT_PREFLIGHT_BLOCKED",details:plan.Issues);

    string destination=Publications.Write(prepared,cli.Destination,cli.Overwrite,new ExportProgress(Progress),token);
    Event("result",new
    {
        exitCode=0,code="OK",status=Status(plan.Status),output=destination,profile=plan.Profile,
        sourceSha256=input.SourceHash,pages=input.Pages.Count,issues=plan.Issues,reservations=plan.Reservations,
        inspectionDiagnostics=manifest.Diagnostics
    });
    return 0;
}
catch(UsageError ex){return Failure(Usage,ex.Message,ex.Option);}
catch(OperationCanceledException){return Failure(Cancelled,"CLI_CANCELLED");}
catch(PublicationRefusal ex){return Failure(Refused,"EXPORT_PREFLIGHT_BLOCKED",details:ex.Issues);}
catch(InvalidDataException ex){return Failure(Refused,ex.Message.StartsWith("EXPORT_",StringComparison.Ordinal)?ex.Message:"CLI_INVALID_DATA");}
catch(UnauthorizedAccessException){return Failure(Io,"CLI_ACCESS_REFUSED");}
catch(IOException ex){return Failure(Io,ex.Message.StartsWith("EXPORT_",StringComparison.Ordinal)?ex.Message:"CLI_IO_ERROR");}
catch(ArgumentException){return Failure(Usage,"CLI_INVALID_PATH_OR_OPTION");}
catch(Exception ex){return Failure(Internal,"CLI_INTERNAL_ERROR",details:new{exception=ex.GetType().Name});}

void Event(string kind,object payload)=>Console.WriteLine(JsonSerializer.Serialize(new{protocol,contractVersion,eventType=kind,payload},json));
void Progress(string phase,int completed,int total)=>Event("progress",new{phase,completed,total});
int Failure(int exitCode,string code,string? option=null,object? details=null)
{
    Event("result",new{exitCode,code,status=exitCode==Cancelled?"cancelled":exitCode==Refused?"refused":"error",
        option,profile=plan?.Profile,details,issues=plan?.Issues,reservations=plan?.Reservations,
        inspectionDiagnostics=manifest?.Diagnostics});
    return exitCode;
}
void Cancel(PosixSignalContext context){context.Cancel=true;cancellation.Cancel();}
static string Status(PublicationStatus status)=>status.ToString().ToLowerInvariant();
static string PhysicalPath(FileSystemInfo entry)
{
    var target=entry.ResolveLinkTarget(returnFinalTarget:true)??entry;
    string? parent=Path.GetDirectoryName(target.FullName);
    return parent is null?target.FullName:Path.Combine(PhysicalPath(new DirectoryInfo(parent)),target.Name);
}

internal sealed class ExportProgress(Action<string,int,int> report):IProgress<PublicationProgress>
{
    public void Report(PublicationProgress value)=>report(value.Phase,value.Completed,value.Total);
}
