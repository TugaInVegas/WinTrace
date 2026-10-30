# API stability

WinTrace is at 0.x. This page says what you can rely on before 1.0, so you can adopt it without surprises.

## Stable

These will not change in a breaking way before 1.0. If one ever must, it gets a deprecation period of at least one
minor version, with the old member marked `[Obsolete]` and an entry in the changelog.

| Area | Members |
|---|---|
| Per-test session | `new TraceSession(testName, source, outputDir, options)`, `Recorder`, `Finish(testFailed, attach)`, `Finish(testFailed, attach, failureMessage)`, `SaveOnlyOnFailure`, `WriteHtml`, `StartVideo` |
| Recording | `TraceRecorder.Step(...)` (all overloads), `Group`, `Log`, `AttachVideo`, `TraceRecorder.Current` |
| Options | `TraceOptions` properties and their defaults, `SnapshotMode`, `Capture` |
| Sources | `ITraceSource`, `AppiumTraceSource` |
| Appium helpers | `Find`, `FindAll`, `Click`, `SendKeys`, `Clear` and the `...Traced` extensions |
| Trace format | `formatVersion` 1: existing fields keep their meaning; new fields can be added (readers ignore unknown fields) |
| CLI | `wintrace show`, `summary`, `inspect`, their options and exit codes; `--json` fields keep their names (new fields can be added) |

## Can still change

- The viewer's look and layout.
- The wording of text (non-JSON) output of the CLI.
- `TraceAnalysis`, `TraceReader`, `TraceHtmlExporter` and other lower-level types.
- Default output folder and file names (announced in the changelog).

## Versioning

- Patch versions (0.4.x): fixes only.
- Minor versions (0.x.0): new features; anything breaking is listed under **Breaking** in the
  [changelog](../CHANGELOG.md), with a migration note.
- Pin an exact version in production test suites until 1.0 if you prefer no surprises; upgrading is a
  one-line change.
