using System.Buffers.Binary;
using System.IO.Compression;

namespace LecteurWord.Flow;

/// <summary>Conservative PNG gate for the reading view, not a decoder sandbox.</summary>
public static class SafePng
{
    internal const string ColorUnqualified="MEDIA_PNG_COLOR_UNQUALIFIED";
    const int MaxBytes = 8 * 1024 * 1024, MaxDimension = 8192;
    const long MaxPixels = 8_000_000, MaxInflated = 32 * 1024 * 1024;
    static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint value = (uint)i;
        for (int k = 0; k < 8; k++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();
    public static bool Validate(byte[] input) => TryGetRenderableBytes(input, out _, out _);
    public static bool TryGetRenderableBytes(byte[] input, out byte[] renderable, out string reason, CancellationToken cancellationToken = default)
        =>Decode(input,out renderable,out reason,new(cancellationToken));

    internal static bool Decode(byte[] input,out byte[] renderable,out string reason,MediaDecodeBudget budget)
    {
        renderable = []; reason = "PNG refusé : structure ou budget non qualifié.";
        if (input.Length is < 57 or > MaxBytes || !input.AsSpan(0, 8).SequenceEqual(Signature)) return false;
        void Check()=>budget.Check();
        try
        {
            using var compressed = new MemoryStream(); using var clean = new MemoryStream(); clean.Write(Signature);
            int cursor = 8, width = 0, height = 0, channels = 0, palette = 0, color = -1, chunks = 0;
            bool header = false, idat = false, idatEnded = false, ended = false, transparency = false, omitted = false;
            bool srgb=false,gamma=false,chromaticities=false;
            while (cursor < input.Length)
            {
                Check(); if (++chunks > 4096 || input.Length - cursor < 12) return false;
                uint length = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(cursor, 4));
                if (length > MaxBytes || (ulong)cursor + 12 + length > (ulong)input.Length) return false;
                int size = (int)length; var type = input.AsSpan(cursor + 4, 4); var data = input.AsSpan(cursor + 8, size);
                if (!type.ToArray().All(b => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z') || (type[2] & 0x20) != 0) return false;
                uint crc = uint.MaxValue;
                foreach (byte b in input.AsSpan(cursor + 4, size + 4)) crc = Table[(crc ^ b) & 255] ^ (crc >> 8);
                if (~crc != BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(cursor + 8 + size, 4))) return false;
                bool keep = true;
                if (type.SequenceEqual("IHDR"u8))
                {
                    if (header || cursor != 8 || size != 13) return false;
                    uint w = BinaryPrimitives.ReadUInt32BigEndian(data[..4]), h = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                    if (w is 0 or > MaxDimension || h is 0 or > MaxDimension || (long)w * h > MaxPixels || data[8] != 8 || data[10] != 0 || data[11] != 0 || data[12] != 0) return false;
                    width = (int)w; height = (int)h; color = data[9];
                    channels = color switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
                    if (channels == 0 || ((long)width * channels + 1) * height > MaxInflated) return false;
                    header = true;
                }
                else if (!header) return false;
                else if(type.SequenceEqual("sRGB"u8))
                {
                    if(srgb||palette>0||idat||size!=1||data[0]>3)return false;
                    srgb=true;
                }
                else if(type.SequenceEqual("gAMA"u8))
                {
                    if(gamma||palette>0||idat||size!=4||BinaryPrimitives.ReadUInt32BigEndian(data)==0)return false;
                    gamma=true;keep=false;
                }
                else if(type.SequenceEqual("cHRM"u8))
                {
                    if(chromaticities||palette>0||idat||size!=32)return false;
                    chromaticities=true;keep=false;
                }
                else if(type.SequenceEqual("iCCP"u8)||type.SequenceEqual("cICP"u8))
                {reason=ColorUnqualified;return false;}
                else if (type.SequenceEqual("PLTE"u8))
                {
                    if (palette > 0 || idat || size == 0 || size % 3 != 0 || size > 768 || color is 0 or 4) return false;
                    palette = size / 3;
                }
                else if (type.SequenceEqual("tRNS"u8))
                {
                    if (transparency || idat || color is 4 or 6 || color == 0 && size != 2 || color == 2 && size != 6 || color == 3 && (palette == 0 || size == 0 || size > palette)) return false;
                    if (color == 0 && data[0] != 0 || color == 2 && (data[0] != 0 || data[2] != 0 || data[4] != 0)) return false;
                    transparency = true;
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    if (idatEnded || color == 3 && palette == 0 || compressed.Length + size > MaxBytes) return false;
                    idat = true; compressed.Write(data);
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    if (!idat || size != 0 || cursor + 12 != input.Length) return false;
                    ended = true;
                }
                else
                {
                    if (type[0] is >= (byte)'A' and <= (byte)'Z') return false;
                    // Do not feed ICC/text/exif or other metadata parsers of the native decoder.
                    keep = false; omitted = true;
                }
                if (idat && !type.SequenceEqual("IDAT"u8)) idatEnded = true;
                if (keep) clean.Write(input.AsSpan(cursor, size + 12));
                cursor += size + 12;
                if (ended) break;
            }
            if (!header || !ended || compressed.Length == 0) return false;
            // sRGB takes precedence over gAMA/cHRM. A standalone profile needs a qualified color conversion.
            if(!srgb&&(gamma||chromaticities)){reason=ColorUnqualified;return false;}
            budget.FinishPhase("PNG structure et CRC");
            compressed.Position = 0;
            using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress, true))
            {
                int row = checked(width * channels); byte[] scratch = new byte[Math.Min(8192, row)];
                for (int y = 0; y < height; y++)
                {
                    Check(); int filter = inflate.ReadByte(); if (filter is < 0 or > 4) return false;
                    int remaining = row;
                    while (remaining > 0)
                    {
                        Check(); int read = inflate.Read(scratch, 0, Math.Min(remaining, scratch.Length));
                        if (read == 0) return false; remaining -= read;
                    }
                }
                if (inflate.ReadByte() != -1) return false;
            }
            budget.FinishPhase("PNG validation de l’inflation");
            renderable = clean.ToArray();
            reason = omitted ? "Métadonnées PNG non nécessaires au décodage omises dans la copie de rendu ; original et empreinte conservés." : "";
            return true;
        }
        catch (InvalidDataException error) when(MediaDecodeBudget.IsLimit(error.Message))
        { renderable=[];reason=error.Message;return false; }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (OverflowException) { return false; }
    }
}
