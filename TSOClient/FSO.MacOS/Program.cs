using System;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using FSO.Client;
using FSO.Client.UI.Panels;
using FSO.Common.Rendering.Framework.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace FSO.MacOS
{
    public static class Program
    {

        /// <summary>
        /// The main entry point for the application.
        /// </summary>

        public static void Main(string[] args)
        {
            InitMacOS();

            if ((new FSOProgram()).InitWithArguments(args))
            {
                var startProxy = new GameStartProxy();
                startProxy.Start(false);
            }

            Environment.Exit(0);
        }

        public static void InitMacOS()
        {
            FSO.Files.ImageLoaderHelpers.BitmapFunction = BitmapReader;
            // ClipboardHandler.Default = new WinFormsClipboard();
            FSO.Files.ImageLoaderHelpers.SavePNGFunc = SavePNG;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            FSOProgram.ShowDialog = ShowDialog;
        }

        public static void ShowDialog(string text)
        {
            ShowDialog(text, "FreeSO Message");
        }
        
        private static void ShowDialog(string text, string title)
        {
            if (text.Length > 1500) text = text.Substring(0, 1500) + "...";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"display alert \\\"{title.Replace("\"", "\\\"")}\\\" message \\\"{text.Replace("\"", "\\\"")}\\\" giving up after 15\"",
                UseShellExecute = true
            };
            Process.Start(psi)?.WaitForExit();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject;
            
            if (exception is OutOfMemoryException)
            {
                ShowDialog(e.ExceptionObject.ToString(), "Out of Memory! FreeSO needs to close.");
            }
            else
            {
                ShowDialog(e.ExceptionObject.ToString(), "A fatal error occured! Screenshot this dialog and post it on Discord.");
            }
            
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
