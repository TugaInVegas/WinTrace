using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinTrace;
using Xunit;

public class RunReportTests
{
    static string Dir(string root, string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }

    static void FailedTrace(string dir, string test, string target, string error)
    {
        var s = new TraceSession(test, null, dir) { WriteHtml = false };
        try { s.Recorder.Step("click", target, () => throw new InvalidOperationException(error)); } catch { }
        s.Finish(true);
    }

    static void FailedOutsideSteps(string dir, string test, string message)
    {
        var s = new TraceSession(test, null, dir) { WriteHtml = false };
        s.Recorder.Step("click", "By.Name: Save", () => { });
        s.Finish(true, null, message);
    }

    static void Trx(string path, params (string test, string outcome)[] results)
    {
        var rows = string.Concat(results.Select(r => $"<UnitTestResult testName=\"{r.test}\" outcome=\"{r.outcome}\" />"));
        File.WriteAllText(path, $"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>{rows}</Results></TestRun>");
    }

    [Fact]
    public void Groups_by_cause_and_classifies_against_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "wintrace-report", Guid.NewGuid().ToString("N"));
        const string notFound = "An element could not be located (timeout 10 s)";

        var run1 = Dir(root, "run1");
        FailedTrace(run1, "A", "By.AccessibilityId: btnSubmitt", notFound);
        FailedTrace(run1, "C", "By.Name: Close", "boom");
        Trx(Path.Combine(run1, "r.trx"), ("A", "Failed"), ("B", "Passed"), ("C", "Failed"));

        var run2 = Dir(root, "run2");
        FailedTrace(run2, "A", "By.AccessibilityId: btnSubmitt", "An element could not be located (timeout 30 s)");
        FailedTrace(run2, "B", "By.Name: Other", "different");
        FailedTrace(run2, "C", "By.Name: Close", "boom");

        var current = Dir(root, "current");
        FailedTrace(current, "A", "By.AccessibilityId: btnSubmitt", notFound);
        FailedTrace(current, "B", "By.Name: Other", "different");
        FailedTrace(current, "D", "By.AccessibilityId: btnSubmitt", notFound);
        FailedOutsideSteps(current, "E", "Assert.AreEqual failed. Expected:<2>. Actual:<1>.");
        var trxPath = Path.Combine(current, "r.trx");
        Trx(trxPath, ("A", "Failed"), ("B", "Failed"), ("C", "Passed"), ("D", "Failed"), ("E", "Failed"), ("F", "Failed"), ("G", "Passed"));

        var history = new List<HistoricRun>
        {
            RunReport.ReadHistoricRun("run2", Directory.GetFiles(run2, "*.zip"), null),               // newest first
            RunReport.ReadHistoricRun("run1", Directory.GetFiles(run1, "*.zip"), TrxRun.Read(run1)),
        };
        var report = RunReport.Build(current, Directory.GetFiles(current, "*.zip"), history, TrxRun.Read(trxPath));

        var byTitle = report.Entries.ToDictionary(e => e.Title);
        Assert.Equal("recurring", byTitle["A"].History);
        Assert.Contains("same cause", byTitle["A"].HistoryDetail);          // numbers normalized: 10 s vs 30 s
        Assert.Equal("intermittent", byTitle["B"].History);                  // failed in run2, passed in run1
        Assert.Equal("new", byTitle["D"].History);
        Assert.Equal("new", byTitle["E"].History);
        Assert.True(byTitle["E"].OutsideSteps);

        var biggest = report.Clusters[0];
        Assert.Equal(new[] { "A", "D" }, biggest.Tests.Select(t => t.Title).ToArray());
        Assert.Equal(3, report.Clusters.Count);
        Assert.Equal(new[] { "F" }, report.FailedWithoutTrace);
        Assert.Equal(new[] { "C" }, report.Fixed);

        var md = report.ToMarkdown(current);
        Assert.Contains("## Failures by cause", md);
        Assert.Contains("(2 tests)", md);
        Assert.Contains("intermittent", md);
        Assert.Contains("## Failed without a trace", md);
        Assert.Contains("## Passing again", md);
        Assert.Contains("](A_", md);                                           // relative link to A's .md summary
        Assert.Contains("TRX: 7 tests, 2 passed, **5 failed**", md);
    }

    [Fact]
    public void Normalize_hides_numbers_and_guids()
    {
        Assert.Equal(RunReport.Normalize("Timeout 10 s at row 3"), RunReport.Normalize("Timeout 30 s at row 12"));
        Assert.Equal("id #", RunReport.Normalize("id 0f8fad5b-d9cb-469f-a165-70867728950e"));
    }

    [Fact]
    public void Without_history_or_trx_the_report_says_what_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wintrace-report", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        FailedTrace(dir, "A", "By.Name: X", "boom");
        var md = RunReport.Build(dir, Directory.GetFiles(dir, "*.zip")).ToMarkdown(dir);
        Assert.Contains("no earlier runs given", md);
        Assert.Contains("Pass `--trx`", md);
    }
}

public class RunComparisonTests
{
    sealed class FakeSource : ITraceSource
    {
        public byte[]? CaptureScreenshot() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 7 };
        public string? CaptureSnapshot() => "<Window Name=\"Sample App\"/>";
    }

    static TraceEvent Ev(int seq, string name, string target, double ms, string? error = null) => new TraceEvent
    {
        Seq = seq, Kind = error != null ? "error" : "action", Name = name, Target = target, DurationMs = ms, Error = error,
        Start = DateTimeOffset.UtcNow.AddSeconds(seq)
    };

    static string Write(string dir, string test, string outcome, params TraceEvent[] events)
    {
        Directory.CreateDirectory(dir);
        var meta = new TraceMetadata { Title = test, Started = DateTimeOffset.UtcNow, Outcome = outcome };
        return TraceArchive.Write(Path.Combine(dir, $"{test}_20261102_100000_{outcome}.zip"), meta, events, Array.Empty<KeyValuePair<string, byte[]>>());
    }

    [Fact]
    public void Failed_test_is_compared_with_its_last_passing_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "wintrace-compare", Guid.NewGuid().ToString("N"));
        var before = Path.Combine(root, "run1");
        Write(before, "Orders", "passed",
            Ev(1, "click", "By.Name: Orders", 300), Ev(2, "click", "By.Name: New order", 1700), Ev(3, "click", "By.Name: Customer dropdown", 200),
            Ev(4, "click", "By.Name: First customer", 150));
        Write(before, "Invoices", "passed", Ev(1, "click", "By.Name: Invoices", 400), Ev(2, "wait", "Invoice dialog", 1700));

        var now = Path.Combine(root, "current");
        var failed = Write(now, "Orders", "failed",
            Ev(1, "click", "By.Name: Orders", 320), Ev(2, "click", "By.Name: New order", 28000), Ev(3, "click", "By.Name: Customer dropdown", 210),
            Ev(4, "find", "By.Name: First customer", 10000, "not found"));
        var passed = Write(now, "Invoices", "passed", Ev(1, "click", "By.Name: Invoices", 380), Ev(2, "wait", "Invoice dialog", 28000));

        var history = new List<HistoricRun> { RunReport.ReadHistoricRun("run1", Directory.GetFiles(before, "*.zip"), null) };
        var report = RunReport.Build(now, new[] { failed, passed }, history);

        var orders = report.Entries.Single(e => e.Title == "Orders");
        Assert.Equal("run1", orders.ComparedWith);
        Assert.Equal("new", orders.History);
        Assert.Contains("#4 find By.Name: First customer here, where the last passing run did #4 click By.Name: First customer", orders.Divergence);
        Assert.Equal(2, Assert.Single(orders.SlowerSteps).Seq);

        var invoices = report.Entries.Single(e => e.Title == "Invoices");
        Assert.Null(invoices.Divergence);
        Assert.Equal(28000, Assert.Single(invoices.SlowerSteps).DurationMs);

        var md = report.ToMarkdown();
        Assert.Contains("Compared with the last passing run:", md);
        Assert.Contains("## Slower than the last passing run", md);
        Assert.Contains("| Invoices | #2 wait Invoice dialog | 28.0 s | 1.7 s | run1 |", md);
    }

    [Fact]
    public void Same_steps_until_the_failure_reports_the_step_that_passed_before()
    {
        var root = Path.Combine(Path.GetTempPath(), "wintrace-compare", Guid.NewGuid().ToString("N"));
        var before = Path.Combine(root, "run1");
        Write(before, "T", "passed", Ev(1, "click", "By.Name: A", 100), Ev(2, "click", "By.Name: B", 900));
        var now = Path.Combine(root, "current");
        var failed = Write(now, "T", "failed", Ev(1, "click", "By.Name: A", 100), Ev(2, "click", "By.Name: B", 10000, "timeout"));
        var report = RunReport.Build(now, new[] { failed }, new[] { RunReport.ReadHistoricRun("run1", Directory.GetFiles(before, "*.zip"), null) });
        Assert.Contains("failed after 10.0 s; the same step passed in 0.9 s", report.Entries[0].Divergence);
    }

    [Fact]
    public void Passed_summary_has_steps_but_no_screenshots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wintrace-compare", Guid.NewGuid().ToString("N"));
        var s = new TraceSession("Light", new FakeSource(), dir) { SavePassedSummary = true };
        s.Recorder.Step("click", "By.Name: Save", () => { });
        var files = s.Finish(false);
        var zip = Assert.Single(files);
        Assert.EndsWith("_passed.zip", zip);
        var t = TraceReader.Read(zip, null);
        Assert.Equal("passed", t.Meta.Outcome);
        Assert.Single(t.Events);
        Assert.Null(t.Events[0].ScreenshotAfter);
        Assert.Empty(t.Resources);
    }
}
