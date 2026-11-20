using System.IO.Compression;
using System.Text;

internal static class Fixtures
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string Main = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    public static byte[] Png => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    public static string Xml(string root, string body) => $"<w:{root} xmlns:w=\"{W}\" xmlns:r=\"{R}\">{body}</w:{root}>";
    public static string Document(string body) => Xml("document", "<w:body>" + body + "</w:body>");
    public static string Paragraph(string text) => "<w:p><w:r><w:t>" + text + "</w:t></w:r></w:p>";
    public static string Relationship(string id, string type, string target, string mode = "Internal") =>
        $"<Relationship Id=\"{id}\" Type=\"{R}/{type}\" Target=\"{target}\" TargetMode=\"{mode}\"/>";
    public static string Relations(string body) => $"<Relationships xmlns=\"{Rel}\">{body}</Relationships>";
    public static Dictionary<string, byte[]> Parts(string body, Dictionary<string, string>? extra = null,
        string relationships = "", Dictionary<string, byte[]>? binary = null, string mainType = Main)
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["word/document.xml"] = Encoding.UTF8.GetBytes(Document(body)),
            ["_rels/.rels"] = Encoding.UTF8.GetBytes(Relations(Relationship("rMain", "officeDocument", "word/document.xml")))
        };
        var types = new Dictionary<string, string> { ["word/document.xml"] = mainType };
        if (extra is not null)
            foreach (var (name, content) in extra)
            {
                parts[name] = Encoding.UTF8.GetBytes(content);
                string stem = Path.GetFileNameWithoutExtension(name);
                string type = stem.StartsWith("header", StringComparison.Ordinal) ? "header" : stem.StartsWith("footer", StringComparison.Ordinal) ? "footer" : stem;
                types[name] = "application/vnd.openxmlformats-officedocument.wordprocessingml." + type + "+xml";
            }
        if (relationships.Length > 0) parts["word/_rels/document.xml.rels"] = Encoding.UTF8.GetBytes(Relations(relationships));
        if (binary is not null) foreach (var (name, content) in binary) parts[name] = content;
        string overrides = string.Concat(types.Select(kv => $"<Override PartName=\"/{kv.Key}\" ContentType=\"{kv.Value}\"/>"));
        parts["[Content_Types].xml"] = Encoding.UTF8.GetBytes("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/>" +
            "<Default Extension=\"bin\" ContentType=\"application/octet-stream\"/>" + overrides + "</Types>");
        return parts;
    }
    public static string Write(string root, string name, Dictionary<string, byte[]> parts, CompressionLevel compression = CompressionLevel.Optimal)
    {
        string path = Path.Combine(root, name);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (key, data) in parts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var entry = zip.CreateEntry(key, compression);
            entry.LastWriteTime = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(data);
        }
        return path;
    }
    public static byte[] Compound(params string[] streamNames)
    {
        byte[] data = new byte[1536];
        void U16(int p, ushort v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p,2),v);
        void U32(int p, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p,4),v);
        new byte[]{0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1}.CopyTo(data,0);
        U16(24,0x3e);U16(26,3);U16(28,0xfffe);U16(30,9);U16(32,6);
        U32(44,1);U32(48,1);U32(56,4096);U32(60,0xfffffffe);U32(68,0xfffffffe);
        for(int j=0;j<109;j++)U32(76+j*4,uint.MaxValue);
        U32(76,0);
        for(int j=0;j<128;j++)U32(512+j*4,uint.MaxValue);
        U32(512,0xfffffffd);U32(516,0xfffffffe);
        var names=new[]{"Root Entry"}.Concat(streamNames).ToArray();
        for(int j=0;j<names.Length;j++)
        {
            int p=1024+j*128;var text=Encoding.Unicode.GetBytes(names[j]+"\0");text.CopyTo(data,p);
            U16(p+64,(ushort)text.Length);data[p+66]=(byte)(j==0?5:2);data[p+67]=1;
            U32(p+68,uint.MaxValue);U32(p+72,j+1<names.Length?(uint)(j+1):uint.MaxValue);
            U32(p+76,j==0?1u:uint.MaxValue);U32(p+116,0xfffffffe);
        }
        return data;
    }
}
