using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinTrace
{
    /// <summary>Outcome of one test in one run, from a trace or a TRX file.</summary>
    public enum RunOutcome { Unknown, Passed, Failed, NotRun }

    /// <summary>A failed (or saved) test of the current run.</summary>
    public sealed class RunReportEntry
    {
        public string Title { get; set; } = "";
        public string TracePath { get; set; } = "";
        public bool Failed { get; set; }
        public string? Signature { get; set; }
        public string? FailingStep { get; set; }
        public string? Error { get; set; }
        public string? Location { get; set; }
        public bool OutsideSteps { get; set; }
        public string? MarkdownPath { get; set; }
        public IReadOnlyList<string> EnvironmentWarnings { get; set; } = Array.Empty<string>();
        /// <summary>Test context from the trace (<c>TraceRecorder.SetContext</c>).</summary>
        public IReadOnlyDictionary<string, string> Context { get; set; } = new Dictionary<string, string>();
        /// <summary>new, recurring, intermittent or unknown (no history).</summary>
        public string History { get; set; } = "unknown";
        public string HistoryDetail { get; set; } = "";
        /// <summary>The earlier run whose passing trace of this test was compared, when there is one.</summary>
        public string? ComparedWith { get; set; }
        /// <summary>For a failed test: where its steps first differ from the last passing run.</summary>
        public string? Divergence { get; set; }
        /// <summary>Steps at least 2x and 1 s slower than in the last passing run, slowest change first.</summary>
        public List<SlowerStep> SlowerSteps { get; set; } = new List<SlowerStep>();
    }

    /// <summary>Failed tests that failed the same way (same step, target and normalized error).</summary>
    public sealed class SlowerStep
    {
        public int Seq { get; set; }
        public string Step { get; set; } = "";
        public double DurationMs { get; set; }
        public double PassingDurationMs { get; set; }
    }

    public sealed class FailureCluster
    {
        public string Signature { get; set; } = "";
        public string Description { get; set; } = "";
        public List<RunReportEntry> Tests { get; set; } = new List<RunReportEntry>();
    }

    /// <summary>Test outcomes of one run read from TRX files.</summary>
    public sealed class TrxRun
    {
        public Dictionary<string, RunOutcome> Tests { get; } = new Dictionary<string, RunOutcome>(StringComparer.OrdinalIgnoreCase);
        public int Total => Tests.Count;
        public int Passed => Tests.Values.Count(o => o == RunOutcome.Passed);
        public int Failed => Tests.Values.Count(o => o == RunOutcome.Failed);
        public int NotRun => Tests.Values.Count(o => o == RunOutcome.NotRun);

        /// <summary>Reads one TRX file, or every TRX file under a folder.</summary>
        public static TrxRun Read(string fileOrFolder)
        {
            var run = new TrxRun();
            var files = File.Exists(fileOrFolder) ? new[] { fileOrFolder }
                : Directory.Exists(fileOrFolder) ? Directory.GetFiles(fileOrFolder, "*.trx", SearchOption.AllDirectories) : Array.Empty<string>();
            foreach (var f in files)
            {
                XDocument doc;
                try { doc = XDocument.Load(f); }
                catch (Exception ex) when (ex is System.Xml.XmlException || ex is IOException) { continue; }
                foreach (var r in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
                {
                    var name = (string?)r.Attribute("testName");
                    if (string.IsNullOrEmpty(name)) continue;
                    var outcome = ((string?)r.Attribute("outcome") ?? "").ToLowerInvariant() switch
                    {
                        "passed" => RunOutcome.Passed,
                        "failed" or "error" or "timeout" or "aborted" => RunOutcome.Failed,
                        "notexecuted" or "inconclusive" or "pending" or "disconnected" or "warning" => RunOutcome.NotRun,
                        _ => RunOutcome.Unknown
                    };
                    // A test retried in the same run counts as failed only if every attempt failed.
                    if (run.Tests.TryGetValue(name!, out var prev) && prev == RunOutcome.Passed) continue;
                    run.Tests[name!] = outcome;
                }
            }
            return run;
        }
    }

    /// <summary>One earlier run: the traces it saved and, when available, its TRX outcomes.</summary>
    public sealed class HistoricRun
    {
        public string Name { get; set; } = "";
        public Dictionary<string, string?> FailedSignatures { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        public TrxRun? Trx { get; set; }
        /// <summary>Steps of the tests that passed in this run and left a trace (e.g. <c>SavePassedSummary</c>), by test title.</summary>
        public Dictionary<string, IReadOnlyList<TraceEvent>> PassedSteps { get; } = new Dictionary<string, IReadOnlyList<TraceEvent>>(StringComparer.OrdinalIgnoreCase);

        public RunOutcome OutcomeOf(string test) =>
            FailedSignatures.ContainsKey(test) ? RunOutcome.Failed
            : PassedSteps.ContainsKey(test) && !(Trx != null && Trx.Tests.TryGetValue(test, out var t) && t == RunOutcome.Failed) ? RunOutcome.Passed
            : Trx != null && Trx.Tests.TryGetValue(test, out var o) ? o
            : RunOutcome.Unknown;
    }

    /// <summary>
    /// Summary of a test run from its traces: failures grouped by cause, each classified against earlier runs as new,
    /// recurring or intermittent (a flakiness signal), with TRX totals when available. Markdown or JSON.
    /// </summary>
    public sealed class RunReport
    {
        public string Folder { get; set; } = "";
        public List<RunReportEntry> Entries { get; } = new List<RunReportEntry>();
        public List<FailureCluster> Clusters { get; } = new List<FailureCluster>();
        public List<HistoricRun> History { get; } = new List<HistoricRun>();
        public TrxRun? Trx { get; set; }
        /// <summary>Failed in the TRX but no trace was saved (e.g. web-only tests, or failures before the session started).</summary>
        public List<string> FailedWithoutTrace { get; } = new List<string>();
        /// <summary>Passed now, failed in the previous run.</summary>
        public List<string> Fixed { get; } = new List<string>();

        public static RunReport Build(string folder, IEnumerable<string> tracePaths, IEnumerable<HistoricRun>? history = null, TrxRun? trx = null)
        {
            var report = new RunReport { Folder = folder, Trx = trx };
            if (history != null) report.History.AddRange(history);

            // Several traces of one test in a run (e.g. retries): keep the newest.
            var latest = new Dictionary<string, (LoadedTrace trace, string path)>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in tracePaths.OrderBy(p => File.GetLastWriteTimeUtc(p)))
            {
                LoadedTrace t;
                try { t = TraceReader.ReadSummary(path); }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is System.Text.Json.JsonException) { continue; }
                latest[t.Meta.Title] = (t, path);
            }

            foreach (var (t, path) in latest.Values)
            {
                var entry = Describe(t, path);
                if (entry.Failed) Classify(entry, report.History);
                Compare(entry, t.Events, report.History);
                report.Entries.Add(entry);
            }

            foreach (var g in report.Entries.Where(e => e.Failed).GroupBy(e => e.Signature ?? "").OrderByDescending(g => g.Count()))
            {
                var first = g.First();
                report.Clusters.Add(new FailureCluster
                {
                    Signature = g.Key,
                    Description = first.OutsideSteps ? "Failed outside the recorded steps: " + (first.Error ?? "(no message)") : $"{first.FailingStep} — {first.Error}",
                    Tests = g.OrderBy(e => e.Title).ToList()
                });
            }

            if (trx != null)
            {
                var traced = new HashSet<string>(report.Entries.Where(e => e.Failed).Select(e => e.Title), StringComparer.OrdinalIgnoreCase);
                report.FailedWithoutTrace.AddRange(trx.Tests.Where(kv => kv.Value == RunOutcome.Failed && !traced.Contains(kv.Key)).Select(kv => kv.Key).OrderBy(n => n));
                var previous = report.History.FirstOrDefault();
                if (previous != null)
                    report.Fixed.AddRange(trx.Tests.Where(kv => kv.Value == RunOutcome.Passed && previous.OutcomeOf(kv.Key) == RunOutcome.Failed).Select(kv => kv.Key).OrderBy(n => n));
            }
            return report;
        }

        /// <summary>Reads the failed traces of an earlier run folder (and its TRX files when present).</summary>
        public static HistoricRun ReadHistoricRun(string name, IEnumerable<string> tracePaths, TrxRun? trx)
        {
            var run = new HistoricRun { Name = name, Trx = trx };
            var passedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in tracePaths)
            {
                try
                {
                    var t = TraceReader.ReadSummary(path);
                    if (!TraceAnalysis.IsFailed(t.Meta, t.Events))
                    {
                        // Several passing traces of one test (retries): keep the newest.
                        if (passedAt.TryGetValue(t.Meta.Title, out var at) && at >= t.Meta.Started) continue;
                        passedAt[t.Meta.Title] = t.Meta.Started;
                        run.PassedSteps[t.Meta.Title] = t.Events;
                        continue;
                    }
                    run.FailedSignatures[t.Meta.Title] = Describe(t, path).Signature;
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is System.Text.Json.JsonException) { }
            }
            return run;
        }

        static RunReportEntry Describe(LoadedTrace t, string path)
        {
            var failed = TraceAnalysis.IsFailed(t.Meta, t.Events);
            var step = TraceAnalysis.FailureStep(t.Meta, t.Events, out var outside);
            var md = Path.ChangeExtension(path, ".md");
            var entry = new RunReportEntry
            {
                Title = t.Meta.Title,
                TracePath = path,
                Failed = failed,
                OutsideSteps = failed && outside,
                MarkdownPath = File.Exists(md) ? md : null,
                EnvironmentWarnings = TraceAnalysis.EnvironmentWarnings(t.Meta),
                Context = t.Meta.Context ?? new Dictionary<string, string>()
            };
            if (!failed) return entry;
            if (outside)
            {
                entry.Error = t.Meta.FailureMessage;
                entry.Signature = "outside|" + Normalize(t.Meta.FailureMessage);
            }
            else if (step != null)
            {
                entry.FailingStep = (step.Origin == "web" ? "[web] " : "") + step.Name + (step.Target != null ? " " + step.Target : "");
                entry.Error = step.Error;
                entry.Location = step.Location;
                entry.Signature = $"{step.Origin}|{step.Name}|{step.Target}|{Normalize(step.Error)}";
            }
            return entry;
        }

        // Steps that matter for comparing runs: actions and failures, not logs or minor probes.
        static List<TraceEvent> Comparable(IEnumerable<TraceEvent> events) =>
            events.Where(e => e.Kind != "log" && e.Minor != true && e.CauseSeq == null).OrderBy(e => e.Seq).ToList();

        static string Key(TraceEvent e) => $"{e.Origin}|{e.Group}|{e.Name}|{e.Target}";
        static string Label(TraceEvent e) => $"#{e.Seq} {(e.Origin == "web" ? "[web] " : "")}{e.Name}{(e.Target != null ? " " + e.Target : "")}";
        static string Secs(double ms) => (ms / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";

        /// <summary>Compares a test with its last passing run: first divergent step (failed tests) and steps that got slower.</summary>
        static void Compare(RunReportEntry entry, IReadOnlyList<TraceEvent> events, List<HistoricRun> history)
        {
            var run = history.FirstOrDefault(h => h.PassedSteps.ContainsKey(entry.Title) && h.OutcomeOf(entry.Title) == RunOutcome.Passed);
            if (run == null) return;
            entry.ComparedWith = run.Name;
            var now = Comparable(events);
            var before = Comparable(run.PassedSteps[entry.Title]);

            if (entry.Failed)
            {
                for (var i = 0; ; i++)
                {
                    if (i >= now.Count)
                    {
                        entry.Divergence = i >= before.Count
                            ? "same steps as the last passing run; the failure came after them"
                            : $"this run stopped after {(i == 0 ? "no steps" : Label(now[i - 1]))}; the last passing run went on with {Label(before[i])}";
                        break;
                    }
                    if (i >= before.Count) { entry.Divergence = $"{Label(now[i])}: the last passing run had no more steps at this point"; break; }
                    if (Key(now[i]) != Key(before[i]))
                    {
                        entry.Divergence = $"{Label(now[i])} here, where the last passing run did {Label(before[i])}";
                        break;
                    }
                    if (now[i].Kind == "error")
                    {
                        entry.Divergence = $"{Label(now[i])} failed after {Secs(now[i].DurationMs)}; the same step passed in {Secs(before[i].DurationMs)} in the last passing run (all steps before it matched)";
                        break;
                    }
                }
            }

            // Match repeated steps by occurrence (the 3rd "click Save" with the 3rd "click Save").
            var passing = new Dictionary<string, List<TraceEvent>>();
            foreach (var e in before)
            {
                if (!passing.TryGetValue(Key(e), out var l)) passing[Key(e)] = l = new List<TraceEvent>();
                l.Add(e);
            }
            var seen = new Dictionary<string, int>();
            var slower = new List<SlowerStep>();
            foreach (var e in now)
            {
                var k = Key(e);
                seen.TryGetValue(k, out var n);
                seen[k] = n + 1;
                if (e.Kind == "error" || !passing.TryGetValue(k, out var list) || n >= list.Count) continue;
                var was = list[n].DurationMs;
                if (e.DurationMs >= 2 * was && e.DurationMs - was >= 1000)
                    slower.Add(new SlowerStep { Seq = e.Seq, Step = Label(e).Substring(Label(e).IndexOf(' ') + 1), DurationMs = Math.Round(e.DurationMs), PassingDurationMs = Math.Round(was) });
            }
            entry.SlowerSteps = slower.OrderByDescending(s => s.DurationMs - s.PassingDurationMs).Take(5).ToList();
        }

        static void Classify(RunReportEntry entry, List<HistoricRun> history)
        {
            var known = history.Select(h => (h, o: h.OutcomeOf(entry.Title))).Where(x => x.o == RunOutcome.Failed || x.o == RunOutcome.Passed).ToList();
            if (history.Count == 0) { entry.History = "unknown"; entry.HistoryDetail = "no earlier runs to compare"; return; }
            var failedBefore = known.Count(x => x.o == RunOutcome.Failed);
            var passedBefore = known.Count(x => x.o == RunOutcome.Passed);
            var same = known.Count(x => x.o == RunOutcome.Failed && x.h.FailedSignatures.TryGetValue(entry.Title, out var s) && s == entry.Signature);
            if (failedBefore == 0)
            {
                entry.History = "new";
                entry.HistoryDetail = passedBefore > 0 ? $"passed in {passedBefore} earlier run(s)" : $"no failure in {history.Count} earlier run(s)";
            }
            else if (passedBefore > 0)
            {
                entry.History = "intermittent";
                entry.HistoryDetail = $"failed in {failedBefore}, passed in {passedBefore} of {history.Count} earlier runs";
            }
            else
            {
                entry.History = "recurring";
                entry.HistoryDetail = $"failed in {failedBefore} of {history.Count} earlier runs" + (same == failedBefore ? ", same cause" : $", same cause in {same}");
            }
        }

        static readonly Regex Volatile = new Regex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\d+([.,]\d+)?", RegexOptions.IgnoreCase);

        /// <summary>Error text with numbers and GUIDs replaced, so the same failure matches across tests and runs.</summary>
        public static string Normalize(string? error)
        {
            if (string.IsNullOrEmpty(error)) return "";
            var s = Volatile.Replace(error!.Trim(), "#");
            return s.Length > 300 ? s.Substring(0, 300) : s;
        }

        public string ToMarkdown(string? reportDirectory = null)
        {
            string Link(string? path, string text) =>
                path == null ? text : $"[{text}]({Rel(path, reportDirectory).Replace(" ", "%20").Replace("\\", "/")})";
            string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            var failed = Entries.Where(e => e.Failed).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("# Test run report").AppendLine();
            sb.Append("- Traces: **").Append(Entries.Count).Append("**, failed tests with a trace: **").Append(failed.Count).AppendLine("**");
            if (Trx != null)
                sb.Append("- TRX: ").Append(Trx.Total).Append(" tests, ").Append(Trx.Passed).Append(" passed, **").Append(Trx.Failed).Append(" failed**, ")
                  .Append(Trx.NotRun).AppendLine(" not run");
            sb.Append("- History: ").AppendLine(History.Count == 0 ? "no earlier runs given or found (use --history)" : $"compared with {History.Count} earlier run(s)");
            if (failed.Count > 0 && History.Count > 0)
                sb.Append("- New: **").Append(failed.Count(e => e.History == "new")).Append("**, recurring: ").Append(failed.Count(e => e.History == "recurring"))
                  .Append(", intermittent (possibly flaky): ").Append(failed.Count(e => e.History == "intermittent")).AppendLine();
            sb.AppendLine();

            var warnings = Entries.SelectMany(e => e.EnvironmentWarnings).Distinct().ToList();
            foreach (var w in warnings) sb.Append("> **Environment:** ").AppendLine(w).AppendLine();

            if (Clusters.Count > 0)
            {
                sb.AppendLine("## Failures by cause").AppendLine();
                var i = 0;
                foreach (var c in Clusters)
                {
                    sb.Append("### ").Append(++i).Append(". ").Append(c.Description).Append(" (").Append(c.Tests.Count)
                      .Append(c.Tests.Count == 1 ? " test" : " tests").AppendLine(")").AppendLine();
                    sb.AppendLine("| Test | Context | History | Called from | Report |").AppendLine("|---|---|---|---|---|");
                    foreach (var e in c.Tests)
                        sb.Append("| ").Append(Cell(e.Title)).Append(" | ").Append(Cell(string.Join(", ", e.Context.Select(kv => kv.Key + "=" + kv.Value)))).Append(" | ").Append(e.History).Append(e.HistoryDetail.Length > 0 ? " (" + Cell(e.HistoryDetail) + ")" : "")
                          .Append(" | ").Append(Cell(e.Location)).Append(" | ").Append(Link(e.MarkdownPath ?? e.TracePath, e.MarkdownPath != null ? "summary" : "trace")).AppendLine(" |");
                    sb.AppendLine();
                    var compared = c.Tests.Where(e => e.Divergence != null).ToList();
                    if (compared.Count > 0)
                    {
                        sb.AppendLine("Compared with the last passing run:").AppendLine();
                        foreach (var e in compared)
                            sb.Append("- ").Append(e.Title).Append(" (").Append(e.ComparedWith).Append("): first difference at ").Append(e.Divergence).AppendLine();
                        sb.AppendLine();
                    }
                }
            }
            else sb.AppendLine("No failed tests with a trace.").AppendLine();

            var slow = Entries.Where(e => e.SlowerSteps.Count > 0).OrderBy(e => e.Title).ToList();
            if (slow.Count > 0)
            {
                sb.AppendLine("## Slower than the last passing run").AppendLine();
                sb.AppendLine("Steps at least twice and 1 s slower than in the test's last passing run (a sign of a slower app, data or environment):").AppendLine();
                sb.AppendLine("| Test | Step | This run | Last pass | Run compared |").AppendLine("|---|---|---|---|---|");
                foreach (var e in slow)
                    foreach (var s in e.SlowerSteps)
                        sb.Append("| ").Append(Cell(e.Title)).Append(" | #").Append(s.Seq).Append(' ').Append(Cell(s.Step)).Append(" | ").Append(Secs(s.DurationMs))
                          .Append(" | ").Append(Secs(s.PassingDurationMs)).Append(" | ").Append(Cell(e.ComparedWith)).AppendLine(" |");
                sb.AppendLine();
            }

            if (FailedWithoutTrace.Count > 0)
            {
                sb.AppendLine("## Failed without a trace").AppendLine();
                sb.AppendLine("Failed in the TRX but no trace was saved (for example web-only tests, or failures before the trace started):").AppendLine();
                foreach (var n in FailedWithoutTrace) sb.Append("- ").AppendLine(n);
                sb.AppendLine();
            }
            if (Fixed.Count > 0)
            {
                sb.AppendLine("## Passing again").AppendLine();
                sb.AppendLine("Failed in the previous run, passed in this one:").AppendLine();
                foreach (var n in Fixed) sb.Append("- ").AppendLine(n);
                sb.AppendLine();
            }
            if (Trx == null)
                sb.AppendLine("_Only failed traces are saved by default, so passed tests are not counted. Pass `--trx` for totals and tests that failed without a trace._");
            return sb.ToString();
        }

        static string Rel(string path, string? from)
        {
            if (from == null) return path;
            try
            {
                var f = new Uri(Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                return Uri.UnescapeDataString(f.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString());
            }
            catch (UriFormatException) { return path; }
        }
    }
}
