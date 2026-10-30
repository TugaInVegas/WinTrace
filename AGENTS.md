# AGENTS.md

Guidance for AI agents working **on this repository**. To add WinTrace to another project, read
[docs/ai-agents.md](docs/ai-agents.md) instead.

## Layout

| Path | What |
|---|---|
| `src/WinTrace.Core` | Recorder, `TraceSession`, `TraceReader`, `TraceAnalysis`, HTML exporter, ffmpeg recorder. netstandard2.0, no UI-automation or test-framework dependency. |
| `src/WinTrace.Core/Viewer/viewer.html` | The whole viewer (HTML/CSS/JS, no build step, no external requests). The exporter replaces `/*__TRACE_DATA__*/null` with the trace JSON. |
| `src/WinTrace.Appium` | `AppiumTraceSource` and traced element helpers. Depends only on `IWebDriver`. |
| `src/WinTrace.Cli` | `wintrace` dotnet tool (`show`, `summary`, `inspect`). |
| `tests/` | xUnit tests. `WinTrace.Appium.Tests` fakes the driver with `DispatchProxy`. |
| `docs/` | User documentation; `llms.txt` is the entry point for agents. |

## Commands

- Build and test: `dotnet test` (runs on Linux too).
- Packages: `dotnet pack -c Release -o artifacts` must not produce warnings.
- Viewer changes: generate a trace (see `tests/WinTrace.Core.Tests/RecorderTests.cs`), call `SaveHtml`, and
  check it in a browser at desktop and phone widths, in light and dark themes, with no console errors.

## Rules

- Keep Core free of UI-automation and test-framework dependencies.
- UI tree snapshots stay on-error by default; never make slow captures the default.
- Trace format changes must stay readable by `TraceReader` and the viewer; note them in `CHANGELOG.md`.
- Add a test for every fix or feature.
- Examples, docs and screenshots must be generic: never use names, data or details from a user's own project.
