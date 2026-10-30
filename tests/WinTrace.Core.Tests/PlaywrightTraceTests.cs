using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using WinTrace;
using Xunit;

public class PlaywrightTraceTests
{
    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "playwright-trace.zip");
    static readonly HashSet<string> Redact = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "password" };
    static string TempDir() => Path.Combine(Path.GetTempPath(), "wintrace-pw", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Reads_actions_console_errors_failed_requests_and_screenshots()
    {
        var r = PlaywrightTrace.Read(Fixture, Redact);
        var actions = r.Events.Where(e => e.Kind != "log").ToList();
        Assert.Equal(new[] { "goto", "fill", "fill", "click", "waitForTimeout", "click" }, actions.Select(e => e.Name).ToArray());
        Assert.All(r.Events, e => Assert.Equal("web", e.Origin));
        Assert.Equal("getByTestId('name')", actions[1].Target);
        Assert.Equal("Ada Lovelace", actions[1].Params["value"]);
        Assert.Equal("***", actions[2].Params["value"]);   // selector #password
        Assert.StartsWith("customer-form.spec.js:", actions[1].Location);
        Assert.Contains(r.Events, e => e.Kind == "log" && e.Name.StartsWith("console.error: save failed"));
        Assert.Contains(r.Events, e => e.Kind == "log" && e.Name.StartsWith("HTTP 500 POST"));
        Assert.All(actions.Where(a => a.ScreenshotAfter != null), a => Assert.True(r.Resources.ContainsKey(a.ScreenshotAfter!)));
        Assert.Contains(actions, a => a.ScreenshotAfter != null);
        Assert.Equal("1.63.0", r.PlaywrightVersion);
    }

    [Fact]
    public void Failed_action_has_error_call_log_and_location()
    {
        var failed = PlaywrightTrace.Read(Fixture).Events.Single(e => e.Kind == "error");
        Assert.Equal("click", failed.Name);
        Assert.Equal("locator('#submit')", failed.Target);
        Assert.StartsWith("TimeoutError: Timeout 400ms exceeded", failed.Error);
        Assert.Contains("Call log:", failed.ErrorDetail);
        Assert.Contains("waiting for locator('#submit')", failed.ErrorDetail);
        Assert.Contains("Called from customer-form.spec.js:", failed.ErrorDetail);
    }

    [Fact]
    public void Import_interleaves_by_time_renumbers_and_keeps_the_original()
    {
        var t0 = DateTimeOffset.UtcNow;
        var pw = Path.Combine(TempDir(), "web.zip");
        // Web actions at +100 ms and +300 ms; desktop steps around them.
        SyntheticTrace(pw, t0, ("click", 100, 50, null), ("fill", 300, 20, "Timeout"));

        var rec = new TraceRecorder();
        rec.Step("desktop before", null, () => { });
        try { rec.Step("outer", null, () => rec.Step("inner", null, () => throw new Exception("x"))); } catch { }
        var events = rec.Events.ToList();
        events[0].Start = t0;                      // seq 1: desktop before
        events.Single(e => e.Name == "inner").Start = t0.AddMilliseconds(200);
        events.Single(e => e.Name == "outer").Start = t0.AddMilliseconds(190);

        rec.ImportPlaywrightTrace(pw);
        var merged = rec.Events;
        Assert.Equal(new[] { "desktop before", "click", "outer", "inner", "fill" }, merged.Select(e => e.Name).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, merged.Select(e => e.Seq).ToArray());
        Assert.Equal(4, merged.Single(e => e.Name == "outer").CauseSeq);   // still points at "inner"
        Assert.Equal("error", merged.Single(e => e.Name == "fill").Kind);

        rec.Step("after import", null, () => { });
        Assert.Equal(6, rec.Events.Last().Seq);

        var dir = TempDir();
        var zip = rec.Save(Path.Combine(dir, "t.zip"));
        var loaded = TraceReader.Read(zip, null);
        Assert.Equal("playwright/web.zip", Assert.Single(loaded.Meta.PlaywrightTraces).File);
        Assert.Equal(2, loaded.Meta.PlaywrightTraces[0].Actions);
        Assert.True(loaded.Resources.ContainsKey("playwright/web.zip"));

        var html = File.ReadAllText(rec.SaveHtml(Path.Combine(dir, "t.html")));
        Assert.Contains("\"embedded\":\"playwright/web.zip\"", html);
    }

    [Fact]
    public void Older_format_with_apiName_is_read()
    {
        var pw = Path.Combine(TempDir(), "old.zip");
        SyntheticTrace(pw, DateTimeOffset.UtcNow, ("page.click", 10, 5, null));
        var e = Assert.Single(PlaywrightTrace.Read(pw).Events);
        Assert.Equal("page.click", e.Name);
        Assert.Equal("#ok", e.Target);
    }

    [Fact]
    public void Unreadable_file_is_logged_not_thrown()
    {
        var bad = Path.Combine(TempDir(), "bad.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllText(bad, "not a zip");
        var rec = new TraceRecorder();
        rec.ImportPlaywrightTrace(bad);
        Assert.Contains(rec.Events, e => e.Kind == "log" && e.Name.Contains("Playwright trace not imported"));
    }

    // Minimal Playwright trace: names with a dot are written as apiName (older format), others as class/method.
    static void SyntheticTrace(string path, DateTimeOffset start, params (string Name, double At, double Duration, string? Error)[] calls)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"context-options\",\"version\":6,\"wallTime\":").Append(start.ToUnixTimeMilliseconds()).Append(",\"monotonicTime\":0}\n");
        var i = 1;
        foreach (var (name, at, duration, error) in calls)
        {
            var id = "call@" + i++;
            var head = name.Contains('.') ? $"\"apiName\":\"{name}\"" : $"\"class\":\"Frame\",\"method\":\"{name}\"";
            sb.Append($"{{\"type\":\"before\",\"callId\":\"{id}\",\"startTime\":{at},{head},\"params\":{{\"selector\":\"#ok\"}}}}\n");
            sb.Append($"{{\"type\":\"after\",\"callId\":\"{id}\",\"endTime\":{at + duration}{(error != null ? $",\"error\":{{\"message\":\"{error}\",\"name\":\"TimeoutError\"}}" : "")}}}\n");
        }
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("trace.trace").Open());
        w.Write(sb.ToString());
    }
}

public class PlaywrightStartTests
{
    [Fact]
    public void Trace_start_moves_back_when_the_browser_part_started_earlier()
    {
        var rec = new TraceRecorder();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wintrace-pw", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var pw = System.IO.Path.Combine(dir, "early.zip");
        var earlier = rec.GetMetadata().Started.AddSeconds(-30);
        using (var zip = System.IO.Compression.ZipFile.Open(pw, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new System.IO.StreamWriter(zip.CreateEntry("trace.trace").Open()))
            w.Write($"{{\"type\":\"context-options\",\"wallTime\":{earlier.ToUnixTimeMilliseconds()},\"monotonicTime\":0}}\n" +
                    "{\"type\":\"before\",\"callId\":\"call@1\",\"startTime\":0,\"class\":\"Frame\",\"method\":\"goto\",\"params\":{\"url\":\"http://x\"}}\n" +
                    "{\"type\":\"after\",\"callId\":\"call@1\",\"endTime\":10}\n");
        rec.ImportPlaywrightTrace(pw);
        Assert.True(Math.Abs((rec.GetMetadata().Started - earlier).TotalMilliseconds) < 5);
    }
}
