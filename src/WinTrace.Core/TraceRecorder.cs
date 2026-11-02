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
        LiveTraceWriter? _live;
        string? _lastScreenshot;

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
            if (_options.CaptureEnvironment)
                try { _meta.Environment = EnvironmentProbe.Capture(); }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
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
                _live?.WriteMeta(_meta);
                _live?.WriteVideos(_videoFiles);
            }
        }

        /// <summary>
        /// Merge a Playwright trace (the zip from <c>context.Tracing.StopAsync(new() { Path = ... })</c>) into this one:
        /// its actions, browser console errors and failed requests join the timeline as web events, interleaved with the
        /// desktop steps by time, and the original zip is kept so it opens in Playwright's trace viewer with DOM
        /// snapshots, network and console. Call it after tracing stopped and before the trace is saved. Never throws
        /// for an unreadable file: the problem is logged in the trace.
        /// </summary>
        public void ImportPlaywrightTrace(string path)
        {
            lock (_gate)
            {
                try
                {
                    PlaywrightTrace.Merge(_meta, _events, _resources, path, _options.RedactParams);
                    if (_events.Count > 0) _seq = Math.Max(_seq, _events.Max(e => e.Seq));
                    if (_live != null)
                    {
                        foreach (var kv in _resources) _live.WriteResource(kv.Key, kv.Value);
                        _live.RewriteEvents(_events);
                        _live.WriteMeta(_meta);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    Log($"Playwright trace not imported ({Path.GetFileName(path)}): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Keep a copy of the trace in <paramref name="directory"/> while the test runs (done by <see cref="TraceSession"/>),
        /// so it can be recovered if the test process ends before the trace is saved. See <see cref="TraceRecovery"/>.
        /// </summary>
        public void WriteLiveTo(string directory)
        {
            lock (_gate)
            {
                _live = new LiveTraceWriter(directory, Json);
                _live.WriteMeta(_meta);
                foreach (var kv in _resources) _live.WriteResource(kv.Key, kv.Value);
                _live.RewriteEvents(_events);
                _live.WriteVideos(_videoFiles);
            }
        }

        /// <summary>Stop the live copy and delete its folder (after the trace was saved, or nothing needed saving).</summary>
        public void DeleteLiveCopy()
        {
            lock (_gate) { _live?.Delete(); _live = null; }
        }

        /// <summary>
        /// Attach test context shown in the viewer, the .md and the run report: environment, data set, record ids...
        /// Values of keys matching <see cref="TraceOptions.RedactParams"/> are hidden.
        /// </summary>
        public void SetContext(string key, string value)
        {
            lock (_gate)
            {
                _meta.Context[key] = IsSensitive(key) ? "***" : value;
                _live?.WriteMeta(_meta);
            }
        }

        /// <summary>Record the test outcome (done by <see cref="TraceSession.Finish(bool, Action{string}, string)"/>).</summary>
        public void SetOutcome(bool failed, string? failureMessage = null)
        {
            lock (_gate)
            {
                _meta.Outcome = failed ? "failed" : "passed";
                _meta.FailureMessage = failureMessage;
                _live?.WriteMeta(_meta);
                if (_options.CaptureEnvironment && EnvironmentProbe.InputDesktopName() is string desk) _meta.Environment["desktopAtEnd"] = desk;
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
                _live?.AppendEvent(ev);
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
            var ev = new TraceEvent { Name = name, Target = target, Start = DateTimeOffset.UtcNow, Minor = capture == Capture.None ? true : (bool?)null };
            lock (_gate) { ev.Seq = ++_seq; ev.Group = CurrentGroup(); _live?.WriteCurrent(ev); }
            if (_options.CaptureCallSite) ev.Location = CallSite.Describe(_options.CallSiteDepth, _options.CallSiteSkip);
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

            // The screen before the step: this step's own screenshot, or the last one taken (to tell "nothing changed").
            string? screenBefore;
            lock (_gate) screenBefore = ev.ScreenshotBefore ?? _lastScreenshot;
            var diagnostics = _source as IStepDiagnostics;
            object? diagState = null;
            if (diagnostics != null && capture != Capture.None)
                try { diagState = diagnostics.BeginStep(); } catch (Exception ex) when (!(ex is OutOfMemoryException)) { diagnostics = null; }

            Exception? firstFailure = null, takeOver = null;
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
                if (cause != null && cause.HasCapture)
                {
                    // The same failure, propagating from a nested step: reuse its capture instead of taking another.
                    alreadyCaptured = true;
                    ev.CauseSeq = cause.Seq;
                    ev.ScreenshotAfter = cause.ScreenshotAfter;
                }
                else if (cause != null)
                {
                    // The nested step that failed captured nothing (e.g. Capture.None probe): this step captures for it.
                    ev.CauseSeq = cause.Seq;
                    takeOver = ex;
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
                if (diagnostics != null)
                    try
                    {
                        if (diagnostics.EndStep(diagState) is IDictionary<string, string> d && d.Count > 0)
                            foreach (var kv in d) (ev.Diagnostics ??= new Dictionary<string, string>())[kv.Key] = kv.Value;
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
                if (!alreadyCaptured && capture != Capture.None)
                {
                    captureTime.Start();
                    ev.ScreenshotAfter = TryScreenshot();
                    if (ev.ScreenshotAfter != null && screenBefore != null)
                        (ev.Diagnostics ??= new Dictionary<string, string>())["screen"] = ev.ScreenshotAfter == screenBefore ? "unchanged" : "changed";
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
                var registered = firstFailure ?? takeOver;
                if (registered != null)
                {
                    var hasCapture = ev.ScreenshotAfter != null || ev.Snapshot != null;
                    lock (_gate)
                    {
                        _captured.Remove(registered);
                        _captured.Add(registered, new CapturedFailure(ev.Seq, ev.ScreenshotAfter, hasCapture));
                    }
                }
                lock (_gate)
                {
                    _events.Add(ev);
                    _live?.AppendEvent(ev);
                    _live?.ClearCurrent();
                }
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
                if (image == null) return null;
                var id = AddResource("resources", ImageExtension(image), image);
                lock (_gate) _lastScreenshot = id;
                return id;
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
                if (!_resources.ContainsKey(path))
                {
                    _resources[path] = data;
                    _live?.WriteResource(path, data);
                }
            return path;
        }

        internal byte[]? GetResource(string id) { lock (_gate) return _resources.TryGetValue(id, out var b) ? b : null; }

        /// <summary>A copy of the trace metadata (title, start, outcome, videos...).</summary>
        public TraceMetadata GetMetadata()
        {
            lock (_gate) return JsonSerializer.Deserialize<TraceMetadata>(JsonSerializer.Serialize(_meta, Json), Json)!;
        }

        /// <summary>
        /// Write a light trace.zip with the metadata and steps only (no screenshots, UI trees, videos or Playwright traces),
        /// for comparing runs. Returns the path.
        /// </summary>
        public string SaveSummary(string path)
        {
            lock (_gate)
            {
                var meta = JsonSerializer.Deserialize<TraceMetadata>(JsonSerializer.Serialize(_meta, Json), Json)!;
                meta.Stopped = DateTimeOffset.UtcNow;
                meta.Videos.Clear();
                meta.PlaywrightTraces.Clear();
                var events = _events.Select(e =>
                {
                    var c = JsonSerializer.Deserialize<TraceEvent>(JsonSerializer.Serialize(e, Json), Json)!;
                    c.ScreenshotBefore = c.ScreenshotAfter = c.Snapshot = null;
                    return c;
                }).ToList();
                return TraceArchive.Write(path, meta, events, Array.Empty<KeyValuePair<string, byte[]>>());
            }
        }

        /// <summary>Write trace.zip (trace.json, events.ndjson, resources/, snapshots/, video/).</summary>
        public string Save(string path)
        {
            lock (_gate)
            {
                _meta.Stopped = DateTimeOffset.UtcNow;
                return TraceArchive.Write(path, _meta, _events, _resources, _videoFiles);
            }
        }

        /// <summary>
        /// Write a self-contained HTML report (no server, no dependencies). Videos are embedded so the single file
        /// works as a CI attachment; pass <paramref name="embedVideo"/> false to write them next to the report instead.
        /// </summary>
        public string SaveHtml(string path, bool embedVideo = true) => ExportHtml(path, embedVideo)[0];

        /// <summary>
        /// Like <see cref="SaveHtml"/>, choosing separately whether videos and Playwright traces are embedded; returns every
        /// file written (the HTML first, then anything copied next to it).
        /// </summary>
        public IReadOnlyList<string> ExportHtml(string path, bool embedVideo, bool embedPlaywrightTraces)
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
                videos, embedVideo, embedPlaywrightTraces);
        }

        /// <summary>Like <see cref="SaveHtml"/>, returning every file written (the HTML first, then any video copied next to it).</summary>
        public IReadOnlyList<string> ExportHtml(string path, bool embedVideo = true) => ExportHtml(path, embedVideo, true);

        sealed class CapturedFailure
        {
            public CapturedFailure(int seq, string? screenshotAfter, bool hasCapture) { Seq = seq; ScreenshotAfter = screenshotAfter; HasCapture = hasCapture; }
            public int Seq { get; }
            public string? ScreenshotAfter { get; }
            public bool HasCapture { get; }
        }

        sealed class Scope : IDisposable
        {
            Action? _end;
            public Scope(Action end) { _end = end; }
            public void Dispose() { _end?.Invoke(); _end = null; }
        }
    }
}
