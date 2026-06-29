using System;
using System.IO;

namespace FSO.Client.Utils.GameLocator
{
    public class MacOSLocator : ILocator
    {
        public string FindTheSimsOnline()
        {
            // The launcher installs TSO as a sibling of the client (FSO) dir: <root>/The Sims Online and
            // <root>/FSO. Resolve relative to the EXECUTABLE — not the cwd (which the .app sets inside the
            // bundle) — so it's found whether launched by the launcher or by double-clicking OpenSO.app.
            var baseDir = AppContext.BaseDirectory;
            var inBundle = Path.Combine("OpenSO.app", "Contents", "MacOS") + Path.DirectorySeparatorChar;
            int idx = baseDir.IndexOf(inBundle, StringComparison.Ordinal);
            string fsoDir = idx >= 0
                ? baseDir.Substring(0, idx).TrimEnd(Path.DirectorySeparatorChar)   // .app's parent = the FSO dir
                : baseDir.TrimEnd(Path.DirectorySeparatorChar);
            // Return WITH a trailing separator (like the fallbacks below). Content._ScanFiles strips this
            // BasePath off each scanned file by length; without the trailing slash the keys keep a leading
            // "/", and Path.Combine(BasePath, "/uigraphics/...") then treats them as rooted and resolves at
            // the filesystem root (FAR3 "Could not open the specified archive - /uigraphics/.../*.dat").
            var sibling = Path.GetFullPath(Path.Combine(fsoDir, "..", "The Sims Online", "TSOClient")) + Path.DirectorySeparatorChar;
            if (File.Exists(Path.Combine(sibling, "tuning.dat"))) return sibling;

            // Legacy cwd-relative layout (FreeSO put TSO next to the working dir).
            string localDir = @"../The Sims Online/TSOClient/";
            if (File.Exists(Path.Combine(localDir, "tuning.dat"))) return localDir;

            return string.Format("{0}/The Sims Online/TSOClient/", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }
    }
}
