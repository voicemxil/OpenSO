---
name: feature-routing
description: Deciding WHERE a new OpenSO feature belongs and tracing end-to-end flows across client, protocol, server, and VM layers — with worked examples (player action, persistent data, UI panel, city-level feature). Use when starting any multi-layer feature, when unsure which project should own code, or when tracing how an existing feature works.
---

# Feature routing (OpenSO)

Use this decision table before writing code. Putting logic in the wrong layer is the classic
mistake: gameplay in the server domain layer desyncs nothing but also reaches no clients; client-only
gameplay desyncs multiplayer.

## Decision table

| The feature is... | It belongs in... |
|---|---|
| In-lot behavior every player must see identically (NPCs, motives, object logic, socials) | `tso.simantics` — stateless tick system or primitive (READ the simantics-determinism skill first) |
| Triggered by a player action | A `VMNetCommand` in `tso.simantics/NetPlay/Model/Commands/` (server validates; never trust client fields) + UI hook in `tso.client` |
| Persistent across sessions (currency, unlocks, records) | `FSO.Server.Database` DA + migration (db-migrations skill), surfaced via DataService or a Voltron PDU |
| Live city/global state (top100, neighborhoods, mail) | Server domain (`FSO.Server/Servers/City/`, `FSO.Server.Domain`) + `FSO.Common.DataService` sync |
| Client↔city messaging | New PDU pair in `FSO.Server.Protocol/Voltron/Packets/` + handlers both sides |
| City↔lot-host coordination | `FSO.Server.Protocol/Gluon/` |
| Out-of-game HTTP (accounts, admin, registration) | `FSO.Server.Api.Core` controllers |
| Rendering / post-processing / lighting | `tso.world` (+ `tso.content/ContentSrc/Effects` — shader-pipeline skill) |
| Avatar appearance/animation | `tso.vitaboy.model` (data) / `tso.vitaboy.engine` (runtime) |
| HUD/screens/panels | `tso.client/UI/` using `FSO.UI` framework widgets; wire via `Controllers/` |
| New asset type / game-file parsing | Format reader in `tso.files`, provider in `tso.content` (mind the TSO vs TS1 split) |
| Settings | Client: `FSO.UI/GlobalSettings.cs`; Server: `config.json` → `ServerConfiguration.cs`; runtime-tunable gameplay: the tuning system (`Documentation/Tuning.md`) |

## Worked flow traces (follow these when building analogous things)

**Player clicks a pie-menu interaction on an object:**
`tso.client` UI → `VMNetInteractionCmd` (NetPlay command) → server `VMServerDriver` validates &
schedules → command broadcast to all clients → every VM queues the interaction on the avatar →
BHAV tree runs in `VMThread`, opcodes dispatch to `Primitives/` → world state changes render via
`tso.world` components observing entities.

**Login → in-lot (connection lifecycle):**
`tso.client/Regulators/LoginRegulator` (auth via CitySelector/Electron HTTP) →
`CityConnectionRegulator` (Aries session + Voltron PDUs to City server) → city hands off via
`LotConnectionRegulator` → lot host (`FSO.Server/Servers/Lot/LotHost`) streams `VMMarshal` state +
subsequent tick commands.

**Live-synced profile data (e.g. avatar description):**
Client requests by ID via `DataServiceWrapperPDU` → `FSO.Common.DataService` resolves through a
server Provider (often backed by a `FSO.Server.Database` DA) → mutations flow back as data service
updates with server-side permission checks in the provider.

## Cross-cutting rules

- The engine supports TS1 too: engine-generic changes should either handle the TS1 path
  (`TS1/` providers, `VMGenericTS1Call`) or be explicitly TSO-gated.
- `FSO.Server.Framework` is an empty stub — don't put anything there.
- Folder ≠ assembly in places: `FSO.Server.DataService/` builds `FSO.Common.DataService`;
  `tso.client/` builds `FSO.Client.csproj`. Glob for `*.csproj` when unsure.
- Volcanic (`FSO.IDE`) is the live object/BHAV editor — invaluable for inspecting SimAntics state
  when testing by hand on Windows.
- New project references reachable from `FSO.Server.Core` require a matching edit in
  `docker/Dockerfile`'s restore layer.
