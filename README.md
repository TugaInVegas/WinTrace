# WinTrace

[![NuGet](https://img.shields.io/nuget/v/WinTrace.Core.svg)](https://www.nuget.org/packages/WinTrace.Core)
[![CI](https://github.com/TugaInVegas/WinTrace/actions/workflows/ci.yml/badge.svg)](https://github.com/TugaInVegas/WinTrace/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/TugaInVegas/WinTrace/blob/main/LICENSE)

**Playwright-style traces for Windows desktop UI tests.** When a test fails, open one HTML file and see
every step: screenshots before and after, timings, the error and stack trace, the UI Automation tree at the
moment of failure, and an optional screen recording synced to the steps.

Works with **WinAppDriver** and **NovaWindowsDriver** (any Appium/Selenium Windows driver), and with any test
framework: MSTest, NUnit, xUnit.

![WinTrace viewer: step list, timeline, failure banner, before/after screenshots and video](https://raw.githubusercontent.com/TugaInVegas/WinTrace/main/docs/images/viewer-overview.png)

## Why

Desktop UI tests fail with messages like *"An element could not be located"*. Finding out why means re-running
the test and watching it. WinTrace records what happened, so the trace shows which step failed, what the screen
looked like and what the UI tree actually contained. A typo in a locator stands out at once:

![UI tree picker: hover the screenshot to highlight elements and copy their locator](https://raw.githubusercontent.com/TugaInVegas/WinTrace/main/docs/images/viewer-picker.png)

## Install

```
dotnet add package WinTrace.Appium        # adapter for WinAppDriver / NovaWindowsDriver (brings WinTrace.Core)
dotnet tool install -g WinTrace.Cli       # optional: wintrace show / summary
```

## Five-minute setup (MSTest)

```csharp
using WinTrace;
using WinTrace.Appium;

[TestClass]
public class CustomerFormTests
{
    public TestContext TestContext { get; set; } = null!;
    TraceSession _trace = null!;

    [TestInitialize]
    public void Init() =>
        _trace = new TraceSession(TestContext.TestName!, new AppiumTraceSource(() => Driver));

    [TestMethod]
    public void SubmitCustomer()
    {
        var rec = _trace.Recorder;
        using (rec.Group("Fill in form"))
        {
            rec.SendKeys(rec.Find(Driver, By.Name("Customer name")), "Ada Lovelace");
            rec.Click(rec.Find(Driver, MobileBy.AccessibilityId("btnSave")));
        }
        rec.Step("submit form", "toolbar/Submit", () => page.Submit());   // any code can be a step
    }

    // Saved only when the test fails, and attached to the .trx so CI publishes it with the results.
    [TestCleanup]
    public void Done() =>
        _trace.Finish(TestContext.CurrentTestOutcome != UnitTestOutcome.Passed, TestContext.AddResultFile);
}
```

A failing run writes `TestResults/WinTrace/<test>_<time>_failed.zip` and a `.html` next to it. Open the
HTML in any browser.

## Web + desktop in one trace

Tests that use Playwright for a web app and WinAppDriver for a desktop app can merge the Playwright trace into the
WinTrace one: one timeline with web and desktop steps, browser console errors and failed requests, and the original
Playwright trace kept whole to open in Playwright's viewer (DOM snapshots, network, console).
`_trace.Recorder.ImportPlaywrightTrace(path)` in the test, or `wintrace merge` afterwards.
See [Combining with Playwright](https://github.com/TugaInVegas/WinTrace/blob/main/docs/playwright.md).

## Using an AI coding agent?

WinTrace is built to be added and used by coding agents (Claude Code, Copilot, Cursor, Codex...). Ask yours:

> Add WinTrace to this test project. Follow https://raw.githubusercontent.com/TugaInVegas/WinTrace/main/docs/ai-agents.md

What agents get:

- **A Markdown report and the failure screenshot** next to every failed trace: read them, no tool needed.
- **`wintrace list TestResults --failed`** and **`wintrace inspect TestResults`**: failed tests after a run and the
  newest failure in detail (similar elements with suggested locators, stack trace, `--tree`, `--extract`, `--json`).
- **`wintrace snapshot --window "<title>" --find <text>`**: the live UI tree of the running app with a locator per
  element, to write new tests with real locators instead of guesses.
- **Claude Code skills** ([integrations](https://github.com/TugaInVegas/WinTrace/tree/main/integrations)) for
  debugging a failed UI test and writing a new one, and an
  [`llms.txt`](https://github.com/TugaInVegas/WinTrace/blob/main/llms.txt) (also inside each package).

## Documentation

| Guide | What it covers |
|---|---|
| [AI agents](https://github.com/TugaInVegas/WinTrace/blob/main/docs/ai-agents.md) | Recipe for coding agents to integrate WinTrace, and to debug failures from traces |
| [Getting started](https://github.com/TugaInVegas/WinTrace/blob/main/docs/getting-started.md) | MSTest, NUnit and xUnit setup; page objects; publishing traces from Azure DevOps and GitHub Actions |
| [Using the viewer](https://github.com/TugaInVegas/WinTrace/blob/main/docs/viewer.md) | Timeline, tabs, UI tree picker, keyboard shortcuts, sharing links |
| [Configuration](https://github.com/TugaInVegas/WinTrace/blob/main/docs/configuration.md) | `TraceOptions`, `TraceSession`, video, privacy and performance tuning |
| [Combining with Playwright](https://github.com/TugaInVegas/WinTrace/blob/main/docs/playwright.md) | Merge Playwright traces into WinTrace traces, keep the originals for Playwright's viewer |
| [Writing an adapter](https://github.com/TugaInVegas/WinTrace/blob/main/docs/adapters.md) | Support another tool (e.g. FlaUI) in two methods |
| [API stability](https://github.com/TugaInVegas/WinTrace/blob/main/docs/stability.md) | What will not change before 1.0, and how versions are numbered |
| [Trace format](https://github.com/TugaInVegas/WinTrace/blob/main/docs/trace-format.md) | What is inside `trace.zip`, for tools that read it |
| [Troubleshooting](https://github.com/TugaInVegas/WinTrace/blob/main/docs/troubleshooting.md) | Slow snapshots, missing screenshots, video out of sync, highlight offset |

## Packages

| Package | Purpose |
|---|---|
| [`WinTrace.Core`](https://www.nuget.org/packages/WinTrace.Core) | Recorder, `TraceSession`, trace format, HTML viewer, ffmpeg recorder. netstandard2.0, no UI-automation dependency. |
| [`WinTrace.Appium`](https://www.nuget.org/packages/WinTrace.Appium) | `AppiumTraceSource` and traced `Find`, `FindAll`, `Click`, `SendKeys`, `Clear`. |
| [`WinTrace.Cli`](https://www.nuget.org/packages/WinTrace.Cli) | `wintrace list`, `summary`, `inspect`, `show` for traces, and `snapshot` for the live UI tree (console or `--json`, for CI and AI agents). |

## Contributing

Issues and pull requests are welcome; see [CONTRIBUTING.md](https://github.com/TugaInVegas/WinTrace/blob/main/CONTRIBUTING.md) and the
[changelog](https://github.com/TugaInVegas/WinTrace/blob/main/CHANGELOG.md).

License: Apache-2.0
