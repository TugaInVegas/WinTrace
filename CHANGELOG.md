# Changelog

## 0.4.0

Based on feedback from a real MSTest + WinAppDriver suite.

### Breaking
- The test outcome passed to `TraceSession.Finish` now decides whether a test failed. Steps that threw in a passed
  test are *recovered errors*: they no longer mark the trace as failed nor save it, unless
  `SaveRecoveredErrors = true`.
- The HTML report embeds the video by default (`EmbedVideoInHtml`). Set it to `false` for the previous behaviour.

### Added
- Outcome in the trace (`outcome`, `failureMessage`); the viewer badge, banner, `wintrace summary` and its exit
  code use it. "Test failed outside the recorded steps" is shown when no step failed.
- `Finish(testFailed, attach, failureMessage)`.
- Per-step capture control: `Step(name, target, Capture.None | Screenshots | Full, ...)`; `Log(message, screenshot: true)`.
- `TraceOptions.SnapshotTimeout`.
- Capture times per step (`captureMs`, `snapshotMs`) in the trace, viewer and CLI.
- Video segments: call `AttachVideo` once per file, with an optional duration; the viewer picks the segment for
  each step and shows gaps.
- Videos embedded in the HTML, so a single file works as a CI attachment; when not embedded, the video file is
  also passed to `attach`. Over HTTP without range requests the viewer loads the video as a Blob so it can seek.
- `AppiumTraceSource.Compression`: `ScreenshotCompression.ToJpeg(quality, maxWidth)` and `ScaleDown(maxWidth)`.
- `TraceRecorder.Current` (set by `TraceSession`) and drop-in Appium extensions: `ClickTraced`, `SendKeysTraced`,
  `ClearTraced`, `FindElementTraced`, `FindElementsTraced`, `Traced`.
- `wintrace inspect`: defaults to the step where the test failed; finds the locator in the error text or recent logs
  when the step has no target; `--extract <dir> --all` saves every screenshot; shows nested-failure causes.
- [API stability](docs/stability.md) page.

### Fixed
- A failure inside nested steps was captured once per level (screenshots, UI tree and error events). It is now
  captured once; outer steps are marked as failed in that step (`causeSeq`).
- `wintrace summary` printed times with the OS decimal separator; output now always uses a dot.
- A passed test's report could show the failure banner.

## 0.3.0

### Changed
- Traces are written to `TestResults/WinTrace` by default. Pass `outputDir` to
  `TraceSession` to choose another folder.

### Added
- New viewer: tabs (Screenshots, Details, UI tree, Logs, Trace info), failure banner, timeline with time
  scale and screenshot preview, collapsible groups, duration bars, light/dark theme toggle, keyboard help,
  links to a step (`report.html#7`).
- UI tree locator picker: hover the screenshot to highlight elements, click to pick one and copy
  ready-made locators. A failed find preselects the closest matching element.
- Screenshot lightbox; the video plays next to the screenshots and keeps its position between steps.
- Documentation in `docs/`: getting started (MSTest, NUnit, xUnit, CI), viewer, configuration, adapters,
  trace format, troubleshooting.

- AI agents: `docs/ai-agents.md` integration recipe, `llms.txt` (also inside each package), `AGENTS.md`.
- `wintrace inspect`: failed step, stack trace, UI elements similar to a failed locator, UI tree as text
  (`--tree`), screenshots saved as PNG (`--extract`). `wintrace summary --json` and `inspect --json`.
- `TraceAnalysis` in Core: parse UI trees, compact text tree, find elements similar to a locator.

### Fixed
- Clicking a screenshot did nothing in Chrome (it opened a blocked `data:` URL).

## 0.2.0

### Breaking
- `WinTrace.Appium` no longer depends on MSTest. `WinTraceSession` is replaced by `TraceSession` in
  `WinTrace.Core`, which works with any test framework (pass the outcome and an attach callback).
- `TraceRecorder` no longer implements `IDisposable` (it held nothing to dispose).
- Resources in trace.zip are named by content hash (`resources/<sha1>.png`) instead of step number.

### Fixed
- Text typed into fields whose label or locator contains "password", "secret" or "token" was not
  hidden. It now is, and `SendKeys(..., sensitive: true)` hides any value.
- Nested groups lost the outer group when the inner one closed.
- Video timing: the recording start is anchored to when ffmpeg actually starts capturing.
- `wintrace summary` left extracted videos in the temp folder; a non-trace file now gives a clear error.

### Added
- Steps on elements found through `rec.Find`/`FindAll` are labelled by locator with no extra driver calls.
- `FindAll`, `Clear`; `TraceSession.StartVideo()`.
- Error stack traces, searchable UIA tree and copy-target button in the viewer.
- `TraceOptions.ScreenshotBefore`; identical screenshots stored once.
- Symbols (.snupkg), Source Link and XML docs in the packages.

## 0.1.0
- First release: recorder, trace.zip, HTML viewer with timeline and video sync, Appium adapter, CLI.
