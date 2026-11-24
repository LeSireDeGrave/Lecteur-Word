using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

internal sealed record RasterResource(byte[] Bytes,int Width,int Height,int Orientation,string Notice,
    IReadOnlyList<MediaPhase> Phases);
internal readonly record struct RasterAdmission(RasterResource? Raster,string? Failure);

/// <summary>Bounded asynchronous admission of required raster resources from one inspected snapshot.</summary>
internal sealed class DocumentRasterCache
{
    internal const string HashMismatch="LAYOUT_IMAGE_HASH_MISMATCH", DecodeRefused="LAYOUT_IMAGE_DECODE_REFUSED",
        QueueLimit="LAYOUT_IMAGE_QUEUE_LIMIT";
    static readonly ConditionalWeakTable<InspectionResult,DocumentRasterCache> snapshots=new();
    static readonly SemaphoreSlim workers=new(Math.Clamp(Environment.ProcessorCount,1,2));
    static int pending;
    readonly object gate=new();
    readonly Dictionary<(string Hash,string Format),RasterResource> ready=[];
    readonly Dictionary<(string Hash,string Format),DecodeJob> active=[];
    long pixels,bytes;

    sealed class DecodeJob
    {
        internal readonly CancellationTokenSource Lifetime=new();
        internal Task<RasterAdmission> Work=null!;
        internal int Waiters;
    }

    internal static DocumentRasterCache For(InspectionResult input)=>snapshots.GetValue(input,_=>new());

    internal async Task<RasterAdmission> ReadAsync(MediaResource media,byte[] input,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(media.ContentType is not ("image/png" or "image/jpeg")||input.Length>8*1024*1024)
            return new(null,DecodeRefused);
        if(!HasHash(input,media.Sha256))return new(null,HashMismatch);
        var key=(media.Sha256,media.ContentType);
        DecodeJob job;
        lock(gate)
        {
            if(ready.TryGetValue(key,out var found))return new(found,null);
            if(!active.TryGetValue(key,out job!))
            {
                if(Interlocked.Increment(ref pending)>16)
                {
                    Interlocked.Decrement(ref pending);
                    return new(null,QueueLimit);
                }
                var scheduled=new DecodeJob();
                active.Add(key,scheduled);
                scheduled.Work=Task.Run(async ()=>
                {
                    try
                    {
                        if(!await workers.WaitAsync(TimeSpan.FromSeconds(12),scheduled.Lifetime.Token).ConfigureAwait(false))
                            return new RasterAdmission(null,QueueLimit);
                        try
                        {
                            var buffer=(byte[])input.Clone();
                            if(!HasHash(buffer,media.Sha256))return new RasterAdmission(null,HashMismatch);
                            var result=Decode(media,buffer,scheduled.Lifetime.Token);
                            lock(gate)
                            {
                                if(active.GetValueOrDefault(key)==scheduled&&result.Raster is {} decoded&&ready.Count<64&&
                                    pixels+(long)decoded.Width*decoded.Height<=24_000_000&&bytes+decoded.Bytes.Length<=32*1024*1024)
                                {
                                    ready.Add(key,decoded);
                                    pixels+=(long)decoded.Width*decoded.Height;
                                    bytes+=decoded.Bytes.Length;
                                }
                            }
                            return result;
                        }
                        finally{workers.Release();}
                    }
                    finally
                    {
                        lock(gate)
                        {
                            if(active.GetValueOrDefault(key)==scheduled)active.Remove(key);
                            scheduled.Lifetime.Dispose();
                        }
                        Interlocked.Decrement(ref pending);
                    }
                });
                job=scheduled;
            }
            job.Waiters++;
        }
        try{return await job.Work.WaitAsync(TimeSpan.FromSeconds(24),cancellation).ConfigureAwait(false);}
        catch(TimeoutException){return new(null,MediaDecodeBudget.WallLimit);}
        finally
        {
            lock(gate)
            {
                if(--job.Waiters==0&&active.GetValueOrDefault(key)==job)
                {
                    active.Remove(key);
                    job.Lifetime.Cancel();
                }
            }
        }
    }

    internal static RasterAdmission Decode(MediaResource media,byte[] input,CancellationToken cancellation)
    {
        var phases=new List<MediaPhase>();
        var budget=new MediaDecodeBudget(cancellation,phases);
        int width=media.Width??0,height=media.Height??0,orientation=1;
        bool decoded=media.ContentType=="image/jpeg"?
            SafeJpeg.Decode(input,out var renderable,out width,out height,out orientation,out var reason,budget):
            SafePng.Decode(input,out renderable,out reason,budget);
        if(!decoded)return new(null,MediaDecodeBudget.IsLimit(reason)||reason is SafePng.ColorUnqualified or SafeJpeg.ColorUnqualified?
            reason:DecodeRefused);
        if(width<=0||height<=0)return new(null,DecodeRefused);
        if(media.ContentType=="image/png")
        {
            using var bitmap=SKBitmap.Decode(renderable);
            if(bitmap is null||bitmap.Width!=width||bitmap.Height!=height)return new(null,DecodeRefused);
            try{budget.FinishPhase("PNG validation du décodage natif");}
            catch(InvalidDataException error) when(MediaDecodeBudget.IsLimit(error.Message)){return new(null,error.Message);}
        }
        return new(new(renderable,width,height,orientation,reason,phases.AsReadOnly()),null);
    }

    static bool HasHash(byte[] input,string expected)=>
        Convert.ToHexStringLower(SHA256.HashData(input)).Equals(expected,StringComparison.Ordinal);
}
