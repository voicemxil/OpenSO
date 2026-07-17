# Building OpenSO

> This document originally described FreeSO's legacy Visual Studio/.NET Framework build (targeting
> pack workarounds, Protobuild, Azure Pipelines). OpenSO has since moved the whole solution to
> modern SDK-style projects on a single .NET SDK, so that history no longer applies — this page
> describes the current build.

Historically FreeSO's client and server targeted different, older runtimes and had to be built on
Windows even in CI. OpenSO's client and server now both build against the **same .NET 10 SDK**, and
build/publish cleanly from the `dotnet` CLI on Windows, Linux, and macOS (see `.github/workflows/dotnet.yml`
and `release.yml`, which build every supported platform on every push/release). Visual Studio is still a
perfectly good editor for the solution, but it is no longer required.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) — the only SDK required for the client and server.
- Git, cloned **with submodules** (`git clone --recurse-submodules`, or `git submodule update --init` after
  a plain clone) — `Other/libs/FSOMonoGame`, `Other/libs/FSOMina.NET`, and `Other/libs/assimp-net` are
  submodules that a handful of `Other/libs/*` projects in the solution reference directly.
- Visual Studio 2022 (optional) or any editor/IDE that understands SDK-style `.csproj` files.

MonoGame itself comes in via NuGet (`MonoGame.Framework.DesktopGL` / `MonoGame.Framework.WindowsDX`,
referenced directly by the client projects) — no separate MonoGame install or Protobuild project
generation step is needed.

One exception: `FSO.TAALab`, a standalone TAA-tuning dev harness (not part of the shipped client or
server), still targets `net9.0-windows` — it's an experimental tool, not a production
project, so it doesn't need to factor into your SDK choice. See
[`TSOClient/FSO.TAALab/README.md`](../TSOClient/FSO.TAALab/README.md) for what it does, what production
TAA code it mirrors, and how to build/run it.

## Build Process

Open `./TSOClient/FreeSO.sln` in Visual Studio, or build individual projects from the command line with
`dotnet build`/`dotnet publish`. Change the active/target project to change which aspect you build:

- `FSO.Windows`: The Windows-only client (`OpenSO.exe`).
- `FSO.IDE`: The Windows client bundled with Volcanic. This is the project CI publishes for the Windows
  release asset — its output folder contains the full client plus the IDE/JIT tooling.
  - Don't always use this as your run target — the IDE can run out of memory or misbehave in multiplayer;
    it's not meant for normal play.
- `FSO.Unix`: The cross-platform client (macOS/Linux, also produces `OpenSO.exe`), published for
  `linux-x64`/`osx-x64`/`osx-arm64`.
- `FSO.Server.Core`: The server. Publish it and launch the output with `dotnet FSO.Server.Core.dll run`
  (framework-dependent) or run the self-contained executable directly, depending on how you published it.
- `FSOFacadeWorker`: A worker application that builds 3D thumbnails for properties that have been updated since their last thumbnail upload. A bit memory hungry, so closes itself after processing a few.
- `FSO.Server.Watchdog`: A legacy helper that tries to self-update the server using update data it downloaded. Superseded for Docker deployments by the image-based auto-update flow in `docker/DEPLOY.md`.

Building in Debug does make it a lot easier to make changes and debug when anything goes wrong, but it impacts performance very significantly. Don't distribute a debug build to players.

## Content Build

The repository ships prebuilt MonoGame content (shaders, fonts) for DX and OGL, but if you change a shader
or font you'll need to rebuild it. CI does this with `dotnet tool restore` (pulls the pinned `mgcb` tool)
followed by `dotnet mgcb` against the content project — see `.github/scripts/compile-shaders.sh` for the
exact commands, which you can run locally the same way (this step must run on Windows; MonoGame's effect
compiler shells out to Wine on Linux/macOS, which isn't available on CI runners).

You can find the content projects for each target in `TSOClient/tso.content/ContentSrc/`:

- TSOClientContent.mgcb: OpenGL content
- TSOClientContentDX.mgcb: DirectX content
- TSOClientContentiOS.mgcb: iOS content. Not really used now - makes some changes to shaders for OpenGL ES 2.0 support.

## CI

OpenSO builds on GitHub Actions:

- `.github/workflows/dotnet.yml` — per-push CI. Compiles shaders, then publishes the client (`FSO.IDE` for
  Windows, `FSO.Unix` for linux/osx-x64/osx-arm64) and the server (`FSO.Server.Core`, linux-x64/win-x64) as
  build artifacts. Doesn't cut a release.
- `.github/workflows/release.yml` — tag-triggered release build. Same client/server matrix, plus
  version-stamping, delta-patch generation, and the per-RID distribution manifest. See
  [Updates.md](./Updates.md) and [update-manifest.md](./update-manifest.md) for what it publishes.
- `.github/workflows/docker.yml` — builds and pushes the server's Docker image on every push to `main`.

FreeSO's original Azure Pipelines build (`windows-2019`, frozen to dodge a .NET Framework targeting-pack
issue on newer images) is no longer used by this fork.
