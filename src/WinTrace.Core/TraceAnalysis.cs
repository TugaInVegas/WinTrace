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
            !string.IsNullOrEmpty(AutomationId) ? $"MobileBy.AccessibilityId(\"{AutomationId}\")"
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
        /// <summary>Step errors that are not the same failure propagating out of a nested step (no <c>CauseSeq</c>).</summary>
        public static IReadOnlyList<TraceEvent> StepErrors(IEnumerable<TraceEvent> events) =>
            events.Where(e => e.Kind == "error" && e.CauseSeq == null).OrderBy(e => e.Seq).ToList();

        /// <summary>Whether the test failed: the reported outcome, or for older traces, any step error.</summary>
        public static bool IsFailed(TraceMetadata meta, IEnumerable<TraceEvent> events) =>
            meta.Outcome != null ? meta.Outcome == "failed" : StepErrors(events).Count > 0;

        /// <summary>
        /// The step to look at first: where the test failed (the last step error; nested failures report their
        /// innermost step), or in a passed test the first recovered error. When the test failed outside any step,
        /// returns the last recorded step and sets <paramref name="failedOutsideSteps"/>.
        /// </summary>
        public static TraceEvent? FailureStep(TraceMetadata meta, IReadOnlyList<TraceEvent> events, out bool failedOutsideSteps)
        {
            var errors = StepErrors(events);
            var failed = IsFailed(meta, events);
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
