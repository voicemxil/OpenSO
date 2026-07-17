using FSO.Common;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FSO.Client.Utils
{
    /// <summary>
    /// Detects the OS display scale so HiDPI screens (eg. 4k at 200%) automatically get a matching
    /// UI scale. The Windows entry points opt into PerMonitorV2 DPI awareness, so the OS hands the
    /// game raw pixels and a 1x UI renders tiny - read the system scale back and match it. macOS
    /// sizes windows in points (the OS applies the Retina scale itself) and Wayland compositors
    /// scale clients, so those stay at 1x; X11 users can hint with GDK_SCALE / QT_SCALE_FACTOR.
    /// </summary>
    public static class DPIScaleDetect
    {
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        /// <summary>
        /// Raw OS scale factor (1 = 96dpi). 1 on platforms that scale for us, or on failure.
        /// Pass the game window's HWND when it exists so the scale follows the monitor the
        /// window is actually on (ignored on other platforms / non-HWND handles).
        /// </summary>
        public static float GetScale(IntPtr hwnd = default)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Live effective DPI of the window's monitor, falling back to the primary
                    // monitor's. GetDpiForSystem only reports the sign-in value (stale if the
                    // user changed display scale since), and in a non-DPI-aware process the
                    // monitor queries virtualize to 96 - also correct, because the OS is
                    // already stretching our window there.
                    try
                    {
                        if (hwnd != default)
                        {
                            var dpi = GetDpiForWindow(hwnd);
                            if (dpi > 0) return dpi / 96f;
                        }
                        var monitor = MonitorFromPoint(new POINT(), 1); //MONITOR_DEFAULTTOPRIMARY
                        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0 /*MDT_EFFECTIVE_DPI*/, out var dpiX, out _) == 0 && dpiX > 0)
                            return dpiX / 96f;
                    }
                    catch { }
                    return GetDpiForSystem() / 96f;
                }
                if (OperatingSystem.IsLinux())
                {
                    //no reliable query before the window exists - honour the common env hints.
                    var hint = Environment.GetEnvironmentVariable("GDK_SCALE")
                        ?? Environment.GetEnvironmentVariable("QT_SCALE_FACTOR");
                    if (hint != null && float.TryParse(hint, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                        && scale > 0 && scale < 8)
                        return scale;
                }
            }
            catch { }
            return 1f;
        }

        /// <summary>
        /// Detected scale snapped to the DPI slider's quarter steps, capped so the game's 1024x768
        /// design resolution still fits on the current display.
        /// </summary>
        public static float GetSnappedScale()
        {
            var display = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            var scale = (float)Math.Round(GetScale() * 4) / 4f;
            var maxScale = (float)Math.Floor(Math.Min(display.Width / 1024f, display.Height / 768f) * 4) / 4f;
            return Math.Clamp(scale, 1f, Math.Max(1f, maxScale));
        }

        /// <summary>
        /// Scale to use for a live window: the detected OS scale, stepped down in quarter tiers
        /// while the client area is too small to fit the 1024x768 design resolution at it.
        /// </summary>
        public static float GetScaleForWindow(IntPtr hwnd, int clientWidth, int clientHeight)
        {
            var scale = (float)Math.Round(GetScale(hwnd) * 4) / 4f;
            var maxScale = (float)Math.Floor(Math.Min(clientWidth / 1024f, clientHeight / 768f) * 4) / 4f;
            return Math.Clamp(scale, 1f, Math.Max(1f, maxScale));
        }

        /// <summary>
        /// Startup auto-DPI: when enabled and the detected scale differs from the saved one, apply it,
        /// keeping the window's physical size by rescaling the UI-unit resolution - the same trade the
        /// in-game DPI slider makes. Must run before TSOGame sizes the backbuffer.
        /// </summary>
        public static void Startup(GlobalSettings settings)
        {
            //mobile manages its own scale; macOS windows are in points so 1x is already correct.
            if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) return;
            if (settings.AutoDPI != 1) return;
            var scale = GetSnappedScale();
            if (scale == settings.DPIScaleFactor) return;
            var width = (int)(settings.GraphicsWidth * settings.DPIScaleFactor / scale);
            var height = (int)(settings.GraphicsHeight * settings.DPIScaleFactor / scale);
            //floor at the design resolution: the scale cap above guarantees it fits the display.
            settings.GraphicsWidth = Math.Max(1024, width);
            settings.GraphicsHeight = Math.Max(768, height);
            settings.DPIScaleFactor = scale;
            settings.Save();
        }
    }
}
