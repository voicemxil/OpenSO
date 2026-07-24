using System.Runtime.InteropServices;
using System.Threading;

namespace FSO.Common
{
    public static class FSOEnvironment
    {
        public static Thread GameThread;

        /// <summary>
        /// The .NET runtime identifier this build runs as, normalized to the RIDs release CI publishes:
        /// win-x64 / linux-x64 / osx-x64 / osx-arm64 (or "&lt;os&gt;-&lt;arch&gt;" for anything else). Sent to the
        /// server at login so it can return a platform-correct update payload, and used by the launcher to
        /// pick this platform's package from the per-RID distribution manifest.
        /// </summary>
        public static string RID { get; } = GetRID();

        private static string GetRID()
        {
            string os =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                "unknown";
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
            };
            return os + "-" + arch;
        }

        public static string ContentDir = "Content/";
        public static string UserDir = "Content/";
        public static string GFXContentDir = "Content/OGL";
        public static bool DirectX = false;
        public static bool Linux = false;
        public static bool UseMRT = true;
        /// <summary>
        /// True if system does not support gl_FragDepth (eg. iOS). Uses alternate pipeline that abuses stencil buffer.
        /// </summary>
        public static bool SoftwareDepth = false;
        public static int GLVer = 3;
        public static float UIZoomFactor = 1f;
        public static float DPIScaleFactor = 1;
        /// <summary>
        /// Scale from OS window coordinates (points) to backbuffer pixels. 1 everywhere except macOS
        /// native-Retina rendering, where the GL drawable is denser than the SDL window (e.g. 2).
        /// Mouse reads multiply by this (window points -> render pixels); Mouse.SetPosition divides.
        /// </summary>
        public static float WindowPixelRatio = 1f;

        /// <summary>Map a window-space mouse state to backbuffer pixels (see WindowPixelRatio).</summary>
        public static Microsoft.Xna.Framework.Input.MouseState ScaleMouse(Microsoft.Xna.Framework.Input.MouseState m)
        {
            if (WindowPixelRatio == 1f) return m;
            return new Microsoft.Xna.Framework.Input.MouseState(
                (int)(m.X * WindowPixelRatio), (int)(m.Y * WindowPixelRatio), m.ScrollWheelValue,
                m.LeftButton, m.MiddleButton, m.RightButton, m.XButton1, m.XButton2, m.HorizontalScrollWheelValue);
        }
        public static bool SoftwareKeyboard = false;
        public static bool NoSound = false;
        public static int RefreshRate = 60;
        /// <summary>Real wall-clock seconds elapsed since the previous frame (set each frame in TSOGame.Update).
        /// Use for framerate-independent animation: "X per second" = "X * DeltaTime per frame". Prefer this over
        /// dividing by RefreshRate, which only holds when RefreshRate exactly equals the real, even frame rate.</summary>
        public static float DeltaTime = 1f / 60f;

        /// <summary>
        /// True if 3D features are enabled (like smooth rotation + zoom). Loads some content with mipmaps and other things.
        /// Used to mean "3d camera" as well, though that has been moved to configuration and world state.
        /// </summary>
        public static bool Enable3D;
        public static bool EnableNPOTMip = true;
        public static bool TexCompress = true;
        public static bool TexCompressSupport = true;
        public static bool MSAASupport = true;
        // Highest hardware MSAA sample count the GPU can actually render+resolve (set by FeatureLevelTest).
        // e.g. Apple Silicon caps at 4. The settings menu only offers tiers up to this and the renderer clamps
        // to it, so a higher selection (or a saved 8x) can't produce a black screen.
        public static int MaxMSAA = 8;

        public static string Args = "";
    }
}
