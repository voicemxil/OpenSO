using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FSO.Patcher
{
    /// <summary>
    /// Minimal reader for the FSOUpdateManifest JSON that travels alongside each incremental patch zip
    /// (written by FSO.DeltaGen / FSO.Server.Api.Core's GenerateUpdateService, downloaded by
    /// UpdateController.BuildFiles as "PatchFiles/path{i}.json" next to "path{i}.zip"). We only need the
    /// removed-file list here, so this reads the raw JSON directly rather than taking a project reference
    /// on FSO.Files just for the FileDiff/FileDiffType shape (which would drag in MonoGame/Ninject).
    /// </summary>
    internal static class UpdateManifest
    {
        // FSO.Files.Utils.FileDiffType declaration order: Add=0, Modify=1, Remove=2, Unchanged=3. Both
        // manifest writers call JsonConvert.SerializeObject with no settings, so the enum serializes as
        // this raw ordinal rather than a string.
        private const int RemoveDiffType = 2;

        /// <summary>
        /// Returns the install-relative paths this patch step's manifest says were removed. Manifests are
        /// optional (older releases, full-zip-only steps, and downloaded extras have none) and a missing or
        /// unparsable one just means "nothing to remove" — never a reason to abort the patch.
        /// </summary>
        public static List<string> GetRemovedPaths(string zipPath)
        {
            var result = new List<string>();
            var manifestPath = Path.ChangeExtension(zipPath, ".json");
            if (!File.Exists(manifestPath)) return result;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!doc.RootElement.TryGetProperty("Diffs", out var diffs) || diffs.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var d in diffs.EnumerateArray())
                {
                    if (d.TryGetProperty("DiffType", out var t) && t.ValueKind == JsonValueKind.Number &&
                        t.GetInt32() == RemoveDiffType &&
                        d.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
                    {
                        result.Add(p.GetString());
                    }
                }
            }
            catch (Exception)
            {
                //corrupt/partial manifest download -- skip removals for this step rather than aborting the patch.
            }

            return result;
        }
    }
}
