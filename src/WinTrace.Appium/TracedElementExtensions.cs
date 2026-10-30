using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenQA.Selenium;

namespace WinTrace.Appium
{
    /// <summary>
    /// Drop-in traced versions of common Appium calls, recorded on <see cref="TraceRecorder.Current"/> (the running
    /// <see cref="TraceSession"/>). Replace <c>element.Click()</c> with <c>element.ClickTraced()</c>,
    /// <c>driver.FindElement(by)</c> with <c>driver.FindElementTraced(by)</c>, and so on. Outside a traced test they
    /// just perform the call.
    /// </summary>
    public static class TracedElementExtensions
    {
        public static void ClickTraced(this IWebElement element, string? label = null)
        {
            if (TraceRecorder.Current is { } rec) rec.Click(element, label);
            else element.Click();
        }

        public static void ClearTraced(this IWebElement element, string? label = null)
        {
            if (TraceRecorder.Current is { } rec) rec.Clear(element, label);
            else element.Clear();
        }

        /// <summary>Types <paramref name="text"/>; hidden in the trace when sensitive or when the label/locator says "password".</summary>
        public static void SendKeysTraced(this IWebElement element, string text, string? label = null, bool sensitive = false)
        {
            if (TraceRecorder.Current is { } rec) rec.SendKeys(element, text, label, sensitive);
            else element.SendKeys(text);
        }

        public static IWebElement FindElementTraced(this ISearchContext context, By by) =>
            TraceRecorder.Current is { } rec ? rec.Find(context, by) : context.FindElement(by);

        /// <summary>Typed find, e.g. <c>driver.FindElementTraced&lt;WindowsElement&gt;(by)</c>.</summary>
        public static T FindElementTraced<T>(this ISearchContext context, By by) where T : IWebElement =>
            (T)context.FindElementTraced(by);

        public static ReadOnlyCollection<IWebElement> FindElementsTraced(this ISearchContext context, By by) =>
            TraceRecorder.Current is { } rec ? rec.FindAll(context, by) : context.FindElements(by);

        /// <summary>Any other call as a step: <c>driver.Traced("switch window", "Settings", () => ...)</c>.</summary>
        public static void Traced(this ISearchContext context, string name, string? target, Action action, Capture capture = Capture.Default)
        {
            if (TraceRecorder.Current is { } rec) rec.Step(name, target, capture, action);
            else action();
        }
    }
}
