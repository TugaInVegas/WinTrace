---
name: write-ui-test
description: Write or extend a Windows desktop UI test (WinAppDriver or NovaWindowsDriver, Appium.WebDriver) using real locators from the running application and WinTrace for verification. Use when asked to automate a new desktop flow, add a desktop test, or find locators for desktop UI elements.
---

# Write a desktop UI test with real locators

Never guess locators. Read them from the running application, follow the project's existing patterns (base
class, page objects, helpers, test data), and record the new actions with WinTrace.

## 1. Learn the project's patterns first

- Find the test base class, the page objects for the area you are automating, the shared find/click/wait helpers,
  and how test data is loaded. Reuse them; do not introduce a parallel style.
- Check how WinTrace is wired (`TraceSession`, `TraceRecorder.Current`, helpers that call `Step(...)`).

## 2. Get locators from the live UI

The application must be running with WinAppDriver (default `http://127.0.0.1:4723`) or an Appium server.

```
wintrace snapshot --window "<window title>"                 # UI tree with a suggested locator per element
wintrace snapshot --window "<window title>" --find "Save"   # only elements matching or resembling "Save"
wintrace snapshot --window "<window title>" --out tree.xml --screenshot window.png
```

- NovaWindowsDriver / Appium 2: add `--url <appium url>` and `--automation-name NovaWindows`.
- A test paused at a breakpoint: `wintrace snapshot --session <session id>` reads its session.
- If the app is not running here, ask for a snapshot (`--out tree.xml`) or a failed trace, and work from those.

Prefer, in order: `MobileBy.AccessibilityId` (AutomationId) → `By.Name` (changes with UI language) →
`By.ClassName` within a container. Avoid XPath on WinAppDriver: it serializes the whole UI tree and is slow.
Scope searches to a container element when names repeat.

## 3. Write the test

- Put actions in page objects or existing helpers, each user action as a traced step:
  `TraceRecorder.Current?.Step("save customer", "toolbar/Save", () => ...)`, or the drop-in Appium extensions
  `ClickTraced()`, `SendKeysTraced(...)`, `FindElementTraced(by)`.
- Group phases: `using (TraceRecorder.Current?.Group("Fill in form"))`.
- Use explicit waits for elements that appear after an action; no fixed sleeps.
- Pass sensitive values with `sensitive: true`, or targets that contain "password".
- Frequent probes and polls that may miss: `Step(..., Capture.None, ...)` so misses stay cheap.

## 4. Verify

1. Build.
2. Run the new test. If it fails, follow the `debug-ui-test` skill with its trace.
3. To review a passing run, set `SaveOnlyOnFailure = false` temporarily and check the steps with
   `wintrace summary <folder>`, then revert.
4. If UI tests cannot run here, say so and give the exact command (`dotnet test --filter ...`) to run.
