using System.Buffers.Binary;
using System.Security.Cryptography;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class RasterAdmissionCases
{
    internal static void Run(string root)
    {
        var png=Fixtures.Png;
        string hash=Convert.ToHexStringLower(SHA256.HashData(png));
        var media=new MediaResource("image","/word/media/image.png",hash,png.Length,"image/png","test",1,1);
        var cache=new DocumentRasterCache();
        var first=cache.ReadAsync(media,png);
        var concurrent=cache.ReadAsync(media,png);
        var (a,b)=(first.GetAwaiter().GetResult(),concurrent.GetAwaiter().GetResult());
        Check(a.Raster is {} raster&&ReferenceEquals(raster,b.Raster)&&png.SequenceEqual(raster.Bytes)&&
            raster.Phases.Select(p=>p.Name).SequenceEqual(new[]{"PNG structure et CRC","PNG validation de l’inflation",
                "PNG validation du décodage natif"}),
            "Une admission partagée conserve le PNG et vérifie aussi le décodage natif.");
        var warm=cache.ReadAsync(media with{Id="second"},png).GetAwaiter().GetResult();
        Check(ReferenceEquals(warm.Raster,a.Raster),"L’empreinte du média identifie deux occurrences du même raster.");
        byte[] corrupted=(byte[])png.Clone();corrupted[20]^=1;
        Check(cache.ReadAsync(media,corrupted).GetAwaiter().GetResult().Failure==DocumentRasterCache.HashMismatch,
            "Le résultat mis en cache ne masque pas une empreinte incohérente.");
        var damaged=media with{Sha256=Convert.ToHexStringLower(SHA256.HashData(corrupted))};
        Check(cache.ReadAsync(damaged,corrupted).GetAwaiter().GetResult().Failure==DocumentRasterCache.DecodeRefused,
            "Un CRC invalide reste refusé.");
        byte[] gamma=Chunk("gAMA"u8,[0,0,177,143]);
        byte[] srgb=Chunk("sRGB"u8,[0]);
        byte[] described=[..png.AsSpan(0,33),..gamma,..srgb,..png.AsSpan(33)];
        var rendered=Read(described);
        Check(rendered.Raster is {} standard&&standard.Bytes.SequenceEqual(
                png.Take(33).Concat(srgb).Concat(png.Skip(33))),
            "La déclaration sRGB prévaut sur le gamma annexe et atteint le décodeur natif.");
        Check(Read([..png.AsSpan(0,33),..gamma,..png.AsSpan(33)]).Failure==SafePng.ColorUnqualified,
            "Un gamma autonome ne peut pas devenir un raster sRGB par omission.");
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        bool cancellation=false;
        try{cache.ReadAsync(media,png,cancelled.Token).GetAwaiter().GetResult();}
        catch(OperationCanceledException){cancellation=true;}
        Check(cancellation,"Une admission annulée ne retourne pas de raster, même disponible.");

        RasterAdmission Read(byte[] input)=>cache.ReadAsync(media with
        {
            Sha256=Convert.ToHexStringLower(SHA256.HashData(input)),Bytes=input.Length
        },input).GetAwaiter().GetResult();
    }

    static byte[] Chunk(ReadOnlySpan<byte> type,ReadOnlySpan<byte> data)
    {
        byte[] bytes=new byte[12+data.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes,data.Length);
        type.CopyTo(bytes.AsSpan(4));
        data.CopyTo(bytes.AsSpan(8));
        uint crc=uint.MaxValue;
        foreach(byte value in bytes.AsSpan(4,4+data.Length))
        {
            crc^=value;
            for(int bit=0;bit<8;bit++)crc=(crc&1)==0?crc>>1:(crc>>1)^0xedb88320;
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8+data.Length),~crc);
        return bytes;
    }

    static void Check(bool valid,string detail)=>LineCases.Check(valid,detail);
}
