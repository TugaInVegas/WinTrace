# Changelog

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
