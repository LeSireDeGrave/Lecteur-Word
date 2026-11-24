using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using static LecteurWord.Flow.PublicationPackages;

namespace LecteurWord.Flow;

/// <summary>Strict subset of OpusNova native v27 at 8633f8fb457396c9015304228694afc72ee7ded6.</summary>
internal static class NovaPublication
{
    const int Version=27;
    const int TextLimit=32*1024*1024,StyleLimit=4*1024*1024;
    sealed record Attributes(bool bold,bool italic,bool underline,string? family,int? size,byte[]? highlight,byte[]? foreground);
    sealed record Span(int start,int end,Attributes attributes);
    sealed record Manuscript(string Text,IReadOnlyList<Span> Spans);

    internal static void Assess(StructuredPublication book,IEnumerable<string> blocks)
    {
        foreach(string id in blocks)
        {
            var source=book.Model.Source(id);
            if(book.Model.Element(id).Kind!=SemanticKind.Paragraph||book.Paragraph(id)?.Properties is not {} p||
                p.Heading>0||p.NumId is not null||p.PageBreakBefore||p.Tabs.Count>0||p.HasBorders||p.Shading is not null||
                Math.Abs(p.Left)>.01f||Math.Abs(p.Right)>.01f||Math.Abs(p.FirstLine)>.01f||p.Alignment!="left")
            {
                book.Issue("EXPORT_NOVA_STRUCTURE",source,"La structure, la liste, le tableau ou la mise en page dépasse le profil NOVA qualifié.");
                continue;
            }
            foreach(var inline in book.ReadInlines(id))
            {
                if(inline.Anchor is not null||inline.Link is not null||inline.Value is null||inline.Value.Contains('\0')||
                    inline.Style is {Tracking:not 0}||inline.Style is {RightToLeft:true}||
                    inline.Style is {} style&&Math.Abs(style.Size*10-MathF.Round(style.Size*10))>.01f)
                    book.Issue("EXPORT_NOVA_INLINE",inline.Source,"Le lien, le signet ou le style dépasse le profil NOVA qualifié.");
            }
        }
        if(Encoding.UTF8.GetByteCount(book.Metadata.Title)>4096||book.Metadata.Title.Contains('\0')||book.Metadata.Author?.Contains('\0')==true)
            throw new InvalidDataException("EXPORT_NOVA_METADATA");
        foreach(string? value in new[]{book.Metadata.Subject,book.Metadata.Keywords,book.Metadata.Description})
            if(!string.IsNullOrWhiteSpace(value))
                book.Issue("EXPORT_NOVA_METADATA_CONVERSION",book.Model.Source(book.Model.Stories.First(s=>s.Kind=="body").RootId),
                    "Titre, auteur et dates vont dans le projet. Les autres propriétés restent dans le DOCX.",false);
        book.Feature("nova-text",PublicationRetention.Native,null,"Manuscrit éditable et plages Unicode scalaires, styles natifs v27.");
    }

    internal static void Write(string path,StructuredPublication book,ExportBudget budget,CancellationToken token)
    {
        var content=Compose(book,token);
        string project=Guid.NewGuid().ToString(),doc=Guid.NewGuid().ToString();
        string textPath="documents/"+doc+".txt";
        string stylePath="styles/"+doc+".json";
        var manifest=new
        {
            format="opus-nova",version=Version,project_id=project,active_document=doc,
            metadata=new
            {
                title=book.Metadata.Title,author=book.Metadata.Author??"",created_utc=Epoch(book.Metadata.Created),
                modified_utc=Epoch(book.Metadata.Modified??book.Metadata.Created)
            },
            documents=new[]{new{id=doc,title=book.Metadata.Title,path=textPath,styles=stylePath}},
            extensions=new Dictionary<string,object>()
        };
        var entries=new List<Entry>
        {
            new("manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest)),
            StringEntry(textPath,content.Text),
            new(stylePath,JsonSerializer.SerializeToUtf8Bytes(Styles(content)))
        };
        if(entries[1].Data.Length>TextLimit||entries[2].Data.Length>StyleLimit||entries.Sum(e=>e.Data.LongLength)>64*1024*1024)
            throw new InvalidDataException("EXPORT_NOVA_BUDGET");
        WriteEntries(path,entries,budget,null,token);
    }

    internal static void Verify(string path,StructuredPublication book,CancellationToken token)
    {
        var content=Compose(book,token);
        using var file=File.OpenRead(path);
        using var zip=new ZipArchive(file,ZipArchiveMode.Read);
        using var json=JsonDocument.Parse(Read("manifest.json",64*1024));
        var manifest=json.RootElement;
        string id=manifest.GetProperty("active_document").GetString()??"";
        string project=manifest.GetProperty("project_id").GetString()??"";
        var metadata=manifest.GetProperty("metadata");
        var doc=manifest.GetProperty("documents").EnumerateArray().Single();
        if(zip.Entries.Count!=3||!Guid.TryParseExact(id,"D",out var documentId)||documentId==Guid.Empty||
            !Guid.TryParseExact(project,"D",out var projectId)||projectId==Guid.Empty||documentId==projectId||
            manifest.GetProperty("format").GetString()!="opus-nova"||manifest.GetProperty("version").GetInt32()!=Version||
            doc.GetProperty("id").GetString()!=id||doc.GetProperty("path").GetString()!="documents/"+id+".txt"||
            doc.GetProperty("styles").GetString()!="styles/"+id+".json"||metadata.GetProperty("title").GetString()!=book.Metadata.Title||
            metadata.GetProperty("author").GetString()!=(book.Metadata.Author??"")||
            !Read("documents/"+id+".txt",TextLimit).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(content.Text))||
            !Read("styles/"+id+".json",StyleLimit).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(Styles(content))))
            throw new InvalidDataException("EXPORT_NOVA_ROUNDTRIP");

        byte[] Read(string name,int limit)
        {
            token.ThrowIfCancellationRequested();
            if(zip.GetEntry(name) is not {Length:>=0} entry||entry.Length>limit)
                throw new InvalidDataException("EXPORT_NOVA_ENTRY");
            using var stream=entry.Open();
            byte[] bytes=new byte[(int)entry.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    static object Styles(Manuscript book)=>new
    {
        version=9,spans=book.Spans.Select(p=>new
        {
            p.start,p.end,attributes=new
            {
                p.attributes.bold,p.attributes.italic,p.attributes.underline,p.attributes.family,p.attributes.size,
                highlight=p.attributes.highlight?.Select(x=>(int)x).ToArray(),
                foreground=p.attributes.foreground?.Select(x=>(int)x).ToArray()
            }
        }).ToArray()
    };

    static Manuscript Compose(StructuredPublication book,CancellationToken token)
    {
        var text=new StringBuilder();
        var spans=new List<Span>();
        int offset=0;
        foreach(string id in book.Model.BodyBlocks)
        {
            token.ThrowIfCancellationRequested();
            if(offset>0||text.Length>0){text.Append('\n');offset++;}
            else if(id!=book.Model.BodyBlocks[0]){text.Append('\n');offset++;}
            foreach(var part in book.ReadInlines(id))
            {
                if(part.Value is not {} value)continue;
                int end=offset+value.EnumerateRunes().Count();
                text.Append(value);
                var style=part.Style;
                if(style is not null)
                {
                    var attributes=new Attributes(style.Bold,style.Italic,style.Underline=="single",StructuredPublication.Family(part),
                        (int)MathF.Round(style.Size*10),Rgb(style.Background),Rgb(style.Color));
                    if(spans.Count>0&&spans[^1].end==offset&&Same(spans[^1].attributes,attributes))
                        spans[^1]=spans[^1] with{end=end};
                    else spans.Add(new(offset,end,attributes));
                }
                offset=end;
            }
        }
        if(spans.Count>100_000)throw new InvalidDataException("EXPORT_NOVA_SPAN_LIMIT");
        return new(text.ToString(),spans);

        static bool Same(Attributes a,Attributes b)=>a.bold==b.bold&&a.italic==b.italic&&a.underline==b.underline&&
            a.family==b.family&&a.size==b.size&&(a.highlight??[]).SequenceEqual(b.highlight??[])&&
            (a.foreground??[]).SequenceEqual(b.foreground??[]);
    }

    static byte[]? Rgb(string? color)=>color is null?null:uint.TryParse(color.TrimStart('#'),NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture,out uint rgb)&&rgb<=0xffffff?[(byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb]:null;
    static long Epoch(DateTimeOffset? date)=>Math.Max(0,(date??DateTimeOffset.UnixEpoch).ToUnixTimeSeconds());
}
