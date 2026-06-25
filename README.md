# OpenSO

**OpenSO** is a modernized, self-hostable client and server for *The Sims Online*, built on the
[FreeSO](https://github.com/riperiperi/FreeSO) engine. It is a full reimplementation of TSO using
MonoGame, faithful to the original game while adding quality-of-life improvements such as hardware
rendering, custom dynamic lighting, hi-res output, modern anti-aliasing, and 2+ floor houses. OpenSO
is a technology base for running your own The Sims Online server. See **[openso.org](https://openso.org)**
for downloads, news, and account registration.

OpenSO depends on the original game files (objects, avatars, UI) to function. It is simply a game
engine and contains no copyrighted material in and of itself; hosts and players supply their own copy
of the original game.

> **Built on FreeSO.** OpenSO is a fork of [FreeSO](https://github.com/riperiperi/FreeSO) by Rhys
> Simpson (riperiperi) and contributors, used under the Mozilla Public License 2.0. Huge thanks to the
> FreeSO project and community — without their work, OpenSO would not exist.

# The Sims 1 via Simitone

The engine is also a base for an ongoing re-implementation of The Sims 1's engine,
[Simitone](https://github.com/riperiperi/Simitone). The content system, HIT VM, and SimAntics VM in
this repo support both TSO and TS1 game files — meaning TS1 will run in a limited sense under TSO's UI
frontend. Simitone fully restores TS1 gameplay with a suitable UI frontend.

# 3D Mode

OpenSO supports a 3D mode that lets you see the game from a different perspective. 3D meshes are
reconstructed at runtime from the z-buffers included with object sprites, and 3D geometry for walls and
floors is generated at runtime, switching to an alternate camera with different controls when enabled.

The mode can be enabled via the launch parameter `-3d`.

# Volcanic (object IDE)

Volcanic is an extension that lets you view, modify, and save game objects alongside a live instance of
the SimAntics VM. It features a vast array of resource editors for objects — most prominently the script
editor — for creating new objects and debugging existing ones. Volcanic also functions when the engine
has loaded TS1 objects and other resources.

# Building & deploying

* **Build:** one solution at `TSOClient/FreeSO.sln` (.NET 9 / MonoGame). The desktop client builds from
  `FSO.Windows` (Windows) and `FSO.Unix` (macOS/Linux), producing **`OpenSO.exe`**; the server is
  `FSO.Server.Core`.
* **Server deployment:** see [`docker/DEPLOY.md`](docker/DEPLOY.md) for the full from-zero runbook
  (Docker stack: game server + MariaDB + Caddy HTTPS, DNS, and email-verification registration).
* **Website:** the static site (landing page, news, account registration) lives in its own repo and is
  served at [openso.org](https://openso.org).

# Contributing

You can contribute by testing features in the latest releases, filing bugs, and joining the discussion.
For engine internals, the upstream FreeSO documentation is still the best reference:

* [Project Structure (upstream)](https://github.com/riperiperi/FreeSO/wiki/Project-structure)
* [Coding Standards (upstream)](https://github.com/riperiperi/FreeSO/wiki/Coding-standards)

## Prerequisites

* [.NET 9 SDK](https://dotnet.microsoft.com/download)
* [MonoGame](http://www.monogame.net)

# License

> This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
> If a copy of the MPL was not distributed with this file, You can obtain one at
> http://mozilla.org/MPL/2.0/.
>
> OpenSO is a fork of FreeSO and retains FreeSO's MPL-2.0 license and attribution. See
> [`NOTICE.md`](NOTICE.md).
