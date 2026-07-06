# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OpenSO is a modernized, self-hostable client + server for *The Sims Online*, forked from FreeSO
(a C#/MonoGame reimplementation of the original game engine). Everything builds from one solution:
`TSOClient/FreeSO.sln` (~44 projects, .NET 9, MonoGame 3.8.5-preview.2 from NuGet). The engine also
loads The Sims 1 content (Simitone lineage), so many systems have parallel TSO/TS1 paths.

`Documentation/Building FreeSO.md` is **legacy upstream documentation** (VS2019, .NET Framework 4.5,
Protobuild) — do not follow it. The current build is plain `dotnet` CLI on net9.0, and the git
submodules in `Other/libs` (FSOMonoGame, FSOMina.NET, assimp-net) are **only needed for the iOS/Android
targets**; desktop client and server builds use NuGet packages and the committed (non-submodule)
libraries in `Other/libs`, so `git submodule update --init` is not required. CI checks out without
submodules.

## Build & run commands

Requires the .NET 9 SDK. There are **no test projects in the solution** — CI verifies compilation
only, so "does it publish for every RID" is the de-facto test suite.

```bash
# Client (pick the entry project for the platform):
dotnet publish TSOClient/FSO.Windows -c Release -r win-x64 --self-contained   # Windows client (OpenSO.exe)
dotnet publish TSOClient/FSO.IDE     -c Release -r win-x64 --self-contained   # Windows client + Volcanic IDE (what CI/release ship)
dotnet publish TSOClient/FSO.Unix    -c Release -r linux-x64 --self-contained # Linux (also osx-x64 / osx-arm64)

# Server:
dotnet publish TSOClient/FSO.Server.Core -c Release -r linux-x64 --self-contained

# Quick compile check of a single project (much faster than publish):
dotnet build TSOClient/tso.simantics/FSO.SimAntics.csproj -c Release
```

- `TSOClient/FSO.Unix/deploy.sh` builds + installs + launches the client locally (macOS/Linux).
- `-3d` launch flag enables 3D mode.
- The client needs original TSO game files at runtime; the repo is engine-only.

Running the server locally: copy `TSOClient/FSO.Server/config.sample.json` to `config.json` next to
the built binaries (it drives everything; `appsettings.json` is only logging), create a MariaDB/MySQL
database, then `dotnet FSO.Server.Core.dll db-init` followed by `dotnet FSO.Server.Core.dll run`.
The full production stack (server + MariaDB + Caddy) is `docker/docker-compose.yml`; from-zero
runbook in `docker/DEPLOY.md`.

## CI / release pipeline (.github/workflows)

- **dotnet.yml** — per-push CI on every branch: recompiles shaders from source on Windows, then
  publishes the client for all four supported RIDs (win-x64 via FSO.IDE; linux-x64/osx-x64/osx-arm64
  via FSO.Unix) and the server for win-x64 + linux-x64. R2R is disabled per-push for speed.
- **release.yml** — triggered by `v*` / `dev-*` / `alpha-*` / `beta-*` tags or manual dispatch. Same
  matrix with R2R on, stamps `version.txt` from the tag, bundles FSO.Patcher into the win-x64 client,
  builds+pushes `ghcr.io/voicemxil/openso-server` (`:<version>` + `:release`), creates the GitHub
  release, and generates a win-x64 incremental delta vs the previous stable release via FSO.DeltaGen.
- **docker.yml** — pushes `:edge` server image on main (dev builds only, never deployed).
- **delta-backfill.yml** — manual regeneration of a release's incremental delta.

`TSOClient/Directory.Build.props` turns on deterministic/CI builds **under GitHub Actions only** so
release deltas stay small — identical source must produce byte-identical DLLs. Don't add
machine-specific or timestamp-embedding build steps.

## Critical conventions and traps

### Shaders ship as committed .xnb — source can silently drift
`TSOClient/tso.content/ContentSrc/Effects/*.fx` is the source; the game loads the **committed**
compiled effects in `TSOClient/tso.content/Content/DX/Effects/` (Windows) and `Content/OGL/Effects/`
(DesktopGL). The in-build MonoGame content task is disabled, so **editing a .fx does nothing at
runtime until the .xnb is recompiled**. CI recompiles both sets fresh on every push/release
(`.github/scripts/compile-shaders.sh`) and overlays them into published clients, so shipped builds
always match source — but a local run uses the committed .xnb. Recompilation only works on Windows
(MonoGame's effect compiler needs Wine elsewhere): `cd TSOClient/tso.content && dotnet tool restore`,
then run the compile script via git-bash.

Shaders compile under the **Reach profile (shader model 3.0)** for the OGL target. ps_3_0 has hard
register/instruction limits — feature-heavy shader code must be gated behind SM4
(`#if SM4`-style / `FeatureLevelTest.cs` at runtime) or the OGL compile fails with X4505-class errors
even though the DX build passes. Always assume a shader change must compile for **both** DX and OGL;
CI's compile-shaders job is the arbiter. `*iOS*.fx` variants and `LightingCommon.fx` (an #include)
are excluded from the desktop compile.

### The SimAntics VM is deterministic lockstep — desync is the #1 gameplay bug class
The server runs the authoritative VM; every client runs a mirror VM fed only input commands
(`tso.simantics/NetPlay/`). `VM.InternalTick(tickID)` must produce **identical results on every
machine**. When touching anything under `tso.simantics`:
- No `Random`, `DateTime.Now`, dictionary-order iteration, float nondeterminism sources, or state
  derived from anything not shared. Use the VM's synced state, `tickID`, and `VMContext` RNG.
- Prefer **stateless tick systems** derived entirely from shared entity state.
  `Entities/VMSocialBunnySystem.cs` is the reference pattern (deliberately field-free; every decision
  recomputed from the entity list + tickID) — earlier stateful versions caused duplicates and desyncs.
- Anything that must persist or reach late-joining clients has to be in marshalled VM state
  (`VMMarshal`), not in C# fields.
- Object behavior itself is data-driven: BHAV bytecode trees (from IFF content) interpreted by
  `Engine/VMThread`, with opcodes <256 dispatching to C# primitives in `Primitives/` (registered in
  `VMContext.cs`), and ≥256 calling other BHAVs. Engine-side gameplay hooks usually land in
  `VMGenericTSOCall.cs` / `VMGenericTS1Call.cs` or a new primitive.

### Database changes are manifest-driven migrations
Add a new SQL file under `TSOClient/FSO.Server.Database/DatabaseScripts/changes/NNNN_name.sql` plus an
entry (fresh GUID, `idempotent` flag, optional `requires` GUIDs) in `DatabaseScripts/manifest.json`.
Never edit an already-shipped script: `DbChangeTool` hashes each script (whitespace-normalized MD5)
against the `fso_db_changes` table and flags edits as MODIFIED. Migrations apply via
`FSO.Server.Core.dll db-init` (docker entrypoint auto-runs it). The DAL (`FSO.Server.Database/DA/`)
is Dapper with an interface + `Sql*` implementation per entity, and supports **both MySQL and
SQLite** — new queries must work on both (or be handled in `SqliteCompat/`).

### docker/Dockerfile lists FSO.Server.Core's csproj closure by hand
The restore layer copies every .csproj the server depends on, explicitly, for layer caching. If you
add/remove a project reference reachable from FSO.Server.Core, update `docker/Dockerfile` or the
image build breaks.

### Style
- C# with `<Nullable>disable</Nullable>` in the core projects; no repo-wide .editorconfig, no
  warnings-as-errors. This is a 10+ year old codebase — match surrounding style, don't modernize
  wholesale, and expect pre-nullable idioms.
- Shell scripts must keep LF endings (enforced via .gitattributes) — they run on the Linux deploy box.

## Architecture (big picture)

Dependency layering, bottom up (DI is Ninject; networking is Mina.NET + Lidgren):

- **FSO.Common** (`tso.common/`) — platform env, config, math; depends on nothing else.
- **FSO.Files** (`tso.files/`) — readers/writers for the original game's binary formats: FAR archives,
  IFF chunks (BHAV/OBJD/STR…), HIT/XA/UTK audio.
- **FSO.Content** (`tso.content/`) — asset resolution on top of FSO.Files. `Content.cs` is the central
  registry of lazy `*Provider`s; `TS1/` providers handle The Sims 1 content. `FSO.Content.TSO` holds
  TSO-specific embedded content.
- **FSO.SimAntics** (`tso.simantics/`) — the gameplay VM (see traps above). `FSO.SimAntics.JIT[.Roslyn]`
  optionally translates BHAV trees to compiled C# for speed; the interpreter is the fallback.
- **FSO.LotView** (`tso.world/`) — lot/world rendering: tiles, walls, lighting (`LMap/`), 3D
  reconstruction (`RC/`), lot facades. TAA/TAAU temporal upscaling lives in `Utils/TAAResolve.cs` +
  `ContentSrc/Effects/TAA.fx` and is an area of active development.
- **FSO.Vitaboy** (`tso.vitaboy.model` data + `tso.vitaboy.engine` animation/skinning) — avatars.
- **FSO.HIT** (`tso.sound/`) — bytecode-scripted audio VM.
- **FSO.UI** (`FSO.UI/`) — UI framework/widgets, `GlobalSettings` (client settings; default API URL
  `https://api.openso.org`, overridable with F1 on the login screen).
- **FSO.Client** (`tso.client/`) — ties it all together: `TSOGame.cs` (MonoGame Game), `Regulators/`
  (connection state machines for login/city/lot), `Network/`, `Rendering/`.

Server side:

- **FSO.Server** — server roles + admin CLI tools (`ITool` dispatch in `Program.cs`): `Servers/City/`,
  `Servers/Lot/` (`LotHost` runs the authoritative SimAntics VMs), `Servers/UserApi/`, `Servers/Tasks/`
  (cron jobs). **FSO.Server.Core** is the net9.0 host; **FSO.Server.Api.Core** the ASP.NET Core
  account/registration API.
- **FSO.Server.Protocol** — the wire stack: **Aries** (framing) → **Voltron** (client↔city PDUs) and
  **Gluon** (server↔server, city↔lot); **CitySelector**/**Electron** handle login/auth.
- **FSO.Common.DataService** (in `FSO.Server.DataService/` — folder name ≠ assembly name) — the
  reflective, ID-addressed object graph that syncs live avatar/lot/city state between server and
  clients (`DataServiceWrapperPDU`).
- **FSO.Server.Database** — Dapper DAL + migrations (see traps above).

Other entry points: **FSO.IDE** ("Volcanic" — live object/BHAV editor, ships inside the Windows
client), **FSOFacadeWorker** (headless lot-thumbnail renderer), **FSO.Patcher** (client self-update
applier), **FSO.DeltaGen** (release delta generator), **FSO.Server.Updater** (server watchdog).

## Where things go

- New engine-side gameplay/NPC behavior → stateless system or primitive in `tso.simantics`
  (see VMSocialBunnySystem pattern), never in the server domain layer — it must run in every VM.
- Rendering/post-processing → `tso.world` (+ shader source in `tso.content/ContentSrc/Effects`,
  remembering the .xnb recompile and SM3/SM4 gating).
- Persistent player/world data → `FSO.Server.Database` DA pair + migration; expose to clients via
  DataService or a Voltron PDU.
- Client UI → `FSO.UI` (framework) / `tso.client/UI` (screens/panels).
