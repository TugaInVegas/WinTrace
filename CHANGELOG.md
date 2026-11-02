# Changelog

## 0.8.1

From the first runs of 0.8.0 (passing, failing and killed mid-run).

### Fixed
- Comparison with the last passing run aligned web steps against desktop steps ("first difference at #1 [web] goto,
  where the last passing run did #1 <desktop step>"). Each origin is now compared with the same origin, and web steps
  are left out when the passing run has none (Playwright traces are usually saved only for failures).
- An aborted run that stopped between steps was described as "failed outside the recorded steps (for example in an
  assertion)". The `.md`, viewer, `inspect`, `list` and run report now say "The run stopped after step #N ...; no
  step was running" (the run report groups these as "Did not finish (aborted) between steps").
- `wintrace snapshot --window` spent most of its time finding the window through the driver's desktop root (~30 s
  on a busy desktop). With a driver on the same machine it now finds the window through Win32 and attaches by handle,
  falling back to the old search when Win32 finds nothing.

### Added
- Aborted traces say in the `.md`, viewer and `inspect` that web steps and video are not available (they are added
  when a test finishes); `wintrace tail` says it shows desktop steps only.

## 0.8.0

From the 0.7.1 wishlist of an MSTest suite driving a desktop app through WinAppDriver and a web app through
Playwright.

### Added
- **Crash-safe traces.** While a test runs, its trace is written step by step to `<output>/.live/` (removed by
  `Finish`). `wintrace recover <folder>` and `TraceSession.RecoverAbandoned(folder)` turn what a crashed, timed-out
  or stopped test left into `*_aborted.zip` (+ `.html`, `.md`) with the running step marked *Unfinished*. New
  outcome `aborted`. `TraceSession.LiveRecordingDefault = false` turns the live copy off.
- **`wintrace tail <folder> [--follow]`**: live view of a running test (current group and step, time in it, errors
  so far, environment warnings, last steps).
- **Test context**: `TraceRecorder.SetContext(key, value)`, shown in the viewer, the `.md`, `summary --json` and the
  run report.
- **Step diagnostics** (`TraceEvent.Diagnostics`): `screen` unchanged/changed for every captured step;
  `AppiumTraceSource` adds `appResponding` (`IsHungAppWindow`), `appCpu` and `appRunning`. Shown in the viewer
  (badges, Details), the `.md` ("Observed: ..." for the failing step and the step before it) and `inspect`.
  `IStepDiagnostics` lets other sources add their own.
- **The app's other windows in the UI tree**: `AppiumTraceSource` adds the visible top-level windows of the app's
  process (popups, dropdowns, dialogs) as `ProcessWindow` elements; the `.md` and `inspect` list them at the failure.
  `IncludeProcessWindows` / `CollectAppDiagnostics` turn them off.
- **Comparison with the last passing run**: `TraceSession.SavePassedSummary` writes a light `*_passed.zip` for passed
  tests (steps and durations only). `wintrace report --history` then shows where a failed test first differs from
  its last passing run and a *Slower than the last passing run* table (steps ≥ 2x and ≥ 1 s slower).
- **`wintrace snapshot --verify <locator>`**: match count and lookup time of a locator in the live window, native
  and as the equivalent XPath, with a verdict (unique / N matches / no match). Accepts `accessibilityid=`, `name=`,
  `classname=`, `xpath=` or a recorded target like `By.Name: Save`; skips reading the tree when only verifying.
- **Noise filter**: `Capture.None` steps and Playwright internal queries are marked `minor`. The viewer's
  *Hide noise* hides them (never the failing step); probes that missed and were recovered are labelled *missed
  probe* / *missed (recovered)* and no longer picked as the failure step by the `.md`, `inspect` and the run report.

### Fixed
- The viewer's step filters (Errors, No logs, text search) did not hide rows on screen.

## 0.7.1

### Fixed
- Call sites in async tests could end in an unrelated frame: after an `await` (without a synchronization context, as
  in MSTest), the rest of the test runs inside the completion of the awaited method, whose frame then appeared as a
  caller. Call sites now stop at async continuation boundaries and at the test, setup or cleanup method.
- "No UI element resembles ..." in the `.md` and `inspect` now also names the case of an action that did not happen
  (a menu or dropdown that did not open) and points to the screenshot at the failure.

## 0.7.0

Fixes from the first live run of 0.6.0 in an MSTest + WinAppDriver + Playwright .NET suite.

### Fixed
- A failure in a `Capture.None` step nested in a normal step was left without any capture (no screenshot, UI tree,
  similar elements or failure image). The innermost enclosing step that may capture now takes it;
  `TraceAnalysis.CaptureFor` finds it, and the viewer, `.md`, `inspect` and the failure image use it.
- False "lock screen" warnings on every trace: the input desktop name was read through the ANSI entry point and
  decoded as UTF-16. It now uses the Unicode one, and an unreadable name never raises a warning.
- `wintrace report --history` with a folder that does not exist yet (first run, clean agent) now reports without
  history instead of failing.
- Call sites no longer spend frames on lambda bodies or on several frames of the same helper type.
- Web-only traces were labelled with the configured desktop framework; the label now follows the events
  (`TraceAnalysis.FrameworkLabel`: "Playwright", "WinAppDriver + Playwright").

### Added
- `TraceOptions.CallSiteSkip`: type or namespace prefixes to leave out of call sites.

### Upgrade notes
- `WinTrace.Appium` references `System.Drawing.Common` 8: projects that use `System.Drawing` and target `net8.0`
  (not `net8.0-windows`) get CA1416 warnings; target `net8.0-windows` or add `[assembly: SupportedOSPlatform("windows")]`.
- `TraceRecorder.Current` is a plain static (one traced test at a time); with parallel tests pass `session.Recorder`.

## 0.6.0

### Added
- `wintrace report`: run summary with failures grouped by cause (step, target and normalized error), each marked
  new, recurring or intermittent against earlier runs (`--history`), TRX totals, tests that failed without a trace and
  tests passing again (`--trx`), environment warnings and links to each test's summary. `--json`; in Azure Pipelines
  (`--azdo` or `TF_BUILD`) it is added to the run's Summary tab and new failures become warnings. `RunReport` and
  `TrxRun` in Core.
- Call sites for WinTrace steps (`TraceOptions.CaptureCallSite`, `CallSiteDepth`): the user code that issued each step,
  e.g. `UiHelpers.cs:120 ← CustomerPage.cs:55 ← CustomerTests.cs:42`, skipping WinTrace, framework code and helpers
  marked `[StackTraceHidden]` / `[DebuggerStepThrough]`.
- Environment diagnostics (`TraceOptions.CaptureEnvironment`): screen, monitors, display scaling, Remote Desktop or
  console session, interactive session, and the input desktop at the start and end (lock screen detection), with
  warnings in the viewer, the `.md`, `inspect` and the run report.
- [Run report](docs/run-report.md) guide.

## 0.5.0

### Added
- Combine Playwright and WinTrace traces: `TraceRecorder.ImportPlaywrightTrace(path)` merges a Playwright trace's
  actions (readable locator, values with passwords hidden, duration, error, call log, source line, screencast
  screenshots), browser console errors/warnings and failed requests into the timeline as web steps. The original
  Playwright trace is kept in the zip and the HTML ("Open in Playwright Trace Viewer").
- `wintrace merge`: combine a WinTrace trace with Playwright traces after the fact, or turn Playwright-only traces into
  WinTrace traces (zip, HTML and `.md`).
- Viewer: desktop and web lanes on the timeline, WEB/DESK badges, Desktop/Web filters, source line of web steps,
  Playwright trace dialog. `.md`, `summary`, `inspect` and `list` mark web steps.
- `TraceSession.EmbedPlaywrightTracesInHtml`; `TraceEvent.Origin` and `Location`; `TraceMetadata.PlaywrightTraces`;
  `TraceArchive.Write`; `TraceRecorder.ExportHtml(path, embedVideo, embedPlaywrightTraces)`.
- [Combining with Playwright](docs/playwright.md) guide.

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

For AI coding agents (and people) working on desktop tests:
- A Markdown report (`.md`) and the screenshot at the failure are written next to each saved trace
  (`TraceSession.WriteMarkdown`, on by default); `TraceReport.Markdown` in Core; `wintrace inspect --markdown`.
- `wintrace list <folder>`: traces newest first with outcome and failing step (`--failed`, `--json`).
- `summary`, `inspect` and `show` accept a folder and use the newest failed trace in it.
- `wintrace snapshot`: the live UI tree of a window from WinAppDriver or an Appium server (NovaWindowsDriver), with a
  suggested locator per element; `--find`, `--out`, `--screenshot`, `--session`, `--root`, `--json`.
- Claude Code skills in `integrations/`: `debug-ui-test` and `write-ui-test`.
- `TraceRecorder.GetMetadata()`, `TraceReader.ReadSummary()`, `TraceAnalysis.IsFailed/StepErrors/FailureStep`.

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
