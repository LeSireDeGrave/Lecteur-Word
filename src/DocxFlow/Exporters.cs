using System.Text;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public enum ExportFormat { Pdf, Odt, Epub3, Nova }
public sealed record ExportPreflight(bool Allowed,string Profile,IReadOnlyList<FlowIssue> Issues);

public static class DocumentExports
{
    public static ExportPreflight Preflight(InspectionResult source,ExportFormat format)
    {
        var compatibility=Compatibility(source,format,default);
        if(!compatibility.Allowed)return compatibility;
        using var lease=PageComposer.Compose(source);
        var plan=Publications.Prepare(new(source,lease.Snapshot,"Document"),new(format,Policy:PublicationPolicy.Complete),default);
        return new(plan.Allowed,plan.Profile,compatibility.Issues.Concat(plan.Issues).ToArray());
    }

    /// <summary>Legacy directory entry point. Its original input profile stays stable.
    /// Encoding and verification are shared with the publication window.</summary>
    public static string Export(InspectionResult source,ExportFormat format,string newOutputDirectory,CancellationToken token=default)
    {
        var compatibility=Compatibility(source,format,token);
        if(!compatibility.Allowed)throw new InvalidDataException("EXPORT_PREFLIGHT_BLOCKED");
        var budget=new ExportBudget(source.Manifest.Budgets,token);
        string output=Path.GetFullPath(newOutputDirectory),parent=Path.GetDirectoryName(output)!;
        if(!Directory.Exists(parent)||Path.Exists(output))throw new IOException("Unused temporary output directory required.");
        for(DirectoryInfo? d=new(parent);d is not null;d=d.Parent)
            if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Symbolic output ancestor refused.");
        using var lease=PageComposer.Compose(source,token);
        var publication=new PublicationSource(source,lease.Snapshot,"Document");
        var request=new PublicationRequest(format,Policy:PublicationPolicy.Complete);
        var plan=Publications.Prepare(publication,request,token);
        if(!plan.Allowed)throw new InvalidDataException("EXPORT_PREFLIGHT_BLOCKED");
        string stage=Path.Combine(parent,".lecteur-export-"+Guid.NewGuid().ToString("N"));
        if(OperatingSystem.IsWindows())Directory.CreateDirectory(stage);
        else Directory.CreateDirectory(stage,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string filename="document."+Publications.Extension(format);
        try
        {
            Publications.Write(publication,request,Path.Combine(stage,filename),token:token);
            using(var file=budget.Create(Path.Combine(stage,"inspection.json")))
                System.Text.Json.JsonSerializer.Serialize(file,source.Manifest,ContractJson.Default.InspectionManifest);
            using(var notice=budget.Create(Path.Combine(stage,"qualification.txt")))
                notice.Write(Encoding.UTF8.GetBytes("profile: "+plan.Profile+"\nSource SHA-256: "+publication.SourceHash+"\n"));
            if(format==ExportFormat.Pdf)
                using(var notice=budget.Create(Path.Combine(stage,"FONT-LICENSE.txt")))
                    notice.Write(Encoding.UTF8.GetBytes(Composer.FontLicense));
            if(Directory.EnumerateFiles(stage).Sum(p=>new FileInfo(p).Length)>source.Manifest.Budgets.MaxOutputBytes)
                throw new OutputLimitException("OUTPUT_SIZE_LIMIT");
            token.ThrowIfCancellationRequested();
            Directory.Move(stage,output);
            return Path.Combine(output,filename);
        }
        finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }

    static ExportPreflight Compatibility(InspectionResult source,ExportFormat format,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(format is not (ExportFormat.Pdf or ExportFormat.Odt or ExportFormat.Epub3))
            return new(false,FlowDocument.Profile,[new("EXPORT_FORMAT_UNSUPPORTED",null,
                "Format hors du contrat d’export historique.",false)]);
        if(source.Manifest.Conformance=="strict")
            return new(false,FlowDocument.Profile,[new("EXPORT_STRICT_SOURCE_UNQUALIFIED",null,
                "Source DOCX Strict hors du contrat d’export historique.",false)]);
        var flow=FlowReader.Read(source);
        var issues=flow.Issues.ToList();
        string tableName=SemanticIndex.QName(SemanticIndex.W,"tbl");
        foreach(var table in source.Manifest.Nodes.Where(n=>n.Name==tableName).Take(128))
        {
            token.ThrowIfCancellationRequested();
            issues.Add(new("EXPORT_TABLE_UNQUALIFIED",table.Source,"Tableau hors du contrat d’export historique.",false));
        }
        foreach(var p in flow.Paragraphs.Where(p=>p.Text.Contains('\r')))
            issues.Add(new("LITERAL_CR_NOT_QUALIFIED",p.Source,"Retour CR littéral hors du profil qualifié.",false));
        if(flow.Paragraphs.Any(p=>p.PageBreakAfter))
            issues.Add(new("BREAK_EXPORT_NOT_QUALIFIED",null,"Saut de page hors du contrat d’export historique.",false));
        return new(!issues.Any(i=>i.BlocksExport),FlowDocument.Profile,issues);
    }
}
