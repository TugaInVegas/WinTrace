using System;
using System.Collections.Generic;
using System.IO;
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
        Assert.Equal(2, files.Count);
        Assert.Equal(files, attached);
        Assert.All(files, f => Assert.True(File.Exists(f)));
        Assert.Contains("Login_ShowsDashboard_", Path.GetFileName(files[0]));
        Assert.EndsWith("_failed.zip", files[0]);
    }

    [Fact]
    public void Failed_step_saves_even_if_test_reported_passed()
    {
        var s = new TraceSession("t", null, TempDir()) { WriteHtml = false };
        try { s.Recorder.Step("x", null, () => throw new Exception("swallowed by the test")); } catch { }
        Assert.Single(s.Finish(testFailed: false));
    }

    [Fact]
    public void Unsafe_test_names_become_valid_file_names()
    {
        var s = new TraceSession("a/b:c*d?", null, TempDir()) { WriteHtml = false };
        var f = Assert.Single(s.Finish(testFailed: true));
        Assert.True(File.Exists(f));
    }
}
