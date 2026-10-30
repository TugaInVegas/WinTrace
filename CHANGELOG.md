# Changelog

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
