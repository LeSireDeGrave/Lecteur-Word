using System.Diagnostics;
using LecteurWord.DocxCore;
namespace LecteurWord.Flow;
internal sealed class ExportBudget(InspectionOptions options,CancellationToken token)
{
    readonly Stopwatch elapsed=Stopwatch.StartNew();long written;Exception? failure;
    public void RecordFailure(Exception error)=>failure??=error;
    public void ThrowIfFailed(){if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();}
    public void Check(int bytes)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfFailed();
        if(elapsed.ElapsedMilliseconds>options.MaxDurationMilliseconds)throw new OutputLimitException("EXPORT_TIME_LIMIT");
        if(written+bytes>options.MaxOutputBytes)throw new OutputLimitException("EXPORT_SIZE_LIMIT");
        written+=bytes;
    }
    public Stream Create(string path,bool deferFailures=false)=>new QuotaStream(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None),this,deferFailures);
    sealed class QuotaStream(Stream file,ExportBudget budget,bool deferFailures):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>file.CanSeek;public override bool CanWrite=>true;
        public override long Length=>file.Length;public override long Position{get=>file.Position;set=>file.Position=value;}
        public override void Flush(){try{file.Flush();}catch(IOException e)when(deferFailures){budget.RecordFailure(e);}}
        public override void Write(byte[] b,int o,int n)=>Write(b.AsSpan(o,n));
        public override void Write(ReadOnlySpan<byte> b)
        {
            try{budget.Check(b.Length);file.Write(b);}
            catch(Exception e) when(deferFailures && e is IOException or OperationCanceledException){budget.RecordFailure(e);}
        }
        public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin origin)=>file.Seek(o,origin);
        public override void SetLength(long value)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)file.Dispose();base.Dispose(disposing);}
    }
}
