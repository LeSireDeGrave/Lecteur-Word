using System.Buffers.Binary;

namespace LecteurWord.DocxCore;

/// <summary>Dimension probe only: 8-bit baseline JPEG, one or three components. No native decoder.</summary>
public static class JpegHeader
{
    public static bool TryRead(ReadOnlySpan<byte> data,out int width,out int height)
    {
        width=height=0;
        if(data.Length is < 20 or > 8*1024*1024||data[0]!=255||data[1]!=216)return false;
        int cursor=2,segments=0;bool frame=false;
        while(cursor<data.Length&&++segments<=4096)
        {
            if(data[cursor++]!=255)return false;
            while(cursor<data.Length&&data[cursor]==255)cursor++;
            if(cursor>=data.Length)return false;byte marker=data[cursor++];
            if(marker is 0 or 216 or 217||marker is >=208 and <=215||cursor+2>data.Length)return false;
            int size=BinaryPrimitives.ReadUInt16BigEndian(data.Slice(cursor,2));
            if(size<2||size>data.Length-cursor)return false;
            if(marker==192)
            {
                if(frame||size<11||data[cursor+2]!=8)return false;
                height=BinaryPrimitives.ReadUInt16BigEndian(data.Slice(cursor+3,2));width=BinaryPrimitives.ReadUInt16BigEndian(data.Slice(cursor+5,2));int components=data[cursor+7];
                if(components is not (1 or 3)||size!=8+3*components||width<=0||height<=0)return false;frame=true;
            }
            else if(marker is >=193 and <=207&&marker is not (196 or 200 or 204))return false;
            if(marker==218)return frame&&size>=6;
            cursor+=size;
        }
        return false;
    }
}
