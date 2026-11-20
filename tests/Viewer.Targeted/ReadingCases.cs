using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

public static class ReadingCases
{
    static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static IEnumerable<ReadableBlock> Walk(IEnumerable<ReadableBlock> blocks)
    {foreach(var block in blocks){yield return block;foreach(var child in Walk(block.Children))yield return child;}}
    static string Digest(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public static void Model()
    {
        string root=Path.Combine(Path.GetTempPath(),"lecteur-readable-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string paragraph="<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:rPr><w:i/></w:rPr><w:t>Écriture العربية 中文 😀 é</w:t></w:r></w:p>";
        string list="<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"7\"/></w:numPr></w:pPr><w:r><w:t>Liste</w:t></w:r></w:p>";
        string table="<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:sdt><w:sdtContent><w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"2\"/><w:vMerge w:val=\"restart\"/></w:tcPr>"+Fixtures.Paragraph("Cellule")+"<w:tbl><w:tr><w:tc>"+Fixtures.Paragraph("Tableau imbriqué")+"</w:tc></w:tr></w:tbl></w:tc></w:tr></w:sdtContent></w:sdt></w:tbl>";
        string policy="<w:p><w:bookmarkStart w:id=\"1\" w:name=\"cible\"/><w:ins w:id=\"2\"><w:r><w:t>Inséré</w:t></w:r></w:ins><w:del w:id=\"3\"><w:r><w:delText>Supprimé</w:delText></w:r></w:del><w:fldSimple w:instr=\"PAGE\"><w:r><w:t>9</w:t></w:r></w:fldSimple><w:hyperlink r:id=\"web\"><w:r><w:t>Lien</w:t></w:r></w:hyperlink><w:r><w:footnoteReference w:id=\"1\"/></w:r><w:bookmarkEnd w:id=\"1\"/></w:p>";
        string drawing="<w:p><w:r><w:drawing><a:blip xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" r:embed=\"image\"/><a:blip xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" r:link=\"external-image\"/><w:txbxContent>"+Fixtures.Paragraph("Zone de texte préservée")+"</w:txbxContent></w:drawing></w:r></w:p>";
        string breaks="<w:p><w:pPr><w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/></w:sectPr></w:pPr><w:r><w:t>Avant colonne</w:t><w:br w:type=\"column\"/><w:t>Après colonne</w:t></w:r></w:p>";
        string unknown="<x:future xmlns:x=\"urn:reading-test\">Texte inconnu préservé</x:future>";
        var parts=Fixtures.Parts(paragraph+list+table+policy+drawing+breaks+unknown,new()
        {
            ["word/styles.xml"]=Fixtures.Xml("styles","<w:style w:styleId=\"Normal\" w:type=\"paragraph\"><w:rPr><w:b/></w:rPr></w:style><w:style w:styleId=\"Heading1\" w:type=\"paragraph\"><w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr></w:style>"),
            ["word/numbering.xml"]=Fixtures.Xml("numbering","<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"4\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl></w:abstractNum><w:num w:numId=\"7\"><w:abstractNumId w:val=\"0\"/></w:num>"),
            ["word/footnotes.xml"]=Fixtures.Xml("footnotes","<w:footnote w:id=\"1\">"+Fixtures.Paragraph("Corps de note conservé")+"</w:footnote>")
        },Fixtures.Relationship("styles","styles","styles.xml")+Fixtures.Relationship("numbering","numbering","numbering.xml")+Fixtures.Relationship("notes","footnotes","footnotes.xml")+Fixtures.Relationship("web","hyperlink","https://example.invalid/never","External")+Fixtures.Relationship("image","image","media/a.png")+Fixtures.Relationship("external-image","image","https://example.invalid/never.png","External"),new(){["word/media/a.png"]=Fixtures.Png});
        string path=Fixtures.Write(root,"structured.docx",parts);string before=Digest(path);
        var inspection=DocumentInspector.InspectDocument(path);Assert(inspection.Manifest.InspectionComplete,"Fixture inspection refused");
        var reading=ReadableReader.Read(inspection);var all=Walk(reading.Blocks).ToArray();
        Assert(reading.Available&&!reading.PaginationAvailable&&reading.IsApproximate,"Reading must be available without pagination qualification");
        Assert(reading.Outline.Count==1&&reading.Bookmarks.Count==1,"Outline/bookmark missing");
        Assert(all.Any(b=>b.Kind=="heading"&&b.Bold&&Walk(b.Children).Any(c=>c.Kind=="run"&&c.Bold&&c.Italic)),"Inherited/basic style missing");
        Assert(all.Any(b=>b.ListLabel=="4."),"List start missing");
        Assert(all.Count(b=>b.Kind=="table")==2&&all.Any(b=>b.ColumnSpan==2&&b.VerticalMerge=="restart"),"Nested table/merge missing");
        foreach(string expected in new[]{"Écriture العربية 中文 😀 é","Cellule","Tableau imbriqué","Inséré","Supprimé","9","Corps de note conservé","Zone de texte préservée","Texte inconnu préservé"})Assert(reading.PlainText.Contains(expected,StringComparison.Ordinal),"Text lost: "+expected);
        Assert(all.Any(b=>b.Kind=="revision"&&b.Revision=="deleted")&&all.Any(b=>b.Kind=="field")&&all.Any(b=>b.FieldRole=="result"),"Revision/field policy dissolved");
        Assert(all.Count(b=>b.Kind=="image")==2&&all.Any(b=>b.ResourceId is not null)&&all.Any(b=>b.Kind=="image"&&b.ResourceId is null),"Images or external marker missing");
        Assert(all.Any(b=>b.Kind=="section")&&all.Any(b=>b.Kind=="break"&&b.Text.Contains("colonne",StringComparison.Ordinal)),"Section or column break silently removed");
        Assert(reading.Resources.Count==1&&reading.Resources.Values.All(SafePng.Validate),"Unsafe rendering resources");
        Assert(Digest(path)==before&&!DocumentExports.Preflight(inspection,ExportFormat.Pdf).Allowed,"Source or export guard changed");
        var refused=inspection with {Manifest=inspection.Manifest with {InspectionComplete=false}};Assert(!ReadableReader.Read(refused).Available,"Incomplete inventory became readable");
        using var cancel=new CancellationTokenSource();cancel.Cancel();try{ReadableReader.Read(inspection,cancel.Token);throw new InvalidOperationException("Reading cancellation ignored");}catch(OperationCanceledException){}
        Console.WriteLine("OK reading model | fixture: "+path);
    }
    public static void RealDocument()
    {
        string path=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT")??throw new InvalidOperationException("LECTEUR_WORD_QA_INPUT must identify the consented local copy");
        string before=Digest(path);var inspection=DocumentInspector.InspectDocument(path);var result=ReadableReader.Read(inspection);
        Assert(inspection.Manifest.InspectionComplete&&result.Available,"Consented real DOCX not readable");
        var all=Walk(result.Blocks).ToArray();Assert(all.Any(b=>b.Kind=="table")&&result.Outline.Count>0&&result.PlainText.Length>0,"Real document structures absent");
        var children=inspection.Manifest.Nodes.Where(n=>n.ParentId is not null).ToLookup(n=>n.ParentId!);
        string main=inspection.Manifest.Relationships.Single(r=>r.SourcePart=="/"&&r.Type.EndsWith("/officeDocument",StringComparison.Ordinal)).ResolvedPart!;
        var represented=all.Where(b=>b.Kind=="text").GroupBy(b=>b.Id).ToDictionary(g=>g.Key,g=>g.First());
        foreach(var text in inspection.Manifest.Nodes.Where(n=>n.Source.PartUri==main&&(n.Name.EndsWith("}t",StringComparison.Ordinal)||n.Name.EndsWith("}delText",StringComparison.Ordinal))))
        {
            string expected=string.Concat(children[text.Id].Where(n=>n.Kind=="textValue").Select(n=>n.Text));
            Assert(represented.TryGetValue(text.Id,out var found)&&found.Text==expected,"Real main-story text lost at "+text.Source.Path);
        }
        // Independent XML oracle also checks document order, including field instructions.
        using(var zip=ZipFile.OpenRead(path))using(var part=zip.GetEntry(main.TrimStart('/'))!.Open())
        using(var xml=XmlReader.Create(part,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16*1024*1024}))
        {
            var document=XDocument.Load(xml,LoadOptions.PreserveWhitespace);XNamespace w=Fixtures.W;
            int cursor=0;string actual=result.PlainText;
            foreach(var text in document.Descendants().Where(e=>e.Name==w+"t"||e.Name==w+"delText"||e.Name==w+"instrText").Select(e=>e.Value).Where(t=>t.Length>0))
            {int found=actual.IndexOf(text,cursor,StringComparison.Ordinal);Assert(found>=cursor,"Independent source text/order mismatch");cursor=found+text.Length;}
        }
        Assert(Digest(path)==before&&!result.PaginationAvailable,"Original or pagination guard changed");
        Console.WriteLine($"OK real reading | blocks={result.Blocks.Count} | descendants={all.Length} | outline={result.Outline.Count} | issues={result.Issues.Count} | sourceSHA256={before}");
    }
    public static void PngGate()
    {
        Assert(SafePng.TryGetRenderableBytes(Fixtures.Png,out var clean,out _),"Known tiny PNG rejected");Assert(clean.SequenceEqual(Fixtures.Png),"Clean PNG changed");
        byte[] bad=Fixtures.Png;bad[20]^=1;Assert(!SafePng.Validate(bad),"CRC corruption accepted");
        Assert(!SafePng.Validate(Fixtures.Png[..^2]),"Truncated PNG accepted");
        Assert(!SafePng.Validate(Fixtures.Png.Concat(new byte[]{1}).ToArray()),"Trailing PNG data accepted");
        byte[] metadata=Chunk("tEXt",Encoding.ASCII.GetBytes("Private\0metadata"));var original=Fixtures.Png;
        byte[] withMetadata=original[..33].Concat(metadata).Concat(original[33..]).ToArray();
        Assert(SafePng.TryGetRenderableBytes(withMetadata,out var stripped,out var reason)&&stripped.SequenceEqual(original)&&reason.Length>0,"Ancillary metadata not safely omitted/signalled");
        byte[] huge=(byte[])original.Clone();BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16,4),8192);BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(20,4),8192);
        byte[] ihdr=Chunk("IHDR",huge.AsSpan(16,13).ToArray());ihdr.CopyTo(huge,8);Assert(!SafePng.Validate(huge),"Pixel budget ignored");
        byte[] invalidFilter=MakePng([9,0,0]);Assert(!SafePng.Validate(invalidFilter),"Invalid scanline filter accepted");
        Assert(!SafePng.Validate(MakePng([0,0])),"Short scanline accepted");Assert(!SafePng.Validate(MakePng([0,0,0,0])),"Extra inflated data accepted");
        Console.WriteLine("OK PNG gate: CRC, chunks, metadata, pixels and exact zlib scanline bounds");
    }
    static byte[] MakePng(byte[] raw)
    {
        byte[] header=new byte[13];BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0,4),1);BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4,4),1);header[8]=8;header[9]=4;
        using var compressed=new MemoryStream();using(var z=new System.IO.Compression.ZLibStream(compressed,System.IO.Compression.CompressionLevel.Optimal,true))z.Write(raw);
        return new byte[]{137,80,78,71,13,10,26,10}.Concat(Chunk("IHDR",header)).Concat(Chunk("IDAT",compressed.ToArray())).Concat(Chunk("IEND",[])).ToArray();
    }
    static byte[] Chunk(string type,byte[] data)
    {
        var output=new byte[data.Length+12];BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0,4),(uint)data.Length);Encoding.ASCII.GetBytes(type).CopyTo(output,4);data.CopyTo(output,8);
        uint crc=uint.MaxValue;foreach(byte b in output.AsSpan(4,data.Length+4)){crc^=b;for(int k=0;k<8;k++)crc=(crc&1)!=0?0xedb88320u^(crc>>1):crc>>1;}
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(data.Length+8,4),~crc);return output;
    }
}
