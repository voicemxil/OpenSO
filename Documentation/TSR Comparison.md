# TSR vs Cosmic TAA vs FSR2/3 — Structural Comparison

*2026-07-11. Source: full read of UE5 TSR (EpicGames/UnrealEngine `release` branch,
`Engine/Shaders/Private/TemporalSuperResolution/*` + `TemporalSuperResolution.cpp`), compared against
this repo's `TAA.fx` (TAA_Core + TAALite) and the FSR2/3 findings in `TAA Temporal Systems.md`.
Companion to that file's "Reference comparison & improvement plan" section.*

**State caveat:** the shipping `TAA.fx` analyzed here is main's resolve plus R4 (the FSR-style lock
escape, commit `50868376a`); the 2026-07-10 reference-alignment batch (R1/R2/R3/R5/R6) remains
reverted pending re-evaluation.

---

## A. TSR architecture, pass by pass

TSR is ~10 compute passes per frame, all wave-op/LDS-heavy CS. Frame-persistent state
(`FTSRHistoryTextures`): **ColorArray** (history res, `PF_FloatR11G11B10` by default via
`r.TSR.History.R11G11B10=1`; Texture2DArray so resurrection can keep old frames as slices),
**MetadataArray** (`PF_R8`, history res — per-pixel sample-count/validity), **GuideArray** (input
res, `PF_A2B10G10R10` unorm — shading-rejection reference color), **MoireArray** (input res,
`PF_R8G8B8A8` — anti-flicker state), **CoverageArray** (`PF_R8` — thin-geometry).

**0. `TSRClearPrevTextures`** — zeroes the atomic scatter target.

**1. `TSRMeasureFlickeringLuma.usf`** (input res, on scene color *before* translucency). Stores one
R8 luma per pixel: `GLuma = SMCSToGCS(dot(LinearToSMCS(Color * ExposureOffsetFactor),
kMoireSMCSWeights))`. GCS `c/(c+0.17)` and SMCS `= GCS²` are TSR's perceptual LDR pair
(`TSRColorSpace.ush`). Measuring pre-translucency lets `ComputeMoireError` later subtract legitimate
translucency/fog changes from the flicker signal.

**2. `TSRDilateVelocity.usf`** (input res, 8×8 tiles). From a 3×3 depth+velocity neighborhood:
- **Closest-occluder dilation, depth-edge-gated** (`FindClosestDepthOffset`): the nearest-depth
  offset only wins if the pair straddles a real depth edge — `DepthVariation > max(DepthDiff * 0.25,
  DeviceZError)` where `DeviceZError = ComputePixelDeviceZError(DeviceZ)`. Flat surfaces keep their
  own velocity.
- **Reprojection jacobian** (`ComputeReprojectionJacobian`): per-pixel 2×2 velocity gradient from
  central differences, depth-bilateral-weighted, rotated-diagonal fallback stencil. Encoded 8
  bits/component, sign-preserving sqrt curve, range ±2 px/px.
- **Reprojection boundary**: when dilation changes velocity by more than
  `r.TSR.ReprojectionField.AntiAliasPixelSpeed = 0.125` output px, a mini spatial anti-aliaser
  browses the depth buffer along the edge (3 iterations) and stores a sub-pixel edge position so
  `UpdateHistory` can dilate velocity by *half an anti-aliased pixel*.
- **IsMoving mask**: world displacement vs 2× pixel world radius + rotational parallax vs
  `r.TSR.ShadingRejection.Flickering.MaxParallaxVelocity = 10` px@1080p; material
  `bHasPixelAnimation` forces 1. Discards flicker history on movers.
- **Closest-occluder scatter** (`ScatterClosestOccluder`): each pixel `InterlockedMax`-writes
  `f32tof16(PrevClosestDeviceZ) << 18 | EncodedHoleFillingVelocity` (13-bit length + 5-bit angle)
  into the 4 bilinear-footprint texels of its previous-frame position — "what depth landed here last
  frame, and moving how". **Compute-only** (globallycoherent UAV + atomics).

The reprojection field is 3 uint32 planes (vector 2×16, jacobian 4×8, boundary 26 bits). CVar
`r.TSR.ReprojectionField` (default 0, on at High+ scalability).

**3. `TSRDecimateHistory.usf`** (input res). Reprojects the input-res histories: Guide via 5-tap
Stubbe Catmull-Rom, Moire via a *randomly jittered point* sample (deliberate dither over bilinear
smear). Parallax-disocclusion verdict: scattered closest-occluder depth at the reprojected position
vs `PrevDeviceZ`, tolerance `WorldDepthEpsilon = PixelWorldRadius×3×2 + WorldDepthError`;
`DepthRejection = saturate(2 − ΔDepth/ε)`; disoccluded when the bilinear-weighted mask `< 0.5`. On
disocclusion, a valid **hole-filling velocity** from the scatter *replaces* the reprojection vector.
Outputs a `DecimateMask` bitmask (offscreen / parallax-disoccluded / pixel-animation / hole-fill /
resurrection-offscreen) + `ReprojectionEdge` (velocity-coherence: `saturate(1.1 − dot(|Δv_px|,
1.1))`).

**4. `TSRDetectThinGeometry.usf` + `TSRWeightRelaxation.usf`** (optional,
`r.TSR.ThinGeometryDetection`, forced on with Nanite foliage). Detection is a 3×3 **thin-line test
on depth**: `HorizontalThinEdge3x3` marks a pixel where `Center − Top > EdgeThreshold` AND `Center −
Bottom > EdgeThreshold` (a 1-px ridge), threshold `= ComputePixelDeviceZError(depth) ×
r.TSR.ThinGeometryDetection.ErrorMultiplier (200)`; same vertically; optional intensity-line variant
with contrast ≥ `MinKeepLineContrast = 0.30`. History coverage is validated with a Student t-test on
the 3×3 Bernoulli coverage distribution (`T < 20.754`). The `HistoryRelaxationWeight` (7 bits,
shaped, dilated, capped `MaxRelaxationWeight = 0.037`) is trimmed where translucency dominates
(`TranslucencyRatio > 0.6`). Consumption: the rejection clamp box min/max are **lerped toward the
history's own 3×3 min/max** by the relaxation weight — validated thin geometry may partially clamp
to itself instead of to the undersampled input.

**5. `TSRRejectShading.usf`** (input res, 32×32 register-resident tiles, `TileOverscan = 3`). The
quality heart. (`TSRConvolutionNetwork.ush` is not a neural net — it is the wave-op/LDS SIMD tensor
framework (`TLaneVector2D`, `Blur3x3`, `Median3x3`, `Min/Max3x3`) that chains many 3×3 convolutions
without memory round-trips; it is why this pass is structurally compute-only.)
- **Anti-flicker (`ComputeMoireError`)** — Moire history = filtered luma, signed gradient,
  accumulated `TotalVariation`, `TotalVariationCount` (max 20). Each frame it *simulates the
  resolve's own blend* on the pre-translucency luma (vs a `GhostingHistory` at `MinBlendFinal =
  0.05`) to get `CurrentGradient`. Flicker = current and previous gradients have **opposite signs**,
  both exceeding `MoireEncodingError = 1/127`. Accumulates `GradientVariation =
  min(|prev|,|curr|)·IsFlicker` + a flip count, both dilated 5×5, discarded on movers/disocclusion.
  `MoireError = (|TV|/count + count/127) · CountFadeIn`, count fade-in threshold `= 1 −
  (1−0.05)^FlickeringFramePeriod` (`FlickeringFramePeriod = 2.0` frames, frame-rate adjusted).
  **Consumption: an amplitude, not a flag** — it widens the rejection clamp box by exactly the
  measured flicker amplitude (`StableFilteredBoxMax = max(BoxMax, BoxMin + MoireErrorSize)`).
- **Shading rejection (`MeasureRejection`)** — in SMCS. Inputs mutually "annihilated" first
  (`Clamp3x3` of input toward history, `AnnihilateToGuide3x3` of history toward input — removes the
  *aliasing* difference so only *shading* difference remains). Then `FilteredInput = Blur3x3(In)`,
  `FilteredHistory = Blur3x3(Hist)` (weights 1, 4×0.5, 4×0.25); clamp box `MinMax3x3(FilteredInput)`
  expanded by `ClampError` (incl. `MeasureBackbufferLDRQuantizationError() = 0.5/1024`); `Delta =
  max(|FilteredInput − FilteredHistory|, BoxSize)`; per-channel `RawFactor = saturate(1 −
  |ClampedFilteredHistory − FilteredHistory| / Delta)`, min over channels; then **spatially
  denoised** — `Median3x3` + a 3×3 max-energy/min-rejection pass — producing `RejectionBlendFinal`
  and `RejectionClampBlend`. Rejection is a normalized, low-pass, median-filtered measure of how
  much clamping the history would destroy relative to how different the frames are.
- **Guide update**: `BlendFinal = max(TheoricBlendFactor, 1 − RejectionBlendFinal)`,
  `TheoricBlendFactor = 1/(1 + HistorySampleCount/ratio²)`; the guide is TSR's second color history
  at input res (GCS, stochastic 10-bit quantization) — what makes rejection at input res possible
  without touching the output-res color history.
- **History resurrection** (optional, `r.TSR.Resurrection`, default 0): input compared against a
  many-frames-old stored slice; on win, the reprojection field is overwritten with the resurrection
  transform (`ClipToResurrectionClip`).
- Outputs `HistoryRejectionTexture` RGBA8: R = `RejectionBlendFinal`, G = `DisableHistoryClamp`
  (rejection-trust × uncertainty, 0 on disocclusion), B = validity multiplier, A = bitmask; plus the
  composed scene color and the spatial-AA mask.

**6. `TSRSpatialAntiAliasing.usf`** (input res, quality 1–3 via
`r.TSR.RejectionAntiAliasingQuality = 3`). FXAA-family: browse direction from 3×3 total-variation,
walk the edge bilinearly N iterations, output a **sub-pixel texel offset + noise-filtering scalar**.
Runs only where `RejectionBlendFinal < 0.25` or disoccluded-and-not-resurrected. It does **not
blur**: `UpdateHistory` *shifts its input sampling position* by the offset (`InputPPCo +=
SpatialAntiAliasingOffset × saturate(1 − LowFrequencyRejection·4)`).

**7. `TSRUpdateHistory.usf`** — the resolve, at **history resolution** (output ×
`r.TSR.History.ScreenPercentage`: 100 default, **200 on Epic/Cinematic** — the "Nyquist" mode; only
100–200 supported). Per history pixel:
- Reprojection vector gets the **jacobian correction**: `ReprojectionPixelPosCorrection =
  mul(dInputKO, ReprojectionJacobian)` — every history pixel inside one input pixel reprojects to
  its own previous position; `ReprojectionUpscaleCorrection = rcp(max(UpscaleFactorFromJacobian,
  1))` feeds rejection.
- History fetch: Stubbe 5-tap Catmull-Rom + metadata, NaN/negative fallback to center.
- Input filtering: 5-tap PLUS at Low/Medium, PLUS_MOVE_FAR at High/Epic. Weights
  `ComputeSampleWeigth(k, d) = saturate((0.9x² − 1.9)x² + 1)`, `x = |d| ·
  KernelInputToHistoryFactor` — kernel is history-pixel-sized only when refining (rejection ≥ ~0.87),
  input-sized on rejection/disocclusion. Same taps form the min/max clamp box.
- Accumulation is **explicit sample counting**: `HistorySampleCount = r.TSR.History.SampleCount
  (16) / OutputToHistoryResolutionFraction²`; `CurrentWeight = InputPixelAlignement × 1/N`;
  rejection caps validity via `r.TSR.ShadingRejection.SampleCount (2)`.
- **Velocity weight clamping** with a **contrast exception**: `MaxValidity = 1 −
  WeightClampingPixelSpeedAmplitude · saturate(v_px)` (from `r.TSR.Velocity.WeightClampingSampleCount
  = 4`, reference speed `r.TSR.Velocity.WeightClampingPixelSpeed = 1.0`) BUT
  `MinValidityForStability = |FilteredLuma − PrevHistoryLuma| / max(...)`; `MaxValidity =
  max(MaxValidity, MinValidityForStability)` — high-contrast edges KEEP history under motion
  (stability beats freshness where crawl is most visible).
- Clamp: `fastClamp(PrevColor, InputMin, InputMax)`, blended back toward *unclamped* by
  `DisableHistoryClamp` with HDR-weighted lerp factors. Final blend HDR-weighted (Karis-family,
  always on). Output stochastically quantized against R11G11B10 banding.

**8. `TSRResolveHistory.usf`** (only when history res ≠ output res): Mitchell-Netravali B=C=1/3
downsample, HDR-weighted, clamped to the local 2×2-downsampled min/max.

---

## B. Three-way mechanism table

| Mechanism | UE5 TSR | FSR 2.2 / 3 | Cosmic TAA |
|---|---|---|---|
| **Input conditioning** | Translucency composed separately; perceptual GCS/SMCS; stochastic quantization | Exposure normalize; luma pyramid; RCAS after | YCoCg; output-sized Mitchell/Lanczos2 reconstruction, anisotropic edge kernel + clutter width, depth-aware tap weights, firefly bound, speckle consolidation |
| **Reprojection** | Depth-edge-gated dilation + per-pixel 2×2 jacobian + sub-pixel AA'd dilation boundary; Stubbe 5-tap CR fetch | Dilated nearest-depth; Lanczos2 fetch | Dilated 3×3 nearest-depth (plus) + split reprojection (color=dilated, tests=own) + magnitude-gated own-velocity color reproject; 9-tap CR + hull dering |
| **Disocclusion** | Scatter: prev closest-occluder depth via InterlockedMax at prev position; `saturate(2 − ΔDepth/ε)`; hole-fill velocity substitution | Gather depth-based mask; reactive mask input for VFX | Gather: dilated depth in history alpha; range + ghost-side + center-depth tests, motion/remembered-motion gated, color-proportional `rejAuth` |
| **Shading-change rejection** | Input-res Guide history; mutual annihilation → Blur3x3 both sides → clamp-energy/Delta → Median3x3 denoise; drives blend, clamp-disable, kernel size, spatial AA | FSR3.1 shading-change detection; FSR2 clamp + reactive mask | Point luma diff (resolution-matched >1.5×) + featReject gradient test + ringContam; no filtered/normalized verdict, no spatial denoise |
| **Anti-flicker** | Moire history (luma, signed gradient, TV, count): sign-alternating amplitude over `FlickeringFramePeriod`, 5×5 dilated; **widens clamp by measured amplitude**; killed on movers/disocclusion | None temporal; locks (2.2) lerp to unclamped on luma stability, broken by shading change > 0.1; Karis always on | Oscillation detector (sign + 7-bit EMA, witness-ruled); `oscLock` → lock escape `lerp(clamped, raw, oscLock)`; Karis (motion-faded) |
| **Thin geometry** | Depth 3×3 thin-line test (`PixelDeviceZError×200`) + coverage + Bernoulli t-test → clamp relaxation toward history's own min/max (≤ 0.037), translucency-trimmed | None | No structural detector; temporal lock + mip bias + aniso kernel only |
| **Locks / protection** | `DisableHistoryClamp` channel; thin-geometry relaxation; moire amplitude slack | Luma-lock lerp to unclamped, similarity-gated, reactivity-cut | `oscLock` → bounded escape + `oscCeil` cycle-aware ceiling |
| **Accumulation** | Explicit sample count (R8); per-cause validity caps (rejection → 2, velocity → 4 w/ contrast exception); resolution-fraction-aware | `1/(1+N)`-style + kernel-proximity confidence; ~8–33-frame windows | Kalman evidence counter (max 128): σ-normalized innovation, sign-alternation credit, bias penalty, collapse, witness rule; per-cause caps |
| **Kernels** | Input: quartic `saturate((0.9x²−1.9)x²+1)`, size lerped input↔history by rejection; history: Stubbe CR; resolve: Mitchell + min/max clamp | Lanczos2 + kernel proximity | Mitchell/Lanczos2 + hull clamp, aniso/clutter/depth-aware; 9-tap CR history |
| **Spatial AA on rejection** | Edge-browse pass → **sampling offset** into UpdateHistory | None (RCAS output only) | RawSoften display swap (blur, motion-suppressed) |
| **Output** | No sharpen in TSR; stochastic quantization | RCAS | Game-side ratio-scaled RCAS auto-sharpen |

---

## C. What TSR has that Cosmic TAA lacks — ranked for this content (scales 0.33–1.0)

1. **Filtered, normalized, spatially-denoised shading rejection.** PORTABLE cheaply on SM4: blurred
   current = `m1` (exists); blurred history = plus-blur of the featReject point taps + historyPoint
   (exist); verdict `= saturate(1 − |clamp(bH, box±ε) − bH| / max(|bC − bH|, boxSize))` with an ε
   quantization floor (2/255 analogue of TSR's 0.5/1024); feed into `diff`/`rejTighten`. Targets the
   ghost bucket (37% of lab objective): catches motion-silent shading changes (TVs, lighting,
   cutaways) while blurred operands suppress aliasing false-rejects. The Median3x3 verdict denoise is
   NOT portable in-pass (needs neighbors' verdicts); blurred operands are the partial substitute. No
   new targets, no version bump.
2. **Depth thin-line structural prior.** PORTABLE nearly free, fits SM3: center nearer than BOTH
   opposite neighbors by a relative ε, on already-fetched plus-pattern depths (~12 ALU, 0 fetches,
   0 state). Consume: ease `oscLock` entry (structural evidence standing in for the first ~6
   witnessed frames — 1-in-9 witnessing at 0.33x) + exempt from `biasPenalty`. Ghost-safe: a color
   trail cannot fake a same-frame depth ridge; lock kill-gates unchanged. The coverage-buffer +
   t-test half needs a raster-side channel we don't have (the planned velocity-mask alpha band).
3. **Amplitude-proportional flicker slack (Moire-lite).** PARTIAL: meta.A re-pack sign(1) +
   oscRate(4) + oscAmp(3), widen the clamp by decoded amplitude (`cmax.x += k·oscAmp`, capped),
   scale the lock-escape share down. Targets fizzle/temporal (33%) on high-frequency terrain/foliage
   equilibrating at partial osc. Meta ENCODING change → `RESOLVE_VERSION` bump + lab retune. A full
   port (4×8-bit state) needs a second meta target (~8 MB @1440p + 1 fetch/px).
4. **Jacobian-corrected reprojection.** PORTABLE (gather half): central-difference 2×2 velocity
   gradient from the plus taps, depth-bilateral weighted; `histUV += mul(fracd, J) · InvColorSize`.
   Everyday win = camera ZOOM (systematic sub-texel reprojection error currently scrubbed by the
   motion trust cap). ~15 ALU, 0 new fetches, SM4. Cheapest first step: a uniform per-frame camera
   zoom jacobian from C# — exact for isometric zoom, zero per-pixel cost. The AA'd-boundary half is
   not worth the cost. Also: TSR's depth-edge-gated dilation (skip dilation on flat 3×3s) would
   simplify what split-reprojection already covers.
5. **Spatial AA by sampling offset on rejected pixels.** PARTIAL/EXPENSIVE: faithful port = extra
   pass + R8 target; a single-iteration inline FXAA-style version (browse from the 5 box taps, one
   extra luma pair, shift up to ±0.5 texel) fits SM4 budget. Value modest here (reveals are rare;
   honest-disocclusion knee already bounds the raw window).

**Not portable (explicitly):** closest-occluder scatter + hole-fill velocities (UAV atomics; weak
gather approximation: retry reprojection with meta.GB's stored velocity on rejection); history
resurrection (extra full-res history slices, ≥16 MB); 200% history screen percentage (4× memory +
resolve cost; off below Epic even in UE — our output-res history is the TSR-validated choice);
the convolution-network tiling itself; translucency-separated rejection + `bHasPixelAnimation`
material flags (engine-side inputs — exactly the planned shading-change flag, which TSR's design
confirms belongs input-side).

---

## D. Already at or beyond parity — do not churn

- 9-tap Catmull-Rom + hull dering ≥ TSR's Stubbe 5-tap.
- Output-sized kernel + sample confidence = the FSR2 recipe ≈ TSR's quartic + `InputPixelAlignement`
  (theirs is cheaper, not better). Aniso edge kernel / clutter width / depth-aware weights have NO
  TSR or FSR equivalent.
- Kalman evidence counter is richer than TSR's sample counting (same architecture family; ours adds
  innovation verdicts). TSR validates, don't simplify.
- Oscillation detector: TSR's flickering-luma is the same sign-alternation insight; **FSR has no
  equivalent** — TSR validates our mechanism over FSR's. The gap is proportional amplitude (C.3),
  not existence.
- Split/own-velocity reprojection + foreign-velocity + ringContam: no reference equivalent; ahead of
  both for mover trails. TSR's depth-edge-gated dilation validates the same instinct input-side.
- Disocclusion gather tests ≈ TSR scatter behavior within pixel-shader constraints.
- Jitter policy (cycled Halton, `8·ceil(ratio²)`) matches both references.
- One-liner TSR sanctions if edge crawl shows during pans: scale `MotionTrustCap` by
  `(1 − lumaContrast)` (TSR's `MinValidityForStability` contrast exception).
- Karis weighting: TSR/FSR keep it ALWAYS on (re-endorses the reverted R6 direction).

---

## E. Top 3 recommendations

1. **TSR-style filtered rejection verdict** — attack the ghost bucket with a normalized
   clamp-energy measure over blurred operands (taps mostly exist); min() with the existing diff for
   one tuning cycle; lab ghost/fizzle terms arbitrate. ~5–8 ALU + ≤2 fetches, SM4, no version bump.
   Risk medium-low (ε too tight re-introduces flicker-as-rejection).
2. **Depth thin-line prior for the lock/clamp** — fences/railings/stems stable from frame ~2 instead
   of ~20 at 0.33x; survives evidence wipes. ~12 ALU, 0 fetches, 0 state, one new tunable
   (ErrorMultiplier analogue), fits Core + Lite. Risk low (needs a same-frame depth ridge; lock
   kills stay in force — the prior accelerates entry, never bypasses kills).
3. **Amplitude-proportional flicker slack** — smooth, exactly-sized ghosting allowance replacing the
   binary lock threshold chatter on partial-osc content; TSR-precedented
   (`StableFilteredBoxMax = max(BoxMax, BoxMin + MoireErrorSize)`). Meta re-encode +
   `RESOLVE_VERSION` bump + lab ghost-persistence gate before shipping. Risk medium (a licensed
   ghost band, bounded by cap + kill gates; monotonic ghosts decay the amplitude by the same
   argument TSR relies on).
