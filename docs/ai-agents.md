# Integration guide for AI agents

This page is written for AI coding agents (and for people who like checklists). It covers two jobs:

1. [Adding WinTrace to a test project](#adding-wintrace-to-a-test-project) when a user asks for it.
2. [Debugging a failed test from its trace](#debugging-a-failed-test-from-its-trace) without a browser.

## Adding WinTrace to a test project

### 1. Inspect the project

Find, before changing anything:

| Question | Where to look |
|---|---|
| Test framework | `PackageReference` in the test `.csproj`: `MSTest.TestFramework`, `NUnit` or `xunit`. |
| Driver | `Appium.WebDriver` (WinAppDriver, NovaWindowsDriver) → use `WinTrace.Appium`. FlaUI or another tool → write an `ITraceSource` ([adapters](adapters.md)). |
| Where the driver lives | A base test class, a fixture, or a static helper that creates the session. |
| Shared setup/teardown | A base class with `[TestInitialize]`/`[TestCleanup]`, `[SetUp]`/`[TearDown]`, or a constructor/`Dispose`. |
| Central interaction helpers | Methods most tests call to find, click or type (for example `ClickElement(By)`, page objects). |

If the project has no base class and many test classes, ask the user before introducing one.

### 2. Install

```
dotnet add <path/to/Tests.csproj> package WinTrace.Appium
```

Requires `Appium.WebDriver` 4.x (already present in Appium-based projects). Target framework: .NET 8 or later
for `WinTrace.Appium`; `WinTrace.Core` alone is netstandard2.0.

### 3. Create and finish one session per test

Put this in the shared setup/teardown. `() => Driver` must return the project's current driver instance;
it is read lazily, so it can be created later in setup or replaced during the test.

**MSTest**

```csharp
using WinTrace;
using WinTrace.Appium;

protected TraceSession Trace = null!;

[TestInitialize]
public void StartTrace() =>
    Trace = new TraceSession(TestContext.TestName!, new AppiumTraceSource(() => Driver));

[TestCleanup]
public void FinishTrace() =>
    Trace.Finish(TestContext.CurrentTestOutcome != UnitTestOutcome.Passed, TestContext.AddResultFile);
```

**NUnit**

```csharp
[SetUp]
public void StartTrace() =>
    Trace = new TraceSession(TestContext.CurrentContext.Test.Name, new AppiumTraceSource(() => Driver));

[TearDown]
public void FinishTrace() =>
    Trace.Finish(TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed,
                 path => TestContext.AddTestAttachment(path));
```

**xUnit** has no outcome API: set a `_passed = true` flag as the last line of each test and call
`Trace.Finish(!_passed)` in `Dispose()`. See [getting started](getting-started.md#xunit).

If the project already has a teardown that closes the driver, call `Finish` **before** closing it, so the
last screenshot can still be taken.

### 4. Record steps

Nothing is recorded until code calls the recorder. Prefer few, high-value edits:

1. **Central helpers first.** If tests go through helpers such as `Click(By by)`, change only the helper:

   ```csharp
   public void Click(By by) =>
       Trace.Recorder.Step("click", by.ToString(), () => Driver.FindElement(by).Click());
   ```

2. **Page-object methods** that represent a user action: wrap the body in `Trace.Recorder.Step(...)`.
3. **Direct calls** in tests: `Trace.Recorder.Click(Trace.Recorder.Find(Driver, by))`.
4. Use `using (Trace.Recorder.Group("Login")) { ... }` around logical phases.

Pass values typed into password fields through `SendKeys(element, text, sensitive: true)` or a step whose
target contains "password"; WinTrace then stores `***`.

### 5. Keep these rules

- Do **not** set `Snapshots = SnapshotMode.Always` on WinAppDriver projects unless the user asks: it can add
  tens of seconds per step.
- Do **not** catch or swallow exceptions to record them; `Step` records and rethrows by itself.
- Do **not** change test outcomes, retries or timeouts as part of the integration.
- Add the output folder (`TestResults/`) to `.gitignore` if it is not ignored already.
- Keep the integration in one commit that the user can review: package reference, setup/teardown, wrapped helpers.

### 6. Verify

1. `dotnet build` the test project.
2. Run one test that fails, or temporarily set `SaveOnlyOnFailure = false` on the session and run one test.
3. Check that `TestResults/WinTrace/*.zip` and `.html` exist next to the test binaries (usually under
   `bin/<Configuration>/<TargetFramework>/`).
4. `wintrace summary <zip>` should list the steps you wrapped. Revert any temporary `SaveOnlyOnFailure` change.

Running UI tests needs a Windows machine with an interactive desktop and the driver running. If you cannot
run them, say so and leave verification steps for the user.

## Debugging a failed test from its trace

Install the CLI once: `dotnet tool install -g WinTrace.Cli`.

```
wintrace summary <trace.zip>               # every step, FAIL marks, exit code 1 if any failed
wintrace inspect <trace.zip>               # the first failed step in detail
wintrace inspect <trace.zip> --tree        # ... plus the UI tree as indented text
wintrace inspect <trace.zip> --extract out # ... and save before/after screenshots as PNG
wintrace inspect <trace.zip> --step 4      # any other step
```

Add `--json` to `summary` or `inspect` for structured output (fields: `seq`, `kind`, `name`, `target`,
`group`, `durationMs`, `error`, `errorDetail`, `params`, `similarElements[]` with `score`, `element`,
`locator`, and `uiTree` with `--tree`).

How to read the result:

| What `inspect` shows | Likely cause | Typical fix |
|---|---|---|
| `similarElements` with a high score (≥ 80%) | Typo or renamed AutomationId/Name | Use the suggested locator. |
| No similar elements, but a UI tree | Element not there yet, or in another window | Add an explicit wait; attach to the right window. |
| No UI tree and no screenshots | Driver session was gone | Look at the step before; check the app did not crash. |
| Error on a step that passed before | App state differs (dialog, focus, data) | Look at the before/after screenshots of the previous steps. |

Prefer `MobileBy.AccessibilityId` locators over XPath: XPath is slow on WinAppDriver.

## Snippet for a project's agent instructions

To make future agents use WinTrace in a project that already has it, add this to the project's
`AGENTS.md` or `CLAUDE.md`:

```markdown
## UI test traces (WinTrace)
- Failed UI tests write traces to `TestResults/WinTrace/*_failed.zip`.
- To debug a failure, run `wintrace inspect <zip>` (add `--tree`, `--extract <dir>` or `--json`) before
  changing code. Use its suggested locators for "element not found" errors.
- Record new user actions with `Trace.Recorder.Step(...)` or the project's traced helpers.
```
