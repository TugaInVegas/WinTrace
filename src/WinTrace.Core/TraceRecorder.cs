using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinTrace
{
    /// <summary>Records actions with before/after screenshots and UI snapshots into a trace.zip.</summary>
    public sealed class TraceRecorder
    {
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        readonly object _gate = new object();
        readonly TraceOptions _options;
        readonly ITraceSource? _source;
        readonly List<TraceEvent> _events = new List<TraceEvent>();
        // Content-addressed: identical screenshots (common between consecutive steps) are stored once.
        readonly Dictionary<string, byte[]> _resources = new Dictionary<string, byte[]>();
        readonly TraceMetadata _meta;
        readonly Dictionary<string, string> _videoFiles = new Dictionary<string, string>();
        readonly List<string> _groups = new List<string>();
        int _seq;

        public TraceRecorder(ITraceSource? source = null, TraceOptions? options = null)
        {
            _source = source;
            _options = options ?? new TraceOptions();
            _meta = new TraceMetadata
            {
                Title = _options.Title,
                Framework = _options.Framework,
                Started = DateTimeOffset.UtcNow
            };
        }

        public bool HasErrors { get { lock (_gate) return _events.Any(e => e.Kind == "error"); } }

        public IReadOnlyList<TraceEvent> Events { get { lock (_gate) return _events.OrderBy(e => e.Seq).ToList(); } }

        /// <summary>
        /// Attach a screen recording (e.g. from your own ffmpeg ScreenRecorder). <paramref name="startedAt"/>
        /// must be the instant the recording began so the viewer can seek to each step.
        /// </summary>
        public void AttachVideo(string path, DateTimeOffset startedAt)
        {
            var name = "video/" + Path.GetFileName(path);
            lock (_gate)
            {
                _meta.Videos.Add(new TraceVideo { File = name, Started = startedAt });
                _videoFiles[name] = path;
            }
        }

        /// <summary>Label subsequent actions until the returned scope is disposed. Groups nest ("Login / Submit").</summary>
        public IDisposable Group(string name)
        {
            lock (_gate) _groups.Add(name);
            return new Scope(() => { lock (_gate) { var i = _groups.LastIndexOf(name); if (i >= 0) _groups.RemoveAt(i); } });
        }

        public void Log(string message)
        {
            lock (_gate)
                _events.Add(new TraceEvent { Seq = ++_seq, Kind = "log", Name = message, Start = DateTimeOffset.UtcNow, Group = CurrentGroup() });
        }

        /// <summary>Run <paramref name="body"/> as a traced step. Exceptions are recorded and rethrown.</summary>
        public T Step<T>(string name, string? target, Func<T> body, IDictionary<string, string>? parameters = null)
        {
            var ev = new TraceEvent { Name = name, Target = target, Start = DateTimeOffset.UtcNow };
            lock (_gate) { ev.Seq = ++_seq; ev.Group = CurrentGroup(); }
            if (parameters != null)
            {
                var sensitiveTarget = IsSensitive(target);
                foreach (var kv in parameters)
                    ev.Params[kv.Key] = sensitiveTarget || IsSensitive(kv.Key) ? "***" : kv.Value;
            }

            if (_options.ScreenshotBefore) ev.ScreenshotBefore = TryScreenshot();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return body();
            }
            catch (Exception ex)
            {
                ev.Kind = "error";
                ev.Error = ex.GetType().Name + ": " + ex.Message;
                ev.ErrorDetail = ex.ToString();
                throw;
            }
            finally
            {
                sw.Stop();
                ev.DurationMs = sw.Elapsed.TotalMilliseconds;
                ev.ScreenshotAfter = TryScreenshot();
                ev.Snapshot = TrySnapshot(ev.Error != null);
                lock (_gate) _events.Add(ev);
            }
        }

        public void Step(string name, string? target, Action body, IDictionary<string, string>? parameters = null) =>
            Step<object?>(name, target, () => { body(); return null; }, parameters);

        string? CurrentGroup() => _groups.Count == 0 ? null : string.Join(" / ", _groups);

        bool IsSensitive(string? text) =>
            text != null && _options.RedactParams.Any(p => text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

        string? TryScreenshot()
        {
            if (!_options.Screenshots || _source == null) return null;
            try
            {
                var png = _source.CaptureScreenshot();
                return png == null ? null : AddResource("resources", ".png", png);
            }
            catch (Exception ex) { Log("screenshot failed: " + ex.Message); return null; }
        }

        string? TrySnapshot(bool failed)
        {
            var mode = _options.Snapshots;
            if (_source == null || mode == SnapshotMode.Off || (mode == SnapshotMode.OnError && !failed)) return null;
            try
            {
                var s = _source.CaptureSnapshot();
                if (s == null) return null;
                var head = s.TrimStart();
                var ext = head.StartsWith("<") ? ".xml" : head.StartsWith("{") || head.StartsWith("[") ? ".json" : ".txt";
                return AddResource("snapshots", ext, Encoding.UTF8.GetBytes(s));
            }
            catch (Exception ex) { Log("snapshot failed: " + ex.Message); return null; }
        }

        string AddResource(string folder, string ext, byte[] data)
        {
            string hash;
            using (var sha = SHA1.Create()) hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            var path = $"{folder}/{hash}{ext}";
            lock (_gate)
                if (!_resources.ContainsKey(path)) _resources[path] = data;
            return path;
        }

        /// <summary>Write trace.zip (trace.json, events.ndjson, resources/, snapshots/, video/).</summary>
        public string Save(string path)
        {
            lock (_gate)
            {
                _meta.Stopped = DateTimeOffset.UtcNow;
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (var fs = File.Create(path))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    Write(zip, "trace.json", JsonSerializer.Serialize(_meta, Json));
                    var sb = new StringBuilder();
                    foreach (var e in _events.OrderBy(e => e.Seq)) sb.Append(JsonSerializer.Serialize(e, Json)).Append('\n');
                    Write(zip, "events.ndjson", sb.ToString());
                    foreach (var kv in _resources)
                    {
                        // PNG is already compressed; deflating it again only costs time.
                        var entry = zip.CreateEntry(kv.Key, kv.Key.EndsWith(".png") ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                        using (var s = entry.Open()) s.Write(kv.Value, 0, kv.Value.Length);
                    }
                    foreach (var kv in _videoFiles.Where(v => File.Exists(v.Value)))
                    {
                        var entry = zip.CreateEntry(kv.Key, CompressionLevel.NoCompression);
                        using (var s = entry.Open())
                        using (var f = new FileStream(kv.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            f.CopyTo(s);
                    }
                }
            }
            return path;
        }

        /// <summary>Write a self-contained HTML report (no server, no dependencies).</summary>
        public string SaveHtml(string path)
        {
            TraceMetadata meta;
            Dictionary<string, string> videos;
            lock (_gate)
            {
                _meta.Stopped = DateTimeOffset.UtcNow;
                meta = JsonSerializer.Deserialize<TraceMetadata>(JsonSerializer.Serialize(_meta, Json), Json)!;
                videos = new Dictionary<string, string>(_videoFiles);
            }
            return TraceHtmlExporter.Write(path, meta, Events, id => { lock (_gate) return _resources.TryGetValue(id, out var b) ? b : null; }, videos);
        }

        static void Write(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false))) w.Write(text);
        }

        sealed class Scope : IDisposable
        {
            Action? _end;
            public Scope(Action end) { _end = end; }
            public void Dispose() { _end?.Invoke(); _end = null; }
        }
    }
}
