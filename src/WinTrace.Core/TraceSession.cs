using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinTrace
{
    /// <summary>
    /// One trace per test, independent of the test framework. Create it in setup and call
    /// <see cref="Finish(bool, Action{string})"/> in teardown with the outcome:
    /// <code>
    /// // MSTest
    /// _trace = new TraceSession(TestContext.TestName!, new AppiumTraceSource(() => Driver));
    /// _trace.Finish(TestContext.CurrentTestOutcome != UnitTestOutcome.Passed, TestContext.AddResultFile);
    /// // NUnit
    /// _trace.Finish(TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed, p => TestContext.AddTestAttachment(p));
    /// </code>
    /// </summary>
    public sealed class TraceSession
    {
        readonly string _name;
        readonly string _stamp;

        /// <summary>
        /// Whether new sessions keep a live copy of the trace on disk while the test runs (default true), so a test process
        /// that dies, times out or is stopped still leaves a trace that <see cref="RecoverAbandoned"/> or
        /// <c>wintrace recover</c> can turn into a normal one. The copy is deleted by <see cref="Finish(bool, Action{string})"/>.
        /// </summary>
        public static bool LiveRecordingDefault { get; set; } = true;

        /// <summary>Folder of this session's live copy: &lt;OutputDir&gt;/.live/&lt;test&gt;_&lt;time&gt;.</summary>
        public string LiveDirectory => Path.Combine(OutputDir, ".live", $"{_name}_{_stamp}");

        /// <summary>
        /// Turns live copies left by tests that never reached <c>Finish</c> (process killed, timeout, run stopped) into
        /// <c>*_aborted</c> traces (zip, html, md) in <paramref name="outputDir"/>. Call it before a run starts (e.g. in
        /// AssemblyInitialize), when no test of this output folder is running. Returns the files written.
        /// </summary>
        public static IReadOnlyList<string> RecoverAbandoned(string outputDir = "TestResults/WinTrace")
        {
            var written = new List<string>();
            var live = Path.Combine(outputDir, ".live");
            if (!Directory.Exists(live)) return written;
            foreach (var dir in Directory.GetDirectories(live))
                try { written.AddRange(TraceRecovery.Recover(dir, outputDir)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.Json.JsonException || ex is InvalidDataException) { }
            return written;
        }
        FfmpegScreenRecorder? _video;
        string? _videoPath;

        public TraceSession(string testName, ITraceSource? source, string outputDir = "TestResults/WinTrace", TraceOptions? options = null)
        {
            _name = SafeName(testName);
            OutputDir = outputDir;
            options ??= new TraceOptions();
            if (string.IsNullOrEmpty(options.Title) || options.Title == "WinTrace") options.Title = testName;
            Recorder = new TraceRecorder(source, options);
            TraceRecorder.Current = Recorder;
            _stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (LiveRecordingDefault) Recorder.WriteLiveTo(LiveDirectory);
        }

        public TraceRecorder Recorder { get; }
        public string OutputDir { get; }
        /// <summary>Keep traces only for failed tests (default), like Playwright's retain-on-failure.</summary>
        public bool SaveOnlyOnFailure { get; set; } = true;
        /// <summary>Also write the self-contained HTML report next to the zip (default true).</summary>
        public bool WriteHtml { get; set; } = true;
        /// <summary>
        /// Put the video inside the HTML report (default true), so the single file works as a CI attachment.
        /// When false, the video is copied to <c>&lt;name&gt;_files/</c> and also passed to the attach callback.
        /// </summary>
        public bool EmbedVideoInHtml { get; set; } = true;
        /// <summary>
        /// Put Playwright traces merged with <see cref="TraceRecorder.ImportPlaywrightTrace"/> inside the HTML report
        /// (default true), so its "Open in Playwright Trace Viewer" button works from the single file.
        /// </summary>
        public bool EmbedPlaywrightTracesInHtml { get; set; } = true;
        /// <summary>
        /// Also write a Markdown summary (<c>.md</c>) and the screenshot taken at the failure next to the zip (default
        /// true). Text and images are what people skimming CI output and AI agents read most easily.
        /// </summary>
        public bool WriteMarkdown { get; set; } = true;
        /// <summary>
        /// Also save the trace of a passed test when one of its steps threw and the test recovered (default false).
        /// </summary>
        public bool SaveRecoveredErrors { get; set; }
        /// <summary>
        /// For a passed test whose trace is not saved, write a light <c>&lt;name&gt;_&lt;stamp&gt;_passed.zip</c> with the steps and
        /// their durations only (no screenshots, UI trees or video; a few KB). <c>wintrace report --history</c> uses these to
        /// show where a failed run first differs from the last passing one and which steps got slower. Default false. The
        /// file is returned by <see cref="Finish(bool, Action{string}, string)"/> but not passed to the attach callback.
        /// </summary>
        public bool SavePassedSummary { get; set; }

        /// <summary>Record the desktop with ffmpeg for this test. The file is kept only if the trace is saved.</summary>
        public void StartVideo(string ffmpegPath = "ffmpeg", int fps = 10)
        {
            if (_video != null) return;
            _videoPath = Path.Combine(OutputDir, $"{_name}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            _video = FfmpegScreenRecorder.Start(_videoPath, ffmpegPath, fps);
        }

        /// <summary>
        /// Stops any video, records the outcome and saves the trace when needed. <paramref name="attach"/> receives each
        /// written file (e.g. TestContext.AddResultFile) so it shows up in the test report. Returns the written files.
        /// </summary>
        public IReadOnlyList<string> Finish(bool testFailed, Action<string>? attach = null) => Finish(testFailed, attach, null);

        /// <summary>
        /// Like <see cref="Finish(bool, Action{string})"/>, with the failure message from the test framework, shown in the
        /// viewer when the test failed outside any recorded step.
        /// </summary>
        public IReadOnlyList<string> Finish(bool testFailed, Action<string>? attach, string? failureMessage)
        {
            if (TraceRecorder.Current == Recorder) TraceRecorder.Current = null;
            Recorder.SetOutcome(testFailed, testFailed ? failureMessage : null);
            var failed = testFailed;
            if (_video != null)
            {
                _video.Stop();
                Recorder.AttachVideo(_video.OutputPath, _video.StartedAt);
                _video = null;
            }

            var written = new List<string>();
            if (!SaveOnlyOnFailure || failed || (SaveRecoveredErrors && Recorder.HasErrors))
            {
                var stem = Path.Combine(OutputDir, $"{_name}_{_stamp}{(failed ? "_failed" : "")}");
                var zip = Recorder.Save(stem + ".zip");
                written.Add(zip);
                if (WriteHtml) written.AddRange(Recorder.ExportHtml(stem + ".html", EmbedVideoInHtml, EmbedPlaywrightTracesInHtml));
                if (WriteMarkdown) written.AddRange(WriteReport(stem, zip, WriteHtml ? stem + ".html" : null));
                foreach (var f in written) attach?.Invoke(f);
            }
            else if (SavePassedSummary && !failed)
                written.Add(Recorder.SaveSummary(Path.Combine(OutputDir, $"{_name}_{_stamp}_passed.zip")));
            Recorder.DeleteLiveCopy();
            try
            {
                var live = Path.Combine(OutputDir, ".live");
                if (Directory.Exists(live) && !Directory.EnumerateFileSystemEntries(live).Any()) Directory.Delete(live);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            // The video is inside the zip (and in or next to the HTML), so the raw recording is no longer needed.
            if (_videoPath != null) TryDelete(_videoPath);
            return written;
        }

        IEnumerable<string> WriteReport(string stem, string zip, string? html)
        {
            var events = Recorder.Events;
            var meta = Recorder.GetMetadata();
            var step = TraceAnalysis.FailureStep(meta, events, out _);
            var captured = step == null ? null : TraceAnalysis.CaptureFor(step, events);
            var imageId = captured?.ScreenshotAfter ?? captured?.ScreenshotBefore;
            var image = imageId == null ? null : Recorder.GetResource(imageId);
            string? imageFile = null;
            if (image != null && imageId != null)
            {
                imageFile = stem + Path.GetExtension(imageId);
                File.WriteAllBytes(imageFile, image);
            }

            var md = stem + ".md";
            File.WriteAllText(md, TraceReport.Markdown(meta, events, Recorder.GetResource,
                Path.GetFileName(zip), html == null ? null : Path.GetFileName(html), imageFile == null ? null : Path.GetFileName(imageFile)),
                new System.Text.UTF8Encoding(false));
            yield return md;
            if (imageFile != null) yield return imageFile;
        }

        static string SafeName(string name)
        {
            var s = string.Concat((name ?? "test").Split(Path.GetInvalidFileNameChars()));
            return s.Length == 0 ? "test" : s.Length > 80 ? s.Substring(0, 80) : s;
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
