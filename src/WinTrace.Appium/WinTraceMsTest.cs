using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinTrace.Appium
{
    /// <summary>
    /// MSTest helper: start in TestInitialize, call Finish in TestCleanup.
    /// Saves trace.zip + trace.html (only on failure by default) and attaches them to the .trx.
    /// </summary>
    public sealed class WinTraceSession
    {
        readonly TestContext _ctx;
        readonly string _dir;
        readonly bool _onFailureOnly;
        public TraceRecorder Recorder { get; }

        public WinTraceSession(TestContext ctx, ITraceSource source, string outputDir = "TestResults/WinTrace",
            bool onFailureOnly = true, TraceOptions? options = null)
        {
            _ctx = ctx;
            _dir = outputDir;
            _onFailureOnly = onFailureOnly;
            options ??= new TraceOptions();
            if (string.IsNullOrEmpty(options.Title) || options.Title == "WinTrace") options.Title = ctx.TestName ?? "test";
            Recorder = new TraceRecorder(source, options);
        }

        public void Finish()
        {
            var failed = _ctx.CurrentTestOutcome != UnitTestOutcome.Passed || Recorder.HasErrors;
            if (_onFailureOnly && !failed) return;
            var name = string.Concat((_ctx.TestName ?? "test").Split(Path.GetInvalidFileNameChars()));
            var stem = Path.Combine(_dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{(failed ? "_failed" : "")}");
            _ctx.AddResultFile(Recorder.Save(stem + ".zip"));
            _ctx.AddResultFile(Recorder.SaveHtml(stem + ".html"));
        }
    }
}
