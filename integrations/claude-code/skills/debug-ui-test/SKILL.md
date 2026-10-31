---
name: debug-ui-test
description: Find out why a Windows desktop UI test (WinAppDriver or NovaWindowsDriver) failed, using its WinTrace trace. Use when a desktop UI test failed, when trace files (*_failed.zip / *_failed.md) exist in the project's trace folder, or when asked to fix a failing desktop test.
---

# Debug a failed desktop UI test with WinTrace

Work from the evidence in the trace before changing code. Do not re-run long UI tests just to look at them.

## 1. Find the trace

- Traces of failed tests are `<test>_<yyyyMMdd_HHmmss>_failed.zip`, with a `.md` report and the screenshot at the
  failure (`.png` or `.jpg`) next to them. Default folder: `TestResults/WinTrace` under the test output
  (`bin/<Configuration>/<TargetFramework>/`); the project may configure another folder (look for `TraceSession`).
- List them, newest first: `wintrace list <folder> --failed` (add `--json` for structured output).
- If `wintrace` is missing: `dotnet tool install -g WinTrace.Cli`.

After a full run with several failures, start with the run report: `wintrace report <folder> --history <archive>`.
It groups failures by cause and marks each one new, recurring or intermittent; work on one cause at a time.

## 2. Read the failure

1. Read the `.md` next to the trace: outcome, failing step, error, UI elements similar to the locator that failed,
   the step list and the UI tree at the failure.
2. Look at the failure screenshot (the image file named like the trace).
3. Each step records the code that issued it ("called from" in the `.md`, `Called:` in `inspect`): open that file and line.
4. For more: `wintrace inspect <zip or folder>`; `--tree` prints the UI tree, `--extract <dir> --all` saves every
   step's screenshots, `--step <n>` shows another step, `--json` gives structured output.

## 3. Decide the cause

| Evidence | Likely cause | Fix |
|---|---|---|
| Similar element with a high score (≥ 80%) | Typo or renamed AutomationId/Name | Use the suggested locator. |
| No similar element, UI tree present | Element not there yet, or in another window/dialog | Add an explicit wait for it; attach to the right window. |
| "Test failed outside the recorded steps" | Assertion or code outside any step | Read the failure message; the screenshot shows the state before it. |
| `failed in nested step #N` | The failure happened inside a nested step | Read step #N: its error, stack trace and capture. |
| No screenshots or UI tree | Driver session was gone | Check the previous step and whether the app crashed. |
| Environment warning: lock screen / non-interactive / Remote Desktop | The machine, not the test | Report it; do not change test code for it. |
| `intermittent` in the run report | Flaky test or environment | Compare with the earlier failures' traces before changing waits. |
| Recovered errors in a passed test | Expected misses (probes, optional dialogs) | Usually nothing to fix. |
| `[web]` step failed, or `console.error` / `HTTP 5xx` lines just before the failure | The web part (Playwright) failed or left bad state | Read the web step's call log and the browser lines; for DOM details open the Playwright trace from `playwright/` in the zip with `playwright.ps1 show-trace`. |

Check a locator against the live app when it is running: `wintrace snapshot --window "<window title>" --find "<text>"`.

## 4. Fix and report

- Change the smallest thing that addresses the cause (locator, wait, window switch). Prefer
  `MobileBy.AccessibilityId` locators; XPath is slow on WinAppDriver.
- Do not hide failures: no catch-and-ignore, no longer blanket sleeps, no retries added just to pass.
- In your summary, name the trace you used, the step that failed, the evidence (similar element, screenshot) and
  the change. If the UI tests cannot be run here, say so and give the exact command to verify.
