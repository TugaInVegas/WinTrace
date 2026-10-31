# Run report

After a test run, `wintrace report` turns the saved traces into one summary: which tests failed, grouped by cause,
and whether each failure is **new**, **recurring** or **intermittent** compared with earlier runs.

```
wintrace report TestResults/WinTrace --history PreviousRuns --trx TestResults
```

It writes `wintrace-run-report.md` in the folder (`--out` to choose) and prints a short version. `--json` gives the
same data for scripts and AI agents.

## What it shows

- **Failures by cause.** Failed tests are grouped by the step that failed, its target and the error text, with
  numbers and GUIDs ignored, so "timeout 10 s" and "timeout 30 s" count as the same cause. When ten tests fail on
  the same login button, you see one cause with ten tests, not ten separate failures.
- **History** for each failed test, with `--history <dir>` (one subfolder per earlier run, as an archive of
  previous runs usually is):

  | Label | Meaning |
  |---|---|
  | `new` | Did not fail in any earlier run (and passed there, when a TRX says so) |
  | `recurring` | Failed in every earlier run where its outcome is known; the report says whether the cause was the same |
  | `intermittent` | Failed in some earlier runs and passed in others: a sign of a flaky test or environment |

- **TRX totals** with `--trx <file or folder>`: tests passed, failed and not run; tests that **failed without a
  trace** (for example web-only tests, or failures before the trace started); and tests **passing again** after
  failing in the previous run. Earlier runs that contain TRX files are used too, which makes `intermittent` more
  accurate (traces exist only for failures).
- **Environment warnings** from the traces: locked screen, non-interactive session, Remote Desktop, display scaling.
- **Links** to each test's Markdown summary (or trace).

## Azure DevOps

With `--azdo`, or automatically when the `TF_BUILD` variable is set (inside Azure Pipelines), the command also:

- adds the report to the run's **Summary** tab (`##vso[task.uploadsummary]`);
- adds a warning to the run for each **new** failure (`##vso[task.logissue]`).

```yaml
- script: dotnet tool install -g WinTrace.Cli
  displayName: Install wintrace
  condition: always()
- script: wintrace report "$(System.DefaultWorkingDirectory)/TestResults/WinTrace" --history "$(Pipeline.Workspace)/PreviousRuns" --trx "$(Agent.TempDirectory)"
  displayName: WinTrace run report
  condition: always()
```

Adjust the folders to where your tests write traces, TRX files and archived runs.

## Notes

- Tests are matched across runs by the trace title (the test name passed to `TraceSession`). Use the same name in
  every run; with data-driven tests, include the data row name (for example `TestContext.TestDisplayName`).
- When a test has several traces in one run (retries), the newest is used.
- The report only reads traces; it never changes them.
