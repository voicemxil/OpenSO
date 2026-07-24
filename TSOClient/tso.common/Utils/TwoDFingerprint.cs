using System;
using System.IO;
using System.Threading;

namespace FSO.Common.Utils
{
    /// <summary>
    /// TEMPORARY (branch-only): compact 2D pipeline state fingerprint, appended to
    /// openso-2d-fp.log in the working directory. Run the game once launched -2d and once
    /// launched normally (3D default, switch to 2D in-game); diffing the two logs pins any
    /// remaining divergent variable between the pipelines. Strip before merge.
    /// </summary>
    public static class TwoDFingerprint
    {
        private static int _count;
        public static bool CountOk(int max) => Interlocked.Increment(ref _count) <= max;

        public static void Log(string line)
        {
            try
            {
                Console.WriteLine("[2dfp] " + line);
                File.AppendAllText("openso-2d-fp.log", line + Environment.NewLine);
            }
            catch { /* best effort */ }
        }
    }
}
