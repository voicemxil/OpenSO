using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FSO.Client;
using FSO.Client.UI.Panels;
using FSO.Common;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace FSO.Unix
{
    public static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        public static void Main(string[] args)
        {
            InitUnix();
            ResolveContentPaths();

            var mgAssembly = typeof(Microsoft.Xna.Framework.Game).Assembly;
            var platform = mgAssembly.GetType("MonoGame.Framework.Utilities.PlatformInfo");
            var backend = platform?.GetProperty("GraphicsBackend")?.GetValue(null);
            Console.WriteLine($"[OpenSO] MonoGame: {mgAssembly.GetName().Version} | Backend: {backend ?? "Unknown"}");

            FSOEnvironment.Enable3D = true;

            if ((new FSOProgram()).InitWithArguments(args))
            {
                var startProxy = new GameStartProxy();
                startProxy.Start(false);
            }

            Environment.Exit(0);
        }

        /// <summary>
        /// Resolve the game content directory from the executable's location so it's found whether OpenSO
        /// is launched by the launcher or by double-clicking OpenSO.app (the working directory differs).
        /// On macOS we ship a CODE-ONLY OpenSO.app; the game data + mutable content (MeshReplace, MeshCache,
        /// saves) live in the folder AROUND the .app, so codesign can seal the bundle and updates/remesh
        /// never touch it. Shows a readable error and exits if the content can't be found.
        /// </summary>
        private static void ResolveContentPaths()
        {
            var baseDir = AppContext.BaseDirectory; // dir holding the running apphost + managed DLLs

            // Inside an .app bundle, the install dir is the folder that CONTAINS OpenSO.app.
            var inBundle = Path.Combine("OpenSO.app", "Contents", "MacOS") + Path.DirectorySeparatorChar;
            int idx = baseDir.IndexOf(inBundle, StringComparison.Ordinal);
            string installDir = idx >= 0
                ? baseDir.Substring(0, idx).TrimEnd(Path.DirectorySeparatorChar)
                : baseDir.TrimEnd(Path.DirectorySeparatorChar);

            var contentDir = Path.Combine(installDir, "Content");
            if (!Directory.Exists(contentDir))
            {
                ShowDialog(
                    "OpenSO couldn't find its game content.\n\nExpected it here:\n" + contentDir +
                    "\n\nInstall or repair the game through the OpenSO launcher, then try again.",
                    "Game content not found");
                Environment.Exit(1);
            }

            // The content loader resolves several paths RELATIVE to the working directory — most importantly
            // Content.InitBasic scans a hardcoded "Content/", and FSOProgram.InitWithArguments sets
            // FSOEnvironment.ContentDir = "Content/" (relative). When OpenSO.app is double-clicked the cwd is
            // the bundle's Contents/MacOS, so "Content/" would resolve INSIDE the bundle (where there is no
            // Content). Point the cwd at the install dir so every relative game path resolves there instead.
            try { Directory.SetCurrentDirectory(installDir); } catch { /* best effort */ }

            FSOEnvironment.ContentDir = contentDir + Path.DirectorySeparatorChar;
            FSOEnvironment.UserDir = contentDir + Path.DirectorySeparatorChar;
            FSOEnvironment.GFXContentDir = Path.Combine(contentDir, "OGL") + Path.DirectorySeparatorChar;
            Console.WriteLine($"[OpenSO] Install directory: {installDir}");
            Console.WriteLine($"[OpenSO] Content directory: {FSOEnvironment.ContentDir}");
        }

        public static void InitUnix()
        {
            FSO.Files.ImageLoaderHelpers.BitmapFunction = BitmapReader;
            FSO.Files.ImageLoaderHelpers.SavePNGFunc = SavePNG;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            FSOProgram.ShowDialog = ShowDialog;
        }

        public static void ShowDialog(string text)
        {
            ShowDialog(text, "OpenSO Message");
        }

        private static string Escape(string s) => s.Replace("\"", "\\\"");

        private static void ShowDialog(string text, string title)
        {
            if (text.Length > 1500) text = text.Substring(0, 1500) + "...";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e \"display alert \\\"{Escape(title)}\\\" message \\\"{Escape(text)}\\\" giving up after 15\"",
                    UseShellExecute = true
                };
                Process.Start(psi)?.WaitForExit();
            }
            else
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "zenity",
                        Arguments = $"--error --title=\"{Escape(title)}\" --text=\"{Escape(text)}\"",
                        UseShellExecute = false
                    };
                    Process.Start(psi)?.WaitForExit();
                }
                catch
                {
                    Console.Error.WriteLine($"[{title}] {text}");
                }
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            string title = e.ExceptionObject is OutOfMemoryException
                ? "Out of Memory! OpenSO needs to close."
                : "A fatal error occured! Screenshot this dialog and post it on Discord.";

            ShowDialog(e.ExceptionObject.ToString(), title);
            Environment.Exit(1);
        }

        public static void SavePNG(byte[] data, int width, int height, Stream str)
        {
            using var image = new Image<Rgba32>(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    image[x, y] = new Rgba32(data[i], data[i + 1], data[i + 2], data[i + 3]);
                }
            }

            image.Save(str, new PngEncoder());
        }

        public static Tuple<byte[], int, int> BitmapReader(Stream str)
        {
            using var image = Image.Load<Rgba32>(str);
            int width = image.Width;
            int height = image.Height;

            var data = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    Rgba32 px = image[x, y];
                    data[i] = px.R;
                    data[i + 1] = px.G;
                    data[i + 2] = px.B;
                    data[i + 3] = px.A;
                }
            }

            return new Tuple<byte[], int, int>(data, width, height);
        }
    }
}
