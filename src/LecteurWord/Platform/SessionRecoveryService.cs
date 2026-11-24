using System.Diagnostics;
using System.Text.Json;

namespace LecteurWord.Platform;

internal enum SessionStage { Idle, Startup, OpenDocument, RestoreDocument, ApplyExtension, RunExtension, Interface, Background, ExportDocument }
internal enum SessionView { Unavailable, SinglePage, FacingPages, Continuous }

internal sealed record ReadingSnapshot(
    string? Document, string? Anchor, int Page, double PageOffset, double PagedZoom, double ContinuousZoom,
    SessionView View, bool Navigation, bool Elements, string Panel, double NavigationWidth, double ElementsWidth,
    bool PreferElements, bool English, bool Ruler, bool Marks, bool FitWidth, int ElementCategory, string? DocumentHash = null);

internal sealed record ExtensionIdentity(string Id, string Version);
internal sealed record SessionOperation(SessionStage Stage, string? Document = null, ExtensionIdentity? Extension = null);
internal sealed record Incident(Guid Id, DateTimeOffset Utc, SessionStage Stage, bool Unclean, string? ErrorType,
    string[] FrameNames, string? LocalStack, ExtensionIdentity? Extension, string Version, string Build, string System)
{
    internal string TechnicalText() => string.Join(Environment.NewLine,
        $"Lecteur Word {Version}", $"Incident : {Id:N} · {Utc:O}", $"Build : {Build}", $"Système : {System}", $"Étape : {Stage}",
        $"Arrêt non propre : {(Unclean ? "oui" : "non")}", $"Extension : {(Extension is null ? "—" : $"{Extension.Id} {Extension.Version}")}",
        $"Erreur : {ErrorType ?? "non observée"}",
        FrameNames.Length == 0 ? "Pile : indisponible" : "Pile : " + Environment.NewLine + string.Join(Environment.NewLine, FrameNames));
    internal string LocalText() => TechnicalText() +
        (LocalStack is null ? "" : Environment.NewLine + Environment.NewLine + LocalStack);
}

internal sealed record SessionJournal(int Schema, bool Clean, ReadingSnapshot? Reading,
    SessionOperation Operation, string? RetryDocument, int RetryCount, Guid? LastReport, Guid? FatalReport = null);
internal sealed record SessionStart(bool Interrupted, ReadingSnapshot? Reading, bool ResumeBlocked,
    ExtensionIdentity? InterruptedExtension, Incident? Report);

internal sealed class SessionRecoveryService : IDisposable
{
    readonly object gate = new();
    readonly string journalPath;
    readonly string reportsPath;
    readonly FileStream ownership;
    SessionJournal state;
    bool disposed;
    internal SessionStart Start { get; }
    internal Incident? LatestReport { get; private set; }
    internal string? BlockedDocument => state.RetryCount >= 2 ? state.RetryDocument : null;
    internal string? StorageWarning { get; }

    internal SessionRecoveryService(PlatformPaths paths)
    {
        PlatformFiles.MakePrivateDirectory(paths.State);
        journalPath = Path.Combine(paths.State, "session.json");
        reportsPath = Path.Combine(paths.State, "incidents");
        ownership = new FileStream(Path.Combine(paths.State, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            SessionJournal? previous = null;
            try
            {
                previous = PlatformFiles.ReadOptional<SessionJournal>(journalPath);
                if (previous is not null && (previous.Schema != 1 || previous.Operation is null ||
                    previous.RetryCount is < 0 or > 2 || !Enum.IsDefined(previous.Operation.Stage)))
                    throw new InvalidDataException("Schéma de session invalide.");
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                StorageWarning = "Journal de session illisible. Il a été conservé pour examen.";
                File.Move(journalPath, journalPath + "." + Guid.NewGuid().ToString("N") + ".invalid");
                previous = null;
            }

            state = previous ?? new SessionJournal(1, true, null, new(SessionStage.Idle), null, 0, null);
            bool interrupted = !state.Clean;
            var operation = state.Operation;
            ReadingSnapshot? target = state.Reading;
            if (interrupted && operation.Document is {} path && target?.Document != path)
                target = (target ?? DefaultReading()) with { Document = path, DocumentHash = null, Anchor = null, Page = 0,
                    PageOffset = 0, View = SessionView.Unavailable, FitWidth = false };
            if (state.LastReport is {} last)
            {
                try { LatestReport = PlatformFiles.ReadOptional<Incident>(ReportPath(last)); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    StorageWarning = "Le dernier rapport d’incident est illisible. Le fichier est conservé.";
                }
            }
            if (interrupted)
            {
                if (LatestReport is null || LatestReport.Id != state.FatalReport)
                    RecordCore(null, operation.Stage, operation.Extension, unclean: true);
            }
            bool blocked = target?.Document is {} doc && SameDocument(doc, state.RetryDocument) && state.RetryCount >= 2;
            Start = new SessionStart(interrupted, target, blocked, interrupted ? operation.Extension : null, interrupted ? LatestReport : null);
            Persist(state with { Clean = false, Reading = target, Operation = new(SessionStage.Startup, Extension: Start.InterruptedExtension), FatalReport = null });
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    internal static ReadingSnapshot DefaultReading() =>
        new(null, null, 0, 0, 1, 1, SessionView.Unavailable, true, false, "pages", 220, 294, false, false, false, false, false, 0);

    internal void Begin(SessionOperation operation, bool retry = false)
    {
        lock (gate)
        {
            if (disposed) return;
            var next = state with { Operation = operation };
            if (operation.Document is {} path && retry)
                next = next with { RetryDocument = path, RetryCount = SameDocument(path, state.RetryDocument) ? Math.Min(2, state.RetryCount + 1) : 1 };
            Persist(next);
        }
    }

    internal void Checkpoint(ReadingSnapshot snapshot, bool completedOperation = false, string? successfulDocument = null, SessionOperation? nextOperation = null)
    {
        lock (gate)
        {
            if (disposed) return;
            var next = state with { Reading = snapshot };
            if (completedOperation) next = next with { Operation = nextOperation ?? new(SessionStage.Idle) };
            if (successfulDocument is not null && SameDocument(successfulDocument, next.RetryDocument))
                next = next with { RetryCount = 0, RetryDocument = null };
            if (next != state) Persist(next);
        }
    }

    internal void Record(Exception error, SessionStage? stage = null, ExtensionIdentity? extension = null, bool fatal = false)
    {
        lock (gate)
        {
            if (disposed) return;
            var currentStage = stage ?? state.Operation.Stage;
            RecordCore(error, currentStage == SessionStage.Idle ? SessionStage.Interface : currentStage,
                extension ?? (stage is null ? state.Operation.Extension : null), fatal);
        }
    }

    void RecordCore(Exception? error, SessionStage stage, ExtensionIdentity? extension, bool unclean)
    {
        var build = BuildReference.Current;
        string[] names = error is null ? [] : new StackTrace(error).GetFrames().Take(16)
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.FullName is not null)
            .Select(method => method!.DeclaringType!.FullName + "." + method.Name).ToArray();
        string? stack = error is null ? null : new StackTrace(error, fNeedFileInfo: false).ToString();
        var report = new Incident(Guid.NewGuid(), DateTimeOffset.UtcNow, stage, unclean, error?.GetType().FullName,
            names, stack is { Length: > 16000 } ? stack[..16000] : stack, extension, build.Version ?? "indisponible",
            (build.CompilationUtc?.ToString("O") ?? "indisponible") + (build.Commit is {} commit ? " / " + commit : ""), build.RuntimePlatform);
        PlatformFiles.Write(ReportPath(report.Id), report);
        Persist(state with { LastReport = report.Id, FatalReport = unclean ? report.Id : state.FatalReport });
        LatestReport = report;
    }

    internal void Close(ReadingSnapshot snapshot)
    {
        lock (gate)
        {
            if (disposed) return;
            Persist(state with { Clean = true, Reading = snapshot, Operation = new(SessionStage.Idle) });
        }
    }

    static bool SameDocument(string path, string? other) => StringComparer.Ordinal.Equals(path, other);
    string ReportPath(Guid id) => Path.Combine(reportsPath, id.ToString("N") + ".json");
    void Persist(SessionJournal next)
    {
        PlatformFiles.Write(journalPath, next);
        state = next;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            ownership.Dispose();
        }
    }
}
