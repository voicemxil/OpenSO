# FSO.TAALab

**Status: EXPERIMENTAL.** This is a developer tool, not a production project. It is not built by
CI, not part of any release artifact, and is not an authoritative reference for how OpenSO's
temporal anti-aliasing behaves — see [Not a reference](#not-a-reference-for-production-taa) below
before using it to justify a production change. Owner: repo maintainer. This status may be
revisited later.

## What it is

`FSO.TAALab` is a standalone, interactive TAA-tuning harness. It renders a small synthetic scene
(plus a few real game meshes for texture/geometry stress-testing) and runs it through the game's
**actual compiled TAA resolve shader** (`Content/DX/Effects/TAA.xnb`, loaded straight from
`tso.content`'s DirectX/SM4 content build — not re-implemented or approximated), with every
`Tune*`/`Lite*` uniform exposed as a live ImGui slider. The idea is to let the temporal-resolve
constants be refined visually, frame by frame, without restarting the full game. It also includes
a deterministic scripted evaluation sequence (rest / motion / abrupt-reveal / slow-creep phases)
and a small dependency-free Nelder-Mead optimizer (`AutoTuner.cs`) that can auto-search the
tunable space against that sequence.

It intentionally has **no project reference to any `tso.*` assembly**. The small amount of driver
logic it needs (jitter sequence, resolve uniform upload, velocity-buffer conventions) is
hand-mirrored from the production sources, with comments in the code marking each mirrored piece.

## What it mirrors (the production source of truth)

TAALab duplicates behavior from the following production files. If you change TAA behavior in the
game, these are the files to change — **not** anything in `FSO.TAALab`:

- `TSOClient/tso.world/Utils/TAAResolve.cs` — the real resolve driver (`TAAResolve.Draw`): sets up
  and uploads every uniform the shader reads, exactly what `RunResolve` in `LabGame.cs` mirrors.
- `TSOClient/tso.world/Utils/TAATuning.cs` — single source of truth for the `Tune*`/`Lite*`
  tuning-constant defaults. TAALab's `Tunables` class in `LabGame.cs` is a mutable copy of these
  values for interactive experimentation; a validated value only ships once it's copied back here.
- `TSOClient/tso.common/Utils/R2Jitter.cs` — the cycled Halton(2,3) sub-pixel jitter sequence.
  Mirrored verbatim in `LabGame.cs` (`SampleHalton`/`HaltonValue`/`HaltonCycle`).
- `TSOClient/tso.content/ContentSrc/Effects/TAA.fx` — the actual shader source (compiled to the
  `TAA.xnb` that TAALab loads and runs). TAALab does not fork or re-author this shader.
- `TSOClient/tso.world/World.cs` and `TSOClient/tso.world/WorldContent.cs` — where the production
  client wires jitter, the velocity buffer, and the TAA effect into the real render pipeline
  (`World.PreDraw`, `World.ChangeAAMode`/`ConfigureCityAA`, `WorldContent.TAA`).

## Not a reference for production TAA

TAALab is useful for *exploring* tuning values quickly, but it is a synthetic harness: a small
scripted scene, a handful of borrowed meshes, no real lots, no multiplayer, no full game content
pipeline. Matching behavior in TAALab does not prove correctness in the actual client. **Any change
to production TAA/resolve behavior must be validated by running the real client** (`FSO.Windows` /
`FSO.Unix`) against real content, not merely by observing TAALab. Treat TAALab as a scratchpad for
forming a hypothesis, and the files listed above as the only place that hypothesis becomes real.

## Why net9.0-windows

OpenSO's client and server target **.NET 10** (see `Documentation/Building FreeSO.md`). This
project deliberately stays on `net9.0-windows` and is not aligned with that requirement — it isn't
part of the shipped client/server and there is no plan to migrate it. It's also Windows-only on
purpose: it references `MonoGame.Framework.WindowsDX` directly (no `FSO.Windows`-style
MonogameLinker DLL swap), because five of the tunable uniforms
(`TuneRawSoftenOnset`/`Slope`/`MotionSup`, `TuneRingLo`/`Hi`) live in `#if SM4` blocks in `TAA.fx`
that only the DirectX/SM4 shader build (not the MojoShader/OpenGL build) binds.

## Build / run (Windows only)

1. Make sure the DirectX content is built: `TSOClient/tso.content/Content/DX/Effects/TAA.xnb` and
   `LabVelocity.xnb` must exist (see "Content Build" in `Documentation/Building FreeSO.md` — this
   step must run on Windows). These are checked in as part of the normal content build output, so a
   fresh Windows checkout typically already has them.
2. From `TSOClient/FSO.TAALab/`, run:
   ```
   dotnet run
   ```
   (or open/build the project directly in Visual Studio — it is **not** part of `FreeSO.sln`, see
   below, so it must be opened/built as a standalone project.)
3. Controls: Space = pause, R = reset history, T = toggle TAA on/off, P = print the current tuning
   values as a ready-to-paste `TAATuning.cs` block. The ImGui panel exposes sliders for every
   tunable, technique A/B (`TAA` vs `TAALite`), render-scale presets, and the auto-tuner.

## Solution / CI status

`FSO.TAALab` is **not** listed in `TSOClient/FreeSO.sln`, and none of the workflows in
`.github/workflows/` (`dotnet.yml`, `release.yml`, `docker.yml`, `delta-backfill.yml`) build or
reference it. It does not build as part of `dotnet build`/`dotnet publish` against the solution,
is not part of CI, and is not included in any release artifact. Building it means building this
project directly, as described above.
