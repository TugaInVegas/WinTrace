using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using OpenQA.Selenium;

namespace WinTrace.Appium
{
    /// <summary>Screenshot + UIA tree (PageSource) from any Appium/Selenium driver: WinAppDriver, NovaWindowsDriver...</summary>
    public sealed class AppiumTraceSource : ITraceSource
    {
        readonly Func<IWebDriver?> _driver;

        public AppiumTraceSource(IWebDriver driver) : this(() => driver) { }

        /// <summary>Lazy driver lookup, so sessions that are recreated or attached later are still traced.</summary>
        public AppiumTraceSource(Func<IWebDriver?> driver) { _driver = driver; }

        /// <summary>Optional: store smaller screenshots (JPEG and/or a maximum width). Null keeps the driver's PNG.</summary>
        public ScreenshotCompression? Compression { get; set; }

        public byte[]? CaptureScreenshot()
        {
            if (!(_driver() is ITakesScreenshot t)) return null;
            var png = t.GetScreenshot().AsByteArray;
            return Compression == null ? png : Compression.Apply(png);
        }

        public string? CaptureSnapshot() => _driver()?.PageSource;
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
