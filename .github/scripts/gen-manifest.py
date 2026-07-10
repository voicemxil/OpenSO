#!/usr/bin/env python3
"""
Build the per-RID client distribution manifest (openso-manifest.json) for a release.

Scans <artifact-dir> for the release's client artifacts (downloaded via `gh release download`),
computes SHA-256 for each, and emits the manifest documented in Documentation/update-manifest.md.
This manifest is the platform-aware distribution index the OpenSO Launcher consumes: one entry per
RID this release actually built, each with a hash-verified full zip (plus, for Windows, the optional
in-game delta). It is distinct from fso_updates / OpenSO-client-win-x64.manifest.json, which is the
Windows-only in-game patch chain.

Rules enforced here (see the accepted plan):
  - Only RIDs whose full client zip is present are emitted (a missing RID is omitted, never a generic
    fallback - the launcher errors on an unknown RID rather than substituting another platform's build).
  - The full package is mandatory per emitted RID; SHA-256 is always computed.
  - Deltas are optional and Windows-only for now, included only when the incremental zip is present and
    a previous version was resolved.

usage: gen-manifest.py <version> <prev-or-empty> <artifact-dir> <owner/repo> <out-file>
"""
import hashlib
import json
import os
import sys

# RIDs release.yml builds a full client zip for. Order here is cosmetic (JSON object key order).
RIDS = ["win-x64", "linux-x64", "osx-x64", "osx-arm64"]
# Deltas are produced Windows-only today (FSO.DeltaGen and the in-game patcher are Windows-only).
DELTA_RIDS = {"win-x64"}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if len(sys.argv) != 6:
        sys.stderr.write(__doc__)
        return 2
    version, prev, art_dir, owner_repo, out_file = sys.argv[1:6]
    base = "https://github.com/{}/releases/download/{}".format(owner_repo, version)

    clients = {}
    for rid in RIDS:
        full_name = "OpenSO-client-{}.zip".format(rid)
        full_path = os.path.join(art_dir, full_name)
        if not os.path.isfile(full_path):
            print("skip {}: {} not present".format(rid, full_name))
            continue
        entry = {"full": {"url": "{}/{}".format(base, full_name), "sha256": sha256(full_path)}}

        if rid in DELTA_RIDS and prev:
            incr_name = "OpenSO-client-{}.incremental.zip".format(rid)
            incr_path = os.path.join(art_dir, incr_name)
            if os.path.isfile(incr_path):
                entry["deltas"] = [{
                    "from": prev,
                    "url": "{}/{}".format(base, incr_name),
                    "sha256": sha256(incr_path),
                }]

        clients[rid] = entry

    if not clients:
        sys.stderr.write("no client zips found in {}; refusing to write an empty manifest\n".format(art_dir))
        return 1

    manifest = {"schemaVersion": 1, "version": version, "clients": clients}
    with open(out_file, "w") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
