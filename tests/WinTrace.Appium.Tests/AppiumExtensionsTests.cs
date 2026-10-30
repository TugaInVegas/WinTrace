using System;
using System.Collections.Generic;
using System.Reflection;
using OpenQA.Selenium;
using WinTrace;
using WinTrace.Appium;
using Xunit;

public class AppiumExtensionsTests
{
    /// <summary>Records every IWebElement/ISearchContext call so tests can assert on driver round-trips.</summary>
    public class Fake : DispatchProxy
    {
        public List<string> Calls = new List<string>();
        public object? Child;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            Calls.Add(m!.Name);
            return m.Name switch
            {
                "FindElement" => Child,
                "GetAttribute" => "attr",
                _ => m.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null
            };
        }

        public static (T proxy, Fake fake) Create<T>() where T : class
        {
            var p = Create<T, Fake>();
            return (p, (Fake)(object)p);
        }
    }

    [Fact]
    public void Found_element_is_labelled_by_locator_without_extra_driver_calls()
    {
        var (el, elFake) = Fake.Create<IWebElement>();
        var (ctx, ctxFake) = Fake.Create<ISearchContext>();
        ctxFake.Child = el;
        var rec = new TraceRecorder();

        var found = rec.Find(ctx, By.Name("Ok"));
        rec.Click(found);

        Assert.Equal(By.Name("Ok").ToString(), rec.Events[1].Target);
        Assert.Equal(new[] { "Click" }, elFake.Calls);
    }

    [Fact]
    public void Sensitive_text_is_redacted()
    {
        var (el, _) = Fake.Create<IWebElement>();
        var rec = new TraceRecorder();
        rec.SendKeys(el, "hunter2", "user field", sensitive: true);
        rec.SendKeys(el, "hunter2", "Password");
        Assert.All(rec.Events, e => Assert.DoesNotContain("hunter2", e.Params.Values));
    }

    [Fact]
    public void Unknown_element_falls_back_to_attributes()
    {
        var (el, elFake) = Fake.Create<IWebElement>();
        var rec = new TraceRecorder();
        rec.Click(el);
        Assert.Contains("GetAttribute", elFake.Calls);
        Assert.Contains("name='attr'", rec.Events[0].Target);
    }
}
