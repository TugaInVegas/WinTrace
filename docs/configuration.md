# Configuration

- [TraceSession](#tracesession)
- [TraceOptions](#traceoptions)
- [Choosing what to capture per step](#choosing-what-to-capture-per-step)
- [Recording from static helpers](#recording-from-static-helpers)
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
    SaveRecoveredErrors = false  // default: a passed test is not saved just because a step threw and was recovered
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

File names are `<test name>_<yyyyMMdd_HHmmss>[_failed]` with `.zip`, `.html`, `.md` (summary for people and
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

For cheap markers, `rec.Log("message")` records a line on the timeline, and
`rec.Log("message", screenshot: true)` adds one screenshot.

**Nested steps.** A step can contain other steps. When an inner step fails, it is captured once; the outer steps
that fail with the same exception are marked as failed in that step (`↳ failed in #N` in the viewer) without
taking another screenshot or UI tree.

## Recording from static helpers

While a test runs, `TraceRecorder.Current` is its recorder (it is `null` outside a traced test). Use it from
static helpers instead of passing the recorder around. It assumes one traced test at a time, which is the case
for desktop UI tests sharing one desktop.

With `WinTrace.Appium`, drop-in extensions record on `TraceRecorder.Current` and just run the call outside a
traced test:

```csharp
driver.FindElementTraced(MobileBy.AccessibilityId("btnSave")).ClickTraced();
var field = driver.FindElementTraced<WindowsElement>(By.Name("Customer name"));
field.SendKeysTraced("Ada Lovelace");
driver.Traced("switch window", "Settings", () => SwitchToSettings());
```

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
  `Capture.None` for probes that are expected to miss, and consider `SnapshotTimeout`.
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
