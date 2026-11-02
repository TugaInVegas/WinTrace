# Configuration

- [TraceSession](#tracesession)
- [TraceOptions](#traceoptions)
- [Choosing what to capture per step](#choosing-what-to-capture-per-step)
- [Recording from static helpers](#recording-from-static-helpers)
- [Test context](#test-context)
- [Crash-safe recording and live view](#crash-safe-recording-and-live-view)
- [What the app was doing (step diagnostics)](#what-the-app-was-doing-step-diagnostics)
- [Light traces for passed tests](#light-traces-for-passed-tests)
- [Video](#video)
- [Smaller screenshots](#smaller-screenshots)
- [Performance](#performance)
- [Privacy](#privacy)

## TraceSession

`TraceSession` manages one trace per test.

```csharp
var session = new TraceSession(testName, source, outputDir: "TestResults/WinTrace", options: new TraceOptions { ... })
{
    SaveOnlyOnFailure = true,    // default: keep traces of failed tests only
    WriteHtml = true,            // default: write the HTML report next to the zip
    EmbedVideoInHtml = true,     // default: the HTML is a single file, video included
    WriteMarkdown = true,        // default: also a .md report and the failure screenshot
    EmbedPlaywrightTracesInHtml = true,  // default: merged Playwright traces go inside the HTML
    SaveRecoveredErrors = false, // default: a passed test is not saved just because a step threw and was recovered
    SavePassedSummary = false    // default: nothing is written for a passed test (true: a few-KB steps-only trace)
};
```

| Member | Purpose |
|---|---|
| `Recorder` | The `TraceRecorder` your test calls. Also available as `TraceRecorder.Current` while the test runs. |
| `StartVideo(ffmpegPath = "ffmpeg", fps = 10)` | Record the desktop for this test. The video is kept only if the trace is saved. |
| `Finish(testFailed, attach)` | Stops the video, records the outcome, saves the trace when needed, calls `attach` for each file and returns the files written. |
| `Finish(testFailed, attach, failureMessage)` | The same, plus the failure message, shown in the viewer when the test failed outside any recorded step. |

The outcome you pass to `Finish` is stored in the trace and drives the viewer's passed/failed badge, `wintrace
summary` and its exit code. Steps that threw in a passed test are shown as *recovered errors*.

File names are `<test name>_<yyyyMMdd_HHmmss>[_failed|_aborted|_passed]` with `.zip`, `.html`, `.md` (summary for people and
AI agents) and `.png`/`.jpg` (the screenshot at the failure). Characters that are not valid in
file names are removed from the test name.

## TraceOptions

| Option | Default | Purpose |
|---|---|---|
| `Title` | test name | Shown in the viewer header. |
| `Framework` | `""` | Free text shown in the header, e.g. `"WinAppDriver"`. |
| `Screenshots` | `true` | Take screenshots at all. |
| `ScreenshotBefore` | `true` | Also take one before each step. Turn off to halve the screenshot cost on slow drivers. |
| `Snapshots` | `SnapshotMode.OnError` | When to capture the UI tree: `Off`, `OnError` or `Always`. |
| `SnapshotTimeout` | none | Stop waiting for a UI tree after this long and record the step without it. The driver may still finish the request before it answers the next command. |
| `RedactParams` | `password`, `secret`, `token` | Case-insensitive words. A matching parameter name or step target hides the values. |
| `CaptureCallSite` | `true` | Record which code issued each step (`UiHelpers.cs:120 ← CustomerPage.cs:55 ← CustomerTests.cs:42`). Needs PDB files next to the test assemblies. |
| `CallSiteDepth` | `3` | How many of your own frames to keep. |
| `CallSiteSkip` | empty | Type or namespace prefixes to leave out of call sites (helpers you cannot mark with attributes). |
| `CaptureEnvironment` | `true` | Record screen, display scaling, Remote Desktop or console session, interactive session and whether the screen was locked, at the start and the end. |

**Call sites.** WinTrace skips its own frames, framework code (`System`, `Microsoft`, `OpenQA`...) and lambda bodies,
and collapses consecutive frames of one type to the outermost (the method other code called), so a helper with an
inner wrapper counts once. It stops at the test method (or setup/cleanup method) and at async continuation
boundaries, so frames of an earlier awaited method never appear. Mark your own
tracing helpers with `[StackTraceHidden]` or `[DebuggerStepThrough]` so the call site starts at the code that
called them:

```csharp
[DebuggerStepThrough]   // or [StackTraceHidden]
public static class DesktopTrace { ... }
```

## Choosing what to capture per step

`TraceOptions` applies to every step. For one step, pass a `Capture` value:

```csharp
rec.Step("find", by.ToString(), Capture.None, () => driver.FindElements(by));   // frequent probe: timing only
rec.Step("open settings", "menu/Settings", Capture.Screenshots, () => OpenSettings());
rec.Step("submit form", "toolbar/Submit", Capture.Full, () => Submit());        // UI tree even if it succeeds
```

| Value | Screenshots | UI tree |
|---|---|---|
| `Default` | as configured | as configured (on error by default) |
| `None` | no | no |
| `Screenshots` | as configured | never |
| `Full` | as configured | always |

`Capture.None` steps are also marked *minor*: when one throws and the test handles it (a probe that missed), the
viewer's **Hide noise** button hides it, and the `.md` lists it as *missed (recovered)* rather than as the failure.

For cheap markers, `rec.Log("message")` records a line on the timeline, and
`rec.Log("message", screenshot: true)` adds one screenshot.

**Nested steps.** A step can contain other steps. When an inner step fails, it is captured once; the outer steps
that fail with the same exception are marked as failed in that step (`↳ failed in #N` in the viewer) without
taking another screenshot or UI tree.

## Recording from static helpers

While a test runs, `TraceRecorder.Current` is its recorder (it is `null` outside a traced test). Use it from
static helpers instead of passing the recorder around. It is a plain static, so it assumes **one traced test at a
time**, which is the case for desktop UI tests sharing one desktop. If tests run in parallel (for example web-only
tests importing Playwright traces), do not use `Current`: pass `session.Recorder` explicitly in those tests.

With `WinTrace.Appium`, drop-in extensions record on `TraceRecorder.Current` and just run the call outside a
traced test:

```csharp
driver.FindElementTraced(MobileBy.AccessibilityId("btnSave")).ClickTraced();
var field = driver.FindElementTraced<WindowsElement>(By.Name("Customer name"));
field.SendKeysTraced("Ada Lovelace");
driver.Traced("switch window", "Settings", () => SwitchToSettings());
```

## Test context

Record which data a test used, so a failure says it without opening another log:

```csharp
_trace.Recorder.SetContext("environment", "QA");
_trace.Recorder.SetContext("customer id", customerId);
```

Context is shown in the viewer header and Info tab, in the `.md` and in the run report. Keys containing a
`RedactParams` word (`password`, `secret`, `token`) have their values hidden. Setting a key again replaces it.

## Crash-safe recording and live view

While a test runs, its trace is also written step by step to `<output>/.live/<test>_<time>/` and deleted by
`Finish`. If the test host crashes, the run times out or is stopped by hand, `Finish` never runs, and that folder
is what is left. Turn it into a normal trace:

```
wintrace recover TestResults/WinTrace          # every abandoned live trace under the folder
```

It writes `<test>_<time>_aborted.zip` (+ `.html`, `.md`) with outcome *aborted* and the step that was running marked
as *Unfinished* (or, when it stopped between steps, the last step it completed). Web steps from Playwright traces and
the video are added when a test finishes, so an aborted trace has neither; `wintrace tail` likewise shows desktop
steps only. To do it automatically at the start of the next run (for example in `[AssemblyInitialize]`):

```csharp
TraceSession.RecoverAbandoned("TestResults/WinTrace");   // returns the files written
```

The same folder gives a live view of a long test from another console:

```
wintrace tail TestResults/WinTrace --follow    # current group and step, time in it, errors so far, last steps
```

Set `TraceSession.LiveRecordingDefault = false` before creating sessions to turn the live copy off. Its cost is one
small file append per step; screenshots are written once, when taken.

## What the app was doing (step diagnostics)

Many UI test failures are races: a click that lands while the app is busy, a dropdown that never opens. Each step
records what was observed around it, in `diagnostics`:

| Key | From | Meaning |
|---|---|---|
| `screen` | Core | `unchanged` when the screenshot after the step equals the one before it, `changed` otherwise. An action step that left the screen unchanged, followed by a failure, probably did not take effect. |
| `appResponding` | `AppiumTraceSource` | `no` when Windows considered the app window hung (`IsHungAppWindow`) at the end of the step. |
| `appCpu` | `AppiumTraceSource` | CPU the app process used during the step, relative to all cores (e.g. `93%`). |
| `appRunning` | `AppiumTraceSource` | `no` when the app process exited during the step. |

The viewer marks *not responding* steps and a *no visible change* step right before a failure; the `.md` and
`wintrace inspect` say what was observed at the failing step and the step before it. Steps recorded with
`Capture.None` skip diagnostics.

`AppiumTraceSource` also adds the **other visible windows of the app's process** (dropdowns, popups, dialogs, which
are separate top-level windows) to the UI tree it captures, as `ProcessWindow` elements with title, class and
position, so a trace can tell whether a popup was open. Both run on Windows only and can be turned off:

```csharp
var source = new AppiumTraceSource(() => Driver) { CollectAppDiagnostics = false, IncludeProcessWindows = false };
```

Other sources can add their own diagnostics by implementing `IStepDiagnostics` (`BeginStep` before the step,
`EndStep` returns key/value pairs after it).

## Light traces for passed tests

With `SavePassedSummary = true`, a passed test writes `<test>_<time>_passed.zip`: steps and durations only, no
screenshots, UI trees or video (a few KB). `wintrace report --history` then compares each test with its last
passing run: the first step where a failed run differs, and the steps that got much slower
([run report](run-report.md#comparison-with-the-last-passing-run)). The file is returned by `Finish` but not passed
to the `attach` callback.

## Video

Built in (needs `ffmpeg` on `PATH`, or pass its full path):

```csharp
_trace.StartVideo();                      // in setup
_trace.StartVideo(@"C:\tools\ffmpeg.exe", fps: 5);
```

Already recording with your own tool? Attach the file and the instant the recording began:

```csharp
_trace.Recorder.AttachVideo(@"C:\recordings\screen.mp4", recordingStartedAt);
```

The start time must be accurate: the viewer uses it to place each step on the video. If your recorder writes
several files (for example one per window or monitor switch), call `AttachVideo` once per segment, with each
segment's start and, when you know it, its duration:

```csharp
foreach (var segment in recorder.Segments)
    _trace.Recorder.AttachVideo(segment.Path, segment.StartedAt, segment.Duration);
```

The viewer plays the segment that covers the selected step and shows "No recording at this moment" in gaps.

By default the video is embedded in the HTML, so the report is a single file that works as a CI attachment.
For very long recordings, set `EmbedVideoInHtml = false`: the video is then copied to `<name>_files/` next to the
HTML and passed to the `attach` callback too.

## Smaller screenshots

Screenshots are PNG at the screen's resolution. On high-resolution screens and long tests they make most of a
trace's size. `AppiumTraceSource` can shrink them:

```csharp
var source = new AppiumTraceSource(() => Driver)
{
    Compression = ScreenshotCompression.ToJpeg(quality: 80, maxWidth: 1600)   // or ScreenshotCompression.ScaleDown(1600)
};
```

`ScaleDown` keeps PNG (sharpest text); `ToJpeg` is usually smaller for screens with photos or gradients. A
compressed image is kept only when it is smaller than the original. Compression runs on Windows; elsewhere
screenshots are stored unchanged. Identical screenshots are stored once in the zip and in the HTML.

## Performance

Every screenshot and snapshot is a call to the driver. On large applications under WinAppDriver:

- **UI tree snapshots** can take tens of seconds. Keep `Snapshots = SnapshotMode.OnError` (the default), use
  `Capture.None` for probes that are expected to miss, and consider `SnapshotTimeout`. When a `Capture.None` step
fails inside a normal step, the enclosing step takes the screenshot and UI tree for it, so the failure is never left
without a capture.
- **Screenshots** usually take 100-500 ms each. Each step takes two by default. Set
  `ScreenshotBefore = false` if that is too slow, or wrap several driver calls in one `rec.Step`.
- **Labels:** elements found through `rec.Find` remember their locator. Elements found elsewhere cost two
  attribute reads for their label; pass a `label` argument to avoid them:
  `rec.Click(element, "Submit button")`.

The time spent capturing is recorded per step (`captureMs`, `snapshotMs`) and shown in the viewer and in
`wintrace inspect`; it is not part of the step's duration.

## Privacy

Traces contain screenshots of whatever was on screen, and the values typed by the test. Values are hidden
when a parameter name or step target contains a `RedactParams` word, or when you pass
`sensitive: true` to `SendKeys`. Screenshots are not redacted: avoid tracing screens that show secrets, or
use `Capture.None` for those steps.
