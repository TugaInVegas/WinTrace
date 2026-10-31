using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace WinTrace
{
    /// <summary>
    /// What the desktop looked like to the test: screen, display scaling, remote or console session, and whether the
    /// input desktop was the normal one or the lock screen. Desktop UI automation fails in confusing ways (element not
    /// found, black screenshots) when the screen is locked or the session is not interactive.
    /// </summary>
    static class EnvironmentProbe
    {
        public static Dictionary<string, string> Capture()
        {
            var env = new Dictionary<string, string>
            {
                [".NET"] = RuntimeInformation.FrameworkDescription,
                ["interactive"] = Environment.UserInteractive ? "yes" : "no"
            };
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return env;
            Try(() =>
            {
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                if (EnumDisplaySettings(null, -1, ref mode) && mode.dmPelsWidth > 0)
                    env["screen"] = mode.dmPelsWidth + "x" + mode.dmPelsHeight;
            });
            Try(() => env["monitors"] = GetSystemMetrics(SM_CMONITORS).ToString());
            Try(() => env["session"] = GetSystemMetrics(SM_REMOTESESSION) != 0 ? "remote desktop" : "console");
            Try(() => { if (Dpi() is int dpi) env["scaling"] = (dpi * 100 / 96) + "%"; });
            Try(() => env["desktop"] = InputDesktop());
            return env;
        }

        /// <summary>Name of the desktop receiving input: "Default" normally, "Winlogon" on the lock screen.</summary>
        public static string? InputDesktopName() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? SafeInputDesktop() : null;

        static string? SafeInputDesktop()
        {
            try { return InputDesktop(); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { return null; }
        }

        static string InputDesktop()
        {
            var desk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
            if (desk == IntPtr.Zero) return "unavailable (locked or disconnected)";
            try
            {
                var buffer = new byte[256];
                return GetUserObjectInformation(desk, UOI_NAME, buffer, buffer.Length, out var needed)
                    ? Encoding.Unicode.GetString(buffer, 0, Math.Max(0, Math.Min(needed, buffer.Length) - 2)).TrimEnd('\0')
                    : "unknown";
            }
            finally { CloseDesktop(desk); }
        }

        // The applied DPI is what the user set, whatever this process's DPI awareness is.
        static int? Dpi()
        {
            foreach (var (key, value) in new[] { (@"Control Panel\Desktop\WindowMetrics", "AppliedDPI"), (@"Control Panel\Desktop", "LogPixels") })
            {
                uint size = 4;
                if (RegGetValue(HKEY_CURRENT_USER, key, value, RRF_RT_REG_DWORD, out _, out var data, ref size) == 0 && data > 0) return (int)data;
            }
            return null;
        }

        static void Try(Action a)
        {
            try { a(); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is MarshalDirectiveException) { }
        }

        const int SM_CMONITORS = 80, SM_REMOTESESSION = 0x1000, UOI_NAME = 2;
        const uint DESKTOP_READOBJECTS = 0x0001, RRF_RT_REG_DWORD = 0x10;
        static readonly IntPtr HKEY_CURRENT_USER = new IntPtr(unchecked((int)0x80000001));

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE mode);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetUserObjectInformation(IntPtr obj, int index, byte[] info, int length, out int needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegGetValue(IntPtr hkey, string subKey, string value, uint flags, out uint type, out uint data, ref uint size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
    }
}
