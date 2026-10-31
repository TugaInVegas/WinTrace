# Integration guide for AI agents

This page is written for AI coding agents (and for people who like checklists). It covers:

1. [Adding WinTrace to a test project](#adding-wintrace-to-a-test-project) when a user asks for it.
2. [Debugging a failed test from its trace](#debugging-a-failed-test-from-its-trace) without a browser.
3. [Writing new tests with real locators](#writing-new-tests-with-real-locators) from the running application.

Ready-made Claude Code skills for 2 and 3 are in
[integrations/claude-code](https://github.com/TugaInVegas/WinTrace/tree/main/integrations); copy them into the
project's `.claude/skills/`.

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

`Finish` only writes files (screenshots are taken during the steps), so it can run anywhere in the teardown,
including after the driver is closed. Put it last, in a `finally`, after a Playwright trace was saved and imported
(see [Playwright](playwright.md)) and after any external screen recorder stopped. The exception is
`TraceSession.StartVideo`: `Finish` stops that recording itself.

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
5. **Drop-in replacements** where wrapping is awkward: `element.ClickTraced()`, `SendKeysTraced(...)`,
   `driver.FindElementTraced(by)` (record on the running test's trace, plain calls otherwise).
6. **Frequent probes and polling waits** (expected to miss sometimes): `Step(..., Capture.None, ...)` or
   `Log("...")`, so a miss does not take screenshots and a UI tree.

Pass values typed into password fields through `SendKeys(element, text, sensitive: true)` or a step whose
target contains "password"; WinTrace then stores `***`.

### 5. Keep these rules

- Do **not** set `Snapshots = SnapshotMode.Always` on WinAppDriver projects unless the user asks: it can add
  tens of seconds per step.
- Do **not** catch or swallow exceptions to record them; `Step` records and rethrows by itself.
- Call `Finish` with the real outcome from the test framework; it drives the passed/failed status of the trace.
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

Every failed test leaves, next to its `*_failed.zip`:

- `*_failed.md`: outcome, failing step, error, UI elements similar to the locator that failed (with suggested
  locators), stack trace, every step and the UI tree at the failure. **Read this first**; no tool needed.
- `*_failed.png` (or `.jpg`): the screenshot at the failure, for agents that can read images.

Each step records the code that issued it (`AuxMethods.cs:120 ← CustomerPage.cs:55 ← CustomerTests.cs:42`), so
you can open the right file and line directly. Environment warnings at the top of the `.md` (locked screen,
non-interactive session) mean the failure is probably not in the test code.

After a full run, start with `wintrace report <folder> --history <archive>`: it groups failures by cause and marks
each one new, recurring or intermittent. Fix one cause, not ten symptoms.

For more, install the CLI once: `dotnet tool install -g WinTrace.Cli`. Every command takes a trace file **or a
folder** (it then uses the newest failed trace under it).

```
wintrace list TestResults --failed         # failed traces after a run, newest first, with the failing step
wintrace summary <trace.zip|folder>        # every step, FAIL marks, exit code 1 if the test failed
wintrace inspect <trace.zip>               # the step where the test failed, in detail
wintrace inspect <trace.zip> --tree        # ... plus the UI tree as indented text
wintrace inspect <trace.zip> --extract out # ... and save its screenshots (add --all for every step)
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
| "The test failed outside the recorded steps" | An assertion or code outside any step failed | Read the failure message; the last recorded step shows the state before it. |
| `Cause: failed in nested step #N` | The failure happened inside a nested step | Read step #N: its stack trace and capture are shown. |
| Error on a step that passed before | App state differs (dialog, focus, data) | Look at the before/after screenshots of the previous steps. |

Prefer `MobileBy.AccessibilityId` locators over XPath: XPath is slow on WinAppDriver.

### Tests that also use Playwright

When the project merges Playwright traces ([how](playwright.md)), web steps appear as `[web]` in the `.md`,
`summary` and `inspect`, with the test line that issued them and Playwright's call log. Browser console errors and
failed requests are on the same timeline, which often explains a later desktop failure. For DOM details, extract
the original from `playwright/` in the zip and open it with `playwright.ps1 show-trace`. To get a Markdown report for
a web-only test: `wintrace merge <playwright trace.zip>`.

## Writing new tests with real locators

Do not guess locators. With the application running under WinAppDriver (or an Appium server with
NovaWindowsDriver), read them from the live UI:

```
wintrace snapshot --window "<window title>"                  # UI tree with a suggested locator per element
wintrace snapshot --window "<window title>" --find "Save"    # elements matching or resembling "Save"
wintrace snapshot --window "<window title>" --out tree.xml --screenshot window.png --json
wintrace snapshot --session <id>                             # the session of a test paused at a breakpoint
wintrace snapshot --url http://127.0.0.1:4723 --automation-name NovaWindows --window "<title>"   # Appium 2
```

`--window` matches the exact title first, then a title containing the text. Sessions created by `snapshot` are
deleted when it finishes; `--session` only reads.

Then write the test with the project's existing base class, page objects and helpers, record each user action as
a step (`Step(...)` or the `...Traced()` extensions), run it, and debug failures from its trace as above.

## Snippet for a project's agent instructions

To make future agents use WinTrace in a project that already has it, add this to the project's
`AGENTS.md` or `CLAUDE.md`:

```markdown
## Desktop UI tests (WinTrace)
- Failed UI tests write `TestResults/WinTrace/*_failed.zip` plus a `.md` report and the failure screenshot.
  Read the `.md` and the screenshot before changing code; `wintrace list TestResults --failed` lists them and
  `wintrace inspect TestResults` (`--tree`, `--extract <dir> --all`, `--json`) shows the newest failure.
- Use the suggested locators for "element not found" errors.
- New locators come from the running app: `wintrace snapshot --window "<title>" [--find <text>]`. Never guess.
- Record new user actions as steps (`Step(...)`, `ClickTraced()`, `FindElementTraced(by)`).
```
