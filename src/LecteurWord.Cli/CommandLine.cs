using System.Globalization;
using LecteurWord.Flow;

namespace LecteurWord.Cli;

internal sealed record CommandLine(string Source,string Destination,PublicationRequest Request,string? CoverPath,bool Overwrite)
{
    internal static CommandLine Parse(string[] args)
    {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        var switches=new HashSet<string>(StringComparer.Ordinal);
        for(int position=1;position<args.Length;position++)
        {
            string flag=args[position];
            if(flag is "--overwrite" or "--pdf-provenance" or "--cover-first-image")
            {
                if(!switches.Add(flag))throw new UsageError("CLI_DUPLICATE_OPTION",flag);
                continue;
            }
            if(flag is not ("--source" or "--format" or "--output" or "--policy" or "--contract-version" or "--nova-version" or
                "--from-page" or "--to-page" or "--pdf-image-quality" or "--cover"))
                throw new UsageError("CLI_UNKNOWN_OPTION",flag);
            if(position+1>=args.Length||args[position+1].Length==0)
                throw new UsageError("CLI_MISSING_VALUE",flag);
            if(!values.TryAdd(flag,args[++position]))throw new UsageError("CLI_DUPLICATE_OPTION",flag);
        }

        string expectedContract=BuildReference.CliContractVersion.ToString(CultureInfo.InvariantCulture);
        if(values.GetValueOrDefault("--contract-version",expectedContract)!=expectedContract)
            throw new UsageError("CLI_UNSUPPORTED_CONTRACT","--contract-version");
        string source=Required("--source");
        string destination=Required("--output");
        var format=Required("--format") switch
        {
            "pdf"=>ExportFormat.Pdf,"odt"=>ExportFormat.Odt,"epub3"=>ExportFormat.Epub3,"nova"=>ExportFormat.Nova,
            _=>throw new UsageError("CLI_UNKNOWN_FORMAT","--format")
        };
        if(Number("--nova-version",int.MaxValue) is int novaVersion&&
            (format!=ExportFormat.Nova||novaVersion!=NovaPublication.Version))
            throw new UsageError("CLI_UNSUPPORTED_NOVA_VERSION","--nova-version");
        var policy=values.GetValueOrDefault("--policy","compatible") switch
        {
            "compatible"=>PublicationPolicy.Compatible,"complete"=>PublicationPolicy.Complete,
            _=>throw new UsageError("CLI_UNKNOWN_POLICY","--policy")
        };
        int? first=Number("--from-page",int.MaxValue);
        int? last=Number("--to-page",int.MaxValue);
        int? quality=Number("--pdf-image-quality",101);
        bool provenance=switches.Contains("--pdf-provenance");
        string? cover=values.GetValueOrDefault("--cover");
        bool useFirstImage=switches.Contains("--cover-first-image");
        if((first is not null||last is not null||quality is not null||provenance)&&format!=ExportFormat.Pdf)
            throw new UsageError("CLI_PDF_OPTION_FORMAT","--format");
        if(first>last)throw new UsageError("CLI_INVALID_PAGE_RANGE","--from-page");
        if((cover is not null||useFirstImage)&&format!=ExportFormat.Epub3)
            throw new UsageError("CLI_EPUB_OPTION_FORMAT","--format");
        if(cover is not null&&useFirstImage)
            throw new UsageError("CLI_CONFLICTING_COVER_OPTIONS","--cover");
        if(!Path.GetExtension(destination).Equals("."+Publications.Extension(format),StringComparison.OrdinalIgnoreCase))
            throw new UsageError("CLI_DESTINATION_EXTENSION","--output");
        return new(Path.GetFullPath(source),Path.GetFullPath(destination),new(format,first,last,useFirstImage,
            quality??101,provenance,Policy:policy),cover is null?null:Path.GetFullPath(cover),switches.Contains("--overwrite"));

        string Required(string flag)=>values.TryGetValue(flag,out string? value)?value:
            throw new UsageError("CLI_MISSING_OPTION",flag);
        int? Number(string flag,int max)
        {
            if(!values.TryGetValue(flag,out string? value))return null;
            if(!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int number)||number<1||number>max)
                throw new UsageError("CLI_INVALID_NUMBER",flag);
            return number;
        }
    }
}

internal sealed class UsageError(string code,string option):Exception(code)
{
    internal string Option {get;}=option;
}
