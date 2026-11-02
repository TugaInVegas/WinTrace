using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenQA.Selenium;

namespace WinTrace.Appium
{
    /// <summary>
    /// Screenshot + UIA tree (PageSource) from any Appium/Selenium driver: WinAppDriver, NovaWindowsDriver... On Windows it
    /// also records, per step, whether the app window was responding and how busy its process was, and adds the other
    /// visible top-level windows of the same process (popups, dropdowns, dialogs) to the UI tree snapshot.
    /// </summary>
    public sealed class AppiumTraceSource : ITraceSource, IStepDiagnostics
    {
        readonly Func<IWebDriver?> _driver;
        (IWebDriver Driver, IntPtr Window, int Pid, long At)? _app;

        public AppiumTraceSource(IWebDriver driver) : this(() => driver) { }

        /// <summary>Lazy driver lookup, so sessions that are recreated or attached later are still traced.</summary>
        public AppiumTraceSource(Func<IWebDriver?> driver) { _driver = driver; }

        /// <summary>Optional: store smaller screenshots (JPEG and/or a maximum width). Null keeps the driver's PNG.</summary>
        public ScreenshotCompression? Compression { get; set; }

        /// <summary>Record per step whether the app window responded and the CPU use of its process (Windows; default true).</summary>
        public bool CollectAppDiagnostics { get; set; } = true;

        /// <summary>
        /// Add the other visible top-level windows of the app's process (popups, dropdowns, dialogs, which are separate
        /// windows) to the UI tree snapshot as <c>ProcessWindow</c> elements (Windows; default true).
        /// </summary>
        public bool IncludeProcessWindows { get; set; } = true;

        public byte[]? CaptureScreenshot()
        {
            if (!(_driver() is ITakesScreenshot t)) return null;
            var png = t.GetScreenshot().AsByteArray;
            return Compression == null ? png : Compression.Apply(png);
        }

        public string? CaptureSnapshot()
        {
            var source = _driver()?.PageSource;
            if (source == null || !IncludeProcessWindows || !OperatingSystem.IsWindows()) return source;
            var app = AppWindow();
            if (app == null) return source;
            return ProcessWindows.AppendTo(source, ProcessWindows.Visible(app.Value.Pid, app.Value.Window));
        }

        object? IStepDiagnostics.BeginStep()
        {
            if (!CollectAppDiagnostics || !OperatingSystem.IsWindows()) return null;
            var app = AppWindow();
            if (app == null) return null;
            try
            {
                using var p = Process.GetProcessById(app.Value.Pid);
                return new StepState(app.Value.Window, app.Value.Pid, p.TotalProcessorTime, Stopwatch.GetTimestamp());
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return null; }
        }

        IDictionary<string, string>? IStepDiagnostics.EndStep(object? state)
        {
            if (!(state is StepState s) || !OperatingSystem.IsWindows()) return null;
            var result = new Dictionary<string, string>();
            try
            {
                using var p = Process.GetProcessById(s.Pid);
                var elapsed = Stopwatch.GetElapsedTime(s.Started);
                result["appCpu"] = ProcessWindows.CpuPercent(p.TotalProcessorTime - s.Cpu, elapsed, Environment.ProcessorCount) + "%";
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                result["appRunning"] = "no";   // the process exited during the step
                return result;
            }
            result["appResponding"] = ProcessWindows.IsResponding(s.Window) ? "yes" : "no";
            return result;
        }

        // The window the session is attached to and its process, re-read when the session changes or every few seconds.
        (IntPtr Window, int Pid)? AppWindow()
        {
            var driver = _driver();
            if (driver == null) return null;
            var now = Stopwatch.GetTimestamp();
            if (_app is { } a && ReferenceEquals(a.Driver, driver) && Stopwatch.GetElapsedTime(a.At, now) < TimeSpan.FromSeconds(5))
                return a.Pid == 0 ? null : (a.Window, a.Pid);
            IntPtr hwnd = IntPtr.Zero;
            int pid = 0;
            try
            {
                var handle = driver.CurrentWindowHandle;
                if (ProcessWindows.TryParseHandle(handle, out hwnd)) pid = ProcessWindows.ProcessOf(hwnd);
            }
            catch (WebDriverException) { }
            _app = (driver, hwnd, pid, now);
            return pid == 0 ? null : (hwnd, pid);
        }

        sealed record StepState(IntPtr Window, int Pid, TimeSpan Cpu, long Started);
    }

    /// <summary>
    /// Traced wrappers for common element actions. Elements found through <see cref="Find"/> / <see cref="FindAll"/>
    /// remember their locator, so later steps are labelled without extra driver round-trips.
    /// </summary>
    public static class AppiumTraceExtensions
    {
        static readonly ConditionalWeakTable<IWebElement, string> Locators = new ConditionalWeakTable<IWebElement, string>();

        /// <summary>
        /// Label for an element: its locator when found through WinTrace, otherwise Name/AutomationId read from the
        /// driver (two extra calls; pass a label to avoid them).
        /// </summary>
        public static string Describe(this IWebElement e)
        {
            if (Locators.TryGetValue(e, out var locator)) return locator;
            try { return $"name='{e.GetAttribute("Name")}' id='{e.GetAttribute("AutomationId")}'"; }
            catch (WebDriverException) { return "<element>"; }
        }

        public static void Click(this TraceRecorder rec, IWebElement element, string? label = null) =>
            rec.Step("click", label ?? element.Describe(), element.Click);

        public static void Clear(this TraceRecorder rec, IWebElement element, string? label = null) =>
            rec.Step("clear", label ?? element.Describe(), element.Clear);

        /// <summary>
        /// Types <paramref name="text"/>. The value is hidden in the trace when <paramref name="sensitive"/> is true or
        /// when the label/locator matches <see cref="TraceOptions.RedactParams"/> (e.g. contains "password").
        /// </summary>
        public static void SendKeys(this TraceRecorder rec, IWebElement element, string text, string? label = null, bool sensitive = false) =>
            rec.Step("type", label ?? element.Describe(), () => element.SendKeys(text),
                new Dictionary<string, string> { [sensitive ? "password" : "text"] = text });

        public static IWebElement Find(this TraceRecorder rec, ISearchContext ctx, By by)
        {
            var e = rec.Step("find", by.ToString(), () => ctx.FindElement(by));
            Remember(e, by);
            return e;
        }

        public static ReadOnlyCollection<IWebElement> FindAll(this TraceRecorder rec, ISearchContext ctx, By by)
        {
            var all = rec.Step("find all", by.ToString(), () => ctx.FindElements(by));
            for (var i = 0; i < all.Count; i++) Remember(all[i], by, i);
            rec.Log($"{by} matched {all.Count} element(s)");
            return all;
        }

        static void Remember(IWebElement e, By by, int? index = null)
        {
            var label = index == null ? by.ToString() : $"{by}[{index}]";
            Locators.AddOrUpdate(e, label);
        }
    }
}
