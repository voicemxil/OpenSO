using System;
using System.Runtime.InteropServices;

namespace FSO.Common.Utils
{
    /// <summary>
    /// Drives macOS-native (Spaces) fullscreen on the game window - the mechanism behind the green
    /// stoplight button. On macOS this is the ONLY fullscreen path the game uses: it is the one that
    /// places the frame below the camera housing and keeps the mouse origin on the content, and SDL
    /// fullscreen would otherwise coexist with it and stack. GraphicsDeviceManager knows nothing about
    /// it, so gdm.IsFullScreen stays false throughout on macOS.
    /// </summary>
    public static class MacWindowState
    {
        private const string LIBOBJC = "/usr/lib/libobjc.dylib";

        [DllImport(LIBOBJC, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetClass(string name);

        [DllImport(LIBOBJC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
        private static extern IntPtr Sel(string name);

        [DllImport(LIBOBJC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

        [DllImport(LIBOBJC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidArg(IntPtr receiver, IntPtr selector, IntPtr arg);

        private static IntPtr MainWindow()
        {
            var appClass = GetClass("NSApplication");
            if (appClass == IntPtr.Zero) return IntPtr.Zero;
            var app = SendIntPtr(appClass, Sel("sharedApplication"));
            if (app == IntPtr.Zero) return IntPtr.Zero;
            // mainWindow can legitimately be nil (e.g. while unfocused); callers treat that as "not
            // fullscreen" and fall back to the normal toggle path.
            return SendIntPtr(app, Sel("mainWindow"));
        }

        /// <summary>
        /// Ask the system to toggle native fullscreen ([mainWindow toggleFullScreen:]) - the programmatic
        /// equivalent of the green button. Returns false if there was no window to send it to.
        /// </summary>
        public static bool ToggleNativeFullScreen()
        {
            if (!OperatingSystem.IsMacOS()) return false;
            try
            {
                var win = MainWindow();
                if (win == IntPtr.Zero) return false;
                SendVoidArg(win, Sel("toggleFullScreen:"), IntPtr.Zero);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
