using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WinTrace
{
    /// <summary>
    /// A Markdown summary of a trace: outcome, the failing step with its error and stack trace, UI elements that
    /// resemble a locator that failed, every step, and the UI tree. Meant for people reading CI output and for AI
    /// agents, which read text and images more easily than an interactive HTML page.
    /// </summary>
    public static class TraceReport
    {
        public static string Markdown(TraceMetadata meta, IReadOnlyList<TraceEvent> events, Func<string, byte[]?> resource,
            string? traceFile = null, string? htmlFile = null, string? failureImageFile = null)
        {
            var inv = CultureInfo.InvariantCulture;
            string Secs(double ms) => (ms / 1000).ToString("0.00", inv) + " s";
            string Off(TraceEvent e) => "+" + (e.Start - meta.Started).TotalSeconds.ToString("0.00", inv) + " s";
            string? Text(string? id) { var b = id == null ? null : resource(id); return b == null ? null : Encoding.UTF8.GetString(b); }
            string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

            var failed = TraceAnalysis.IsFailed(meta, events);
            var errors = TraceAnalysis.StepErrors(events);
            var step = TraceAnalysis.FailureStep(meta, events, out var outside);
            var total = events.Count == 0 ? 0 : events.Max(e => (e.Start - meta.Started).TotalMilliseconds + e.DurationMs);
            var sb = new StringBuilder();

            var aborted = meta.Outcome == "aborted";
            sb.Append("# ").Append(meta.Title).Append(aborted ? " — ABORTED" : failed ? " — FAILED" : " — passed").AppendLine().AppendLine();
            sb.Append("- Outcome: **").Append(aborted ? "aborted (did not finish)" : failed ? "failed" : "passed").Append("**")
              .Append(meta.Outcome == null ? " (from step errors; the test framework did not report it)" : "")
              .Append(!failed && errors.Count > 0 ? $" with {errors.Count} recovered step error(s)" : "").AppendLine();
            if (failed && meta.FailureMessage != null) sb.Append("- Failure message: `").Append(meta.FailureMessage.Replace("`", "'")).AppendLine("`");
            sb.Append("- Started: ").Append(meta.Started.ToString("u", inv)).Append(", duration ").Append(Secs(total))
              .Append(", ").Append(events.Count(e => e.Kind != "log")).Append(" steps")
              .Append(TraceAnalysis.FrameworkLabel(meta, events) is string fw && fw.Length > 0 ? ", " + fw : "").AppendLine();
            foreach (var kv in meta.Context ?? new Dictionary<string, string>()) sb.Append("- ").Append(kv.Key).Append(": `").Append(kv.Value.Replace("`", "'")).AppendLine("`");
            if (traceFile != null) sb.Append("- Trace: `").Append(traceFile).AppendLine("`");
            if (htmlFile != null) sb.Append("- Viewer: `").Append(htmlFile).AppendLine("` (open in a browser)");
            if (failureImageFile != null) sb.Append("- Screenshot at the failure: `").Append(failureImageFile).AppendLine("`");
            sb.AppendLine();
            foreach (var w in TraceAnalysis.EnvironmentWarnings(meta)) sb.Append("> **Environment:** ").AppendLine(w).AppendLine();

            if (step != null && (failed || errors.Count > 0))
            {
                var captured = TraceAnalysis.CaptureFor(step, events);
                var detailFrom = step.CauseSeq is int c ? events.FirstOrDefault(e => e.Seq == c) ?? step : step;
                sb.AppendLine(failed ? "## Failure" : "## First recovered error").AppendLine();
                if (outside)
                    sb.AppendLine("The test failed **outside the recorded steps** (for example in an assertion). The last recorded step was:").AppendLine();
                sb.Append("Step #").Append(step.Seq).Append(" **").Append(step.Name).Append("**");
                if (step.Target != null) sb.Append(" `").Append(step.Target).Append('`');
                if (step.Group != null) sb.Append(" (group: ").Append(step.Group).Append(')');
                if (step.Origin == "web") sb.Append(" [web, Playwright]");
                if (step.Location != null) sb.Append(", called from `").Append(step.Location).Append('`');
                sb.Append(", ").Append(Off(step)).AppendLine().AppendLine();
                if (step.Error != null) sb.Append("Error: `").Append(step.Error.Replace("`", "'")).AppendLine("`").AppendLine();

                var snapshot = Text(captured.Snapshot);
                var locator = step.Kind == "error" ? TraceAnalysis.GuessLocator(detailFrom, events) : null;
                var similar = locator != null ? TraceAnalysis.FindSimilar(snapshot, locator) : Array.Empty<SimilarElement>();
                if (similar.Count > 0)
                {
                    sb.Append("UI elements similar to `").Append(TraceAnalysis.LocatorValue(locator)).AppendLine("` (possible typo or renamed element):").AppendLine();
                    sb.AppendLine("| Match | Element | Suggested locator |").AppendLine("|---|---|---|");
                    foreach (var s in similar)
                        sb.Append("| ").Append((s.Score * 100).ToString("0", inv)).Append("% | ").Append(Cell(s.Element.Describe()))
                          .Append(" | `").Append(s.Element.SuggestedLocator()).AppendLine("` |");
                    sb.AppendLine();
                }
                else if (step.Kind == "error" && snapshot != null)
                    sb.AppendLine(locator == null
                        ? "No locator was recorded for this step, so no similar elements can be suggested."
                        : $"No UI element resembles `{TraceAnalysis.LocatorValue(locator)}`: it may not exist yet (add a wait), be in another window, or the action that shows it did not happen (a menu or dropdown that did not open)."
                          + (failureImageFile != null ? $" The screenshot at the failure (`{failureImageFile}`) shows what was on screen." : "")).AppendLine();

                if (captured != step && captured != detailFrom)
                    sb.Append("Screenshot and UI tree were captured by the enclosing step #").Append(captured.Seq).Append(" (").Append(captured.Name).AppendLine(").").AppendLine();
                if (detailFrom.ErrorDetail != null)
                    sb.AppendLine("<details><summary>Stack trace</summary>").AppendLine().AppendLine("```")
                      .AppendLine(detailFrom.ErrorDetail.TrimEnd()).AppendLine("```").AppendLine().AppendLine("</details>").AppendLine();

                var others = TraceAnalysis.ParseTree(snapshot).Where(e => e.Tag == "ProcessWindow").ToList();
                if (others.Count > 0)
                {
                    sb.AppendLine("Other windows of the app that were open (popups, dropdowns, dialogs):").AppendLine();
                    foreach (var w in others)
                        sb.Append("- ").Append(string.IsNullOrEmpty(w.Name) ? "(no title)" : "\"" + w.Name + "\"").Append(" class `").Append(w.ClassName)
                          .Append("` at ").Append(w.X).Append(',').Append(w.Y).Append(", ").Append(w.Width).Append('x').Append(w.Height).AppendLine();
                    sb.AppendLine();
                }
                var diag = step.Diagnostics ?? captured.Diagnostics;
                var previous = events.Where(e => e.Seq < step.Seq && e.Kind != "log").OrderByDescending(e => e.Seq).FirstOrDefault();
                var notes = new List<string>();
                if (diag != null && diag.TryGetValue("appResponding", out var resp) && resp == "no") notes.Add("the app window was **not responding** at the end of this step");
                if (diag != null && diag.TryGetValue("appCpu", out var cpu)) notes.Add("app CPU during the step: " + cpu);
                if (previous?.Diagnostics != null && previous.Diagnostics.TryGetValue("screen", out var scr) && scr == "unchanged")
                    notes.Add($"the screen did **not change** after the previous step (#{previous.Seq} {previous.Name} {previous.Target}): its action may not have taken effect");
                if (previous?.Diagnostics != null && previous.Diagnostics.TryGetValue("appResponding", out var presp) && presp == "no")
                    notes.Add($"the app was not responding after the previous step (#{previous.Seq})");
                if (notes.Count > 0) sb.Append("Observed: ").Append(string.Join("; ", notes)).AppendLine(".").AppendLine();

                if (snapshot != null)
                {
                    var tree = TraceAnalysis.CompactTree(snapshot, 250);
                    sb.AppendLine("<details><summary>UI tree at the failure</summary>").AppendLine().AppendLine("```")
                      .AppendLine(tree.TrimEnd()).AppendLine("```").AppendLine().AppendLine("</details>").AppendLine();
                }
            }

            sb.AppendLine("## Steps").AppendLine();
            var misses = events.Count(e => TraceAnalysis.IsRecoveredMiss(e, meta, events));
            if (misses > 0) sb.Append(misses).AppendLine(misses == 1
                ? " step marked *missed (recovered)* is a probe (`Capture.None`) that threw and was handled; it is not where the test failed."
                : " steps marked *missed (recovered)* are probes (`Capture.None`) that threw and were handled; they are not where the test failed.").AppendLine();
            sb.AppendLine("| # | | Time | Duration | Group | Step | Target / message |").AppendLine("|---|---|---|---|---|---|---|");
            foreach (var e in events.Take(300))
            {
                var status = e.Kind == "log" ? "log" : e.Kind != "error" ? "ok"
                    : TraceAnalysis.IsRecoveredMiss(e, meta, events) ? "missed (recovered)" : failed ? "**FAIL**" : "recovered";
                if (e.Diagnostics != null && e.Diagnostics.TryGetValue("appResponding", out var hung) && hung == "no") status += ", app not responding";
                var target = e.Error != null ? (e.Target != null ? e.Target + " — " : "") + (e.CauseSeq is int cs ? $"(failed in #{cs}) " : "") + e.Error : e.Target;
                sb.Append("| ").Append(e.Seq).Append(" | ").Append(status).Append(" | ").Append(Off(e)).Append(" | ")
                  .Append(e.Kind == "log" ? "" : Secs(e.DurationMs)).Append(" | ").Append(Cell(e.Group)).Append(" | ")
                  .Append(e.Origin == "web" ? "[web] " : "").Append(Cell(e.Name)).Append(" | ").Append(Cell(target)).AppendLine(" |");
            }
            if (events.Count > 300) sb.Append("\n... ").Append(events.Count - 300).AppendLine(" more events in the trace.");
            sb.AppendLine();

            if (meta.Environment != null && meta.Environment.Count > 0)
            {
                sb.AppendLine("## Environment").AppendLine().AppendLine("| | |").AppendLine("|---|---|");
                foreach (var kv in meta.Environment) sb.Append("| ").Append(Cell(kv.Key)).Append(" | ").Append(Cell(kv.Value)).AppendLine(" |");
                sb.Append("| machine | ").Append(Cell(meta.Machine)).AppendLine(" |").Append("| OS | ").Append(Cell(meta.Os)).AppendLine(" |").AppendLine();
            }

            if (meta.PlaywrightTraces.Count > 0)
                sb.AppendLine("Web steps (`[web]`) come from the Playwright trace(s) stored in the zip under `playwright/`. For DOM snapshots, network and console, extract one and open it with `pwsh bin/<config>/<tfm>/playwright.ps1 show-trace <file>` or https://trace.playwright.dev.").AppendLine();

            if (traceFile != null)
                sb.AppendLine("## Dig further").AppendLine().AppendLine("```")
                  .Append("wintrace inspect \"").Append(traceFile).AppendLine("\" --tree")
                  .Append("wintrace inspect \"").Append(traceFile).AppendLine("\" --extract screenshots --all")
                  .AppendLine("```");
            return sb.ToString();
        }
    }
}
