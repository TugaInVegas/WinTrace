# Writing an adapter

WinTrace.Core does not know about any automation tool. An adapter is a class that implements
`ITraceSource`:

```csharp
public interface ITraceSource
{
    byte[]? CaptureScreenshot();   // PNG bytes, or null if not available
    string? CaptureSnapshot();     // UI tree as text, or null
}
```

Both methods may throw: the recorder logs the failure in the trace and carries on, so a broken screenshot
never fails a test.

## Snapshot format

Any text works, but **XML** unlocks the tree view and the locator picker. Use one element per UI element,
named after its control type, with these attributes when you have them:

| Attribute | Used for |
|---|---|
| `Name`, `AutomationId`, `ClassName` | Labels in the tree, search, and suggested locators. |
| `x`, `y`, `width`, `height` (screen pixels) | Highlight boxes on the screenshot. `BoundingRectangle="x,y,w,h"` also works. |

This is the format WinAppDriver and NovaWindowsDriver return from `PageSource`.

## Example: FlaUI (sketch)

There is no FlaUI package yet. This sketch shows the shape of one; contributions are welcome.

```csharp
using System.Drawing.Imaging;
using System.Text;
using System.Xml;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

public sealed class FlaUITraceSource : ITraceSource
{
    readonly Func<AutomationElement> _root;
    public FlaUITraceSource(Func<AutomationElement> root) { _root = root; }

    public byte[]? CaptureScreenshot()
    {
        using var image = Capture.Element(_root());
        using var ms = new MemoryStream();
        image.Bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public string? CaptureSnapshot()
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
            Write(w, _root(), depth: 0);
        return sb.ToString();
    }

    static void Write(XmlWriter w, AutomationElement e, int depth)
    {
        w.WriteStartElement(e.ControlType.ToString());
        w.WriteAttributeString("Name", e.Properties.Name.ValueOrDefault ?? "");
        w.WriteAttributeString("AutomationId", e.Properties.AutomationId.ValueOrDefault ?? "");
        w.WriteAttributeString("ClassName", e.Properties.ClassName.ValueOrDefault ?? "");
        var r = e.BoundingRectangle;
        w.WriteAttributeString("x", r.X.ToString()); w.WriteAttributeString("y", r.Y.ToString());
        w.WriteAttributeString("width", r.Width.ToString()); w.WriteAttributeString("height", r.Height.ToString());
        if (depth < 30) foreach (var child in e.FindAllChildren()) Write(w, child, depth + 1);
        w.WriteEndElement();
    }
}
```

Use it like any other source:

```csharp
_trace = new TraceSession(TestContext.TestName!, new FlaUITraceSource(() => app.GetMainWindow(automation)));
_trace.Recorder.Step("click", "Validate", () => validateButton.Click());
```

## Packaging an adapter

Put tool-specific code in its own project (`WinTrace.<Tool>`) that references `WinTrace.Core`, so users of
other tools do not pull its dependencies. See [CONTRIBUTING.md](../CONTRIBUTING.md).
