using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FSO.Client.Utils
{
    /// <summary>
    /// macOS Retina support. MonoGame DesktopGL creates its SDL window without ALLOW_HIGHDPI, so the
    /// GL drawable is capped at point resolution and upscaled (blurry) - no backbuffer size fixes it.
    /// This flips the NSView back to a best-resolution surface and reports the scale so the backbuffer
    /// can be sized to real pixels. Everything is best-effort: any failure leaves the old behaviour.
    /// </summary>
    internal static class MacRetina
    {
        // MonoGame ships SDL as "libSDL2-2.0.0.dylib"; DllImport("SDL2") never resolves that name
        // (DllNotFoundException, swallowed). Bind the exact filename - only ever called on macOS.
        private const string SDL = "libSDL2-2.0.0.dylib";
        private const string OBJC = "/usr/lib/libobjc.dylib";
        private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(SDL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GetWindowWMInfo(IntPtr window, ref SDL_SysWMinfo info);
        [DllImport(SDL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GetVersion(out SDL_version ver);
        [DllImport(SDL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GL_GetDrawableSize(IntPtr window, out int w, out int h);
        [DllImport(SDL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GetWindowSize(IntPtr window, out int w, out int h);
        [DllImport(SDL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_SetWindowSize(IntPtr window, int w, int h);

        [DllImport(OBJC, EntryPoint = "sel_registerName")]
        private static extern IntPtr sel(string name);
        [DllImport(OBJC, EntryPoint = "objc_getClass")]
        private static extern IntPtr objc_getClass(string name);
        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr msgSend(IntPtr receiver, IntPtr selector);
        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        private static extern void msgSend_bool(IntPtr receiver, IntPtr selector, byte value);
        [DllImport(OBJC, EntryPoint = "objc_msgSend")]
        private static extern void msgSend_ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(CG)] private static extern uint CGMainDisplayID();
        [DllImport(CG)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
        [DllImport(CG)] private static extern void CGDisplayModeRelease(IntPtr mode);
        [DllImport(CG)] private static extern long CGDisplayModeGetPixelWidth(IntPtr mode);
        [DllImport(CG)] private static extern long CGDisplayModeGetWidth(IntPtr mode);

        [StructLayout(LayoutKind.Sequential)]
        private struct SDL_version { public byte major, minor, patch; }

        // SDL_SysWMinfo: version (3 bytes), subsystem (uint @4), 64-byte union @8 (Cocoa: NSWindow* first).
        [StructLayout(LayoutKind.Sequential)]
        private struct SDL_SysWMinfo
        {
            public SDL_version version;
            public uint subsystem;
            public IntPtr window; // cocoa.window (NSWindow*)
            private IntPtr p1, p2, p3, p4, p5, p6, p7; // pad rest of the 64-byte union
        }

        /// <summary>Backing scale of the main display (≈2 on standard Retina), or 1 if it can't be read.</summary>
        public static float MainDisplayBackingScale()
        {
            try
            {
                var mode = CGDisplayCopyDisplayMode(CGMainDisplayID());
                if (mode == IntPtr.Zero) return 1f;
                long px = CGDisplayModeGetPixelWidth(mode);
                long pt = CGDisplayModeGetWidth(mode);
                CGDisplayModeRelease(mode);
                return (pt > 0) ? (float)px / pt : 1f;
            }
            catch { return 1f; }
        }

        /// <summary>
        /// Request a best-resolution GL surface for the SDL window; returns the resulting drawable
        /// scale (drawablePixels / windowPoints), 1 if no Retina surface was granted.
        /// </summary>
        public static float EnableBestResolutionSurface(IntPtr sdlWindow)
        {
            try
            {
                var info = new SDL_SysWMinfo();
                SDL_GetVersion(out info.version);
                if (SDL_GetWindowWMInfo(sdlWindow, ref info) == 0 || info.window == IntPtr.Zero) return 1f;
                var contentView = msgSend(info.window, sel("contentView"));
                if (contentView == IntPtr.Zero) return 1f;
                msgSend_bool(contentView, sel("setWantsBestResolutionOpenGLSurface:"), 1);
                // Setting the flag doesn't resize a live GL surface - nudge the window size by 1px and
                // back so Cocoa recreates the drawable at the native backing scale.
                SDL_GetWindowSize(sdlWindow, out int ww, out int wh);
                if (ww > 1 && wh > 1)
                {
                    SDL_SetWindowSize(sdlWindow, ww, wh - 1);
                    SDL_SetWindowSize(sdlWindow, ww, wh);
                }
                SDL_GL_GetDrawableSize(sdlWindow, out int dw, out _);
                return (ww > 0) ? (float)dw / ww : 1f;
            }
            catch { return 1f; }
        }

        /// <summary>Restore the bundle's Dock icon (Liquid Glass): MonoGame's SDL_SetWindowIcon replaces
        /// the Dock tile on macOS; setting the app icon image to nil reverts to the bundle icon.</summary>
        public static void RestoreBundleDockIcon()
        {
            try
            {
                var nsapp = msgSend(objc_getClass("NSApplication"), sel("sharedApplication"));
                if (nsapp != IntPtr.Zero)
                    msgSend_ptr(nsapp, sel("setApplicationIconImage:"), IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>Drawable (pixel) size of the GL surface — for diagnostics.</summary>
        public static (int w, int h) DrawableSize(IntPtr sdlWindow)
        {
            try { SDL_GL_GetDrawableSize(sdlWindow, out int w, out int h); return (w, h); } catch { return (0, 0); }
        }

        /// <summary>Window (point) size.</summary>
        public static (int w, int h) WindowSize(IntPtr sdlWindow)
        {
            try { SDL_GetWindowSize(sdlWindow, out int w, out int h); return (w, h); } catch { return (0, 0); }
        }

        /// <summary>Set the window (point) size - shrinks the window back after GraphicsDevice.Reset grows it.</summary>
        public static void SetWindowSize(IntPtr sdlWindow, int w, int h)
        {
            try { SDL_SetWindowSize(sdlWindow, w, h); } catch { }
        }

        /// <summary>Best-effort diagnostic line appended to &lt;dir&gt;/openso-dpi.log.</summary>
        public static void Log(string dir, string line)
        {
            try { File.AppendAllText(Path.Combine(dir, "openso-dpi.log"), line + "\n"); } catch { }
        }
    }
}
