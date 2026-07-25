<p align="center">
  <img src="https://github.com/voicemxil/openso-branding/raw/refs/heads/main/openso-icon-flat.svg" alt="OpenSO Icon" width="128" height="128">
</p>

# <div align="center">OpenSO</div>


**OpenSO** is a fan-hosted client and server for *The Sims Online*, built on the
[FreeSO](https://github.com/riperiperi/FreeSO) engine.

Faithful to the original game, OpenSO carries forward FreeSO’s improvements such as hardware rendering, dynamic lighting, high-resolution output, multi-floor homes, and optional 3D view, while modernizing the codebase to .NET 10 with a cleaned-up dependency stack that retires legacy build and networking components. It offers high-refresh rendering, scalable post-processing and anti-aliasing, cross-platform releases, and improved building/hosting infrastructure.

See **[openso.org](https://openso.org)**
for downloads, news, and account registration.

>OpenSO depends on the original game files (objects, avatars, UI) to function. It is simply a game
engine and contains no copyrighted material in and of itself; hosts and players supply their own copy
of the original game. The project has no affiliation with Electronic Arts or Maxis.

> **Built on FreeSO.** OpenSO is a fork of [FreeSO](https://github.com/riperiperi/FreeSO) by Rhys
> Simpson (riperiperi) and contributors, used under the Mozilla Public License 2.0. Huge thanks to the
> FreeSO project and community — without their work, OpenSO would not exist.

## What OpenSO adds over upstream

* **Modern foundation and toolchain:** the whole solution builds with the plain `dotnet` CLI on **.NET 10** with MonoGame from NuGet — no Protobuild or Visual Studio requirement. 
* **New rendering improvements:** a consolidated in-game graphics settings dialog provides granular resolution and AA options. An optional post processing stack for 3D mode adds depth & velocity buffer support and unlocks several effects: FXAA/SMAA/TAA, scaling options, motion blur, and AO/bloom.
* **Dynamic high framerate support:** the original renderer was limited to a fixed 60Hz. Ours uses delta time for a fully unlocked and dynamic FPS, matching your GPU and monitor. The new render scale setting lets you fine-tune the graphics workload, whether you have a potato laptop or want to supersample on a high-end PC.
* **Modernized networking:** the legacy Mina.NET stack is replaced with a custom async
  `TcpClient`/`SslStream` transport, with opt-in **TLS 1.2/1.3** for client↔server traffic via the
  `tls://` host scheme.
* **Self-update pipeline:** the client updates itself to the latest live version, including incremental release deltas on Windows with a
  full-download fallback. The server ships as a container image for one-command upgrades, and can also update on a routine.
* **OpenSO Launcher:** The [OpenSO Launcher](https://github.com/voicemxil/OpenSO.Launcher) built on native C# and Avalonia is the cross-platform installer and updater for OpenSO, providing a straightforward way to install, update, and repair the game on Windows, macOS, and Linux.
* **Turnkey server hosting:** a Docker Compose stack (game server + MariaDB + Caddy HTTPS) with email-verification registration and admin web panel.
* **Automated releases:** tag-driven CI builds versioned client packages for Windows, Linux,
  and macOS (x64 + Apple Silicon), recompiling shaders from source on every build. macOS gets a native app bundle instead of a folder of loose files.

# The Sims 1 via Simitone

The FreeSO engine is also a base for an ongoing re-implementation of The Sims 1's engine,
[Simitone](https://github.com/riperiperi/Simitone). The content system, HIT VM, and SimAntics VM in
this repo support both TSO and TS1 game files — meaning TS1 will run in a limited sense under TSO's UI
frontend. Simitone fully restores TS1 gameplay with a suitable UI frontend.
*Simitone is not in active development under OpenSO at the moment - other than the foundation it is in the same state as upstream.*

# 3D Mode

OpenSO supports 3D mode which lets you see the game from a different perspective, switching to an alternate camera with different controls when enabled. 3D meshes are reconstructed at runtime from the z-buffers included with object sprites, and 3D geometry for walls and floors is generated at runtime.
Community-made meshes are available for a large number of objects thanks to [FreeSO's remesh pack](https://github.com/riperiperi/FSO.Remeshes).

The mode can be enabled via the launch parameter `-3d`.

# Volcanic (object IDE)

Volcanic is an extension that lets you view, modify, and save game objects alongside a live instance of
the SimAntics VM. It features a vast array of resource editors for objects — most prominently the script
editor — for creating new objects and debugging existing ones. Volcanic also functions when the engine
has loaded TS1 objects and other resources. The Windows client release ships with Volcanic included.

# Building

The only prerequisite is the [.NET 10 SDK](https://dotnet.microsoft.com/download) (MonoGame is pulled
in via NuGet). Everything builds from one solution, `TSOClient/FreeSO.sln`:

```bash
# Windows client (OpenSO.exe)
dotnet publish TSOClient/FSO.Windows -c Release -r win-x64 --self-contained

# Windows client + Volcanic IDE (what releases ship)
dotnet publish TSOClient/FSO.IDE -c Release -r win-x64 --self-contained

# macOS / Linux client (also osx-x64 / osx-arm64)
dotnet publish TSOClient/FSO.Unix -c Release -r linux-x64 --self-contained

# Server
dotnet publish TSOClient/FSO.Server.Core -c Release -r linux-x64 --self-contained
```

The client needs the original TSO game files at runtime — point it at (or place next to it) a copy of
`The Sims Online` from the final TSO release.

> Note: [`Documentation/Building FreeSO.md`](<Documentation/Building FreeSO.md>) describes the
> **legacy upstream** build (Visual Studio 2019 / Protobuild) and does not apply to OpenSO.

# Running a server

* **Production:** see [`docker/DEPLOY.md`](docker/DEPLOY.md) for the full from-zero runbook — Docker
  stack (game server + MariaDB + Caddy HTTPS), DNS, and email-verification registration. Prebuilt
  server images are published to GHCR on every release.
* **Manual:** copy `TSOClient/FSO.Server/config.sample.json` to `config.json` next to the built
  binaries, create a MariaDB/MySQL database, then run `dotnet FSO.Server.Core.dll db-init` followed by
  `dotnet FSO.Server.Core.dll run`. Further guides live in [`Documentation/`](Documentation).

Clients can point at any server: press **F1** on the login screen to change the API address.

# Contributing

You can contribute by testing the latest releases, filing bugs, and opening pull requests. CI builds
every push for all supported platforms, so a green build across the matrix is the baseline for any
change. For engine internals, the upstream FreeSO documentation is still a useful reference:

* [Project Structure (upstream)](https://github.com/riperiperi/FreeSO/wiki/Project-structure)
* [Coding Standards (upstream)](https://github.com/riperiperi/FreeSO/wiki/Coding-standards)

# License

> This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
> If a copy of the MPL was not distributed with this file, You can obtain one at
> http://mozilla.org/MPL/2.0/.
>
> OpenSO is a fork of FreeSO and retains FreeSO's MPL-2.0 license and attribution. See
> [`NOTICE.md`](NOTICE.md).
