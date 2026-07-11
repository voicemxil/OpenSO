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

## Lab tuner upgrades — 2026-07-09, second pass (post-branch)

Rebuilt on the audited driver (not a port of the tuner-v2 code): CMA-ES default optimizer
(deterministic, ask/tell, normalized coordinates) with Nelder-Mead retained; warm start (defaults /
sliders / session best); early pruning on the monotone partial-score bound; duplicate-vector score
cache; pipelined metric readback; **8x (8K+) ground truth** via an exact box chain; **multi-scale
objective** (0.33/0.5/1.0 weighted 0.5/0.3/0.2, ground truth is output-res so scale-independent);
interactive + scored **A/B** against a baseline snapshot; **continuous self-training** (chained
cycles from best, sigma/population escalation when stagnant, stops after 3 fruitless escalations).
Reference/metric hardening from the tuner-v2 findings: **linear-light averaging** (gamma-space box
biased the reference dark on contrast edges), **sigma-0.44 out-px Gaussian reference** (the box
passed aliasing the metric treated as truth), **detail-weighted error** (weight from the control's
luma gradient, floor 0.25 / gain 8 — fine detail ~4x flat), and **Lo+Gap reparameterization** of the
paired bounds (kills the Lo==Hi collapse manifold). Lab-only shader: `LabDownsample.fx`
(BoxDecode/GaussDown). Every reference/metric change re-baselines scores; the determinism
double-eval remains the per-run validity gate. Not carried over from tuner-v2: GPU metric + batched
generations (would need the majority-vote flake defense; CPU path is flake-free), 120-frame
multi-fidelity screening (subsumed by exact-bound pruning), ghost-persistence metric term (open).

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

## Reference comparison & improvement plan — 2026-07-10

Three-way study: full audit of TAA.fx's ~35 upscaleRatio-dependent sites vs (a) FSR 2.2 / FSR 3
/ Snapdragon GSR2 / Arm ASR actual source, (b) UE4 TAAU / UE5 TSR public material, (c) the classic
TAA literature (Salvi variance clipping, Karis 2014, Playdead, Intel, Yang/Liu/Salvi survey).

**Ratio-widened clamp: precedented, ours is conservative.** FSR 2.2/GSR2/ASR widen the rectification
box at rest by `min(20, (1/(sx*sy))^3)` sigma — ~11.4σ at 0.667x, capped 20σ below ~0.58x; FSR 3
simplified to a fixed 3.0σ at rest. Our 1.5→3.0σ ramp lands on FSR3's number. Native-res TAA
(gamma≈1) and TSR (rejection, not clamping) never widen — but among shipping temporal *upscalers*,
wide-at-rest is the norm. Ours is NOT the outlier the "every widening ghosted" comment feared,
**provided the FSR safety rails exist — which ours lacks:**

1. **No true-AABB intersection.** FSR always intersects the widened sigma box with the neighborhood
   min/max (`boxMin=max(aabbMin,boxMin)`), so 20σ degenerates to a plain min/max clamp. Our box is
   unbounded; the lock stack (below) can reach an effective no-clamp.
2. **No full motion collapse.** FSR lerps the widening to 1.0σ by 20px/frame velocity or on
   disocclusion/reactive; Intel tightens gamma under motion even at native. Ours narrows
   (motion-decay/MotionClampTighten/rejTighten) but bottoms near base 1.5σ, and has NO motion
   tightening at native (MotionClampTighten is upscale-gated).
3. **Lock escape is unbounded box inflation.** The ratio widening (x2.0) and lock widening (x2.6)
   stack to 7.8σ on a locked still pixel at 0.33x — the in-shader "~3.9σ" comment assumes base
   gamma and is drift. FSR's locks instead LERP from clamped toward UNCLAMPED history, gated by
   lock/current luma similarity, cut by reactivity, killed on shading change >0.1 — the escape is
   bounded by its endpoints and cannot compound.
4. **Permanent raw-injection floor has no reference analogue** (`blend = max(blend,
   texDetail*TuneTexDetailFloor*(1-oscLock))`): fine-detail pixels that never earn a lock receive
   aliased raw forever — the prime suspect for the fizzle/stipple at full accumulation on low
   scales. References solve convergence flicker with weighting (Karis, Playdead luma feedback
   0.88–0.97, TSR flicker-period trust), never permanent injection.
5. **Karis fade under motion** (`lumaFade = motion * TuneKarisFade`, default 1.0 → fully uniform
   weighting in motion): references keep inverse-luma weighting always on; bright-highlight ringing
   is most visible exactly where Karis weighting would suppress it.
6. **Rectification hull rides the stack**: the ratio>1.5 low-frequency correction is bounded only
   by 2x-gammaEff (up to ±15.6σ when locked) — an unbounded overshoot path on bright edges.
7. Minor inconsistencies: Core's widening endpoint 2.0 hardcoded (Lite's LiteGammaScale is
   tunable); Core deepCap ratio-scaled vs Lite's flat LiteDeepCap; effective accumulation window
   (~125 frames at deepCap 0.992) far exceeds reference norms (TSR 8–32, UE4 ~25, Playdead 8–33) —
   deliberate for static isometric content, but it magnifies every escape path above.

Aligned with references (no action): YCoCg stats + soft clip, Catmull-Rom history + hull dering,
Lanczos/kscale output-sized kernel (FSR2's own recipe), jitter phases 8*ratio^2 (matches FSR),
nearest-depth dilation, reactive velocity-disparity (FSR lock-break analogue), honest disocclusion,
oscillation machinery (TSR's flicker-period pass is the same idea), input-res rectification (TSR).

### Recommendations (priority order, each behind a revert signature)

- **R1 — true-AABB intersection** of the clip box in BOTH tiers (taps already fetched; track
  min/max alongside m1/m2), applied to the main box AND the rectification safety hull. FSR-standard,
  bounds items 1/3/6 in one move. Expected: similar-color ghost reduction at upscale + ringing cap.
- **R2 — expose Core's widening endpoint** as TuneGammaScale (mirror LiteGammaScale) so the
  multi-scale auto-tuner can vote on it (FSR2 says up to AABB; FSR3 says 3.0; let the metric decide
  within [1, 3]). Fix the 3.9σ comment either way.
- **R3 — full motion collapse**: drive gammaEff to ~1.0σ under strong motion/disocclusion/reactive
  (including native — un-gate a motion tighten from upscaleRatio). Matches FSR/Intel direction; our
  historical motion-ghost regressions all came from WIDE-in-motion, so this is revert-graveyard-safe.
- **R4 — FSR-style lock escape**: replace `gammaEff *= (1+1.6*oscLock)` with
  `history = lerp(clamped, unclamped, oscLock * lumaSimilarity * (1-reactive))`; keep our lock
  entry/kill machinery (evidence wipe, still gate). Removes the 7.8σ regime entirely; R1 remains
  the backstop.
- **R5 — stipple**: fade the texDetail floor with accumulation depth (converged pixels stop
  receiving raw), or replace with Playdead-style luma-feedback response; re-tune
  TexDetailFloor/ConfFloor via multi-scale continuous auto-tune on the Gaussian reference (which,
  unlike the old box reference, no longer rewards matching the stipple).
- **R6 — ringing**: after R1, if highlight ringing persists, try TuneKarisFade < 1 (keep partial
  inverse-luma weighting under motion) before touching kernels.
- **R7 (optional) — velocity-sharpened box gather** (FSR's fRectificationCurveBias analogue) and/or
  3x3 weighted moments instead of the 5-tap plus. Modest, SM4-only.
- **Future/structural (noted, not scheduled)**: FSR3.1-style shading-change detection; TSR-style
  higher-res history (VRAM cost); both belong to the "better inputs" phase below.

**IMPLEMENTED 2026-07-10 (all of R1–R6 in one user-approved batch; revert signatures at each site
in TAA.fx):**
- R1 AABB intersection: Core (SM4-only — the aabb pair's box-taps→clamp lifetime blew the ps_3_0
  budget; SM3/GL keeps the classic sigma-only box) + the rectification hull + Lite (fits SM3).
- R2 `TuneGammaScale` (default 2.0, lab bounds [1,3]).
- R3 full motion collapse: widening decay hardened to (1−motion) (TuneGammaMotionDecay removed);
  MotionClampTighten un-gated from upscale (native full-motion ≈1.16σ).
- R4 lock escape: `history = lerp(clamped, historyRaw, oscLock)` replaces the ×(1+1.6·oscLock)
  box inflation — bounded, phase-independent, all lock kill-gates unchanged.
- R5 texDetail blend floor REMOVED (not faded — user call; no reference analogue). biasPenalty's
  sigma band restored to the full flat domain (the narrowing had delegated σ<0.03 to the floor).
  TuneTexDetailFloor removed. The native input-side raw lean (texDetail·0.75·floorScale) remains.
- R6 Karis motion fade REMOVED (weighting always on, per references; TuneKarisFade removed). The
  dark-to-light fizzle it once fixed is expected to be covered by R3+R1; revert = restore lumaFade.
- Prune: RawSoften path deleted entirely (shipped off at slope 0; kx1/filtSoft accumulators gone —
  saves loop ALU). Core tunables 22 → **17** (+GammaScale −RawSoften×3 −TexDetailFloor −KarisFade
  −GammaMotionDecay); binding audit 27/27. Lab print-block bug fixed in passing (gap-encoded
  optimizer names were leaking into the P output).
All smokes pass (full/lite/multi-scale, determinism bit-identical). Scores re-baseline AGAIN
(shader changed): new-shader defaults at 0.33x = 0.010024. Defaults are now UNTUNED for the new
mechanics — run the multi-scale continuous auto-tune before judging quality vs the old resolve.

**Scene + reference updates (2026-07-10, same session):** reference final stage is a linear-light
BOX by default (user call — the tuner must not target Gaussian softness); the sigma-0.44 Gaussian
stays as a Lab toggle (anti-alias-honest; A/B the tuning TARGETS by re-running with each; control
cache + score cache invalidate on toggle). Scene audit: every element carries a matched
velocity/depth quad; fixed the thin-line crossing whose paint order contradicted its depths (drift
line now nearer). New elements, each targeting a live artifact: bright-glint cluster (3 static
near-white sub-px dots + a sub-pixel orbiter — highlight-ringing/Karis venue), fixed-seed
fine-noise patch at 1 texel/output px (stipple/texture-crunch venue), and a VELOCITY-LESS mover
(color pass only — the animated-texture/overlay case; the clamp+feedback path must catch it).
Box-reference defaults baseline at 0.33x = 0.013509 (another re-baseline). Scene idea noted for
later: a camera-pan phase (uniform whole-scene motion, the most common game motion) — needs mesh
view-matrix pairing, deferred.

**Strict artifact-class metric (2026-07-10, same session):** three terms added to the OBJECTIVE
(not just diagnostics), all deterministic and monotone (prune bound extended):
`total = 1.0*MSE + 2.0*TD + 4.0*ghost + 3.0*fizzle + 0.03*strict`.
- *Ghost/trail persistence* (ritchie metric-v3 PersGain idea): per-pixel signed luma-error run
  counter; scores runs of 3..30 frames with a ramp-out — permanent same-sign offset is steady-state
  reconstruction bias (already scored spatially every frame), and without the fade-out the term was
  52% of the defaults total. Survives the metric parity rotation because ghosts are regional.
- *Fizzle/noise*: TAA luma change where the ground truth held still (<1/255) on a parity pair,
  scored above a 1/255 dead-band. Detail-weighted like MSE/TD.
- *Strict bad-pixel fractions*: unweighted fraction of pixel-frames with max-channel error >2/255
  (+4x extra beyond 5/255) — a small very-wrong region can no longer hide in the mean.
Defaults decomposition at 0.33x box reference: total 0.049931 = ghost 37% / strict 24% /
temporal 21% / fizzle 12% / spatial 6%. Weights are constants at the top of the metric block —
rebalance there. ANOTHER score re-baseline.

**Gamma pulled OUT of the tuner — fixed reference schedule (2026-07-10):** the first strict-metric
run drove the free base UP (Gamma 1.5->1.85, GammaScale 2.17 => ~4σ rest at 0.33x) — the classic
unconstrained-variance-clip failure the references avoid (with the AABB intersection now in place, a
4σ rest box is just "clamp to the neighborhood", so the metric happily inflates it). Gamma is now a
FIXED SCHEDULE like FSR/TSR: `GAMMA = lerp(GammaNative 1.5, GammaUpscale 3.0, ratioTerm * (1-susp) *
(1-motion))` — native rest 1.5σ (bit-identical to the old default; baseline unchanged at 0.049931),
heavy-upscale rest 3.0σ (FSR3's number, AABB-bounded), collapsing to native under motion then below
via MotionClampTighten. Endpoints stay as uniforms + lab sliders (adjustable, captured per-run so
mid-run drags stay deterministic) but are OUT of the optimizer: Core search space 17 -> **15**
params; uniform binding still 27/27 (TuneGamma/GammaScale -> TuneGammaNative/GammaUpscale). NOTE: the
posted auto-tune result was conditioned on FREE gamma, so its other 16 values are stale — re-run the
tuner (now 15-param, gamma-fixed) before trusting any of them.

**Applied tuning + sharpness fix (2026-07-10):** the 15-param gamma-fixed auto-tune result was
pasted into TAATuning.cs (and the TAA.fx fallback literals synced — audit clean, 27/27). Notable
directions the strict metric pushed: DeepCapBase 0.992->0.967 (accumulation window ~125->~30 frames,
cutting over-smoothing), ConfFloor 0.14->0.57 (more current-frame injection at upscale),
MotionClampTighten ->0.98 (barely tightens — keeps detail under motion). "Blurry at 0.5x/0.33x"
root-caused: the RCAS auto-sharpen that pairs with TAA (World.cs, on by default whenever TAA is
active, including plain TAAU) keyed its strength ONLY on output resolution, so it stayed ~0.25 across
every render scale — but TAA's history-resample low-pass GROWS with the upscale ratio, so heavier
upscale was under-compensated. Fixed: added a moderate upscale-ratio term (x1.0 native -> x1.8 at
0.33x, ceiling raised 0.5->0.6), so auto-sharpen now ~0.25 native / 0.35 at 0.5x / 0.45 at 0.33x.
Kept moderate on purpose (the highlight-ringing complaint means over-sharpen is a real risk; raise
the 1.8 multiplier / 0.6 ceiling in World.cs if still soft). This is GAME-SIDE — the Lab tests TAA
in isolation and does NOT apply RCAS, so the lab still reads softer than the game. Reconstruction-
level follow-up if the sharpen isn't enough: gate the main-box AABB intersection by motion/suspicion
(release it at clean rest so converged super-res detail isn't re-clamped to the render-res
neighborhood — FSR keeps its rest box loose and leans on RCAS, which is what we now do).

## Where the next visual win actually is (unchanged conclusion)

Better inputs, not more resolve heuristics: a strict motion-vector contract for every writer
(avatars, skinned meshes, animated materials, transparencies), explicit camera-cut signals wired
to `InvalidateTAAHistory`, UI composited after the upscaler, and a capture/replay harness feeding
the lab real lot/city sequences instead of the one synthetic scene. Per-material temporal flags
can ride the existing velocity MRT's spare channels or a repurposed third target on DX — derived
from material *class* at draw time (alpha-blended, water, animated texture, particle), not
hand-authored per asset.
