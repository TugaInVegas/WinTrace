using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WinTrace
{
    /// <summary>
    /// Writes a trace to a folder while the test runs (metadata, one line per event, each resource once, the step in
    /// progress), so a test process that dies, times out or is stopped still leaves a trace behind. <see cref="TraceRecovery"/>
    /// turns such a folder into a normal trace. Write errors are ignored: recording never fails a test.
    /// </summary>
    sealed class LiveTraceWriter
    {
        public const string MetaFile = "trace.json", EventsFile = "events.ndjson", CurrentFile = "current.json", VideosFile = "videos.json";
        readonly string _dir;
        readonly JsonSerializerOptions _json;

        public LiveTraceWriter(string dir, JsonSerializerOptions json)
        {
            _dir = dir;
            _json = json;
            Try(() => Directory.CreateDirectory(dir));
        }

        public string Directory_ => _dir;

        public void WriteMeta(TraceMetadata meta) => Try(() => AtomicWrite(MetaFile, JsonSerializer.Serialize(meta, _json)));

        public void AppendEvent(TraceEvent ev) =>
            Try(() => File.AppendAllText(Path.Combine(_dir, EventsFile), JsonSerializer.Serialize(ev, _json) + "\n", new UTF8Encoding(false)));

        public void RewriteEvents(IEnumerable<TraceEvent> events)
        {
            var sb = new StringBuilder();
            foreach (var e in events.OrderBy(e => e.Seq)) sb.Append(JsonSerializer.Serialize(e, _json)).Append('\n');
            Try(() => AtomicWrite(EventsFile, sb.ToString()));
        }

        public void WriteResource(string path, byte[] data) => Try(() =>
        {
            var full = Path.Combine(_dir, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        });

        public void WriteVideos(IDictionary<string, string> videoFiles) =>
            Try(() => AtomicWrite(VideosFile, JsonSerializer.Serialize(videoFiles, _json)));

        /// <summary>The step that started and has not finished yet (read by <c>wintrace tail</c>).</summary>
        public void WriteCurrent(TraceEvent ev) => Try(() => AtomicWrite(CurrentFile,
            JsonSerializer.Serialize(new { ev.Seq, ev.Name, ev.Target, ev.Group, ev.Start, ev.Origin }, _json)));

        public void ClearCurrent() => Try(() => File.Delete(Path.Combine(_dir, CurrentFile)));

        public void Delete() => Try(() => { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); });

        void AtomicWrite(string name, string text)
        {
            var target = Path.Combine(_dir, name);
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
        }

        static void Try(Action a)
        {
            try { a(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }

    /// <summary>A live folder read back: what <c>wintrace tail</c> shows and <see cref="TraceRecovery"/> rebuilds.</summary>
    public sealed class LiveTraceState
    {
        public string Directory { get; set; } = "";
        public TraceMetadata Meta { get; set; } = new TraceMetadata();
        public List<TraceEvent> Events { get; set; } = new List<TraceEvent>();
        /// <summary>The step in progress, if any (name, target, group, start).</summary>
        public TraceEvent? Current { get; set; }
        public DateTime LastWriteUtc { get; set; }
    }

    /// <summary>Reads and recovers live trace folders (<c>&lt;output&gt;/.live/&lt;test&gt;_&lt;time&gt;</c>).</summary>
    public static class TraceRecovery
    {
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>Live folders under <paramref name="root"/> (recursively), newest activity first.</summary>
        public static IReadOnlyList<string> FindLiveFolders(string root)
        {
            if (!System.IO.Directory.Exists(root)) return Array.Empty<string>();
            var result = new List<string>();
            try
            {
                foreach (var live in System.IO.Directory.GetDirectories(root, ".live", SearchOption.AllDirectories))
                    result.AddRange(System.IO.Directory.GetDirectories(live).Where(d => File.Exists(Path.Combine(d, LiveTraceWriter.MetaFile))));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return result.OrderByDescending(LastWrite).ToList();
        }

        public static LiveTraceState Read(string liveDir)
        {
            var state = new LiveTraceState { Directory = liveDir, LastWriteUtc = LastWrite(liveDir) };
            var metaPath = Path.Combine(liveDir, LiveTraceWriter.MetaFile);
            if (File.Exists(metaPath)) state.Meta = JsonSerializer.Deserialize<TraceMetadata>(File.ReadAllText(metaPath), Json) ?? state.Meta;
            var eventsPath = Path.Combine(liveDir, LiveTraceWriter.EventsFile);
            if (File.Exists(eventsPath))
            {
                var bySeq = new Dictionary<int, TraceEvent>();
                foreach (var line in File.ReadAllLines(eventsPath))
                {
                    if (line.Length == 0) continue;
                    try { var e = JsonSerializer.Deserialize<TraceEvent>(line, Json); if (e != null) bySeq[e.Seq] = e; }
                    catch (JsonException) { }   // a line cut short by the crash
                }
                state.Events = bySeq.Values.OrderBy(e => e.Seq).ToList();
            }
            var currentPath = Path.Combine(liveDir, LiveTraceWriter.CurrentFile);
            if (File.Exists(currentPath))
                try { state.Current = JsonSerializer.Deserialize<TraceEvent>(File.ReadAllText(currentPath), Json); }
                catch (JsonException) { }
            return state;
        }

        /// <summary>
        /// Builds <c>&lt;test&gt;_&lt;time&gt;_aborted.zip</c> (plus .html and .md) in <paramref name="outputDir"/> from a live
        /// folder left by a test that never finished, then deletes the folder unless <paramref name="keep"/>. A step that was
        /// in progress is added as failed. Returns the files written.
        /// </summary>
        public static IReadOnlyList<string> Recover(string liveDir, string outputDir, bool keep = false)
        {
            var state = Read(liveDir);
            var meta = state.Meta;
            if (meta.Outcome == null)
            {
                meta.Outcome = "aborted";
                meta.FailureMessage ??= "The test did not finish: the test process ended, timed out or was stopped before the trace was saved. Recovered from the live trace.";
            }
            meta.Stopped ??= state.LastWriteUtc == default ? (DateTimeOffset?)null : new DateTimeOffset(state.LastWriteUtc, TimeSpan.Zero);
            var events = state.Events;
            if (state.Current != null && events.All(e => e.Seq != state.Current.Seq))
            {
                var c = state.Current;
                c.Kind = "error";
                c.Error = $"Unfinished: the test stopped during this step (started {c.Start.ToLocalTime():HH:mm:ss}; nothing was recorded after that)";
                c.DurationMs = Math.Max(0, (state.LastWriteUtc - c.Start.UtcDateTime).TotalMilliseconds);
                events.Add(c);
            }

            var resources = new Dictionary<string, byte[]>();
            foreach (var folder in new[] { "resources", "snapshots", "playwright" })
            {
                var dir = Path.Combine(liveDir, folder);
                if (!System.IO.Directory.Exists(dir)) continue;
                foreach (var f in System.IO.Directory.GetFiles(dir))
                    resources[folder + "/" + Path.GetFileName(f)] = File.ReadAllBytes(f);
            }
            var videos = new Dictionary<string, string>();
            var videosPath = Path.Combine(liveDir, LiveTraceWriter.VideosFile);
            if (File.Exists(videosPath))
                try { videos = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(videosPath), Json) ?? videos; }
                catch (JsonException) { }

            var name = Path.GetFileName(liveDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var stem = Path.Combine(outputDir, name + "_aborted");
            var written = new List<string> { TraceArchive.Write(stem + ".zip", meta, events, resources, videos) };
            byte[]? Res(string id) => resources.TryGetValue(id, out var b) ? b : null;
            written.AddRange(TraceHtmlExporter.Export(stem + ".html", meta, events, Res, videos, true, true));
            File.WriteAllText(stem + ".md", TraceReport.Markdown(meta, events, Res, Path.GetFileName(stem + ".zip"), Path.GetFileName(stem + ".html")), new UTF8Encoding(false));
            written.Add(stem + ".md");
            if (!keep)
                try { System.IO.Directory.Delete(liveDir, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return written;
        }

        static DateTime LastWrite(string dir)
        {
            try
            {
                var times = System.IO.Directory.GetFiles(dir).Select(File.GetLastWriteTimeUtc).ToList();
                return times.Count == 0 ? System.IO.Directory.GetLastWriteTimeUtc(dir) : times.Max();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return DateTime.MinValue; }
        }
    }
}
