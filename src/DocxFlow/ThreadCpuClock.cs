using System.Runtime.InteropServices;

namespace LecteurWord.Flow;

internal static partial class ThreadCpuClock
{
    internal static long? Nanoseconds()
    {
        if(!OperatingSystem.IsLinux()||RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            return null;
        // Linux CLOCK_THREAD_CPUTIME_ID excludes time descheduled and other threads.
        return ClockGetTime(3,out var time)==0?time.Seconds*1_000_000_000L+time.Nanoseconds:null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Timespec
    {
        internal long Seconds,Nanoseconds;
    }

    [LibraryImport("libc",EntryPoint="clock_gettime")]
    private static partial int ClockGetTime(int clock,out Timespec time);
}
