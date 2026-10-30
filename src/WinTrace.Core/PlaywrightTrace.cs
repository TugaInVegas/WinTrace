using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinTrace
{
    /// <summary>Web actions, browser console errors and failed requests read from a Playwright trace.</summary>
    public sealed class PlaywrightImport
    {
        public List<TraceEvent> Events { get; } = new List<TraceEvent>();
        /// <summary>Screencast frames used as step screenshots, keyed like other WinTrace resources.</summary>
        public Dictionary<string, byte[]> Resources { get; } = new Dictionary<string, byte[]>();
        public string? PlaywrightVersion { get; set; }
        public int Actions { get; set; }
    }

    /// <summary>
    /// Reads Playwright trace files (the zip written by <c>context.Tracing.StopAsync(new() { Path = ... })</c>) and
    /// merges them into WinTrace traces, so web and desktop steps share one timeline. The original Playwright trace is
    /// kept whole, so DOM snapshots, network and console stay available in Playwright's own trace viewer.
    /// </summary>
    public static class PlaywrightTrace
    {
        // Calls that are plumbing rather than user actions.
        static readonly HashSet<string> SkippedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "newPage", "newContext", "launch", "launchPersistentContext", "connect", "connectOverCDP",
            "tracingStart", "tracingStartChunk", "tracingStop", "tracingStopChunk", "tracingGroup", "tracingGroupEnd",
            "waitForEventInfo", "harStart", "harExport"
        };

        const int MaxLogEvents = 1000;

        public static PlaywrightImport Read(string zipPath, ISet<string>? redact = null)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
                return Read(zip, redact);
        }

        public static PlaywrightImport Read(ZipArchive zip, ISet<string>? redact = null)
        {
            var result = new PlaywrightImport();
            var stacks = ReadStacks(zip);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".trace", StringComparison.OrdinalIgnoreCase)
                                                         && !e.FullName.EndsWith("test.trace", StringComparison.OrdinalIgnoreCase)))
            {
                var prefix = entry.FullName.Substring(0, entry.FullName.Length - ".trace".Length);
                var network = zip.GetEntry(prefix + ".network");
                ReadTraceFile(zip, entry, network, stacks, redact, result);
            }
            return result;
        }

        /// <summary>
        /// Merges a Playwright trace into WinTrace data: imported events are interleaved by time and every event is
        /// renumbered (cause references follow), screencast frames become resources, and the original zip is kept
        /// under <c>playwright/</c>. Returns the reference added to <paramref name="meta"/>.
        /// </summary>
        public static PlaywrightTraceRef Merge(TraceMetadata meta, List<TraceEvent> events, IDictionary<string, byte[]> resources,
            string playwrightZip, ISet<string>? redact = null)
        {
            var bytes = File.ReadAllBytes(playwrightZip);
            var name = "playwright/" + Path.GetFileName(playwrightZip);
            for (var i = 2; meta.PlaywrightTraces.Any(p => p.File == name); i++)
                name = "playwright/" + Path.GetFileNameWithoutExtension(playwrightZip) + "-" + i + Path.GetExtension(playwrightZip);
            var reference = new PlaywrightTraceRef { File = name };
            resources[name] = bytes;
            meta.PlaywrightTraces.Add(reference);

            PlaywrightImport import;
            using (var ms = new MemoryStream(bytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                import = Read(zip, redact);
            reference.PlaywrightVersion = import.PlaywrightVersion;
            reference.Actions = import.Actions;
            foreach (var kv in import.Resources) resources[kv.Key] = kv.Value;
            // Keep offsets positive when the browser part started before the WinTrace session.
            if (import.Events.Count > 0)
            {
                var earliest = import.Events.Min(e => e.Start);
                if (meta.Started == default || earliest < meta.Started) meta.Started = earliest;
            }

            // Interleave by start time; keep the original order for ties, existing events first.
            var ordered = events.Select((e, i) => (e, key: 0, i))
                .Concat(import.Events.Select((e, i) => (e, key: 1, i)))
                .OrderBy(x => x.e.Start).ThenBy(x => x.key).ThenBy(x => x.i)
                .Select(x => x.e).ToList();
            var renumber = new Dictionary<int, int>();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!import.Events.Contains(ordered[i])) renumber[ordered[i].Seq] = i + 1;
                ordered[i].Seq = i + 1;
            }
            foreach (var e in ordered)
                if (e.CauseSeq is int c && renumber.TryGetValue(c, out var n)) e.CauseSeq = n;
            events.Clear();
            events.AddRange(ordered);
            return reference;
        }

        sealed class Call
        {
            public string Id = "";
            public double Start, End = double.NaN;
            public string? Class, Method, ApiName, Title;
            public JsonElement Params;
            public string? ErrorName, ErrorMessage, ErrorStack;
            public readonly List<string> Log = new List<string>();
        }

        static void ReadTraceFile(ZipArchive zip, ZipArchiveEntry traceEntry, ZipArchiveEntry? networkEntry,
            Dictionary<string, string> stacks, ISet<string>? redact, PlaywrightImport result)
        {
            double wallTime = 0, monotonicTime = 0;
            var calls = new Dictionary<string, Call>();
            var order = new List<Call>();
            var frames = new List<(double Time, string File)>();
            var logs = new List<TraceEvent>();
            DateTimeOffset At(double monotonic) => DateTimeOffset.FromUnixTimeMilliseconds(0).AddMilliseconds(wallTime + (monotonic - monotonicTime));

            foreach (var line in Lines(traceEntry))
            {
                using var doc = ParseLine(line);
                if (doc == null) continue;
                var d = doc.RootElement;
                switch (Str(d, "type"))
                {
                    case "context-options":
                        wallTime = Num(d, "wallTime") ?? 0;
                        monotonicTime = Num(d, "monotonicTime") ?? 0;
                        result.PlaywrightVersion ??= Str(d, "playwrightVersion");
                        break;
                    case "before":
                    {
                        var c = new Call
                        {
                            Id = Str(d, "callId") ?? "", Start = Num(d, "startTime") ?? 0,
                            Class = Str(d, "class"), Method = Str(d, "method"), ApiName = Str(d, "apiName"), Title = Str(d, "title"),
                            Params = d.TryGetProperty("params", out var p) ? p.Clone() : default
                        };
                        if (c.Id.Length > 0 && !calls.ContainsKey(c.Id)) { calls[c.Id] = c; order.Add(c); }
                        break;
                    }
                    case "after":
                        if (calls.TryGetValue(Str(d, "callId") ?? "", out var call))
                        {
                            call.End = Num(d, "endTime") ?? call.Start;
                            if (d.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                            {
                                var inner = err.TryGetProperty("error", out var e2) && e2.ValueKind == JsonValueKind.Object ? e2 : err;
                                call.ErrorName = Str(inner, "name");
                                call.ErrorMessage = Str(inner, "message");
                                call.ErrorStack = Str(inner, "stack");
                            }
                        }
                        break;
                    case "log":
                        if (calls.TryGetValue(Str(d, "callId") ?? "", out var logged) && Str(d, "message") is string m) logged.Log.Add(m);
                        break;
                    case "screencast-frame":
                        if (Str(d, "sha1") is string sha) frames.Add((Num(d, "timestamp") ?? 0, "resources/" + sha));
                        else if (Str(d, "file") is string file) frames.Add((Num(d, "timestamp") ?? 0, file));
                        break;
                    case "console":
                    {
                        var type = Str(d, "messageType") ?? "log";
                        if (type != "error" && type != "warning" || logs.Count >= MaxLogEvents) break;
                        var url = d.TryGetProperty("location", out var loc) ? Str(loc, "url") : null;
                        logs.Add(new TraceEvent { Kind = "log", Origin = "web", Name = $"console.{type}: {Str(d, "text")}", Target = url, Start = At(Num(d, "time") ?? 0) });
                        break;
                    }
                    case "error":
                        if (logs.Count < MaxLogEvents)
                            logs.Add(new TraceEvent { Kind = "log", Origin = "web", Name = "page error: " + FirstLine(Str(d, "message")), Start = At(Num(d, "time") ?? monotonicTime) });
                        break;
                }
            }

            if (networkEntry != null)
                foreach (var line in Lines(networkEntry))
                {
                    if (logs.Count >= MaxLogEvents) break;
                    using var doc = ParseLine(line);
                    if (doc == null || Str(doc.RootElement, "type") != "resource-snapshot") continue;
                    if (!doc.RootElement.TryGetProperty("snapshot", out var s)) continue;
                    var req = s.TryGetProperty("request", out var r) ? r : default;
                    var res = s.TryGetProperty("response", out var rs) ? rs : default;
                    var status = res.ValueKind == JsonValueKind.Object ? (int)(Num(res, "status") ?? 0) : 0;
                    var failure = res.ValueKind == JsonValueKind.Object ? Str(res, "_failureText") : null;
                    if (status < 400 && failure == null && status != 0) continue;
                    if (status == 0 && failure == null) continue;
                    var method = req.ValueKind == JsonValueKind.Object ? Str(req, "method") : null;
                    var url = req.ValueKind == JsonValueKind.Object ? Str(req, "url") : null;
                    var when = Num(s, "_monotonicTime") is double mono ? At(mono)
                        : DateTimeOffset.TryParse(Str(s, "startedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : At(monotonicTime);
                    logs.Add(new TraceEvent
                    {
                        Kind = "log", Origin = "web", Start = when, Target = url,
                        Name = failure != null ? $"request failed ({failure}): {method} {url}" : $"HTTP {status} {method} {url}"
                    });
                }

            frames.Sort((a, b) => a.Time.CompareTo(b.Time));
            string? Frame(double time, bool after)
            {
                if (frames.Count == 0) return null;
                (double Time, string File)? pick = null;
                if (after) { foreach (var f in frames) if (f.Time >= time) { pick = f; break; } }
                if (pick == null) foreach (var f in frames) { if (f.Time <= time) pick = f; else break; }
                if (pick == null) return null;
                var entry = zip.GetEntry(pick.Value.File);
                if (entry == null) return null;
                byte[] bytes;
                using (var st = entry.Open()) using (var ms = new MemoryStream()) { st.CopyTo(ms); bytes = ms.ToArray(); }
                string hash;
                using (var sha = SHA1.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                var key = "resources/" + hash + (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg" : ".png");
                result.Resources[key] = bytes;
                return key;
            }

            foreach (var c in order)
            {
                if (c.Class == "Tracing" || (c.Method != null && SkippedMethods.Contains(c.Method))) continue;
                var end = double.IsNaN(c.End) ? c.Start : c.End;
                var target = PrettyTarget(c);
                var ev = new TraceEvent
                {
                    Origin = "web",
                    Name = Title(c),
                    Target = target,
                    Start = At(c.Start),
                    DurationMs = Math.Max(0, end - c.Start),
                    ScreenshotBefore = Frame(c.Start, after: false),
                    ScreenshotAfter = Frame(end, after: true),
                    Location = stacks.TryGetValue(c.Id, out var where) ? where : null
                };
                AddParams(c, ev, target, redact);
                if (c.ErrorMessage != null)
                {
                    ev.Kind = "error";
                    ev.Error = (c.ErrorName ?? "Error") + ": " + FirstLine(c.ErrorMessage);
                    var detail = new StringBuilder(c.ErrorMessage.TrimEnd());
                    if (c.ErrorStack != null && !c.ErrorMessage.Contains(c.ErrorStack)) detail.Append("\n").Append(c.ErrorStack.TrimEnd());
                    if (c.Log.Count > 0) detail.Append("\n\nCall log:\n").Append(string.Join("\n", c.Log.Select(l => "  - " + l.Trim())));
                    if (ev.Location != null) detail.Append("\n\nCalled from ").Append(ev.Location);
                    ev.ErrorDetail = detail.ToString();
                }
                result.Events.Add(ev);
                result.Actions++;
            }
            result.Events.AddRange(logs);
        }

        static string Title(Call c)
        {
            if (!string.IsNullOrEmpty(c.Title)) return c.Title!;
            if (!string.IsNullOrEmpty(c.ApiName)) return c.ApiName!;
            if (c.Method == "expect" && Param(c, "expression") is string expr) return "expect " + expr;
            return c.Method ?? "call";
        }

        // Playwright logs the readable locator ("waiting for getByTestId('name')"); prefer it to the internal selector.
        static string? PrettyTarget(Call c)
        {
            foreach (var l in c.Log)
            {
                var t = l.Trim();
                if (t.StartsWith("waiting for ", StringComparison.Ordinal) && (t.Contains("(") || t.Contains("locator")))
                {
                    var v = t.Substring("waiting for ".Length).Trim();
                    if (!v.StartsWith("element to ", StringComparison.Ordinal) && !v.StartsWith("navigation", StringComparison.Ordinal)
                        && !v.StartsWith("scheduled", StringComparison.Ordinal)) return v;
                }
            }
            return Param(c, "selector") ?? Param(c, "url");
        }

        static void AddParams(Call c, TraceEvent ev, string? target, ISet<string>? redact)
        {
            bool Sensitive(string? s) => s != null && redact != null && redact.Any(r => s.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0);
            var sensitiveTarget = Sensitive(target) || Sensitive(Param(c, "selector"));
            foreach (var key in new[] { "value", "text", "key", "url", "expectedText", "expectedValue" })
            {
                var v = Param(c, key);
                if (v == null || (key == "url" && v == target)) continue;
                ev.Params[key] = sensitiveTarget || Sensitive(key) ? "***" : v.Length > 500 ? v.Substring(0, 500) + "…" : v;
            }
        }

        static string? Param(Call c, string name)
        {
            if (c.Params.ValueKind != JsonValueKind.Object || !c.Params.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString()
                : v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False ? v.ToString()
                : null;
        }

        // trace.stacks: { files: [...], stacks: [[callNumber, [[fileIndex, line, column, function], ...]], ...] }
        static Dictionary<string, string> ReadStacks(ZipArchive zip)
        {
            var map = new Dictionary<string, string>();
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".stacks", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return map;
            try
            {
                using var s = entry.Open();
                using var doc = JsonDocument.Parse(s);
                var files = doc.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetString() ?? "").ToList();
                foreach (var item in doc.RootElement.GetProperty("stacks").EnumerateArray())
                {
                    var callNumber = item[0].ToString();
                    var top = item[1].EnumerateArray().FirstOrDefault();
                    if (top.ValueKind != JsonValueKind.Array) continue;
                    var fileIndex = top[0].GetInt32();
                    var file = fileIndex >= 0 && fileIndex < files.Count ? files[fileIndex] : "?";
                    map["call@" + callNumber] = Path.GetFileName(file.Replace('\\', '/')) + ":" + top[1].ToString();
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is KeyNotFoundException || ex is IndexOutOfRangeException || ex is FormatException) { }
            return map;
        }

        static IEnumerable<string> Lines(ZipArchiveEntry entry)
        {
            using var r = new StreamReader(entry.Open(), Encoding.UTF8);
            string? line;
            while ((line = r.ReadLine()) != null)
                if (line.Length > 0) yield return line;
        }

        static JsonDocument? ParseLine(string line)
        {
            try { return JsonDocument.Parse(line); }
            catch (JsonException) { return null; }
        }

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static double? Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;

        static string FirstLine(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var i = s!.IndexOf('\n');
            return (i < 0 ? s : s.Substring(0, i)).Trim();
        }
    }
}
