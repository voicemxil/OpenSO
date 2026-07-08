using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FSO.Patcher
{
    /// <summary>
    /// Detects the running OpenSO client so the patcher never writes over a live install. The patcher used
    /// to only infer locks from failed writes — and on Windows a running .exe can still be renamed, so the
    /// AttemptRename "is it closed?" sentinel passed while the game was open and the patch then corrupted
    /// the install overwriting locked runtime DLLs. This waits for the game to actually exit first. The
    /// in-client update path launches update.exe and *then* exits the game, so a short wait also covers
    /// that hand-off race cleanly.
    /// </summary>
    public static class GameProcess
    {
        private const string ClientProcessName = "OpenSO"; // OpenSO.exe apphost; the patcher itself is "update"

        /// <summary>
        /// True if an OpenSO client is running out of this patcher's own directory. If a process's image
        /// path can't be read, it's counted as running (waiting a little longer is harmless; patching over
        /// a live game is not).
        /// </summary>
        public static bool AnyRunning()
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(ClientProcessName); }
            catch { return false; } // can't enumerate — don't wedge the patcher

            try
            {
                var here = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var p in procs)
                {
                    var path = TryPath(p);
                    if (path == null) return true;      // unverifiable — assume it's ours
                    if (IsWithin(path, here)) return true;
                }
                return false;
            }
            finally
            {
                foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// Polls until no OpenSO client is running or <paramref name="timeout"/> elapses, reporting a human
        /// status string (with the seconds remaining) each second. Returns true once clear, false on timeout.
        /// </summary>
        public static async Task<bool> WaitForExit(TimeSpan timeout, Action<string> onStatus)
        {
            var sw = Stopwatch.StartNew();
            while (AnyRunning())
            {
                if (sw.Elapsed >= timeout) return false;
                var left = (int)Math.Ceiling((timeout - sw.Elapsed).TotalSeconds);
                onStatus?.Invoke($"Waiting for OpenSO to close before updating… ({left}s)");
                await Task.Delay(1000);
            }
            return true;
        }

        private static string TryPath(Process p)
        {
            try { return p.MainModule?.FileName; } catch { return null; }
        }

        private static bool IsWithin(string exePath, string dir)
        {
            try
            {
                var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(exePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
