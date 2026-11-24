using System.Buffers.Binary;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

/// <summary>Bounded baseline JPEG gate. Adobe CMYK retains a checked ICC profile until conversion to sRGB.</summary>
public static class SafeJpeg
{
    internal const string ColorUnqualified="MEDIA_JPEG_ICC_UNQUALIFIED";

    public static bool TryGetRenderableBytes(byte[] input,out byte[] output,out int width,out int height,out int orientation,out string reason,CancellationToken token=default)
        =>Decode(input,out output,out width,out height,out orientation,out reason,new(token));

    internal static bool Decode(byte[] input,out byte[] output,out int width,out int height,out int orientation,out string reason,
        MediaDecodeBudget budget)
    {
        output=[];width=height=0;orientation=1;reason="JPEG refusé : structure, décodage ou budget hors profil.";
        if(!JpegHeader.TryRead(input,out int encodedWidth,out int encodedHeight,out int components)||
            encodedWidth>8192||encodedHeight>8192||(long)encodedWidth*encodedHeight>8_000_000)return false;
        bool cmyk=components==4;
        void Check()=>budget.Check();
        try
        {
            using var clean=new MemoryStream();clean.Write(input,0,2);int cursor=2,segments=0;bool ended=false,exif=false,omitted=false,adobe=false;
            using var profile=cmyk?new MemoryStream():null;
            int iccCount=0,iccNext=1;
            while(cursor<input.Length)
            {
                Check();if(++segments>4096||input[cursor]!=255)return false;int start=cursor++;
                while(cursor<input.Length&&input[cursor]==255)cursor++;if(cursor+3>input.Length)return false;
                byte marker=input[cursor++];int size=BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(cursor,2));if(size<2||size>input.Length-cursor)return false;
                var data=input.AsSpan(cursor+2,size-2);bool keep=true;
                if(marker is >=224 and <=239||marker==254)
                {
                    keep=false;
                    if(marker==225&&data.StartsWith("Exif\0\0"u8))
                    {if(exif||!ReadOrientation(data[6..],out orientation))return false;exif=true;}
                    // Adobe's transform flag changes color interpretation and is not disposable metadata.
                    if(marker==238)
                    {
                        if(adobe||data.Length!=12||!data.StartsWith("Adobe"u8)||data[11]>(cmyk?0:1))return false;
                        adobe=keep=true;
                    }
                    if(marker==226&&data.StartsWith("ICC_PROFILE\0"u8))
                    {
                        if(!cmyk){reason=ColorUnqualified;return false;}
                        if(data.Length<=14||data[12]!=iccNext||data[13] is <1 or >32||
                            iccCount!=0&&data[13]!=iccCount||iccNext>data[13]||profile!.Length+data.Length-14>1_048_576)
                            return false;
                        iccCount=data[13];iccNext++;
                        profile!.Write(data[14..]);keep=true;
                    }
                    omitted|=!keep;
                }
                else if(marker is not (192 or 196 or 219 or 221 or 218))return false;
                if(keep)clean.Write(input,start,cursor+size-start);cursor+=size;
                if(marker==218)
                {
                    if(cmyk&&(data.Length!=12||data[0]!=4||!adobe||iccNext!=iccCount+1||
                        !CheckCmykProfile(profile!.GetBuffer().AsSpan(0,(int)profile.Length))))return false;
                    int entropy=cursor;
                    while(cursor<input.Length)
                    {
                        Check();
                        int length=Math.Min(4096,input.Length-cursor);
                        int markerOffset=input.AsSpan(cursor,length).IndexOf((byte)255);
                        if(markerOffset<0){cursor+=length;continue;}
                        cursor+=markerOffset+1;
                        if(cursor>=input.Length)return false;byte next=input[cursor++];
                        if(next==0||next is >=208 and <=215)continue;
                        if(next!=217||cursor!=input.Length)return false;
                        clean.Write(input,entropy,cursor-entropy);ended=true;break;
                    }
                    break;
                }
            }
            if(!ended)return false;
            budget.FinishPhase("JPEG structure et ICC");
            byte[] compressed=clean.ToArray();
            using var stream=new SKMemoryStream(compressed);using var codec=SKCodec.Create(stream);
            if(codec is null||codec.Info.Width!=encodedWidth||codec.Info.Height!=encodedHeight)return false;
            using var srgb=cmyk?SKColorSpace.CreateSrgb():null;
            using var decoded=new SKBitmap(new SKImageInfo(encodedWidth,encodedHeight,SKColorType.Rgba8888,SKAlphaType.Opaque,srgb));
            if(codec.GetPixels(decoded.Info,decoded.GetPixels())!=SKCodecResult.Success)return false;
            budget.FinishPhase("JPEG décodage et conversion couleur");
            width=orientation>=5?encodedHeight:encodedWidth;height=orientation>=5?encodedWidth:encodedHeight;
            using var oriented=orientation==1?null:new SKBitmap(new SKImageInfo(width,height,SKColorType.Rgba8888,SKAlphaType.Opaque,srgb));
            // Fixed affine transforms preserve all source pixels, including mirrored EXIF origins.
            if(oriented is not null)
            {
                using var canvas=new SKCanvas(oriented);
                var matrix=orientation switch
                {
                    2=>new SKMatrix(-1,0,encodedWidth,0,1,0,0,0,1),
                    3=>new SKMatrix(-1,0,encodedWidth,0,-1,encodedHeight,0,0,1),
                    4=>new SKMatrix(1,0,0,0,-1,encodedHeight,0,0,1),
                    5=>new SKMatrix(0,1,0,1,0,0,0,0,1),
                    6=>new SKMatrix(0,-1,encodedHeight,1,0,0,0,0,1),
                    7=>new SKMatrix(0,-1,encodedHeight,-1,0,encodedWidth,0,0,1),
                    8=>new SKMatrix(0,1,0,-1,0,encodedWidth,0,0,1),
                    _=>SKMatrix.Identity
                };
                canvas.SetMatrix(matrix);canvas.DrawBitmap(decoded,0,0);
            }
            budget.FinishPhase("JPEG orientation");
            using var image=SKImage.FromBitmap(oriented??decoded);using var png=image.Encode(SKEncodedImageFormat.Png,100);
            if(png is null||png.Size>8*1024*1024)return false;
            output=png.ToArray();
            budget.FinishPhase("JPEG encodage du raster");
            reason=cmyk?"Profil ICC CMJN utilisé au décodage sRGB. Source et empreinte conservées.":
                orientation!=1?"Orientation EXIF appliquée à la copie de rendu ; dimensions Word, source et empreinte conservées.":
                omitted?"Métadonnées JPEG omises de la copie de rendu ; source et empreinte conservées.":"";
            return true;
        }
        catch(InvalidDataException error) when(MediaDecodeBudget.IsLimit(error.Message))
        {
            output=[];width=height=0;orientation=1;
            reason=error.Message;
            return false;
        }
        catch(InvalidDataException){return false;}
        catch(OverflowException){return false;}
    }

    static bool CheckCmykProfile(ReadOnlySpan<byte> profile)
    {
        if(profile.Length<132||Read32(profile,0)!=profile.Length||profile[8]!=2||
            !profile[12..20].SequenceEqual("prtrCMYK"u8)||
            !(profile[20..24].SequenceEqual("Lab "u8)||profile[20..24].SequenceEqual("XYZ "u8))||
            !profile[36..40].SequenceEqual("acsp"u8)||Read32(profile,64)>3)return false;
        uint count=Read32(profile,128);
        if(count is <1 or >64||132+count*12>profile.Length)return false;
        uint firstData=132+count*12;
        var ids=new HashSet<uint>();
        var ranges=new List<(uint Start,uint End)>();
        bool conversion=false;
        for(int i=0;i<count;i++)
        {
            var entry=profile.Slice(132+i*12,12);
            uint id=Read32(entry,0),offset=Read32(entry,4),size=Read32(entry,8);
            if(!ids.Add(id)||offset<firstData||offset%4!=0||offset>profile.Length||size<8||size>profile.Length-offset)return false;
            uint end=offset+size;
            if(ranges.Any(r=>r.Start<end&&offset<r.End&&(r.Start!=offset||r.End!=end)))return false;
            ranges.Add((offset,end));
            var tag=profile.Slice((int)offset,(int)size);
            bool forward=entry[..3].SequenceEqual("A2B"u8);
            bool reverse=entry[..3].SequenceEqual("B2A"u8);
            if(forward||reverse||entry[..4].SequenceEqual("gamt"u8))
            {
                bool sixteen=tag[..4].SequenceEqual("mft2"u8);
                if((!sixteen&&!tag[..4].SequenceEqual("mft1"u8))||tag.Length<(sixteen?52:48)||
                    tag[8]!=(forward?4:3)||tag[9]!=(forward?3:reverse?4:1)||tag[10] is <2 or >33)return false;
                int grid=1;
                for(int axis=0;axis<tag[8];axis++)grid*=tag[10];
                int input=sixteen?BinaryPrimitives.ReadUInt16BigEndian(tag[48..50]):256;
                int output=sixteen?BinaryPrimitives.ReadUInt16BigEndian(tag[50..52]):256;
                int required=(sixteen?52:48)+(sixteen?2:1)*(tag[8]*input+grid*tag[9]+output*tag[9]);
                if(input is <2 or >4096||output is <2 or >4096||tag.Length<required||tag.Length-required>3)return false;
                conversion|=entry[..4].SequenceEqual("A2B0"u8);
            }
            else if(!(tag[..4].SequenceEqual("desc"u8)||tag[..4].SequenceEqual("text"u8)||tag[..4].SequenceEqual("XYZ "u8)||
                tag[..4].SequenceEqual("sig "u8)||tag[..4].SequenceEqual("view"u8)))return false;
        }
        return conversion;
    }

    static uint Read32(ReadOnlySpan<byte> data,int offset)=>BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset,4));
    static bool ReadOrientation(ReadOnlySpan<byte> tiff,out int orientation)
    {
        orientation=1;if(tiff.Length<8)return false;
        bool little=tiff[..2].SequenceEqual("II"u8);if(!little&&!tiff[..2].SequenceEqual("MM"u8))return false;
        if(U16(tiff.Slice(2,2),little)!=42)return false;
        uint offset=U32(tiff.Slice(4,4),little);if(offset<8||offset>(uint)tiff.Length-2)return false;
        int start=(int)offset,count=U16(tiff.Slice(start,2),little);if(count>256||start+2L+count*12L+4>tiff.Length)return false;bool found=false;
        for(int i=0;i<count;i++)
        {
            var entry=tiff.Slice(start+2+i*12,12);if(U16(entry[..2],little)!=274)continue;
            if(found||U16(entry.Slice(2,2),little)!=3||U32(entry.Slice(4,4),little)!=1)return false;
            orientation=U16(entry.Slice(8,2),little);if(orientation is <1 or >8)return false;found=true;
        }
        return true;
    }
    static ushort U16(ReadOnlySpan<byte> data,bool little)=>little?BinaryPrimitives.ReadUInt16LittleEndian(data):BinaryPrimitives.ReadUInt16BigEndian(data);
    static uint U32(ReadOnlySpan<byte> data,bool little)=>little?BinaryPrimitives.ReadUInt32LittleEndian(data):BinaryPrimitives.ReadUInt32BigEndian(data);
}
