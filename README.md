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

Anything can be traced manually: `rec.Step("submit form", "toolbar/Submit", () => ...)`.

## Design notes

- **UIA snapshots are errors-only by default.** On WinAppDriver `PageSource` can take
  tens of seconds on large UI trees. Change with `TraceOptions.Snapshots`.
- Param names containing `password`, `secret` or `token` are redacted (configurable).
- Screenshot/snapshot failures never fail the test; they are logged in the trace.

## Roadmap

FlaUI adapter, `wintrace show` CLI, interactive viewer with timeline, video sync (ffmpeg), Playwright trace side-by-side.

License: Apache-2.0
