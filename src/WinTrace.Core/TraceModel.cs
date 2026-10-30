using System;
using System.Collections.Generic;

namespace WinTrace
{
    public sealed class TraceEvent
    {
        public int Seq { get; set; }
        /// <summary>action | log | error</summary>
        public string Kind { get; set; } = "action";
        public string Name { get; set; } = "";
        public string? Target { get; set; }
        public Dictionary<string, string> Params { get; set; } = new Dictionary<string, string>();
        public DateTimeOffset Start { get; set; }
        public double DurationMs { get; set; }
        public string? Error { get; set; }
        /// <summary>Full exception text (type, message, stack) when the step failed.</summary>
        public string? ErrorDetail { get; set; }
        public string? Group { get; set; }
        public string? ScreenshotBefore { get; set; }
        public string? ScreenshotAfter { get; set; }
        public string? Snapshot { get; set; }
    }

    public sealed class TraceVideo
    {
        /// <summary>Path inside trace.zip, e.g. video/screen.mp4.</summary>
        public string File { get; set; } = "";
        /// <summary>Wall-clock instant the recording started; used to sync steps with video time.</summary>
        public DateTimeOffset Started { get; set; }
        /// <summary>Relative URL used by the HTML report (set on export).</summary>
        public string? Src { get; set; }
    }

    public sealed class TraceMetadata
    {
        public int FormatVersion { get; set; } = 1;
        public string Title { get; set; } = "";
        public string Framework { get; set; } = "";
        public DateTimeOffset Started { get; set; }
        public DateTimeOffset? Stopped { get; set; }
        public List<TraceVideo> Videos { get; set; } = new List<TraceVideo>();
        public string Machine { get; set; } = Environment.MachineName;
        public string Os { get; set; } = Environment.OSVersion.ToString();
    }

    public enum SnapshotMode { Off, OnError, Always }

    public sealed class TraceOptions
    {
        public string Title { get; set; } = "WinTrace";
        public string Framework { get; set; } = "";
        public bool Screenshots { get; set; } = true;
        /// <summary>Also capture a screenshot before each step. Turn off to halve capture cost on slow drivers.</summary>
        public bool ScreenshotBefore { get; set; } = true;
        /// <summary>UI-tree snapshots can be very slow on WinAppDriver (tens of seconds); default is errors only.</summary>
        public SnapshotMode Snapshots { get; set; } = SnapshotMode.OnError;
        /// <summary>
        /// Case-insensitive substrings. A param whose name matches is replaced with ***; when the step
        /// target matches (e.g. "Password box"), all of that step's params are replaced.
        /// </summary>
        public ISet<string> RedactParams { get; set; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "password", "secret", "token" };
    }

    /// <summary>Implement per automation framework: how to grab a PNG and a UI-tree snapshot.</summary>
    public interface ITraceSource
    {
        byte[]? CaptureScreenshot();
        /// <summary>Serialized UI tree (XML or JSON); null if unavailable.</summary>
        string? CaptureSnapshot();
    }
}
