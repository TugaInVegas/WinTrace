# Troubleshooting

## Tests got much slower

Usually UI tree snapshots. Check that `TraceOptions.Snapshots` is `OnError` (the default) and not `Always`.
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

## Video is out of sync

The viewer places steps on the video using its start time. With `TraceSession.StartVideo` this is taken when
ffmpeg starts capturing. With `AttachVideo`, pass the exact instant your recorder started; a few hundred
milliseconds of error shifts every step by the same amount.

## `ffmpeg` was not found

`StartVideo` runs `ffmpeg` from `PATH`. Install it or pass the full path:
`_trace.StartVideo(@"C:\tools\ffmpeg\bin\ffmpeg.exe")`. The built-in recorder uses `gdigrab`, which needs
an interactive desktop session (not a service account without a desktop).

## The HTML file is large

Screenshots are embedded in the HTML so it works offline. Identical screenshots are stored once. To shrink
it, set `ScreenshotBefore = false` or wrap several driver calls in one step.
