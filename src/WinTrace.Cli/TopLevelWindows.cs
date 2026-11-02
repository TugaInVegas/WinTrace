using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace WinTrace.Cli
{
    /// <summary>Finds visible top-level windows by title through Win32, much faster than the driver's desktop root.</summary>
    [SupportedOSPlatform("windows")]
    static class TopLevelWindows
    {
        /// <summary>The handle of the visible top-level window titled <paramref name="title"/> (exact, else containing it, ignoring case), or null.</summary>
        public static long? Find(string title)
        {
            var windows = new List<(IntPtr Handle, string Title)>();
            try
            {
                EnumWindows((h, _) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    var len = GetWindowTextLengthW(h);
                    if (len <= 0) return true;
                    var sb = new StringBuilder(len + 1);
                    GetWindowTextW(h, sb, sb.Capacity);
                    windows.Add((h, sb.ToString()));
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { return null; }
            return Pick(windows, title);
        }

        /// <summary>Exact title first, then the first (topmost) title containing the text, ignoring case.</summary>
        internal static long? Pick(IEnumerable<(IntPtr Handle, string Title)> windows, string title)
        {
            (IntPtr Handle, string Title)? contains = null;
            foreach (var w in windows)
            {
                if (string.Equals(w.Title, title, StringComparison.Ordinal)) return w.Handle.ToInt64();
                if (contains == null && w.Title.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) contains = w;
            }
            return contains?.Handle.ToInt64();
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLengthW(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
    }
}
