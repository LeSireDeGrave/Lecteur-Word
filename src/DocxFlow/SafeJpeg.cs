using System.Buffers.Binary;
using System.Diagnostics;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

/// <summary>Bounded baseline JPEG gate. Metadata is removed before native decoding; EXIF orientation is applied explicitly.</summary>
public static class SafeJpeg
{
    public static bool TryGetRenderableBytes(byte[] input,out byte[] output,out int width,out int height,out int orientation,out string reason,CancellationToken token=default)
    {
        output=[];width=height=0;orientation=1;reason="JPEG refusé : structure, décodage ou budget hors profil.";
        if(!JpegHeader.TryRead(input,out int encodedWidth,out int encodedHeight)||encodedWidth>8192||encodedHeight>8192||(long)encodedWidth*encodedHeight>8_000_000)return false;
        var watch=Stopwatch.StartNew();void Check(){token.ThrowIfCancellationRequested();if(watch.ElapsedMilliseconds>1500)throw new InvalidDataException("JPEG_TIME_LIMIT");}
        try
        {
            using var clean=new MemoryStream();clean.Write(input,0,2);int cursor=2,segments=0;bool ended=false,exif=false,omitted=false;
            while(cursor<input.Length)
            {
                Check();if(++segments>4096||input[cursor]!=255)return false;int start=cursor++;
                while(cursor<input.Length&&input[cursor]==255)cursor++;if(cursor+3>input.Length)return false;
                byte marker=input[cursor++];int size=BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(cursor,2));if(size<2||size>input.Length-cursor)return false;
                var data=input.AsSpan(cursor+2,size-2);bool keep=true;
                if(marker is >=224 and <=239||marker==254)
                {
                    keep=false;omitted=true;
                    if(marker==225&&data.StartsWith("Exif\0\0"u8))
                    {if(exif||!ReadOrientation(data[6..],out orientation))return false;exif=true;}
                    // Adobe's transform flag changes color interpretation and is not disposable metadata.
                    if(marker==238)
                    {if(data.Length!=12||!data.StartsWith("Adobe"u8)||data[11]>1)return false;keep=true;}
                }
                else if(marker is not (192 or 196 or 219 or 221 or 218))return false;
                if(keep)clean.Write(input,start,cursor+size-start);cursor+=size;
                if(marker==218)
                {
                    int entropy=cursor;
                    while(cursor<input.Length)
                    {
                        Check();if(input[cursor++]!=255)continue;
                        if(cursor>=input.Length)return false;byte next=input[cursor++];
                        if(next==0||next is >=208 and <=215)continue;
                        if(next!=217||cursor!=input.Length)return false;
                        clean.Write(input,entropy,cursor-entropy);ended=true;break;
                    }
                    break;
                }
            }
            if(!ended)return false;Check();byte[] compressed=clean.ToArray();
            using var stream=new SKMemoryStream(compressed);using var codec=SKCodec.Create(stream);
            if(codec is null||codec.Info.Width!=encodedWidth||codec.Info.Height!=encodedHeight)return false;
            using var decoded=new SKBitmap(new SKImageInfo(encodedWidth,encodedHeight,SKColorType.Rgba8888,SKAlphaType.Opaque));
            if(codec.GetPixels(decoded.Info,decoded.GetPixels())!=SKCodecResult.Success)return false;Check();
            width=orientation>=5?encodedHeight:encodedWidth;height=orientation>=5?encodedWidth:encodedHeight;
            using var oriented=new SKBitmap(new SKImageInfo(width,height,SKColorType.Rgba8888,SKAlphaType.Opaque));
            // Fixed affine transforms preserve all source pixels, including mirrored EXIF origins.
            using(var canvas=new SKCanvas(oriented))
            {
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
            Check();using var image=SKImage.FromBitmap(oriented);using var png=image.Encode(SKEncodedImageFormat.Png,100);
            if(png is null||png.Size>8*1024*1024)return false;output=png.ToArray();Check();
            reason=orientation!=1?"Orientation EXIF appliquée à la copie de rendu ; dimensions Word, source et empreinte conservées.":omitted?"Métadonnées JPEG omises de la copie de rendu ; source et empreinte conservées.":"";return true;
        }
        catch(InvalidDataException){return false;}
        catch(OverflowException){return false;}
    }
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
