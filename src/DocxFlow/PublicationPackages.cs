using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using static LecteurWord.Flow.StructuredPublication;

namespace LecteurWord.Flow;

internal static class PublicationPackages
{
    static readonly XNamespace Manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0",
        Meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0",Dc="http://purl.org/dc/elements/1.1/",
        Opf="http://www.idpf.org/2007/opf",Container="urn:oasis:names:tc:opendocument:xmlns:container";
    static readonly DateTimeOffset ZipDate=new(2000,1,1,0,0,0,TimeSpan.Zero);
    internal sealed record Entry(string Path,byte[] Data,bool Store=false);
    internal static Entry XmlEntry(string name,XElement tree)=>StringEntry(name,Xml(tree));
    internal static Entry StringEntry(string name,string text,bool store=false)=>new(name,Encoding.UTF8.GetBytes(text),store);

    internal static void WriteEntries(string path,IReadOnlyList<Entry> entries,ExportBudget budget,
        IProgress<PublicationProgress>? progress,CancellationToken token)
    {
        using var file=budget.Create(path);
        using var zip=new ZipArchive(file,ZipArchiveMode.Create);
        for(int i=0;i<entries.Count;i++)
        {
            token.ThrowIfCancellationRequested();
            var item=entries[i];
            var entry=zip.CreateEntry(item.Path,item.Store?CompressionLevel.NoCompression:CompressionLevel.Optimal);
            entry.LastWriteTime=ZipDate;
            using(var output=entry.Open())
                for(int written=0;written<item.Data.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    int count=Math.Min(64*1024,item.Data.Length-written);
                    output.Write(item.Data.AsSpan(written,count));
                    written+=count;
                }
            progress?.Report(new("write",i+1,entries.Count));
        }
    }

    internal static void Verify(string path,IReadOnlyList<Entry> expected,long maxOutputBytes,
        CancellationToken token)
    {
        using var file=File.OpenRead(path);
        using var zip=new ZipArchive(file,ZipArchiveMode.Read);
        if(zip.Entries.Count!=expected.Count)throw new InvalidDataException("EXPORT_PACKAGE_INDEX");
        Span<byte> buffer=stackalloc byte[4096];
        for(int i=0;i<expected.Count;i++)
        {
            token.ThrowIfCancellationRequested();
            var actual=zip.Entries[i];
            var item=expected[i];
            if(actual.FullName!=item.Path||actual.Length!=item.Data.Length||item.Store&&actual.CompressedLength!=actual.Length)
                throw new InvalidDataException("EXPORT_PACKAGE_ENTRY");
            using var stream=actual.Open();
            if(item.Path.EndsWith(".xml",StringComparison.Ordinal)||item.Path.EndsWith(".xhtml",StringComparison.Ordinal)||
                item.Path.EndsWith(".opf",StringComparison.Ordinal)||item.Path.EndsWith(".svg",StringComparison.Ordinal))
            {
                using var xml=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,
                    XmlResolver=null,MaxCharactersInDocument=maxOutputBytes});
                var read=XDocument.Load(xml,LoadOptions.PreserveWhitespace);
                var reference=XDocument.Parse(Encoding.UTF8.GetString(item.Data),LoadOptions.PreserveWhitespace);
                if(!XNode.DeepEquals(read,reference))throw new InvalidDataException("EXPORT_PACKAGE_XML_ROUNDTRIP");
            }
            else
            {
                for(int position=0;position<item.Data.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    int length=Math.Min(buffer.Length,item.Data.Length-position);
                    stream.ReadExactly(buffer[..length]);
                    if(!buffer[..length].SequenceEqual(item.Data.AsSpan(position,length)))
                        throw new InvalidDataException("EXPORT_PACKAGE_BINARY_ROUNDTRIP");
                    position+=length;
                }
            }
        }
    }

    internal static List<Entry> Entries(PublicationSource source,StructuredPublication book,XElement[] body,ExportFormat format)=>
        format==ExportFormat.Odt?Odt(source,book,body):Epub3(source,book,body);

    static List<Entry> Odt(PublicationSource source,StructuredPublication book,XElement[] body)
    {
        const string mime="application/vnd.oasis.opendocument.text";
        XElement Root(string kind,params object[] values)=>new(Office+kind,new XAttribute(XNamespace.Xmlns+"office",Office),
            new XAttribute(Office+"version","1.3"),values);
        var result=new List<Entry>{StringEntry("mimetype",mime,true),
            XmlEntry("content.xml",Root("document-content",book.Automatic,new XElement(Office+"body",new XElement(Office+"text",body))))};
        var auxiliaryStyles=new XElement(book.OdtPageDefinitions);
        auxiliaryStyles.Add(book.Automatic.Elements().Select(style=>new XElement(style)));
        result.Add(XmlEntry("styles.xml",Root("document-styles",book.OdtNamedStyles,auxiliaryStyles,book.OdtMasters)));
        var m=book.Metadata;
        result.Add(XmlEntry("meta.xml",Root("document-meta",new XElement(Office+"meta",
            Property(Dc+"title",m.Title),Property(Meta+"initial-creator",m.Author),Property(Dc+"creator",m.Author),
            Property(Dc+"subject",m.Subject),Property(Dc+"description",m.Description),Property(Meta+"keyword",m.Keywords),
            Property(Dc+"language",Language(m)),Property(Meta+"creation-date",Date(m.Created)),Property(Dc+"date",Date(m.Modified)),
            book.OdtDocumentProperties.Select(property=>new XElement(Meta+"user-defined",
                new XAttribute(Meta+"name",property.Key),new XAttribute(Meta+"value-type","string"),property.Value))))));
        result.AddRange(book.Assets.Select(asset=>new Entry(asset.Path,asset.Data)));
        var declarations=new[]{("/",mime),("content.xml","text/xml"),("styles.xml","text/xml"),("meta.xml","text/xml")}
            .Concat(book.Assets.Select(asset=>(asset.Path,asset.Mime)));
        result.Add(XmlEntry("META-INF/manifest.xml",new XElement(Manifest+"manifest",
            new XAttribute(XNamespace.Xmlns+"manifest",Manifest),new XAttribute(Manifest+"version","1.3"),
            declarations.Select(p=>new XElement(Manifest+"file-entry",new XAttribute(Manifest+"full-path",p.Item1),
                new XAttribute(Manifest+"media-type",p.Item2))))));
        return result;
    }

    static List<Entry> Epub3(PublicationSource source,StructuredPublication book,XElement[] body)
    {
        var metadata=book.Metadata;
        string language=Language(metadata);
        XElement Xhtml(string title,params object[] contents)=>new(Html+"html",
            new XAttribute(XNamespace.Xmlns+"epub",Epub),new XAttribute(XNamespace.Xml+"lang",language),new XAttribute("lang",language),
            new XElement(Html+"head",new XElement(Html+"title",title),new XElement(Html+"link",
                new XAttribute("rel","stylesheet"),new XAttribute("href","style.css"))),new XElement(Html+"body",contents));
        var noteLinks=body.Concat(book.Notes.SelectMany(note=>note.Blocks)).SelectMany(b=>b.DescendantsAndSelf(Html+"a"))
            .Where(a=>(string?)a.Attribute(Epub+"type")=="noteref")
            .ToLookup(a=>(string)a.Attribute("href")!,a=>(string)a.Attribute("id")!,StringComparer.Ordinal);
        var notes=book.Notes.Select(n=>new XElement(Html+"aside",new XAttribute(Epub+"type",n.End?"endnote":"footnote"),
            new XAttribute("role",n.End?"note":"doc-footnote"),new XAttribute("id",n.Id),n.Blocks,
            new XElement(Html+"p",Backlinks(n.Id)))).ToArray();
        IEnumerable<XNode> Backlinks(string note)
        {
            var callers=noteLinks["#"+note].ToArray();
            var label=language.StartsWith("fr",StringComparison.Ordinal)?"Retour à l’appel":"Back to note reference";
            for(int index=0;index<callers.Length;index++)
            {
                if(index>0)yield return new XText(" ");
                string suffix=callers.Length==1?"":" "+(index+1).ToString(CultureInfo.InvariantCulture);
                yield return new XElement(Html+"a",new XAttribute("href","#"+callers[index]),
                    new XAttribute("aria-label",label+suffix),"↩"+suffix);
            }
        }
        var chapters=new List<(string Path,string Title,XElement Contents)>();
        if(book.Cover is {} cover)
        {
            string label=language.StartsWith("fr",StringComparison.Ordinal)?"Couverture":"Cover";
            chapters.Add(("cover.xhtml",label,Xhtml(label,new XElement(Html+"section",
                new XAttribute(Epub+"type","cover"),new XElement(Html+"img",new XAttribute("src",cover.Path),
                    new XAttribute("alt",metadata.Title))))));
        }
        int chapterOffset=chapters.Count;
        var pending=new List<XElement>();
        var knownHeadings=book.Headings.ToDictionary(h=>h.Id,h=>(h.Level,h.Title),StringComparer.Ordinal);
        int splitLevel=body.Select(element=>(string?)element.Attribute("id")).OfType<string>()
            .Where(knownHeadings.ContainsKey).Select(id=>knownHeadings[id].Level).DefaultIfEmpty(0).Min();
        string chapterTitle=metadata.Title;
        foreach(var block in body)
        {
            if((string?)block.Attribute("id") is {} id&&knownHeadings.TryGetValue(id,out var heading)&&heading.Level==splitLevel)
            {
                if(pending.Count>0)FinishChapter();
                chapterTitle=heading.Title;
            }
            pending.Add(block);
        }
        if(pending.Count>0||chapters.Count==chapterOffset)FinishChapter();
        if(notes.Length>0)
        {
            const string title="Notes";
            chapters.Add(("notes.xhtml",title,Xhtml(title,new XElement(Html+"section",
                new XElement(Html+"h1",title),notes))));
        }
        var locations=new Dictionary<string,string>(StringComparer.Ordinal);
        var readingOrder=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var chapter in chapters)
            foreach(var id in chapter.Contents.Descendants().Attributes("id"))
            {
                if(!locations.TryAdd(id.Value,chapter.Path))throw new InvalidDataException("EXPORT_EPUB_DUPLICATE_ID");
                readingOrder.Add(id.Value,readingOrder.Count);
            }
        foreach(var chapter in chapters)
            foreach(var link in chapter.Contents.Descendants(Html+"a").Attributes("href").Where(a=>a.Value.StartsWith('#')).ToArray())
            {
                if(!locations.TryGetValue(link.Value[1..],out var target))
                {
                    if((string?)link.Parent?.Attribute(Epub+"type")=="noteref")
                        throw new InvalidDataException("EXPORT_EPUB_NOTE_TARGET");
                    book.Reserve("EXPORT_EPUB_LINK_TARGET",book.SourceForPublicationId(link.Value[1..]),
                        "Le libellé du renvoi reste lisible. Sa destination ne figure pas dans le livre.");
                    link.Parent?.ReplaceWith(new XElement(Html+"span",link.Parent.Nodes()));
                    continue;
                }
                if(target!=chapter.Path)link.SetValue(target+link.Value);
            }

        void FinishChapter()
        {
            int number=chapters.Count-chapterOffset+1;
            string path=number==1?"content.xhtml":$"chapter-{number:000}.xhtml";
            chapters.Add((path,chapterTitle,Xhtml(chapterTitle,new XElement(Html+"section",pending))));
            pending.Clear();
        }
        var navigation=new XElement(Html+"nav",new XAttribute(Epub+"type","toc"),
            new XAttribute("id","toc"),new XElement(Html+"h1",metadata.Title),Contents());
        XElement Contents()
        {
            var root=new XElement(Html+"ol");
            var hierarchy=new Stack<(int Level,XElement Item)>();
            var grouped=book.Headings.Where(h=>locations.ContainsKey(h.Id)).OrderBy(h=>readingOrder[h.Id])
                .ToLookup(h=>locations[h.Id],StringComparer.Ordinal);
            foreach(var chapter in chapters)
            {
                if(!grouped.Contains(chapter.Path))
                {
                    hierarchy.Clear();
                    root.Add(new XElement(Html+"li",new XElement(Html+"a",new XAttribute("href",chapter.Path),chapter.Title)));
                }
                foreach(var heading in grouped[chapter.Path])
                {
                    while(hierarchy.TryPeek(out var current)&&current.Level>=heading.Level)hierarchy.Pop();
                    XElement list=root;
                    if(hierarchy.TryPeek(out var previous))
                    {
                        list=previous.Item.Element(Html+"ol")??new XElement(Html+"ol");
                        if(list.Parent is null)previous.Item.Add(list);
                    }
                    var item=new XElement(Html+"li",new XElement(Html+"a",new XAttribute("href",chapter.Path+"#"+heading.Id),heading.Title));
                    list.Add(item);
                    hierarchy.Push((heading.Level,item));
                }
            }
            return root;
        }
        var package=new XElement(Opf+"package",new XAttribute("version","3.0"),new XAttribute("unique-identifier","uid"),
            new XElement(Opf+"metadata",new XAttribute(XNamespace.Xmlns+"dc",Dc),
                new XElement(Dc+"identifier",new XAttribute("id","uid"),"urn:sha256:"+source.SourceHash),
                Property(Dc+"title",metadata.Title),Property(Dc+"language",language),Property(Dc+"creator",metadata.Author),
                Property(Dc+"subject",metadata.Subject),Property(Dc+"description",metadata.Description),Property(Dc+"date",Date(metadata.Created)),
                new XElement(Opf+"meta",new XAttribute("property","dcterms:modified"),Date(metadata.Modified??metadata.Created??ZipDate))),
            new XElement(Opf+"manifest",chapters.Select((c,i)=>Item("body"+i,c.Path,"application/xhtml+xml")),Item("nav","nav.xhtml","application/xhtml+xml","nav"),
                Item("styles","style.css","text/css"),book.Assets.Select((a,i)=>Item("asset"+i.ToString(CultureInfo.InvariantCulture),a.Path,a.Mime,a==book.Cover?"cover-image":null))),
            new XElement(Opf+"spine",chapters.Select((_,i)=>new XElement(Opf+"itemref",new XAttribute("idref","body"+i)))));
        var entries=new List<Entry>
        {
            StringEntry("mimetype","application/epub+zip",true),
            XmlEntry("META-INF/container.xml",new XElement(Container+"container",new XAttribute("version","1.0"),
                new XElement(Container+"rootfiles",new XElement(Container+"rootfile",new XAttribute("full-path","EPUB/package.opf"),
                    new XAttribute("media-type","application/oebps-package+xml"))))),
            XmlEntry("EPUB/package.opf",package),
            XmlEntry("EPUB/nav.xhtml",Xhtml(metadata.Title,navigation)),
            StringEntry("EPUB/style.css","body { line-height: 1.4; } p, h1, h2, h3, h4, h5, h6 { white-space: pre-wrap; overflow-wrap: anywhere; tab-size: 4; } table { border-collapse: collapse; } img { max-width: 100%; }")
        };
        entries.AddRange(chapters.Select(c=>XmlEntry("EPUB/"+c.Path,c.Contents)));
        entries.AddRange(book.Assets.Select(a=>new Entry("EPUB/"+a.Path,a.Data)));
        return entries;
    }

    static XElement Item(string id,string path,string type,string? properties=null)=>new(Opf+"item",
        new XAttribute("id",id),new XAttribute("href",path),new XAttribute("media-type",type),
        properties is null?null:new XAttribute("properties",properties));
    static XElement? Property(XName name,string? value)=>string.IsNullOrWhiteSpace(value)?null:new(name,value);
    static string? Date(DateTimeOffset? value)=>value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture);
}
