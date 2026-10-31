# Getting started

WinTrace has two parts: a **recorder** that your tests call, and a **viewer** (a single HTML file) that
you open when a test fails. This guide sets up the recorder in each test framework and shows how to get
the traces out of CI.

- [Install](#install)
- [MSTest](#mstest)
- [NUnit](#nunit)
- [xUnit](#xunit)
- [Recording steps](#recording-steps)
- [Page objects](#page-objects)
- [Publishing traces from CI](#publishing-traces-from-ci)

## Install

```
dotnet add package WinTrace.Appium
```

`WinTrace.Appium` brings `WinTrace.Core`. It needs `Appium.WebDriver` 4.x, which you already have if your
tests use WinAppDriver or NovaWindowsDriver.

## MSTest

```csharp
public TestContext TestContext { get; set; } = null!;
TraceSession _trace = null!;

[TestInitialize]
public void Init() =>
    _trace = new TraceSession(TestContext.TestName!, new AppiumTraceSource(() => Driver));

[TestCleanup]
public void Done() =>
    _trace.Finish(TestContext.CurrentTestOutcome != UnitTestOutcome.Passed, TestContext.AddResultFile);
```

`TestContext.AddResultFile` attaches the zip and HTML to the test result, so they appear next to the test
in the `.trx` and in the Azure DevOps **Tests** tab.

If your tests share a base class, put both methods there once. `new AppiumTraceSource(() => Driver)` reads
the driver lazily, so it keeps working when the session is created later or replaced (for example a new
session attached to a dialog window).

## NUnit

```csharp
TraceSession _trace = null!;

[SetUp]
public void Init() =>
    _trace = new TraceSession(TestContext.CurrentContext.Test.Name, new AppiumTraceSource(() => Driver));

[TearDown]
public void Done() =>
    _trace.Finish(TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed,
                  path => TestContext.AddTestAttachment(path));
```

## xUnit

xUnit does not expose the test outcome to the test class. Mark success at the end of the test instead:

```csharp
public class CustomerFormTests : IDisposable
{
    readonly TraceSession _trace;
    readonly ITestOutputHelper _output;
    bool _passed;

    public CustomerFormTests(ITestOutputHelper output)
    {
        _output = output;
        _trace = new TraceSession(nameof(CustomerFormTests), new AppiumTraceSource(() => Driver));
    }

    [Fact]
    public void SubmitCustomer()
    {
        // ... steps ...
        _passed = true;
    }

    public void Dispose()
    {
        foreach (var file in _trace.Finish(testFailed: !_passed))
            _output.WriteLine("Trace: " + file);
    }
}
```

## Recording steps

Everything goes through `_trace.Recorder` (a `TraceRecorder`):

| Call | Records |
|---|---|
| `rec.Find(ctx, By...)` | A "find" step. The element remembers its locator for later steps. |
| `rec.FindAll(ctx, By...)` | A "find all" step and a log with the match count. |
| `rec.Click(element)` | A "click" step. |
| `rec.SendKeys(element, text)` | A "type" step with the text (hidden for passwords, see below). |
| `rec.Clear(element)` | A "clear" step. |
| `rec.Step(name, target, () => ...)` | Any code as a step. Returns the value of the lambda if it has one. |
| `rec.Step(name, target, Capture.None, () => ...)` | A step that records timing and errors only (for frequent probes). See [Capture](configuration.md#choosing-what-to-capture-per-step). |
| `rec.Log("message")`, `rec.Log("message", screenshot: true)` | A log line on the timeline, optionally with a screenshot. |
| `element.ClickTraced()`, `driver.FindElementTraced(by)` ... | Drop-in versions of Appium calls that record on the running test's trace. |
| `using (rec.Group("Login")) { ... }` | Groups steps in the viewer. Groups can be nested. |

Exceptions are recorded with their stack trace and then rethrown, so your test fails exactly as before.

**Passwords:** values are shown as `***` when the parameter name or the step target contains `password`,
`secret` or `token`, for example `rec.SendKeys(rec.Find(Driver, By.Name("Password")), pwd)`. For any other
field, pass `sensitive: true`.

## Page objects

You do not need to change every call site. Wrap the page-object methods that matter:

```csharp
public class CustomerFormPage
{
    readonly TraceRecorder _rec;
    readonly WindowsDriver<WindowsElement> _driver;

    public CustomerFormPage(WindowsDriver<WindowsElement> driver, TraceRecorder rec) { _driver = driver; _rec = rec; }

    public void Submit() => _rec.Step("submit form", "toolbar/Submit", () =>
    {
        _driver.FindElement(MobileBy.AccessibilityId("btnSubmit")).Click();
        WaitForConfirmation();
    });
}
```

A step that wraps several driver calls gets one before/after screenshot pair, which keeps traces fast.

## Publishing traces from CI

Traces are written to `TestResults/WinTrace` by default (change it with the `outputDir` argument of
`TraceSession`).

**Azure DevOps.** With MSTest, `AddResultFile` already attaches the files to the `.trx`, and
`PublishTestResults@2` uploads them with the results. To also keep the folder as a build artifact:

```yaml
- task: PublishBuildArtifacts@1
  condition: always()
  inputs:
    PathtoPublish: '$(System.DefaultWorkingDirectory)/**/TestResults/WinTrace'
    ArtifactName: traces
```

**GitHub Actions.**

```yaml
- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: traces
    path: '**/TestResults/WinTrace/*'
```

After the run, `wintrace report <folder> --history <archive> --trx <TestResults>` summarizes it: failures grouped by
cause, marked new, recurring or intermittent, published to the Azure DevOps Summary tab. See
[Run report](run-report.md).

Each failed test leaves a `.zip`, a self-contained `.html`, a `.md` summary and the failure screenshot. The `.md`
reads well in CI logs and artifact viewers. With `EmbedVideoInHtml = false`, keep the `.html` together with its
`<name>_files` folder.

Next: [Using the viewer](viewer.md).
