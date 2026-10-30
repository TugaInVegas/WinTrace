# Trace format

A trace is a zip file. The format is versioned by `formatVersion` in `trace.json` (currently `1`).
`TraceReader.Read` in WinTrace.Core loads it.

```
trace.zip
├── trace.json          metadata
├── events.ndjson       one JSON event per line, ordered by seq
├── resources/<sha1>.png   screenshots, named by content hash (identical screenshots stored once)
├── snapshots/<sha1>.xml   UI trees (.json or .txt when not XML)
└── video/<name>.mp4       optional screen recording
```

## trace.json

| Field | Type | Meaning |
|---|---|---|
| `formatVersion` | int | Format version. |
| `title` | string | Test name or title. |
| `framework` | string | Free text, e.g. driver name. |
| `started`, `stopped` | ISO 8601 | Trace start and end (UTC). |
| `videos` | array | `{ file, started }`: path in the zip and the instant the recording started. |
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
| `screenshotBefore`, `screenshotAfter`, `snapshot` | string? | Paths of resources in the zip. |

Readers should ignore unknown fields: new fields may be added without changing `formatVersion`.
