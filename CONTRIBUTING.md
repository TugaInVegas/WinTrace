# Contributing

Issues and pull requests are welcome.

- Build and test: `dotnet test` (the core and tests also run on Linux/macOS).
- `dotnet pack -c Release` must stay free of warnings.
- Keep `WinTrace.Core` free of UI-automation and test-framework dependencies; tool-specific code goes in
  an adapter package that implements `ITraceSource`.
- Changes to the trace format (`trace.json`, `events.ndjson`) must stay readable by the viewer and by
  `TraceReader`, and are noted in `CHANGELOG.md`.
- Add a test for every fix or feature.
