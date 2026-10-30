using System;
using System.Collections.Generic;
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

        public byte[]? CaptureScreenshot() =>
            _driver() is ITakesScreenshot t ? t.GetScreenshot().AsByteArray : null;

        public string? CaptureSnapshot() => _driver()?.PageSource;
    }

    /// <summary>Traced wrappers for common element actions. Use the same call-sites you already have.</summary>
    public static class AppiumTraceExtensions
    {
        public static string Describe(this IWebElement e)
        {
            try { return $"{e.TagName} name='{e.GetAttribute("Name")}' id='{e.GetAttribute("AutomationId")}'"; }
            catch { return "<element>"; }
        }

        public static void Click(this TraceRecorder rec, IWebElement element, string? label = null) =>
            rec.Step("click", label ?? element.Describe(), element.Click);

        public static void SendKeys(this TraceRecorder rec, IWebElement element, string text, string? label = null) =>
            rec.Step("type", label ?? element.Describe(), () => element.SendKeys(text),
                new Dictionary<string, string> { ["text"] = text });

        public static IWebElement Find(this TraceRecorder rec, ISearchContext ctx, By by) =>
            rec.Step("find", by.ToString(), () => ctx.FindElement(by));
    }
}
