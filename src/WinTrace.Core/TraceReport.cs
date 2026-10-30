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

            sb.Append("# ").Append(meta.Title).Append(failed ? " — FAILED" : " — passed").AppendLine().AppendLine();
            sb.Append("- Outcome: **").Append(failed ? "failed" : "passed").Append("**")
              .Append(meta.Outcome == null ? " (from step errors; the test framework did not report it)" : "")
              .Append(!failed && errors.Count > 0 ? $" with {errors.Count} recovered step error(s)" : "").AppendLine();
            if (failed && meta.FailureMessage != null) sb.Append("- Failure message: `").Append(meta.FailureMessage.Replace("`", "'")).AppendLine("`");
            sb.Append("- Started: ").Append(meta.Started.ToString("u", inv)).Append(", duration ").Append(Secs(total))
              .Append(", ").Append(events.Count(e => e.Kind != "log")).Append(" steps")
              .Append(string.IsNullOrEmpty(meta.Framework) ? "" : ", " + meta.Framework).AppendLine();
            if (traceFile != null) sb.Append("- Trace: `").Append(traceFile).AppendLine("`");
            if (htmlFile != null) sb.Append("- Viewer: `").Append(htmlFile).AppendLine("` (open in a browser)");
            if (failureImageFile != null) sb.Append("- Screenshot at the failure: `").Append(failureImageFile).AppendLine("`");
            sb.AppendLine();

            if (step != null && (failed || errors.Count > 0))
            {
                var captured = step.CauseSeq is int c ? events.FirstOrDefault(e => e.Seq == c) ?? step : step;
                sb.AppendLine(failed ? "## Failure" : "## First recovered error").AppendLine();
                if (outside)
                    sb.AppendLine("The test failed **outside the recorded steps** (for example in an assertion). The last recorded step was:").AppendLine();
                sb.Append("Step #").Append(step.Seq).Append(" **").Append(step.Name).Append("**");
                if (step.Target != null) sb.Append(" `").Append(step.Target).Append('`');
                if (step.Group != null) sb.Append(" (group: ").Append(step.Group).Append(')');
                sb.Append(", ").Append(Off(step)).AppendLine().AppendLine();
                if (step.Error != null) sb.Append("Error: `").Append(step.Error.Replace("`", "'")).AppendLine("`").AppendLine();

                var snapshot = Text(captured.Snapshot);
                var locator = step.Kind == "error" ? TraceAnalysis.GuessLocator(captured, events) : null;
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
                        : $"No UI element resembles `{TraceAnalysis.LocatorValue(locator)}`: it may not exist yet (add a wait) or be in another window.").AppendLine();

                if (captured.ErrorDetail != null)
                    sb.AppendLine("<details><summary>Stack trace</summary>").AppendLine().AppendLine("```")
                      .AppendLine(captured.ErrorDetail.TrimEnd()).AppendLine("```").AppendLine().AppendLine("</details>").AppendLine();

                if (snapshot != null)
                {
                    var tree = TraceAnalysis.CompactTree(snapshot, 250);
                    sb.AppendLine("<details><summary>UI tree at the failure</summary>").AppendLine().AppendLine("```")
                      .AppendLine(tree.TrimEnd()).AppendLine("```").AppendLine().AppendLine("</details>").AppendLine();
                }
            }

            sb.AppendLine("## Steps").AppendLine();
            sb.AppendLine("| # | | Time | Duration | Group | Step | Target / message |").AppendLine("|---|---|---|---|---|---|---|");
            foreach (var e in events.Take(300))
            {
                var status = e.Kind == "log" ? "log" : e.Kind != "error" ? "ok" : failed ? "**FAIL**" : "recovered";
                var target = e.Error != null ? (e.Target != null ? e.Target + " — " : "") + (e.CauseSeq is int cs ? $"(failed in #{cs}) " : "") + e.Error : e.Target;
                sb.Append("| ").Append(e.Seq).Append(" | ").Append(status).Append(" | ").Append(Off(e)).Append(" | ")
                  .Append(e.Kind == "log" ? "" : Secs(e.DurationMs)).Append(" | ").Append(Cell(e.Group)).Append(" | ")
                  .Append(Cell(e.Name)).Append(" | ").Append(Cell(target)).AppendLine(" |");
            }
            if (events.Count > 300) sb.Append("\n... ").Append(events.Count - 300).AppendLine(" more events in the trace.");
            sb.AppendLine();

            if (traceFile != null)
                sb.AppendLine("## Dig further").AppendLine().AppendLine("```")
                  .Append("wintrace inspect \"").Append(traceFile).AppendLine("\" --tree")
                  .Append("wintrace inspect \"").Append(traceFile).AppendLine("\" --extract screenshots --all")
                  .AppendLine("```");
            return sb.ToString();
        }
    }
}
