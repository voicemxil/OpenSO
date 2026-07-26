using System;
using System.Runtime.InteropServices;

namespace FSO.Common.Utils
{
    /// <summary>
    /// Reads the top safe-area inset of the main display on macOS - the height of the camera housing
    /// ("notch") on the MacBook Pro and Air. There is no managed API for this, so it goes through the
    /// Objective-C runtime to NSScreen.mainScreen.safeAreaInsets.
    ///
    /// The app renders under the notch on purpose (Info.plist: NSPrefersDisplaySafeAreaCompatibilityMode
    /// = false), so that the window frame and the content area share an origin - the alternative,
    /// letting macOS letterbox content below the strip, desynchronises the two and shifts every mouse
    /// coordinate down by this same amount. The cost is that top-anchored UI has to keep clear of the
    /// notch itself, which is what this value is for.
    /// </summary>
    public static class MacSafeArea
    {
        private const string LIBOBJC = "/usr/lib/libobjc.dylib";

        // NSEdgeInsets is four CGFloats. On arm64 a struct this size comes back indirectly (via x8),
        // which the .NET P/Invoke marshaller handles when the return type is declared as the struct.
        [StructLayout(LayoutKind.Sequential)]
        private struct NSEdgeInsets
        {
            public double Top, Left, Bottom, Right;
        }

        [DllImport(LIBOBJC, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetClass(string name);

        [DllImport(LIBOBJC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
        private static extern IntPtr Sel(string name);

        [DllImport(LIBOBJC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

        [DllImport(LIBOBJC, EntryPoint = "objc_msgSend")]
        private static extern NSEdgeInsets SendInsets(IntPtr receiver, IntPtr selector);

        [DllImport(LIBOBJC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendRespondsTo(IntPtr receiver, IntPtr selector, IntPtr arg);

        private static bool _Queried;
        private static float _TopPoints;

        /// <summary>
        /// Height of the notch in WINDOW POINTS, or 0 where there is none (any non-Mac, any Mac without
        /// a camera housing, or macOS older than 12 where safeAreaInsets does not exist). Cached: it is
        /// a property of the hardware and cannot change while running.
        /// </summary>
        public static float TopPoints
        {
            get
            {
                if (_Queried) return _TopPoints;
                _Queried = true;
                _TopPoints = Query();
                return _TopPoints;
            }
        }

        private static float Query()
        {
            if (!OperatingSystem.IsMacOS()) return 0f;
            try
            {
                var screenClass = GetClass("NSScreen");
                if (screenClass == IntPtr.Zero) return 0f;
                var main = SendIntPtr(screenClass, Sel("mainScreen"));
                if (main == IntPtr.Zero) return 0f;

                // safeAreaInsets is macOS 12+; calling it on an older system would raise an ObjC
                // exception straight through the runtime, which a managed catch cannot contain.
                var sel = Sel("safeAreaInsets");
                if (!SendRespondsTo(main, Sel("respondsToSelector:"), sel)) return 0f;

                var insets = SendInsets(main, sel);
                var top = (float)insets.Top;
                // Sanity clamp: a plausible notch is tens of points. Anything else means the call
                // returned garbage, and inseting the UI by a wrong amount is worse than not at all.
                return (top > 0f && top < 200f) ? top : 0f;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
