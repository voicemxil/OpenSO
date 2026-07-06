---
name: simantics-determinism
description: Making safe changes to the SimAntics VM (tso.simantics) — determinism/lockstep rules, desync avoidance, stateless tick systems, primitives, dialogs, and NPC patterns. Use for ANY change under TSOClient/tso.simantics (gameplay, NPCs, motives, socials, object behavior, dialogs, routing).
---

# SimAntics VM: determinism rules (OpenSO)

## Why this matters

The server runs the authoritative VM; **every client runs a mirror VM** that receives only input
commands (`tso.simantics/NetPlay/`). `VM.InternalTick(tickID)` must produce byte-identical results
on every machine, every tick. A change that works fine in single-player can silently desync
multiplayer — the #1 gameplay bug class in this codebase. There are no tests to catch it; only
discipline.

## Hard rules — never do these inside tso.simantics

- **No `new Random()`, `Guid.NewGuid()`, `DateTime.Now/UtcNow`, `Environment.TickCount`.**
  Use `context.NextRandom(n)` (VMContext's synced RNG) and the synced `tickID` / `Context.Clock`.
- **No iteration over unordered collections** (`Dictionary`, `HashSet`) where order affects outcomes.
  Iterate `VM.Entities` (ordered) or sort first.
- **No state in C# fields that isn't marshalled.** Anything that must survive save/load or reach a
  late-joining client (clients that join mid-session receive `VMMarshal` state, not your object's
  fields) must live in marshalled VM state — entity attributes/data, `VMTSOEntityState`, or the
  marshal structs in `Marshals/`. A plain field on a system/entity class will be default-valued on
  a fresh client → divergence.
- **No decisions based on client-only inputs** (graphics settings, platform, wall-clock, whether a
  UI panel is open). The VM cannot know it's on a client.
- Float math is OK (it's used throughout) but avoid anything whose result depends on platform
  intrinsics or timing.

## The blessed pattern: stateless tick systems

`Entities/VMSocialBunnySystem.cs` is the reference implementation for engine-side NPC/gameplay
systems, hard-won across several desync fixes:

- The system class holds **no fields** — every tick, every decision is recomputed from shared state:
  the entity list, entity attributes, motives, and `tickID`.
- It is invoked from `VM.InternalTick` via `Context.SocialBunnySystem.Tick(this, tickID)`
  (see `VM.cs`), so it runs identically everywhere.
- NPC identity is encoded in synced entity state (`PrivateToPersistID`, ephemeral IDs based at
  `0xF0000000`), not in a C# map.
- Spawn/despawn uses hysteresis on synced motive values (spawn at Social < −50, despawn at > +20)
  so it can't flap and every VM agrees.
- Blocking dialogs are auto-answered by inspecting synced dialog state
  (`Engine/VMDialogHandler.cs`, `Primitives/VMDialogPrivateStrings.cs`), and ask-style socials are
  auto-accepted — again purely from shared state.

Copy this shape for any new ambient/NPC behavior. If you're about to add a field, ask: "what does a
client that joins 10 minutes from now see?" If the answer isn't "exactly the same value," it must be
marshalled or derived.

## How object behavior executes (mental model)

- Objects/avatars are `VMEntity` (`VMGameObject` / `VMAvatar`) in `Entities/`.
- Behavior is **BHAV bytecode trees** loaded from IFF content (`FSO.Files` → `FSO.Content`),
  interpreted by `Engine/VMThread` + `VMStackFrame`.
- Each BHAV node has an opcode: **< 256 → C# primitive** in `Primitives/` (registered into a
  256-slot table in `VMContext.cs` via `AddPrimitive`); **≥ 256 → gosub** into another BHAV.
- Engine-side hooks for game-wide behavior usually land in `Primitives/VMGenericTSOCall.cs`
  (TSO) or `VMGenericTS1Call.cs` (TS1) rather than a brand-new primitive.
- Routing (walking to objects) goes through `Engine/Routing/`, `VMRouteFinder`, and SLOT parsing.
- The Roslyn JIT (`FSO.SimAntics.JIT.Roslyn`) compiles hot BHAVs to C#; it must remain
  behavior-identical to the interpreter. If you change interpreter semantics (a primitive's
  behavior, operand decoding), check whether `FSO.SimAntics.JIT` mirrors that logic.

## User-visible actions come in as commands

Player inputs arrive as `NetPlay/Model/Commands/` (`VMNetCommand` subclasses) validated and
scheduled by the server driver (`NetPlay/Drivers/VMServerDriver.cs`) then broadcast. New
player-triggered features need a command class (keep validation server-side: never trust client
fields) — not a direct client-side VM mutation.

## Checklist before committing a tso.simantics change

1. No forbidden nondeterminism sources (grep your diff for `Random`, `DateTime`, `Guid`).
2. New persistent state is marshalled (or the system is stateless); saving/loading a lot and a
   late-join both reproduce it.
3. Iteration order over entities/collections is deterministic.
4. TS1 path considered (`VMGenericTS1Call`, `TS1/` content) if the change is engine-generic.
5. If interpreter semantics changed, JIT equivalence checked.
6. Build check: `dotnet build TSOClient/tso.simantics/FSO.SimAntics.csproj -c Release` compiles
   (it's near the bottom of the dependency stack, so also build `tso.client` to catch API breaks).
