using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;

namespace LecteurWord.DocxCore;

/// <summary>Read-only, bounded OPC inspection. No rendering, field evaluation or conversion.</summary>
public static class DocumentInspector
{
    internal const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    const string Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    const string MainType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    static readonly HashSet<string> ReadElements = new(StringComparer.Ordinal)
    {
        "document", "body", "p", "r", "t", "tab", "br", "cr", "tbl", "tr", "tc",
        "hyperlink", "bookmarkStart", "bookmarkEnd", "footnotes", "footnote", "endnotes", "endnote",
        "footnoteReference", "endnoteReference", "hdr", "ftr", "ins", "del", "delText", "moveFrom", "moveTo",
        "fldChar", "instrText", "fldSimple", "commentRangeStart", "commentRangeEnd", "commentReference"
    };
    static readonly HashSet<string> QualifiedAttributes = new(StringComparer.Ordinal)
    {
        "{http://www.w3.org/XML/1998/namespace}space", $"{{{W}}}id", $"{{{W}}}name", $"{{{W}}}anchor",
        $"{{{R}}}id", $"{{{W}}}fldCharType", $"{{{W}}}instr", $"{{{W}}}type", $"{{{W}}}clear",
        $"{{{W}}}author", $"{{{W}}}date"
    };
    sealed class Refusal(string code) : Exception(code) { public string Code => Message; }
    sealed class Budget(InspectionOptions options, CancellationToken token)
    {
        readonly Stopwatch time = Stopwatch.StartNew();
        readonly long allocationStart = GC.GetTotalAllocatedBytes();
        int calls;
        public void Check(bool force = false)
        {
            token.ThrowIfCancellationRequested();
            if (time.ElapsedMilliseconds > options.MaxDurationMilliseconds) throw new Refusal("TIME_LIMIT");
            if (!force && ++calls % 128 != 0) return;
            if (GC.GetTotalAllocatedBytes() - allocationStart > options.MaxAllocatedBytes) throw new Refusal("ALLOCATION_LIMIT");
            using var process = Process.GetCurrentProcess();
            if (process.WorkingSet64 > options.MaxWorkingSetBytes) throw new Refusal("MEMORY_LIMIT");
        }
    }

    public static InspectionResult InspectDocument(string input, InspectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        options.Validate();
        var manifest = new InspectionManifest { Budgets = options };
        var media = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var fontParts = new SortedDictionary<string,byte[]>(StringComparer.Ordinal);
        var budget = new Budget(options, cancellationToken);
        try
        {
            budget.Check(true);
            // A bounded immutable snapshot prevents SDK reads observing a concurrently modified file.
            using var file = InputFile.Read(input);
            if (file.Length > options.MaxZipBytes) throw new Refusal("ZIP_SIZE_LIMIT");
            byte[] snapshot = ReadBounded(file, options.MaxZipBytes, budget);
            manifest.SourceSha256 = Hash(snapshot);
            if (snapshot.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            {
                manifest.Format = CompoundClassifier.Classify(snapshot, () => budget.Check(true));
                throw new Refusal(manifest.Format switch
                {
                    "encryptedOffice" => "ENCRYPTED_OFFICE", "legacyDoc" => "LEGACY_DOC",
                    "invalidCompoundOffice" => "INVALID_COMPOUND_OFFICE", _ => "COMPOUND_OFFICE_REFUSED"
                });
            }
            if (snapshot.Length < 4 || snapshot[0] != 'P' || snapshot[1] != 'K' || snapshot[2] != 3 || snapshot[3] != 4)
                throw new Refusal("NOT_ZIP_DOCX");
            if(ZipPreflight.Check(snapshot,options,()=>budget.Check()) is {} zipFailure)throw new Refusal(zipFailure);
            using var zipStream = new MemoryStream(snapshot, false);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, false);
            if (zip.Entries.Count > options.MaxEntries) throw new Refusal("ZIP_ENTRY_LIMIT");
            var entries = new SortedDictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var caseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                budget.Check();
                string name = entry.FullName;
                if (!SafePartName(name)) throw new Refusal("UNSAFE_PART_PATH");
                if (!caseNames.Add(name)) throw new Refusal("AMBIGUOUS_DUPLICATE_ENTRY");
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new Refusal("ZIP_SYMLINK");
                if (entry.Length > options.MaxPartBytes) throw new Refusal("PART_SIZE_LIMIT");
                expanded = checked(expanded + entry.Length);
                if (expanded > options.MaxExpandedBytes) throw new Refusal("EXPANDED_SIZE_LIMIT");
                if (entry.Length > Math.Max(1, entry.CompressedLength) * (long)options.MaxExpansionRatio)
                    throw new Refusal("ZIP_EXPANSION_LIMIT");
                entries.Add(name, entry);
            }
            if (!entries.ContainsKey("[Content_Types].xml") || !entries.ContainsKey("_rels/.rels"))
                throw new Refusal("MISSING_OPC_METADATA");
            var bytes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, entry) in entries)
            {
                budget.Check(true);
                using var stream = entry.Open();
                var data = ReadBounded(stream, options.MaxPartBytes, budget);
                if (data.LongLength != entry.Length) throw new Refusal("ZIP_SIZE_MISMATCH");
                if (Crc32(data) != entry.Crc32) throw new Refusal("ZIP_CRC_MISMATCH");
                bytes.Add(name, data);
                // Content types also participate in the complete node inventory.
                if (name == "[Content_Types].xml")
                {
                    using var reader = Reader(data, options);
                    string? root = null;
                    while (reader.Read())
                    {
                        budget.Check();
                        if (reader.Depth > options.MaxDepth) throw new Refusal("XML_DEPTH_LIMIT");
                        if (reader.NodeType != XmlNodeType.Element) continue;
                        root ??= reader.NamespaceURI + ":" + reader.LocalName;
                        if (reader.NamespaceURI != Ct) throw new Refusal("INVALID_CONTENT_TYPES");
                        if (reader.LocalName == "Default")
                        {
                            if (!defaults.TryAdd(Required(reader, "Extension"), Required(reader, "ContentType")))
                                throw new Refusal("DUPLICATE_CONTENT_TYPE");
                        }
                        else if (reader.LocalName == "Override")
                        {
                            var part = Required(reader, "PartName");
                            if (!part.StartsWith('/') || !SafePartName(part[1..]) || !overrides.TryAdd(part[1..], Required(reader, "ContentType")))
                                throw new Refusal("INVALID_CONTENT_TYPES");
                        }
                        else if (reader.LocalName != "Types" || reader.Depth != 0) throw new Refusal("INVALID_CONTENT_TYPES");
                    }
                    if (root != Ct + ":Types") throw new Refusal("INVALID_CONTENT_TYPES");
                }
            }
            foreach (var target in overrides.Keys)
                if (!entries.ContainsKey(target)) throw new Refusal("MISSING_CONTENT_TYPE_PART");
            string TypeOf(string name) => name == "[Content_Types].xml" ? "application/xml" :
                overrides.GetValueOrDefault(name) ?? defaults.GetValueOrDefault(Path.GetExtension(name).TrimStart('.')) ?? "";
            foreach (var name in bytes.Keys)
            {
                string type = TypeOf(name);
                if (type.Length == 0) throw new Refusal("MISSING_PART_CONTENT_TYPE");
                if (type.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) || type.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
                { manifest.Format = "docm"; throw new Refusal("MACRO_PACKAGE_REFUSED"); }
                if (type == "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml")
                { manifest.Format = "dotx"; throw new Refusal("TEMPLATE_REFUSED"); }
                if (name.EndsWith(".rels", StringComparison.Ordinal))
                {
                    if (type != "application/vnd.openxmlformats-package.relationships+xml") throw new Refusal("RELATIONSHIP_CONTENT_TYPE");
                    ReadRelationships(name, bytes[name], manifest, entries, options, budget);
                }
            }
            var mains = manifest.Relationships.Where(r => r.SourcePart == "/" && r.Type == R + "/officeDocument").ToList();
            if (mains.Count != 1 || mains[0].Mode != "Internal" || mains[0].ResolvedPart is null)
                throw new Refusal("INVALID_MAIN_RELATIONSHIP");
            string main = mains[0].ResolvedPart![1..];
            if (TypeOf(main) != MainType) throw new Refusal("NOT_WORD_DOCUMENT_TYPE");
            manifest.Format = "docx";
            ValidateKnownRelationships(manifest, TypeOf);
            foreach (var (name, data) in bytes)
            {
                budget.Check(true);
                string type = TypeOf(name);
                bool xml = type.EndsWith("+xml", StringComparison.Ordinal) || type is "application/xml" or "text/xml";
                int before = manifest.Nodes.Count;
                if (xml) InventoryXml(name, data, manifest, options, budget);
                else
                {
                    if (type is "application/vnd.openxmlformats-officedocument.obfuscatedFont" or "application/x-font-ttf")
                    {
                        if (data.Length<=8*1024*1024 && fontParts.Count<16 && fontParts.Values.Sum(b=>(long)b.Length)+data.Length<=16*1024*1024)
                            fontParts.Add("/"+name,data);
                        else Add(manifest,"FONT_PAYLOAD_LIMIT",Coverage.Unrepresented,new("/"+name,"/"),"Font retained in inventory only: local font payload budget exceeded.");
                    }
                    Add(manifest, "BINARY_NOT_INTERPRETED", Coverage.Unrepresented, new("/" + name, "/"),
                        "Binary part hashed; no execution or semantic interpretation.");
                    if (type.StartsWith("image/", StringComparison.Ordinal))
                    {
                        if (manifest.Media.Count >= options.MaxMedia) throw new Refusal("MEDIA_COUNT_LIMIT");
                        if (data.Length > options.MaxMediaBytes) throw new Refusal("MEDIA_SIZE_LIMIT");
                        var (safe, width, height) = ImageHeader(data, type);
                        if (width.HasValue && ((long)width.Value * height!.Value > options.MaxImagePixels || width <= 0 || height <= 0))
                            throw new Refusal("IMAGE_PIXEL_LIMIT");
                        string hash = Hash(data), id = "sha256-" + hash;
                        manifest.Media.Add(new(id, "/" + name, hash, data.Length, type,
                            safe ? Coverage.Inventory : Coverage.Unrepresented, width, height));
                        if (safe) media.TryAdd(id, data);
                        else Add(manifest, "MEDIA_QUARANTINED", Coverage.Unrepresented, new("/" + name, "/"),
                            "Unsupported or inconsistent media signature; asset not exposed.");
                    }
                }
                manifest.Parts.Add(new("/" + name, type, Hash(data), data.Length,
                    xml ? Coverage.Inventory : Coverage.Unrepresented, manifest.Nodes.Count - before));
            }
            ValidateReferences(manifest);
            ResolveStyleChains(manifest, budget);
            budget.Check(true);
            // SDK verifies the OPC/Word document identity. DOM access follows bounded XML scanning.
            using (var sdkStream = new MemoryStream(snapshot, false))
            using (var sdk = WordprocessingDocument.Open(sdkStream, false, new OpenSettings
            { AutoSave = false, MaxCharactersInPart = options.MaxPartBytes }))
            {
                if (sdk.DocumentType != DocumentFormat.OpenXml.WordprocessingDocumentType.Document ||
                    sdk.MainDocumentPart?.Document?.Body is null) throw new Refusal("SDK_MAIN_DOCUMENT_REFUSED");
                if (sdk.MainDocumentPart.Uri.OriginalString != "/" + main) throw new Refusal("SDK_MAIN_PART_MISMATCH");
            }
            budget.Check(true);
            manifest.InspectionComplete = true;
            manifest.Status = Coverage.Inventory;
            Add(manifest, "FIDELITY_NOT_QUALIFIED", Coverage.Untested, null,
                "Inventory completed; Word layout, import mapping and OOXML schema validity are not qualified.");
        }
        catch (OperationCanceledException) { Fail(manifest, "CANCELLED"); }
        catch (Refusal refusal) { Fail(manifest, refusal.Code); }
        catch (XmlException) { Fail(manifest, "UNSAFE_OR_MALFORMED_XML"); }
        catch (InvalidDataException) { Fail(manifest, "INVALID_ZIP"); }
        catch (OpenXmlPackageException) { Fail(manifest, "SDK_PACKAGE_REFUSED"); }
        catch (IOException) { Fail(manifest, "IO_ERROR"); }
        catch (UnauthorizedAccessException) { Fail(manifest, "ACCESS_REFUSED"); }
        catch (OverflowException) { Fail(manifest, "RESOURCE_OVERFLOW"); }
        catch (ArgumentException) { Fail(manifest, "INVALID_PACKAGE_ARGUMENT"); }
        if (!manifest.InspectionComplete) {media.Clear();fontParts.Clear();}
        return new(manifest, media){FontParts=fontParts};
    }

    static void Fail(InspectionManifest m, string code)
    {
        m.Status = Coverage.Refused;
        m.InspectionComplete = false;
        Add(m, code, Coverage.Refused, null, "Inspection stopped; partial inventory is unusable for import.", "error");
    }
    static void Add(InspectionManifest m, string code, string coverage, SourceLocation? source, string message, string severity = "warning") =>
        m.Diagnostics.Add(new(code, severity, coverage, source, message));
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        return ~crc;
    }
    static bool SafePartName(string name) => name.Length is > 0 and <= 512 && !name.StartsWith('/') &&
        !name.Contains('\\') && !name.Contains('%') && !name.Contains(':') && !name.Contains('?') && !name.Contains('#') &&
        !name.Any(char.IsControl) && name.Split('/').All(s => s.Length > 0 && s != "." && s != "..");
    static string Required(XmlReader reader, string name) => reader.GetAttribute(name) is { Length: > 0 } value ? value : throw new Refusal("OPC_ATTRIBUTE_MISSING");
    static byte[] ReadBounded(Stream stream, long max, Budget budget)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[65536];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            budget.Check(true);
            if (buffer.Length + read > max) throw new Refusal("STREAM_SIZE_LIMIT");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
    static XmlReader Reader(byte[] data, InspectionOptions options) => XmlReader.Create(new MemoryStream(data, false), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = options.MaxPartBytes,
        MaxCharactersFromEntities = 1024, IgnoreWhitespace = false, CloseInput = true
    });
    static void ReadRelationships(string name, byte[] data, InspectionManifest m,
        SortedDictionary<string, ZipArchiveEntry> entries, InspectionOptions options, Budget budget)
    {
        string source;
        if (name == "_rels/.rels") source = "/";
        else
        {
            int pos = name.LastIndexOf("/_rels/", StringComparison.Ordinal);
            if (pos < 0) throw new Refusal("INVALID_RELATIONSHIP_PART_PATH");
            source = "/" + name[..(pos + 1)] + name[(pos + 7)..^5];
            if (!entries.ContainsKey(source[1..])) throw new Refusal("ORPHAN_RELATIONSHIP_PART");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = Reader(data, options);
        bool root = false;
        while (reader.Read())
        {
            budget.Check();
            if (reader.Depth > 1) throw new Refusal("INVALID_RELATIONSHIPS");
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.NamespaceURI != Rel) throw new Refusal("INVALID_RELATIONSHIPS");
            if (reader.Depth == 0 && reader.LocalName == "Relationships") { root = true; continue; }
            if (reader.LocalName != "Relationship" || reader.Depth != 1) throw new Refusal("INVALID_RELATIONSHIPS");
            string id = Required(reader, "Id"), target = Required(reader, "Target"), type = Required(reader, "Type");
            if (id.Length > 256 || target.Length > 4096 || type.Length > 1024 || !ids.Add(id)) throw new Refusal("AMBIGUOUS_RELATIONSHIP");
            string mode = reader.GetAttribute("TargetMode") ?? "Internal";
            string? resolved = null;
            if (mode == "External")
                Add(m, "EXTERNAL_NOT_FETCHED", Coverage.Inventory, new("/" + name, "/" + id), "External relationship recorded; never followed.");
            else if (mode == "Internal")
            {
                resolved = ResolveTarget(source, target);
                if (!entries.ContainsKey(resolved[1..])) throw new Refusal("MISSING_RELATIONSHIP_TARGET");
            }
            else throw new Refusal("INVALID_TARGET_MODE");
            m.Relationships.Add(new(source, id, type, target, mode, resolved));
        }
        if (!root) throw new Refusal("INVALID_RELATIONSHIPS");
    }
    static string ResolveTarget(string source, string target)
    {
        // OPC legitimately uses ../media between parts. Normalize only within package root.
        if (target.Contains('\\') || target.Contains(':') || target.Contains('%') || target.Contains('?') || target.Contains('#') || target.Any(char.IsControl))
            throw new Refusal("UNSAFE_RELATIONSHIP_TARGET");
        var segments = target.StartsWith('/') ? new List<string>() : source.Split('/').Skip(1).SkipLast(1).ToList();
        foreach (string s in target.Split('/'))
        {
            if (s is "" or ".") continue;
            if (s == "..") { if (segments.Count == 0) throw new Refusal("RELATIONSHIP_ROOT_ESCAPE"); segments.RemoveAt(segments.Count - 1); }
            else segments.Add(s);
        }
        string result = string.Join('/', segments);
        if (!SafePartName(result)) throw new Refusal("UNSAFE_RELATIONSHIP_TARGET");
        return "/" + result;
    }
    static void ValidateKnownRelationships(InspectionManifest m, Func<string, string> typeOf)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["styles"] = "styles", ["numbering"] = "numbering", ["footnotes"] = "footnotes", ["endnotes"] = "endnotes",
            ["header"] = "header", ["footer"] = "footer", ["comments"] = "comments", ["settings"] = "settings"
        };
        foreach (var r in m.Relationships.Where(r => r.Mode == "Internal"))
        {
            if (r.Type.StartsWith(R + "/", StringComparison.Ordinal) && expected.TryGetValue(r.Type[(R.Length + 1)..], out string? suffix) &&
                typeOf(r.ResolvedPart![1..]) != "application/vnd.openxmlformats-officedocument.wordprocessingml." + suffix + "+xml")
                throw new Refusal("DEPENDENCY_CONTENT_TYPE_MISMATCH");
            if (r.Type == R + "/image" && !typeOf(r.ResolvedPart![1..]).StartsWith("image/", StringComparison.Ordinal))
                throw new Refusal("IMAGE_CONTENT_TYPE_MISMATCH");
        }
    }
    sealed class Frame(InventoryNode node, string local, string ns, bool simpleField)
    {
        public InventoryNode Node { get; } = node;
        public string Local { get; } = local;
        public string Namespace { get; } = ns;
        public bool SimpleField { get; } = simpleField;
        public int ChildCount { get; set; }
    }
    static void InventoryXml(string name, byte[] bytes, InspectionManifest m, InspectionOptions options, Budget budget)
    {
        using var reader = Reader(bytes, options);
        var stack = new Stack<Frame>();
        var fields = new List<string>();
        int rootCount = 0;
        while (reader.Read())
        {
            budget.Check();
            if (reader.Depth >= options.MaxDepth) throw new Refusal("XML_DEPTH_LIMIT");
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (stack.Pop().SimpleField)
                { if (fields.Count == 0) throw new Refusal("UNBALANCED_FIELD"); fields.RemoveAt(fields.Count - 1); }
                continue;
            }
            if (reader.NodeType is XmlNodeType.XmlDeclaration) continue;
            if (m.Nodes.Count >= options.MaxNodes) throw new Refusal("NODE_COUNT_LIMIT");
            var parent = stack.TryPeek(out var frame) ? frame : null;
            int index = parent is null ? ++rootCount : ++parent.ChildCount;
            var location = new SourceLocation("/" + name, (parent?.Node.Source.Path ?? "") + "/" + index);
            string id = "n" + (m.Nodes.Count + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var attrs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string local = reader.LocalName, ns = reader.NamespaceURI;
            bool element = reader.NodeType == XmlNodeType.Element;
            string kind = element ? Kind(local, ns) : reader.NodeType switch
            {
                XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace => "textValue",
                XmlNodeType.Comment => "xmlComment", XmlNodeType.ProcessingInstruction => "processingInstruction", _ => "xmlNode"
            };
            string coverage = element && ns == W && ReadElements.Contains(local) || kind == "textValue" ? Coverage.Read : Coverage.Inventory;
            if (element && reader.HasAttributes)
            {
                if (reader.AttributeCount > options.MaxAttributesPerNode) throw new Refusal("ATTRIBUTE_COUNT_LIMIT");
                while (reader.MoveToNextAttribute())
                {
                    if (reader.Value.Length > options.MaxValueChars) throw new Refusal("VALUE_SIZE_LIMIT");
                    string key = "{" + reader.NamespaceURI + "}" + reader.LocalName;
                    attrs.Add(key, reader.Value);
                    if (coverage == Coverage.Read && reader.NamespaceURI != "http://www.w3.org/2000/xmlns/" && !QualifiedAttributes.Contains(key))
                        Add(m, "ATTRIBUTE_SEMANTICS_UNQUALIFIED", Coverage.Inventory, location, "Attribute retained: " + key);
                }
                reader.MoveToElement();
            }
            if (reader.Value.Length > options.MaxValueChars || reader.Name.Length > 1024 || reader.NamespaceURI.Length > 2048)
                throw new Refusal("VALUE_SIZE_LIMIT");
            var node = new InventoryNode(id, parent?.Node.Id, location, element ? "{" + ns + "}" + local : "#" + reader.NodeType,
                kind, coverage, attrs)
            {
                Text = element ? null : reader.Value,
                Revision = parent?.Node.Revision ?? "unchanged",
                FieldRole = fields.Count > 0 ? fields[^1] : "none"
            };
            if (ns == W)
            {
                if (local is "ins" or "moveTo") node.Revision = "inserted";
                if (local is "del" or "moveFrom" or "delText") node.Revision = "deleted";
                if (local == "instrText") node.FieldRole = "instruction";
                if (local == "fldChar")
                {
                    string? state = attrs.GetValueOrDefault("{" + W + "}fldCharType");
                    switch (state)
                    {
                        case "begin": fields.Add("instruction"); break;
                        case "separate" when fields.Count > 0: fields[^1] = "result"; break;
                        case "end" when fields.Count > 0: fields.RemoveAt(fields.Count - 1); break;
                        default: Add(m, "UNBALANCED_FIELD", Coverage.Unrepresented, location, "Field boundary has no matching state."); break;
                    }
                }
                if (local == "fldSimple") { fields.Add("result"); node.FieldRole = "result"; }
                if (local is "fldChar" or "fldSimple" or "ins" or "del" or "moveFrom" or "moveTo")
                    Add(m, "IMPORT_POLICY_REQUIRED", Coverage.Inventory, location, "Revision or field preserved; explicit import policy required.");
                if (local is "documentProtection" or "writeProtection")
                    Add(m, "DOCUMENT_PROTECTED", Coverage.Inventory, location, "Protection recorded; never bypassed or modified.");
            }
            if (kind == "textValue" && parent?.Node.FieldRole == "instruction") node.FieldRole = "instruction";
            m.Nodes.Add(node);
            if (coverage != Coverage.Read)
                Add(m, "ELEMENT_SEMANTICS_UNQUALIFIED", coverage, location, "XML structure retained; semantics not qualified: " + node.Name);
            if (element && !reader.IsEmptyElement) stack.Push(new(node, local, ns, ns == W && local == "fldSimple"));
            else if (element && ns == W && local == "fldSimple") fields.RemoveAt(fields.Count - 1);
        }
        if (fields.Count > 0) Add(m, "UNBALANCED_FIELD", Coverage.Unrepresented, new("/" + name, "/"), "Unclosed complex field.");
    }
    static string Kind(string local, string ns) => ns != W ? "extensionElement" : local switch
    {
        "p" => "paragraph", "r" => "run", "t" or "delText" => "text", "tbl" => "table", "tr" => "row", "tc" => "cell",
        "pPr" or "rPr" or "tblPr" or "tcPr" => "properties", "style" => "style", "basedOn" => "styleParent",
        "num" or "abstractNum" or "lvl" or "numPr" => "numbering", "gridSpan" or "vMerge" or "hMerge" => "merge",
        "drawing" or "pict" => "drawing", "hyperlink" => "link", "bookmarkStart" or "bookmarkEnd" => "bookmark",
        "footnote" or "endnote" => "note", "footnoteReference" or "endnoteReference" => "noteReference",
        "hdr" => "header", "ftr" => "footer", "sectPr" => "section", "pgSz" or "pgMar" => "pageProperties",
        "br" or "cr" => "break", "tab" => "tab", "fldChar" or "fldSimple" or "instrText" => "field",
        "comment" or "commentReference" or "commentRangeStart" or "commentRangeEnd" => "comment",
        "ins" or "del" or "moveFrom" or "moveTo" => "revision", "vanish" or "webHidden" => "hidden",
        "object" => "embeddedObject", "sdt" => "contentControl", _ => "wordElement"
    };
    static void ValidateReferences(InspectionManifest m)
    {
        var rels = m.Relationships.ToDictionary(r => (r.SourcePart, r.Id));
        var bookmarks = m.Nodes.Where(n => n.Name == "{" + W + "}bookmarkStart")
            .Select(n => n.Attributes.GetValueOrDefault("{" + W + "}name")).ToHashSet();
        var notes = m.Nodes.Where(n => n.Kind == "note")
            .Select(n => (n.Name, n.Attributes.GetValueOrDefault("{" + W + "}id"))).ToHashSet();
        foreach (var node in m.Nodes)
        {
            foreach (var (key, value) in node.Attributes)
            {
                if (key is "{" + R + "}id" or "{" + R + "}embed" or "{" + R + "}link")
                    if (!rels.ContainsKey((node.Source.PartUri, value)))
                        Add(m, "UNRESOLVED_RELATIONSHIP", Coverage.Unrepresented, node.Source, "Referenced relationship is absent.");
                if (key == "{" + W + "}anchor" && !bookmarks.Contains(value))
                    Add(m, "UNRESOLVED_BOOKMARK", Coverage.Unrepresented, node.Source, "Bookmark target is absent.");
            }
            if (node.Kind == "noteReference")
            {
                string target = node.Name.Replace("Reference}", "}", StringComparison.Ordinal).Replace("Reference", "", StringComparison.Ordinal);
                if (!notes.Contains((target, node.Attributes.GetValueOrDefault("{" + W + "}id"))))
                    Add(m, "UNRESOLVED_NOTE", Coverage.Unrepresented, node.Source, "Note body is absent.");
            }
        }
    }
    static void ResolveStyleChains(InspectionManifest m, Budget budget)
    {
        string Val(InventoryNode n, string attr) => n.Attributes.GetValueOrDefault("{" + W + "}" + attr) ?? "";
        var children = m.Nodes.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!);
        var definitions = new Dictionary<string, InventoryNode>(StringComparer.Ordinal);
        foreach (var n in m.Nodes.Where(n => n.Kind == "style"))
            if (!definitions.TryAdd(Val(n, "styleId"), n))
                Add(m, "DUPLICATE_STYLE", Coverage.Unrepresented, n.Source, "Ambiguous style identifier.");
        foreach (var n in m.Nodes.Where(n => n.Name is "{" + W + "}pStyle" or "{" + W + "}rStyle" || n.Kind == "style"))
        {
            budget.Check();
            string style = Val(n, n.Kind == "style" ? "styleId" : "val"), current = style;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var chain = new List<string>();
            while (current.Length > 0)
            {
                budget.Check();
                if (chain.Count >= m.Budgets.MaxDepth) throw new Refusal("STYLE_DEPTH_LIMIT");
                if (!visited.Add(current)) { Add(m, "STYLE_CYCLE", Coverage.Unrepresented, n.Source, "Cyclic style inheritance."); break; }
                chain.Add(current);
                if (!definitions.TryGetValue(current, out var definition))
                { Add(m, "MISSING_STYLE", Coverage.Unrepresented, n.Source, "Referenced style is absent."); break; }
                current = children[definition.Id].Where(c => c.Kind == "styleParent").Select(c => Val(c, "val")).FirstOrDefault() ?? "";
            }
            m.Styles.Add(new(n.Id, style, chain, Coverage.Inventory));
        }
    }
    static (bool Safe, int? Width, int? Height) ImageHeader(byte[] data, string type)
    {
        if (type == "image/png" && data.Length >= 24 && data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) &&
            data.AsSpan(12,4).SequenceEqual("IHDR"u8))
        {
            uint w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16,4));
            uint h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20,4));
            if (w > int.MaxValue || h > int.MaxValue) throw new Refusal("IMAGE_PIXEL_LIMIT");
            return (true, (int)w, (int)h);
        }
        // JPEG is not exposed until a bounded dimension decoder has been qualified.
        return (false, null, null);
    }
}
