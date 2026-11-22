using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;
public enum ExportFormat { Pdf, Odt, Epub3 }
public sealed record ExportPreflight(bool Allowed,string Profile,IReadOnlyList<FlowIssue> Issues);
public static class DocumentExports
{
    public static ExportPreflight Preflight(InspectionResult source,ExportFormat format) =>
        Prepare(source,format,default,out _,out _);

    static ExportPreflight Prepare(InspectionResult source,ExportFormat format,CancellationToken token,
        out FlowDocument? flow,out Composition? composition)
    {
        token.ThrowIfCancellationRequested();
        flow=null;
        composition=null;
        if(format is not (ExportFormat.Pdf or ExportFormat.Odt or ExportFormat.Epub3))
            return new(false,FlowDocument.Profile,[new("EXPORT_FORMAT_UNSUPPORTED",null,
                "Format d’export hors profil / Unsupported export format",false)]);
        flow=FlowReader.Read(source);
        var issues=flow.Issues.ToList();
        string tableName=SemanticIndex.QName(SemanticIndex.W,"tbl");
        foreach(var table in source.Manifest.Nodes.Where(n=>n.Name==tableName).Take(128))
        {
            token.ThrowIfCancellationRequested();
            issues.Add(new("EXPORT_TABLE_UNQUALIFIED",table.Source,
                "Tableau DOCX hors profil PDF, ODT et EPUB3 ; export bloqué / DOCX table outside the PDF, ODT and EPUB3 export profile",false));
        }
        foreach(var p in flow.Paragraphs.Where(p=>p.Text.Contains('\r')))
            issues.Add(new("LITERAL_CR_NOT_QUALIFIED",p.Source,"Retour CR littéral hors profil / Literal CR outside export profile",false));
        if(flow.Paragraphs.Any(p=>p.PageBreakAfter))issues.Add(new("BREAK_EXPORT_NOT_QUALIFIED",null,"Sauts de page hors profil export / Page breaks outside export profile",false));
        if(format==ExportFormat.Pdf && !issues.Any(i=>i.BlocksExport))
        {
            composition=Composer.Compose(flow,token);
            foreach(var issue in composition.Issues)
                if(!issues.Contains(issue))issues.Add(issue);
            if(!composition.Available)issues.Add(new("NO_COMPOSED_PAGES",null,"Aucune page qualifiée / No qualified page composition",true));
        }
        return new(!issues.Any(i=>i.BlocksExport),FlowDocument.Profile,issues);
    }
    // Always creates a new directory with the export and a versioned inspection manifest together.
    public static string Export(InspectionResult source,ExportFormat format,string newOutputDirectory,CancellationToken token=default)
    {
        var check=Prepare(source,format,token,out var flow,out var composition);
        if(!check.Allowed||flow is null)throw new InvalidDataException("EXPORT_PREFLIGHT_BLOCKED");
        var budget=new ExportBudget(source.Manifest.Budgets,token);
        string output=Path.GetFullPath(newOutputDirectory),parent=Path.GetDirectoryName(output)!;
        if(!Directory.Exists(parent)||Path.Exists(output))throw new IOException("Unused temporary output directory required.");
        for(DirectoryInfo? d=new(parent);d is not null;d=d.Parent)
            if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Symbolic output ancestor refused.");
        string stage=Path.Combine(parent,".lecteur-export-"+Guid.NewGuid().ToString("N"));
        if(OperatingSystem.IsWindows())Directory.CreateDirectory(stage);else Directory.CreateDirectory(stage,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string filename="document."+(format==ExportFormat.Epub3?"epub":format.ToString().ToLowerInvariant());
        try
        {
            token.ThrowIfCancellationRequested();
            string path=Path.Combine(stage,filename);
            switch(format)
            {
                case ExportFormat.Pdf: Pdf(path,composition!,token,budget); break;
                case ExportFormat.Odt: Odt(path,flow,token,budget); break;
                case ExportFormat.Epub3: Epub(path,flow,token,budget); break;
            }
            using(var file=budget.Create(Path.Combine(stage,"inspection.json")))
                System.Text.Json.JsonSerializer.Serialize(file,source.Manifest,ContractJson.Default.InspectionManifest);
            using(var notice=budget.Create(Path.Combine(stage,"qualification.txt")))notice.Write(Encoding.UTF8.GetBytes("profile: "+FlowDocument.Profile+"\nSource SHA-256: "+flow.SourceHash+"\nComposition expérimentale, sans identité Word. EPUB3 reflowable.\n"));
            if(format==ExportFormat.Pdf)
                using(var notice=budget.Create(Path.Combine(stage,"FONT-LICENSE.txt")))notice.Write(Encoding.UTF8.GetBytes(Composer.FontLicense));
            if(Directory.EnumerateFiles(stage).Sum(p=>new FileInfo(p).Length)>source.Manifest.Budgets.MaxOutputBytes)throw new OutputLimitException("OUTPUT_SIZE_LIMIT");
            token.ThrowIfCancellationRequested();Directory.Move(stage,output);return Path.Combine(output,filename);
        }
        finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
    static void Pdf(string path,Composition composition,CancellationToken token,ExportBudget budget)
    {
        // Native PDF callbacks defer managed write failures until control returns to managed code.
        using var output=budget.Create(path,deferFailures:true);
        using var document=SKDocument.CreatePdf(output,new SKDocumentPdfMetadata{Title="DOCX — composition expérimentale",Creator="Lecteur Word",Subject=Composer.FontLicense,RasterDpi=144});
        foreach(var page in composition.Pages)
        {token.ThrowIfCancellationRequested();var canvas=document.BeginPage(Composition.Width,Composition.Height);Composer.DrawPage(canvas,page);document.EndPage();}
        document.Close();budget.ThrowIfFailed();output.Flush();
    }
    static void Entry(ZipArchive zip,string path,string value,bool stored=false)
    {
        var entry=zip.CreateEntry(path,stored?CompressionLevel.NoCompression:CompressionLevel.Optimal);
        entry.LastWriteTime=new DateTimeOffset(2026,11,20,0,0,0,TimeSpan.Zero);
        using var stream=entry.Open();stream.Write(Encoding.UTF8.GetBytes(value));
    }
    static string Xml(XElement element)=>new XDocument(new XDeclaration("1.0","utf-8",null),element).ToString(SaveOptions.DisableFormatting);
    static readonly XNamespace Office="urn:oasis:names:tc:opendocument:xmlns:office:1.0",Text="urn:oasis:names:tc:opendocument:xmlns:text:1.0",Style="urn:oasis:names:tc:opendocument:xmlns:style:1.0",Fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0",Manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    static XElement OdtParagraph(FlowParagraph p)
    {
        var element=new XElement(Text+"p");
        var buffer=new StringBuilder();void Flush(){if(buffer.Length>0){element.Add(new XText(buffer.ToString()));buffer.Clear();}}
        for(int i=0;i<p.Text.Length;i++)
        {
            char c=p.Text[i];
            if(c==' '){Flush();int count=1;while(i+1<p.Text.Length&&p.Text[i+1]==' '){i++;count++;}element.Add(new XElement(Text+"s",new XAttribute(Text+"c",count)));}
            else if(c=='\n'){Flush();element.Add(new XElement(Text+"line-break"));}
            else if(c=='\t'){Flush();element.Add(new XElement(Text+"tab"));}else buffer.Append(c);
        }
        Flush();return element;
    }
    static void Odt(string path,FlowDocument flow,CancellationToken token,ExportBudget budget)
    {
        using var file=budget.Create(path);using var zip=new ZipArchive(file,ZipArchiveMode.Create);
        Entry(zip,"mimetype","application/vnd.oasis.opendocument.text",true);
        var paragraphs=new List<XElement>();foreach(var p in flow.Paragraphs){token.ThrowIfCancellationRequested();paragraphs.Add(OdtParagraph(p));}
        Entry(zip,"content.xml",Xml(new XElement(Office+"document-content",new XAttribute(XNamespace.Xmlns+"office",Office),new XAttribute(XNamespace.Xmlns+"text",Text),new XAttribute(Office+"version","1.3"),new XElement(Office+"body",new XElement(Office+"text",paragraphs)))));
        Entry(zip,"styles.xml",Xml(new XElement(Office+"document-styles",new XAttribute(XNamespace.Xmlns+"office",Office),new XAttribute(Office+"version","1.3"),new XElement(Office+"styles"))));
        Entry(zip,"META-INF/manifest.xml",Xml(new XElement(Manifest+"manifest",new XAttribute(XNamespace.Xmlns+"manifest",Manifest),new XAttribute(Manifest+"version","1.3"),new[]{("/","application/vnd.oasis.opendocument.text"),("content.xml","text/xml"),("styles.xml","text/xml")}.Select(p=>new XElement(Manifest+"file-entry",new XAttribute(Manifest+"full-path",p.Item1),new XAttribute(Manifest+"media-type",p.Item2))))));
    }
    static void Epub(string path,FlowDocument flow,CancellationToken token,ExportBudget budget)
    {
        XNamespace html="http://www.w3.org/1999/xhtml",epub="http://www.idpf.org/2007/ops",opf="http://www.idpf.org/2007/opf",dc="http://purl.org/dc/elements/1.1/";
        using var file=budget.Create(path);using var zip=new ZipArchive(file,ZipArchiveMode.Create);
        Entry(zip,"mimetype","application/epub+zip",true);
        Entry(zip,"META-INF/container.xml","<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"EPUB/package.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        var paragraphs=new List<XElement>();int index=0;foreach(var p in flow.Paragraphs){token.ThrowIfCancellationRequested();paragraphs.Add(new XElement(html+"p",new XAttribute("id","p"+index++),p.Text));}
        XElement Head(string title)=>new(html+"head",new XElement(html+"title",title),new XElement(html+"link",new XAttribute("rel","stylesheet"),new XAttribute("href","style.css")));
        XElement Html(XElement head,XElement body)=>new(html+"html",new XAttribute(XNamespace.Xmlns+"epub",epub),new XAttribute(XNamespace.Xml+"lang","und"),new XAttribute("lang","und"),head,body);
        Entry(zip,"EPUB/content.xhtml",Xml(Html(Head("Document"),new XElement(html+"body",paragraphs))));
        Entry(zip,"EPUB/nav.xhtml",Xml(Html(Head("Navigation"),new XElement(html+"body",new XElement(html+"nav",new XAttribute(epub+"type","toc"),new XAttribute("id","toc"),new XElement(html+"h1","Document"),new XElement(html+"ol",new XElement(html+"li",new XElement(html+"a",new XAttribute("href","content.xhtml"),"Document"))))))));
        Entry(zip,"EPUB/style.css","body { font-family: serif; } p { white-space: pre-wrap; overflow-wrap: anywhere; }\n");
        Entry(zip,"EPUB/package.opf",Xml(new XElement(opf+"package",new XAttribute("version","3.0"),new XAttribute("unique-identifier","book-id"),
            new XElement(opf+"metadata",new XAttribute(XNamespace.Xmlns+"dc",dc),new XElement(dc+"identifier",new XAttribute("id","book-id"),"urn:sha256:"+flow.SourceHash),new XElement(dc+"title","Document"),new XElement(dc+"language","und"),new XElement(opf+"meta",new XAttribute("property","dcterms:modified"),DateTime.UtcNow.ToString("yyyy-MM-dd\u0027T\u0027HH:mm:ss\u0027Z\u0027",System.Globalization.CultureInfo.InvariantCulture))),
            new XElement(opf+"manifest",new XElement(opf+"item",new XAttribute("id","content"),new XAttribute("href","content.xhtml"),new XAttribute("media-type","application/xhtml+xml")),new XElement(opf+"item",new XAttribute("id","nav"),new XAttribute("href","nav.xhtml"),new XAttribute("media-type","application/xhtml+xml"),new XAttribute("properties","nav")),new XElement(opf+"item",new XAttribute("id","css"),new XAttribute("href","style.css"),new XAttribute("media-type","text/css"))),new XElement(opf+"spine",new XElement(opf+"itemref",new XAttribute("idref","content"))))));
    }
}
