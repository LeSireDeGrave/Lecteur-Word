using System.Buffers.Binary;

namespace LecteurWord.DocxCore;
// Validates/counts the central directory before ZipArchive allocates its entry graph.
internal static class ZipPreflight
{
    public static string? Check(byte[] bytes,InspectionOptions options,Action check)
    {
        ushort U16(int p)=>BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p,2));
        uint U32(int p)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p,4));
        int end=-1;
        for(int p=bytes.Length-22;p>=Math.Max(0,bytes.Length-65557);p--)
            if(U32(p)==0x06054b50 && p+22+U16(p+20)==bytes.Length){end=p;break;}
        if(end<0)return "INVALID_ZIP";
        check();
        if(U16(end+4)!=0||U16(end+6)!=0||U16(end+8)!=U16(end+10))return "ZIP_MULTIDISK_REFUSED";
        int declared=U16(end+10);uint length=U32(end+12),start=U32(end+16);
        if(declared==ushort.MaxValue||length==uint.MaxValue||start==uint.MaxValue)return "ZIP64_REFUSED";
        if(declared>options.MaxEntries)return "ZIP_ENTRY_LIMIT";
        if((ulong)start+length!=(ulong)end)return "INVALID_ZIP";
        int cursor=(int)start,count=0;long expanded=0;
        while(cursor<end)
        {
            check();
            if(end-cursor<46||U32(cursor)!=0x02014b50)return "INVALID_ZIP";
            if(++count>options.MaxEntries)return "ZIP_ENTRY_LIMIT";
            int flags=U16(cursor+8),method=U16(cursor+10),name=U16(cursor+28),extra=U16(cursor+30),comment=U16(cursor+32);
            uint compressed=U32(cursor+20),raw=U32(cursor+24),local=U32(cursor+42);
            if(compressed==uint.MaxValue||raw==uint.MaxValue||local==uint.MaxValue)return "ZIP64_REFUSED";
            if((flags&0x41)!=0)return "ZIP_ENCRYPTED";
            if(method is not (0 or 8))return "ZIP_COMPRESSION_REFUSED";
            if((ulong)cursor+46u+(uint)name+(uint)extra+(uint)comment>(ulong)end)return "INVALID_ZIP";
            if(raw>options.MaxPartBytes)return "PART_SIZE_LIMIT";
            expanded+=raw;if(expanded>options.MaxExpandedBytes)return "EXPANDED_SIZE_LIMIT";
            if(raw>Math.Max(1L,compressed)*options.MaxExpansionRatio)return "ZIP_EXPANSION_LIMIT";
            int extraEnd=cursor+46+name+extra;
            for(int x=cursor+46+name;x<extraEnd;)
            {
                if(extraEnd-x<4)return "INVALID_ZIP";
                int kind=U16(x),size=U16(x+2);if(kind==1)return "ZIP64_REFUSED";
                x+=4+size;if(x>extraEnd)return "INVALID_ZIP";
            }
            if((ulong)local+30>(ulong)start||U32((int)local)!=0x04034b50)return "INVALID_ZIP";
            int offset=(int)local,localName=U16(offset+26),localExtra=U16(offset+28);
            if(U16(offset+6)!=flags||U16(offset+8)!=method||localName!=name)return "ZIP_HEADER_MISMATCH";
            if((ulong)local+30u+(uint)localName+(uint)localExtra+compressed>start)return "INVALID_ZIP";
            if(!bytes.AsSpan(cursor+46,name).SequenceEqual(bytes.AsSpan(offset+30,localName)))return "ZIP_HEADER_MISMATCH";
            cursor=extraEnd+comment;
        }
        return count==declared?null:"ZIP_DIRECTORY_COUNT";
    }
}
