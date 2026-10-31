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
        /// <summary>
        /// Set when the step failed because a nested step failed with the same exception: the seq of the step where
        /// it was first recorded (and captured). Steps with a cause are not counted as separate failures.
        /// </summary>
        public int? CauseSeq { get; set; }
        /// <summary>Time spent taking screenshots and the UI snapshot for this step (not part of DurationMs).</summary>
        public double? CaptureMs { get; set; }
        /// <summary>Part of <see cref="CaptureMs"/> spent on the UI snapshot.</summary>
        public double? SnapshotMs { get; set; }
        /// <summary>Where the event comes from: null for WinTrace steps, "web" for events imported from a Playwright trace.</summary>
        public string? Origin { get; set; }
        /// <summary>Source location that issued the call when known (e.g. "LoginTests.cs:42"), from Playwright stacks.</summary>
        public string? Location { get; set; }
    }

    /// <summary>What to capture for one step, overriding <see cref="TraceOptions"/>.</summary>
    public enum Capture
    {
        /// <summary>Follow <see cref="TraceOptions"/>.</summary>
        Default,
        /// <summary>Record name, timing and error only: no screenshots, no UI snapshot (for frequent probes and polls).</summary>
        None,
        /// <summary>Screenshots as configured, never a UI snapshot.</summary>
        Screenshots,
        /// <summary>Screenshots and a UI snapshot, even if the step succeeds.</summary>
        Full
    }

    public sealed class TraceVideo
    {
        /// <summary>Path inside trace.zip, e.g. video/screen.mp4.</summary>
        public string File { get; set; } = "";
        /// <summary>Wall-clock instant the recording started; used to sync steps with video time.</summary>
        public DateTimeOffset Started { get; set; }
        /// <summary>Length of the recording when known; lets the viewer show gaps between segments.</summary>
        public double? DurationSeconds { get; set; }
        /// <summary>Relative URL used by the HTML report when the video is not embedded (set on export).</summary>
        public string? Src { get; set; }
        /// <summary>Key of the embedded video in the HTML report's resources (set on export).</summary>
        public string? Embedded { get; set; }
    }

    public sealed class PlaywrightTraceRef
    {
        /// <summary>Path inside trace.zip, e.g. playwright/Login_failed.zip.</summary>
        public string File { get; set; } = "";
        /// <summary>Playwright version that wrote it, when known.</summary>
        public string? PlaywrightVersion { get; set; }
        /// <summary>Number of web actions imported from it.</summary>
        public int Actions { get; set; }
        /// <summary>Key of the embedded copy in the HTML report's resources (set on export).</summary>
        public string? Embedded { get; set; }
        /// <summary>Relative URL of the copy next to the HTML report when not embedded (set on export).</summary>
        public string? Src { get; set; }
    }

    public sealed class TraceMetadata
    {
        public int FormatVersion { get; set; } = 1;
        public string Title { get; set; } = "";
        public string Framework { get; set; } = "";
        public DateTimeOffset Started { get; set; }
        public DateTimeOffset? Stopped { get; set; }
        /// <summary>"passed" or "failed" as reported by the test framework; null when not reported.</summary>
        public string? Outcome { get; set; }
        /// <summary>Failure message reported with the outcome, if any.</summary>
        public string? FailureMessage { get; set; }
        public List<TraceVideo> Videos { get; set; } = new List<TraceVideo>();
        /// <summary>Original Playwright traces merged into this one, kept whole so they open in Playwright's own viewer.</summary>
        public List<PlaywrightTraceRef> PlaywrightTraces { get; set; } = new List<PlaywrightTraceRef>();
        /// <summary>
        /// Desktop environment at the start (screen, scaling, session, input desktop...) and <c>desktopAtEnd</c>, when
        /// known. See <see cref="TraceAnalysis.EnvironmentWarnings"/>.
        /// </summary>
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
        public string Machine { get; set; } = System.Environment.MachineName;
        public string Os { get; set; } = System.Environment.OSVersion.ToString();
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
        /// Stop waiting for a UI snapshot after this long and record the step without it (null: no limit).
        /// The driver may still finish the request in the background before it answers the next command.
        /// </summary>
        public TimeSpan? SnapshotTimeout { get; set; }
        /// <summary>
        /// Record which code issued each step (e.g. "AuxMethods.cs:120 ← CustomerPage.cs:55 ← CustomerTests.cs:42"),
        /// from the call stack, skipping WinTrace, framework code and methods or types marked
        /// <c>[StackTraceHidden]</c> or <c>[DebuggerStepThrough]</c> (mark your own tracing helpers that way).
        /// Needs PDB files next to the test assemblies (the default for Debug and Release builds). Default true.
        /// </summary>
        public bool CaptureCallSite { get; set; } = true;
        /// <summary>How many of your own frames to keep in a step's call site (default 3).</summary>
        public int CallSiteDepth { get; set; } = 3;
        /// <summary>Record the desktop environment (screen, scaling, session, lock screen) at the start and end (default true).</summary>
        public bool CaptureEnvironment { get; set; } = true;
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
