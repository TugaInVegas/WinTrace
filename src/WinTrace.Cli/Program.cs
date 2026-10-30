using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using WinTrace;
using WinTrace.Cli;

const string Usage = @"wintrace - inspect WinTrace traces and live Windows UI

  wintrace list [<folder>] [--failed] [--limit <n>] [--json]
      Traces under a folder (default: current), newest first, with outcome and failing step.

  wintrace summary <trace.zip|folder> [--json]
      Steps and the test outcome. Exit code 1 if the test failed.

  wintrace inspect <trace.zip|folder> [--step <n>] [--tree] [--extract <dir> [--all]] [--markdown] [--json]
      Details of one step (default: the step where the test failed): error, stack trace, parameters,
      UI elements similar to the locator that failed, and the UI tree (--tree).
      --extract saves the step's screenshots and prints their paths; with --all, those of every step.
      --markdown prints the full Markdown report (as written next to failed traces).

  wintrace show <trace.zip|folder> [--out <file.html>] [--no-open]
      Build the HTML report and open it.

  wintrace snapshot [--url <driver-url>] (--window <title> | --session <id> | --root) [--find <text>]
                    [--out <tree.xml>] [--screenshot <file.png>] [--automation-name <name>] [--json]
      The live UI tree of a window from WinAppDriver or an Appium server (NovaWindowsDriver), with a
      suggested locator per element. --find lists the elements matching or resembling a text.
      Default URL: http://127.0.0.1:4723.

  wintrace --version

A folder argument picks the newest failed trace in it (searching subfolders).
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

var json = args.Contains("--json");
// Console output, never embedded in HTML: keep <, > and quotes readable.
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

if (args[0] == "list") return List();
if (args[0] == "snapshot") return Snapshot();
if (args[0] is not ("show" or "summary" or "inspect")) { Console.Error.WriteLine(Usage); return 2; }
if (args.Length < 2 || args[1].StartsWith("--")) { Console.Error.WriteLine("Give a trace file or a folder.\n\n" + Usage); return 2; }
var zip = TraceFiles.Resolve(args[1], out var picked) ?? "";
if (zip.Length == 0) { Console.Error.WriteLine($"No trace found at '{args[1]}'."); return 2; }
if (picked != null) Console.Error.WriteLine($"Using {picked}");

try
{
    switch (args[0])
    {
        case "show":
            return Show();
        case "summary":
            return Summary(TraceReader.ReadSummary(zip));
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

// summary and inspect never need the videos.
int WithTrace(Func<LoadedTrace, int> body) => body(TraceReader.Read(zip, null));

double Offset(LoadedTrace t, TraceEvent e) => (e.Start - t.Meta.Started).TotalSeconds;

List<TraceEvent> RootErrors(LoadedTrace t) => TraceAnalysis.StepErrors(t.Events).ToList();

bool TestFailed(LoadedTrace t) => TraceAnalysis.IsFailed(t.Meta, t.Events);

int List()
{
    var folder = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : ".";
    var limit = int.TryParse(Opt("--limit"), out var l) ? l : 20;
    var files = TraceFiles.Find(folder).Where(f => !args.Contains("--failed") || TraceFiles.IsFailedName(f.Name)).Take(limit).ToList();
    var rows = new List<(FileInfo file, LoadedTrace? trace, string? error)>();
    foreach (var f in files)
        try { rows.Add((f, TraceReader.ReadSummary(f.FullName), null)); }
        catch (Exception ex) when (ex is InvalidDataException || ex is JsonException || ex is IOException) { rows.Add((f, null, ex.Message)); }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(rows.Select(r =>
        {
            var step = r.trace == null ? null : TraceAnalysis.FailureStep(r.trace.Meta, r.trace.Events, out _);
            var failed = r.trace != null && TraceAnalysis.IsFailed(r.trace.Meta, r.trace.Events);
            return new
            {
                path = r.file.FullName,
                written = r.file.LastWriteTime,
                title = r.trace?.Meta.Title,
                outcome = r.trace == null ? null : failed ? "failed" : "passed",
                steps = r.trace?.Events.Count(e => e.Kind != "log"),
                failingStep = failed && step != null ? new { step.Seq, step.Name, step.Target, step.Error } : null,
                failureMessage = failed ? r.trace?.Meta.FailureMessage : null,
                readError = r.error
            };
        }), jsonOptions));
        return 0;
    }

    if (rows.Count == 0) { Console.WriteLine($"No traces under '{Path.GetFullPath(folder)}'."); return 0; }
    foreach (var (file, trace, error) in rows)
    {
        if (trace == null) { Console.WriteLine($"????  {file.LastWriteTime:yyyy-MM-dd HH:mm}  {file.Name}  (unreadable: {error})"); continue; }
        var failed = TraceAnalysis.IsFailed(trace.Meta, trace.Events);
        var step = TraceAnalysis.FailureStep(trace.Meta, trace.Events, out var outside);
        Console.WriteLine($"{(failed ? "FAIL" : "ok  ")}  {file.LastWriteTime:yyyy-MM-dd HH:mm}  {trace.Meta.Title}");
        if (failed)
            Console.WriteLine("      " + (outside ? "outside steps: " + (trace.Meta.FailureMessage ?? "(no message)")
                : $"#{step?.Seq} {step?.Name} {step?.Target} — {step?.Error}"));
        Console.WriteLine("      " + file.FullName);
    }
    return 0;
}

int Snapshot()
{
    var url = Opt("--url") ?? "http://127.0.0.1:4723";
    var request = new LiveSnapshot.Request
    {
        Url = url,
        Session = Opt("--session"),
        Window = Opt("--window"),
        Root = args.Contains("--root"),
        AutomationName = Opt("--automation-name"),
        WantScreenshot = Opt("--screenshot") != null
    };
    if (request.Session == null && request.Window == null && !request.Root)
    {
        Console.Error.WriteLine("Choose what to read: --window <title>, --session <id> or --root.\n\n" + Usage);
        return 2;
    }
    LiveSnapshot.Result result;
    try { result = LiveSnapshot.TakeAsync(request).GetAwaiter().GetResult(); }
    catch (LiveSnapshotException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (System.Net.Http.HttpRequestException ex) { Console.Error.WriteLine($"Could not reach the driver at {url}: {ex.Message}. Is WinAppDriver or Appium running?"); return 2; }

    if (Opt("--out") is string outXml) File.WriteAllText(outXml, result.Source);
    if (Opt("--screenshot") is string png && result.Screenshot != null) File.WriteAllBytes(png, result.Screenshot);
    var elements = TraceAnalysis.ParseTree(result.Source);
    var find = Opt("--find");
    var matches = find == null ? null : LiveSnapshot.Search(result.Source, find);

    if (json)
    {
        object Item(UiElement e) => new { e.Depth, e.Tag, e.Name, e.AutomationId, e.ClassName, locator = e.SuggestedLocator(), e.X, e.Y, e.Width, e.Height };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            sessionId = result.SessionId,
            elementCount = elements.Count,
            matches = matches?.Select(m => new { score = Math.Round(m.Score, 2), element = Item(m.Element) }),
            elements = find == null ? elements.Select(Item) : null
        }, jsonOptions));
        return 0;
    }

    if (matches != null)
    {
        Console.WriteLine(matches.Count == 0 ? $"No element matches or resembles \"{find}\" ({elements.Count} elements in the tree)."
            : $"Elements matching \"{find}\" ({elements.Count} in the tree):");
        foreach (var m in matches)
            Console.WriteLine($"  {m.Score * 100,4:F0}%  {m.Element.Describe()}  ->  {m.Element.SuggestedLocator() ?? "(no AutomationId/Name/ClassName)"}");
    }
    else
    {
        foreach (var e in elements.Take(600))
        {
            var loc = e.SuggestedLocator();
            Console.WriteLine(new string(' ', e.Depth * 2) + e.Describe() + (loc != null ? "   -> " + loc : ""));
        }
        if (elements.Count > 600) Console.WriteLine($"... {elements.Count - 600} more elements (use --out to save the full tree, or --find).");
        if (elements.Count == 0) Console.WriteLine(result.Source);
    }
    if (Opt("--out") is string o) Console.Error.WriteLine($"UI tree written to {Path.GetFullPath(o)}");
    if (Opt("--screenshot") is string p) Console.Error.WriteLine(result.Screenshot != null ? $"Screenshot written to {Path.GetFullPath(p)}" : "The driver returned no screenshot.");
    return 0;
}

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
    if (args.Contains("--markdown"))
    {
        Console.WriteLine(TraceReport.Markdown(t.Meta, t.Events, id => t.Resources.TryGetValue(id, out var b) ? b : null, zip));
        return 0;
    }

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
