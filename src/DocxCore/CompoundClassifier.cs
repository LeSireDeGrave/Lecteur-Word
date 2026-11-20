using System.Buffers.Binary;
using System.Text;

namespace LecteurWord.DocxCore;

/// <summary>Reads only CFB allocation and directory metadata. Never reads or executes stored objects.</summary>
internal static class CompoundClassifier
{
    public static string Classify(byte[] bytes, Action check)
    {
        const uint End = 0xfffffffe, Free = 0xffffffff;
        if (bytes.Length < 512) return "invalidCompoundOffice";
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        int shift = U16(30), major = U16(26);
        if (U16(28) != 0xfffe || !(major == 3 && shift == 9 || major == 4 && shift == 12)) return "invalidCompoundOffice";
        int size = 1 << shift;
        int sectors = bytes.Length / size - 1;
        if (sectors < 1 || bytes.Length % size != 0) return "invalidCompoundOffice";
        int Offset(uint sector) => sector < sectors ? checked(((int)sector + 1) * size) : throw new InvalidDataException("Invalid CFB sector.");
        var fats = new List<uint>();
        for (int i = 0; i < 109; i++) { uint id = U32(76 + i * 4); if (id != Free) fats.Add(id); }
        uint difat = U32(68), difatCount = U32(72);
        var visitedDifat = new HashSet<uint>();
        if (difatCount > sectors) return "invalidCompoundOffice";
        for (int i = 0; i < difatCount; i++)
        {
            check();
            if (!visitedDifat.Add(difat)) return "invalidCompoundOffice";
            int offset = Offset(difat);
            for (int k = 0; k < size / 4 - 1; k++) { uint id = U32(offset + k * 4); if (id != Free) fats.Add(id); }
            if (fats.Count > sectors) return "invalidCompoundOffice";
            difat = U32(offset + size - 4);
        }
        if (fats.Count != U32(44) || fats.Count == 0 || fats.Count > sectors || fats.Distinct().Count() != fats.Count)
            return "invalidCompoundOffice";
        foreach (uint fat in fats) _ = Offset(fat);
        uint Next(uint sector)
        {
            int fatIndex = (int)(sector / (size / 4));
            if (fatIndex >= fats.Count) throw new InvalidDataException("Missing CFB FAT.");
            return U32(Offset(fats[fatIndex]) + (int)(sector % (size / 4)) * 4);
        }
        uint directory = U32(48);
        var visited = new HashSet<uint>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (directory != End)
        {
            check();
            if (!visited.Add(directory) || visited.Count > sectors) return "invalidCompoundOffice";
            int offset = Offset(directory);
            for (int i = 0; i < size; i += 128)
            {
                int p = offset + i;
                if (bytes[p + 66] != 2) continue; // Stream directory entry only.
                int length = U16(p + 64);
                if (length < 2 || length > 64 || length % 2 != 0 || U16(p + length - 2) != 0)
                    return "invalidCompoundOffice";
                names.Add(Encoding.Unicode.GetString(bytes, p, length - 2));
            }
            directory = Next(directory);
        }
        if (names.Contains("EncryptedPackage") && names.Contains("EncryptionInfo")) return "encryptedOffice";
        if (names.Contains("WordDocument")) return "legacyDoc";
        return "compoundOffice";
    }
}
