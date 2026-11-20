using System.Diagnostics;
using System.Text.Json;

namespace LecteurWord.DocxCore;

public sealed class OutputLimitException(string code) : IOException(code);

/// <summary>Publishes a new result directory by rename; never replaces an existing file or directory.</summary>
public static class AtomicOutput
{
    public static void Write(InspectionResult result, string outputDirectory, CancellationToken cancellationToken = default)
    {
        string output = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string parent = Path.GetDirectoryName(output) ?? throw new IOException("Output parent required.");
        if (!Directory.Exists(parent) || Path.Exists(output)) throw new IOException("Existing parent and unused output name required.");
        for (DirectoryInfo? d = new(parent); d is not null; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic output ancestor refused.");
        string stage = Path.Combine(parent, ".docx-core-" + Guid.NewGuid().ToString("N"));
        var timer = Stopwatch.StartNew();
        long written = 0;
        void Check(int size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.ElapsedMilliseconds > result.Manifest.Budgets.MaxDurationMilliseconds) throw new OutputLimitException("OUTPUT_TIME_LIMIT");
            written = checked(written + size);
            if (written > result.Manifest.Budgets.MaxOutputBytes) throw new OutputLimitException("OUTPUT_SIZE_LIMIT");
        }
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stage);
        else Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using (var file = new FileStream(Path.Combine(stage, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var bounded = new BudgetWriteStream(file, Check))
            {
                JsonSerializer.Serialize(bounded, result.Manifest, ContractJson.Default.InspectionManifest);
                file.Flush(true);
            }
            if (result.Manifest.InspectionComplete && result.SafeMedia.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(stage, "assets"));
                foreach (var (id, bytes) in result.SafeMedia.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (id.Length != 71 || !id.StartsWith("sha256-", StringComparison.Ordinal) ||
                        id[7..].Any(c => !char.IsAsciiHexDigitLower(c)) ||
                        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != id[7..])
                        throw new IOException("Invalid asset identity.");
                    Check(bytes.Length);
                    using var asset = new FileStream(Path.Combine(stage, "assets", id + ".bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    for (int offset = 0; offset < bytes.Length; offset += 65536)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        asset.Write(bytes, offset, Math.Min(65536, bytes.Length - offset));
                    }
                    asset.Flush(true);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, output);
        }
        finally
        {
            // Only this invocation's fresh, private staging directory is eligible for removal.
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
    sealed class BudgetWriteStream(Stream target, Action<int> check) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => target.Length;
        public override long Position { get => target.Position; set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override void Write(byte[] buffer, int offset, int count) { check(count); target.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { check(buffer.Length); target.Write(buffer); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
