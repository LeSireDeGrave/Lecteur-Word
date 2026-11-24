using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace LecteurWord.Flow;

[EventSource(Name="LecteurWord-DocumentOpen")]
internal sealed class OpenTrace : EventSource
{
    internal static readonly OpenTrace Log=new();

    [Event(1,Level=EventLevel.Informational)]
    public void Phase(long request,string phase,string outcome,double elapsedMs,double cpuMs)
    {
        if(IsEnabled())WriteEvent(1,request,phase,outcome,elapsedMs,cpuMs);
    }

    [Event(2,Level=EventLevel.Informational)]
    public void AdmissionMemory(long request,long beforeBytes,long afterManagedBytes,long afterBytes,long limitBytes)
    {
        if(IsEnabled())WriteEvent(2,request,beforeBytes,afterManagedBytes,afterBytes,limitBytes);
    }
}

internal readonly struct OpenClock
{
    readonly long started;
    readonly long? cpuStarted;

    public OpenClock()
    {
        started=OpenTrace.Log.IsEnabled()?Stopwatch.GetTimestamp():0;
        cpuStarted=started!=0?ThreadCpuClock.Nanoseconds():null;
    }

    internal double ElapsedMs=>Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    internal double CpuMs=>cpuStarted is {} initial&&ThreadCpuClock.Nanoseconds() is {} now?
        (now-initial)/1_000_000d:-1;

    internal void Report(long request,string phase,string outcome)
    {
        if(started!=0)OpenTrace.Log.Phase(request,phase,outcome,ElapsedMs,CpuMs);
    }
}
