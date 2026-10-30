using System;
using System.Collections.Generic;
using System.IO;

namespace WinTrace
{
    /// <summary>
    /// One trace per test, independent of the test framework. Create it in setup and call
    /// <see cref="Finish"/> in teardown with the outcome:
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
        FfmpegScreenRecorder? _video;
        string? _videoPath;

        public TraceSession(string testName, ITraceSource? source, string outputDir = "Reports_Logs/Traces", TraceOptions? options = null)
        {
            _name = SafeName(testName);
            OutputDir = outputDir;
            options ??= new TraceOptions();
            if (string.IsNullOrEmpty(options.Title) || options.Title == "WinTrace") options.Title = testName;
            Recorder = new TraceRecorder(source, options);
        }

        public TraceRecorder Recorder { get; }
        public string OutputDir { get; }
        /// <summary>Keep traces only for failed tests (default), like Playwright's retain-on-failure.</summary>
        public bool SaveOnlyOnFailure { get; set; } = true;
        /// <summary>Also write the self-contained HTML report next to the zip (default true).</summary>
        public bool WriteHtml { get; set; } = true;

        /// <summary>Record the desktop with ffmpeg for this test. The file is kept only if the trace is saved.</summary>
        public void StartVideo(string ffmpegPath = "ffmpeg", int fps = 10)
        {
            if (_video != null) return;
            _videoPath = Path.Combine(OutputDir, $"{_name}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            _video = FfmpegScreenRecorder.Start(_videoPath, ffmpegPath, fps);
        }

        /// <summary>
        /// Stops any video and saves the trace when needed. <paramref name="attach"/> receives each written file
        /// (e.g. TestContext.AddResultFile) so it shows up in the test report. Returns the written files.
        /// </summary>
        public IReadOnlyList<string> Finish(bool testFailed, Action<string>? attach = null)
        {
            var failed = testFailed || Recorder.HasErrors;
            if (_video != null)
            {
                _video.Stop();
                Recorder.AttachVideo(_video.OutputPath, _video.StartedAt);
                _video = null;
            }

            var written = new List<string>();
            if (!SaveOnlyOnFailure || failed)
            {
                var stem = Path.Combine(OutputDir, $"{_name}_{DateTime.Now:yyyyMMdd_HHmmss}{(failed ? "_failed" : "")}");
                written.Add(Recorder.Save(stem + ".zip"));
                if (WriteHtml) written.Add(Recorder.SaveHtml(stem + ".html"));
                foreach (var f in written) attach?.Invoke(f);
            }
            // The video is inside the zip (and copied next to the HTML), so the raw recording is no longer needed.
            if (_videoPath != null) TryDelete(_videoPath);
            return written;
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
