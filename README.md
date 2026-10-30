# WinTrace

[![NuGet](https://img.shields.io/nuget/v/WinTrace.Core.svg)](https://www.nuget.org/packages/WinTrace.Core)
[![CI](https://github.com/TugaInVegas/WinTrace/actions/workflows/ci.yml/badge.svg)](https://github.com/TugaInVegas/WinTrace/actions/workflows/ci.yml)

Playwright-style traces for Windows desktop UI automation. WinTrace records every step with
before/after screenshots, timings, errors with stack traces, the UIA tree when something fails, and
optionally a screen recording. It writes a `trace.zip` plus a self-contained `trace.html` that opens
in any browser, with no server and nothing to install.

Works with any Appium-based Windows driver (**WinAppDriver**, **NovaWindowsDriver**) and with any test
framework (MSTest, NUnit, xUnit...). The core has no UI-automation dependency, so other tools only need
to implement `ITraceSource` (two methods).

| Package | Purpose |
|---|---|
| [`WinTrace.Core`](https://www.nuget.org/packages/WinTrace.Core) | Recorder, `TraceSession` (one trace per test), trace.zip format, HTML viewer, ffmpeg recorder. netstandard2.0. |
| [`WinTrace.Appium`](https://www.nuget.org/packages/WinTrace.Appium) | `AppiumTraceSource` and traced `Find`/`FindAll`/`Click`/`SendKeys`/`Clear`. |
| [`WinTrace.Cli`](https://www.nuget.org/packages/WinTrace.Cli) | `wintrace` dotnet tool: `show` (zip → HTML) and `summary` (console, CI-friendly exit code). |

## Quick start (MSTest + WinAppDriver)

```
dotnet add package WinTrace.Appium
```

```csharp
using WinTrace;
using WinTrace.Appium;

TraceSession _trace = null!;

[TestInitialize]
public void Init() =>
    _trace = new TraceSession(TestContext.TestName!, new AppiumTraceSource(() => Driver));

[TestMethod]
public void Login()
{
    var rec = _trace.Recorder;
    using (rec.Group("Login"))
    {
        rec.SendKeys(rec.Find(Driver, By.Name("User")), "bob");
        rec.SendKeys(rec.Find(Driver, By.Name("Password")), "secret");   // hidden in the trace: locator says "Password"
        rec.Click(rec.Find(Driver, MobileBy.AccessibilityId("Ok")));
    }
}

// Saved only when the test fails, and attached to the .trx so CI publishes it with the results.
[TestCleanup]
public void Done() =>
    _trace.Finish(TestContext.CurrentTestOutcome != UnitTestOutcome.Passed, TestContext.AddResultFile);
```

NUnit: `_trace.Finish(TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed, p => TestContext.AddTestAttachment(p))`.

Anything can be a step, including your own page-object methods:
`rec.Step("submit form", "toolbar/Submit", () => page.Submit())`.

## What you get

- **Timeline** of all steps; failures in red. The report opens on the first failure.
- **Before/after screenshots** per step. Identical screenshots are stored once.
- **Errors** with the full stack trace.
- **UIA tree** at the moment of failure, as a searchable tree. A typo in a locator stands out at once.
- **Video** (optional) synced to the steps: click a step and the video jumps there.
- **Filters and keys:** `↑/↓` or `j/k` move between steps, `e` jumps to the next error, `/` filters.

## Video

```csharp
_trace.StartVideo();   // in setup; needs ffmpeg on PATH. Kept only if the trace is saved.
```

Already recording with your own tool? Attach the file and the instant the recording began:
`_trace.Recorder.AttachVideo(path, startedAt)`.

## Viewing traces

```
dotnet tool install -g WinTrace.Cli
wintrace show TestResults/WinTrace/Login_20261030_101500_failed.zip
wintrace summary Login_20261030_101500_failed.zip    # exit code 1 if any step failed
```

The `.html` written next to each zip is the same report and can be opened directly.

## Performance and privacy

- **UIA snapshots are taken only on failure by default.** On WinAppDriver `PageSource` can take
  tens of seconds on large UI trees. See `TraceOptions.Snapshots`.
- On slow drivers, set `TraceOptions.ScreenshotBefore = false` to halve the screenshot cost.
- Elements found through `rec.Find` remember their locator, so steps are labelled with no extra driver
  calls. Other elements cost two attribute reads; pass a `label` to avoid them.
- Values are hidden (`***`) when the parameter name or the step target contains `password`, `secret`
  or `token` (`TraceOptions.RedactParams`), or when you pass `sensitive: true`.
- Screenshot or snapshot failures never fail the test; they are logged in the trace.

## Writing an adapter

```csharp
public sealed class MyToolSource : ITraceSource
{
    public byte[]? CaptureScreenshot() => /* PNG bytes */;
    public string? CaptureSnapshot() => /* UI tree as XML (rendered as a tree) or any text */;
}
```

Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md). A FlaUI adapter is the next
planned one.

License: Apache-2.0
