using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FSO.Client.Utils
{
    /// <summary>
    /// macOS Retina / High-DPI support.
    ///
    /// MonoGame DesktopGL creates its SDL window WITHOUT SDL_WINDOW_ALLOW_HIGHDPI, so SDL sets the GL
    /// view's <c>wantsBestResolutionOpenGLSurface = NO</c>. On a Retina display that means the OpenGL
    /// drawable is created at POINT resolution (e.g. 1024x768) and the window server scales it up to the
    /// physical backing (e.g. 2048x1536) — a soft, "quarter-resolution" image. No backbuffer size alone
    /// fixes it because the GL surface itself is capped at point resolution.
    ///
    /// This helper flips the NSView back to a best-resolution (native pixel) surface and reports the
    /// resulting scale, so the game can size its backbuffer to the real pixels and render natively.
    /// All P/Invokes target libraries already loaded by the running macOS client; everything is guarded
    /// and best-effort — any failure leaves the prior (upscaled) behaviour untouched.
    /// </summary>
    internal static class MacRetina
    {
        // MonoGame DesktopGL ships SDL as "libSDL2-2.0.0.dylib" (the SDL2 ABI soname) next to the apphost.
        // .NET's DllImport name resolution for "SDL2" never tries that versioned filename, so the calls
        // threw DllNotFoundException (swallowed -> 0x0). Bind the exact name; macOS-guarded so it's never
        // resolved on Linux/Windows.
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

        // SDL_SysWMinfo: { SDL_version (3 bytes); SDL_SYSWM_TYPE subsystem (uint @4); union info (64 bytes @8) }.
        // For the Cocoa subsystem the union's first member is NSWindow* — the rest is padding.
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
        /// Ask the SDL window's GL view for a best-resolution (native pixel) surface and return the
        /// resulting drawable scale (drawablePixels / windowPoints). 1 means no Retina surface was granted.
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
                // Setting the flag doesn't resize a live GL surface. Nudge the window size by 1px and back to
                // make SDL/Cocoa recreate the drawable at the now-native backing scale; restore the size so
                // the window doesn't visibly change.
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

        /// <summary>Restore the .app bundle's Dock icon (Liquid Glass). MonoGame DesktopGL sets its own
        /// window icon (an embedded Icon.bmp, or its built-in logo when none is embedded) via
        /// SDL_SetWindowIcon, which on macOS replaces the Dock tile — setting the app icon image to nil
        /// reverts the Dock to the bundle icon (CFBundleIconName / Assets.car).</summary>
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

        /// <summary>Best-effort diagnostic line appended to &lt;dir&gt;/openso-dpi.log.</summary>
        public static void Log(string dir, string line)
        {
            try { File.AppendAllText(Path.Combine(dir, "openso-dpi.log"), line + "\n"); } catch { }
        }
    }
}
