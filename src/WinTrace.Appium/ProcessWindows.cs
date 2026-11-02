using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WinTrace.Appium
{
    /// <summary>A visible top-level window of the app's process (popup, dropdown, dialog...).</summary>
    public sealed record ProcessWindowInfo(IntPtr Handle, string Title, string ClassName, int X, int Y, int Width, int Height);

    /// <summary>Win32 helpers for the app's own windows; never walks the desktop's UI Automation tree.</summary>
    static class ProcessWindows
    {
        /// <summary>Adds windows to the snapshot's root as <c>ProcessWindow</c> elements; the source is returned unchanged if it is not XML.</summary>
        public static string AppendTo(string source, IEnumerable<ProcessWindowInfo> windows)
        {
            var list = windows.ToList();
            if (list.Count == 0) return source;
            XDocument doc;
            try { doc = XDocument.Parse(source, LoadOptions.PreserveWhitespace); }
            catch (XmlException) { return source; }
            if (doc.Root == null) return source;
            foreach (var w in list)
                doc.Root.Add(new XElement("ProcessWindow",
                    new XAttribute("Name", w.Title),
                    new XAttribute("ClassName", w.ClassName),
                    new XAttribute("NativeWindowHandle", "0x" + w.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture)),
                    new XAttribute("x", w.X), new XAttribute("y", w.Y), new XAttribute("width", w.Width), new XAttribute("height", w.Height)));
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        public static int CpuPercent(TimeSpan cpu, TimeSpan elapsed, int cores) =>
            elapsed <= TimeSpan.Zero || cores <= 0 ? 0 : (int)Math.Round(Math.Min(100, Math.Max(0, cpu.TotalMilliseconds / (elapsed.TotalMilliseconds * cores) * 100)));

        /// <summary>WinAppDriver and Appium report the window handle as hex ("0x0005089C") or decimal.</summary>
        public static bool TryParseHandle(string? text, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            long value;
            var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                : long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            if (!ok || value == 0) return false;
            handle = new IntPtr(value);
            return true;
        }

        public static int ProcessOf(IntPtr window)
        {
            if (!OperatingSystem.IsWindows()) return 0;
            GetWindowThreadProcessId(window, out var pid);
            return (int)pid;
        }

        public static bool IsResponding(IntPtr window) => !OperatingSystem.IsWindows() || !IsHungAppWindow(window);

        /// <summary>Visible top-level windows of <paramref name="pid"/> with a size, except <paramref name="exclude"/>.</summary>
        public static List<ProcessWindowInfo> Visible(int pid, IntPtr exclude)
        {
            var result = new List<ProcessWindowInfo>();
            if (!OperatingSystem.IsWindows()) return result;
            EnumWindows((hwnd, _) =>
            {
                if (hwnd == exclude || !IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner != pid || !GetWindowRect(hwnd, out var r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
                var title = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                var cls = new StringBuilder(256);
                GetClassName(hwnd, cls, cls.Capacity);
                result.Add(new ProcessWindowInfo(hwnd, title.ToString(), cls.ToString(), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                return result.Count < 50;
            }, IntPtr.Zero);
            return result;
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    }
}
