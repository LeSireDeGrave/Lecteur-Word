using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

namespace LecteurWord;

internal static class ReaderOpenMemory
{
    static readonly object Reclamation=new();

    internal static void Prepare(InspectionOptions options,long request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var clock=new OpenClock();
        using var process=Process.GetCurrentProcess();
        long before=process.WorkingSet64,afterManaged=before,after=before;
        // Leave headroom for the expanded package before starting the inspector.
        // Its process-wide limit also counts unused managed pages from previous documents.
        long threshold=options.MaxWorkingSetBytes-options.MaxExpandedBytes;
        if(before>=threshold)
        {
            lock(Reclamation)
            {
                token.ThrowIfCancellationRequested();
                process.Refresh();
                after=afterManaged=process.WorkingSet64;
                if(after>=threshold)
                {
                    GCSettings.LargeObjectHeapCompactionMode=GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,blocking:true,compacting:true);
                    process.Refresh();
                    after=afterManaged=process.WorkingSet64;
                    clock.Report(request,"admission","compacted");
                    if(after>=threshold&&OperatingSystem.IsLinux()&&TrimNativeHeap())
                    {
                        process.Refresh();
                        after=process.WorkingSet64;
                        clock.Report(request,"admission","native-pages-released");
                    }
                }
            }
        }
        OpenTrace.Log.AdmissionMemory(request,before,afterManaged,after,options.MaxWorkingSetBytes);
        token.ThrowIfCancellationRequested();
    }

    static bool TrimNativeHeap()
    {
        // glibc may keep free resident pages in the arenas used by raster work.
        // Other Linux C libraries may not provide this operation.
        try {return MallocTrim(0)!=0;}
        catch(EntryPointNotFoundException){return false;}
        catch(DllNotFoundException){return false;}
    }

    [DllImport("libc",EntryPoint="malloc_trim")]
    private static extern int MallocTrim(nuint pad);
}
