# TAA Temporal Systems — contract, history state, tuning flow

*2026-07-09. Companion to the `claude/temporal-contract` change set. Read alongside the header
comments in `TAA.fx`, `TAAResolve.cs`, `TemporalHistoryState.cs`, `TemporalFrameContract.cs`,
and `TAATuning.cs` — this file records the architecture and the audit findings; the code comments
record the local rules.*

## The two systems

**TemporalFrameContract** (`tso.common/Utils/TemporalFrameContract.cs`) — everything the resolve
consumes for one frame, built in one place (`TAAResolve.BuildContract`) with its coherence rules
(`Ready`) attached. Color, velocity, history handles, jitter deltas, dimensioning, blend/window
parameters, and the resolve **tier**. If a new input is added to the resolve, it goes through the
contract, not through a new static read at a new time.

**TemporalHistoryState** (`tso.common/Utils/TemporalHistoryState.cs`) — the persistent state:
history/meta ping-pong targets plus an explicit record of the *layout* they were written with
(owner presenter, tier Full/Lite/Debug, TAAU flag). The resolve declares its layout each frame
(`BeginResolve`); any mismatch clears history first. `Invalidate(reason)` covers events the
signature can't see. History resets are no longer implicit "the shader's rejection heuristics
will eat it" events — they are logged, reasoned state transitions.

Invalidation triggers now wired:

| Trigger | Mechanism |
|---|---|
| TAADebug toggle (either direction) | tier in layout signature |
| Full ↔ Lite tier switch | tier in layout signature |
| TAAU / upscaler toggle | upscale flag in signature |
| Lot ↔ city presenter switch | `DeclareOwner` (World instance vs city token) |
| New lot on the same World instance | explicit `InvalidateTAAHistory("blueprint init")` |
| Resize / render-scale / fp16-fallback change | reallocation (always cleared) |
| Camera cut / teleport (future) | call `PPXDepthEngine.InvalidateTAAHistory(reason)` at the site |

The debug-exit bug this kills: TAADebug wrote diagnostics into meta.GB; the first frame after
toggling debug off decoded them as ~100 px/frame phantom velocity — a screen-wide one-frame trust
wipe plus a multi-frame re-deepening. Tier-change invalidation makes the exit frame an ordinary
first frame.

## One capability predicate

`World.TaaCapable(cfg)` = `cfg.TAA && WorldContent.TAA != null` is the single root for MSAA
stripping, resolve wiring (lot + city), and the jitter gate (which additionally requires
`TAAFunc != null` and allocated history — jitter is a strict subset of resolve, so jitter without
a consuming resolve is structurally impossible). The old predicates also required
`WorldContent.MotionBlur != null` — a vestige; the resolve never uses that effect, and a missing
MotionBlur.xnb silently disabled TAA while the jitter gate (which never checked it) kept shaking
the image.

## Tuning: one preset, three consumers

`TAATuning.cs` is canonical. The game uploads every public static float via reflection each frame
(`TAAResolve.UploadTunables`: `X` → uniform `TuneX`, `LiteX` → `LiteX`) and **audits the loaded
effect once** — missing uniforms and baked shader defaults that differ from TAATuning are reported
(`TAAResolve.BindingWarnings`), not silently absorbed. The lab source-links `TAATuning.cs`
(no project reference) so its live values and reset table initialize from shipped defaults.
The `TAA.fx` initializers are fallback-only and were re-synced (three had drifted:
MotionTrustCap 0.65→0.75, RawSoftenSlope 2.2→0.0, RawSoftenMotionSup 0.85→0.65 — the Lab and a
stale-xnb fallback were testing/shipping a tuning that included a soften pass production had
deliberately turned off). Adding a tunable is now a two-file change (TAATuning + TAA.fx uniform);
upload, missing-check, and drift-check are automatic. **Shader .xnb rebuild is CI-only** (Windows
job, `.github/scripts/compile-shaders.sh`) — after any .fx edit the committed xnb is stale until CI
recompiles; the binding audit flags exactly this state at runtime.

## Verified cost numbers (2026-07-09 audit, by tap enumeration)

Texture fetches per output pixel: **TAA_Core 39** at native / mild TAAU (≤1.5x ratio), **43** at
heavy TAAU (>1.5x), **35** on the SM3/GL full path (no featReject taps, no hLow); **TAALite 30**
everywhere. Both tiers run at output resolution under TAAU. No GPU timestamps exist yet — a
per-tier frame-time budget still needs measuring before more shader logic is added.

## Audit findings — applied

- **Dead `closestMask`** write in the velocity loop removed (its consumer was removed earlier).
- **Debug-exit contamination** — fixed structurally (tier-change invalidation, above).
- **Doc drift corrected in TAA.fx:** fp16 depth-reject epsilon (0.0005→0.0015, matching the C#
  upload), oscillation evidence-wipe knee (0.25→0.4), velocity-saturation arithmetic (64px was
  1280-wide-era; ~96px at 1920), the stale "cycleWindow mirrors cycleCeil" alignment claim
  (divisors diverged 1.0 vs 1.2 in 2026-07-05), the stale "storedMove matches moveGate" claim
  (moveGate retuned to 0.6..2.0, storedMove kept 0.35..1.5), the false "suspicion = union of
  every detector" claim (featReject is absent), and the false "sampleConf saturates at native by
  construction" claim in TAALite (assumed zero jitter; TAA always jitters).
- **TAAResolve/TAA docs**: the resolve runs *after* spatial AA (FXAA/SMAA at render-res under
  upscale), not "replacing" it; GL is no longer Lite-only (technique is user-selected, both run
  on both backends).
- **Lab metric parity blindness fixed**: the auto-tuner's half-res point-sampled metric grid read
  a single fixed output-pixel parity — with the scene's single-pixel geometry, lines on the other
  three parities were invisible to the optimizer, spatially and temporally. The sampling offset
  now rotates through all four parities on a two-frame cadence (control and eval share the
  frame-indexed schedule), and the temporal flicker term only compares the same-parity frame
  pairs, with per-phase counts keeping normalization exact. Same readback and cache cost.
  Scores are not comparable with pre-fix runs. The tuner remains candidate generation, not proof:
  one synthetic 240-frame sequence, no real-scene captures or holdout paths yet.

## Audit findings — second round, APPLIED 2026-07-09 (user-approved; need CI shader recompile + lab re-tune)

All of the former "deferred A/B candidates" are now in, with revert signatures documented at each
site in TAA.fx. **The committed .xnb is stale until CI recompiles**, and the auto-tuner should be
re-run afterwards — scores are not comparable with earlier runs (the metric parity fix alone
guarantees that), and two of these changes alter tuning semantics:

1. **featReject joined the suspicion union** — the resolution-widened variance box now narrows on
   feature-level ghost evidence. Failure signature: slight fizzle on *moving* fine detail at
   upscale; demote to `featReject * 0.5` before removing.
2. **TAALite native confidence fade** — the injection throttle fades out toward 1:1
   (`lerp(1, confMul, saturate(upscaleRatio - 1))`), mirroring TAA_Core's explicit native
   exemption. Kills the jitter-phase accumulation "breathing" at native; Lite may read slightly
   rawer at rest — that's the levers working, retune LiteRespEnd/LiteConfFloor if needed.
3. **storedMove unified on `TuneMoveGateLo/Hi`** — remembered motion now arms on the same band as
   current motion (was the stale pre-retune 0.35..1.5 constants).
4. **Reactive 0.85 trust cap removed** — the N-cap to 8 already bounds trust at 0.889. Revert
   signature: screen-wide aliased pulse the frame the camera stops (restore line documented
   in-shader).
5. **motionBoost re-keyed onto the gate band (both tiers)** — was UV-keyed, saturating at ~96
   px/frame, i.e. practically inert at gameplay speeds. Now `moveGate * MotionBoostMax` /
   `moveGate * LiteMotionBoost`: the lever finally does what its docs claimed. **This raises raw
   injection during ordinary motion — MotionBoostMax/Floor and LiteMotionBoost (0.35, tuned while
   inert) likely want to come DOWN in the lab.**
6. **biasPenalty sigma gate narrowed to its unique 0.03..0.12 band** — below that the texDetail
   floor already scrubs; failure signature: similar-color ghost residue returning on *very* flat
   content at upscale.
7. **Karis anti-flicker weight keyed on `dispCurr.x`** (displayed luma) — bit-neutral while
   RawSoftenSlope = 0, correct if soften returns. (The soften path itself stays: live tunable
   with a documented restore recipe.)
8. **Lab GrowOffPhase floored at 0.25** (slider + optimizer bounds) — RGBA8 meta quantization
   makes lower values mean "no growth", not slower growth; the optimizer can no longer tune
   inside the dead zone.
9. **Lite's N counts age, not evidence** — annotated only; tier-switch invalidation prevents the
   age-as-evidence import into Full, so this is contained (unchanged this round).

## Audit claims checked and REJECTED

- *"VelGatePxScale ignores SSAA>1 so motion gates arm at half speed when supersampling"* — false:
  at SSAA>1 the resolve runs at native resolution after the box downsample (`DrawBackbuffer`'s
  nonNative slot precedes the TAA slot), history is viewport-sized, and velocity is stored in UV —
  so `velPx = |v| * texSize` is already native px. The scale-up-by-SSAA in `World.PreDraw` applies
  to *jitter* (applied pre-downsample on the supersampled grid), which is correct and unrelated.
- Verified sound, no action: YCoCg pair, Mitchell/Lanczos/Catmull-Rom kernels + normalization +
  hull clamps, oscillation sign/EMA pack collision-freedom, warmup ordering (fresh pixels cannot
  be starved), division floors, MAX_ACCUM=128 window arithmetic, ghost/depth sign conventions.

## Where the next visual win actually is (unchanged conclusion)

Better inputs, not more resolve heuristics: a strict motion-vector contract for every writer
(avatars, skinned meshes, animated materials, transparencies), explicit camera-cut signals wired
to `InvalidateTAAHistory`, UI composited after the upscaler, and a capture/replay harness feeding
the lab real lot/city sequences instead of the one synthetic scene. Per-material temporal flags
can ride the existing velocity MRT's spare channels or a repurposed third target on DX — derived
from material *class* at draw time (alpha-blended, water, animated texture, particle), not
hand-authored per asset.
