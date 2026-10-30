using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using OpenQA.Selenium;
using WinTrace;
using WinTrace.Appium;
using Xunit;

public class TracedExtensionsTests
{
    [Fact]
    public void Traced_calls_record_on_the_current_recorder_and_just_run_otherwise()
    {
        var (el, elFake) = AppiumExtensionsTests.Fake.Create<IWebElement>();
        var (ctx, ctxFake) = AppiumExtensionsTests.Fake.Create<ISearchContext>();
        ctxFake.Child = el;

        var rec = new TraceRecorder();
        TraceRecorder.Current = rec;
        try
        {
            ctx.FindElementTraced(By.Name("Save")).ClickTraced();
            el.SendKeysTraced("hunter2", "Password box");
            ctx.Traced("custom", "x", () => { }, Capture.None);
        }
        finally { TraceRecorder.Current = null; }

        Assert.Equal(new[] { "find", "click", "type", "custom" }, rec.Events.Select(e => e.Name).ToArray());
        Assert.Equal(By.Name("Save").ToString(), rec.Events[1].Target);
        Assert.Equal("***", rec.Events[2].Params.Values.Single());

        elFake.Calls.Clear();
        el.ClickTraced();
        Assert.Equal(new[] { "Click" }, elFake.Calls);
        Assert.Equal(4, rec.Events.Count);
    }
}

public class ScreenshotCompressionTests
{
    static byte[] Png(int width, int height)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return Array.Empty<byte>();
        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            for (var x = 0; x < width; x += 40) g.FillRectangle(Brushes.SteelBlue, x, 10, 20, height - 20);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [Fact]
    public void Jpeg_with_max_width_shrinks_and_reencodes()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return; // System.Drawing runs on Windows only; CI runs on Windows.
        var png = Png(2560, 1440);
        var jpg = ScreenshotCompression.ToJpeg(75, 1280).Apply(png);
        Assert.Equal(0xFF, jpg[0]);
        Assert.Equal(0xD8, jpg[1]);
        using var img = Image.FromStream(new MemoryStream(jpg));
        Assert.Equal(1280, img.Width);
        Assert.Equal(720, img.Height);
    }

    [Fact]
    public void Smaller_images_and_invalid_input_are_left_alone()
    {
        var garbage = new byte[] { 1, 2, 3 };
        Assert.Same(garbage, ScreenshotCompression.ScaleDown(800).Apply(garbage));
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        var small = Png(400, 300);
        Assert.Same(small, ScreenshotCompression.ScaleDown(800).Apply(small));
    }

    [Fact]
    public void Arguments_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotCompression.ToJpeg(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotCompression.ScaleDown(5));
    }
}
