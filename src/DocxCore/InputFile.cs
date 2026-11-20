using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LecteurWord.DocxCore;
internal static class InputFile
{
    // Nonblocking open prevents a hostile FIFO path from hanging before cancellation can be checked.
    // NOFOLLOW also rejects symbolic sources. Linux constants; other platforms use the BCL path.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    public static FileStream Read(string path)
    {
        if (!OperatingSystem.IsLinux()) return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        int fd = Open(path, 0x800 | 0x20000 | 0x80000); // O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC
        if (fd < 0) throw new IOException("Input open refused.");
        var handle = new SafeFileHandle((IntPtr)fd, true);
        try
        {
            var stream = new FileStream(handle, FileAccess.Read, 65536);
            if (!stream.CanSeek) { stream.Dispose(); throw new IOException("Seekable input required."); }
            return stream;
        }
        catch { handle.Dispose(); throw; }
    }
}
