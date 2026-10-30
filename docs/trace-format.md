# Trace format

A trace is a zip file. The format is versioned by `formatVersion` in `trace.json` (currently `1`).
`TraceReader.Read` in WinTrace.Core loads it.

```
trace.zip
├── trace.json          metadata
├── events.ndjson       one JSON event per line, ordered by seq
├── resources/<sha1>.png   screenshots (.png, or .jpg when compressed), named by content hash (stored once)
├── snapshots/<sha1>.xml   UI trees (.json or .txt when not XML)
├── video/<name>.mp4       optional screen recording, one file per segment
└── playwright/<name>.zip  optional original Playwright traces merged into this one
```

## trace.json

| Field | Type | Meaning |
|---|---|---|
| `formatVersion` | int | Format version. |
| `title` | string | Test name or title. |
| `framework` | string | Free text, e.g. driver name. |
| `started`, `stopped` | ISO 8601 | Trace start and end (UTC). |
| `outcome` | string? | `passed` or `failed` as reported by the test framework; absent in traces where it was not reported. |
| `failureMessage` | string? | Failure message reported with the outcome. |
| `playwrightTraces` | array | Merged Playwright traces: `{ file, playwrightVersion?, actions }`. |
| `videos` | array | Segments: `{ file, started, durationSeconds? }`, the path in the zip, the instant the recording started and its length when known. |
| `machine`, `os` | string | Where the trace was recorded. |

## events.ndjson

| Field | Type | Meaning |
|---|---|---|
| `seq` | int | Order of the step, starting at 1. |
| `kind` | string | `action`, `error` (the step threw) or `log`. |
| `name` | string | Step name (`click`, `type`, ...) or log message. |
| `target` | string? | Locator or label. |
| `params` | object | String parameters, with sensitive values already replaced by `***`. |
| `start` | ISO 8601 | Step start. |
| `durationMs` | number | Step duration. |
| `group` | string? | Group path, nested groups joined by ` / `. |
| `error` | string? | `ExceptionType: message`. |
| `errorDetail` | string? | Full exception text with stack trace. |
| `screenshotBefore`, `screenshotAfter`, `snapshot` | string? | Paths of resources in the zip. Log entries can have a `screenshotAfter`. |
| `origin` | string? | `web` for events imported from a Playwright trace; absent for WinTrace steps. |
| `location` | string? | Source location that issued the call (e.g. `LoginTests.cs:42`), when known. |
| `causeSeq` | int? | The step failed because a nested step (`seq` = this value) failed with the same exception; it shares that step's capture and is not a separate failure. |
| `captureMs`, `snapshotMs` | number? | Time spent on screenshots and UI tree for this step, and the UI tree part of it. Not included in `durationMs`. |

A test failed when `outcome` is `failed`. For traces without `outcome`, readers should treat the test as failed when
any event has `kind: error` and no `causeSeq`.

Readers should ignore unknown fields: new fields may be added without changing `formatVersion`.
