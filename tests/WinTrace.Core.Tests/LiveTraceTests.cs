using System;
using System.IO;
using System.Linq;
using WinTrace;
using Xunit;

public class LiveTraceTests
{
    sealed class Source : ITraceSource
    {
        public byte[]? CaptureScreenshot() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 7 };
        public string? CaptureSnapshot() => "<Window Name=\"Sample App\"/>";
    }

    static string TempDir() => Path.Combine(Path.GetTempPath(), "wintrace-live", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Live_copy_is_written_while_the_test_runs_and_removed_by_Finish()
    {
        var dir = TempDir();
        var s = new TraceSession("LoginTest", new Source(), dir);
        s.Recorder.SetContext("customer", "Ada Lovelace");
        s.Recorder.Step("click", "By.Name: Save", () => { });
        var state = TraceRecovery.Read(s.LiveDirectory);
        Assert.Single(state.Events);
        Assert.Equal("Ada Lovelace", state.Meta.Context["customer"]);
        Assert.True(Directory.GetFiles(Path.Combine(s.LiveDirectory, "resources")).Length > 0);

        s.Finish(false);
        Assert.False(Directory.Exists(s.LiveDirectory));
        Assert.False(Directory.Exists(Path.Combine(dir, ".live")));
    }

    [Fact]
    public void Step_in_progress_is_visible_and_recovered_as_unfinished()
    {
        var dir = TempDir();
        var s = new TraceSession("LongWorkflow", new Source(), dir);
        s.Recorder.Step("open", "menu/Open", () => { });
        string? inProgress = null;
        try
        {
            s.Recorder.Step("wait for dialog", "dlgSettings", () =>
            {
                inProgress = TraceRecovery.Read(s.LiveDirectory).Current?.Name;
                // The process "dies" here: no Finish is ever called.
                throw new OperationCanceledException("killed");
            });
        }
        catch (OperationCanceledException) { }
        Assert.Equal("wait for dialog", inProgress);

        // Simulate a crash before the second step was written: recover only what is on disk.
        var files = TraceRecovery.Recover(s.LiveDirectory, dir);
        var zip = files.Single(f => f.EndsWith("_aborted.zip"));
        var t = TraceReader.Read(zip, null);
        Assert.Equal("aborted", t.Meta.Outcome);
        Assert.True(TraceAnalysis.IsFailed(t.Meta, t.Events));
        Assert.Contains(files, f => f.EndsWith(".html"));
        Assert.Contains("ABORTED", File.ReadAllText(files.Single(f => f.EndsWith(".md"))));
        Assert.False(Directory.Exists(s.LiveDirectory));
    }

    [Fact]
    public void Unfinished_step_without_an_end_is_added_as_failed()
    {
        var dir = TempDir();
        var s = new TraceSession("Killed", new Source(), dir);
        s.Recorder.Step("open", "menu/Open", () => { });
        // Write the "current step" marker as the recorder does at step start, then stop as if the process died.
        File.WriteAllText(Path.Combine(s.LiveDirectory, "current.json"),
            "{\"seq\":2,\"name\":\"wait for dialog\",\"target\":\"dlgSettings\",\"start\":\"" + DateTimeOffset.UtcNow.AddSeconds(-5).ToString("o") + "\"}");
        var written = TraceSession.RecoverAbandoned(dir);
        var t = TraceReader.Read(written.Single(f => f.EndsWith(".zip")), null);
        var last = t.Events.Last();
        Assert.Equal("wait for dialog", last.Name);
        Assert.Equal("error", last.Kind);
        Assert.StartsWith("Unfinished", last.Error);
    }

    [Fact]
    public void Screen_unchanged_is_reported_when_the_screenshot_does_not_change()
    {
        var rec = new TraceRecorder(new Source());
        rec.Step("click", "Open menu", () => { });
        Assert.Equal("unchanged", rec.Events[0].Diagnostics!["screen"]);
    }

    sealed class DiagSource : ITraceSource, IStepDiagnostics
    {
        public byte[]? CaptureScreenshot() => null;
        public string? CaptureSnapshot() => null;
        public object? BeginStep() => 42;
        public System.Collections.Generic.IDictionary<string, string>? EndStep(object? state) =>
            new System.Collections.Generic.Dictionary<string, string> { ["appResponding"] = (int)state! == 42 ? "no" : "?" };
    }

    [Fact]
    public void Step_diagnostics_from_the_source_are_recorded()
    {
        var rec = new TraceRecorder(new DiagSource());
        rec.Step("click", "Save", () => { });
        rec.Step("probe", "x", Capture.None, () => { });
        Assert.Equal("no", rec.Events[0].Diagnostics!["appResponding"]);
        Assert.Null(rec.Events[1].Diagnostics);
    }
}

public class NoiseTests
{
    [Fact]
    public void Missed_probe_is_noise_and_not_the_failure_step()
    {
        var rec = new TraceRecorder(null, new TraceOptions { CaptureCallSite = false, CaptureEnvironment = false });
        try { rec.Step("find", "By.Name: Optional banner", Capture.None, () => throw new InvalidOperationException("no such element")); } catch (InvalidOperationException) { }
        rec.Step("click", "By.Name: Save", () => { });
        try { rec.Step("find", "By.Name: Done", () => throw new InvalidOperationException("no such element")); } catch (InvalidOperationException) { }
        rec.SetOutcome(true);
        var meta = rec.GetMetadata();
        var events = rec.Events.ToList();
        Assert.True(events[0].Minor);
        Assert.True(TraceAnalysis.IsRecoveredMiss(events[0], meta, events));
        Assert.False(TraceAnalysis.IsNoise(events[2], meta, events));
        Assert.Equal(3, TraceAnalysis.FailureStep(meta, events, out _)!.Seq);
        var md = TraceReport.Markdown(meta, events, _ => null);
        Assert.Contains("missed (recovered)", md);
    }

    [Fact]
    public void Probe_that_ended_the_test_is_the_failure()
    {
        var rec = new TraceRecorder(null, new TraceOptions { CaptureCallSite = false, CaptureEnvironment = false });
        rec.Step("click", "By.Name: Save", () => { });
        try { rec.Step("find", "By.Name: Done", Capture.None, () => throw new InvalidOperationException("no such element")); } catch (InvalidOperationException) { }
        rec.SetOutcome(true);
        var meta = rec.GetMetadata();
        var events = rec.Events.ToList();
        Assert.False(TraceAnalysis.IsNoise(events[1], meta, events));
        Assert.Equal(2, TraceAnalysis.FailureStep(meta, events, out var outside)!.Seq);
        Assert.False(outside);
    }
}
