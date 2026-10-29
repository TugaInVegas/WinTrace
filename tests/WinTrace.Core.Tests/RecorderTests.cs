using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using WinTrace;
using Xunit;

public class RecorderTests
{
    sealed class FakeSource : ITraceSource
    {
        public byte[]? CaptureScreenshot() => new byte[] { 1, 2, 3 };
        public string? CaptureSnapshot() => "<Window/>";
    }

    [Fact]
    public void Step_records_event_with_artifacts_and_redacts()
    {
        var rec = new TraceRecorder(new FakeSource(), new TraceOptions { Snapshots = SnapshotMode.Always });
        rec.Step("type", "#pwd", () => { }, new Dictionary<string, string> { ["password"] = "hunter2", ["text"] = "hi" });
        var e = Assert.Single(rec.Events);
        Assert.Equal("***", e.Params["password"]);
        Assert.Equal("hi", e.Params["text"]);
        Assert.NotNull(e.ScreenshotBefore);
        Assert.NotNull(e.Snapshot);
    }

    [Fact]
    public void Snapshot_default_only_on_error()
    {
        var rec = new TraceRecorder(new FakeSource());
        rec.Step("ok", null, () => { });
        Assert.Throws<Exception>(() => rec.Step("bad", null, () => throw new Exception("x")));
        Assert.Null(rec.Events[0].Snapshot);
        Assert.NotNull(rec.Events[1].Snapshot);
        Assert.True(rec.HasErrors);
    }

    [Fact]
    public void Step_records_error_and_rethrows()
    {
        var rec = new TraceRecorder();
        Assert.Throws<InvalidOperationException>(() => rec.Step("click", null, () => throw new InvalidOperationException("boom")));
        var e = Assert.Single(rec.Events);
        Assert.Equal("error", e.Kind);
        Assert.Contains("boom", e.Error);
    }

    [Fact]
    public void Save_writes_zip_and_html()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var rec = new TraceRecorder(new FakeSource());
        using (rec.Group("login")) rec.Step("click", "ok", () => { });
        var zipPath = rec.Save(Path.Combine(dir, "trace.zip"));
        using (var z = ZipFile.OpenRead(zipPath))
        {
            var names = z.Entries.Select(x => x.FullName).ToList();
            Assert.Contains("trace.json", names);
            Assert.Contains("events.ndjson", names);
            Assert.Contains("resources/0001-before.png", names);
        }
        var html = File.ReadAllText(rec.SaveHtml(Path.Combine(dir, "trace.html")));
        Assert.Contains("\"name\":\"click\"", html);
        Assert.DoesNotContain("/*__TRACE_DATA__*/", html);
    }
}

public class VideoAndReaderTests
{
    [Fact]
    public void Video_roundtrips_through_zip_and_html()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vid = Path.Combine(dir, "screen.mp4");
        File.WriteAllBytes(vid, new byte[] { 9, 9, 9 });

        var rec = new TraceRecorder();
        rec.AttachVideo(vid, DateTimeOffset.UtcNow);
        rec.Step("click", "ok", () => { });
        var zip = rec.Save(Path.Combine(dir, "t.zip"));

        var loaded = TraceReader.Read(zip, Path.Combine(dir, "x"));
        Assert.Single(loaded.Events);
        Assert.Equal("video/screen.mp4", Assert.Single(loaded.Meta.Videos).File);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(loaded.VideoFiles["video/screen.mp4"]));

        var htmlPath = Path.Combine(dir, "out", "r.html");
        TraceHtmlExporter.Write(htmlPath, loaded.Meta, loaded.Events, id => null, loaded.VideoFiles);
        Assert.Contains("\"src\":\"r_files/screen.mp4\"", File.ReadAllText(htmlPath));
        Assert.True(File.Exists(Path.Combine(dir, "out", "r_files", "screen.mp4")));
    }

    [Fact]
    public void Ffmpeg_arguments_use_gdigrab()
    {
        var a = FfmpegScreenRecorder.BuildArguments("a b.mp4", 5);
        Assert.Contains("-f gdigrab", a);
        Assert.Contains("-framerate 5", a);
        Assert.Contains("\"a b.mp4\"", a);
    }
}
