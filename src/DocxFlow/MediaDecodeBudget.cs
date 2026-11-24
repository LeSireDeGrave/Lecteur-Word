using System.Diagnostics;

namespace LecteurWord.Flow;

internal sealed record MediaPhase(string Name,double ElapsedMs,double? CpuMs);

// Created and used inside one synchronous worker. The portable fallback retains the
// original elapsed-time limit when a per-thread CPU clock is unavailable.
internal sealed partial class MediaDecodeBudget(CancellationToken cancellation,List<MediaPhase>? phases=null)
{
    internal const string CpuLimit="MEDIA_CPU_LIMIT", WallLimit="MEDIA_WALL_LIMIT";
    const double CpuMilliseconds=1500, WallMilliseconds=12_000;
    readonly long started=Stopwatch.GetTimestamp();
    readonly long? cpuStarted=ThreadCpuClock.Nanoseconds();
    long previous=Stopwatch.GetTimestamp();
    long? cpuPrevious=ThreadCpuClock.Nanoseconds();
    int checkpoints;

    internal void Check()
    {
        cancellation.ThrowIfCancellationRequested();
        if(Stopwatch.GetElapsedTime(started).TotalMilliseconds>(cpuStarted is null?CpuMilliseconds:WallMilliseconds))
            throw new InvalidDataException(WallLimit);
        if(++checkpoints%32==0)CheckCpu();
    }

    internal void FinishPhase(string name)
    {
        long now=Stopwatch.GetTimestamp();
        long? cpu=ThreadCpuClock.Nanoseconds();
        phases?.Add(new(name,Stopwatch.GetElapsedTime(previous,now).TotalMilliseconds,(cpu-cpuPrevious)/1_000_000d));
        previous=now;
        cpuPrevious=cpu;
        Check();
        CheckCpu(cpu);
    }

    void CheckCpu(long? cpu=null)
    {
        if(cpuStarted is not {} initial)return;
        if((cpu??ThreadCpuClock.Nanoseconds()) is {} now)
        {
            if((now-initial)/1_000_000d>CpuMilliseconds)throw new InvalidDataException(CpuLimit);
        }
        else if(Stopwatch.GetElapsedTime(started).TotalMilliseconds>CpuMilliseconds)
            throw new InvalidDataException(WallLimit);
    }

    internal static bool IsLimit(string code)=>code is CpuLimit or WallLimit;

}
