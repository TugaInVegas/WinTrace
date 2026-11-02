using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WinTrace
{
    /// <summary>One element of a UI tree snapshot (WinAppDriver/NovaWindowsDriver PageSource format).</summary>
    public sealed class UiElement
    {
        public string Tag { get; set; } = "";
        public string? Name { get; set; }
        public string? AutomationId { get; set; }
        public string? ClassName { get; set; }
        public int Depth { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }

        /// <summary>One line such as <c>Button Name="Save" AutomationId="btnSave"</c>.</summary>
        public string Describe()
        {
            var sb = new StringBuilder(Tag);
            if (!string.IsNullOrEmpty(Name)) sb.Append(" Name=\"").Append(Name).Append('"');
            if (!string.IsNullOrEmpty(AutomationId)) sb.Append(" AutomationId=\"").Append(AutomationId).Append('"');
            if (!string.IsNullOrEmpty(ClassName)) sb.Append(" ClassName=\"").Append(ClassName).Append('"');
            return sb.ToString();
        }

        /// <summary>The most robust locator for this element, in Appium C# syntax, or null if it has no usable property.</summary>
        public string? SuggestedLocator() =>
            Tag == "ProcessWindow" ? null   // another top-level window of the app, added by WinTrace.Appium: not an element locator
            : !string.IsNullOrEmpty(AutomationId) ? $"MobileBy.AccessibilityId(\"{AutomationId}\")"
            : !string.IsNullOrEmpty(Name) ? $"By.Name(\"{Name}\")"
            : !string.IsNullOrEmpty(ClassName) ? $"By.ClassName(\"{ClassName}\")"
            : null;
    }

    /// <summary>A UI element that resembles a locator that failed.</summary>
    public sealed class SimilarElement
    {
        public UiElement Element { get; set; } = new UiElement();
        /// <summary>0..1, where 1 is an exact match.</summary>
        public double Score { get; set; }
    }

    /// <summary>
    /// Text-only analysis of traces, for consoles, CI logs and AI agents that cannot use the HTML viewer.
    /// </summary>
    public static class TraceAnalysis
    {
        /// <summary>
        /// Problems in the recorded desktop environment that commonly break UI automation or the viewer's highlights:
        /// a locked or disconnected desktop, a non-interactive session, display scaling, remote desktop sessions.
        /// </summary>
        public static IReadOnlyList<string> EnvironmentWarnings(TraceMetadata meta)
        {
            var env = meta.Environment ?? new Dictionary<string, string>();
            var warnings = new List<string>();
            string? Get(string k) => env.TryGetValue(k, out var v) ? v : null;
            foreach (var (key, when) in new[] { ("desktop", "at the start"), ("desktopAtEnd", "at the end") })
                // Only a readable desktop name counts: a garbled value is a probe problem, not evidence of a lock screen.
                if (Get(key) is string d && !string.Equals(d, "Default", StringComparison.OrdinalIgnoreCase) && IsPlainText(d))
                    warnings.Add($"The input desktop {when} was \"{d}\" (lock screen or disconnected session): UI automation cannot reach the app, which shows up as \"element not found\" and blank screenshots.");
            if (Get("interactive") == "no")
                warnings.Add("The tests ran in a non-interactive session (e.g. a service account): desktop UI tests need an interactive, unlocked desktop.");
            if (Get("session") == "remote desktop")
                warnings.Add("The tests ran in a Remote Desktop session: minimizing or disconnecting the RDP window stops rendering for UI automation.");
            if (Get("scaling") is string s && s != "100%")
                warnings.Add($"Display scaling was {s}: highlight boxes in the viewer may be offset (locators are not affected).");
            return warnings;
        }

        static bool IsPlainText(string s) => s.All(ch => ch >= 0x20 && ch < 0x7F);

        /// <summary>
        /// "WinAppDriver", "Playwright", or "WinAppDriver + Playwright": the configured framework, adjusted to what the
        /// events actually contain (web events come from merged Playwright traces).
        /// </summary>
        public static string FrameworkLabel(TraceMetadata meta, IReadOnlyList<TraceEvent> events)
        {
            var web = events.Any(e => e.Origin == "web");
            var desktop = events.Any(e => e.Origin != "web" && e.Kind != "log");
            var fw = meta.Framework ?? "";
            if (web && !desktop) return "Playwright";
            if (web && fw.IndexOf("Playwright", StringComparison.OrdinalIgnoreCase) < 0) return fw.Length == 0 ? "Playwright" : fw + " + Playwright";
            return fw;
        }

        /// <summary>Step errors that are not the same failure propagating out of a nested step (no <c>CauseSeq</c>).</summary>
        public static IReadOnlyList<TraceEvent> StepErrors(IEnumerable<TraceEvent> events) =>
            events.Where(e => e.Kind == "error" && e.CauseSeq == null).OrderBy(e => e.Seq).ToList();

        /// <summary>
        /// The event holding the screenshot and UI tree for <paramref name="step"/>: the step itself, the nested step it
        /// failed in (<c>CauseSeq</c>), or, when that step captured nothing (e.g. a <c>Capture.None</c> probe), the
        /// innermost enclosing step that captured the same failure.
        /// </summary>
        public static TraceEvent CaptureFor(TraceEvent step, IReadOnlyList<TraceEvent> events)
        {
            var start = step.CauseSeq is int c ? events.FirstOrDefault(e => e.Seq == c) ?? step : step;
            if (start.ScreenshotAfter != null || start.Snapshot != null) return start;
            var current = start;
            for (var guard = 0; guard < 50; guard++)
            {
                var outer = events.Where(e => e.CauseSeq == current.Seq).OrderByDescending(e => e.Seq).FirstOrDefault();
                if (outer == null) break;
                if (outer.ScreenshotAfter != null || outer.Snapshot != null) return outer;
                current = outer;
            }
            return start;
        }

        /// <summary>Whether the test failed: the reported outcome, or for older traces, any step error.</summary>
        public static bool IsFailed(TraceMetadata meta, IEnumerable<TraceEvent> events) =>
            meta.Outcome != null ? meta.Outcome != "passed" : StepErrors(events).Count > 0;

        /// <summary>
        /// True for a minor step (<see cref="TraceEvent.Minor"/>) that is not part of the test's failure: a probe that
        /// succeeded or missed and was recovered. Viewers and reports may hide these.
        /// </summary>
        public static bool IsNoise(TraceEvent e, TraceMetadata meta, IReadOnlyList<TraceEvent> events)
        {
            if (e.Minor != true) return false;
            if (e.Kind != "error" || !IsFailed(meta, events)) return true;
            return !EndedTheTest(e, events);
        }

        // A minor step that threw is part of the failure when an enclosing step failed with it or nothing ran after it.
        static bool EndedTheTest(TraceEvent e, IReadOnlyList<TraceEvent> events) =>
            events.Any(x => x.CauseSeq == e.Seq) || events.Where(x => x.Kind != "log").LastOrDefault()?.Seq == e.Seq;

        /// <summary>
        /// Why the test failed when no step failed: for an aborted run, where it stopped ("The run stopped after step #3
        /// click Save; no step was running"); otherwise "The test failed outside the recorded steps (for example in an
        /// assertion)". Plain text, no trailing period.
        /// </summary>
        public static string OutsideStepsText(TraceMetadata meta, IReadOnlyList<TraceEvent> events)
        {
            if (meta.Outcome != "aborted") return "The test failed outside the recorded steps (for example in an assertion)";
            var last = events.Where(e => e.Kind != "log").OrderBy(e => e.Seq).LastOrDefault();
            return last == null ? "The run stopped before any step; no step was running"
                : $"The run stopped after step #{last.Seq} {last.Name}{(last.Target != null ? " " + last.Target : "")}; no step was running";
        }

        /// <summary>A minor step that threw and was recovered (a probe that missed).</summary>
        public static bool IsRecoveredMiss(TraceEvent e, TraceMetadata meta, IReadOnlyList<TraceEvent> events) =>
            e.Kind == "error" && IsNoise(e, meta, events);

        /// <summary>
        /// The step to look at first: where the test failed (the last step error; nested failures report their
        /// innermost step), or in a passed test the first recovered error. When the test failed outside any step,
        /// returns the last recorded step and sets <paramref name="failedOutsideSteps"/>.
        /// </summary>
        public static TraceEvent? FailureStep(TraceMetadata meta, IReadOnlyList<TraceEvent> events, out bool failedOutsideSteps)
        {
            var failed = IsFailed(meta, events);
            // In a failed test, a probe that missed and was recovered is not where it failed.
            var errors = StepErrors(events).Where(e => !failed || e.Minor != true || EndedTheTest(e, events)).ToList();
            failedOutsideSteps = failed && errors.Count == 0;
            if (failedOutsideSteps) return events.LastOrDefault(e => e.Kind != "log") ?? events.LastOrDefault();
            return failed ? errors.LastOrDefault() : errors.FirstOrDefault();
        }

        /// <summary>Parses an XML UI tree into a flat list in document order. Returns empty for non-XML snapshots.</summary>
        public static IReadOnlyList<UiElement> ParseTree(string? snapshot)
        {
            var list = new List<UiElement>();
            if (string.IsNullOrWhiteSpace(snapshot) || !snapshot!.TrimStart().StartsWith("<")) return list;
            XDocument doc;
            try { doc = XDocument.Parse(snapshot); }
            catch (XmlException) { return list; }
            if (doc.Root != null) Walk(doc.Root, 0, list);
            return list;
        }

        static void Walk(XElement e, int depth, List<UiElement> list)
        {
            string? A(string n) => (string?)e.Attribute(n);
            int? I(string n) => int.TryParse(A(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;
            list.Add(new UiElement
            {
                Tag = e.Name.LocalName, Name = A("Name"), AutomationId = A("AutomationId"), ClassName = A("ClassName"),
                Depth = depth, X = I("x"), Y = I("y"), Width = I("width"), Height = I("height")
            });
            foreach (var c in e.Elements()) Walk(c, depth + 1, list);
        }

        /// <summary>Indented one-line-per-element view of a UI tree, truncated after <paramref name="maxElements"/>.</summary>
        public static string CompactTree(string? snapshot, int maxElements = 400)
        {
            var all = ParseTree(snapshot);
            if (all.Count == 0) return snapshot ?? "";
            var sb = new StringBuilder();
            foreach (var e in all.Take(maxElements)) sb.Append(' ', e.Depth * 2).AppendLine(e.Describe());
            if (all.Count > maxElements) sb.AppendLine($"... {all.Count - maxElements} more elements");
            return sb.ToString();
        }

        /// <summary>
        /// The value part of a step target: <c>By.AccessibilityId: btnSave</c>, <c>accessibility id=btnSave</c> and
        /// <c>btnSave</c> all give <c>btnSave</c>.
        /// </summary>
        public static string LocatorValue(string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return "";
            var t = target!.Trim();
            var i = Math.Max(t.LastIndexOf(": ", StringComparison.Ordinal), t.LastIndexOf('='));
            var v = i >= 0 ? t.Substring(i + 1) : t;
            return v.Trim().Trim('"', '\'', '[', ']').Trim();
        }

        static readonly System.Text.RegularExpressions.Regex[] LocatorPatterns =
        {
            // Selenium/Appium By.ToString(): "By.AccessibilityId: btnSave", "By.Name: Save"
            new System.Text.RegularExpressions.Regex(@"By\.\w+:\s*[^\r\n\)\]]+"),
            // WebDriver JSON wire: using=name, value=Save
            new System.Text.RegularExpressions.Regex(@"using\W+(?<u>[\w ]+)\W+value\W+(?<v>[^""\r\n,}]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            // Plain "accessibility id=btnSave", "name: Save", "AutomationId='btnSave'"
            // (unquoted values end at whitespace; quoted ones may contain spaces)
            new System.Text.RegularExpressions.Regex(@"\b(accessibility ?id|automationid|class ?name|name|xpath)\s*[=:]\s*(?:['""](?<v>[^'""\r\n]+)['""]|(?<v>[^\s'"",;]+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        };

        /// <summary>
        /// The locator a failed step was looking for: its target, else one found in the error text, else in the log
        /// lines just before it. Null when none can be found.
        /// </summary>
        public static string? GuessLocator(TraceEvent step, IEnumerable<TraceEvent>? earlierEvents = null)
        {
            if (!string.IsNullOrWhiteSpace(step.Target)) return step.Target;
            var fromError = LocatorIn(step.ErrorDetail ?? step.Error);
            if (fromError != null) return fromError;
            if (earlierEvents == null) return null;
            foreach (var log in earlierEvents.Where(e => e.Seq < step.Seq && e.Kind == "log").OrderByDescending(e => e.Seq).Take(5))
            {
                var found = LocatorIn(log.Name);
                if (found != null) return found;
            }
            return null;
        }

        static string? LocatorIn(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var re in LocatorPatterns)
            {
                var m = re.Match(text);
                if (!m.Success) continue;
                return m.Groups["v"].Success ? m.Groups["v"].Value.Trim() : m.Value.Trim();
            }
            return null;
        }

        /// <summary>
        /// Elements whose AutomationId or Name resemble the locator in <paramref name="target"/>, best first.
        /// Use it on a failed find to spot typos and renamed elements.
        /// </summary>
        public static IReadOnlyList<SimilarElement> FindSimilar(string? snapshot, string? target, int max = 5, double minScore = 0.5)
        {
            var want = LocatorValue(target).ToLowerInvariant();
            if (want.Length == 0) return Array.Empty<SimilarElement>();
            return ParseTree(snapshot)
                .Where(e => e.Tag != "ProcessWindow")
                .Select(e => new SimilarElement { Element = e, Score = Math.Max(Similarity(want, e.AutomationId), Similarity(want, e.Name)) })
                .Where(s => s.Score >= minScore)
                .OrderByDescending(s => s.Score)
                .Take(max)
                .ToList();
        }

        static double Similarity(string want, string? candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return 0;
            var c = candidate!.ToLowerInvariant();
            if (c == want) return 1;
            var max = Math.Max(c.Length, want.Length);
            return 1.0 - (double)Levenshtein(want, c) / max;
        }

        static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) prev[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
