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
