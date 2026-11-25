using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class OpenReliabilityCases
{
    internal static void Run(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        using var trace=new Trace();
        using var process=Process.GetCurrentProcess();
        var options=new InspectionOptions(MaxWorkingSetBytes:process.WorkingSet64+64*1024*1024);
        ReaderOpenMemory.Prepare(options,-1,default);
        LineCases.Check(trace.History.Any(e=>e is (-1,"admission","compacted"))&&trace.Memory(-1)==1,
            "L'admission sous pression doit effectuer une seule collecte");

        string first=Fixture("first"),intermediate=Fixture("intermediate"),last=Fixture("last");
        var window=new ReaderWindow();
        window.Show();
        try
        {
            AboutCases.Complete(window.OpenDocumentAsync(first));
            CheckPublication(first);
            var replacedPage=window.CurrentComposition!.Pages.Single();
            trace.Arm();
            var superseded=window.OpenDocumentAsync(intermediate);
            Task newest;
            try
            {
                LineCases.Check(trace.Entered.Wait(TimeSpan.FromSeconds(10)),"Le premier lecteur n'a pas démarré");
                newest=window.OpenDocumentAsync(last);
                CheckPublication(first);
            }
            finally {trace.Release.Set();}
            AboutCases.Complete(Task.WhenAll(superseded,newest));
            CheckPublication(last);
            try
            {
                _=Composer.PagePng(replacedPage);
                throw new InvalidOperationException("Le compositeur du document remplacé reste utilisable.");
            }
            catch(ObjectDisposedException)
            {
                LineCases.Check(Composer.PagePng(window.CurrentComposition!.Pages.Single()).Length>0,
                    "La libération du compositeur précédent doit préserver le nouveau.");
            }
            var discarded=trace.History.Single(e=>e is (_, "finished","superseded")).Request;
            LineCases.Check(trace.History.Count(e=>e.Phase=="publication")==2&&
                !trace.History.Any(e=>e.Request==discarded&&e.Phase=="publication")&&trace.Memory(discarded)==1,
                "Une ouverture remplacée a publié un état ou a été relancée");

            trace.Arm();
            var cancelled=window.OpenDocumentAsync(intermediate);
            try
            {
                LineCases.Check(trace.Entered.Wait(TimeSpan.FromSeconds(10)),"Le lecteur à annuler n'a pas démarré");
                window.GetVisualDescendants().OfType<Button>()
                    .Single(b=>AutomationProperties.GetName(b)=="Annuler l’opération")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally {trace.Release.Set();}
            AboutCases.Complete(cancelled);
            CheckPublication(last);
            LineCases.Check(trace.History.Count(e=>e.Phase=="publication")==2&&
                trace.History.Count(e=>e is (_,"finished","cancelled"))==1,
                "L'annulation doit conserver le dernier document publié");
        }
        finally {window.Close();}

        string Fixture(string name)=>Fixtures.Write(root,name+".docx",Fixtures.Parts(Fixtures.Paragraph(name)));

        void CheckPublication(string path)=>
            LineCases.Check(window.NativePageStatus=="Complete"&&window.CurrentPage==1&&window.CurrentComposition?.Available==true&&
                window.Inspection?.Manifest.SourceSha256==Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                "L'état natif doit correspondre au dernier DOCX publié");
    }

    sealed class Trace : EventListener
    {
        readonly ConcurrentQueue<long> admissions=new();
        internal readonly ConcurrentQueue<(long Request,string Phase,string Outcome)> History=new();
        internal readonly ManualResetEventSlim Entered=new(false),Release=new(true);
        int armed;

        internal int Memory(long request)=>admissions.Count(id=>id==request);
        internal void Arm()
        {
            Entered.Reset();
            Release.Reset();
            Volatile.Write(ref armed,1);
        }
        protected override void OnEventSourceCreated(EventSource source)
        {
            if(source.Name=="LecteurWord-DocumentOpen")EnableEvents(source,EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if(data is {EventId:2,Payload:[long admission,..]})admissions.Enqueue(admission);
            if(data is not {EventId:1,Payload:[long id,string phase,string outcome,..]})return;
            History.Enqueue((id,phase,outcome));
            if(phase=="reading"&&outcome=="started"&&Interlocked.Exchange(ref armed,0)==1)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(20));
            }
        }
        public override void Dispose()
        {
            base.Dispose();
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
