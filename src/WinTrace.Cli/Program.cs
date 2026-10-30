using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using WinTrace;

const string Usage = @"wintrace - inspect WinTrace traces

  wintrace show <trace.zip> [--out <file.html>] [--no-open]
      Build the HTML report and open it.

  wintrace summary <trace.zip> [--json]
      List steps and the test outcome. Exit code 1 if the test failed.

  wintrace inspect <trace.zip> [--step <n>] [--tree] [--extract <dir> [--all]] [--json]
      Details of one step (default: the step where the test failed): error, stack trace, parameters,
      UI elements similar to the locator that failed, and the UI tree (--tree).
      --extract saves the step's screenshots and prints their paths; with --all, those of every step.

  wintrace --version

Text output is meant for people, CI logs and AI agents; --json is stable for scripts.";

// Dots as decimal separators whatever the OS language, so output is easy to parse.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
if (args[0] == "--version")
{
    var info = typeof(TraceRecorder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
    Console.WriteLine(info.Split('+')[0]);
    return 0;
}

if (args.Length < 2 || !File.Exists(args[1])) { Console.Error.WriteLine("Trace file not found.\n\n" + Usage); return 2; }
var zip = Path.GetFullPath(args[1]);
var json = args.Contains("--json");
// Console output, never embedded in HTML: keep <, > and quotes readable.
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

try
{
    switch (args[0])
    {
        case "show":
            return Show();
        case "summary":
            return WithTrace(Summary);
        case "inspect":
            return WithTrace(Inspect);
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidDataException || ex is JsonException || ex is IOException)
{
    Console.Error.WriteLine($"Could not read '{zip}' as a WinTrace trace: {ex.Message}");
    return 2;
}

int Show()
{
    var outHtml = Path.GetFullPath(Opt("--out") ?? Path.ChangeExtension(zip, ".html"));
    var stem = Path.GetFileNameWithoutExtension(outHtml);
    var filesDir = Path.Combine(Path.GetDirectoryName(outHtml)!, stem + "_files");
    var t = TraceReader.Read(zip, filesDir);
    TraceHtmlExporter.Write(outHtml, t.Meta, t.Events, id => t.Resources.TryGetValue(id, out var b) ? b : null, t.VideoFiles);
    Console.WriteLine(outHtml);
    if (!args.Contains("--no-open"))
        try { Process.Start(new ProcessStartInfo(outHtml) { UseShellExecute = true }); }
        catch (Exception ex) { Console.Error.WriteLine("Could not open browser: " + ex.Message); }
    return 0;
}

// summary and inspect only read events and resources; extracted videos go to a temp folder that is removed.
int WithTrace(Func<LoadedTrace, int> body)
{
    var tmp = Path.Combine(Path.GetTempPath(), "wintrace-" + Guid.NewGuid().ToString("N"));
    try { return body(TraceReader.Read(zip, tmp)); }
    finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
}

double Offset(LoadedTrace t, TraceEvent e) => (e.Start - t.Meta.Started).TotalSeconds;

// Step errors that are not just the same failure propagating out of a nested step.
List<TraceEvent> RootErrors(LoadedTrace t) => t.Events.Where(e => e.Kind == "error" && e.CauseSeq == null).ToList();

// The outcome reported by the test framework; traces without one fall back to "any step failed".
bool TestFailed(LoadedTrace t) => t.Meta.Outcome != null ? t.Meta.Outcome == "failed" : RootErrors(t).Count > 0;

int Summary(LoadedTrace t)
{
    var failed = TestFailed(t);
    var errors = RootErrors(t);
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            title = t.Meta.Title,
            framework = t.Meta.Framework,
            started = t.Meta.Started,
            outcome = failed ? "failed" : "passed",
            outcomeReported = t.Meta.Outcome != null,
            failureMessage = t.Meta.FailureMessage,
            passed = !failed,
            failedSteps = failed ? errors.Count : 0,
            recoveredErrors = failed ? 0 : errors.Count,
            steps = t.Events.Select(e => new
            {
                e.Seq, e.Kind, e.Name, e.Target, e.Group, e.CauseSeq,
                offsetSeconds = Math.Round(Offset(t, e), 3),
                durationMs = Math.Round(e.DurationMs, 1),
                captureMs = e.CaptureMs is double c ? Math.Round(c, 1) : (double?)null,
                snapshotMs = e.SnapshotMs is double sn ? Math.Round(sn, 1) : (double?)null,
                e.Error
            })
        }, jsonOptions));
        return failed ? 1 : 0;
    }

    var verdict = failed ? "FAILED" : errors.Count > 0 ? $"PASSED ({errors.Count} recovered step error(s))" : "PASSED";
    Console.WriteLine($"{t.Meta.Title}  {verdict}  ({t.Events.Count} events)");
    if (failed && t.Meta.FailureMessage != null) Console.WriteLine("Failure: " + t.Meta.FailureMessage);
    foreach (var e in t.Events)
    {
        var status = e.Kind == "log" ? "log " : e.Kind != "error" ? "ok  " : failed ? "FAIL" : "rcvd";
        var capture = e.CaptureMs is double c && c >= 1000 ? $"  (capture {c / 1000:F1}s)" : "";
        Console.WriteLine($"{status} #{e.Seq,-3} +{Offset(t, e),7:F2}s {e.DurationMs,7:F0}ms  {e.Group ?? "-"} | {e.Name} {e.Target}{capture}");
        if (e.Error != null) Console.WriteLine("       " + (e.CauseSeq is int cause ? $"(failed in #{cause}) " : "") + e.Error);
    }
    if (failed) Console.WriteLine($"\nDetails of the failure: wintrace inspect \"{args[1]}\"");
    return failed ? 1 : 0;
}

int Inspect(LoadedTrace t)
{
    if (t.Events.Count == 0) { Console.Error.WriteLine("The trace has no steps."); return 2; }
    TraceEvent? e;
    var stepArg = Opt("--step");
    if (stepArg != null)
    {
        if (!int.TryParse(stepArg, out var n) || (e = t.Events.FirstOrDefault(x => x.Seq == n)) == null)
        {
            Console.Error.WriteLine($"No step #{stepArg}. Steps go from #1 to #{t.Events.Max(x => x.Seq)}.");
            return 2;
        }
    }
    else
    {
        // The step where the test failed: the last step error (nested failures report their innermost step);
        // a passed test shows its first recovered error, else the last step.
        var errors = RootErrors(t);
        e = TestFailed(t) ? errors.LastOrDefault() : errors.FirstOrDefault();
        e ??= t.Events.LastOrDefault(x => x.Kind != "log") ?? t.Events.Last();
    }

    var failedOutsideSteps = TestFailed(t) && RootErrors(t).Count == 0;
    // A step that failed because a nested step failed shares that step's capture.
    var captured = e.CauseSeq is int causeSeq ? t.Events.FirstOrDefault(x => x.Seq == causeSeq) ?? e : e;
    string? Text(string? id) => id != null && t.Resources.TryGetValue(id, out var b) ? System.Text.Encoding.UTF8.GetString(b) : null;
    var snapshot = Text(captured.Snapshot);
    var locator = e.Kind == "error" ? TraceAnalysis.GuessLocator(captured, t.Events) : null;
    var similar = locator != null ? TraceAnalysis.FindSimilar(snapshot, locator) : Array.Empty<SimilarElement>();
    var elements = TraceAnalysis.ParseTree(snapshot);

    var extracted = new List<string>();
    var extractDir = Opt("--extract");
    if (extractDir != null)
    {
        Directory.CreateDirectory(extractDir);
        foreach (var step in args.Contains("--all") ? t.Events : (IEnumerable<TraceEvent>)new[] { e })
            foreach (var (label, id) in new[] { (step.Kind == "log" ? "screenshot" : "before", step.ScreenshotBefore ?? (step.Kind == "log" ? step.ScreenshotAfter : null)), ("after", step.Kind == "log" ? null : step.ScreenshotAfter) })
            {
                if (id == null || !t.Resources.TryGetValue(id, out var image)) continue;
                var path = Path.GetFullPath(Path.Combine(extractDir, $"step{step.Seq}-{label}{Path.GetExtension(id)}"));
                File.WriteAllBytes(path, image);
                extracted.Add(path);
            }
    }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            e.Seq, e.Kind, e.Name, e.Target, e.Group, e.CauseSeq,
            testOutcome = TestFailed(t) ? "failed" : "passed",
            failedOutsideSteps,
            failureMessage = t.Meta.FailureMessage,
            offsetSeconds = Math.Round(Offset(t, e), 3),
            durationMs = Math.Round(e.DurationMs, 1),
            captureMs = e.CaptureMs is double c ? Math.Round(c, 1) : (double?)null,
            snapshotMs = captured.SnapshotMs is double sn ? Math.Round(sn, 1) : (double?)null,
            e.Params, e.Error, errorDetail = captured.ErrorDetail,
            locator,
            hasScreenshotBefore = e.ScreenshotBefore != null,
            hasScreenshotAfter = e.ScreenshotAfter != null,
            extractedScreenshots = extracted,
            similarElements = similar.Select(s => new
            {
                score = Math.Round(s.Score, 2), element = s.Element.Describe(), locator = s.Element.SuggestedLocator()
            }),
            uiTree = args.Contains("--tree") ? TraceAnalysis.CompactTree(snapshot) : null,
            uiTreeElements = elements.Count
        }, jsonOptions));
        return 0;
    }

    if (failedOutsideSteps)
        Console.WriteLine("The test failed outside the recorded steps" + (t.Meta.FailureMessage != null ? ": " + t.Meta.FailureMessage : ".")
            + "\nShowing the last recorded step.\n");
    var verdictLabel = e.Kind != "error" ? "" : TestFailed(t) ? "  [FAILED]" : "  [RECOVERED]";
    Console.WriteLine($"Step #{e.Seq} {e.Name}{verdictLabel}");
    if (e.Target != null) Console.WriteLine($"Target:   {e.Target}");
    if (e.Group != null) Console.WriteLine($"Group:    {e.Group}");
    Console.WriteLine($"Time:     +{Offset(t, e):F2}s, took {e.DurationMs:F0} ms"
        + (e.CaptureMs is double cap ? $" (+{cap:F0} ms capture{(e.SnapshotMs is double snap ? $", UI tree {snap:F0} ms" : "")})" : ""));
    foreach (var p in e.Params) Console.WriteLine($"Param:    {p.Key} = {p.Value}");
    if (e.Error != null) Console.WriteLine($"Error:    {e.Error}");
    if (e.CauseSeq != null) Console.WriteLine($"Cause:    failed in nested step #{e.CauseSeq} (details and capture below are from that step)");
    if (captured.ErrorDetail != null) Console.WriteLine("\nStack trace:\n" + captured.ErrorDetail);

    if (similar.Count > 0)
    {
        Console.WriteLine($"\nUI elements similar to \"{TraceAnalysis.LocatorValue(locator)}\" (possible typo or renamed element):");
        foreach (var s in similar)
            Console.WriteLine($"  {s.Score * 100:F0}%  {s.Element.Describe()}  ->  {s.Element.SuggestedLocator()}");
    }
    else if (e.Kind == "error" && elements.Count > 0)
        Console.WriteLine(locator == null
            ? "\nNo locator was recorded for this step. Pass the locator as the step target, e.g. Step(\"click\", by.ToString(), ...), to get suggestions."
            : $"\nNo UI element resembles \"{TraceAnalysis.LocatorValue(locator)}\". The element may not exist yet (add a wait) or be in another window.");

    Console.WriteLine();
    Console.WriteLine(e.ScreenshotBefore != null || e.ScreenshotAfter != null
        ? extracted.Count > 0 ? "Screenshots:\n  " + string.Join("\n  ", extracted) : "Screenshots: available (use --extract <dir> to save them)."
        : "Screenshots: none for this step.");
    if (snapshot == null) Console.WriteLine("UI tree:     none for this step (captured on failure by default).");
    else if (args.Contains("--tree")) Console.WriteLine("\nUI tree:\n" + TraceAnalysis.CompactTree(snapshot));
    else Console.WriteLine($"UI tree:     {elements.Count} elements (use --tree to print it).");
    return 0;
}
