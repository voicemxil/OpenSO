---
name: build-and-verify
description: How to build, publish, and verify changes in OpenSO given there are no automated tests — which project to build for a given change, the CI matrix as the de-facto test suite, and platform gotchas. Use before claiming any OpenSO change "works", and when setting up builds or debugging CI failures.
---

# Build & verify (OpenSO)

## Reality check

- One solution: `TSOClient/FreeSO.sln`, .NET 9, MonoGame from NuGet (3.8.5-preview.2).
- **There are no test projects.** CI (`.github/workflows/dotnet.yml`) verifies that every supported
  publish target compiles — that matrix is the de-facto test suite. "It builds on my platform" is
  not verification; cross-platform/cross-RID breakage is the common failure.
- Runtime verification needs original TSO game files, which CI and most dev containers don't have.
  Be honest in reports about what was compile-verified vs. runtime-verified.
- Ignore `Documentation/Building FreeSO.md` (legacy upstream: VS2019/.NET Framework/Protobuild).
  Git submodules are NOT needed for desktop/server builds (only iOS/Android).

## Fast verification ladder (cheapest first)

1. **Build the project you changed** (seconds):
   `dotnet build TSOClient/<project>/<name>.csproj -c Release`
2. **Build a top-level consumer** to catch API breaks — the client pulls in nearly everything:
   `dotnet build TSOClient/tso.client/FSO.Client.csproj -c Release`
   (for server-side changes: `dotnet build TSOClient/FSO.Server.Core -c Release`)
3. **Publish the affected CI targets** (what CI actually runs):
   ```bash
   dotnet publish TSOClient/FSO.IDE  -c Release -r win-x64   --self-contained -p:PublishReadyToRun=false
   dotnet publish TSOClient/FSO.Unix -c Release -r linux-x64 --self-contained
   dotnet publish TSOClient/FSO.Unix -c Release -r osx-x64   --self-contained   # + osx-arm64
   dotnet publish TSOClient/FSO.Server.Core -c Release -r linux-x64 --self-contained  # + win-x64
   ```
4. **Push and watch CI** — it runs on every branch push and cancels superseded runs, so pushing
   early is cheap. Shader changes are ONLY truly verified by the `compile-shaders` job unless you
   compiled both DX+OGL locally on Windows (see the shader-pipeline skill).

If `dotnet` is missing in the environment, install the .NET 9 SDK (e.g.
`curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0`) rather than
skipping verification. If installation is impossible, push and let CI verify — and say that's what
happened.

## Which entry project maps to what

| Change area | Build/publish this |
|---|---|
| Engine libs (tso.common/files/content/simantics/world/vitaboy/sound, FSO.UI) | `tso.client/FSO.Client.csproj`, then the client matrix |
| Windows client behavior | `FSO.IDE` (this is what CI/release ship for Windows; it embeds the full client + Volcanic) |
| Linux/macOS client | `FSO.Unix` |
| Server, DB, protocol, API | `FSO.Server.Core` (both linux-x64 and win-x64 publish) |
| Lot thumbnails | `FSOFacadeWorker` |
| Updater/patching | `FSO.Patcher`, `FSO.DeltaGen` |

## Platform gotchas that break the matrix

- `FSO.IDE`/`FSO.Windows` target `net9.0-windows` (WinForms) — they only build on Windows runners;
  don't add references to them from cross-platform projects.
- Code paths differ DX vs DesktopGL (`MonoGame.Framework.WindowsDX` vs `.DesktopGL` packages);
  graphics feature detection goes through `tso.common/Utils/FeatureLevelTest.cs`.
- Shell scripts must stay LF (`.gitattributes` enforces); they run on the Linux deploy box.
- Reproducible builds: `TSOClient/Directory.Build.props` enables deterministic CI builds so release
  deltas stay small. Don't introduce build steps that embed timestamps, absolute paths, or
  machine-specific data into assemblies.
- Adding/removing a project reference reachable from `FSO.Server.Core`? Update the hand-maintained
  csproj-copy list in `docker/Dockerfile` or the server image build breaks.

## Runtime smoke (when game files are available)

- Client: `TSOClient/FSO.Unix/deploy.sh` builds+installs+launches (macOS/Linux); `-3d` flag for 3D
  mode; F1 on the login screen overrides the API URL.
- Server stack: `docker compose -f docker/docker-compose.yml up --build -d` (MariaDB + server +
  Caddy); server alone: `config.sample.json` → `config.json`, `dotnet FSO.Server.Core.dll db-init`,
  then `run`.
