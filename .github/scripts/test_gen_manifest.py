#!/usr/bin/env python3
"""
Verification gate for gen-manifest.py (area d).

Exercises the per-RID client manifest generator end to end by invoking it as a subprocess against
throwaway artifact directories:
  - delta case: a Windows full + incremental with a resolved previous version emits a deltas entry
  - baseline: with no previous version, no deltas are emitted even if an incremental is present
  - missing RID: a RID whose full zip is absent is omitted (never substituted)
  - empty dir: refuses to write a manifest and exits non-zero
  - sha256: the emitted digest matches a known-answer hash of the full package
  - schema: schemaVersion / version are stamped as documented
  - usage: the wrong number of arguments exits non-zero

Pure stdlib (unittest), so CI runs it with `python3 .github/scripts/test_gen_manifest.py` - no pip deps.
"""
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "gen-manifest.py")
REPO = "owner/OpenSO"
# Known-answer: sha256(b"hello").
HELLO_SHA256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"


def write(path, data):
    with open(path, "wb") as f:
        f.write(data)


def run(version, prev, art_dir, out_file):
    return subprocess.run(
        [sys.executable, SCRIPT, version, prev, art_dir, REPO, out_file],
        capture_output=True, text=True)


class GenManifestTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.art = os.path.join(self.tmp, "art")
        os.makedirs(self.art)
        self.out = os.path.join(self.tmp, "openso-manifest.json")

    def full(self, rid, data):
        write(os.path.join(self.art, "OpenSO-client-{}.zip".format(rid)), data)

    def incremental(self, rid, data):
        write(os.path.join(self.art, "OpenSO-client-{}.incremental.zip".format(rid)), data)

    def load(self):
        with open(self.out) as f:
            return json.load(f)

    def test_delta_case_emits_windows_delta_with_correct_hashes(self):
        self.full("win-x64", b"hello")
        self.incremental("win-x64", b"world")
        r = run("v0.2.0", "v0.1.0", self.art, self.out)
        self.assertEqual(r.returncode, 0, r.stderr)

        m = self.load()
        self.assertEqual(m["schemaVersion"], 1)
        self.assertEqual(m["version"], "v0.2.0")

        win = m["clients"]["win-x64"]
        self.assertEqual(win["full"]["sha256"], HELLO_SHA256)  # known-answer
        self.assertEqual(
            win["full"]["url"],
            "https://github.com/owner/OpenSO/releases/download/v0.2.0/OpenSO-client-win-x64.zip")

        self.assertIn("deltas", win)
        self.assertEqual(len(win["deltas"]), 1)
        delta = win["deltas"][0]
        self.assertEqual(delta["from"], "v0.1.0")
        self.assertEqual(delta["sha256"], hashlib.sha256(b"world").hexdigest())
        self.assertIn("OpenSO-client-win-x64.incremental.zip", delta["url"])

    def test_baseline_no_prev_omits_deltas(self):
        self.full("win-x64", b"hello")
        self.incremental("win-x64", b"world")  # present, but should be ignored without a prev
        r = run("v0.1.0", "", self.art, self.out)
        self.assertEqual(r.returncode, 0, r.stderr)

        win = self.load()["clients"]["win-x64"]
        self.assertEqual(win["full"]["sha256"], HELLO_SHA256)
        self.assertNotIn("deltas", win)

    def test_missing_rid_is_omitted_not_substituted(self):
        # Only linux built this release; the others must simply not appear.
        self.full("linux-x64", b"penguin")
        r = run("v0.3.0", "", self.art, self.out)
        self.assertEqual(r.returncode, 0, r.stderr)

        clients = self.load()["clients"]
        self.assertEqual(set(clients.keys()), {"linux-x64"})
        self.assertNotIn("win-x64", clients)
        self.assertNotIn("osx-arm64", clients)
        self.assertIn("skip win-x64", r.stdout)

    def test_all_rids_present(self):
        for rid in ("win-x64", "linux-x64", "osx-x64", "osx-arm64"):
            self.full(rid, ("pkg-" + rid).encode())
        r = run("v0.4.0", "", self.art, self.out)
        self.assertEqual(r.returncode, 0, r.stderr)

        clients = self.load()["clients"]
        self.assertEqual(set(clients.keys()), {"win-x64", "linux-x64", "osx-x64", "osx-arm64"})
        self.assertEqual(
            clients["osx-arm64"]["full"]["sha256"],
            hashlib.sha256(b"pkg-osx-arm64").hexdigest())

    def test_empty_dir_fails_nonzero_and_writes_no_manifest(self):
        r = run("v0.5.0", "", self.art, self.out)
        self.assertEqual(r.returncode, 1)
        self.assertFalse(os.path.exists(self.out))
        self.assertIn("refusing to write an empty manifest", r.stderr)

    def test_wrong_arg_count_exits_nonzero(self):
        r = subprocess.run(
            [sys.executable, SCRIPT, "v0.1.0", "only-three", "args"],
            capture_output=True, text=True)
        self.assertNotEqual(r.returncode, 0)

    def tearDown(self):
        import shutil
        shutil.rmtree(self.tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
