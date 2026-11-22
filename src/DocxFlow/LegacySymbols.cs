using System.Buffers.Binary;
using System.Text;

namespace LecteurWord.Flow;

internal sealed record LegacySymbolDecision(string Family,int Code,int? DrawingCode,string Method,string Evidence);

internal static class LegacySymbols
{
    const string Adobe="https://www.unicode.org/Public/MAPPINGS/VENDORS/ADOBE/symbol.txt";
    const string Unicode="https://www.unicode.org/wg2/docs/n4363.pdf";

    internal static bool Private(int value)=>value is >=0xe000 and <=0xf8ff or >=0xf0000 and <=0xffffd or >=0x100000 and <=0x10fffd;

    internal static (int Code,string Face,string Source)? Mapping(string family,int sourceCode)
    {
        int code=sourceCode is >=0xf020 and <=0xf0ff?sourceCode-0xf000:sourceCode;
        if(family.Equals("Symbol",StringComparison.OrdinalIgnoreCase))
            return code switch
            {
                0x2b=>(0x002b,"DejaVu Serif",Adobe),
                0x2d=>(0x2212,"DejaVu Serif",Adobe),
                0xa7=>(0x2663,"DejaVu Serif",Adobe),
                0xb7=>(0x2022,"DejaVu Serif",Adobe),
                _=>null
            };
        if(!family.Equals("Wingdings",StringComparison.OrdinalIgnoreCase))return null;
        return code switch
        {
            0x77=>(0x1f799,"Noto Sans Symbols2",Unicode),
            >=0x8c and <=0x8e=>(0x278a+code-0x8c,"Noto Sans Symbols",Unicode),
            0xa7=>(0x25aa,"DejaVu Serif",Unicode),
            0xd8=>(0x2b9a,"Noto Sans Symbols2",Unicode),
            0xe8=>(0x1f87a,"Noto Sans Symbols2",Unicode),
            _=>null
        };
    }

    internal static bool SingleScalar(string value,out int code)
    {
        code=0;
        if(!Rune.TryGetRuneAt(value,0,out var rune)||rune.Utf16SequenceLength!=value.Length)return false;
        code=rune.Value;
        return true;
    }
}

// Microsoft Symbol encoding (3,0). A private code has no meaning outside its selected face.
internal static class SymbolCmap
{
    internal enum Status { Absent, Present, Invalid }
    static int U16(ReadOnlySpan<byte> table,int offset)=>BinaryPrimitives.ReadUInt16BigEndian(table[offset..]);

    internal static (Status Status,ushort Glyph) Lookup(ReadOnlySpan<byte> cmap,int code,int glyphCount,Action check)
    {
        if(cmap.Length<4||U16(cmap,0)!=0)return (Status.Invalid,0);
        int entries=U16(cmap,2);
        int directoryEnd=4+8*entries;
        if(entries is <1 or >256||directoryEnd>cmap.Length)return (Status.Invalid,0);
        int subtable=-1;
        for(int i=4;i<directoryEnd;i+=8)
        {
            check();
            if(U16(cmap,i)!=3||U16(cmap,i+2)!=0)continue;
            uint position=BinaryPrimitives.ReadUInt32BigEndian(cmap[(i+4)..]);
            if(subtable>=0||position<directoryEnd||position>cmap.Length-16)return (Status.Invalid,0);
            subtable=(int)position;
        }
        if(subtable<0)return (Status.Absent,0);
        var table=cmap[subtable..];
        int length=U16(table,2),segmentsTwice=U16(table,6);
        if(U16(table,0)!=4||U16(table,4)!=0||segmentsTwice is <2 or >8192||
            (segmentsTwice&1)!=0||(length&1)!=0||length>table.Length||length<16+4*segmentsTwice)
            return (Status.Invalid,0);
        table=table[..length];
        int starts=16+segmentsTwice,deltas=starts+segmentsTwice,ranges=deltas+segmentsTwice;
        int glyphArray=ranges+segmentsTwice,previous=-1;
        ushort glyph=0;
        // The OOXML byte form uses the same symbol slots at F020…F0FF.
        int alias=code is >=0x20 and <=0xff?code+0xf000:-1;
        ushort aliasGlyph=0;
        for(int i=0;i<segmentsTwice;i+=2)
        {
            check();
            int end=U16(table,14+i),start=U16(table,starts+i);
            int delta=U16(table,deltas+i),offset=U16(table,ranges+i);
            int first=ranges+i+offset;
            if(start<=previous||end<start||offset!=0&&((offset&1)!=0||first<glyphArray||first+2*(end-start)>length-2))
                return (Status.Invalid,0);
            previous=end;
            if(code>=start&&code<=end)glyph=ReadGlyph(table,code,start,offset,first,delta);
            if(alias>=start&&alias<=end)aliasGlyph=ReadGlyph(table,alias,start,offset,first,delta);
        }
        if(previous!=0xffff||glyph>=glyphCount||aliasGlyph>=glyphCount)return (Status.Invalid,0);
        return (Status.Present,aliasGlyph!=0?aliasGlyph:glyph);
    }

    static ushort ReadGlyph(ReadOnlySpan<byte> table,int code,int start,int offset,int first,int delta)
    {
        if(offset==0)return unchecked((ushort)(code+delta));
        int glyph=U16(table,first+2*(code-start));
        return glyph==0?(ushort)0:unchecked((ushort)(glyph+delta));
    }
}
