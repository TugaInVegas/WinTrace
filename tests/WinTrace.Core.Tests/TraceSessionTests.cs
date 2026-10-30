using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinTrace;
using Xunit;

public class TraceSessionTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "wintrace-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Passed_test_writes_nothing_by_default()
    {
        var dir = TempDir();
        var s = new TraceSession("Login_ShowsDashboard", null, dir);
        s.Recorder.Step("click", "ok", () => { });
        Assert.Empty(s.Finish(testFailed: false));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Failed_test_writes_zip_and_html_and_attaches_them()
    {
        var dir = TempDir();
        var attached = new List<string>();
        var s = new TraceSession("Login_ShowsDashboard", null, dir);
        s.Recorder.Step("click", "ok", () => { });
        var files = s.Finish(testFailed: true, attached.Add);
        Assert.Equal(3, files.Count);   // zip, html, md (no screenshots without a source)
        Assert.Equal(files, attached);
        Assert.All(files, f => Assert.True(File.Exists(f)));
        Assert.Contains("Login_ShowsDashboard_", Path.GetFileName(files[0]));
        Assert.EndsWith("_failed.zip", files[0]);
    }

    [Fact]
    public void Recovered_step_error_is_not_saved_unless_asked()
    {
        var s = new TraceSession("t", null, TempDir()) { WriteHtml = false };
        try { s.Recorder.Step("x", null, () => throw new Exception("recovered by the test")); } catch { }
        Assert.Empty(s.Finish(testFailed: false));

        var s2 = new TraceSession("t", null, TempDir()) { WriteHtml = false, SaveRecoveredErrors = true };
        try { s2.Recorder.Step("x", null, () => throw new Exception("recovered by the test")); } catch { }
        Assert.Equal(2, s2.Finish(testFailed: false).Count);   // zip + md
    }

    [Fact]
    public void Outcome_and_failure_message_are_stored()
    {
        var dir = TempDir();
        var s = new TraceSession("t", null, dir) { WriteHtml = false };
        s.Recorder.Step("ok", null, () => { });
        var zip = s.Finish(true, null, "Assert.AreEqual failed")[0];
        var t = TraceReader.Read(zip, Path.Combine(dir, "x"));
        Assert.Equal("failed", t.Meta.Outcome);
        Assert.Equal("Assert.AreEqual failed", t.Meta.FailureMessage);
    }

    [Fact]
    public void Current_is_set_for_the_running_test_and_cleared_by_Finish()
    {
        var s = new TraceSession("t", null, TempDir());
        Assert.Same(s.Recorder, TraceRecorder.Current);
        s.Finish(false);
        Assert.Null(TraceRecorder.Current);
    }

    [Fact]
    public void Video_is_embedded_in_html_by_default_and_side_file_is_returned_otherwise()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "rec.mp4");
        File.WriteAllBytes(video, new byte[] { 0, 0, 0, 24, 102, 116, 121, 112 });

        var s = new TraceSession("embedded", null, dir);
        s.Recorder.AttachVideo(video, DateTimeOffset.UtcNow);
        var files = s.Finish(true);
        Assert.Equal(3, files.Count);
        var html = File.ReadAllText(files[1]);
        Assert.Contains("\"embedded\":\"video/rec.mp4\"", html);
        Assert.Contains("\"video/rec.mp4\":\"AAAAGGZ0eXA=\"", html);

        var s2 = new TraceSession("side", null, dir) { EmbedVideoInHtml = false };
        s2.Recorder.AttachVideo(video, DateTimeOffset.UtcNow);
        var files2 = s2.Finish(true);
        Assert.Equal(4, files2.Count);
        Assert.EndsWith(Path.Combine("_files", "rec.mp4").Replace("_files", "_failed_files"), files2[2]);
    }

    [Fact]
    public void Unsafe_test_names_become_valid_file_names()
    {
        var s = new TraceSession("a/b:c*d?", null, TempDir()) { WriteHtml = false };
        var files = s.Finish(testFailed: true);
        Assert.All(files, f => Assert.True(File.Exists(f)));
    }
}
