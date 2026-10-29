# WinTrace

Playwright-style traces for Windows desktop UI automation. Records every step with
before/after screenshots, timings, errors and (optionally) the UIA tree, then writes
a `trace.zip` and a self-contained `trace.html` you can open anywhere, no server needed.

Works with any Appium-based Windows driver (**WinAppDriver**, **NovaWindowsDriver**).
Core is `netstandard2.0` and has no UI-automation dependency, so adapters for other
tools (e.g. FlaUI) only need to implement `ITraceSource`.

| Package | Purpose |
|---|---|
| `WinTrace.Core` | Recorder, trace.zip format, HTML viewer. |
| `WinTrace.Cli` | `wintrace` dotnet tool: `show` (zip → HTML) and `summary` (console). |
| `WinTrace.Appium` | `AppiumTraceSource`, traced click/type/find helpers, MSTest session helper. |

## Quick start (MSTest + WinAppDriver)

```csharp
[TestInitialize]
public void Init()
{
    _trace = new WinTraceSession(TestContext, new AppiumTraceSource(() => Driver));
}

[TestMethod]
public void Login()
{
    var rec = _trace.Recorder;
    using (rec.Group("Login"))
    {
        var user = rec.Find(Driver, By.Name("User"));
        rec.SendKeys(user, "bob");
        rec.SendKeys(rec.Find(Driver, By.Name("Password")), "secret"); // value is redacted
        rec.Click(rec.Find(Driver, MobileBy.AccessibilityId("Ok")));
    }
}

[TestCleanup] public void Done() => _trace.Finish(); // saved + attached to the .trx on failure
```

Anything can be traced manually: `rec.Step("validate", "ribbon/Validate", () => ...)`.

## Video

Already have a recorder (e.g. ffmpeg)? Attach its file and its start time:

```csharp
rec.AttachVideo(@"Reports_Logs\screen.mp4", startedAt);   // startedAt = when the recording began
```

Or use the built-in one (needs `ffmpeg` on PATH):

```csharp
var video = FfmpegScreenRecorder.Start("Reports_Logs/screen.mp4");
// ... test ...
video.Stop();
rec.AttachVideo(video.OutputPath, video.StartedAt);
```

The video goes into `trace.zip` (`video/`). In the HTML report, clicking a step or the timeline seeks the video to that moment.

## Viewing

```
dotnet tool install -g WinTrace.Cli     # once published; for now: dotnet run --project src/WinTrace.Cli
wintrace show Reports_Logs/Traces/Login_failed.zip
wintrace summary Login_failed.zip       # exit code 1 if any step failed
```

Viewer keys: `↑/↓` or `j/k` navigate, `e` next error, `/` filter. The report opens on the first failing step.

## Design notes

- **UIA snapshots are errors-only by default.** On WinAppDriver `PageSource` can take
  20-27 s on large WPF/Telerik trees. Change with `TraceOptions.Snapshots`.
- Param names containing `password`, `secret` or `token` are redacted (configurable).
- Screenshot/snapshot failures never fail the test; they are logged in the trace.

## Roadmap

FlaUI adapter, Playwright trace side-by-side, NuGet publishing.

License: Apache-2.0
