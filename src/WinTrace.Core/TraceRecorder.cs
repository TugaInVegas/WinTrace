using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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

        static volatile TraceRecorder? _current;

        readonly object _gate = new object();
        readonly TraceOptions _options;
        readonly ITraceSource? _source;
        readonly List<TraceEvent> _events = new List<TraceEvent>();
        // Content-addressed: identical screenshots (common between consecutive steps) are stored once.
        readonly Dictionary<string, byte[]> _resources = new Dictionary<string, byte[]>();
        readonly TraceMetadata _meta;
        readonly Dictionary<string, string> _videoFiles = new Dictionary<string, string>();
        readonly List<string> _groups = new List<string>();
        // Exceptions already captured by an inner step, so outer steps do not capture the same failure again.
        readonly ConditionalWeakTable<Exception, CapturedFailure> _captured = new ConditionalWeakTable<Exception, CapturedFailure>();
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

        /// <summary>
        /// The recorder of the running test, set by <see cref="TraceSession"/> (null outside a traced test), so static
        /// helpers can record steps without passing the recorder around. Assumes one traced test at a time, which is
        /// the case for desktop UI tests sharing one desktop.
        /// </summary>
        public static TraceRecorder? Current { get => _current; set => _current = value; }

        public bool HasErrors { get { lock (_gate) return _events.Any(e => e.Kind == "error"); } }

        public IReadOnlyList<TraceEvent> Events { get { lock (_gate) return _events.OrderBy(e => e.Seq).ToList(); } }

        /// <summary>
        /// Attach a screen recording (e.g. from your own ffmpeg recorder). <paramref name="startedAt"/> must be the
        /// instant the recording began so the viewer can seek to each step. Call it once per segment when the recorder
        /// writes several files; pass <paramref name="duration"/> when known so the viewer can show gaps.
        /// </summary>
        public void AttachVideo(string path, DateTimeOffset startedAt, TimeSpan? duration = null)
        {
            lock (_gate)
            {
                var name = "video/" + Path.GetFileName(path);
                for (var i = 2; _videoFiles.ContainsKey(name); i++)
                    name = "video/" + Path.GetFileNameWithoutExtension(path) + "-" + i + Path.GetExtension(path);
                _meta.Videos.Add(new TraceVideo { File = name, Started = startedAt, DurationSeconds = duration?.TotalSeconds });
                _videoFiles[name] = path;
            }
        }

        /// <summary>Record the test outcome (done by <see cref="TraceSession.Finish(bool, Action{string}, string)"/>).</summary>
        public void SetOutcome(bool failed, string? failureMessage = null)
        {
            lock (_gate)
            {
                _meta.Outcome = failed ? "failed" : "passed";
                _meta.FailureMessage = failureMessage;
            }
        }

        /// <summary>Label subsequent actions until the returned scope is disposed. Groups nest ("Login / Submit").</summary>
        public IDisposable Group(string name)
        {
            lock (_gate) _groups.Add(name);
            return new Scope(() => { lock (_gate) { var i = _groups.LastIndexOf(name); if (i >= 0) _groups.RemoveAt(i); } });
        }

        public void Log(string message) => Log(message, screenshot: false);

        /// <summary>A log line on the timeline, optionally with a screenshot: a cheap marker for frequent events.</summary>
        public void Log(string message, bool screenshot)
        {
            var ev = new TraceEvent { Kind = "log", Name = message, Start = DateTimeOffset.UtcNow };
            if (screenshot)
            {
                var sw = Stopwatch.StartNew();
                ev.ScreenshotAfter = TryScreenshot();
                ev.CaptureMs = sw.Elapsed.TotalMilliseconds;
            }
            lock (_gate)
            {
                ev.Seq = ++_seq;
                ev.Group = CurrentGroup();
                _events.Add(ev);
            }
        }

        /// <summary>Run <paramref name="body"/> as a traced step. Exceptions are recorded and rethrown.</summary>
        public T Step<T>(string name, string? target, Func<T> body, IDictionary<string, string>? parameters = null) =>
            Step(name, target, Capture.Default, body, parameters);

        public void Step(string name, string? target, Action body, IDictionary<string, string>? parameters = null) =>
            Step<object?>(name, target, Capture.Default, () => { body(); return null; }, parameters);

        public void Step(string name, string? target, Capture capture, Action body, IDictionary<string, string>? parameters = null) =>
            Step<object?>(name, target, capture, () => { body(); return null; }, parameters);

        /// <summary>Run <paramref name="body"/> as a traced step, choosing what to capture for this step only.</summary>
        public T Step<T>(string name, string? target, Capture capture, Func<T> body, IDictionary<string, string>? parameters = null)
        {
            var ev = new TraceEvent { Name = name, Target = target, Start = DateTimeOffset.UtcNow };
            lock (_gate) { ev.Seq = ++_seq; ev.Group = CurrentGroup(); }
            if (parameters != null)
            {
                var sensitiveTarget = IsSensitive(target);
                foreach (var kv in parameters)
                    ev.Params[kv.Key] = sensitiveTarget || IsSensitive(kv.Key) ? "***" : kv.Value;
            }

            var captureTime = new Stopwatch();
            if (capture != Capture.None && _options.ScreenshotBefore)
            {
                captureTime.Start();
                ev.ScreenshotBefore = TryScreenshot();
                captureTime.Stop();
            }

            Exception? firstFailure = null;
            var alreadyCaptured = false;
            var sw = Stopwatch.StartNew();
            try
            {
                return body();
            }
            catch (Exception ex)
            {
                ev.Kind = "error";
                ev.Error = ex.GetType().Name + ": " + ex.Message;
                var cause = FindCaptured(ex);
                if (cause != null)
                {
                    // The same failure, propagating from a nested step: reuse its capture instead of taking another.
                    alreadyCaptured = true;
                    ev.CauseSeq = cause.Seq;
                    ev.ScreenshotAfter = cause.ScreenshotAfter;
                }
                else
                {
                    ev.ErrorDetail = ex.ToString();
                    firstFailure = ex;
                }
                throw;
            }
            finally
            {
                sw.Stop();
                ev.DurationMs = sw.Elapsed.TotalMilliseconds;
                if (!alreadyCaptured && capture != Capture.None)
                {
                    captureTime.Start();
                    ev.ScreenshotAfter = TryScreenshot();
                    var wantSnapshot = capture == Capture.Full
                        || (capture == Capture.Default && (_options.Snapshots == SnapshotMode.Always
                            || (_options.Snapshots == SnapshotMode.OnError && ev.Kind == "error")));
                    if (wantSnapshot)
                    {
                        var snap = Stopwatch.StartNew();
                        ev.Snapshot = TrySnapshot();
                        ev.SnapshotMs = snap.Elapsed.TotalMilliseconds;
                    }
                    captureTime.Stop();
                }
                if (captureTime.ElapsedTicks > 0) ev.CaptureMs = captureTime.Elapsed.TotalMilliseconds;
                if (firstFailure != null)
                    lock (_gate) _captured.Add(firstFailure, new CapturedFailure(ev.Seq, ev.ScreenshotAfter));
                lock (_gate) _events.Add(ev);
            }
        }

        CapturedFailure? FindCaptured(Exception ex)
        {
            lock (_gate)
                for (var e = ex; e != null; e = e.InnerException)
                    if (_captured.TryGetValue(e, out var c)) return c;
            return null;
        }

        string? CurrentGroup() => _groups.Count == 0 ? null : string.Join(" / ", _groups);

        bool IsSensitive(string? text) =>
            text != null && _options.RedactParams.Any(p => text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

        string? TryScreenshot()
        {
            if (!_options.Screenshots || _source == null) return null;
            try
            {
                var image = _source.CaptureScreenshot();
                return image == null ? null : AddResource("resources", ImageExtension(image), image);
            }
            catch (Exception ex) { Log("screenshot failed: " + ex.Message); return null; }
        }

        // Sources may return PNG or a compressed format (see WinTrace.Appium ScreenshotCompression).
        static string ImageExtension(byte[] b) =>
            b.Length > 2 && b[0] == 0xFF && b[1] == 0xD8 ? ".jpg" : ".png";

        string? TrySnapshot()
        {
            if (_source == null) return null;
            try
            {
                string? s;
                if (_options.SnapshotTimeout is TimeSpan timeout)
                {
                    var task = Task.Run(() => _source.CaptureSnapshot());
                    if (!task.Wait(timeout))
                    {
                        task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        Log($"snapshot skipped: took longer than {timeout.TotalSeconds:0.#} s (TraceOptions.SnapshotTimeout)");
                        return null;
                    }
                    s = task.GetAwaiter().GetResult();
                }
                else s = _source.CaptureSnapshot();
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

        internal byte[]? GetResource(string id) { lock (_gate) return _resources.TryGetValue(id, out var b) ? b : null; }

        /// <summary>A copy of the trace metadata (title, start, outcome, videos...).</summary>
        public TraceMetadata GetMetadata()
        {
            lock (_gate) return JsonSerializer.Deserialize<TraceMetadata>(JsonSerializer.Serialize(_meta, Json), Json)!;
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
                        // Images are already compressed; deflating them again only costs time.
                        var image = kv.Key.EndsWith(".png") || kv.Key.EndsWith(".jpg");
                        var entry = zip.CreateEntry(kv.Key, image ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
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

        /// <summary>
        /// Write a self-contained HTML report (no server, no dependencies). Videos are embedded so the single file
        /// works as a CI attachment; pass <paramref name="embedVideo"/> false to write them next to the report instead.
        /// </summary>
        public string SaveHtml(string path, bool embedVideo = true) => ExportHtml(path, embedVideo)[0];

        /// <summary>Like <see cref="SaveHtml"/>, returning every file written (the HTML first, then any video copied next to it).</summary>
        public IReadOnlyList<string> ExportHtml(string path, bool embedVideo = true)
        {
            TraceMetadata meta;
            Dictionary<string, string> videos;
            lock (_gate)
            {
                _meta.Stopped = DateTimeOffset.UtcNow;
                meta = JsonSerializer.Deserialize<TraceMetadata>(JsonSerializer.Serialize(_meta, Json), Json)!;
                videos = new Dictionary<string, string>(_videoFiles);
            }
            return TraceHtmlExporter.Export(path, meta, Events, id => { lock (_gate) return _resources.TryGetValue(id, out var b) ? b : null; },
                videos, embedVideo);
        }

        static void Write(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false))) w.Write(text);
        }

        sealed class CapturedFailure
        {
            public CapturedFailure(int seq, string? screenshotAfter) { Seq = seq; ScreenshotAfter = screenshotAfter; }
            public int Seq { get; }
            public string? ScreenshotAfter { get; }
        }

        sealed class Scope : IDisposable
        {
            Action? _end;
            public Scope(Action end) { _end = end; }
            public void Dispose() { _end?.Invoke(); _end = null; }
        }
    }
}
