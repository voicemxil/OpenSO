# FSO.Patcher (`update.exe`) — deprecated Windows recovery tool

`FSO.Patcher` builds `update.exe`, the standalone WinForms updater that ships next to the Windows
client. It applies a chain of downloaded patch zips (and manifest-driven file removals) in place,
then relaunches `OpenSO.exe`.

## Status: deprecated / recovery-only

During the migration to the native **OpenSO Launcher**, `update.exe` is a **temporary,
Windows-only compatibility and recovery tool** — it is not the primary update mechanism and is not
being extended. The in-game updater that drives it
(`tso.client/Controllers/UpdateController.cs`) is now gated to Windows: on macOS/Linux/mobile the
game diverts to a "download the right build" notice instead of launching the patcher (the patch
payloads and `update.exe` itself are Windows-only). The `CLIPatcher` (Unix console) path in this
project therefore only survives for the rare case where a Windows install is being patched from a
mono-style environment; new platforms should use the Launcher.

Plan for this tool: keep it **safe** for the migration window, then retire it once the Launcher
fully owns install/update/self-update on every platform.

### Windows recovery scenarios that still require `update.exe`

While the migration is in progress, `update.exe` is still the mechanism for:

- **In-place incremental patching of an existing Windows install** — the in-game updater downloads
  `PatchFiles/path{i}.zip` (+ optional `path{i}.json` removal manifests), then hands off to
  `update.exe` (`UpdateController.RestartGamePatch()` → `.\update.exe`) because the running game
  can't overwrite its own files.
- **Replacing a locked/in-use `OpenSO.exe`** — the patcher renames `OpenSO.exe` → `OpenSO.exe.old`
  once the game has exited, then swaps in the new build (it can't be done from inside the game).
- **"Unopenable client" self-recovery** — launched with no queued patch, `update.exe` offers to
  re-download a neutral full client from the release channel (`FormsPatcher.EmergencyDownload`).
- **Repairing an install after a partial/failed update** — because the patcher backs up every file
  it touches under `updateBackup/` and can roll the install back to its pre-update state.

## Archive-extraction security policy (shared with the Launcher)

Every patch zip is fetched over the network and is treated as **untrusted**. Extraction enforces the
**same policy the OpenSO Launcher uses** (see
`OpenSO.Launcher/BUILD_AND_TEST.md` → "Archive-extraction security policy" and
`OpenSO.Launcher/.../Extraction/ArchivePathGuard.cs`). The policy lives here in
[`ArchivePathGuard.cs`](ArchivePathGuard.cs) and is applied before any disk write:

- **Canonicalize + relative-path containment.** Each entry is resolved with `Path.GetFullPath`, then
  `Path.GetRelativePath(installRoot, target)` must be non-rooted and must not start with `..`. This
  cannot be prefix-bypassed (a sibling like `install-evil/…` is rejected).
- **Reject up front:** rooted/absolute entry paths, any `..`/`.` component, empty path components
  (`a//b`), and backslashes (so `a\..\b` can't be reinterpreted as traversal on Windows).
- **Reject symlink/special-file entries** (unix mode `S_IFLNK`, device/fifo/socket in the zip's
  external attributes) — a symlink could redirect a later entry's write outside the install dir.
- **Validate the whole archive, then extract.** `ReversiblePatcher.Validate()` checks **every**
  entry (files *and* directories) before a single file is written; the **first** bad entry rejects
  the **entire** archive with **nothing written** (no partial extraction of an unvalidated archive).
  The legacy `Patcher` form applies the same up-front validation.

## Transactional apply, rollback, and failure result

The patcher applies each step as a transaction that can be rolled back
([`ReversiblePatcher.cs`](ReversiblePatcher.cs)):

- Before overwriting or removing a file, the current copy is backed up under `updateBackup/`.
- **Overwrite/extract failures** leave the failing entries in `ToExtract`; important ones surface a
  dialog. On abort the step is **reverted** from `updateBackup/`.
- **Manifest-driven removals** (`RemoveFiles`) now **return success/failure**. A failed removal
  (backup-copy or delete threw) is treated as a **failed transaction**: the step is reverted and the
  update fails — it is never silently swallowed and finalized. (An unsafe removal path in the
  manifest is refused and logged, not acted on.)
- **`Final()`** (write the "done" state: delete backups + the consumed zip and advance) runs **only
  when every step of that patch succeeded**.

**Failure result / exit semantics:** any failure (unsafe/corrupt/missing archive, unwritable files
the user aborts on, or a failed removal) routes through a single failure path (`FailUpdate` in the
CLI/Forms drivers) that: rolls back the current step, restores `OpenSO.exe` from `OpenSO.exe.old`,
shows a **visible error** (MessageBox on Windows, `stderr` on the CLI), and exits with a **non-zero
exit code**. A failed update never launches the game or reports success; the install is left
**restorable** (previous files and `OpenSO.exe` back in place). Success — and only full success —
relaunches `OpenSO.exe` and exits `0`.

## Build

Targets `net10.0-windows` (WinForms, `OutputType=WinExe`, assembly name `update`). It builds on
non-Windows hosts with Windows targeting enabled:

```bash
dotnet build FSO.Patcher.csproj -p:EnableWindowsTargeting=true -c Debug
```
