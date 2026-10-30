using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WinTrace;
using Xunit;

public class CaptureTests
{
    sealed class CountingSource : ITraceSource
    {
        public int Screenshots, Snapshots;
        public int SnapshotDelayMs;
        public byte[]? Image = { 1, 2, 3 };
        public byte[]? CaptureScreenshot() { Interlocked.Increment(ref Screenshots); return Image; }
        public string? CaptureSnapshot() { Interlocked.Increment(ref Snapshots); Thread.Sleep(SnapshotDelayMs); return "<Window/>"; }
    }

    [Fact]
    public void Nested_failure_is_captured_once()
    {
        var src = new CountingSource();
        var rec = new TraceRecorder(src, new TraceOptions { ScreenshotBefore = false });
        Assert.Throws<InvalidOperationException>(() =>
            rec.Step("outer", null, () => rec.Step("inner", null, () => throw new InvalidOperationException("boom"))));

        Assert.Equal(1, src.Snapshots);
        Assert.Equal(1, src.Screenshots);
        var inner = rec.Events.Single(e => e.Name == "inner");
        var outer = rec.Events.Single(e => e.Name == "outer");
        Assert.Null(inner.CauseSeq);
        Assert.NotNull(inner.ErrorDetail);
        Assert.Equal(inner.Seq, outer.CauseSeq);
        Assert.Equal("error", outer.Kind);
        Assert.Null(outer.ErrorDetail);
        Assert.Equal(inner.ScreenshotAfter, outer.ScreenshotAfter);
    }

    [Fact]
    public void Wrapped_exception_counts_as_the_same_failure()
    {
        var src = new CountingSource();
        var rec = new TraceRecorder(src, new TraceOptions { ScreenshotBefore = false });
        Assert.Throws<ApplicationException>(() => rec.Step("outer", null, () =>
        {
            try { rec.Step("inner", null, () => throw new InvalidOperationException("boom")); }
            catch (Exception ex) { throw new ApplicationException("wrapped", ex); }
        }));
        Assert.Equal(1, src.Snapshots);
    }

    [Fact]
    public void Capture_none_records_timing_and_error_only()
    {
        var src = new CountingSource();
        var rec = new TraceRecorder(src);
        Assert.ThrowsAny<Exception>(() => rec.Step("probe", "id=x", Capture.None, () => throw new Exception("miss")));
        Assert.Equal(0, src.Screenshots + src.Snapshots);
        Assert.Equal("error", rec.Events[0].Kind);
        Assert.Null(rec.Events[0].CaptureMs);
    }

    [Fact]
    public void Capture_screenshots_never_takes_a_snapshot_and_full_always_does()
    {
        var src = new CountingSource();
        var rec = new TraceRecorder(src);
        Assert.ThrowsAny<Exception>(() => rec.Step("a", null, Capture.Screenshots, () => throw new Exception("x")));
        Assert.Equal(0, src.Snapshots);
        rec.Step("b", null, Capture.Full, () => { });
        Assert.Equal(1, src.Snapshots);
        Assert.NotNull(rec.Events.Single(e => e.Name == "b").Snapshot);
    }

    [Fact]
    public void Capture_and_snapshot_times_are_recorded()
    {
        var src = new CountingSource { SnapshotDelayMs = 60 };
        var rec = new TraceRecorder(src);
        Assert.ThrowsAny<Exception>(() => rec.Step("x", null, () => throw new Exception("x")));
        var e = rec.Events[0];
        Assert.True(e.SnapshotMs >= 50, $"snapshot {e.SnapshotMs}");
        Assert.True(e.CaptureMs >= e.SnapshotMs);
        Assert.True(e.DurationMs < 50, "step duration excludes captures");
    }

    [Fact]
    public void Snapshot_timeout_skips_slow_snapshots_and_logs_it()
    {
        var src = new CountingSource { SnapshotDelayMs = 2000 };
        var rec = new TraceRecorder(src, new TraceOptions { SnapshotTimeout = TimeSpan.FromMilliseconds(100) });
        var started = DateTime.UtcNow;
        Assert.ThrowsAny<Exception>(() => rec.Step("x", null, () => throw new Exception("x")));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5));
        Assert.Null(rec.Events.Single(e => e.Name == "x").Snapshot);
        Assert.Contains(rec.Events, e => e.Kind == "log" && e.Name.Contains("SnapshotTimeout"));
    }

    [Fact]
    public void Log_can_take_a_screenshot()
    {
        var src = new CountingSource();
        var rec = new TraceRecorder(src);
        rec.Log("plain");
        rec.Log("marker", screenshot: true);
        Assert.Null(rec.Events[0].ScreenshotAfter);
        Assert.NotNull(rec.Events[1].ScreenshotAfter);
        Assert.Equal(1, src.Screenshots);
    }

    [Fact]
    public void Jpeg_screenshots_keep_their_format()
    {
        var src = new CountingSource { Image = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 } };
        var rec = new TraceRecorder(src);
        rec.Step("x", null, () => { });
        Assert.EndsWith(".jpg", rec.Events[0].ScreenshotAfter);
    }

    [Fact]
    public void Video_segments_get_unique_names_and_durations()
    {
        var rec = new TraceRecorder();
        var t = DateTimeOffset.UtcNow;
        rec.AttachVideo("/rec/screen.mp4", t, TimeSpan.FromSeconds(30));
        rec.AttachVideo("/other/screen.mp4", t.AddSeconds(40));
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var zip = rec.Save(System.IO.Path.Combine(dir, "t.zip"));
        var videos = TraceReader.Read(zip, System.IO.Path.Combine(dir, "x")).Meta.Videos;
        Assert.Equal(new[] { "video/screen.mp4", "video/screen-2.mp4" }, videos.Select(v => v.File).ToArray());
        Assert.Equal(30, videos[0].DurationSeconds);
        Assert.Null(videos[1].DurationSeconds);
    }
}
