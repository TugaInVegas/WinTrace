using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WinTrace
{
    /// <summary>Records actions with before/after screenshots and UI snapshots into a trace.zip.</summary>
    public sealed class TraceRecorder : IDisposable
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
        readonly Dictionary<string, byte[]> _resources = new Dictionary<string, byte[]>();
        readonly TraceMetadata _meta;
        string? _group;
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

        public IReadOnlyList<TraceEvent> Events { get { lock (_gate) return _events.ToList(); } }

        /// <summary>Label subsequent actions until the returned scope is disposed.</summary>
        public IDisposable Group(string name)
        {
            lock (_gate) _group = name;
            return new Scope(() => { lock (_gate) _group = null; });
        }

        public void Log(string message)
        {
            lock (_gate)
                _events.Add(new TraceEvent { Seq = ++_seq, Kind = "log", Name = message, Start = DateTimeOffset.UtcNow, Group = _group });
        }

        /// <summary>Run <paramref name="body"/> as a traced step. Exceptions are recorded and rethrown.</summary>
        public T Step<T>(string name, string? target, Func<T> body, IDictionary<string, string>? parameters = null)
        {
            var ev = new TraceEvent { Name = name, Target = target, Start = DateTimeOffset.UtcNow };
            lock (_gate) { ev.Seq = ++_seq; ev.Group = _group; }
            if (parameters != null)
                foreach (var kv in parameters) ev.Params[kv.Key] = Redact(kv.Key, kv.Value);

            ev.ScreenshotBefore = TryScreenshot(ev.Seq, "before");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return body();
            }
            catch (Exception ex)
            {
                ev.Kind = "error";
                ev.Error = ex.GetType().Name + ": " + ex.Message;
                throw;
            }
            finally
            {
                sw.Stop();
                ev.DurationMs = sw.Elapsed.TotalMilliseconds;
                ev.ScreenshotAfter = TryScreenshot(ev.Seq, "after");
                ev.Snapshot = TrySnapshot(ev.Seq, ev.Error != null);
                lock (_gate) _events.Add(ev);
            }
        }

        public void Step(string name, string? target, Action body, IDictionary<string, string>? parameters = null) =>
            Step<object?>(name, target, () => { body(); return null; }, parameters);

        string Redact(string key, string value) =>
            _options.RedactParams.Any(p => key.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) ? "***" : value;

        string? TryScreenshot(int seq, string phase)
        {
            if (!_options.Screenshots || _source == null) return null;
            try
            {
                var png = _source.CaptureScreenshot();
                if (png == null) return null;
                var path = $"resources/{seq:D4}-{phase}.png";
                lock (_gate) _resources[path] = png;
                return path;
            }
            catch (Exception ex) { Log("screenshot failed: " + ex.Message); return null; }
        }

        string? TrySnapshot(int seq, bool failed)
        {
            var mode = _options.Snapshots;
            if (_source == null || mode == SnapshotMode.Off || (mode == SnapshotMode.OnError && !failed)) return null;
            try
            {
                var s = _source.CaptureSnapshot();
                if (s == null) return null;
                var path = $"snapshots/{seq:D4}.txt";
                lock (_gate) _resources[path] = Encoding.UTF8.GetBytes(s);
                return path;
            }
            catch (Exception ex) { Log("snapshot failed: " + ex.Message); return null; }
        }

        /// <summary>Write trace.zip (trace.json, events.ndjson, resources/, snapshots/).</summary>
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
                        var entry = zip.CreateEntry(kv.Key, CompressionLevel.Fastest);
                        using (var s = entry.Open()) s.Write(kv.Value, 0, kv.Value.Length);
                    }
                }
            }
            return path;
        }

        /// <summary>Write a self-contained HTML report (no server, no dependencies).</summary>
        public string SaveHtml(string path)
        {
            _meta.Stopped = DateTimeOffset.UtcNow;
            return TraceHtmlExporter.Write(path, _meta, Events, id => { lock (_gate) return _resources.TryGetValue(id, out var b) ? b : null; });
        }

        static void Write(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false))) w.Write(text);
        }

        public void Dispose() { }

        sealed class Scope : IDisposable
        {
            readonly Action _end;
            public Scope(Action end) { _end = end; }
            public void Dispose() => _end();
        }
    }
}
