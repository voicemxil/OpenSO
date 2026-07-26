using FSO.Common.Utils;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace FSO.Common.Rendering
{
    /// <summary>
    /// Diagnostic for GPU-resource work attempted off the render thread.
    ///
    /// An OpenGL context is current on exactly one thread. A GL call from any other thread finds no
    /// context and dereferences null deep inside the driver, so it surfaces as a bare SIGSEGV (commonly
    /// in glGetIntegerv, which MonoGame's GL backend calls to save the previously bound texture) rather
    /// than a managed exception - the CLR never gets a chance to see it, so there is no stack trace and
    /// no in-game error. That makes these faults very hard to attribute after the fact.
    ///
    /// This does not prevent the fault. It logs the managed stack BEFORE the call is made, so a crash
    /// report can be tied back to the code path that caused it instead of being guessed at. Fixing a
    /// site it reports means routing the work through GameThread.InUpdate / AssetStreaming.InStreamUpdate.
    /// </summary>
    public static class GLThreadGuard
    {
        private static readonly HashSet<string> _Reported = new HashSet<string>();
        private static readonly object _Lock = new object();
        private const int REPORT_LIMIT = 20; // a broken path can be hit per-sprite; don't flood the log

        /// <summary>
        /// Pass-through check for use in a constructor's base(...) argument list, where the argument
        /// expression is evaluated before the base constructor body runs - which is the only way to log
        /// ahead of a GPU resource allocation that may not survive to return.
        /// </summary>
        public static GraphicsDevice Check(GraphicsDevice gd, string site)
        {
            Warn(site);
            return gd;
        }

        /// <summary>Log once per distinct call site if we are not on the render thread.</summary>
        public static void Warn(string site)
        {
            if (GameThread.IsInGameThread() || GameThread.NoGame) return;

            string stack;
            try { stack = Environment.StackTrace; }
            catch { stack = "(stack unavailable)"; }

            lock (_Lock)
            {
                if (_Reported.Count >= REPORT_LIMIT) return;
                if (!_Reported.Add(site + "|" + stack.GetHashCode())) return;
            }

            Console.WriteLine($"[glthread] GPU work off the render thread at '{site}' on thread " +
                $"'{System.Threading.Thread.CurrentThread.Name ?? "(unnamed)"}'. This segfaults on macOS. Stack:\n{stack}");
        }
    }
}
