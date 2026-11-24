using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LecteurWord.Platform;

internal sealed record PlatformPaths(string Config, string State, string Data)
{
    public static PlatformPaths ForCurrentUser() => new(
        Resolve("XDG_CONFIG_HOME", ".config"),
        Resolve("XDG_STATE_HOME", ".local/state"),
        Resolve("XDG_DATA_HOME", ".local/share"));

    static string Resolve(string variable, string fallback)
    {
        string? root = Environment.GetEnvironmentVariable(variable);
        return Path.Combine(root is not null && Path.IsPathFullyQualified(root)
            ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback), "lecteur-word");
    }
}

internal static class PlatformFiles
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowDuplicateProperties = false,
        MaxDepth = 24,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static void MakePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(path);
    }

    internal static T? Read<T>(string path, int limit = 512 * 1024)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(ReadBounded(stream, limit), Json);
    }

    internal static T? ReadOptional<T>(string path)
    {
        try { return Read<T>(path); }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }

    internal static byte[] ReadBounded(Stream input, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Taille maximale dépassée.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Taille maximale dépassée.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static void Write<T>(string path, T value) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    internal static void SaveReport(string path, string report) => WriteBytes(path, Encoding.UTF8.GetBytes(report), replace: false);

    internal static void WriteBytes(string path, ReadOnlySpan<byte> bytes, bool replace = true)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        MakePrivateDirectory(directory);
        string temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: replace);
        }
        finally { File.Delete(temp); }
    }
}
