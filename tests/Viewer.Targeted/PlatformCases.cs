using LecteurWord.Platform;

static partial class PlatformCases
{
    static PlatformPaths Paths(string root) => new(Path.Combine(root, "config"), Path.Combine(root, "state"), Path.Combine(root, "data"));
    static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    internal static void Recovery(string root)
    {
        var paths = Paths(root);
        string doc = Path.Combine(root, "private.docx");
        string second = Path.Combine(root, "second.docx");
        File.WriteAllText(doc, "fixture, unchanged");
        var reading = SessionRecoveryService.DefaultReading() with
        {
            Document = doc, Anchor = "block-42", Page = 4, PageOffset = 54, PagedZoom = 1.2, ContinuousZoom = 1.3,
            View = SessionView.FacingPages, Elements = true, Ruler = true
        };
        using (var service = new SessionRecoveryService(paths))
        {
            Check(!service.Start.Interrupted && service.Start.Reading is null, "Fresh startup");
            try
            {
                using var competing = new SessionRecoveryService(paths);
                throw new InvalidOperationException("Two owners of the journal");
            }
            catch (IOException) { }
            service.Close(reading);
        }
        using (var service = new SessionRecoveryService(paths))
        {
            Check(!service.Start.Interrupted && service.Start.Reading == reading, "Clean restart and preferences");
            service.Begin(new(SessionStage.OpenDocument, second));
        }
        using (var service = new SessionRecoveryService(paths))
        {
            Check(service.Start.Interrupted && service.Start.Reading is { Anchor: null, Page: 0 } target &&
                target.Document == second && target.PagedZoom == 1.2, "Crash while opening a different document");
            Check(service.Start.Report is { ErrorType: null, LocalStack: null, Unclean: true, FrameNames.Length: 0 },
                "Abrupt termination must not fabricate a stack");
            service.Begin(new(SessionStage.RestoreDocument, second), retry: true);
            try { throw new InvalidOperationException("private text " + doc); }
            catch (Exception error) { service.Record(error, fatal: true); }
        }
        using (var service = new SessionRecoveryService(paths))
        {
            Check(!service.Start.ResumeBlocked && service.Start.Report is { ErrorType: "System.InvalidOperationException",
                LocalStack.Length: > 0, FrameNames.Length: > 0 }, "Captured fatal exception");
            Check(!service.Start.Report!.LocalText().Contains(doc), "Report does not embed document path or exception message");
            service.Begin(new(SessionStage.RestoreDocument, second), retry: true);
        }
        using (var service = new SessionRecoveryService(paths))
        {
            Check(service.Start.ResumeBlocked && service.BlockedDocument == second, "Two unclean restore attempts block recovery");
            Check(service.Start.Report?.ErrorType is null, "A different abrupt failure must not reuse a stale stack");
            service.Close(SessionRecoveryService.DefaultReading());
        }
        using (var service = new SessionRecoveryService(paths))
        {
            Check(!service.Start.Interrupted && service.BlockedDocument == second, "Block survives a clean safe startup");
            service.Begin(new(SessionStage.OpenDocument, second));
            service.Checkpoint(reading with { Document = second }, completedOperation: true, successfulDocument: second);
            Check(service.BlockedDocument is null, "Successful manual inspection clears quarantine");
            service.Close(reading);
        }
        string saved = Path.Combine(root, "report.txt");
        PlatformFiles.SaveReport(saved, "first");
        try
        {
            PlatformFiles.SaveReport(saved, "overwrite");
            throw new InvalidOperationException("Report overwrite was allowed");
        }
        catch (IOException) { }
        Check(File.ReadAllText(saved) == "first" && File.ReadAllText(doc) == "fixture, unchanged", "Preserve existing files");
    }

    internal static void Support()
    {
        var incident = new Incident(Guid.NewGuid(),DateTimeOffset.UtcNow,SessionStage.RunExtension,false,
            "System.InvalidOperationException",["Run"],"/private/system/stack",new("demo.reader","1.0.0"),"0.1.0","build-id","Linux");
        string draft = GitHubSupportService.Draft(SupportKind.Extension,"Open menus","Optional",incident,false);
        Check(draft.Contains("Open menus") && draft.Contains("Optional") && draft.Contains("demo.reader 1.0.0") &&
            !draft.Contains("/private"), "User text included, local stack excluded");
        Check(new GitHubSupportService(CommunityConfiguration.Empty).IssueUrl(SupportKind.Bug) is null, "No default private issue destination");
        var support = new GitHubSupportService(new("lecteur/support", "lecteur/releases", null));
        Check(support.IssueUrl(SupportKind.Extension)?.AbsoluteUri == "https://github.com/lecteur/support/issues/new?template=extension.yml" &&
            support.VersionsUrl?.AbsolutePath == "/lecteur/releases/releases" &&
            support.ContributeUrl?.Host == "github.com", "Only template name is sent in browser URL");
        Check(new GitHubSupportService(new("evil.example/?x=site",null,null)).IssueUrl(SupportKind.Feature) is null,
            "Reject configuration outside GitHub");
    }
}
