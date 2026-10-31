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
        /// <summary>new, recurring, intermittent or unknown (no history).</summary>
        public string History { get; set; } = "unknown";
        public string HistoryDetail { get; set; } = "";
    }

    /// <summary>Failed tests that failed the same way (same step, target and normalized error).</summary>
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

        public RunOutcome OutcomeOf(string test) =>
            FailedSignatures.ContainsKey(test) ? RunOutcome.Failed
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
            foreach (var path in tracePaths)
            {
                try
                {
                    var t = TraceReader.ReadSummary(path);
                    if (!TraceAnalysis.IsFailed(t.Meta, t.Events)) continue;
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
                EnvironmentWarnings = TraceAnalysis.EnvironmentWarnings(t.Meta)
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
            sb.Append("- History: ").AppendLine(History.Count == 0 ? "no earlier runs given (use --history)" : $"compared with {History.Count} earlier run(s)");
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
                    sb.AppendLine("| Test | History | Called from | Report |").AppendLine("|---|---|---|---|");
                    foreach (var e in c.Tests)
                        sb.Append("| ").Append(Cell(e.Title)).Append(" | ").Append(e.History).Append(e.HistoryDetail.Length > 0 ? " (" + Cell(e.HistoryDetail) + ")" : "")
                          .Append(" | ").Append(Cell(e.Location)).Append(" | ").Append(Link(e.MarkdownPath ?? e.TracePath, e.MarkdownPath != null ? "summary" : "trace")).AppendLine(" |");
                    sb.AppendLine();
                }
            }
            else sb.AppendLine("No failed tests with a trace.").AppendLine();

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
