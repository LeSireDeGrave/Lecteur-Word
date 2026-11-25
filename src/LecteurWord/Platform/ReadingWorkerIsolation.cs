using System.Runtime.InteropServices;

namespace LecteurWord.Platform;

internal static class ReadingWorkerIsolation
{
    const int SetDumpable = 4;
    const int SetNoNewPrivileges = 38;
    const int Seccomp = 317;
    const int SetFilter = 1;
    const int SynchronizeThreads = 1;
    const uint AuditX64 = 0xc000003e;
    const uint KillProcess = 0x80000000;
    const uint Allow = 0x7fff0000;
    const uint PermissionDenied = 0x00050001;

    // Linux x86-64 syscall numbers. The worker needs only existing file descriptors,
    // managed memory, signals, clocks and thread synchronization after initialization.
    static readonly int[] AllowedSyscalls =
    [
        0, 1, 3, 5, 7, 8, 9, 10, 11, 12,          // read, write, close, fstat, poll, lseek, mmap, mprotect, munmap, brk
        13, 14, 15, 16, 24, 25, 28,               // signals, ioctl on existing fds, sched_yield, mremap, madvise
        35, 39, 60, 72, 96,                     // nanosleep, PID, exit, fcntl, time
        131, 158, 186, 202, 204, 218,             // alternate signal stack, thread state, futex, affinity
        228, 230, 231, 273, 318, 334,             // clocks, exit_group, thread robustness, random, rseq
    ];

    internal static bool Apply()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            prctl(SetDumpable, 0, 0, 0, 0) != 0 || prctl(SetNoNewPrivileges, 1, 0, 0, 0) != 0)
            return false;

        const ushort LoadWord = 0x20;
        const ushort JumpIfEqual = 0x15;
        const ushort Return = 0x06;
        List<Instruction> instructions =
        [
            new(LoadWord, 0, 0, 4), // seccomp_data.arch
            new(JumpIfEqual, 1, 0, AuditX64),
            new(Return, 0, 0, KillProcess),
            new(LoadWord, 0, 0, 0), // seccomp_data.nr
        ];
        foreach (int syscallNumber in AllowedSyscalls)
        {
            instructions.Add(new(JumpIfEqual, 0, 1, (uint)syscallNumber));
            instructions.Add(new(Return, 0, 0, Allow));
        }
        instructions.Add(new(Return, 0, 0, PermissionDenied));
        var pinned = GCHandle.Alloc(instructions.ToArray(), GCHandleType.Pinned);
        try
        {
            var program = new Filter((ushort)instructions.Count, pinned.AddrOfPinnedObject());
            return syscall(Seccomp, SetFilter, SynchronizeThreads, in program) == 0;
        }
        finally { pinned.Free(); }
    }

    internal static byte[] ReadRequest(int limit)
    {
        using var input = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            nint count = read(0, ref buffer[0], (nuint)buffer.Length);
            if (count == 0) return input.ToArray();
            if (count < 0 && Marshal.GetLastPInvokeError() == 4) continue; // EINTR
            if (count < 0 || input.Length + count > limit)
                throw new IOException("Entrée du processus d’extension invalide ou trop longue.");
            input.Write(buffer, 0, (int)count);
        }
    }

    internal static void WriteResponse(byte[] response)
    {
        int offset = 0;
        while (offset < response.Length)
        {
            nint count = write(1, ref response[offset], (nuint)(response.Length - offset));
            if (count < 0 && Marshal.GetLastPInvokeError() == 4) continue; // EINTR
            if (count <= 0) throw new IOException("Impossible d’écrire la réponse du processus d’extension.");
            offset += (int)count;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Instruction(ushort Code, byte JumpTrue, byte JumpFalse, uint Value);

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Filter(ushort Length, nint Instructions);

    [DllImport("libc")]
    static extern int prctl(int operation, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [DllImport("libc")]
    static extern nint syscall(nint number, uint operation, uint flags, in Filter program);

    [DllImport("libc", SetLastError = true)]
    static extern nint read(int fd, ref byte buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    static extern nint write(int fd, ref byte buffer, nuint count);
}
