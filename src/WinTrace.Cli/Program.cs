using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using WinTrace;

const string Usage = @"wintrace - inspect WinTrace traces

  wintrace show <trace.zip> [--out <file.html>] [--no-open]
      Build the HTML report and open it.

  wintrace summary <trace.zip> [--json]
      List steps and errors. Exit code 1 if any step failed.

  wintrace inspect <trace.zip> [--step <n>] [--tree] [--extract <dir>] [--json]
      Details of one step (default: the first failed step, else the last one): error, stack trace,
      parameters, UI elements similar to a failed locator, and the UI tree (--tree).
      --extract saves the step's screenshots as PNG files and prints their paths.

  wintrace --version

Text output is meant for people, CI logs and AI agents; --json is stable for scripts.";

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

int Summary(LoadedTrace t)
{
    var failed = t.Events.Count(e => e.Kind == "error");
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            title = t.Meta.Title,
            framework = t.Meta.Framework,
            started = t.Meta.Started,
            passed = failed == 0,
            failedSteps = failed,
            steps = t.Events.Select(e => new
            {
                e.Seq, e.Kind, e.Name, e.Target, e.Group,
                offsetSeconds = Math.Round(Offset(t, e), 3),
                durationMs = Math.Round(e.DurationMs, 1),
                e.Error
            })
        }, jsonOptions));
        return failed > 0 ? 1 : 0;
    }

    Console.WriteLine($"{t.Meta.Title}  ({t.Events.Count} events, {failed} errors)");
    foreach (var e in t.Events)
    {
        Console.WriteLine($"{(e.Kind == "error" ? "FAIL" : e.Kind == "log" ? "log " : "ok  ")} #{e.Seq,-3} +{Offset(t, e),7:F2}s {e.DurationMs,7:F0}ms  {e.Group ?? "-"} | {e.Name} {e.Target}");
        if (e.Error != null) Console.WriteLine("       " + e.Error);
    }
    if (failed > 0) Console.WriteLine($"\nDetails of the first failure: wintrace inspect \"{args[1]}\"");
    return failed > 0 ? 1 : 0;
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
    else e = t.Events.FirstOrDefault(x => x.Kind == "error") ?? t.Events.Last(x => x.Kind != "log");

    string? Text(string? id) => id != null && t.Resources.TryGetValue(id, out var b) ? System.Text.Encoding.UTF8.GetString(b) : null;
    var snapshot = Text(e.Snapshot);
    var similar = e.Kind == "error" ? TraceAnalysis.FindSimilar(snapshot, e.Target) : Array.Empty<SimilarElement>();
    var elements = TraceAnalysis.ParseTree(snapshot);

    var extracted = new List<string>();
    var extractDir = Opt("--extract");
    if (extractDir != null)
    {
        Directory.CreateDirectory(extractDir);
        foreach (var (label, id) in new[] { ("before", e.ScreenshotBefore), ("after", e.ScreenshotAfter) })
        {
            if (id == null || !t.Resources.TryGetValue(id, out var png)) continue;
            var path = Path.GetFullPath(Path.Combine(extractDir, $"step{e.Seq}-{label}.png"));
            File.WriteAllBytes(path, png);
            extracted.Add(path);
        }
    }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            e.Seq, e.Kind, e.Name, e.Target, e.Group,
            offsetSeconds = Math.Round(Offset(t, e), 3),
            durationMs = Math.Round(e.DurationMs, 1),
            e.Params, e.Error, e.ErrorDetail,
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

    Console.WriteLine($"Step #{e.Seq} {e.Name}{(e.Kind == "error" ? "  [FAILED]" : "")}");
    if (e.Target != null) Console.WriteLine($"Target:   {e.Target}");
    if (e.Group != null) Console.WriteLine($"Group:    {e.Group}");
    Console.WriteLine($"Time:     +{Offset(t, e):F2}s, took {e.DurationMs:F0} ms");
    foreach (var p in e.Params) Console.WriteLine($"Param:    {p.Key} = {p.Value}");
    if (e.Error != null) Console.WriteLine($"Error:    {e.Error}");
    if (e.ErrorDetail != null) Console.WriteLine("\nStack trace:\n" + e.ErrorDetail);

    if (similar.Count > 0)
    {
        Console.WriteLine($"\nUI elements similar to \"{TraceAnalysis.LocatorValue(e.Target)}\" (possible typo or renamed element):");
        foreach (var s in similar)
            Console.WriteLine($"  {s.Score * 100:F0}%  {s.Element.Describe()}  ->  {s.Element.SuggestedLocator()}");
    }
    else if (e.Kind == "error" && elements.Count > 0)
        Console.WriteLine($"\nNo UI element resembles \"{TraceAnalysis.LocatorValue(e.Target)}\". The element may not exist yet (add a wait) or be in another window.");

    Console.WriteLine();
    Console.WriteLine(e.ScreenshotBefore != null || e.ScreenshotAfter != null
        ? extracted.Count > 0 ? "Screenshots:\n  " + string.Join("\n  ", extracted) : "Screenshots: available (use --extract <dir> to save them)."
        : "Screenshots: none for this step.");
    if (snapshot == null) Console.WriteLine("UI tree:     none for this step (captured on failure by default).");
    else if (args.Contains("--tree")) Console.WriteLine("\nUI tree:\n" + TraceAnalysis.CompactTree(snapshot));
    else Console.WriteLine($"UI tree:     {elements.Count} elements (use --tree to print it).");
    return 0;
}
