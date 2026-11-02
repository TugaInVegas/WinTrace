using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using WinTrace;
using WinTrace.Cli;

const string Usage = @"wintrace - inspect WinTrace traces and live Windows UI

  wintrace tail [<folder>] [--follow]
      The test running now (from its live trace): current group and step, time in the step, errors so far,
      last steps. --follow refreshes every 2 s until the test finishes.

  wintrace recover [<folder>] [--keep]
      Turn live traces left by tests that never finished (process killed, timeout, run stopped) into
      *_aborted traces (zip, HTML, .md).

  wintrace list [<folder>] [--failed] [--limit <n>] [--json]
      Traces under a folder (default: current), newest first, with outcome and failing step.

  wintrace report [<folder>] [--history <dir>] [--trx <file|dir>] [--out <file.md>] [--azdo] [--json]
      Run summary: failures grouped by cause, each marked new / recurring / intermittent against earlier
      runs (one subfolder per run under --history), TRX totals and tests that failed without a trace.
      Writes wintrace-run-report.md in the folder; --azdo also publishes it to the Azure DevOps run summary
      (automatic when TF_BUILD is set).

  wintrace summary <trace.zip|folder> [--json]
      Steps and the test outcome. Exit code 1 if the test failed.

  wintrace inspect <trace.zip|folder> [--step <n>] [--tree] [--extract <dir> [--all]] [--markdown] [--json]
      Details of one step (default: the step where the test failed): error, stack trace, parameters,
      UI elements similar to the locator that failed, and the UI tree (--tree).
      --extract saves the step's screenshots and prints their paths; with --all, those of every step.
      --markdown prints the full Markdown report (as written next to failed traces).

  wintrace show <trace.zip|folder> [--out <file.html>] [--no-open]
      Build the HTML report and open it.

  wintrace merge <trace.zip|playwright.zip> [<playwright.zip>...] [--out <file.zip>] [--title <title>]
      Combine a WinTrace trace with Playwright traces (one timeline, originals kept for Playwright's viewer),
      or turn Playwright traces alone into a WinTrace trace. Writes the zip, the HTML report and the .md.

  wintrace snapshot [--url <driver-url>] (--window <title> | --session <id> | --root) [--find <text>]
                    [--verify <locator>]... [--out <tree.xml>] [--screenshot <file.png>] [--automation-name <name>] [--json]
      The live UI tree of a window from WinAppDriver or an Appium server (NovaWindowsDriver), with a
      suggested locator per element. --find lists the elements matching or resembling a text.
      --verify measures a locator in that window: how many elements match and how long the lookup takes,
      native and as the equivalent XPath (accessibilityid=<id>, name=<name>, classname=<class>,
      xpath=<xpath>, or a recorded target like ""By.Name: Save""). Repeatable; skips reading the tree unless
      --find, --out or --tree is given. Default URL: http://127.0.0.1:4723.

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
if (args[0] == "merge") return Merge();
if (args[0] == "report") return Report();
if (args[0] == "recover") return Recover();
if (args[0] == "tail") return Tail();
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
                : $"#{step?.Seq} {(step?.Origin == "web" ? "[web] " : "")}{step?.Name} {step?.Target} — {step?.Error}"));
        Console.WriteLine("      " + file.FullName);
    }
    return 0;
}

int Report()
{
    var folder = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : ".");
    if (!Directory.Exists(folder)) { Console.Error.WriteLine($"Folder not found: {folder}"); return 2; }
    var historyDir = Opt("--history") is string h ? Path.GetFullPath(h) : null;
    bool Under(string path, string? dir) => dir != null && Path.GetFullPath(path).StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    var traces = TraceFiles.Find(folder).Where(f => !Under(f.FullName, historyDir)).Select(f => f.FullName).ToList();
    var history = new List<HistoricRun>();
    if (historyDir != null)
    {
        // A first run or a clean agent has no archive yet: report without history instead of failing.
        if (!Directory.Exists(historyDir)) Console.Error.WriteLine($"No earlier runs: history folder not found ({historyDir}).");
        // One subfolder per earlier run, newest first.
        else foreach (var run in new DirectoryInfo(historyDir).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc))
        {
            var hasTrx = Directory.EnumerateFiles(run.FullName, "*.trx", SearchOption.AllDirectories).Any();
            history.Add(RunReport.ReadHistoricRun(run.Name, TraceFiles.Find(run.FullName).Select(f => f.FullName), hasTrx ? TrxRun.Read(run.FullName) : null));
        }
    }
    var trx = Opt("--trx") is string trxPath ? TrxRun.Read(trxPath) : null;
    var report = RunReport.Build(folder, traces, history, trx);

    var output = Path.GetFullPath(Opt("--out") ?? Path.Combine(folder, "wintrace-run-report.md"));
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, report.ToMarkdown(Path.GetDirectoryName(output)));

    var azdo = args.Contains("--azdo") || string.Equals(Environment.GetEnvironmentVariable("TF_BUILD"), "true", StringComparison.OrdinalIgnoreCase);
    if (azdo)
    {
        // Azure Pipelines logging commands: the report becomes a section of the run's Summary tab.
        Console.WriteLine($"##vso[task.uploadsummary]{output}");
        foreach (var e in report.Entries.Where(e => e.Failed && e.History == "new"))
            Console.WriteLine("##vso[task.logissue type=warning]" + AzdoEscape($"New failure: {e.Title}: {e.FailingStep ?? "outside steps"} — {e.Error}"));
    }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            report = output,
            traces = report.Entries.Count,
            failed = report.Entries.Count(e => e.Failed),
            historyRuns = report.History.Count,
            trx = report.Trx == null ? null : new { total = report.Trx.Total, passed = report.Trx.Passed, failed = report.Trx.Failed, notRun = report.Trx.NotRun },
            clusters = report.Clusters.Select(c => new
            {
                c.Description,
                tests = c.Tests.Select(e => new { e.Title, e.History, e.HistoryDetail, e.FailingStep, e.Error, e.Location, trace = e.TracePath, markdown = e.MarkdownPath })
            }),
            failedWithoutTrace = report.FailedWithoutTrace,
            passingAgain = report.Fixed,
            environmentWarnings = report.Entries.SelectMany(e => e.EnvironmentWarnings).Distinct()
        }, jsonOptions));
        return 0;
    }

    var failed = report.Entries.Where(e => e.Failed).ToList();
    Console.WriteLine($"{failed.Count} failed test(s) with a trace, {report.Clusters.Count} distinct cause(s)" +
        (report.History.Count > 0 ? $"; new {failed.Count(e => e.History == "new")}, recurring {failed.Count(e => e.History == "recurring")}, intermittent {failed.Count(e => e.History == "intermittent")}" : ""));
    foreach (var c in report.Clusters)
    {
        Console.WriteLine($"  {c.Tests.Count}x  {c.Description}");
        foreach (var e in c.Tests) Console.WriteLine($"        {e.Title}  [{e.History}]");
    }
    if (report.FailedWithoutTrace.Count > 0) Console.WriteLine($"  Failed without a trace: {string.Join(", ", report.FailedWithoutTrace)}");
    Console.WriteLine(output);
    return 0;
}

int Recover()
{
    var folder = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : ".");
    var live = TraceRecovery.FindLiveFolders(folder);
    if (live.Count == 0) { Console.WriteLine($"No live traces under {folder}."); return 0; }
    foreach (var dir in live)
    {
        // <output>/.live/<test>_<time> -> write next to the other traces in <output>.
        var output = Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!;
        try
        {
            foreach (var f in TraceRecovery.Recover(dir, output, args.Contains("--keep"))) Console.WriteLine(f);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidDataException)
        {
            Console.Error.WriteLine($"Could not recover {dir}: {ex.Message}");
        }
    }
    return 0;
}

int Tail()
{
    var folder = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : ".");
    var follow = args.Contains("--follow");
    string? watching = null;
    while (true)
    {
        var live = TraceRecovery.FindLiveFolders(folder);
        var dir = watching != null && live.Contains(watching) ? watching : live.FirstOrDefault();
        if (dir == null)
        {
            Console.WriteLine(watching != null ? "The test finished (its live trace is gone). See wintrace list." : $"No test running under {folder} (no live traces).");
            return 0;
        }
        watching = dir;
        var st = TraceRecovery.Read(dir);
        var now = DateTimeOffset.UtcNow;
        string Ago(DateTimeOffset t) { var d = now - t; return d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds:00} s" : $"{d.TotalSeconds:F0} s"; }
        if (follow && !Console.IsOutputRedirected) try { Console.Clear(); } catch (IOException) { }
        Console.WriteLine($"{st.Meta.Title}   running for {Ago(st.Meta.Started)}   {st.Events.Count(e => e.Kind != "log")} steps done");
        foreach (var kv in st.Meta.Context) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        if (st.Current != null)
            Console.WriteLine($"NOW  {(st.Current.Group != null ? st.Current.Group + " | " : "")}{st.Current.Name} {st.Current.Target}   for {Ago(st.Current.Start)}");
        else
            Console.WriteLine($"Between steps; last activity {Ago(new DateTimeOffset(st.LastWriteUtc, TimeSpan.Zero))} ago");
        var errors = st.Events.Where(e => e.Kind == "error").ToList();
        if (errors.Count > 0) Console.WriteLine($"Errors so far: {errors.Count}; last: #{errors[^1].Seq} {errors[^1].Name} {errors[^1].Target} — {errors[^1].Error}");
        foreach (var w in TraceAnalysis.EnvironmentWarnings(st.Meta)) Console.WriteLine("Environment: " + w);
        Console.WriteLine("Last steps:");
        foreach (var e in st.Events.Skip(Math.Max(0, st.Events.Count - 8)))
            Console.WriteLine($"  {(e.Kind == "error" ? "FAIL" : e.Kind == "log" ? "log " : "ok  ")} #{e.Seq,-4} {e.DurationMs,7:F0}ms  {e.Group ?? "-"} | {(e.Origin == "web" ? "[web] " : "")}{e.Name} {e.Target}");
        if (!follow) return 0;
        Thread.Sleep(2000);
    }
}

// Azure Pipelines logging-command escaping for message text.
static string AzdoEscape(string s) => s.Replace("%", "%AZP25").Replace("\r", "%0D").Replace("\n", "%0A");

int Merge()
{
    var valueOptions = new[] { "--out", "--title" };
    var inputs = args.Skip(1).Where((a, i) => !a.StartsWith("--") && !valueOptions.Contains(args[i])).ToList();
    if (inputs.Count == 0) { Console.Error.WriteLine("Give a WinTrace trace and/or Playwright traces.\n\n" + Usage); return 2; }
    foreach (var i in inputs) if (!File.Exists(i)) { Console.Error.WriteLine($"File not found: {i}"); return 2; }

    bool IsWinTrace(string path)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(path);
        return z.GetEntry("events.ndjson") != null;
    }

    var tmp = Path.Combine(Path.GetTempPath(), "wintrace-merge-" + Guid.NewGuid().ToString("N"));
    try
    {
        LoadedTrace t;
        List<string> playwright;
        if (IsWinTrace(inputs[0]))
        {
            t = TraceReader.Read(inputs[0], tmp);
            playwright = inputs.Skip(1).ToList();
        }
        else
        {
            t = new LoadedTrace { Meta = new TraceMetadata { Title = Opt("--title") ?? Path.GetFileNameWithoutExtension(inputs[0]), Framework = "Playwright" } };
            playwright = inputs;
        }
        if (Opt("--title") is string title) t.Meta.Title = title;
        if (playwright.Count == 0) { Console.Error.WriteLine("Give at least one Playwright trace to merge."); return 2; }
        foreach (var p in playwright)
        {
            if (IsWinTrace(p)) { Console.Error.WriteLine($"{p} is a WinTrace trace; only the first argument can be one."); return 2; }
            var r = PlaywrightTrace.Merge(t.Meta, t.Events, t.Resources, p, new WinTrace.TraceOptions().RedactParams);
            Console.Error.WriteLine($"Merged {Path.GetFileName(p)}: {r.Actions} web actions" + (r.PlaywrightVersion != null ? $" (Playwright {r.PlaywrightVersion})" : ""));
        }
        if (t.Meta.Started == default) t.Meta.Started = DateTimeOffset.UtcNow;
        t.Meta.Stopped ??= t.Events.Count == 0 ? t.Meta.Started : t.Events.Max(e => e.Start.AddMilliseconds(e.DurationMs));

        var output = Path.GetFullPath(Opt("--out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputs[0]))!,
            Path.GetFileNameWithoutExtension(inputs[0]) + "_merged.zip"));
        var stem = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output));
        TraceArchive.Write(output, t.Meta, t.Events, t.Resources, t.VideoFiles);
        byte[]? Res(string id) => t.Resources.TryGetValue(id, out var b) ? b : null;
        var html = TraceHtmlExporter.Export(stem + ".html", t.Meta, t.Events, Res, t.VideoFiles, true, true)[0];
        File.WriteAllText(stem + ".md", TraceReport.Markdown(t.Meta, t.Events, Res, Path.GetFileName(output), Path.GetFileName(html)));
        Console.WriteLine(output);
        Console.WriteLine(html);
        Console.WriteLine(stem + ".md");
        return 0;
    }
    finally
    {
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
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
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] != "--verify") continue;
        var loc = LiveSnapshot.Locator.Parse(args[i + 1]);
        if (loc == null)
        {
            Console.Error.WriteLine($"Cannot read the locator \"{args[i + 1]}\". Use accessibilityid=<id>, name=<name>, classname=<class>, xpath=<xpath> or By.Name: <name>.");
            return 2;
        }
        request.Verify.Add(loc);
    }
    request.SkipSource = request.Verify.Count > 0 && Opt("--find") == null && Opt("--out") == null && !args.Contains("--tree");
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
            verify = result.Verifications.Count == 0 ? null : result.Verifications.Select(v => new
            {
                locator = v.Locator.ToString(),
                count = v.Native.Count, ms = v.Native.Ms, error = v.Native.Error,
                xpath = v.XPath == null ? null : new { value = v.XPath.Value, count = v.XPath.Count, ms = v.XPath.Ms, error = v.XPath.Error },
                verdict = v.Verdict
            }),
            elements = find == null && !request.SkipSource ? elements.Select(Item) : null
        }, jsonOptions));
        return 0;
    }

    foreach (var v in result.Verifications)
    {
        string Count(LiveSnapshot.Measurement m) => m.Count < 0 ? "error" : m.Count == 1 ? "1 match" : $"{m.Count} matches";
        Console.WriteLine($"{v.Locator.Strategy} = \"{v.Locator.Value}\": {Count(v.Native)}, {v.Native.Ms:F0} ms");
        if (v.XPath != null) Console.WriteLine($"  as XPath {v.XPath.Value}: {Count(v.XPath)}, {v.XPath.Ms:F0} ms");
        Console.WriteLine("  -> " + v.Verdict);
    }
    if (result.Verifications.Count > 0 && (matches != null || !request.SkipSource)) Console.WriteLine();
    if (request.SkipSource) { /* only verifying */ }
    else if (matches != null)
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
    var misses = t.Events.Where(e => TraceAnalysis.IsRecoveredMiss(e, t.Meta, t.Events)).Select(e => e.Seq).ToHashSet();
    var errors = RootErrors(t).Where(e => !misses.Contains(e.Seq)).ToList();
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            title = t.Meta.Title,
            framework = t.Meta.Framework,
            started = t.Meta.Started,
            outcome = t.Meta.Outcome == "aborted" ? "aborted" : failed ? "failed" : "passed",
            context = t.Meta.Context,
            missedProbes = misses.Count,
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
                e.Error,
                minor = e.Minor == true ? true : (bool?)null,
                missedProbe = misses.Contains(e.Seq) ? true : (bool?)null,
                e.Diagnostics
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
        if (e.Diagnostics != null && e.Diagnostics.TryGetValue("appResponding", out var resp) && resp == "no") capture += "  [app not responding]";
        if (e.Kind == "error" && TraceAnalysis.IsRecoveredMiss(e, t.Meta, t.Events)) { status = "miss"; capture += "  [probe missed, recovered]"; }
        Console.WriteLine($"{status} #{e.Seq,-3} +{Offset(t, e),7:F2}s {e.DurationMs,7:F0}ms  {e.Group ?? "-"} | {(e.Origin == "web" ? "[web] " : "")}{e.Name} {e.Target}{capture}");
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
        // Probes that missed and were recovered (minor steps) are skipped.
        e = TraceAnalysis.FailureStep(t.Meta, t.Events, out _) ?? t.Events.Last();
    }

    TraceAnalysis.FailureStep(t.Meta, t.Events, out var failedOutsideSteps);
    // A step that failed because a nested step failed shares that step's capture.
    var captured = TraceAnalysis.CaptureFor(e, t.Events);
    var detailFrom = e.CauseSeq is int causeSeq ? t.Events.FirstOrDefault(x => x.Seq == causeSeq) ?? e : e;
    if (args.Contains("--markdown"))
    {
        Console.WriteLine(TraceReport.Markdown(t.Meta, t.Events, id => t.Resources.TryGetValue(id, out var b) ? b : null, zip));
        return 0;
    }

    string? Text(string? id) => id != null && t.Resources.TryGetValue(id, out var b) ? System.Text.Encoding.UTF8.GetString(b) : null;
    var snapshot = Text(captured.Snapshot);
    var locator = e.Kind == "error" ? TraceAnalysis.GuessLocator(detailFrom, t.Events) : null;
    var similar = locator != null ? TraceAnalysis.FindSimilar(snapshot, locator) : Array.Empty<SimilarElement>();
    var elements = TraceAnalysis.ParseTree(snapshot);
    var otherWindows = elements.Where(x => x.Tag == "ProcessWindow").ToList();
    var previousStep = t.Events.Where(x => x.Seq < e.Seq && x.Kind != "log").OrderByDescending(x => x.Seq).FirstOrDefault();

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
            e.Params, e.Error, errorDetail = detailFrom.ErrorDetail, e.Location, capturedBySeq = captured.Seq,
            environment = t.Meta.Environment,
            environmentWarnings = TraceAnalysis.EnvironmentWarnings(t.Meta),
            locator,
            hasScreenshotBefore = e.ScreenshotBefore != null,
            hasScreenshotAfter = e.ScreenshotAfter != null,
            extractedScreenshots = extracted,
            similarElements = similar.Select(s => new
            {
                score = Math.Round(s.Score, 2), element = s.Element.Describe(), locator = s.Element.SuggestedLocator()
            }),
            diagnostics = e.Diagnostics ?? captured.Diagnostics,
            previousStep = previousStep == null ? null : new { previousStep.Seq, previousStep.Name, previousStep.Target, previousStep.Diagnostics },
            otherWindows = otherWindows.Select(w => new { title = w.Name, className = w.ClassName, x = w.X, y = w.Y, width = w.Width, height = w.Height }),
            uiTree = args.Contains("--tree") ? TraceAnalysis.CompactTree(snapshot) : null,
            uiTreeElements = elements.Count
        }, jsonOptions));
        return 0;
    }

    if (failedOutsideSteps)
        Console.WriteLine("The test failed outside the recorded steps" + (t.Meta.FailureMessage != null ? ": " + t.Meta.FailureMessage : ".")
            + "\nShowing the last recorded step.\n");
    foreach (var w in TraceAnalysis.EnvironmentWarnings(t.Meta)) Console.WriteLine("Environment warning: " + w);
    var verdictLabel = e.Kind != "error" ? "" : TestFailed(t) ? "  [FAILED]" : "  [RECOVERED]";
    Console.WriteLine($"Step #{e.Seq} {e.Name}{verdictLabel}");
    if (e.Target != null) Console.WriteLine($"Target:   {e.Target}");
    if (e.Group != null) Console.WriteLine($"Group:    {e.Group}");
    if (e.Origin == "web") Console.WriteLine("Origin:   web (Playwright trace; DOM, network and console are in the original under playwright/ in the zip)");
    if (e.Location != null) Console.WriteLine($"Called:   {e.Location}");
    Console.WriteLine($"Time:     +{Offset(t, e):F2}s, took {e.DurationMs:F0} ms"
        + (e.CaptureMs is double cap ? $" (+{cap:F0} ms capture{(e.SnapshotMs is double snap ? $", UI tree {snap:F0} ms" : "")})" : ""));
    foreach (var p in e.Params) Console.WriteLine($"Param:    {p.Key} = {p.Value}");
    if (e.Error != null) Console.WriteLine($"Error:    {e.Error}");
    if (e.CauseSeq != null) Console.WriteLine($"Cause:    failed in nested step #{e.CauseSeq} (details and capture below are from that step)");
    if (captured != e && captured != detailFrom) Console.WriteLine($"Capture:  screenshot and UI tree taken by the enclosing step #{captured.Seq} ({captured.Name})");
    var diag = e.Diagnostics ?? captured.Diagnostics;
    if (diag != null && diag.Count > 0)
        Console.WriteLine("Observed: " + string.Join(", ", diag.Select(kv => kv.Key switch
        {
            "appResponding" => kv.Value == "no" ? "app NOT RESPONDING" : "app responding",
            "appCpu" => "app CPU " + kv.Value,
            "appRunning" => kv.Value == "no" ? "app process EXITED" : "app running",
            "screen" => "screen " + kv.Value,
            _ => kv.Key + " " + kv.Value
        })));
    if (e.Kind == "error" && previousStep?.Diagnostics is { } pd)
    {
        if (pd.TryGetValue("screen", out var ps) && ps == "unchanged")
            Console.WriteLine($"Previous: #{previousStep.Seq} {previousStep.Name} {previousStep.Target} left the screen unchanged (its action may not have taken effect)");
        if (pd.TryGetValue("appResponding", out var pr) && pr == "no")
            Console.WriteLine($"Previous: #{previousStep.Seq} ended with the app not responding");
    }
    if (otherWindows.Count > 0)
    {
        Console.WriteLine("Other windows of the app (popups, dropdowns, dialogs):");
        foreach (var w in otherWindows)
            Console.WriteLine($"  \"{w.Name}\" class {w.ClassName} at {w.X},{w.Y} {w.Width}x{w.Height}");
    }
    if (detailFrom.ErrorDetail != null) Console.WriteLine("\nStack trace:\n" + detailFrom.ErrorDetail);

    if (similar.Count > 0)
    {
        Console.WriteLine($"\nUI elements similar to \"{TraceAnalysis.LocatorValue(locator)}\" (possible typo or renamed element):");
        foreach (var s in similar)
            Console.WriteLine($"  {s.Score * 100:F0}%  {s.Element.Describe()}  ->  {s.Element.SuggestedLocator()}");
    }
    else if (e.Kind == "error" && elements.Count > 0)
        Console.WriteLine(locator == null
            ? "\nNo locator was recorded for this step. Pass the locator as the step target, e.g. Step(\"click\", by.ToString(), ...), to get suggestions."
            : $"\nNo UI element resembles \"{TraceAnalysis.LocatorValue(locator)}\". The element may not exist yet (add a wait), be in another window, or the action that shows it did not happen (a menu or dropdown that did not open)."
              + (captured.ScreenshotAfter != null ? " The screenshot at the failure shows what was on screen (--extract <dir>)." : ""));

    Console.WriteLine();
    Console.WriteLine(e.ScreenshotBefore != null || e.ScreenshotAfter != null
        ? extracted.Count > 0 ? "Screenshots:\n  " + string.Join("\n  ", extracted) : "Screenshots: available (use --extract <dir> to save them)."
        : "Screenshots: none for this step.");
    if (snapshot == null) Console.WriteLine("UI tree:     none for this step (captured on failure by default).");
    else if (args.Contains("--tree")) Console.WriteLine("\nUI tree:\n" + TraceAnalysis.CompactTree(snapshot));
    else Console.WriteLine($"UI tree:     {elements.Count} elements (use --tree to print it).");
    return 0;
}
