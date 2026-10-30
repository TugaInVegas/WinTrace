# Troubleshooting

## Tests got much slower

Usually UI tree snapshots. Check that `TraceOptions.Snapshots` is `OnError` (the default) and not `Always`, and
use `Capture.None` for probes that are expected to miss (each failed probe would otherwise take a UI tree).
`wintrace inspect` and the viewer show the capture time of each step.
On WinAppDriver a snapshot of a large WPF tree can take tens of seconds. Then see
[Performance](configuration.md#performance) for screenshots and labels.

## No screenshots in the trace

- The driver session must support screenshots (`ITakesScreenshot`). The **Logs** tab shows
  `screenshot failed: ...` when a capture threw.
- With `new AppiumTraceSource(() => Driver)`, `Driver` must not be null while the steps run.
- Check that `TraceOptions.Screenshots` is `true`.

## No trace file was written

By default traces are kept only for failed tests (`SaveOnlyOnFailure = true`). Make sure `Finish` runs in
your cleanup method and receives the real outcome. Files go to `TestResults/WinTrace` relative to the test's
working directory (usually `bin/<config>/<tfm>`).

## The UI tree tab is empty

Snapshots are taken only for failed steps by default. For other steps the tab says so. Set
`Snapshots = SnapshotMode.Always` while you investigate.

## Highlight boxes are shifted

The viewer assumes the screenshot shows the root element of the tree:

- **App session:** the screenshot and the root `Window` should match.
- **Root (desktop) session:** the root is the desktop and the screenshot is the whole screen.

If the screenshot is larger than the root element, the viewer assumes it is the whole screen in screen
pixels. Display scaling (DPI above 100%) can still shift boxes when the driver reports logical pixels and
the screenshot is in physical pixels.

## Video does not play or stays at 0:00

- Report written with `EmbedVideoInHtml = false`: the `<name>_files/` folder must sit next to the HTML. Archive
  and attach them together, or keep the default (embedded).
- Served over HTTP: the viewer downloads the whole video when the server does not support range requests, which
  can take a moment for long recordings.

## Video is out of sync

The viewer places steps on the video using its start time. With `TraceSession.StartVideo` this is taken when
ffmpeg starts capturing. With `AttachVideo`, pass the exact instant your recorder started; a few hundred
milliseconds of error shifts every step by the same amount.

## `wintrace snapshot` fails

- "Could not reach the driver": start WinAppDriver (or the Appium server) and check `--url`
  (WinAppDriver's default is `http://127.0.0.1:4723`; some setups use a `/wd/hub` path).
- "did not return a session id" with Appium 2: pass `--automation-name` (for example `NovaWindows`).
- "No top-level window": use the title shown in the window's title bar (exact, or a part of it), or `--root`.
- `--root` can be slow on WinAppDriver with many windows open: prefer `--window`.

## `ffmpeg` was not found

`StartVideo` runs `ffmpeg` from `PATH`. Install it or pass the full path:
`_trace.StartVideo(@"C:\tools\ffmpeg\bin\ffmpeg.exe")`. The built-in recorder uses `gdigrab`, which needs
an interactive desktop session (not a service account without a desktop).

## The trace or HTML file is large

Screenshots (and the video, by default) are embedded in the HTML so it works offline. Identical screenshots are
stored once. To shrink traces:

- `AppiumTraceSource.Compression = ScreenshotCompression.ToJpeg(80, 1600)` or `ScaleDown(1600)` on high-resolution screens;
- `ScreenshotBefore = false`, or wrap several driver calls in one step;
- `Capture.None` for frequent probes;
- `EmbedVideoInHtml = false` for long recordings (the video stays in the zip and next to the HTML).

## A failed probe saved the trace of a passed test

Since 0.4.0 the outcome passed to `Finish` decides: steps that threw in a passed test are *recovered errors* and do
not save the trace unless `SaveRecoveredErrors = true`. Use `Capture.None` for probes that are expected to miss.

## The test failed but the viewer shows no failed step

The test failed outside any recorded step (for example in an assertion). The banner says so and shows the
failure message when you pass it: `Finish(testFailed, attach, failureMessage)`.
