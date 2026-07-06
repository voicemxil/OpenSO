---
name: deploy-and-release
description: Cutting OpenSO releases and deploying the server — tag-driven release.yml, version.txt update flow, win-x64 incremental deltas (FSO.DeltaGen), GHCR images (:release vs :edge), delta backfill, and the docker/Caddy production stack. Use when releasing, debugging the update/patcher flow, editing workflows, or touching docker/.
---

# Release & deploy (OpenSO)

## Release pipeline (`.github/workflows/release.yml`)

- **Trigger:** push a tag — `v<MAJOR>.<MINOR>.<PATCH>` (current scheme), legacy `dev-N`, or
  `alpha-*`/`beta-*` — or manual `workflow_dispatch` with a `version` input.
- What it does, in order:
  1. Recompiles all shaders from source on Windows (same job/cache as per-push CI).
  2. Publishes clients for the 4 RIDs (win-x64 = `FSO.IDE`; linux/osx = `FSO.Unix`) **with
     ReadyToRun ON** (unlike per-push CI), and servers for linux-x64 + win-x64. SDK pinned exactly
     (e.g. `9.0.315`) — keep the pin when editing.
  3. Stamps `version.txt` from the tag into every artifact. The login-time update check compares
     client vs server `version.txt`, so a mismatch = clients get prompted to update.
  4. win-x64 client gets `FSO.Patcher` bundled (update.exe etc.) so it can self-update in place.
  5. macOS jobs run on `macos-26` (needed for the Icon Composer `.icon` → `Assets.car` step) and
     are ad-hoc codesigned.
  6. Builds and pushes the server image to GHCR: `ghcr.io/voicemxil/openso-server:<version>` AND
     `:release` (production tracks `:release`).
  7. Creates the GitHub release with generated notes from the `OpenSO-*` artifacts.
  8. **client-delta:** finds the previous stable `vX.Y.Z` release, runs
     `dotnet run --project TSOClient/FSO.DeltaGen -c Release -- prev.zip curr.zip <version> out`
     and uploads `.incremental.zip` + `.manifest.json`. **Deltas are win-x64 only.**
- **Never add `cancel-in-progress` to release.yml** (per-push dotnet.yml has it; release builds must
  not be cancelled).

## Determinism is load-bearing for deltas

`TSOClient/Directory.Build.props` enables `ContinuousIntegrationBuild` under GitHub Actions so
identical source → byte-identical DLLs across runner VMs. Without it, every delta contained every
FSO.*.dll (~10–24 MB of churn). Do not add build steps that embed timestamps, absolute paths, or
per-machine data; if a delta suddenly balloons, suspect a determinism regression first.

## Image channels

- `:release` + `:<version>` — from release.yml; what production pulls.
- `:edge` + `:<sha>` — from docker.yml on every push to main touching `TSOClient/**` or the
  dockerfile/entrypoint. Dev-only; never deploy `:edge` to prod.
- `docker/Dockerfile` hand-lists FSO.Server.Core's csproj closure for the cached restore layer —
  update it when project references change, or the image build fails.

## Production stack (`docker/`)

`docker-compose.yml` runs three services: `mariadb` (MariaDB 11), `freeso-server`
(ghcr `:release` image; raw-TCP game ports 33100/33101 city, 34100/34101 lots, 35100/35101 tasks
published directly; API port 9000 NOT published), and `caddy` (80/443, auto-TLS reverse proxy for
`api.openso.org` → server:9000). Server config is the mounted `docker/config.json`; game files
mounted read-only at `/game`; persistent lot state in `./nfs`. The entrypoint auto-generates the
`secret` and auto-runs `db-init` on every start, so DB migrations roll out with deploys.

The from-zero runbook (DNS, firewall, .env, admin promotion, nightly auto-update systemd timer,
admin-triggered deploys via the `deploy-trigger` mount) is `docker/DEPLOY.md` — follow it rather
than improvising. `Documentation/Updates.md` explains version.txt semantics (`dev-0` = local/CI
placeholder) and the update distribution model.

## Common operations

- **Cut a release:** tag `vX.Y.Z` on the commit → push the tag → watch release.yml. Verify the
  release assets include all 4 client zips, 2 server zips, and the incremental delta.
- **Delta missing/broken for a published release:** run `delta-backfill.yml` manually with the
  version, then restart the UserApi so `UpdateReconciler` picks up the new asset.
- **Deploy to prod:** production auto-updates nightly (systemd timer running `openso-update.sh` →
  `docker compose pull && up -d`), or push the deploy-trigger per DEPLOY.md; hotfix = tag a release,
  wait for `:release`, then trigger the update path.
- **Editing workflows:** dotnet.yml (per-push) and release.yml share the shader cache key — keep
  `compile-shaders.sh`, the Effects glob, and `dotnet-tools.json` hashing consistent across both.
