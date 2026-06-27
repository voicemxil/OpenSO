using FSO.Server.Common.Config;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.Updates;
using Octokit;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FSO.Server.Api.Core.Services
{
    /// <summary>
    /// Populates the managed-update list (fso_updates) from the OpenSO GitHub releases.
    ///
    /// CI (release.yml) attaches each release's full client zip + an incremental delta zip + a manifest;
    /// this discovers them and writes the published fso_updates rows that GET /userapi/update serves and
    /// the client's UpdatePath.FindPath chains into an incremental patch path. The releases are the source
    /// of truth, so there is NO CI->DB connectivity, no admin-webapp generation step, and no watchdog/
    /// exit-4 involvement — which is exactly what the OpenSO Docker deploy needs.
    ///
    /// Runs in the background at UserApi startup; offline / rate-limited just means the list isn't refreshed
    /// this boot. Idempotent: existing rows are left alone (or their URLs/chain refreshed if a delta was
    /// attached after the row was first created).
    /// </summary>
    public class UpdateReconciler
    {
        private static void Log(string msg) => Console.WriteLine("[UpdateReconciler] " + msg);

        // Asset names CI attaches (must match FSO.DeltaGen + release.yml). The full client zip is the
        // existing per-platform release asset.
        private const string FullClientAsset = "OpenSO-client-win-x64.zip";
        private const string IncrementalAsset = "OpenSO-client-win-x64.incremental.zip";
        private const string ManifestAsset = "OpenSO-client-win-x64.manifest.json";

        private readonly IDAFactory _da;
        private readonly GithubConfig _gh;
        private readonly string _branch;

        public UpdateReconciler(IDAFactory da, GithubConfig gh, string branch)
        {
            _da = da;
            _gh = gh;
            _branch = string.IsNullOrEmpty(branch) ? "dev" : branch;
        }

        public async Task ReconcileAsync()
        {
            var owner = _gh?.User ?? "voicemxil";
            var repo = _gh?.Repository ?? "OpenSO";

            // Reading public releases needs no auth; use the token only for a higher rate limit if present.
            var client = new GitHubClient(new ProductHeaderValue(string.IsNullOrEmpty(_gh?.AppName) ? "OpenSO" : _gh.AppName));
            if (!string.IsNullOrEmpty(_gh?.AccessToken)) client.Credentials = new Credentials(_gh.AccessToken);

            var releases = await client.Repository.Release.GetAll(owner, repo);

            // This branch's releases, tagged "<branch>-<N>", oldest first so last_update_id chains forward.
            var ordered = releases
                .Where(r => !r.Draft && r.TagName != null && r.TagName.StartsWith(_branch + "-", StringComparison.Ordinal))
                .Select(r => new { r, n = ParseVersion(r.TagName, _branch) })
                .Where(x => x.n >= 0)
                .OrderBy(x => x.n)
                .Select(x => x.r)
                .ToList();

            if (ordered.Count == 0)
            {
                Log($"UpdateReconciler: no '{_branch}-*' releases at {owner}/{repo}.");
                return;
            }

            using var db = _da.Get();

            var branch = db.Updates.GetBranch(_branch);
            if (branch == null)
            {
                // base_build_url is required by the schema but unused here (deltas are generated in CI, not
                // by GenerateUpdateService). Point it at the releases page for documentation.
                db.Updates.AddBranch(new DbUpdateBranch
                {
                    branch_name = _branch,
                    version_format = _branch + "-#",
                    base_build_url = $"https://github.com/{owner}/{repo}/releases",
                    build_mode = DbUpdateBuildMode.zip,
                });
                branch = db.Updates.GetBranch(_branch);
                if (branch == null)
                {
                    Log("UpdateReconciler: could not create update branch.");
                    return;
                }
            }

            int? prevId = null;
            int created = 0, refreshed = 0;
            foreach (var rel in ordered)
            {
                var tag = rel.TagName;
                var full = AssetUrl(rel, FullClientAsset);
                if (full == null)
                {
                    Log($"UpdateReconciler: release {tag} has no {FullClientAsset}; skipping (chain not advanced).");
                    continue;
                }
                var incr = AssetUrl(rel, IncrementalAsset);       // null on the baseline -> client uses the full zip
                var manifest = AssetUrl(rel, ManifestAsset);

                var existing = db.Updates.GetUpdateByVersionName(branch.branch_id, tag);
                if (existing != null)
                {
                    if (existing.full_zip != full || existing.incremental_zip != incr ||
                        existing.manifest_url != manifest || existing.last_update_id != prevId)
                    {
                        db.Updates.UpdateArtifacts(existing.update_id, full, incr, manifest, prevId);
                        refreshed++;
                    }
                    prevId = existing.update_id;
                    continue;
                }

                var id = db.Updates.AddUpdate(new DbUpdate
                {
                    version_name = tag,                 // matches the client's version.txt and the advertised version
                    branch_id = branch.branch_id,
                    full_zip = full,
                    incremental_zip = incr,
                    manifest_url = manifest,
                    server_zip = null,                  // server self-update isn't used under Docker
                    last_update_id = prevId,            // chain to the previous release
                    flags = 0,
                    publish_date = DateTime.UtcNow,                // published immediately (no restart needed)
                    deploy_after = DateTime.UtcNow.AddMinutes(-5),  // in the past so /userapi/update returns it
                });
                prevId = id;
                created++;
            }

            Log($"UpdateReconciler: branch '{_branch}' — {ordered.Count} releases, {created} added, {refreshed} refreshed.");
        }

        private static int ParseVersion(string tag, string branch)
        {
            var s = tag.Substring(branch.Length + 1);
            return int.TryParse(s, out var n) ? n : -1;
        }

        private static string AssetUrl(Release rel, string name)
        {
            var a = rel.Assets?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return a?.BrowserDownloadUrl;
        }
    }
}
