using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using LecteurWord.DocxCore;

if (args.Length != 2 || args[0] != "--case") { Console.Error.WriteLine("Select one targeted case with --case <name>. No aggregate suite command."); return 64; }
string root = Path.Combine(Path.GetTempPath(), "docx-core-001a-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    switch (args[1])
    {
        case "unicode": Unicode(); break;
        case "styles-lists": StylesLists(); break;
        case "tables": Tables(); break;
        case "images": Images(); break;
        case "sections": Sections(); break;
        case "notes-links": NotesLinks(); break;
        case "fields-comments": FieldsComments(); break;
        case "revisions-controls": Revisions(); break;
        case "unknown-objects": Unknown(); break;
        case "hostile-zip": HostileZip(); break;
        case "hostile-xml-opc": HostileXml(); break;
        case "budgets": Budgets(); break;
        case "cancellation-output": CancellationOutput(); break;
        case "volume": Volume(); break;
        case "measure-small": Measure(10,1,0); break;
        case "measure-medium": Measure(1000,20,100); break;
        case "measure-large": Measure(15000,100,1000); break;
        default: throw new InvalidOperationException("Unknown targeted case.");
    }
    Console.WriteLine($"OK {args[1]} | fixtures: {root}");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine($"KO {args[1]} | {e} | fixtures: {root}"); return 1; }

void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
void Code(InspectionResult r, string code) => Assert(r.Manifest.Diagnostics.Any(d => d.Code == code), "Missing diagnostic " + code + ": " + string.Join(',', r.Manifest.Diagnostics.Select(d => d.Code).Distinct()));
InventoryNode[] Kind(InspectionResult r, string kind) => r.Manifest.Nodes.Where(n => n.Kind == kind).ToArray();
InventoryNode[] Named(InspectionResult r, string name) => r.Manifest.Nodes.Where(n => n.Name == "{" + Fixtures.W + "}" + name).ToArray();
string Attr(InventoryNode n, string name) => n.Attributes["{" + Fixtures.W + "}" + name];
InspectionResult Inspect(string name, Dictionary<string, byte[]> parts, InspectionOptions? options = null, bool complete = true)
{
    var path = Fixtures.Write(root, name + ".docx", parts);
    string before = Sha(path);
    var result = DocumentInspector.InspectDocument(path, options);
    Assert(Sha(path) == before, "Source changed");
    Assert(result.Manifest.InspectionComplete == complete, "Unexpected completion: " + string.Join(',', result.Manifest.Diagnostics.Select(d => d.Code).Distinct()));
    Assert(!result.Manifest.ImportReady && result.Manifest.Pagination == Coverage.Untested, "False functionality claim");
    if (complete)
    {
        Assert(result.Manifest.Parts.Count == parts.Count, "Part disappeared");
        // Independent LINQ-to-XML oracle: exact expanded names, all attributes and leaf values per part.
        foreach (var (part, bytes) in parts.Where(kv => kv.Key.EndsWith(".xml", StringComparison.Ordinal) || kv.Key.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var expected = XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.PreserveWhitespace);
            var actual = result.Manifest.Nodes.Where(n => n.Source.PartUri == "/" + part && !n.Name.StartsWith('#')).ToArray();
            var elements = expected.Descendants().ToArray();
            Assert(actual.Length == elements.Length, "Exact element count: " + part);
            for (int i = 0; i < actual.Length; i++)
            {
                Assert(actual[i].Name == "{" + elements[i].Name.NamespaceName + "}" + elements[i].Name.LocalName, "Element order/name");
                foreach (var attr in elements[i].Attributes().Where(a => !a.IsNamespaceDeclaration))
                    Assert(actual[i].Attributes.GetValueOrDefault("{" + attr.Name.NamespaceName + "}" + attr.Name.LocalName) == attr.Value, "Attribute lost");
            }
            string expectedText = string.Concat(expected.DescendantNodes().OfType<XText>().Select(t => t.Value));
            string actualText = string.Concat(result.Manifest.Nodes.Where(n => n.Source.PartUri == "/" + part && n.Kind == "textValue").Select(n => n.Text));
            Assert(actualText == expectedText, "Text/whitespace lost");
        }
        foreach (var node in result.Manifest.Nodes.Where(n => n.Coverage != Coverage.Read))
            Assert(result.Manifest.Diagnostics.Any(d => d.Source == node.Source), "Undiagnosed inventory-only node");
        Assert(result.Manifest.Nodes.Select(n => n.Id).Distinct().Count() == result.Manifest.Nodes.Count, "Duplicate node ID");
        Assert(result.Manifest.Nodes.Select(n => n.Source).Distinct().Count() == result.Manifest.Nodes.Count, "Duplicate provenance");
        var again = DocumentInspector.InspectDocument(path, options);
        Assert(ManifestJson.Serialize(result.Manifest).SequenceEqual(ManifestJson.Serialize(again.Manifest)), "Nondeterministic JSON");
    }
    else Assert(result.Manifest.Status == Coverage.Refused, "Failure not refused");
    return result;
}
void Unicode()
{
    string text = " é œ ﬁ العربية 中文 日本語 😀 ";
    var r = Inspect("unicode", Fixtures.Parts("<w:p><w:r><w:t xml:space=\"preserve\">" + text + "</w:t><w:tab/><w:br/><w:cr/><w:t>fin</w:t></w:r></w:p>"));
    Assert(Kind(r,"paragraph").Length == 1 && Kind(r,"run").Length == 1 && Kind(r,"text").Length == 2, "Exact text inventory");
    Assert(Kind(r,"tab").Length == 1 && Kind(r,"break").Length == 2, "Exact controls");
    Assert(Kind(r,"textValue").Any(n => n.Text == text), "Unicode altered");
}
void StylesLists()
{
    string styles = Fixtures.Xml("styles", "<w:style w:type=\"paragraph\" w:styleId=\"Normal\"><w:rPr><w:b/></w:rPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr></w:style>");
    string numbering = Fixtures.Xml("numbering", "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"lowerLetter\"/></w:lvl></w:abstractNum><w:num w:numId=\"7\"><w:abstractNumId w:val=\"0\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"4\"/></w:lvlOverride></w:num>");
    string p = "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/><w:numPr><w:ilvl w:val=\"1\"/><w:numId w:val=\"7\"/></w:numPr></w:pPr><w:r><w:rPr><w:i/></w:rPr><w:t>Titre</w:t></w:r></w:p>";
    var r = Inspect("styles", Fixtures.Parts(p, new() { ["word/styles.xml"] = styles, ["word/numbering.xml"] = numbering }, Fixtures.Relationship("s", "styles", "styles.xml") + Fixtures.Relationship("n", "numbering", "numbering.xml")));
    Assert(Kind(r,"style").Length == 2 && Kind(r,"numbering").Length == 5, "Style/list counts");
    Assert(r.Manifest.Styles.Any(s => s.StyleId == "Heading1" && s.Ancestors.SequenceEqual(new[]{"Heading1", "Normal"})), "Inheritance chain");
    Assert(Attr(Named(r,"startOverride").Single(),"val") == "4", "List restart lost");
    var cycle = styles.Replace("<w:rPr><w:b/></w:rPr>","<w:basedOn w:val=\"Heading1\"/>",StringComparison.Ordinal);
    Code(Inspect("style-cycle",Fixtures.Parts(p,new(){["word/styles.xml"]=cycle})),"STYLE_CYCLE");
}
void AssertOoxmlSchema(string name)
{
    using var sdk=DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(Path.Combine(root,name+".docx"),false);
    var errors=new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(sdk).Take(12).ToArray();
    Assert(errors.Length==0,"Synthetic OOXML schema: "+string.Join(" | ",errors.Select(e=>e.Description)));
}
void Tables()
{
    string grid="<w:tblPr><w:tblW w:w=\"4800\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2400\"/><w:gridCol w:w=\"2400\"/></w:tblGrid>";
    string nested="<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"4800\"/></w:tblGrid><w:tr><w:tc>"+Fixtures.Paragraph("nested")+"</w:tc></w:tr></w:tbl>";
    string cell="<w:tc><w:tcPr><w:gridSpan w:val=\"2\"/><w:vMerge w:val=\"restart\"/></w:tcPr>"+Fixtures.Paragraph("a")+Fixtures.Paragraph("b")+nested+Fixtures.Paragraph("after nested")+"</w:tc>";
    string table="<w:tbl>"+grid+"<w:tr>"+cell+"</w:tr><w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"2\"/><w:vMerge/></w:tcPr>"+Fixtures.Paragraph("c")+"</w:tc></w:tr><w:tr><w:tc>"+Fixtures.Paragraph("d")+"</w:tc><w:tc>"+Fixtures.Paragraph("e")+"</w:tc></w:tr></w:tbl>"+Fixtures.Paragraph("end");
    var r=Inspect("tables",Fixtures.Parts(table));
    Assert(Kind(r,"table").Length==2&&Kind(r,"row").Length==4&&Kind(r,"cell").Length==5&&Kind(r,"paragraph").Length==8&&Kind(r,"merge").Length==4,"Exact nested table counts");
    Assert(Named(r,"gridSpan").Length==2&&Named(r,"gridSpan").All(n=>Attr(n,"val")=="2"),"Merge geometry");
    AssertOoxmlSchema("tables");
}
void Images()
{
    string Drawing(bool anchor,bool external)
    {
        string graphic=$"""
        <a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="0" name="image.png"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:{(external?"link":"embed")}="{(external?"external":"img")}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic>
        """;
        string frame=anchor?"anchor":"inline";
        string attrs=anchor?" simplePos=\"0\" relativeHeight=\"0\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\"":"";
        string position=anchor?"<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>":"";
        return "<w:p><w:r><w:drawing><wp:"+frame+" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\""+attrs+">"+position+"<wp:extent cx=\"914400\" cy=\"914400\"/>"+(anchor?"<wp:wrapNone/>":"")+"<wp:docPr id=\""+(anchor?"2":"1")+"\" name=\"image\" descr=\"Description\"/>"+graphic+"</wp:"+frame+"></w:drawing></w:r></w:p>";
    }
    string drawing=Drawing(false,false),anchored=Drawing(true,true);
    var r=Inspect("images",Fixtures.Parts(drawing+anchored+Fixtures.Paragraph("Légende"),relationships:Fixtures.Relationship("img","image","media/p.png")+Fixtures.Relationship("external","image","http://127.0.0.1:9/never.png","External"),binary:new(){["word/media/p.png"]=Fixtures.Png}));
    Assert(Kind(r,"drawing").Length==2&&r.Manifest.Media.Count==1&&r.SafeMedia.Count==1,"Image count");
    Assert(r.Manifest.Media[0].Width==1&&r.Manifest.Media[0].Height==1&&r.Manifest.Media[0].Id.Length==71,"Media dimensions/identity");
    Code(r,"EXTERNAL_NOT_FETCHED");AssertOoxmlSchema("images");
    var bad=Inspect("bad-image",Fixtures.Parts(drawing,relationships:Fixtures.Relationship("img","image","media/p.png"),binary:new(){["word/media/p.png"]=Encoding.ASCII.GetBytes("<svg onload='x'/>")}));
    Code(bad,"MEDIA_QUARANTINED");Assert(bad.SafeMedia.Count==0,"Unsafe media exposed");
}
void Sections()
{
    string section="<w:sectPr><w:headerReference w:type=\"default\" r:id=\"h\"/><w:footerReference w:type=\"first\" r:id=\"f\"/><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:bottom=\"1440\"/><w:titlePg/></w:sectPr>";
    var r=Inspect("sections",Fixtures.Parts("<w:p><w:pPr>"+section+"</w:pPr><w:r><w:t>x</w:t><w:br w:type=\"page\"/></w:r></w:p>"+section,new(){["word/header1.xml"]=Fixtures.Xml("hdr",Fixtures.Paragraph("haut")),["word/footer1.xml"]=Fixtures.Xml("ftr",Fixtures.Paragraph("bas"))},Fixtures.Relationship("h","header","header1.xml")+Fixtures.Relationship("f","footer","footer1.xml")));
    Assert(Kind(r,"section").Length==2 && Kind(r,"header").Length==1 && Kind(r,"footer").Length==1 && Kind(r,"pageProperties").Length==4,"Section count");
    Assert(Named(r,"titlePg").Length==2 && Attr(Kind(r,"break").Single(),"type")=="page","First page/break lost");
}
void NotesLinks()
{
    string body="<w:p><w:bookmarkStart w:id=\"1\" w:name=\"ici\"/><w:r><w:t>Texte</w:t><w:footnoteReference w:id=\"2\"/><w:endnoteReference w:id=\"3\"/></w:r><w:bookmarkEnd w:id=\"1\"/><w:hyperlink w:anchor=\"ici\"><w:r><w:t>interne</w:t></w:r></w:hyperlink><w:hyperlink r:id=\"web\"><w:r><w:t>web</w:t></w:r></w:hyperlink></w:p>";
    var r=Inspect("notes",Fixtures.Parts(body,new(){["word/footnotes.xml"]=Fixtures.Xml("footnotes","<w:footnote w:id=\"2\">"+Fixtures.Paragraph("note pied")+"</w:footnote>"),["word/endnotes.xml"]=Fixtures.Xml("endnotes","<w:endnote w:id=\"3\">"+Fixtures.Paragraph("note fin")+"</w:endnote>")},Fixtures.Relationship("fn","footnotes","footnotes.xml")+Fixtures.Relationship("en","endnotes","endnotes.xml")+Fixtures.Relationship("web","hyperlink","https://example.invalid/never","External")));
    Assert(Kind(r,"note").Length==2 && Kind(r,"noteReference").Length==2 && Kind(r,"link").Length==2 && Kind(r,"bookmark").Length==2,"Notes/link counts");
    Assert(!r.Manifest.Diagnostics.Any(d=>d.Code.StartsWith("UNRESOLVED",StringComparison.Ordinal)),"Valid reference unresolved");
}
void FieldsComments()
{
    string p="<w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> TOC \\o &quot;1-3&quot; </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>Titre 1</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:fldSimple w:instr=\"PAGE\"><w:r><w:t>9</w:t></w:r></w:fldSimple><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p>";
    var r=Inspect("fields",Fixtures.Parts(p,new(){["word/comments.xml"]=Fixtures.Xml("comments","<w:comment w:id=\"0\" w:author=\"Synthetic\">"+Fixtures.Paragraph("Commentaire")+"</w:comment>")},Fixtures.Relationship("c","comments","comments.xml")));
    Assert(Kind(r,"field").Length==5 && Kind(r,"comment").Length==4,"Field/comment counts");
    Assert(Kind(r,"textValue").Single(n=>n.Text=="Titre 1").FieldRole=="result","Field result confused");
    Assert(Kind(r,"textValue").Single(n=>n.Text!.Contains("TOC",StringComparison.Ordinal)).FieldRole=="instruction","Field code confused");
    Assert(Kind(r,"textValue").Single(n=>n.Text=="9").FieldRole=="result","Simple field confused");
}
void Revisions()
{
    string p="<w:p><w:ins w:id=\"1\" w:author=\"Synthetic\"><w:r><w:t>ajout</w:t></w:r></w:ins><w:del w:id=\"2\"><w:r><w:delText>supprimé</w:delText></w:r></w:del><w:r><w:rPr><w:vanish/></w:rPr><w:t>caché</w:t></w:r><w:sdt><w:sdtPr><w:tag w:val=\"champ\"/></w:sdtPr><w:sdtContent><w:r><w:t>contrôle</w:t></w:r></w:sdtContent></w:sdt></w:p>";
    var r=Inspect("revisions",Fixtures.Parts(p));
    Assert(Kind(r,"revision").Length==2 && Kind(r,"hidden").Length==1 && Kind(r,"contentControl").Length==1,"Revision counts");
    Assert(Kind(r,"textValue").Single(n=>n.Text=="ajout").Revision=="inserted" && Kind(r,"textValue").Single(n=>n.Text=="supprimé").Revision=="deleted","Revision policy changed");
}
void Unknown()
{
    string p="<w:p><w:r><w:object><o:OLEObject xmlns:o=\"urn:schemas-microsoft-com:office:office\" r:id=\"ole\"/></w:object><m:oMath xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"><m:r><m:t>x+y</m:t></m:r></m:oMath></w:r><w:altChunk r:id=\"chunk\"/><x:unknown xmlns:x=\"urn:test:future\" x:attr=\"retained\">future</x:unknown></w:p>";
    var r=Inspect("unknown",Fixtures.Parts(p,relationships:Fixtures.Relationship("ole","oleObject","embeddings/o.bin")+Fixtures.Relationship("chunk","aFChunk","https://example.invalid/a","External"),binary:new(){["word/embeddings/o.bin"]=new byte[]{1,2,3}}));
    Assert(Kind(r,"embeddedObject").Length==1 && Kind(r,"textValue").Any(n=>n.Text=="x+y") && Kind(r,"textValue").Any(n=>n.Text=="future"),"Unknown content lost");
    Code(r,"BINARY_NOT_INTERPRETED"); Code(r,"ELEMENT_SEMANTICS_UNQUALIFIED");
}
void HostileZip()
{
    var parts=Fixtures.Parts(Fixtures.Paragraph("x"));
    foreach (string path in new[]{"../escape.xml","/absolute.xml","word/../../escape.xml","word\\escape.xml","word/%2e.xml"})
    {
        var hostile=new Dictionary<string,byte[]>(parts){[path]=Encoding.UTF8.GetBytes("<x/>")};
        Code(Inspect("unsafe-"+Guid.NewGuid().ToString("N"),hostile,complete:false),"UNSAFE_PART_PATH");
    }
    var duplicate=new Dictionary<string,byte[]>(parts){["WORD/document.xml"]=parts["word/document.xml"]};
    Code(Inspect("duplicate",duplicate,complete:false),"AMBIGUOUS_DUPLICATE_ENTRY");
    var bomb=new Dictionary<string,byte[]>(parts){["word/large.bin"]=new byte[1_000_000]};
    Code(Inspect("bomb",bomb,complete:false),"ZIP_EXPANSION_LIMIT");
    string file=Path.Combine(root,"invalid.docx"); File.WriteAllBytes(file,new byte[]{80,75,3,4,0,1,2});
    var bad=DocumentInspector.InspectDocument(file); Code(bad,"INVALID_ZIP");
    File.WriteAllText(file,"not a zip"); Code(DocumentInspector.InspectDocument(file),"NOT_ZIP_DOCX");
    byte[] cfb={0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1};
    File.WriteAllBytes(file,cfb); Code(DocumentInspector.InspectDocument(file),"INVALID_COMPOUND_OFFICE");
    File.WriteAllBytes(file,Fixtures.Compound("WordDocument")); Code(DocumentInspector.InspectDocument(file),"LEGACY_DOC");
    File.WriteAllBytes(file,Fixtures.Compound("EncryptionInfo","EncryptedPackage"));
    Code(DocumentInspector.InspectDocument(file),"ENCRYPTED_OFFICE");
    Code(Inspect("docm",Fixtures.Parts("",mainType:"application/vnd.ms-word.document.macroEnabled.main+xml"),complete:false),"MACRO_PACKAGE_REFUSED");
    Code(Inspect("dotx",Fixtures.Parts("",mainType:"application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml"),complete:false),"TEMPLATE_REFUSED");
    string symlink=Path.Combine(root,"symlink.docx");
    using(var fileStream=File.Create(symlink)) using(var zip=new ZipArchive(fileStream,ZipArchiveMode.Create))
    { var entry=zip.CreateEntry("link.xml"); entry.ExternalAttributes=unchecked((int)0xa1ff0000); using var s=entry.Open();s.Write("../outside"u8); }
    Code(DocumentInspector.InspectDocument(symlink),"ZIP_SYMLINK");
    string repeated=Path.Combine(root,"repeated.docx");
    using(var f=File.Create(repeated)) using(var z=new ZipArchive(f,ZipArchiveMode.Create))
    { z.CreateEntry("duplicate");z.CreateEntry("duplicate"); }
    Code(DocumentInspector.InspectDocument(repeated),"AMBIGUOUS_DUPLICATE_ENTRY");
    var crcParts=Fixtures.Parts("",binary:new(){["word/b.bin"]=Encoding.ASCII.GetBytes("UNIQUE_CRC_PAYLOAD")});
    string crcPath=Fixtures.Write(root,"crc.docx",crcParts,CompressionLevel.NoCompression);
    var crcBytes=File.ReadAllBytes(crcPath);int crcOffset=crcBytes.AsSpan().IndexOf("UNIQUE_CRC_PAYLOAD"u8);
    Assert(crcOffset>=0,"CRC fixture offset");crcBytes[crcOffset]^=1;File.WriteAllBytes(crcPath,crcBytes);
    Code(DocumentInspector.InspectDocument(crcPath),"ZIP_CRC_MISMATCH");
    string headerPath=Fixtures.Write(root,"directory-count.docx",Fixtures.Parts(""));
    byte[] headerBytes=File.ReadAllBytes(headerPath);int end=headerBytes.Length-22;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(headerBytes.AsSpan(end+8,2),1);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(headerBytes.AsSpan(end+10,2),1);
    File.WriteAllBytes(headerPath,headerBytes);Code(DocumentInspector.InspectDocument(headerPath),"ZIP_DIRECTORY_COUNT");
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(headerBytes.AsSpan(end+8,2),ushort.MaxValue);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(headerBytes.AsSpan(end+10,2),ushort.MaxValue);
    File.WriteAllBytes(headerPath,headerBytes);Code(DocumentInspector.InspectDocument(headerPath),"ZIP64_REFUSED");
}
void HostileXml()
{
    var broken=Fixtures.Parts("<w:p>");
    Code(Inspect("malformed",broken,complete:false),"UNSAFE_OR_MALFORMED_XML");
    var dtd=Fixtures.Parts(""); dtd["word/document.xml"]=Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY steal SYSTEM 'file:///etc/passwd'>]>"+Fixtures.Document(Fixtures.Paragraph("&steal;")));
    Code(Inspect("dtd",dtd,complete:false),"UNSAFE_OR_MALFORMED_XML");
    var missing=Fixtures.Parts("");missing.Remove("[Content_Types].xml");
    Code(Inspect("missing-opc",missing,complete:false),"MISSING_OPC_METADATA");
    Code(Inspect("broken-rel",Fixtures.Parts("",relationships:Fixtures.Relationship("bad","styles","missing.xml")),complete:false),"MISSING_RELATIONSHIP_TARGET");
    Code(Inspect("escape-rel",Fixtures.Parts("",relationships:Fixtures.Relationship("bad","styles","../../escape.xml")),complete:false),"RELATIONSHIP_ROOT_ESCAPE");
    Code(Inspect("duplicate-rel",Fixtures.Parts("",relationships:Fixtures.Relationship("x","hyperlink","https://example.invalid/1","External")+Fixtures.Relationship("x","hyperlink","https://example.invalid/2","External")),complete:false),"AMBIGUOUS_RELATIONSHIP");
    var wrongType=Fixtures.Parts("",new(){["word/settings.xml"]=Fixtures.Xml("settings","")},Fixtures.Relationship("s","styles","settings.xml"));
    Code(Inspect("wrong-type",wrongType,complete:false),"DEPENDENCY_CONTENT_TYPE_MISMATCH");
    var protectedDoc=Inspect("protected",Fixtures.Parts(Fixtures.Paragraph("protégé"),new(){["word/settings.xml"]=Fixtures.Xml("settings","<w:documentProtection w:edit=\"readOnly\" w:enforcement=\"1\"/>")},Fixtures.Relationship("s","settings","settings.xml")));
    Code(protectedDoc,"DOCUMENT_PROTECTED");
    var unresolved=Inspect("unresolved",Fixtures.Parts("<w:p><w:hyperlink w:anchor=\"missing\" r:id=\"missing\"><w:r><w:t>x</w:t><w:footnoteReference w:id=\"9\"/></w:r></w:hyperlink></w:p>"));
    Code(unresolved,"UNRESOLVED_BOOKMARK");Code(unresolved,"UNRESOLVED_RELATIONSHIP");Code(unresolved,"UNRESOLVED_NOTE");
}
void Budgets()
{
    var fromJson=System.Text.Json.JsonSerializer.Deserialize("{\"maxDurationMilliseconds\":1}",ContractJson.Default.InspectionOptions)!;
    Assert(fromJson.MaxZipBytes==32*1024*1024 && fromJson.MaxDurationMilliseconds==1,
        "JSON budget defaults lost: "+System.Text.Json.JsonSerializer.Serialize(fromJson));
    var p=Fixtures.Parts(Fixtures.Paragraph("budget"));
    var limits=new (InspectionOptions Options,string Code)[]
    {
        (new(){MaxZipBytes=8},"ZIP_SIZE_LIMIT"), (new(){MaxEntries=2},"ZIP_ENTRY_LIMIT"),
        (new(){MaxExpandedBytes=10},"EXPANDED_SIZE_LIMIT"), (new(){MaxPartBytes=10},"PART_SIZE_LIMIT"),
        (new(){MaxNodes=2},"NODE_COUNT_LIMIT"), (new(){MaxDepth=2},"XML_DEPTH_LIMIT"),
        (new(){MaxValueChars=3},"VALUE_SIZE_LIMIT"), (new(){MaxAttributesPerNode=1},"ATTRIBUTE_COUNT_LIMIT"),
        (new(){MaxAllocatedBytes=1},"ALLOCATION_LIMIT"), (new(){MaxWorkingSetBytes=1},"MEMORY_LIMIT")
    };
    int i=0;
    foreach(var (options,code) in limits) Code(Inspect("limit-"+(i++),p,options,complete:false),code);
    var image=Fixtures.Parts("",binary:new(){["word/media/a.png"]=Fixtures.Png,["word/media/b.png"]=Fixtures.Png});
    Code(Inspect("image-size",image,new(){MaxMediaBytes=8},false),"MEDIA_SIZE_LIMIT");
    Code(Inspect("image-count",image,new(){MaxMedia=1},false),"MEDIA_COUNT_LIMIT");
    byte[] huge=Fixtures.Png;System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16,4),100_000);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(20,4),100_000);
    Code(Inspect("image-pixels",Fixtures.Parts("",binary:new(){["word/media/a.png"]=huge}),complete:false),"IMAGE_PIXEL_LIMIT");
    var deep=Fixtures.Parts(string.Concat(Enumerable.Repeat("<w:sdt>",70))+Fixtures.Paragraph("deep")+string.Concat(Enumerable.Repeat("</w:sdt>",70)));
    Code(Inspect("deep",deep,complete:false),"XML_DEPTH_LIMIT");
}
void CancellationOutput()
{
    var parts=Fixtures.Parts(string.Concat(Enumerable.Range(0,20000).Select(i=>Fixtures.Paragraph("item "+i))));
    string path=Fixtures.Write(root,"cancel.docx",parts,CompressionLevel.NoCompression);
    string before=Sha(path);
    using(var pre=new CancellationTokenSource())
    { pre.Cancel();Code(DocumentInspector.InspectDocument(path,cancellationToken:pre.Token),"CANCELLED"); }
    using(var during=new CancellationTokenSource())
    {
        // Token fires while the parser is traversing the large fixture, after the snapshot read starts.
        during.CancelAfter(10);
        Code(DocumentInspector.InspectDocument(path,cancellationToken:during.Token),"CANCELLED");
    }
    Assert(Sha(path)==before,"Cancelled source changed");
    var r=Inspect("output",Fixtures.Parts(Fixtures.Paragraph("stable"),binary:new(){["word/media/p.png"]=Fixtures.Png}));
    string output=Path.Combine(root,"result");AtomicOutput.Write(r,output);
    Assert(File.Exists(Path.Combine(output,"manifest.json")),"No atomic manifest");
    Assert(File.ReadAllBytes(Path.Combine(output,"manifest.json")).SequenceEqual(ManifestJson.Serialize(r.Manifest)),"Written JSON differs");
    foreach(var resource in r.Manifest.Media)Assert(Sha(Path.Combine(output,"assets",resource.Id+".bin"))==resource.Sha256,"Asset hash differs");
    string original=Sha(Path.Combine(output,"manifest.json"));
    try{AtomicOutput.Write(r,output);throw new InvalidOperationException("Existing directory overwritten");}catch(IOException){}
    try{AtomicOutput.Write(r,path);throw new InvalidOperationException("Source output overwritten");}catch(IOException){}
    Assert(Sha(Path.Combine(output,"manifest.json"))==original && Sha(path)==before,"Existing output/source changed");
    using(var cancel=new CancellationTokenSource())
    { cancel.Cancel();try{AtomicOutput.Write(r,Path.Combine(root,"cancel-output"),cancel.Token);throw new InvalidOperationException("Write ignored cancellation");}catch(OperationCanceledException){} }
    var tiny=r with {Manifest=r.Manifest with {Budgets=new(){MaxOutputBytes=16}}};
    try{AtomicOutput.Write(tiny,Path.Combine(root,"too-large"));throw new InvalidOperationException("Output budget ignored");}catch(IOException){}
    Assert(!Directory.Exists(Path.Combine(root,"too-large")) && !Directory.EnumerateDirectories(root,".docx-core-*").Any(),"Staging leaked");
    string link=Path.Combine(root,"linked-parent");Directory.CreateSymbolicLink(link,root);
    try{AtomicOutput.Write(r,Path.Combine(link,"escape"));throw new InvalidOperationException("Output symlink accepted");}catch(IOException){}
}
void Volume()
{
    string table="<w:tbl>"+string.Concat(Enumerable.Range(0,300).Select(i=>"<w:tr><w:tc>"+Fixtures.Paragraph("row "+i)+"</w:tc><w:tc>"+Fixtures.Paragraph("second "+i)+"</w:tc></w:tr>"))+"</w:tbl>";
    var binary=Enumerable.Range(0,100).ToDictionary(i=>"word/media/i"+i+".png",_=>Fixtures.Png);
    string paragraphs=string.Concat(Enumerable.Range(0,5000).Select(i=>Fixtures.Paragraph("Synthetic page-like block "+i)));
    var r=Inspect("volume",Fixtures.Parts(paragraphs+table,binary:binary));
    Assert(Kind(r,"paragraph").Length==5600 && Kind(r,"cell").Length==600 && Kind(r,"row").Length==300,"Volume counts");
    Assert(r.Manifest.Media.Count==100 && r.SafeMedia.Count==1,"Repeated media not deduplicated by hash");
}

void Measure(int paragraphCount,int imageCount,int rows)
{
    string body=string.Concat(Enumerable.Range(0,paragraphCount).Select(i=>Fixtures.Paragraph("Texte synthétique numéro "+i+" — العربية 中文 😀")));
    if(rows>0)body+="<w:tbl>"+string.Concat(Enumerable.Range(0,rows).Select(i=>"<w:tr><w:tc>"+Fixtures.Paragraph("cellule "+i)+"</w:tc><w:tc>"+Fixtures.Paragraph("droite "+i)+"</w:tc></w:tr>"))+"</w:tbl>";
    var binary=Enumerable.Range(0,imageCount).ToDictionary(i=>"word/media/p"+i+".png",_=>Fixtures.Png);
    string input=Fixtures.Write(root,"measure.docx",Fixtures.Parts(body,binary:binary));
    long allocated=GC.GetTotalAllocatedBytes(true);
    using var process=Process.GetCurrentProcess();
    var cpu=process.TotalProcessorTime;var watch=Stopwatch.StartNew();
    var result=DocumentInspector.InspectDocument(input);watch.Stop();
    Assert(result.Manifest.InspectionComplete,"Measurement refused");
    var elapsed=watch.Elapsed.TotalMilliseconds;
    long allocation=GC.GetTotalAllocatedBytes(true)-allocated;
    process.Refresh();
    var report=new{input,paragraphs=paragraphCount+rows*2,tableRows=rows,images=imageCount,
        inputBytes=new FileInfo(input).Length,inspectMilliseconds=elapsed,
        cpuMilliseconds=(process.TotalProcessorTime-cpu).TotalMilliseconds,allocatedBytes=allocation,
        peakWorkingSetBytes=process.PeakWorkingSet64,nodes=result.Manifest.Nodes.Count,
        jsonBytes=ManifestJson.Serialize(result.Manifest).Length};
    string json=System.Text.Json.JsonSerializer.Serialize(report);
    File.WriteAllText(Path.Combine(root,"measure-api.json"),json);Console.WriteLine(json);
}
