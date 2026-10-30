# Configuration

## TraceSession

`TraceSession` manages one trace per test.

```csharp
var session = new TraceSession(testName, source, outputDir: "TestResults/WinTrace", options: new TraceOptions { ... })
{
    SaveOnlyOnFailure = true,   // default: keep traces of failed tests only
    WriteHtml = true            // default: write the HTML report next to the zip
};
```

| Member | Purpose |
|---|---|
| `Recorder` | The `TraceRecorder` your test calls. |
| `StartVideo(ffmpegPath = "ffmpeg", fps = 10)` | Record the desktop for this test. The video is kept only if the trace is saved. |
| `Finish(testFailed, attach)` | Stops the video, saves the trace when needed, calls `attach` for each file, returns the files written. A step that threw counts as a failure even if the test caught the exception. |

File names are `<test name>_<yyyyMMdd_HHmmss>[_failed].zip` and `.html`. Characters that are not valid in
file names are removed from the test name.

## TraceOptions

| Option | Default | Purpose |
|---|---|---|
| `Title` | test name | Shown in the viewer header. |
| `Framework` | `""` | Free text shown in the header, e.g. `"WinAppDriver"`. |
| `Screenshots` | `true` | Take screenshots at all. |
| `ScreenshotBefore` | `true` | Also take one before each step. Turn off to halve the screenshot cost on slow drivers. |
| `Snapshots` | `SnapshotMode.OnError` | When to capture the UI tree: `Off`, `OnError` or `Always`. |
| `RedactParams` | `password`, `secret`, `token` | Case-insensitive words. A matching parameter name or step target hides the values. |

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

The start time must be accurate: the viewer uses it to place each step on the video.

## Performance

Every screenshot and snapshot is a call to the driver. On large applications under WinAppDriver:

- **UI tree snapshots** can take tens of seconds. Keep `Snapshots = SnapshotMode.OnError` (the default).
- **Screenshots** usually take 100-500 ms each. Each step takes two by default. Set
  `ScreenshotBefore = false` if that is too slow, or wrap several driver calls in one `rec.Step`.
- **Labels:** elements found through `rec.Find` remember their locator. Elements found elsewhere cost two
  attribute reads for their label; pass a `label` argument to avoid them:
  `rec.Click(element, "Validate button")`.

Identical screenshots are stored once, so long traces of a mostly static screen stay small.

## Privacy

Traces contain screenshots of whatever was on screen, and the values typed by the test. Values are hidden
when a parameter name or step target contains a `RedactParams` word, or when you pass
`sensitive: true` to `SendKeys`. Screenshots are not redacted: avoid tracing screens that show secrets, or
turn screenshots off for those steps' tests.
