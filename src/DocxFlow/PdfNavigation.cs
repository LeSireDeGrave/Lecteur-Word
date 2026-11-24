using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LecteurWord.Flow;

/// <summary>Adds an outline to the classic cross-reference format written by Skia.
/// Only the privately generated stream is accepted by this reader.</summary>
internal static partial class PdfNavigation
{
    sealed record Trailer(int Root,int Size,long Xref,string Dictionary,int Pages,int? Info);
    sealed class Item(PdfPublication.Outline entry,int id)
    {
        internal PdfPublication.Outline Entry {get;}=entry;
        internal int Id {get;}=id;
        internal List<Item> Children {get;}=[];
    }

    internal static void Append(string path,IReadOnlyList<PdfPublication.Outline> entries,byte[]? provenance,ExportBudget budget)
    {
        if(entries.Count>2048)throw new InvalidDataException("EXPORT_PDF_OUTLINE_LIMIT");
        using var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        var trailer=Read(file);
        if(trailer.Dictionary.Contains("/Outlines",StringComparison.Ordinal))throw new InvalidDataException("EXPORT_PDF_OUTLINE_PRESENT");
        int outline=trailer.Size;
        int added=entries.Count>0?entries.Count+1:0;
        int fileSpec=trailer.Size+added,attachment=fileSpec+1;
        var items=new List<Item>();
        var stack=new Stack<Item>();
        for(int i=0;i<entries.Count;i++)
        {
            var item=new Item(entries[i],outline+i+1);
            while(stack.TryPeek(out var previous)&&previous.Entry.Level>=item.Entry.Level)stack.Pop();
            if(stack.TryPeek(out var parent))parent.Children.Add(item);
            else items.Add(item);
            stack.Push(item);
        }
        string additions=items.Count==0?"":FormattableString.Invariant($"/Outlines {outline} 0 R\n");
        if(provenance is not null)
        {
            if(trailer.Dictionary.Contains("/Names",StringComparison.Ordinal))
                throw new InvalidDataException("EXPORT_PDF_NAMES_PRESENT");
            additions+=FormattableString.Invariant(
                $"/Names << /EmbeddedFiles << /Names [(lecteur-word-glyphes.json) {fileSpec} 0 R] >> >>\n");
            added+=2;
        }
        var offsets=new Dictionary<int,long>();
        file.Position=file.Length;
        Write("\n");
        Object(trailer.Root,trailer.Dictionary.Insert(trailer.Dictionary.LastIndexOf(">>",StringComparison.Ordinal),additions));
        if(items.Count>0)
        {
            Object(outline,FormattableString.Invariant(
                $"<< /Type /Outlines /First {items[0].Id} 0 R /Last {items[^1].Id} 0 R /Count {entries.Count} >>"));
            Emit(items,outline);
        }
        if(provenance is not null)
        {
            Object(fileSpec,FormattableString.Invariant(
                $"<< /Type /Filespec /F (lecteur-word-glyphes.json) /UF (lecteur-word-glyphes.json) /EF << /F {attachment} 0 R >> >>"));
            offsets.Add(attachment,file.Position);
            Write(FormattableString.Invariant(
                $"{attachment} 0 obj\n<< /Type /EmbeddedFile /Subtype /application#2Fjson /Length {provenance.Length} >>\nstream\n"));
            budget.Check(provenance.Length);
            file.Write(provenance);
            Write("\nendstream\nendobj\n");
        }
        long xref=file.Position;
        Write(FormattableString.Invariant($"xref\n{trailer.Root} 1\n"));
        Offset(trailer.Root);
        Write(FormattableString.Invariant($"{outline} {added}\n"));
        for(int i=outline;i<outline+added;i++)Offset(i);
        string info=trailer.Info is {} metadata?FormattableString.Invariant($" /Info {metadata} 0 R"):"";
        Write(FormattableString.Invariant($"trailer\n<< /Size {outline+added} /Root {trailer.Root} 0 R{info} /Prev {trailer.Xref} >>\nstartxref\n{xref}\n%%EOF\n"));
        file.Flush();

        void Emit(List<Item> group,int parent)
        {
            for(int i=0;i<group.Count;i++)
            {
                var item=group[i];
                var title=Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(item.Entry.Title));
                var data=new StringBuilder(FormattableString.Invariant(
                    $"<< /Title <FEFF{title}> /Parent {parent} 0 R /Dest /{item.Entry.Name}"));
                if(i>0)data.Append(CultureInfo.InvariantCulture,$" /Prev {group[i-1].Id} 0 R");
                if(i+1<group.Count)data.Append(CultureInfo.InvariantCulture,$" /Next {group[i+1].Id} 0 R");
                if(item.Children.Count>0)data.Append(CultureInfo.InvariantCulture,
                    $" /First {item.Children[0].Id} 0 R /Last {item.Children[^1].Id} 0 R /Count {Count(item.Children)}");
                data.Append(" >>");
                Object(item.Id,data.ToString());
                if(item.Children.Count>0)Emit(item.Children,item.Id);
            }
        }

        static int Count(List<Item> group)=>group.Sum(i=>1+Count(i.Children));
        void Object(int id,string contents)
        {
            offsets.Add(id,file.Position);
            Write(FormattableString.Invariant($"{id} 0 obj\n{contents}\nendobj\n"));
        }
        void Offset(int id)=>Write(FormattableString.Invariant($"{offsets[id]:0000000000} 00000 n \n"));
        void Write(string contents)
        {
            byte[] bytes=Encoding.ASCII.GetBytes(contents);
            budget.Check(bytes.Length);
            file.Write(bytes);
        }
    }

    internal static void Verify(string path,int expectedPages)
    {
        using var file=File.OpenRead(path);
        if(!ReadAt(file,0,8).StartsWith("%PDF-",StringComparison.Ordinal))throw new InvalidDataException("EXPORT_PDF_HEADER");
        var trailer=Read(file);
        if(trailer.Pages!=expectedPages)throw new InvalidDataException("EXPORT_PDF_PAGE_COUNT");
    }

    static Trailer Read(FileStream file)
    {
        string tail=ReadAt(file,Math.Max(0,file.Length-4096),4096);
        var match=EndPattern().Match(tail);
        if(!match.Success||!long.TryParse(match.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out long current))
            throw new InvalidDataException("EXPORT_PDF_TRAILER");
        int root=0,size=0;
        int? info=null;
        long newest=current;
        var offsets=new Dictionary<int,long>();
        for(int revisions=0;revisions<2;revisions++)
        {
            file.Position=Offset(current,file);
            using var reader=new StreamReader(file,Encoding.ASCII,detectEncodingFromByteOrderMarks:false,4096,leaveOpen:true);
            if(reader.ReadLine()!="xref")throw new InvalidDataException("EXPORT_PDF_XREF");
            while(reader.ReadLine() is {} line&&line!="trailer")
            {
                var fields=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(fields is not [var start,var n]||!int.TryParse(start,out int first)||!int.TryParse(n,out int items)||
                    first<0||items<1||first>100_000-items)throw new InvalidDataException("EXPORT_PDF_XREF_LENGTH");
                for(int i=0;i<items;i++)
                {
                    if(reader.ReadLine() is not {Length:>=18 and <=22} entry||
                        !long.TryParse(entry.AsSpan(0,10),NumberStyles.None,CultureInfo.InvariantCulture,out long offset))
                        throw new InvalidDataException("EXPORT_PDF_XREF_ENTRY");
                    if(entry[17]=='n')offsets.TryAdd(first+i,Offset(offset,file));
                }
            }
            string value="";
            while(reader.ReadLine() is {} line&&line!="startxref"&&value.Length<1024)value+=line+"\n";
            if(root==0)
            {
                root=Number(value,"Root",reference:true);
                size=Number(value,"Size",reference:false);
            }
            if(info is null&&Regex.IsMatch(value,@"/Info\s+[0-9]+",RegexOptions.CultureInvariant))
                info=Number(value,"Info",reference:true);
            if(!Regex.IsMatch(value,@"/Prev\s+[0-9]+",RegexOptions.CultureInvariant))break;
            current=Number(value,"Prev",reference:false);
        }
        var catalog=ObjectText(root,2_000_000);
        int startDictionary=catalog.IndexOf("<<",StringComparison.Ordinal);
        if(startDictionary<0||!catalog.Contains("/Type /Catalog",StringComparison.Ordinal))
            throw new InvalidDataException("EXPORT_PDF_CATALOG");
        string dictionary=catalog[startDictionary..].TrimEnd();
        int pageTree=Number(dictionary,"Pages",reference:true);
        int count=Number(ObjectText(pageTree,64_000),"Count",reference:false);
        if(info is {} reference)_=ObjectText(reference,260_000);
        return new(root,size,newest,dictionary,count,info);

        string ObjectText(int id,int maximum)
        {
            if(!offsets.TryGetValue(id,out long offset))throw new InvalidDataException("EXPORT_PDF_OBJECT");
            string data=ReadAt(file,offset,maximum);
            int end=data.IndexOf("\nendobj",StringComparison.Ordinal);
            if(end<0||!data.StartsWith(FormattableString.Invariant($"{id} 0 obj\n"),StringComparison.Ordinal))
                throw new InvalidDataException("EXPORT_PDF_OBJECT_CONTENT");
            return data[..end];
        }
    }

    static int Number(string text,string field,bool reference)
    {
        var found=Regex.Match(text,$@"/{field}\s+([0-9]+)"+(reference?@"\s+0\s+R":""),RegexOptions.CultureInvariant);
        if(!found.Success||!int.TryParse(found.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n<1)
            throw new InvalidDataException("EXPORT_PDF_"+field.ToUpperInvariant());
        return n;
    }

    static long Offset(long offset,FileStream file)=>offset>0&&offset<file.Length?offset:throw new InvalidDataException("EXPORT_PDF_OFFSET");

    static string ReadAt(FileStream file,long offset,int maximum)
    {
        file.Position=offset;
        byte[] bytes=new byte[(int)Math.Min(file.Length-offset,maximum)];
        file.ReadExactly(bytes);
        return Encoding.Latin1.GetString(bytes);
    }

    [GeneratedRegex(@"startxref\s+([0-9]+)\s+%%EOF\s*$",RegexOptions.CultureInvariant)]
    private static partial Regex EndPattern();
}
