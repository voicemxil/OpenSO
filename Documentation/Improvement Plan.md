# Improvement Plan

Recommendations for the codebase, testing, and CI — planning only, nothing here has been
implemented. Ordered by expected value. Written 2026-07 after a full-repo review.

## 1. Testing (the biggest gap: the solution has zero test projects)

CI currently proves only that every publish target compiles. The highest-leverage additions, in
order:

1. **VM determinism harness** (highest value). Desync is the #1 gameplay bug class and nothing
   guards it. Two cheap-to-build, high-yield tests:
   - *Twin-VM test:* run two `VM` instances from the same initial state, feed identical commands
     for N ticks, and assert state equality (serialize via `VMMarshal` and compare bytes/hashes).
     Any nondeterminism (unordered iteration, stray `Random`) fails fast.
   - *Late-join test:* marshal VM A at tick T, hydrate VM B from it, run both for N more ticks,
     compare. This is exactly the failure mode that stateful NPC code caused before the Social
     Bunny system was made stateless.
   Neither needs copyrighted game files if run against synthetically-built lots/objects; if
   synthetic content is too fiddly at first, gate these behind an env var pointing at local game
   files and run them as a manual/self-hosted job.
2. **Replay regression tests.** `NetPlay/Drivers/VMFSORDriver.cs` already plays back recorded
   sessions. Committing a few short recordings and asserting the final state hash gives broad
   regression coverage of the interpreter, routing, and motives for almost no test-authoring cost.
   The same harness doubles as an **interpreter-vs-Roslyn-JIT equivalence test** (run the replay
   with JIT on and off, compare hashes) — the JIT silently diverging from the interpreter is
   currently unguarded.
3. **File-format round-trip tests** for `tso.files` (IFF/FAR/HIT read → write → read, compare) using
   small synthetic fixtures. Pure functions, no game files needed, catches parser regressions that
   today only surface as in-game corruption.
4. **Migration tests.** Spin up SQLite (already a supported backend, zero infra) in CI, run
   `DbChangeTool` over the full manifest, assert clean apply and idempotent re-run. Optionally a
   MariaDB service container for the MySQL path.
5. Wire the above into a `dotnet test` job in `dotnet.yml` (ubuntu is enough) so tests gate every
   push the same way the publish matrix does.

## 2. CI / build

- **Adopt `packages.lock.json`** (`RestorePackagesWithLockFile`). Enables setup-dotnet's built-in
  NuGet cache (simpler than the current hand-rolled cache), makes restores reproducible, and
  guards against upstream package tampering (`--locked-mode` in CI).
- **Add `dotnet format`/analyzer lint job** once an `.editorconfig` exists (see §3) — style-only at
  first (`--verify-no-changes` on changed files), not analyzers, to avoid a wall of legacy warnings.
- **Replace the hand-maintained csproj list in `docker/Dockerfile`.** It breaks whenever project
  references change. Options: copy all `**/*.csproj` preserving structure in one layer (tar trick or
  a small script), or `dotnet subset`/solution-filter approach. Any of these removes a recurring
  manual-sync failure mode.
- **Deltas for non-Windows clients.** Incremental updates are win-x64 only; linux/osx users
  re-download full zips. FSO.DeltaGen is platform-agnostic — extending the release job is mostly
  YAML.

## 3. Code quality / hygiene

- **Add a repo-wide `.editorconfig`** (currently only the JS admin webapp has one). Start with
  whitespace/encoding/naming rules that match existing style; add analyzers as warnings-not-errors.
- **Nullable reference types on leaf/new projects only.** The core is a pre-nullable codebase with
  `<Nullable>disable</Nullable>`; wholesale enablement would be noise. Recommended policy: new
  projects (e.g. future test projects) enable it; existing projects stay as-is.
- **Delete dead weight** (small PRs, low risk):
  - `FSO.Server.Framework` — an empty stub project (`Class1.cs` only).
  - `TSOClient/MigrationBackup/`, `Performance1.psess` (profiler session), stray
    `GlobalSettings.Designer.cs`/`.settings` at solution root if genuinely unused.
  - `.gitmodules` still declares `assimp-net` (not even initialized) and submodules only the
    mobile targets use — document or prune.
  - Deduplicate the two identical `dotnet-tools.json` manifests (`TSOClient/.config` vs
    `TSOClient/tso.content/.config`) or add a comment stating which one the shader script uses,
    so version bumps don't drift.
- **Dead/stale docs:** `Documentation/Building FreeSO.md`, `Initial Setup.md`, and
  `FSO.Server.Core/README.md` still describe the .NET Framework 4.5 / .NET Core 2.2 era. Either
  update or stamp a "LEGACY — see README.md / CLAUDE.md" header; agents and new contributors
  reliably get misled by these.

## 4. Server / ops

- **Secrets hygiene in `docker/`**: compose defaults to `DB_PASSWORD=password`; fine as a fallback
  but DEPLOY.md should require the `.env` override, and `config.json`'s SMTP fields shouldn't invite
  committed credentials. Consider `env_file` + variable substitution into config.json at entrypoint.
- **Health/observability**: the game server has no healthcheck in compose (only mariadb does) and
  no metrics. A cheap `/health` on the UserApi + compose healthcheck would make the nightly
  auto-update timer safer (don't `up -d` over a healthy stack into a broken image).
- **`db-init` on every container start is convenient but unaudited** — a bad migration applies to
  prod automatically on the nightly update. Consider logging the applied-changes diff to a
  persistent file, or requiring `idempotent: false` scripts to be acknowledged via env var.

## 5. Rendering / content pipeline

- **Local shader compile without Windows** is the biggest dev-loop pain (mgcb needs Wine off-Windows).
  A devcontainer or documented Wine setup for Linux, or a small GH Action workflow_dispatch that
  compiles shaders for a branch and commits the .xnb, would let non-Windows contributors iterate.
- **Guard against .fx/.xnb drift at CI level**: the compile-shaders job could diff its freshly
  compiled output against the committed `.xnb` and warn (not fail) when they differ, so drift is
  visible in the PR instead of discovered later.
- **SM3 headroom tracking**: TAA.fx repeatedly hit ps_3_0 limits (X4505). A CI annotation of
  instruction/register counts per technique (mgcb verbose output) would show how close each change
  is to the cliff.

## Suggested sequencing

| Phase | Items |
|---|---|
| Now (1–2 PRs each, immediate payoff) | Test project scaffold + twin-VM determinism test; SQLite migration test; `.editorconfig`; delete dead projects/files; legacy-doc banners |
| Next | Replay regression harness + JIT equivalence; packages.lock.json; Dockerfile csproj-closure fix; compose healthcheck |
| Later | File-format round-trips; non-Windows deltas; shader drift/register-count CI annotations; Wine-based local shader compile |
