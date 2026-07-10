using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace FSO.Patcher
{
    public class ReversiblePatcher
    {
        public List<string> FileChanges;
        public HashSet<string> Extracted;
        public HashSet<string> Removed;
        public HashSet<ZipArchiveEntry> ToExtract;
        public List<string> Errors;

        public ZipArchive Archive;
        public event Action<string, float> OnStatus;

        public int Total;

        public ReversiblePatcher(ZipArchive zip)
        {
            Archive = zip;
            ToExtract = new HashSet<ZipArchiveEntry>(zip.Entries);
            Total = ToExtract.Count;
            Extracted = new HashSet<string>();
            Removed = new HashSet<string>();

            try
            {
                Directory.Delete("updateBackup/", true);
            }
            catch (Exception)
            {

            }
            Directory.CreateDirectory("updateBackup/");
        }

        public HashSet<string> IgnoreFiles = new HashSet<string>()
        {
            //"updater.exe",
            "Content/config.ini",
            "NLog.config",
            "update.pdb",

            //monogame in the base directory is not used on fso windows, and is manually replaced on unix
            "MonoGame.Framework.dll",
            "MonoGame.Framework.xml",
        };

        public static HashSet<string> UnimportantFiles = new HashSet<string>()
        {
            "discord-rpc.dll"
        };

        public static HashSet<string> DeferredUpdate = new HashSet<string>()
        {

        };

        private void Status(string message)
        {
            OnStatus?.Invoke(message, 1f - (ToExtract.Count / (float)Total));
        }

        public List<string> GetIncompleteFiles()
        {
            return ToExtract.Select(file => file.FullName).Where(name => !UnimportantFiles.Contains(name)).ToList();
        }

        /// <summary>
        /// Validates EVERY entry in the archive against the install directory BEFORE anything is extracted,
        /// using the same policy the launcher enforces (see ArchivePathGuard). The zip is fetched over the
        /// network, so it is untrusted: any entry that would traverse out of the install directory, is rooted,
        /// uses backslash tricks, or is a symlink/special file makes the WHOLE archive invalid -- callers must
        /// abort with nothing written (no partial extraction of an unvalidated archive). Returns false with a
        /// human-readable <paramref name="reason"/> for the first bad entry; true when every entry is safe.
        /// </summary>
        public bool Validate(out string reason)
        {
            // Entries are written relative to the current working directory (see ExtractEntry's
            // Path.Combine("./", name)), so the install root is the CWD.
            var root = Path.GetFullPath(".");
            foreach (var entry in Archive.Entries)
            {
                try
                {
                    ArchivePathGuard.RejectIfLinkOrSpecial(entry);
                    // update.exe is remapped to update2.exe on write (it can't replace the running patcher),
                    // but both are equally safe names -- validate the archive's declared path.
                    ArchivePathGuard.ResolveContainedPath(root, entry.FullName);
                }
                catch (Exception e)
                {
                    reason = $"{entry.FullName}: {e.Message}";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        public async Task<bool> ExtractEntry(ZipArchiveEntry entry, int tryNum)
        {
            var name = (entry.FullName == "update.exe") ? "update2.exe" : entry.FullName;
            var targPath = Path.Combine("./", name);
            Directory.CreateDirectory(Path.GetDirectoryName(targPath));
            try
            {
                if (File.Exists(targPath) && tryNum == 0)
                {
                    //copy to backup folder
                    var backupPath = Path.Combine("updateBackup/", targPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                    File.Copy(targPath, backupPath, true);
                }
                entry.ExtractToFile(targPath, true);
                Status(name + " Extracted...");
                Extracted.Add(targPath);
                return true;
            }
            catch (Exception e)
            {
                if (e is DirectoryNotFoundException) return true;
                if (tryNum++ > 3 || Errors.Count > 4)
                {
                    Status($"Could not replace {targPath}!");
                    Errors.Add($"{targPath}: {e.Message}");
                    return false;
                }
                else
                {
                    Status($"Waiting for {name} ({tryNum}/4)... {e.ToString()}");
                    await Task.Delay(3000);
                    return await ExtractEntry(entry, tryNum);
                }

            }
        }

        public static int RENAME_MAX_ATTEMPTS = 5;

        public async Task<bool> AttemptRename(int renameRetry)
        {
            try
            {
                File.Delete("OpenSO.exe.old");
                if (File.Exists("OpenSO.exe"))  //shouldn't be in use, unless the user has incorrectly renamed and run the freeso executable
                    File.Move("OpenSO.exe", "OpenSO.exe.old");
            }
            catch (Exception)
            {
                if (renameRetry++ < RENAME_MAX_ATTEMPTS)
                {
                    Status($"Waiting for OpenSO to Close ({renameRetry}/{RENAME_MAX_ATTEMPTS})...");
                    await Task.Delay(2000);
                    return await AttemptRename(renameRetry);
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        public async Task AttemptExtract()
        {
            Errors = new List<string>();
            //file being replaced? 
            var clone = ToExtract.ToList();
            foreach (var entry in clone)
            {
                if (IgnoreFiles.Contains(entry.FullName))
                {
                    ToExtract.Remove(entry);
                    continue;
                }
                var result = await ExtractEntry(entry, 0);
                if (result)
                {
                    ToExtract.Remove(entry);
                }
            }
        }

        // Deletes files this patch step's manifest says were removed (see UpdateManifest.GetRemovedPaths),
        // backing each up first under updateBackup/ (same convention as ExtractEntry) so Revert() can bring
        // them back. Paths come from a downloaded JSON file, so every one is validated relative-safe before
        // it's allowed anywhere near File.Delete -- never trust it to stay within the install directory.
        //
        // Returns false if a removal it was asked to perform FAILED (the backup copy or the delete threw):
        // that leaves the install inconsistent, so the caller must treat it as a failed transaction (revert +
        // report failure) rather than finalizing. A path skipped by the safety filter is logged but does not,
        // by itself, fail the transaction -- the manifest is CI-authored and an unsafe entry is refused, not
        // acted on.
        public bool RemoveFiles(IEnumerable<string> paths)
        {
            bool success = true;
            foreach (var raw in paths)
            {
                var rel = SafeRelativePath(raw);
                if (rel == null)
                {
                    Status($"Skipped unsafe removal path from manifest: {raw}");
                    continue;
                }
                var targPath = Path.Combine("./", rel);
                if (!File.Exists(targPath)) continue; //already gone (or never existed here) -- nothing to do

                try
                {
                    var backupPath = Path.Combine("updateBackup/", targPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                    File.Copy(targPath, backupPath, true);
                    File.Delete(targPath);
                    Removed.Add(targPath);
                    Status(targPath + " Removed...");
                }
                catch (Exception e)
                {
                    Status($"Could not remove {targPath}: {e.Message}");
                    Errors.Add($"{targPath}: {e.Message}");
                    success = false;
                }
            }
            return success;
        }

        // Rejects anything not safely relative to the install root: absolute/rooted paths and ".." segments.
        // The manifest is built by our own CI, but it arrives as a downloaded file, so treat it as untrusted.
        private static string SafeRelativePath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var p = raw.Replace('\\', '/');
            while (p.StartsWith("./")) p = p.Substring(2); //DiffGenerator prefixes root-level files with "./"
            if (p.Length == 0 || Path.IsPathRooted(p)) return null;
            foreach (var seg in p.Split('/'))
            {
                if (seg.Length == 0 || seg == "..") return null;
            }
            return p;
        }

        public bool Revert()
        {
            bool success = true;
            foreach (var file in Extracted)
            {
                var backupPath = Path.Combine("updateBackup/", file);
                try
                {
                    Status($"Restoring backup for {file}...");
                    File.Copy(backupPath, file, true);
                }
                catch (FileNotFoundException)
                {
                    Status($"Backup for {file} not found, skipping...");
                }
                catch (Exception e)
                {
                    Status($"Could not restore backup for {file}: {e.Message}");
                    Errors.Add($"{file}: {e.Message}");
                    success = false;
                }
            }
            foreach (var file in Removed)
            {
                var backupPath = Path.Combine("updateBackup/", file);
                try
                {
                    Status($"Restoring removed file {file}...");
                    File.Copy(backupPath, file, true);
                }
                catch (FileNotFoundException)
                {
                    Status($"Backup for {file} not found, skipping...");
                }
                catch (Exception e)
                {
                    Status($"Could not restore removed file {file}: {e.Message}");
                    Errors.Add($"{file}: {e.Message}");
                    success = false;
                }
            }
            if (success)
            {
                try
                {
                    Directory.Delete("updateBackup/", true);
                }
                catch
                {
                    //can't delete backup for some reason. just ignore.
                }
            }
            return success;
        }

        public void Final()
        {
            try
            {
                Directory.Delete("updateBackup/", true);
            }
            catch
            {
                //can't delete backup for some reason. just ignore.
            }
            Archive.Dispose();
        }
    }
}
