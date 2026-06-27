using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FSO.Files.Utils;
using Newtonsoft.Json;

namespace FSO.DeltaGen
{
    /// <summary>
    /// Produces the incremental (delta) patch the in-game updater applies to go from a PREVIOUS client
    /// release to the CURRENT one. This is the same computation FSO.Server.Api.Core's GenerateUpdateService
    /// does (DiffGenerator XxHash32 whole-file diffs → a zip of the Add/Modify files + an FSOUpdateManifest
    /// JSON), lifted into a standalone CI tool so release.yml can attach the delta as a release asset.
    ///
    /// The client patcher (FSO.Patcher.ReversiblePatcher) simply extracts every entry of the incremental
    /// zip over the install, so the zip must contain exactly the changed files at their install-relative
    /// paths — which is what this emits.
    ///
    /// Usage: FSO.DeltaGen &lt;prevClient.zip&gt; &lt;newClient.zip&gt; &lt;version&gt; &lt;outDir&gt;
    /// Emits &lt;outDir&gt;/OpenSO-client-win-x64.incremental.zip and OpenSO-client-win-x64.manifest.json.
    /// </summary>
    public static class Program
    {
        // Asset names the release-delta job uploads and the server-side reconciler looks for.
        public const string IncrementalAsset = "OpenSO-client-win-x64.incremental.zip";
        public const string ManifestAsset = "OpenSO-client-win-x64.manifest.json";

        public static int Main(string[] args)
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("usage: FSO.DeltaGen <prevClient.zip> <newClient.zip> <version> <outDir>");
                return 2;
            }
            var prevZip = args[0];
            var newZip = args[1];
            var version = args[2];
            var outDir = args[3];

            var work = Path.Combine(Path.GetTempPath(), "openso-deltagen-" + Guid.NewGuid().ToString("N"));
            var prevDir = Path.Combine(work, "prev");
            var newDir = Path.Combine(work, "new");
            var diffDir = Path.Combine(work, "diff");
            try
            {
                Directory.CreateDirectory(outDir);
                ZipFile.ExtractToDirectory(prevZip, prevDir);
                ZipFile.ExtractToDirectory(newZip, newDir);

                var diffs = DiffGenerator.GetDiffs(Path.GetFullPath(prevDir), Path.GetFullPath(newDir));

                // The incremental zip carries the Add + Modify files at their install-relative paths.
                // Whole-release diffs (no stable base+addon split), so we include everything that changed —
                // engine DLLs included — otherwise a genuinely-changed file would be missing post-patch.
                var changed = diffs.Where(d => d.DiffType == FileDiffType.Add || d.DiffType == FileDiffType.Modify).ToList();
                Directory.CreateDirectory(diffDir);
                foreach (var d in changed)
                {
                    var dst = Path.Combine(diffDir, d.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(Path.Combine(newDir, d.Path), dst, true);
                }

                var incrementalPath = Path.Combine(outDir, IncrementalAsset);
                if (File.Exists(incrementalPath)) File.Delete(incrementalPath);
                ZipFile.CreateFromDirectory(diffDir, incrementalPath, CompressionLevel.Optimal, includeBaseDirectory: false);

                // Manifest shape matches FSO.Server.Api.Core.Models.FSOUpdateManifest { Version, Diffs }.
                var manifest = new ManifestDto { Version = version, Diffs = diffs };
                File.WriteAllText(Path.Combine(outDir, ManifestAsset), JsonConvert.SerializeObject(manifest));

                int add = diffs.Count(d => d.DiffType == FileDiffType.Add);
                int mod = diffs.Count(d => d.DiffType == FileDiffType.Modify);
                int rem = diffs.Count(d => d.DiffType == FileDiffType.Remove);
                int same = diffs.Count(d => d.DiffType == FileDiffType.Unchanged);
                var size = new FileInfo(incrementalPath).Length;
                Console.WriteLine($"delta {version}: +{add} ~{mod} -{rem} ={same}; incremental {changed.Count} files, {size / 1048576.0:0.0} MB");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("delta generation failed: " + e);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }

        private sealed class ManifestDto
        {
            public string Version;
            public List<FileDiff> Diffs;
        }
    }
}
