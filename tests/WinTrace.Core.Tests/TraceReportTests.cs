using System;
using System.IO;
using System.Linq;
using WinTrace;
using Xunit;

public class TraceReportTests
{
    sealed class Source : ITraceSource
    {
        public byte[]? CaptureScreenshot() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 };
        public string? CaptureSnapshot() =>
            "<Window Name=\"Sample App\"><Button Name=\"Submit\" AutomationId=\"btnSubmit\"/></Window>";
    }

    static string TempDir() => Path.Combine(Path.GetTempPath(), "wintrace-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Failed_session_writes_markdown_and_failure_screenshot()
    {
        var s = new TraceSession("SubmitCustomer", new Source(), TempDir());
        using (s.Recorder.Group("Submit"))
            try { s.Recorder.Step("click", "By.AccessibilityId: btnSubmitt", () => throw new InvalidOperationException("not found")); } catch { }
        var files = s.Finish(true, null, "Assert failed");

        var md = files.Single(f => f.EndsWith(".md"));
        var png = files.Single(f => f.EndsWith(".png"));
        Assert.True(File.Exists(png));
        var text = File.ReadAllText(md);
        Assert.Contains("# SubmitCustomer — FAILED", text);
        Assert.Contains("Failure message: `Assert failed`", text);
        Assert.Contains("Step #1 **click** `By.AccessibilityId: btnSubmitt` (group: Submit)", text);
        Assert.Contains("`MobileBy.AccessibilityId(\"btnSubmit\")`", text);
        Assert.Contains("Screenshot at the failure: `" + Path.GetFileName(png) + "`", text);
        Assert.Contains("UI tree at the failure", text);
        Assert.Contains("wintrace inspect", text);
    }

    [Fact]
    public void Failure_outside_steps_is_explained()
    {
        var rec = new TraceRecorder();
        rec.Step("click", "By.Name: Save", () => { });
        rec.SetOutcome(true, "Expected 2 but was 1");
        var md = TraceReport.Markdown(rec.GetMetadata(), rec.Events, _ => null);
        Assert.Contains("outside the recorded steps", md);
        Assert.Contains("Expected 2 but was 1", md);
    }

    [Fact]
    public void Markdown_can_be_turned_off()
    {
        var s = new TraceSession("t", new Source(), TempDir()) { WriteMarkdown = false, WriteHtml = false };
        s.Recorder.Step("x", null, () => { });
        Assert.Single(s.Finish(true));
    }
}

public class TraceReportHintTests
{
    sealed class Source : ITraceSource
    {
        public byte[]? CaptureScreenshot() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 2 };
        public string? CaptureSnapshot() => "<Window Name=\"Sample App\"><Button Name=\"Save\" AutomationId=\"btnSave\"/></Window>";
    }

    [Fact]
    public void No_similar_element_points_to_the_failure_screenshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wintrace-hint", Guid.NewGuid().ToString("N"));
        var s = new TraceSession("MenuDidNotOpen", new Source(), dir) { WriteHtml = false };
        try { s.Recorder.Step("click", "By.Name: Customer settings", () => throw new InvalidOperationException("not found")); } catch { }
        var files = s.Finish(true);
        var md = File.ReadAllText(files.Single(f => f.EndsWith(".md")));
        var png = Path.GetFileName(files.Single(f => f.EndsWith(".png")));
        Assert.Contains("No UI element resembles `Customer settings`", md);
        Assert.Contains("did not open", md);
        Assert.Contains($"The screenshot at the failure (`{png}`) shows what was on screen.", md);
    }
}
