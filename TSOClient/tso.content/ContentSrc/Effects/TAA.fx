// TAA.fx — temporal anti-aliasing for OpenSO 3D mode.
//
// Reads:
//   colorTex       — this frame's rendered color (post-scale-resolve, pre-blur).
//   historyTex     — previous frame's TAA output (RGB) + packed dilated depth (A), velocity-reprojected.
//   metaHistoryTex — previous frame's meta: R = accumulation count N (N/MaxAccum), GB = previous dilated
//                    velocity (v*10+0.5), A = packed luma-oscillation state (sign bit + 7-bit EMA — the
//                    anti-fizzle detector). Velocity-reprojected too.
//   velocityTex    — per-pixel screen-space velocity (.rg) + normalized linear depth (.b) + valid mask (.a).
//
// Outputs to TWO render targets:
//   COLOR0 — displayed frame + next frame's history (RGB) with this pixel's dilated depth packed in A
//            (fp16 target when available — see PPXDepthEngine.HistoryIsFP16 / DepthRejectParams).
//   COLOR1 — next frame's meta: R = new N, GB = dilated velocity encode, A = oscillation pack (TAADebug technique
//            repurposes GB for diagnostics and disables their consumers).
//
// Algorithm (Karis 2014 / UE4 / Playdead recipe, extended with a per-pixel accumulation counter):
//   1. Velocity dilation: reproject with the nearest-depth motion vector in a 3x3 neighbourhood.
//   2. Jitter-free reprojection: histUV = uv - velocity + JitterDelta.
//   3. Catmull-Rom (bicubic) history fetch — preserves detail across reprojection.
//   4. Fixed tight (1.3 sigma) YCoCg variance clamp — re-anchors history to the current frame (anti-melt).
//   5. Depth-disocclusion rejection only ("surface moved away"). No normal buffer: that MRT is written by
//      only some passes, so a normal test fired inconsistently on minified content and left it stairstepped.
//   6. Blend = baseline content-adaptive luminance-feedback weight (robust, immediate deep history on stable
//      pixels — resolves distant terrain) with a per-pixel accumulation COUNTER layered on to DEEPEN a
//      proven-stable pixel further (crisper persistent thin lines) but never below the baseline floor.
//   7. Anti-flicker inverse-luma weighting on the final blend (LDR pipeline; no tonemap step).

float2 InvScreenSize; // 1 / OUTPUT (history) resolution — the grid TAA resolves on
// 1 / INPUT color resolution. Equal to InvScreenSize normally; SMALLER-res (larger texels) under Cosmic
// TAAU, where this pass IS the upscaler: it accumulates jittered render-res samples directly onto the
// native output grid (history/meta native, color/velocity render-res). The reconstruction below is written
// in input-pixel coordinates throughout, so the 1:1 mode is just the degenerate case.
float2 InvColorSize;
float  BlendFactor;   // baseline deep-history floor (current weight ~= BlendFactor on a stable pixel).
float  MaxAccum;      // cap on the accumulation counter N. Matches TAAResolve.MAX_ACCUM.
// Per-frame jitter delta (UV). Velocity is computed from the jittered projection, so adding this back when
// reprojecting history gives an exact (jitter-free) reproject (removes the sub-pixel wobble/blur).
float2 JitterDelta;
// Depth-disocclusion tuning, set from C# by the ACTUALLY-ALLOCATED history format (fp16 vs RGBA8 fallback):
//   x = ghost dead-zone epsilon (storage quantization must never fire the ghost test by itself)
//   y = depthReject slope   z = depthReject offset   w = relative-compare denominator floor
// fp16 history: (0.0005, 12.0, 0.0, 0.02). RGBA8 fallback: (2/255, 6.0, 0.25, 0.05) — the old blunted curve,
// which existed to hide 8-bit quantization; keeping it uniform-driven prevents fallback hardware shimmer.
float4 DepthRejectParams;
// This frame's jitter as a UV offset: the colour buffer was rendered with the projection translated by the
// jitter, so buffer[uv] holds content that UN-jittered belongs at uv + SampleJitterUV. Sampling the variance
// -box taps at uv - SampleJitterUV therefore reads the un-jittered neighbourhood: the clamp box becomes
// spatially STATIONARY for static content and aligned with the jitter-free reprojected history it clips.
// Without this the box wobbles sub-pixel every frame and re-clips converged history toward a moving window —
// visible as flicker on high-variance same-depth content (foliage, terrain texture) even at full convergence.
// The box stays the same tight width (no extra trust — not the ghosting failure mode); only its POSITION
// stabilises. The centre "curr" sample stays at the jittered uv: that offset IS the new sub-pixel information.
float2 SampleJitterUV;
// Motion-gate pixel scale: the velocity gates (moveGate/stillGate/reactive) think in RESOLVE-GRID pixels,
// but in pre-upscale (FSR1) mode that grid is render-res — the same scene motion produced proportionally
// fewer pixels of velocity at low render scale, so disocclusion rejection barely armed (ghosting) and the
// oscillation lock survived on moving edges (held ghost -> collapse fizzle). This rescales gate-space to
// NATIVE pixels: 1/renderScale in pre-upscale mode, 1 everywhere else (TAAU/native grids are already native).
float  VelGatePxScale;
// Jitter cycle length (frames), from R2Jitter.HaltonCycle: 8 native, 32 at 0.5x, 72 at 1/3. The locked
// accumulation window must EXCEED this for the converged limit cycle to be invisible — see the
// cycle-aware trust ceiling in the blend section.
float  JitterPhases;

// ---- LIVE-TUNING UNIFORMS (TAA_Core / full resolve ONLY — TAALite keeps its own literals). ----
// Promoted from tuned literals so the FSO.TAALab harness can adjust the temporal-resolve motion
// lifecycle (raw -> hazy -> converged) interactively. SINGLE SOURCE OF TRUTH for the defaults is
// FSO.LotView.Utils.TAATuning, which TAAResolve.Draw uploads every frame with these exact values —
// the initializers here are only a fallback so a driver that doesn't set them (or an older build)
// behaves identically to the pre-promotion shader. Do NOT retune here; retune in TAATuning.
float TuneMotionBoostFloor = 0.12;   // motionBoost suspicion floor (clean-motion raw drip share)
float TuneMotionBoostMax = 0.22;     // motionBoost peak scale (evidence-flagged motion raw boost)
float TuneStillGateFloor = 0.25;     // stillGate suspicion velocity scale floor (lock survival on clean pans)
float TuneMoveGateLo = 0.6;          // moveGate smoothstep lower edge (native px/frame)
float TuneMoveGateHi = 2.0;          // moveGate smoothstep upper edge (native px/frame)
float TuneRespEnd = 0.60;            // responsive end of the diff-driven blend lerp (full-diff history weight)
float TuneMotionTrustCap = 0.65;     // motion trust cap at upscale (interior-texture ghost lever; 0.72->0.65 final round: ~35%/frame refresh in motion, trails die in 2-3 frames as crisp Lanczos reconstruction)
float TuneMotionClampTighten = 0.72; // motion-scaled variance-clamp tighten at upscale (self-reveal lever)
float TuneRawSoftenOnset = 0.12;     // raw-state display soften: blend onset
float TuneRawSoftenSlope = 2.2;      // raw-state display soften: slope past onset
float TuneRawSoftenMotionSup = 0.85; // raw-state display soften: suppression under coherent motion
float TuneGamma = 1.5;               // variance clamp base width (sigma) — TAA_Core's GAMMA
float TuneTexDetailFloor = 0.28;     // texture-detail blend floor / low-variance anti-ghost backstop
float TuneConfFloor = 0.14;          // TAAU sample-confidence floor (the <=2x-ratio endpoint)
float TuneRingLo = 0.03;             // ringContam own-vs-dilated color knee, lower edge
float TuneRingHi = 0.10;             // ringContam own-vs-dilated color knee, upper edge
// ---- STRUCTURAL constants (2026-07-07 promotion — the full-vs-lite haze/ghost hunt) ----
float TuneDirectClampMix = 0.75;     // motion direct-clamp share vs phase-coherent rectification (ghost scrub <-> contrast-edge fizzle)
float TuneKarisFade = 1.0;           // scales the Karis anti-flicker motion fade (1 = full fade to plain lerp under motion)
float TuneGammaMotionDecay = 0.6;    // wide-box narrowing strength while in motion (foliage-trail lever)
float TuneConfFadeN = 20.0;          // evidence depth (minN) at which the off-phase confidence throttle is fully armed
float TuneGrowOffPhase = 0.3;        // off-phase growth discount floor for the evidence counter (witness-rule strength)
float TuneDeepCapBase = 0.992;       // Kalman deep-end cap at native/mild upscale (memory depth off the freeze asymptote)

// ---- TAALite tunables (2026-07-07 promotion — user wants Lite tunable too, its "raw motion resolve"
// Switch-2-DLSS-lite character is the DESIGN TARGET; defaults = the shipped literals, TAATuning.cs is
// the C# single source. Only consumed by TAALite_PS.) ----
float LiteGamma = 1.5;               // variance box base width (sigma) at native
float LiteGammaScale = 2.0;          // resolution ramp target multiplier at ratio >= 3 (1.5 -> 3.0)
float LiteDeepCap = 0.985;           // counter deep-end trust cap
float LiteRespEnd = 0.68;            // full-diff responsive end
float LiteMotionBoost = 0.35;        // speed-proportional current boost strength
float LiteConfFloor = 0.14;          // off-phase sample-confidence injection floor
float LiteMoveGateLo = 0.6;          // motion gate lower edge (native px/frame)
float LiteMoveGateHi = 2.0;          // motion gate upper edge
float LiteHonestLo = 0.65;           // honest-disocclusion raw-injection knee, lower edge
float LiteHonestHi = 0.98;           // honest-disocclusion raw-injection knee, upper edge

texture colorTex;
sampler colorSampler = sampler_state {
    texture = <colorTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture historyTex;
sampler historySampler = sampler_state {
    texture = <historyTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};
// POINT history fetches (packed DEPTH in alpha + featReject structure taps) are EMULATED through the
// LINEAR sampler by snapping the UV to the exact texel center — see FetchHistoryPoint below. There used
// to be a second POINT sampler_state aliasing historyTex here; that WAS the GL warble (bisect-convicted
// 2026-07-05): on OpenGL filter state is a glTexParameter property of the TEXTURE object, so one of the
// two aliased states silently won for both samplers and the "point" depth fetch was really BILINEAR — at
// silhouettes it mixed two surfaces' depths, jitter shifted the mixture every frame, and depthReject
// fired rhythmically along every edge. DX honored both states, which is why only GL warbled. LAW: never
// alias one texture with two differently-filtered sampler_states in this engine; point-emulate instead
// (bilinear at an exact texel center IS a point fetch, on every backend). Depth must never be bilinearly
// interpolated: at an edge, LINEAR mixes the two surfaces' depths into a value belonging to neither.

texture metaHistoryTex;
sampler metaHistorySampler = sampler_state {
    texture = <metaHistoryTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT; // counts/normals must not cross-fade between texels
};

texture velocityTex;
sampler velocitySampler = sampler_state {
    texture = <velocityTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

struct VSIn  { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };
struct VSOut { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };

VSOut VS(VSIn input)
{
    VSOut o = (VSOut)0;
    o.Position = input.Position;
    o.Coord = input.Coord;
    o.Coord.y = 1 - o.Coord.y; // match SSAA/FXAA/FSR fullscreen convention
    return o;
}

// YCoCg color space — perceptually-cleaner than RGB for neighborhood comparison (luma dominates Y).
float3 RGB_to_YCoCg(float3 c) { return float3(0.25*c.r + 0.5*c.g + 0.25*c.b, 0.5*c.r - 0.5*c.b, -0.25*c.r + 0.5*c.g - 0.25*c.b); }
float3 YCoCg_to_RGB(float3 c) { return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }

// Clip history toward the YCoCg AABB (soft line-clip, smoother than a hard min/max snap).
float3 ClipAABB(float3 cmin, float3 cmax, float3 hist)
{
    float3 center = 0.5 * (cmax + cmin);
    float3 extent = 0.5 * (cmax - cmin) + 1e-5;
    float3 d = hist - center;
    float3 unit = d / extent;
    float u = max(max(abs(unit.x), abs(unit.y)), abs(unit.z));
    return (u > 1) ? center + d / u : hist;
}

// Mitchell-Netravali (B=C=1/3) kernel at arbitrary distance, valid for x in [0, 2).
// k(0)=8/9=0.8889, k(0.5)=0.5347, k(1)=1/18=0.0556, k(1.5)=-0.0347 -> clamped to 0 by the max(): removing
// the tiny negative lobe keeps the 3x3 reconstruction a convex combination — no ringing overshoot on bright
// sub-pixel sparkle (which would amplify exactly the fizzle the oscillation gate exists to kill).
float MitchellK(float x)
{
    float x2 = x * x;
    float x3 = x2 * x;
    float inner = (7.0 * x3 - 12.0 * x2 + 16.0 / 3.0) / 6.0;                     // |x| < 1
    float outer = ((-7.0 / 3.0) * x3 + 12.0 * x2 - 20.0 * x + 32.0 / 3.0) / 6.0; // 1 <= |x| < 2
    return max((x < 1.0) ? inner : outer, 0.0);
}

#if SM4
// Lanczos2 polynomial approximation — FSR2's reconstruction kernel (Lanczos2ApproxSqNoClamp from the
// FidelityFX-FSR2 SDK's ffx_fsr2_sample.h, evaluated here on distance not squared-distance). Peak k(0)=1,
// zeros at x=1 and x=2, negative lobe ~-0.13 near x~1.4. The negative lobes are the DISTINCTNESS that
// lobe-clamped Mitchell gives up (Lanczos2 ~ Catmull-Rom-class passband vs Mitchell's ~-1.5dB mid-band,
// and our clamped Mitchell is softer still — the user-observed "indistinct" TAAU look). Ringing from the
// lobes is controlled FSR2-style at the USE site: the reconstructed value is clamped to the 3x3 tap hull
// (see the reconstruction loop) — the same dering recipe the Catmull-Rom history fetch already uses.
// SM4/upscale-only: native keeps validated Mitchell bit-exactly; the SM3/GL tier keeps Mitchell too
// (register budget + no hull there). Cap x2 at 4 = kernel support edge.
float LanczosK(float x)
{
    float x2 = min(x * x, 4.0);
    float a = 0.4 * x2 - 1.0;
    float b = 0.25 * x2 - 1.0;
    return (1.5625 * a * a - 0.5625) * (b * b);
}
#endif

// Catmull-Rom (bicubic) history sampling — preserves high frequencies across reprojection so the jittered
// samples build a sharp supersampled image (plain bilinear would low-pass every frame into mush).
float3 SampleHistoryBicubic(float2 uv)
{
    float2 texSize = 1.0 / InvScreenSize;
    float2 samplePos = uv * texSize;
    float2 texPos1 = floor(samplePos - 0.5) + 0.5;
    float2 f = samplePos - texPos1;

    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 offset12 = w2 / w12;

    float2 tp0  = (texPos1 - 1.0) * InvScreenSize;
    float2 tp3  = (texPos1 + 2.0) * InvScreenSize;
    float2 tp12 = (texPos1 + offset12) * InvScreenSize;

    // FULL 9-tap Catmull-Rom (restored from the 5-tap Karis diet): the dropped corner taps' weight was
    // renormalized onto the axis taps, which low-passed DIAGONAL detail slightly on every reprojection —
    // a real resampling-sharpness cost once the reconstruction kernel got anisotropic (diagonal thin
    // geometry is exactly what it now resolves). +4 fetches; still under the pre-diet fetch budget.
#if SM4
    // DERINGING HULL CLAMP (re-auditioned IN ISOLATION per the note that used to sit here — the earlier
    // variant was reverted only because it shipped stacked with regressing changes): Catmull-Rom's negative
    // lobes (w0/w3) overshoot around high-contrast content — a dark/bright halo ringing on fine BRIGHT
    // detail, amplified once the sharper low-scale locks let converged thin lines reach full contrast in
    // the history (every reprojection resample re-rings them, and RCAS then sharpens the halo). Overshoot
    // is definitionally OUTSIDE the local tap hull; faithful interpolation is inside it — clamping to the
    // 9-tap min/max removes the ring exactly without softening the reconstruction (this is the standard
    // TAA bicubic dering, UE-style). ALU-only, no extra fetches. The old global clamp(0,8) kept only its
    // fp16-overflow role (the hull is data-bounded, so it subsumes the undershoot half).
    // SM4-ONLY: naming all 9 taps for the hull overflows ps_3_0's 32 temp registers (CI X4505 on the
    // OGL/MojoShader targets); SM3 keeps the accumulate-and-globally-clamp form below (pre-hull behavior).
    float3 t00 = tex2Dlod(historySampler, float4(tp0.x,  tp0.y,  0, 0)).rgb;
    float3 t10 = tex2Dlod(historySampler, float4(tp12.x, tp0.y,  0, 0)).rgb;
    float3 t20 = tex2Dlod(historySampler, float4(tp3.x,  tp0.y,  0, 0)).rgb;
    float3 t01 = tex2Dlod(historySampler, float4(tp0.x,  tp12.y, 0, 0)).rgb;
    float3 t11 = tex2Dlod(historySampler, float4(tp12.x, tp12.y, 0, 0)).rgb;
    float3 t21 = tex2Dlod(historySampler, float4(tp3.x,  tp12.y, 0, 0)).rgb;
    float3 t02 = tex2Dlod(historySampler, float4(tp0.x,  tp3.y,  0, 0)).rgb;
    float3 t12 = tex2Dlod(historySampler, float4(tp12.x, tp3.y,  0, 0)).rgb;
    float3 t22 = tex2Dlod(historySampler, float4(tp3.x,  tp3.y,  0, 0)).rgb;
    float3 r = t00 * (w0.x  * w0.y) + t10 * (w12.x * w0.y) + t20 * (w3.x  * w0.y)
             + t01 * (w0.x  * w12.y) + t11 * (w12.x * w12.y) + t21 * (w3.x  * w12.y)
             + t02 * (w0.x  * w3.y) + t12 * (w12.x * w3.y) + t22 * (w3.x  * w3.y);
    float3 hullMin = min(min(min(min(t00, t10), min(t20, t01)), min(min(t11, t21), min(t02, t12))), t22);
    float3 hullMax = max(max(max(max(t00, t10), max(t20, t01)), max(max(t11, t21), max(t02, t12))), t22);
    return clamp(r, hullMin, hullMax); // weights sum to 1 exactly; hull bounds also cover fp16 insurance
#else
    // ps_3_0 (OGL/MojoShader) register-budget form: accumulate without naming taps, global clamp only.
    float3 r = float3(0, 0, 0);
    r += tex2Dlod(historySampler, float4(tp0.x,  tp0.y,  0, 0)).rgb * (w0.x  * w0.y);
    r += tex2Dlod(historySampler, float4(tp12.x, tp0.y,  0, 0)).rgb * (w12.x * w0.y);
    r += tex2Dlod(historySampler, float4(tp3.x,  tp0.y,  0, 0)).rgb * (w3.x  * w0.y);
    r += tex2Dlod(historySampler, float4(tp0.x,  tp12.y, 0, 0)).rgb * (w0.x  * w12.y);
    r += tex2Dlod(historySampler, float4(tp12.x, tp12.y, 0, 0)).rgb * (w12.x * w12.y);
    r += tex2Dlod(historySampler, float4(tp3.x,  tp12.y, 0, 0)).rgb * (w3.x  * w12.y);
    r += tex2Dlod(historySampler, float4(tp0.x,  tp3.y,  0, 0)).rgb * (w0.x  * w3.y);
    r += tex2Dlod(historySampler, float4(tp12.x, tp3.y,  0, 0)).rgb * (w12.x * w3.y);
    r += tex2Dlod(historySampler, float4(tp3.x,  tp3.y,  0, 0)).rgb * (w3.x  * w3.y);
    return clamp(r, 0.0, 8.0);
#endif
}

// POINT-emulated history fetch through the LINEAR sampler (the GL sampler-aliasing fix — see the
// comment where historyDepthSampler used to be declared): snap to the exact texel center, where
// bilinear degenerates to a point fetch on every backend.
float4 FetchHistoryPoint(float2 uv)
{
    float2 t = (floor(uv / InvScreenSize) + 0.5) * InvScreenSize;
    return tex2Dlod(historySampler, float4(t, 0, 0));
}

struct TAAOut
{
    float4 color : COLOR0; // displayed frame + next frame's history (RGB), dilated depth in A
    // Meta layout (RGBA8): R = new accumulation count N (N/MaxAccum), GB = this frame's dilated velocity
    // encoded v*10+0.5 (SATURATES at +/-0.05 UV on store — writers now clamp at +/-0.5, so beyond 64px/frame
    // the stored value pins and the velocity-disparity reactive fires during ultra-fast motion: desirable),
    // A = packed luma-oscillation state
    // (sign bit + 7-bit EMA; 0 on non-reprojectable / meta clear). The TAADebug technique repurposes GB+A
    // for diagnostics and correspondingly DISABLES their consuming logic (self-consistent while debugging).
    float4 meta  : COLOR1;
};

// Shared TAA core. debugMeta is a compile-time uniform bool (folded per-technique): when true, meta.GB
// carries diagnostics (G = reject strength, B = non-reprojectable) instead of prev-velocity, and the
// velocity-disparity reactive is forced off so we never decode last frame's debug bytes as a velocity.
TAAOut TAA_Core(VSOut input, uniform bool debugMeta)
{
    TAAOut o;
    float2 uv = input.Coord;

    // 3x3 neighborhood pass — three jobs, three tap sets:
    //  * VARIANCE BOX (m1/m2): taps at the UN-jittered position (boxUV, bilinear does the shift) so the
    //    clamp box stays content-STATIONARY frame-to-frame (validated fix: a wobbling box re-clips converged
    //    history -> flicker). Statistics tolerate the bilinear low-pass; do NOT move these to raw taps.
    //  * FILTERED INPUT (filt/wsum): JITTER-RELATIVE MITCHELL RECONSTRUCTION (UE TAAU / MJP formulation).
    //    Taps at the RAW texel centers (uv + ofs — exact texels, no interpolation), weighted by the Mitchell
    //    kernel evaluated at each tap's distance to the un-jittered pixel center. The previous scheme
    //    (bilinear-shifted taps + fixed integer weights) pre-blurred every frame with an effective
    //    Mitchell(x)tent kernel that DESTROYED the sub-pixel information the jitter exists to provide —
    //    accumulation could never super-resolve through it. With raw taps + jitter-relative weights, each
    //    frame contributes genuinely new sub-pixel information and the converged history super-resolves.
    //    Sign derivation (pinned — this went wrong once): buffer[p] holds content that un-jittered belongs
    //    at p + SampleJitterUV, so the tap at uv + ofs sits at displacement (ofs + SampleJitterUV) from the
    //    pixel center. At zero jitter the weights degenerate bit-identically to the old fixed 0.8889/0.0556
    //    (city view / jitter-off provably unchanged).
    //  * VELOCITY DILATION + depth range: taps at the true pixel (velocity buffer indexed by rendered pixel).
    float2 texSize = 1.0 / InvScreenSize; // OUTPUT pixels (velocity gates, reactive thresholds)
    float2 colSize = 1.0 / InvColorSize;  // INPUT color pixels (reconstruction, box, velocity taps)
    float2 boxUV = uv - SampleJitterUV;
    // Nearest-jittered-sample reconstruction base (TAAU-general; exactly the M1 case at 1:1). In INPUT-pixel
    // coordinates: the output pixel center sits at oPx; this frame's samples sit at texel centers + the
    // content shift sPx. The nearest sample's texel is floor(oPx - sPx) — NOT floor(oPx - sPx - 0.5)+...,
    // the -0.5 variant biases sample confidence low for ~half of output pixels (verified in design review).
    float2 oPx = uv * colSize;
    float2 sPx = SampleJitterUV * colSize;
    float2 baseTexel = floor(oPx - sPx);
    float2 fracd = (baseTexel + 0.5 + sPx) - oPx; // nearest sample's offset from the output center (input px)
    // OUTPUT-SIZED RECONSTRUCTION KERNEL (the reference-TAAU sharpness mechanism, UE TSR / DLSS-class):
    // distances are scaled by the upscale ratio so the Mitchell kernel is sized for the OUTPUT pixel, not
    // the input texel. Unscaled, the kernel footprint at 0.5x scale spans ~4 output pixels and the converged
    // image can never exceed a Mitchell blur at render resolution; scaled, only samples genuinely near each
    // output pixel carry weight — per-frame coverage is sparser (sample confidence + the wsum fallback below
    // carry those pixels via history), and over the Halton phase cycle every output pixel accumulates TRUE
    // output-resolution detail. kscale = 1 at native (bit-identical weights to before). UNCLAMPED — the
    // TRUE output-sized kernel at every ratio (render-scale floor 1/3 bounds it at 3): earlier clamps
    // (2.0, then 2.5) capped converged detail at ~1.5/1.25-output-pixel width at 0.33x — the baseline
    // "blurry/indistinct" at the lowest scale. A tight kernel means many zero-coverage frames, which is
    // safe ONLY because zero-information frames inject nothing (coverage-scaled confidence floor in the
    // blend section) instead of dripping the blurry bilinear fallback; the transient cost is slower
    // re-sharpening after motion (fewer covering frames), traded for the permanently sharper limit.
    float upscaleRatio = InvColorSize.x / InvScreenSize.x; // outputRes / renderRes, > 1 under TAAU
    float kscale = upscaleRatio;
    // VARIANCE BOX (5-tap plus pattern, hoisted out of the loop so the EDGE DIRECTION below is available to
    // the reconstruction weights). Fetched at the content-stationary boxUV: both the clamp statistics and
    // the kernel direction stay stable under jitter (a jitter-wobbling kernel direction would itself be a
    // fizzle source). The center tap doubles as the thin-coverage fallback sample (was a separate fetch).
    float3 cboxC = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV, 0, 0)).rgb);
    float3 cboxW = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxE = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxN = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 cboxS = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 m1 = (cboxC + cboxW + cboxE + cboxN + cboxS) * (1.0 / 5.0);
    float3 m2 = (cboxC * cboxC + cboxW * cboxW + cboxE * cboxE + cboxN * cboxN + cboxS * cboxS) * (1.0 / 5.0);
    // EDGE-DIRECTIONAL (ANISOTROPIC) RECONSTRUCTION — the pixel-shader analogue of DLSS/TSR's learned,
    // edge-following sample kernels. On a strong luma edge, stretch the reconstruction kernel ALONG the
    // edge (distances along the tangent count half): thin geometry then gathers several real samples per
    // frame along its own length instead of ~one, which is where the "intense upscaling can't resolve thin
    // geometry" limit came from. Central-difference gradient from the stationary box taps (no extra
    // fetches); direction is jitter-stable. UPSCALE-GATED (kscale-1: 0 at native -> 1 at <= 0.5x) so the
    // validated native-res kernel is untouched, and faded in with edge strength so flat regions keep the
    // separable kernel bit-exactly.
    float2 grad = float2(cboxE.x - cboxW.x, cboxS.x - cboxN.x);
    float gmag = length(grad);
    float edgeAniso = smoothstep(0.15, 0.5, gmag) * saturate(kscale - 1.0);
    float2 en = grad / max(gmag, 1e-5);   // across-edge unit direction
    float2 et = float2(-en.y, en.x);      // along-edge unit direction
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0)); // neighborhood stddev (clamp box + clutter test below)
    // CLUTTER-ADAPTIVE KERNEL WIDTH (leaves "put together"): foliage is isotropic sub-pixel CLUTTER — high
    // variance but NO coherent gradient direction — so the anisotropic path (built for directed edges)
    // leaves it as scattered per-pixel fragments, and at ratio 3 the output-sized kernel is so tight that
    // nothing bridges neighbouring fragments: leaves fizzle instead of consolidating, regardless of trust
    // (their quasi-random alternation can never earn the full lock, like sand). Widen the kernel toward
    // 0.78x on directionless high-variance neighbourhoods at extreme upscale: fragments support each other
    // -> solid clusters, per-frame reconstruction variance drops BEFORE the blend (flicker falls without
    // touching trust). Lines (dirCoherence -> 1) keep the sharp anisotropic kernel; flat regions (low
    // sigma) and ratios <= 1.5 are bit-exact unchanged.
    float dirCoherence = smoothstep(0.15, 0.5, gmag);
    float clutter = smoothstep(0.10, 0.25, sigma.x) * (1.0 - dirCoherence);
    float kscaleEff = kscale * lerp(1.0, 0.78, clutter * saturate(upscaleRatio - 1.5));
    // Tap k in {-1,0,1} sits at distance fracd + k; at 1:1, fracd == the jitter shift (old jShiftPx).
    // KERNEL SELECT (2026-07-05, FSR2-parity distinctness): at UPSCALE the reconstruction kernel is the
    // FSR2 Lanczos2 approximation (negative lobes kept — Catmull-Rom-class passband; the lobe-clamped
    // Mitchell was the "indistinct TAAU" root: pure-positive = guaranteed low-pass). Ringing is bounded
    // by the 3x3 tap-hull clamp at the reconstruction site (FSR2's own dering recipe). NATIVE keeps
    // Mitchell bit-exactly (user-validated look; also the kernel there is jitter-only-wide, where
    // Mitchell's anti-sparkle convexity matters more than passband). SM3/GL keeps Mitchell everywhere
    // (register budget; no hull clamp there to bound lobes).
#if SM4
    float useLan = step(1.001, upscaleRatio); // branch-free select; SM4/DX only — MojoShader never sees it
    #define RECONK(x) lerp(MitchellK(x), LanczosK(x), useLan)
#else
    #define RECONK(x) MitchellK(x)
#endif
    float3 kx3 = float3(RECONK(abs(fracd.x - 1.0) * kscaleEff), RECONK(abs(fracd.x) * kscaleEff), RECONK(abs(fracd.x + 1.0) * kscaleEff));
    float3 ky3 = float3(RECONK(abs(fracd.y - 1.0) * kscaleEff), RECONK(abs(fracd.y) * kscaleEff), RECONK(abs(fracd.y + 1.0) * kscaleEff));
    // Render-texel-scale weights (kscale 1) for the SOFT display reconstruction (see loop).
    // SM4-ONLY (ps_3_0 temp-register budget — CI X4505 on OGL): the soft display path and several other
    // register-heavy quality features below are gated to SM4. SM3/OGL runs the lean "classic" resolve —
    // the pre-worktree behavior those platforms always shipped.
#if SM4
    float3 kx1 = float3(MitchellK(abs(fracd.x - 1.0)), MitchellK(abs(fracd.x)), MitchellK(abs(fracd.x + 1.0)));
    float3 ky1 = float3(MitchellK(abs(fracd.y - 1.0)), MitchellK(abs(fracd.y)), MitchellK(abs(fracd.y + 1.0)));
    float3 filtSoft = 0;
    float wsumSoft = 0;
    // 3x3 tap hull for the Lanczos dering clamp (FSR2 recipe): the negative lobes may only sharpen
    // WITHIN the local data range, never overshoot past it. Taps are already fetched — ALU only.
    float3 reconHullMin = 1e9;
    float3 reconHullMax = -1e9;
#endif
    float3 filt = 0;
    float wsum = 0;
    float3 crawC = 0; // the raw nearest jittered sample (center recon tap) — see texture-detail lean below
    float wC = 0;     // center tap's actual kernel weight — sample confidence below
    float2 dilatedVel = float2(0, 0);
    float closestDepth = 1e9;
    float closestMask = 0.0;
    float dmin = 1e9, dmax = -1e9; // valid-tap depth RANGE for the disocclusion test below
    // Center velocity tap PRE-FETCHED (the loop re-reads it as its (0,0) plus tap — cached, ~free): the
    // pixel's OWN velocity feeds the foreign-velocity reactive, and its DEPTH anchors the depth-aware
    // reconstruction weights below (must be known before the corner taps are processed).
    // JITTER-COMPENSATED (boxUV, the content-stationary position — same treatment the color box taps
    // received long ago): the velocity buffer is rasterized JITTERED, so raw-uv taps read per-phase-
    // different fragments on sub-pixel geometry — dmin/dmax/centerDepth churned with the jitter and the
    // depth tests FLICKERED on tree edges during motion (the last confirmed artifact after the
    // instrument-bug purge; the buffers themselves simulate honest 12-14px reveal bands).
    float4 vCen = tex2Dlod(velocitySampler, float4(boxUV, 0, 0));
    float2 centerVel = vCen.rg; // unwritten decodes as zero
    float centerDepth = (vCen.a >= 0.5) ? vCen.b : -1.0; // -1 = no depth anchor (weighting disabled)
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float2 ofs = float2(dx, dy) * InvColorSize; // neighborhood spans INPUT texels
        // FETCH DIET (perf: the resolve was ~38 fetches/px): the velocity dilation uses the 5-tap PLUS
        // pattern instead of the full 3x3 — the reference-sanctioned reduction (Playdead's cross; corner
        // contribution to the dilation is marginal). The [unroll]'d literal test compiles the corner taps
        // out entirely. The RECONSTRUCTION keeps all 9 taps (kernel quality). (The variance box is hoisted
        // above the loop now, feeding the kernel direction.)
        // Velocity/depth tap — ALL 9 positions now (was plus-only): the corners' DEPTH feeds the
        // depth-aware reconstruction weights; the dilation/range statistics stay on the plus pattern
        // (validated perf diet — corner contribution to dilation is marginal). JITTER-COMPENSATED
        // positions (boxUV — see the vCen note above): content-stationary taps keep the depth tests
        // phase-stable on sub-pixel geometry.
        float4 v = tex2Dlod(velocitySampler, float4(boxUV + ofs, 0, 0));
        if (dx == 0 || dy == 0)
        {
            // "No velocity written" -> depth sentinel 2.0 (beyond valid [0,1]) so genuinely-far valid
            // pixels still win the nearest-depth tiebreak over unwritten neighbours.
            float d = (v.a >= 0.5) ? v.b : 2.0;
            if (d < closestDepth) { closestDepth = d; dilatedVel = v.rg; closestMask = v.a; }
            if (v.a >= 0.5) { dmin = min(dmin, v.b); dmax = max(dmax, v.b); }
        }
        // Reconstruction tap: RAW texel center (bilinear at an exact center = point fetch) around the
        // nearest jittered sample, weighted by its true distance to the output pixel center. Weight =
        // separable Mitchell blended toward the edge-elongated radial Mitchell by edgeAniso (see above):
        // distances along the edge tangent count HALF, so taps lying along the edge keep real weight.
        float2 tapUV = (baseTexel + float2(dx, dy) + 0.5) * InvColorSize;
        float3 craw = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(tapUV, 0, 0)).rgb);
        // SOFT reconstruction (render-texel-scale Mitchell, no depth/aniso weighting): the display path
        // for legitimately-rejected pixels (big reveals during rotation/pans). A proper smooth upscale of
        // the current frame — the reference response to disocclusion — instead of near-raw. Same taps,
        // ALU only.
#if SM4
        float wSoft = kx1[dx + 1] * ky1[dy + 1];
        filtSoft += craw * wSoft;
        wsumSoft += wSoft;
        reconHullMin = min(reconHullMin, craw);
        reconHullMax = max(reconHullMax, craw);
#endif
        float w = kx3[dx + 1] * ky3[dy + 1];
        if (edgeAniso > 0.001)
        {
            float2 vtap = fracd + float2(dx, dy);
            float dAcross = dot(vtap, en);
            float dAlong = dot(vtap, et);
            float wAni = RECONK(sqrt(dAcross * dAcross + dAlong * dAlong * 0.25) * kscaleEff);
            w = lerp(w, wAni, edgeAniso);
        }
        // DEPTH-AWARE KERNEL WEIGHT (FSR2/DLSS upsampling principle, upscale-only): unweighted, the 9
        // taps mix foreground and background at silhouettes and each jitter phase mixes them DIFFERENTLY
        // — a per-phase shimmer source at exactly the thin-geometry edges. Weight each tap by depth
        // similarity to the pixel's OWN surface (the center tap): a line's estimate comes from line
        // samples, background stays background. Unwritten taps (alpha fringes) count as same-surface;
        // the 0.05 floor keeps wsum sane under depth noise. Native path untouched (validated).
        if (upscaleRatio > 1.001 && centerDepth >= 0.0)
        {
            float dt = (v.a >= 0.5) ? v.b : centerDepth;
            // Slope 5 / floor 0.15 (was 8 / 0.05, adversarial-review Fix C): the aggressive rejection
            // collapsed wsum at interior clothing/limb edges, dropping those pixels to the 3-native-px
            // point fallback every frame — the blocky moving-edge look. The gentler curve keeps enough
            // cross-depth coverage for the FILTERED reconstruction while still favoring same-surface taps.
            w *= max(1.0 - saturate(abs(dt - centerDepth) / max(centerDepth, 0.02) * 5.0), 0.15);
        }
        filt += craw * w;
        wsum += w;
        if (dx == 0 && dy == 0) { crawC = craw; wC = w; } // folds under [unroll]
    }
    // (sigma hoisted above the kernel-width block — it feeds the clutter test now.)
    // Thin-coverage fallback: with the output-sized kernel, some frames leave an output pixel with almost
    // no in-support sample (wsum ~ 0). Divide-guard + smooth fallback to the stationary bilinear estimate
    // at the content-aligned position (the hoisted box center tap — free). Sample confidence already keeps
    // those pixels history-leaning, so the fallback only ever feeds the small current-frame share.
    // Fallback = the NEAREST RAW TEXEL (crawC, point sample), NOT the bilinear box center: bilinear mixes
    // the mover's color a full render texel (3 native px at 0.33x) across EVERY edge — including interior
    // clothing/limb edges — so during motion the TAA INPUT itself carried a blocky, blurry, mover-colored
    // fringe that no history-side rejection could touch (contamination in the current frame, not the
    // history: the "blocky/blurry trailing + interior edge ghosting" report). The fallback is also the
    // main input exactly where the depth-aware kernel zeroes mismatched taps (near edges), which made the
    // depth-blind bilinear doubly wrong there. A point sample is single-surface by construction. At rest
    // the coverage-scaled injection gate keeps fallback frames near-zero anyway; during motion this swaps
    // smear for crispness (masked by motion).
    // Threshold is KSCALE-AWARE (0.15 tuned at kscale <= 2): at kscale 3 a frame whose kernel catches
    // only the tail of one sample yields filt/wsum with tiny wsum — one distant sample AMPLIFIED.
    float3 stationaryC = crawC;
#if SM4
    // Lanczos dering + negative-lobe wsum safety (2026-07-05): with real lobes, wsum can cancel toward
    // (or through) zero on adversarial jitter phases. The saturate() fallback factor already returns 0
    // for wsum <= 0 (pure crawC — correct: no real coverage), the raised 1e-3 epsilon bounds the divide,
    // and the hull clamp bounds the reconstructed value absolutely — a lobe can sharpen within the local
    // data range but never ring past it (FSR2's dering). At native useLan = 0 -> weights are the same
    // all-positive Mitchell as before and the hull clamp is a no-op by construction (a convex combination
    // cannot leave the hull), so this path stays visually identical there.
    float3 recon = clamp(filt / max(wsum, 1e-3), reconHullMin, reconHullMax);
    float3 curr = lerp(stationaryC, recon, saturate(wsum / (0.15 * kscale)));
#else
    float3 curr = lerp(stationaryC, filt / max(wsum, 1e-4), saturate(wsum / (0.15 * kscale)));
#endif

    // TEXTURE-DETAIL PRESERVATION: TAA area-averages EVERYWHERE — unlike MSAA, which only supersamples
    // edges and leaves texture interiors alone — so fine texture detail (sand speckles) converges to a
    // mip-like blur, compounded by the Mitchell reconstruction spreading energy into neighbours every
    // frame. On LOW-VARIANCE neighbourhoods there is no fizzle for the reconstruction to collapse, so lean
    // the input toward the RAW nearest sample there: single-pixel texture energy survives accumulation.
    // NATIVE-ONLY (floorScale, from the resolution-scaled BlendFactor): at low render scales the nearest
    // raw sample can sit far from the output pixel, so the lean is a per-frame reconstruction error (a
    // flicker source) — and the mip-bias path supplies the texture detail properly there instead.
    // High-variance content (foliage, edges, thin lines) keeps the full reconstruction that fixed its fizzle.
    float floorScale = saturate(BlendFactor / 0.03 - 1.0); // 1 at native, 0 at <= 0.5x render scale
    float texDetail = 1.0 - saturate(sigma.x * 12.0);
    curr = lerp(curr, crawC, texDetail * 0.75 * floorScale);

    // FIREFLY SUPPRESSION at upscale (standard input-side TAA stabilizer): a single bright sub-pixel
    // sample (specular glint, one bright leaf texel) otherwise strobes its output pixel once per jitter
    // cycle — an input outlier no accumulation depth can hide, only dilute. Bound the incoming estimate's
    // LUMA against its spatial neighborhood's upper range (generous 2 sigma — texture crunch and genuine
    // bright detail pass; only outliers beyond the neighborhood's own statistics are shaved). Input-side:
    // zero ghost risk. Native path untouched (the raw-lean look there is intentional).
    if (upscaleRatio > 1.5)
    {
        // SYMMETRIC (was upper-bound only): a DARK speckle — a ground/sky sample landing on a pixel that
        // usually shows leaf — dithers exactly like a bright one on clutter, and was unbounded.
        curr.x = clamp(curr.x, m1.x - 2.0 * sigma.x - 0.02, m1.x + 2.0 * sigma.x + 0.02);
        // SPECKLE CONSOLIDATION on directionless clutter (the distant-canopy "dither instead of solid"):
        // sub-render-pixel fragments exist-or-don't per jitter phase, their quasi-random alternation never
        // earns the full lock (by design — see the clutter kernel-width note), so every covering frame
        // re-injects a DIFFERENT fragment pattern and the converged limit cycle reads as dithering. The
        // law from the reverted trust-side attempts: reduce the per-frame variance of the INPUT, don't
        // deepen trust. Lean curr toward the stationary neighborhood mean (m1 — phase-stable box taps) by
        // clutter strength: fragments consolidate into their local average BEFORE the blend, so the
        // injection variance drops with zero ghost risk (current-frame data only; a mean is as honest as
        // a sample). LINES ARE PROTECTED by construction: dirCoherence -> clutter = 0 on directed edges,
        // so thin geometry keeps the sharp anisotropic reconstruction bit-exactly; flat regions have
        // ~zero sigma so the lean is a no-op there. Extreme-upscale only (saturate(ratio-1.5): 0 at
        // <= 1.5x, full at >= 2.5x). Lever if canopy still dithers: raise toward 0.6 (costs canopy
        // sharpness, not line sharpness); if canopy goes MUSHY, lower toward 0.2.
        // 0.4 -> 0.28: the 0.4 dose was tuned while the RCAS auto-sharpen overdose (wrong-height keying,
        // since fixed) was AMPLIFYING the dither ~3x — with the amplifier gone, less consolidation buys
        // the same visual stability and returns some clutter distinctness ("indistinct past 0.5x").
        curr = lerp(curr, m1, 0.28 * clutter * saturate(upscaleRatio - 1.5));
    }

    // Reproject with the dilated velocity (+ jitter delta cancels the jitter baked into the velocity buffer).
    // NO velocity-validity gate: the buffer is un-jittered now, so "velocity never written" decodes as zero
    // velocity = identity reproject — exactly right for static content (2D/backdrop art, alpha fringes that
    // skip the velocity MRT). Gating on the mask made every such pixel output the raw jittered frame forever.
    // Content that moves without writing velocity is caught by the variance clamp + luma feedback instead.
    float2 velocity = dilatedVel;
    float velPx = length(velocity * texSize) * VelGatePxScale; // NATIVE px (see VelGatePxScale)
    // 0.6..2.0 (was 0.35..1.5): with the rejection machinery now structurally sound (center-depth ghost
    // test, evidence wipe, honest disocclusion), SLIGHT motion no longer needs to flip the whole blend
    // regime — sub-pixel drift and slow pans keep their accumulation and reproject through the movement
    // (the reference behavior; previously ANY motion discarded history to raw). Walking sims (~2-4 px/f)
    // still arm everything fully; genuine slow-creep contamination is caught by the ungated evidence
    // paths (Kalman collapse, feature comparison) and the variance clamp.
    float moveGate = smoothstep(TuneMoveGateLo, TuneMoveGateHi, velPx);
    // FOREIGN-VELOCITY REPROJECTION FIX (anti dilation-halo): the nearest-depth dilation gives the RING of
    // background pixels around a mover's silhouette the MOVER's velocity — their (background) history
    // reprojected from the wrong place and passed every depth test, because at a silhouette the 3x3 depth
    // range legitimately spans both surfaces. That misprojection dragged object-colored spill onto the
    // background EVERY frame the edge moved: trust caps could only trade the spill for raw jitter crunch
    // ("edge ringing"), because the contamination was regenerated per frame — debug view showed exactly
    // that signature (high counter, low trust, NO reject firing). The correct treatment is to fix the
    // REPROJECTION, not punish the trust: where the dilated velocity disagrees with the pixel's OWN
    // center-tap velocity, reproject with the own velocity instead. Ring history then reads genuinely
    // valid background — deep blend is safe there, so no spill AND no crunch. Mover-interior/edge pixels
    // have own == dilated (no change); camera pans share motion everywhere (foreign stays 0 screen-wide).
    // Both compared velocities are current-frame fp16 buffer values, so the threshold sits low.
    float velFgnPx = length((velocity - centerVel) * texSize) * VelGatePxScale;
    float foreign = smoothstep(0.75, 2.5, velFgnPx);
    // REPROJECTION IS ALWAYS DILATED (the own-velocity override is RETIRED): switching ring pixels to
    // their center-tap velocity was the first structural anti-halo fix, but during PARALLAX camera pans
    // foreign fires along every silhouette and the switch made adjacent native pixels reproject with
    // DIFFERENT velocities (fg/bg flipping per render texel) — the edge history tore into an unmatchable
    // mix, every edge rejected, and lateral motion read as fully raw. Dilated reprojection keeps edges
    // moving coherently with their foreground (the DLSS/FSR2 standard); the ring contamination the
    // switch used to prevent is now scrubbed by the LATER machinery (center-depth ghost test, evidence
    // wipe, honest disocclusion). foreign remains as a SIGNAL (suspicion, trust cap, lock exclusion).
    float vmag = length(velocity);
    // ROOT CAUSE (fresh-eyes forensic verdict, after factor experiments -1/0.5/1/renderScale all failed
    // and probes certified matrices k=1.000 + writers ratio=0.999): the stored history depth is the
    // DILATED (fattened-foreground) silhouette, and at silhouettes the DILATED velocity is the
    // FOREGROUND's — so background pixels carried the stored near-band at FOREGROUND speed across a
    // background that moves slower: the depth phantom literally OUTRUNS the scene (the user's repeated
    // observation), by a fg/bg velocity RATIO no histUV scalar can touch. SPLIT REPROJECTION fixes it:
    // COLOR keeps the dilated velocity (edge quality — switching color to own-velocity tore parallax
    // edges when tried); the DEPTH/STRUCTURE TESTS reproject with the pixel's OWN velocity, so a
    // background pixel tests its stored depth at the position its own surface actually occupied.
    // MAGNITUDE-GATED OWN-VELOCITY COLOR REPROJECTION (2026-07-05 — the character-INTERIOR ghost):
    // inside a deforming character, limbs overlap the torso at different depths and slightly different
    // velocities, so nearest-depth dilation hands interior pixels a NEIGHBORING BONE's velocity — a
    // systematic sub-pixel-to-few-px reprojection error every frame, too small to arm foreign at slow
    // speeds: the persistent interior fizzle/ghost during movement. This is NOT the retired binary
    // own-velocity override (whose failure lived at LARGE disagreements — fg/bg parallax at silhouettes,
    // where per-texel velocity flipping tore edges): the gate keys on the disagreement MAGNITUDE. Small
    // disagreement (intra-character deformation) -> the pixel's OWN velocity, exact reprojection; large
    // disagreement (true silhouette parallax) -> dilated, bit-identical to the shipped behavior the
    // edge-tear fix demanded. The 1.5..3.0 native-px knee sits above foreign's arm point so the blend is
    // smooth through the transition band. Invalid centers (unwritten velocity) keep dilated as before.
    float2 histVel = lerp((centerDepth >= 0.0) ? centerVel : velocity, velocity, smoothstep(1.5, 3.0, velFgnPx));
    float2 histUV = uv - histVel + JitterDelta;
    float2 ownVel = (centerDepth >= 0.0) ? centerVel : velocity;
    float2 histUVDepth = uv - ownVel + JitterDelta;
    bool reprojectable = (histUV.x >= 0) && (histUV.x <= 1) && (histUV.y >= 0) && (histUV.y <= 1);

    // History fetch (bicubic for detail) + a POINT tap for the packed depth in alpha (see sampler comment).
    float4 historyPoint = FetchHistoryPoint(histUVDepth); // OWN-velocity anchor (split reprojection — see above)
    float3 historyRaw = RGB_to_YCoCg(SampleHistoryBicubic(histUV));

    // --- RING-CONTAMINATION SIGNAL (the split-reprojection blind spot). COLOR reprojects with the DILATED
    //     velocity (histUV) while every structural test reprojects with the pixel's OWN velocity
    //     (histUVDepth). At a moving silhouette those are DIFFERENT SURFACES: a background ring pixel reads
    //     FOREGROUND-trail color into historyRaw, yet the depth/ghost/feature tests all inspect the
    //     own-velocity BACKGROUND history — so they certify "valid" and preserve the contaminated color
    //     (the residual dilation halo the own-velocity tests are structurally blind to; only the variance
    //     clamp caught it before). Measure the disagreement DIRECTLY: the own-velocity-anchored history
    //     color (historyPoint.rgb — already fetched for its depth, free) vs the dilated-anchored blended
    //     color. GATED BY foreign (zero screen-wide on pans and on mover interiors, where own == dilated),
    //     so it arms ONLY on genuine ring pixels; the color-diff knee (0.04..0.15) means a CLEAN pan — where
    //     both reprojections land on the same background — stays silent even though foreign fires there.
    //     Upscale-only (native has no split-surface ring). Feeds diff + suspicion below like a reject. ---
    float ringContam = 0.0;
#if SM4 // ps_3_0 temp-register budget (CI X4505 on OGL) — SM3 runs without this signal
    if (!debugMeta && upscaleRatio > 1.001)
    {
        float3 histOwn = RGB_to_YCoCg(historyPoint.rgb);
        // Knee 0.04..0.15 -> 0.03..0.10 (2026-07-05): user still saw a slight edge halo/ringing GHOST at
        // mover silhouettes under TAAU — same-palette halos (hair-over-skin, skin-over-wall) produce a
        // smaller own-vs-dilated color disagreement than the old knee needed to arm fully. Earlier onset +
        // full strength by 0.10 scrubs the subtle ring; clean pans stay silent (both reprojections land on
        // the same background -> length ~ 0 regardless of knee). Revert signature: edge crunch/rejection
        // bands on mover silhouettes during ordinary walking.
        ringContam = foreign * smoothstep(TuneRingLo, TuneRingHi, length(histOwn - historyRaw));
    }
#endif

    // Reprojected previous meta: per-pixel accumulation count N (R) + previous frame's dilated velocity (GB).
    // No normal buffer — that MRT is only written by SOME passes, so a normal-based disocclusion test fired
    // inconsistently on exactly the minified content where the buffer aliases frame-to-frame.
    float4 pm = tex2Dlod(metaHistorySampler, float4(histUV, 0, 0));
    float prevN = pm.r * MaxAccum;

    // Depth disocclusion (relative, since depth is normalized linear 0=near..1=far). historyPoint.a holds the
    // dilated depth visible at this texel last frame. CRITICAL: compare against the whole 3x3 depth RANGE,
    // not the single dilated (nearest) depth — at a static edge the sub-pixel jitter flips which neighbour
    // wins the nearest-depth contest (fg one frame, bg the next), so a point compare sees a huge delta every
    // frame and permanently resets accumulation along EVERY silhouette (edges were pinned at N=0 — the exact
    // pixels TAA exists to resolve, hence "jitter shows through on all edges"). With the range test a static
    // edge always contains the history depth (both fg and bg are in the neighbourhood) -> accumulates; a true
    // disocclusion (surface left entirely) still lands outside the range -> still rejects.
    // ALL depth rejection is MOTION-GATED (moveGate below). A genuine disocclusion requires something to
    // have MOVED; at rest, with ~zero dilated velocity, any depth mismatch is sampling noise — and foliage
    // proves it: grass blades / leaves are SUB-PIXEL geometry, so the jitter flips which fragments exist at
    // all each frame (blade one frame, ground/sky the next). The 3x3 valid depth RANGE itself jumps between
    // populations and the remembered dilated depth lands outside it -> depthReject fired PERMANENTLY at rest
    // across all foliage (debug view: green speckle), pinning the blend at the responsive floor so no history
    // ever accumulated there — exactly the pixels that most need deep accumulation. moveGate is structurally
    // 0 at rest (un-jittered velocity; unwritten = zero) so static content can't self-reject; movers, pans
    // and zooms all produce real velocity, so genuine disocclusions keep full rejection. Content appearing
    // WITHOUT motion (cutaway wall toggles, build-mode placement) is caught by the variance clamp + luma
    // responsiveness, as in the original build.
    // (velPx / moveGate / foreign are computed with the reprojection above, before the history fetch.)
    // VELOCITYLESS-CONTENT CLASS: the whole 3x3 wrote NO velocity — every motion-evidence system
    // (depth/ghost rejects, reactive, foreign) is structurally silent here. Real scene content always
    // dilates SOME velocity into its neighborhood; this class is the diegetic overlays (headline icons,
    // speech bubbles — drawn after the velocity MRT unbinds). (The sky dome DOES write velocity via
    // SkyVelocity, so it is NOT in this class despite reading flat.) They animate and track their
    // avatars with zero motion signal, so deep trust smeared them badly under TAAU: cap their window
    // (~8 frames, below) and bar them from oscillation locks (their bobbing sign-alternates).
    float noVel = (dmax < dmin) ? 1.0 : 0.0;

    // REJECTION AUTHORITY (color-evidence proportionality — the pan-time fix): depth evidence proves the
    // history is geometrically stale; COLOR evidence measures how wrong it actually LOOKS. A camera-
    // parallax reveal (every silhouette band during lateral pans, at render-texel width = 3 native px at
    // 0.33x) is stale by a sub-texel sliver — its history still matches the scene, and full-raw rejection
    // costs far more than the error (the "green over blue" bands). A mover trail is stale by the mover's
    // entire color -> full authority. Scaled at the SOURCE so every consumer (diff, honest floor, counter
    // resets, suspicion, evidence wipe) inherits proportionality. The history render-texel average
    // fetched here is REUSED by the input-resolution rectification below. 1:1 keeps full authority.
    float3 hLow = historyRaw;
    float rejAuth = 1.0;
#if SM4 // ps_3_0 temp-register budget (CI X4505 on OGL) — SM3 keeps full rejection authority, direct clamp
    if (upscaleRatio > 1.5)
    {
        float2 hOfs = InvColorSize * 0.25;
        hLow = (RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2( hOfs.x,  hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2( hOfs.x, -hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2(-hOfs.x,  hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2(-hOfs.x, -hOfs.y), 0, 0)).rgb)) * 0.25;
        // Floor 0.3 + knee 0.02..0.08 (was a full mute with knee 0.03..0.15 — ghost returned at 0.33x:
        // BOTH comparison sides are render-res lowpassed there, squashing a real trail's measured error
        // under the old knee). Geometric staleness now always keeps >=30% scrub authority (bounded lag,
        // never indefinite), and the lower knee restores full authority for visible trails.
        // OSC-AWARE FLOOR: on pixels carrying sign-alternation evidence (the sub-pixel-geometry
        // signature a ghost cannot fake), a COLOR-SILENT depth reject is phase CHURN, not disocclusion —
        // fragments exist-or-don't per jitter phase on canopy, so the depth taps flip regardless of
        // sampling position. The 0.3 floor let that churn wipe locks and reset trust every phase (the
        // flickering tree-edge rejects + motion fizzle). Evidence-proven pixels drop the color-silent
        // floor to 0.05; any visible contamination still gets full authority through the color knee.
        float prevOscE = debugMeta ? 0.0 : saturate((pm.a - 0.5 * step(0.5, pm.a)) / 0.498);
        // RELATIVE-MOTION OVERRIDE (fixes the slow-motion sim ghosting this muting introduced): sand is
        // ALSO oscillation-proven, so a sim's trail over it was muted along with canopy churn — but a
        // TRAIL remembers the mover's velocity differing from the pixel's own (relMotion), while canopy
        // during a pan shares the camera's motion with its surroundings. Relative motion restores
        // authority regardless of oscillation evidence.
        float relFgnPxE = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1 - centerVel) * texSize) * VelGatePxScale;
        float relMotionE = smoothstep(0.75, 2.5, max(velFgnPx, relFgnPxE));
        float authFloor = max(lerp(0.3, 0.05, smoothstep(0.25, 0.6, prevOscE)), relMotionE * 0.65);
        rejAuth = lerp(authFloor, 1.0, smoothstep(0.02, 0.08, length(m1 - hLow)));
    }
#endif

    float historyDepth = historyPoint.a;
    float outside = max(max(dmin - historyDepth, historyDepth - dmax), 0.0);
    float depthReject = (dmax < dmin) ? 0.0 :
        saturate((outside / max(historyDepth, DepthRejectParams.w)) * DepthRejectParams.y - DepthRejectParams.z);
    depthReject *= moveGate * rejAuth;

    // --- GHOST-SIDE REJECTION (the disocclusion centrepiece): fires only on the GHOST side — history depth
    //     NEARER than every valid current tap = the surface that wrote it has left (trailing edge of a mover).
    //     Dead-zone epsilon (DepthRejectParams.x) keeps storage quantization alone from ever firing it. ---
    float nearer = max(dmin - historyDepth - DepthRejectParams.x, 0.0);
    float ghost = (dmax < dmin) ? 0.0 : saturate(nearer / max(historyDepth, DepthRejectParams.w) * 12.0);
    // CENTER-DEPTH GHOST TEST (the trailing-band hole): the range test above can NEVER fire while the
    // mover is still inside the 3x3 — the stale history depth (the mover's) sits exactly at dmin, so the
    // strip of pixels right behind a continuously-walking mover (3x3 render texels = ~9 NATIVE px at
    // 0.33x) was permanently exempt from ghost rejection and scrubbed only via the slow diff path: THE
    // persistent trailing ghost, worse at lower scale. Ask instead: is the history NEARER than the surface
    // actually at THIS pixel now (the center tap)? The trailing band fires instantly (history = near
    // mover, center = far background); a pixel still on the mover doesn't (equal depths); pans don't
    // (same surface after reprojection). Safe where the range test's designers feared edge flips because
    // it inherits the motion gating (current OR remembered) that did not exist back then — resting
    // foliage keeps its gates closed. Invalid center (unwritten velocity) falls back to the range test.
    // Softer than the range test (slope 8, weight 0.8): the stored history depth is DILATED (the mover's
    // depth extends a render texel past its silhouette), so this test also brushes a band of clean pixels
    // around every moving edge — at full strength that band re-rawed every frame and read as edge aliasing
    // once the ghost itself was gone. Strong evidence still scrubs; grazing evidence no longer serrates.
    // RELATIVE-MOTION GATED: depth alone cannot distinguish "the mover left this pixel" from "camera
    // panning past a static edge" — the stored dilated depth band around EVERY silhouette fired this
    // test throughout lateral pans (the pan-time rejection bands). A true trailing band has the near
    // content moving RELATIVE to the background (current foreign velocity, or the REMEMBERED velocity
    // vs the pixel's own); a static edge shares the camera's motion with its background -> silent.
    float storedFgnPx = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1 - centerVel) * texSize) * VelGatePxScale;
    float relMotion = smoothstep(0.75, 2.5, max(velFgnPx, storedFgnPx));
    float nearerC = (centerDepth >= 0.0) ? max(centerDepth - historyDepth - DepthRejectParams.x, 0.0) : 0.0;
    ghost = max(ghost, saturate(nearerC / max(historyDepth, DepthRejectParams.w) * 8.0) * 0.8 * relMotion);
    // Gated by CURRENT motion OR REMEMBERED motion (the stored meta velocity). Current-only gating had a
    // one-frame timing hole that made mover haze un-scrubbable: the instant the mover exits this pixel's
    // 3x3, dilated velocity drops to zero -> moveGate closes -> the ghost-depth evidence (history depth =
    // the mover's, provably nearer than the whole current range) could never fire, and the contaminated
    // color was left to the slow diff decay — the persistent object haze (debug view: haze pixels showed NO
    // green). A trailing-reveal pixel REMEMBERS the mover's velocity from last frame in pm.gb; resting
    // foliage (the reason the motion gate exists) remembers zero, so it cannot fake this signal.
    float storedMovePx = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1) * texSize) * VelGatePxScale;
    float storedMove = smoothstep(0.35, 1.5, storedMovePx); // matches moveGate's slow-mover arming
    float ghostReject = max(moveGate, storedMove) * ghost * rejAuth;

    // --- FEATURE-LEVEL HISTORY COMPARISON (structure, not value — the DLSS-analogue rectification cue).
    //     A ghost carries STRUCTURE: its own edges, at positions/orientations the current frame does not
    //     confirm. Value-diff misses contamination that preserves average brightness; comparing the
    //     history's luma gradient against the current one catches it. Two signals, RESPONSIVE-ONLY (they
    //     can only increase diff, never protect — law-compliant): (a) both gradients strong but pointing
    //     differently (normalized correlation low); (b) history has structure where the current frame is
    //     FLAT (the classic ghost-on-plain-background). MOTION-ADJACENT GATED (current or remembered
    //     velocity): at rest, converged fine geometry's native-res history gradient legitimately
    //     out-details the render-res current gradient — ungated, this would re-fizzle exactly what the
    //     locks stabilize. Cost: 4 point history taps. ---
    float featReject = 0.0;
#if SM4 // ps_3_0 temp-register budget (CI X4505 on OGL) — SM3 relies on the depth/ghost/reactive rejects
    {
        float3 lw = float3(0.25, 0.5, 0.25); // YCoCg Y from RGB
        float hE = dot(FetchHistoryPoint(histUVDepth + float2(InvScreenSize.x, 0)).rgb, lw);
        float hW = dot(FetchHistoryPoint(histUVDepth - float2(InvScreenSize.x, 0)).rgb, lw);
        float hS = dot(FetchHistoryPoint(histUVDepth + float2(0, InvScreenSize.y)).rgb, lw);
        float hN = dot(FetchHistoryPoint(histUVDepth - float2(0, InvScreenSize.y)).rgb, lw);
        float2 gradH = float2(hE - hW, hS - hN);
        float gH = length(gradH);
        float gC = gmag; // current luma gradient magnitude (stationary box taps, render-res)
        // (a) directional disagreement where both frames claim structure
        float bothStrong = smoothstep(0.08, 0.2, min(gH, gC));
        float ncorr = dot(gradH, grad) / max(gH * gC, 1e-5);
        float dirMismatch = bothStrong * saturate((0.3 - ncorr) * (1.0 / 0.6));
        // (b) history structure over a current flat — the ghost signature
        float structGhost = smoothstep(0.1, 0.25, gH) * (1.0 - smoothstep(0.03, 0.1, gC));
        featReject = max(moveGate, storedMove) * saturate(max(dirMismatch, structGhost)) * 0.6;
    }
#endif

    // --- VELOCITY-DISPARITY REACTIVE (FSR2 lock-break analogue): compare this frame's dilated velocity with
    //     the velocity stored alongside the history. A mismatch means the history was written by content
    //     moving differently than what's here now — reveals after the mover left the 3x3, starts/stops,
    //     direction changes — cases the ghost-side depth test can miss. Trust modulation only (ONE history;
    //     not the dual-rate regression). Zero-vs-zero on backdrop/fringes = no signal (not the mask-gate
    //     regression). Threshold is resolution-scaled: one 8-bit LSB of the encode in PIXELS grows with
    //     resolution, and the old fixed lower edge would sit ON the noise floor at 4K. ---
    float reactive = 0.0;
    if (!debugMeta)
    {
        float2 prevVel = (pm.gb - 0.5) * 0.1;             // inverse of the *10+0.5 encode
        float lsbPx = 3.922e-4 * texSize.x * VelGatePxScale; // one 8-bit LSB of the encode, in NATIVE px
        float dispLo = max(1.5, 2.0 * lsbPx);             // resolution-scaled noise floor
        float velDispPx = length((velocity - prevVel) * texSize) * VelGatePxScale;
        reactive = smoothstep(dispLo, dispLo + 4.5, velDispPx);
    }

    // Center tap's ACTUAL kernel weight normalized by that kernel's peak: 1 = this frame's nearest real
    // sample sits on the output pixel. Mitchell peaks: separable k(0)^2 = 0.7901 (1/0.7901 = 1.2656),
    // radial k(0) = 0.8889 (1/0.8889 = 1.125). Lanczos2 peaks at exactly 1 (separable AND radial), so
    // under the upscale kernel select the normalization is 1.0 — and wC can now go slightly NEGATIVE
    // when the center tap lands in a lobe (kscale*|fracd| > 1): saturate reads that as "no real
    // coverage", which is truthful. Hoisted here for the WITNESS RULE, used by both the oscillation
    // detector and the Kalman counter below: under TAAU, off-phase frames are interpolation, not
    // observation — they may neither testify against history nor build/decay alternation evidence.
#if SM4
    float sampleConf = saturate(wC * lerp(lerp(1.2656, 1.125, edgeAniso), 1.0, useLan));
#else
    float sampleConf = saturate(wC * lerp(1.2656, 1.125, edgeAniso)); // upscaleRatio hoisted to the kernel block
#endif
    float testify = (upscaleRatio > 1.001) ? sampleConf : 1.0;

    // SUSPICION: the union of every contamination/disocclusion detector. THE motion-trust variable —
    // speed is the wrong regime key (a fast coherent pan reprojects exactly; slow creep can still
    // contaminate), so the trust-limiting gates below scale their motion response by whether the
    // evidence actually flags anything. Left/right pans previously went raw purely by exceeding a
    // velocity threshold while their reprojection was flawless.
    // FOREIGN demoted to 0.35 weight: lateral pans create PARALLAX, so foreign fires along EVERY object
    // silhouette during L/R/diagonal camera motion — but foreign's job is to FIX the reprojection (ring
    // pixels reproject with their OWN velocity; their history is VALID after the correction), so counting
    // it as full suspicion double-punished exactly the pixels it had just repaired (the L/R-pan edge
    // rejection bands). True reveals at those silhouettes stay covered by the depth/ghost rejects at
    // full weight; foreign keeps its own mild 0.92 trust cap and its lock exclusion.
    // ringContam joins at full weight (unlike foreign's demoted 0.35): where it fires it is DIRECT evidence
    // of preserved foreground trail on a background pixel — the exact contamination foreign's reprojection
    // fix does NOT repair under dilated-color reprojection.
    float suspicion = max(max(depthReject, ghostReject), max(max(foreign * 0.35, reactive), ringContam));

    // --- LUMA-OSCILLATION DETECTOR (Decima-style anti-fizzle), state in meta.A (1 sign bit + 7-bit EMA).
    //     Fizzle = a CONVERGED pixel whose curr-vs-history luma delta ALTERNATES SIGN at frame frequency
    //     (jitter flipping which sub-pixel fragment covers the sample). A ghost's delta is MONOTONIC (stale
    //     history decays one-way), so sign-alternation is a signal a ghost structurally cannot produce —
    //     the one trust gate that survives this project's "every magnitude/stability gate ghosted" history.
    //     Measured on PRE-blend curr vs PRE-clamp historyRaw so deeper trust doesn't extinguish its own
    //     evidence (no limit cycle). Disabled under debugMeta (GB/A carry diagnostics there; osc=0 -> no-op).
    float osc = 0.0;
    float packedA = 0.0;
    if (!debugMeta)
    {
        float prevSgn = step(0.5, pm.a);
        float prevOsc = saturate((pm.a - 0.5 * prevSgn) / 0.498);
        float dl   = curr.x - historyRaw.x; // signed, pre-clamp history
        // Amplitude gate 0.03: keeps low-contrast texture shimmer (sand speckles) OUT of trust-deepening.
        // A lower low-scale threshold (0.012) was tried to let sand earn the lock — REVERTED: in the
        // small-delta regime, slight ghost residue over a textured surface sign-alternates exactly like
        // texture sampling noise (the texture's noise rides on top of the residue), so the widened
        // admission protected ghosts around mover silhouettes. Sand is an INPUT-side problem (mip bias),
        // not a trust-side one.
        float mag  = step(0.03, abs(dl));
        float sgn  = step(0.0, dl);
        float flip = mag * abs(sgn - prevSgn); // 1 only when a real-amplitude delta reversed sign
        // WITNESS RULE on the EMA (the residual TAAU flicker root): off-phase frames' interpolation error
        // is BIASED (no sign flip), so an ungated EMA DECAYED the alternation evidence between real
        // samples — fine geometry hovered half-locked forever at low scale. Off-frames now neither build
        // nor decay evidence (update rate scaled by testify); the lock holds solid between real samples.
        // WITNESS-RATE BOOST at extreme upscale (the tractable, no-encoding-change stand-in for true
        // phase-bucketed oscillation evidence). At 1/3 render scale an output pixel is witnessed only
        // ~1 frame in 9, so at the fixed 0.15 rate the alternation EMA needed many real samples to cross
        // the lock threshold and fine geometry hovered below it forever (the residual low-scale fizzle).
        // Raise the PER-WITNESS build/decay rate with the upscale ratio so the lock is EARNED in fewer
        // witnessing frames. The rate is symmetric (flip 0 decays as fast as flip 1 builds), so it only
        // speeds convergence to the SAME equilibrium — it does not raise the osc floor, and a monotonic
        // ghost still cannot alternate its way to a lock. Scale-gated past 2x ratio: native and 0.5x keep
        // the validated 0.15 rate BIT-EXACT (saturate(upscaleRatio-2) = 0 there). Full phase-bucketed
        // evidence (per-jitter-phase state) is a separate meta-encoding redesign — see the review notes.
        // ASYMMETRIC rates (the lock-chatter fix): the symmetric boost also DECAYED at 2.2x on witnessed
        // frames whose sign pair legitimately agrees — a converged limit cycle is not strictly
        // alternating — so on borderline clutter (alpha-cutout leaves) osc chattered across the lock
        // thresholds and the pixel's TREATMENT flickered locked<->floored every few frames: the
        // "dithery/indecisive" leaf look. Evidence FOR alternation builds at the boosted rate (locks
        // still earned fast at 1/3 scale); absence-of-flip decays at the base rate (a lock is lost no
        // faster than native). Ghost-safety unchanged: a monotonic ghost emits flip=0 STREAMS, which
        // still decay to zero (just at native speed), and the evidence wipe / off-screen reset bypass
        // the EMA entirely on any real invalidation.
        float oscRateUp = 0.15 * lerp(1.0, 2.2, saturate(upscaleRatio - 2.0)); // 0.15 <=0.5x -> 0.33 at 0.33x
        float oscRate = lerp(0.15, oscRateUp, flip); // build boosted, decay base
        osc = lerp(prevOsc, flip, oscRate * testify); // ~6-7 frame EMA on witnessing frames (faster build at >2x)
        // EVIDENCE WIPE (closes the ghost-under-lock hole, user-diagnosed via the debug view: ghost
        // sitting under bright BLUE trust with the RED counter only slowly refilling): the alternation
        // evidence previously SURVIVED history invalidation — a camera turn fired rejects/reactive and
        // reset N, but the stored osc rode along, so the moment motion stopped the locks re-engaged on
        // CONTAMINATED history, bypassed the warmup floor (lock exemption), widened the clamp, and
        // trapped the ghost under deep trust. Locks must be RE-EARNED after any invalidation event —
        // this line is what actually makes "a lock cannot coexist with invalid history" true.
        // Curved: only MEANINGFUL invalidation wipes (smoothstep knee 0.25) — grazing partial rejects
        // (noisy, constant near any motion) were nuking locks screen-adjacent to movers every frame,
        // re-fizzling fine geometry that was never actually invalidated (the post-ghost-fix aliasing).
        osc *= 1.0 - smoothstep(0.4, 0.85, max(max(depthReject, ghostReject), max(reactive, featReject)));
        float newSgn = lerp(prevSgn, sgn, mag * testify); // hold the sign bit through quiet/blind frames
        packedA = reprojectable ? saturate(newSgn * 0.5 + osc * 0.498) : 0.0; // off-screen = evidence reset
    }

    // --- EVIDENCE-CONDITIONED ACCUMULATION (Kalman-gain counter). The warmup ramp blend >= 1/(N+1) IS a
    //     Kalman gain — but for a filter whose every observation CONFIRMS the estimate: N counted FRAMES.
    //     Now N counts EVIDENCE. The innovation |curr - history| is normalized by the neighbourhood stddev
    //     (sigma = the expected sampling noise for THIS content: sand expects large innovations, a flat
    //     wall expects none). Verdicts:
    //       * agreement (innovation within the noise) grows N (+1/frame, as before);
    //       * SIGN-ALTERNATING innovation counts as agreement regardless of size — zero-mean noise, the
    //         converged-fine-geometry signature (without this, oscillating pixels equilibrated at N~6 and
    //         the ramp floor re-fizzled exactly what the locks stabilize; ghosts/content changes are BIASED
    //         one-way and cannot claim it — the osc signal again);
    //       * persistent one-sided disagreement COLLAPSES N multiplicatively (x0.5/frame): deep trust
    //         unwinds in 2-3 frames and stays responsive until the scene settles. Content changes WITHOUT
    //         motion (TV screens, lighting, cutaway toggles) previously leaned on the instantaneous diff
    //         curve alone.
    //     TAAU WITNESS RULE: only frames whose nearest real sample covers this output pixel may testify
    //     AGAINST history (off-phase frames are interpolation — they would falsely accuse converged thin
    //     geometry); off-frames still accrue trust weakly (x0.3). Ghost-safe BY DIRECTION: a collapsed N
    //     only ever ADDS current weight via the ramp — no deepening past baseline (that direction ghosted
    //     in every variant tried). Hard-reset only when history is off-screen; deliberately NOT zeroed by
    //     depthReject (noisy edge signal once pinned silhouettes at N=0); ghost/depth/reactive caps below.
    float inno = abs(curr.x - historyRaw.x) / max(sigma.x, 0.02);
    // Osc protection edge 0.12 (was 0.25): under TAAU the off-phase frames' interpolation error is BIASED
    // (interpolation underestimates thin bright detail), so the alternation EMA decays between real samples
    // and fine geometry's protection starved — trees collapsed to low N and the ramp floor re-injected
    // jitter. Collapse 0.75 (was 0.5): a verdict should need a few consistent frames, not two.
    // RELATIVE-MOTION CARVE-OUT on the osc branch (matches the one rejAuth/ghostReject already carry): the
    // sign-alternation branch otherwise let ANY oscillation-proven pixel keep growing/holding N regardless
    // of a trail crossing it — so a SLOW sim/drag over oscillation-locked ground (sand, canopy) held its
    // deep N (agreeK -> collapse=1, growK grows) and the ghost sat under the very lock the osc signal
    // granted. The depth/ghost rejects got relMotion so a trail-over-locked-ground still scrubs; the Kalman
    // counter's osc protection was the one consumer that DIDN'T, so the two failed together at low relative
    // speed. Withdraw the osc protection where the pixel's own surface is moving relative to the history
    // (relMotion) — the innovation branch alone then governs, so a real trail collapses N normally while a
    // static locked pixel (relMotion 0) keeps full protection.
    float agreeK = max(1.0 - smoothstep(1.0, 2.5, inno), smoothstep(0.12, 0.35, osc) * (1.0 - relMotion));
    // SLIGHT-BIAS PENALTY (the persistent-tail discriminator — respects the osc law). A faint monotonic ghost
    // has a small ONE-SIDED innovation that the agreement branch reads as ~full agreement (inno < 1 ->
    // agreeK ~ 1), so N maxes and the deep history holds it long after motion evidence is gone. The one
    // resolve-side signal that separates it from converged content is OSCILLATION: texture churn (where the
    // "residue indistinguishable from texture noise" law holds) is HIGH-osc; a truly-flat converged pixel has
    // ~ZERO innovation; a slight ghost on a non-textured surface is LOW-osc with a small-but-REAL innovation.
    // Dock agreeK in that band only (low osc + a mid-innovation window, well clear of both the flat-converged
    // floor and genuine large changes) so the counter settles shallower and the residue washes out. Gated to
    // low osc = non-textured surfaces (where the discriminator is valid — high-osc churn is untouched, so no
    // conflict with the anti-fizzle lock), and to upscale (native bit-exact). Never deepens (min()).
    // Lever to push if ghosting persists: raise the 0.35 dock toward 0.55.
    // NEAR-STATIC GATE (the slow-motion fizzle fix): this penalty targets the post-motion tail on a pixel
    // that is now STATIONARY (the mover has gone). During SLOW MOTION the same low-osc + mid-inno signature
    // appears for an INNOCENT reason — a slowly-drifting pixel's sub-pixel reproject error is small,
    // one-sided (a drift doesn't sign-alternate -> low osc), and lands in the mid-inno band — so UNGATED this
    // docked agreeK on moving pixels, collapsed their N, and injected point-sampled current: exactly the
    // fizzle/speckle-ghosting seen during slow motion (shallow N -> raw current = speckle, while the residual
    // deep history lingers = ghost). Restrict it to velPx ~ 0 (its real domain: the static tail; a
    // just-revealed background pixel reads ~zero dilated velocity), fully off by ~0.35 native px so any real
    // drift keeps its accumulation and reprojects through the motion instead of collapsing to raw.
    // TRULY-FLAT GATE (the edge-halo fizz fix): at rest, a pixel 1-2px from a hard edge / thin feature
    // (a cord on a wall, a door silhouette) ALSO sits in the low-osc + mid-inno band — the jitter
    // modulates the feature's energy in its neighborhood every phase, but not sign-alternately enough to
    // clear the osc gate — so the penalty fired PERMANENTLY there, pinning N at a shallow equilibrium
    // (~7 -> the ramp floor injects ~12% raw/frame): a fizzy ring around every edge on flat surfaces.
    // The discriminator: a slight GHOST's defining venue is a genuinely FLAT surface (low sigma — that
    // is exactly why its small innovation is meaningful), while an edge halo has the edge in its own
    // box statistics (high sigma). Gate to low sigma: the anti-tail keeps its whole domain, the halo
    // ring is exempt.
    float biasPenalty = (1.0 - smoothstep(0.12, 0.35, osc))
                      * smoothstep(0.25, 0.7, inno) * (1.0 - smoothstep(1.0, 2.0, inno))
                      * (1.0 - smoothstep(0.05, 0.35, velPx))
                      * (1.0 - smoothstep(0.04, 0.12, sigma.x))
                      * saturate(upscaleRatio - 1.0);
    // Dock 0.35 -> 0.55 (2026-07-05): pushing the documented lever — user still saw slight fizzly
    // ghosting on SIMILAR-COLOR content under TAAU (exactly this penalty's domain: low-osc,
    // small-but-real one-sided innovation on a static flat pixel). The three gates (low-osc band,
    // near-static velPx, low sigma) stay untouched, so the known mis-fire modes (slow drift, edge
    // halos) remain excluded. Failure signature if too strong: faint noise on low-osc flat detail
    // (gradients/logos) at upscale.
    agreeK = min(agreeK, 1.0 - 0.55 * biasPenalty);
    float collapse = lerp(1.0, lerp(0.75, 1.0, agreeK), testify); // testify hoisted above the osc detector
    // GROWTH is witness-gated only once EVIDENCE exists: the witness rule protects CONVERGED history from
    // off-phase false testimony — but it was also throttling REBUILDING to ~0.3/frame under TAAU, so every
    // pixel a mover or camera-reveal swept re-converged in slow motion: the wide low-N "inverse ghost"
    // trail behind all motion, worst at 0.33x where witnessing frames are rarest. A fresh pixel counts
    // every frame (all samples are information when you know nothing); the off-phase discount fades in
    // with minN as there is real history for off-phase frames to falsely accuse.
    float growK = agreeK * lerp(1.0, lerp(TuneGrowOffPhase, 1.0, testify), saturate(prevN / 8.0));
    float newN = reprojectable ? min(prevN * collapse + growK, MaxAccum) : 0.0;
    // Ghost-side reject now RESETS the counter (was a soft-cap to 2): the surface that wrote the history has
    // provably left, so the honest treatment is the same as off-screen — raw current, then the warmup ramp
    // rebuilds (1 -> 1/2 -> 1/3...). The old cross-fade left a hazy 2-3 frame ghost mix, chunky at low res.
    // SHAPED resets (the counter was the last unshaped reject consumer): PARTIAL band rejects — constant
    // along every silhouette during motion — multiplied N toward zero every frame, pinning edge bands at
    // N~3 where the warmup ramp injects ~25% raw per frame: the aliasing/jitter riding exactly on the
    // debug view's green edges under motion. Only CONFIDENT rejection collapses the evidence now (still a
    // hard reset at full strength — the 1.5 "soft seed" variant regressed and stays dead); gentle
    // corrections leave the counter alone (their blend contribution already handles them).
    newN = lerp(newN, 0.0, smoothstep(0.35, 0.85, ghostReject));
    newN = lerp(newN, min(newN, 6.0), smoothstep(0.3, 0.8, depthReject));
    newN = lerp(newN, min(newN, 8.0), reactive);

    float lumaC = curr.x;                   // Y in YCoCg (current center sample)

    // --- Variance clamp: FIXED tight gamma, exactly as the original (pre-R2) algorithm. Every attempt this
    //     session to let confidently-accumulated pixels relax this box further (normal-buffer reject, hard
    //     depth-reset removal, confidence-scaled relaxation, edge-gated relaxation) reintroduced ghosting the
    //     original never had — a per-pixel luma-difference test isn't a fine enough instrument to tell "this
    //     pixel is genuinely stable" from "this pixel is a ghost that happens to be similarly bright", so a
    //     widened box kept occasionally admitting and then preserving stale/misaligned history. Going back to
    //     a constant box removes that failure mode entirely; the real wins this session (fp16 precision, the
    //     depth-RANGE disocclusion test, ghost-side rejection, velocity-disparity reactive, R2 jitter, no
    //     hard-reset edge-pinning, MSAA-off under TAA) are all independent of this and still apply. ---
    // 1.5 is the PRE-R2 baseline width (the build the user validated as stable). The R2 commit tightened it
    // to 1.3 on the theory that depth rejection would catch ghosting instead — but depth rejection is now
    // motion-gated at rest, so the tighter box only re-clipped converged high-frequency history for no
    // anti-ghost benefit. Restoring the baseline width is not trust-widening beyond baseline; it IS baseline.
    // RESOLUTION-SCALED clamp width (2026-07-05, TAALab user finding — THE low-scale distinctness lever):
    // at upscale the box statistics come from render-res taps, so converged OUTPUT-res detail is diluted
    // in its own box and a fixed 1.5-sigma width re-clips it every frame — the fizzly/indistinct low-res
    // look. Lab-validated at 0.33x: gamma up to 3.0 "smartly decides between samples", producing a clean
    // SUPERSAMPLED look with the modern reject machinery (honest disocclusion, ghost tests, Kalman
    // collapse, ring signal, motion trust cap) policing ghosts instead of the tight box. Base 1.5 at
    // native (validated, unchanged) -> 2x base = 3.0 at ratio 3 (0.33x). The historical "every widening
    // ghosted" law predates ALL of that machinery; the lock's further widening stays multiplicative on
    // top. Live-tunable base via TuneGamma (TAATuning).
    // EVIDENCE-SCALED widening (2026-07-05 retune, same day): the flat ratio-widening traded a SLIGHT
    // edge/high-frequency ghost at low res — a 3-sigma box on high-VARIANCE content is huge in absolute
    // terms, so trail colors the 1.5-sigma box used to clip slid through. Scale the widening down with
    // SUSPICION (the union of every contamination detector, incl. full-weight foreign here — parallax
    // edges are exactly where the wide box leaks): clean converged content (no evidence) keeps the full
    // supersampled box; anything carrying trail/disocclusion/ring/disparity evidence collapses toward
    // the validated 1.5 baseline exactly where ghosts live. The lab win (foliage distinctness at rest)
    // is evidence-silent -> unaffected.
    float suspGamma = max(suspicion, foreign); // foreign at FULL weight for the box (0.35-demoted in suspicion)
    // + MOTION DECAY (2026-07-05, third notch — the last high-frequency/leaf trace): osc-proven clutter
    // (foliage) has its rejects deliberately authority-muted (phase churn), so it is evidence-silent BY
    // DESIGN and suspicion can't protect it — the wide box held a faint trail there that nothing else
    // can reach. Narrow the widening partially while the pixel is IN MOTION (when trails are painted;
    // 0.6 strength keeps some in-motion distinctness), full width returns at rest where the supersampled
    // foliage win lives.
    // MOTION MEMORY (fourth notch — "could the high gamma be contributing?"): keying the decay on
    // CURRENT moveGate alone left two windows where the 3.0 box held residue: the instant motion STOPS
    // (box springs back to full width while the trail is still in history — the wide box then preserves
    // it at rest, leaving only slow diff decay) and sub-gate creep. Use remembered motion too
    // (storedMove, the meta-velocity trick that fixed the same one-frame hole in the ghost test): a
    // just-stopped pixel keeps the narrowed box one extra frame — long enough for the scrub to finish —
    // and genuine rest (no motion this frame or last) keeps the full supersampled width.
    float gammaMotion = max(moveGate, storedMove);
    float GAMMA = TuneGamma * lerp(1.0, 2.0,
        saturate((upscaleRatio - 1.0) * 0.5) * (1.0 - suspGamma) * (1.0 - TuneGammaMotionDecay * gammaMotion));
    // FSR2-style "LOCK" via the oscillation signal (fine-geometry stability, matters most under TAAU): the
    // clamp box is built from RENDER-res taps, but the converged history holds OUTPUT-res detail — a thin
    // line that is sub-pixel at render res is DILUTED in the box statistics, so the box hugs the diluted
    // mean and the clamp erodes the converged sharp feature every frame (the dim/flicker cycling on fine
    // geometry). On pixels with PROVEN sign-alternation (a ghost is monotonic — it cannot earn this), ~zero
    // velocity, and no disocclusion signals, widen the box so the locked history passes through intact.
    // Ghost-safe by the exact argument that admitted the oscillation trust gate; every gate that breaks a
    // lock in FSR2 (motion, disocclusion, velocity disparity) breaks it here too.
    // 0.8..2.0 (was 0.25..0.5 — locks died at HALF A PIXEL of motion): converged locks now survive
    // sub-pixel drift and slow pans, reprojecting through the movement instead of dumping to raw (the
    // "fully discards history on slight motion" report). Real movement still breaks locks; ghosts still
    // can't hold them (monotonic + evidence wipe). SUSPICION-SCALED velocity: coherent motion (no
    // evidence flags) counts at reduced speed for lock purposes — locks ride through clean pans (left/
    // right pans previously stripped all locks by raw speed alone); any flagged pixel counts at full speed.
    // Suspicion floor 0.4 -> 0.25 (flicker pass, 2026-07-05): at 0.4 the effective knee sat at ~2.0-5.0
    // raw px/frame — INSIDE the canonical 2-4 px/f "clean walking sim" band (see moveGate's comment), so
    // ordinary walking/pans were already suppressing locks and re-fizzling converged fine geometry
    // (foliage/fences) — the "flicker during motion" half of the full-vs-lite tradeoff. 0.25 moves the
    // knee to ~3.2-8.0 px/f, clearing the walk band. Ghost-safe: stillGate feeds oscLock only, which
    // still requires proven sign-alternation AND zero depth/ghost/reactive/foreign/feat rejects; any
    // evidence flag restores full-speed lock breaking. Revert signature: ghost-timing changes behind a
    // mover crossing locked ground (would indicate an unexpected coupling — revert immediately).
    float stillGate = 1.0 - smoothstep(0.8, 2.0, velPx * lerp(TuneStillGateFloor, 1.0, suspicion));
    // Lock threshold 0.32 (was 0.4): at low render scales the oscillation evidence builds unevenly (real
    // samples land on a given output pixel only on some phases), so fine geometry hovered under the lock
    // forever — the residual low-scale fizzle. TV-static-like content (~0.5 osc equilibrium) gains a bit
    // more partial trust as the cost; still clamp-bounded.
    // INTENSE-UPSCALE EASING (floorScale: 1 native -> 0 at <= 0.5x): under heavy TAAU an output pixel gets
    // a real sample only ~1 frame in 1/scale^2 (one in nine at 0.33x), so lock evidence accumulates that
    // much slower — distant fine detail (tree canopies) hovered below the threshold forever. Ease the
    // entry edge to 0.24 there; native keeps 0.32.
    float oscLock = smoothstep(lerp(0.24, 0.32, floorScale), 0.7, osc) * stillGate
                  * (1.0 - depthReject) * (1.0 - ghostReject) * (1.0 - reactive) * (1.0 - foreign)
                  * (1.0 - featReject) * (1.0 - noVel);
    // Locked widening scales with upscale INTENSITY past 2x (0.33x: up to ~3.9 sigma; <= 0.5x unchanged):
    // at ratio 3 the box spans ~3 output pixels, a converged thin line is so diluted in its own statistics
    // that even 3 sigma clips it on some jitter phases — the residual position-wobble at the lowest scale.
    float gammaEff = GAMMA * (1.0 + oscLock * lerp(1.0, 1.6, saturate(upscaleRatio - 2.0)));
    // RECTIFY, DON'T REJECT (mid-evidence resolution — the escape from the ghost-vs-aliasing trade):
    // blend-side rejection only chooses between keeping history (ghosts) and injecting raw (aliased
    // edges). TIGHTENING THE CLAMP on reject evidence is the third option: the stale color is forcibly
    // snapped toward the current neighborhood statistics — a FILTERED, anti-aliased value centred on m1 —
    // so mid-strength rejects both scrub the ghost (within ~2 frames) and stay smooth. Raw injection
    // below is reserved for near-certain rejection only.
    float rejTighten = smoothstep(0.12, 0.6, max(depthReject, ghostReject));
    gammaEff *= lerp(1.0, 0.3, rejTighten);
    // MOTION-SCALED CLAMP TIGHTENING, upscale-only (2026-07-05 — the SELF-REVEAL ghost): rotating
    // geometry (the far side of a head turning into view) reveals surface with valid SAME-OBJECT depth,
    // coherent velocity, and often similar colors — every structural detector (depth range, ghost test,
    // disparity, foreign, ringContam) is structurally silent, so only the variance clamp scrubs the
    // stale color, and at 1.5-sigma with a similar-color neighborhood that takes many frames. Under
    // motion the history's sub-pixel detail advantage is smaller (it's being resampled every frame
    // anyway), so a moderate tighten costs little AA while snapping self-reveal residue to the current
    // neighborhood statistics within a frame or two. TIGHTEN-ONLY (the ghost-safe direction — the
    // widening direction is the one with the revert graveyard); locks are unaffected at rest (moveGate
    // 0); upscale-gated so native keeps the validated fixed box.
    // Revert signature: moving surfaces at TAAU losing their converged look entirely (over-tightened —
    // raise 0.72 toward 0.85) or motion AA crunch on clean pans.
    // 0.72 is the validated strength ("definitely better"); a 0.60 push was tried and REVERTED same day
    // — it crushed history detail under motion and read as ALIASING, without touching the remaining
    // interior-texture residue (that's a TRUST-DEPTH problem — see the motion trust cap below, its
    // correctly-shaped lever). Do not re-push this below ~0.7.
    gammaEff *= lerp(1.0, TuneMotionClampTighten, moveGate * 0.8 * smoothstep(1.0, 1.5, upscaleRatio));
    float3 cmin = m1 - gammaEff * sigma;
    float3 cmax = m1 + gammaEff * sigma;
    // INPUT-RESOLUTION RECTIFICATION under TAAU (UE TSR mechanism — the last rest-state flicker fix): the
    // box statistics come from bilinear taps whose mixture changes with jitter phase, so clamping the
    // NATIVE-res history re-clips converged output-res detail slightly differently every frame on pixels
    // that can't fully lock (leaf clutter's quasi-random alternation never earns osc >= 0.7). Split the
    // history into its render-texel AVERAGE (phase-stable low component, ~one render texel of bilinear
    // taps) + the sub-pixel detail riding on top; clamp only the LOW component and apply that correction
    // to the full history. A ghost is wrong in its LOW component -> still fully corrected; converged
    // sub-pixel detail is zero-mean around the low component -> passes untouched, no phase dependence.
    // A wide safety clip (2x the box) bounds the detail component in the worst case. 1:1 keeps the
    // classic direct clamp (box and history live at the same resolution there — no domain mismatch).
    float3 history;
    float lumaHCmp; // history luma FOR THE DIFF COMPARISON — resolution-matched to m1 (see below)
#if SM4 // ps_3_0 temp-register budget (CI X4505 on OGL) — SM3 always takes the direct-clamp else path
    if (upscaleRatio > 1.5)
    {
        // hLow fetched with the rejection-authority block above (same 4-tap render-texel average).
        float3 hLowC = ClipAABB(cmin, cmax, hLow);
        // MOTION-FADED SPLIT RECTIFICATION (2026-07-05, ported from TAALite — the last reprojection
        // ghost during motion): the split clamps only the LOW component and lets sub-pixel detail ride
        // inside a loose 2x-gammaEff safety hull — correct at REST (a ghost is wrong in its low
        // component; converged detail is zero-mean around it), but under MOTION a stale trail's own
        // STRUCTURE rides the protected detail component through the hull, immune to every trust lever
        // (it is never clipped at all). TAALite clamps the FULL history into the 1x box — which is
        // exactly why lite's moving edges never carried this residue. Blend to lite's direct full clamp
        // by moveGate: rest keeps the detail-preserving rectification bit-exactly (the thin-line
        // stillness it was built for), motion gets the hard scrub.
        float3 rectified = ClipAABB(m1 - 2.0 * gammaEff * sigma, m1 + 2.0 * gammaEff * sigma, historyRaw + (hLowC - hLow));
        float3 directCl  = ClipAABB(cmin, cmax, historyRaw);
        // max(moveGate, storedMove): a just-stopped pixel keeps the direct clamp one extra frame so
        // the trail finishes scrubbing before the detail-protecting rectification returns (same
        // motion-memory reasoning as the gamma decay above).
        // PARTIAL (0.75, 2026-07-07): the FULL direct clamp under motion re-clips history to a
        // per-phase-changing box — at high-contrast (dark<->light) transitions the clamp target jumps
        // every frame and the clipped value churns: the dark-to-light fizzle. 75% direct still bounds
        // ghost structure to ~the box (trails die), while the remaining rectified share keeps the
        // clamp value phase-coherent. If ghost returns, raise toward 0.9; if contrast-edge fizzle
        // persists, drop toward 0.6 (and see the motion-faded Karis weighting at the final blend —
        // the other half of this same symptom).
        history = lerp(rectified, directCl, TuneDirectClampMix * max(moveGate, storedMove));
        // RESOLUTION-MATCHED DIFF (TSR: compare at INPUT resolution): m1 is a render-res mean but the
        // sharp history is output-res — for converged thin geometry they disagree FOREVER (the line is
        // diluted in m1), a permanent phantom "content change" that held every line pixel at a 5-10%
        // responsive blend and re-injected the alternating line estimate each covering frame — THE
        // thin-line jitter. Compare against the history's render-texel average instead: a converged
        // line's low component matches m1 (diff -> 0, deep trust holds, line goes still); a ghost or
        // real change is wrong in its LOW component too, so diff still fires.
        lumaHCmp = hLowC.x;
    }
    else
#endif
    {
        history = ClipAABB(cmin, cmax, historyRaw);
        lumaHCmp = history.x;
    }

    // --- Blend: the original content-adaptive luminance-feedback weight, unmodified — diff-driven, no
    //     counter-based deepening. Same reasoning as the clamp above: letting the accumulation counter push
    //     the blend weight past this ceiling was a second, independent freeze mechanism that reintroduced
    //     ghosting (and, being decoupled from ordinary motion, made anti-aliasing appear to stop under normal
    //     panning). The counter (newN) is kept for the ghost-side/reactive soft-caps above; it no longer
    //     drives the clamp or the blend. ---
    // Confidence check uses the NEIGHBOURHOOD MEAN (m1.x), not the single raw jittered `curr` sample, against
    // history. A single tap of high-frequency content (foliage, thin lines) flips between wildly different
    // values every frame BY DESIGN (that's the point of jittered supersampling) even once fully converged —
    // comparing that raw tap to the smoothed history reads as permanent "change" and pins the blend at the
    // responsive 60% floor forever, which is why foliage/thin-lines never settled even at rest. m1 already
    // averages out that per-sample sampling noise while still moving immediately on a REAL change (and
    // depthReject/ghostReject, the actual disocclusion catches, are luma-independent and unaffected). Only
    // this confidence signal changes — the actual displayed blend still uses the sharp `curr` sample below,
    // so this doesn't blur the image, it just stops mistaking supersampling noise for scene change.
    float lumaH = history.x; // display-side luma (Karis weights); the DIFF uses the resolution-matched lumaHCmp
    float diff = saturate(abs(m1.x - lumaHCmp) / max(0.2, max(m1.x, lumaHCmp)));
    // NOTE (sand at low scale): a "noise-floor knee" was tried here — subtracting an expected sampling-noise
    // baseline from diff at upscale so converged noisy textures (sand) stop resetting their own accumulation.
    // REVERTED in every variant (raw, reject-split, motion-gated, oscillation-earned): in the small-delta
    // regime a slight ghost residue over a textured surface is indistinguishable from texture sampling noise
    // by ANY resolve-side signal (magnitude, motion, even sign-alternation — the texture's noise rides on top
    // of the residue), so the knee always slowed ghost cleanup somewhere (haze around mover silhouettes).
    // Sand detail at low scale is an INPUT-side problem (terrain-noise mip bias), not a trust-side one.
    diff = max(max(diff, depthReject), max(max(ghostReject, featReject), ringContam));
    // KALMAN-COMPLETE DEEP END (the reference-upscaler convergence model): DLSS/FSR2/TSR converge fine
    // detail because their accumulation approaches an EQUAL-WEIGHT running average on static pixels —
    // each frame contributes ~1/N with N growing large — while a fixed EMA floor (1 - BlendFactor, i.e.
    // never below ~3% current) has a hard ~32-frame memory that can NEVER fully average a high-variance
    // jitter cycle: the "flickers and won't converge at rest" gap below 0.5x. The deep end now follows
    // the Kalman gain N/(N+1) once the EVIDENCE counter outgrows the EMA baseline (capped 0.985 off the
    // freeze asymptote). Counter-driven deepening historically ghosted — when N counted AGE. It now
    // counts witnessed agreement (sigma-normalized innovation, sign-alternation aware, witness-ruled),
    // collapses x0.75/frame on disagreement, and is reset/capped by every disocclusion path (ghost reset,
    // depth cap, reactive cap) — a stale pixel structurally cannot keep a large N. The diff term still
    // lerps toward full responsiveness instantly on top.
    float minN = min(prevN, newN);
    // KALMAN DEEP-END CAP = the CYCLE-HIDING WINDOW at upscale (was a flat 0.992 = 129-frame memory at every
    // scale). The design deliberately let evidence-trust EXCEED the lock ceiling ("raise-only" below), but at
    // high upscale that excess depth buys NOTHING visible — a converged pixel's cycle is already hidden once
    // the window reaches ~1.2x the Halton cycle (exactly what cycleCeil targets for the lock path) — while it
    // DOES preserve the SLIGHT sub-threshold reprojection residue behind motion: a faint ghost has small
    // innovation, which the Kalman counter reads as agreement (agreeK ~ 1, no collapse), so N maxes and the
    // 129-frame history holds the residue for ~2s AFTER all motion evidence is gone (nothing motion-gated can
    // reach that tail). Capping the deep end to the cycle window washes the residue out ~1.5x faster with no
    // loss on converged content (cycle still hidden; oscillation-LOCKED fine geometry still reaches this same
    // window via oscCeil). Fades in over ratio 1.2..1.8 so native / mild upscale keep the full 0.992.
    // cycleWindow mirrors the lock path's cycleCeil (JitterPhases-driven) — this ALIGNS the two deep paths.
    // Lever to push if ghosting persists: drop the 1.2 divisor toward 1.0 (window -> exactly one cycle).
    // Divisor 1.2 -> 1.0 (2026-07-05): the documented push lever ("drop toward 1.0 — window ->
    // exactly one cycle") — persistent similar-color ghost residue under TAAU is a MEMORY-DEPTH
    // problem (small-innovation residue reads as agreement, N maxes, deep history holds it); one
    // full Halton cycle is the shallowest window that still hides the converged limit cycle.
    // Failure signature if too shallow: faint repeating cycle shimmer on locked converged content.
    float cycleWindow = clamp(1.0 - 1.0 / (1.0 * JitterPhases), 0.965, 0.99);
    float deepCap = lerp(TuneDeepCapBase, cycleWindow, smoothstep(1.2, 1.8, upscaleRatio));
    float deepEnd = min(max(1.0 - BlendFactor, minN / (minN + 1.0)), deepCap);
    // Responsive end 0.68 (was 0.55 = 45% raw/frame -> fully raw in 2-3 frames on ANY luma mismatch —
    // "too quick to go fully raw on objects"): pre-structural-rejects, luma-diff carried disocclusion
    // duty and needed to be violent; now the rejects (depth/ghost/center/foreign/feature) own that, and
    // they enter this same lerp through the max() above at FULL strength — only pure-luma responsiveness
    // is gentled (~32%/frame, fully responsive in ~4 frames).
    // Responsive end 0.68 -> 0.60 (2026-07-05, "rejection not strong enough"): user A/B vs TAALite —
    // the full resolve's gentled luma responsiveness held ghost residue visibly longer than lite's raw
    // look. 0.60 scrubs a full-diff pixel ~40%/frame (fully responsive in ~3 frames) — between the
    // original violent 0.55 and the gentled 0.68. Revert signature: objects flashing raw on ordinary
    // luma changes (TV screens, lighting shifts).
    float historyWeight = lerp(deepEnd, TuneRespEnd, diff);
    // Velocity-disparity reactive caps the history trust directly (soft — 0.88 keeps a moving-content pixel
    // from pulsing aliased when the camera stops; tune toward 0.94 if a screen-wide stop-pulse shows).
    historyWeight = min(historyWeight, lerp(1.0, 0.85, reactive)); // 0.88 -> 0.85 (watch for a screen-wide pulse on camera stop; revert to 0.88 if seen)
    // Foreign-velocity trust cap — MILD only: the reprojection fix above already makes ring-pixel history
    // valid background (a hard 0.75 cap here just re-created raw jitter crunch on the ring). This is a
    // safety net for imperfect own-velocity (e.g. unwritten alpha fringes decoding as zero).
    historyWeight = min(historyWeight, lerp(1.0, 0.92, foreign));
    // Velocityless-overlay cap (see noVel above): headline icons / speech bubbles get an ~8-frame window
    // — fresh animation, no smear — instead of the deep trust that ghosted them under TAAU.
    historyWeight = min(historyWeight, lerp(1.0, 0.88, noVel));
    // MOTION TRUST CAP, upscale-only (2026-07-05 — the INTERIOR-TEXTURE ghost's correctly-shaped lever):
    // a coherently-moving surface (sim clothing/skin) accumulates sub-pixel reprojection error each frame
    // that no detector can see (innovation below the texture's own sigma, foreign 0 on mover interiors,
    // biasPenalty deliberately near-static-gated) — with the deep Kalman window the residue rides for
    // seconds as surface ghosting. Cap trust to ~a 10-frame window WHILE the pixel itself is moving at
    // upscale: the whole surface continuously refreshes (~10%/frame — gradual, and the display soften
    // covers the injected share, so this reads as refresh, not aliasing — unlike the clamp-crush variant
    // above, which was tried at 0.60 and re-aliased edges). At rest the cap releases and full convergence
    // resumes. Ghost-safe by direction (only ever reduces trust). Native untouched.
    // 0.90 -> 0.72 (2026-07-05 follow-up, user direction change): at 0.90 a continuously-moving
    // surface sat in a muddy equilibrium — the deform-error residue regenerates faster than a
    // 10%/frame refresh clears it, and mixed with the softened display it read "indistinct AND
    // ghosted". The user explicitly prefers moving content CRISP (raw or spatially-AA'd). 0.72 =
    // ~28% current per frame while moving: residue dies in 3-4 frames and the equilibrium mix is
    // current-dominant — paired with the crisp display below (soften suppressed under motion), the
    // moving state now reads as spatially-AA'd Lanczos reconstruction, converging deep at rest.
    // Revert signature: shimmer on moving surfaces beyond what FSR1 upscale shows (too low).
    historyWeight = min(historyWeight, lerp(1.0, TuneMotionTrustCap, moveGate * smoothstep(1.0, 1.5, upscaleRatio)));

    // --- OSCILLATION TRUST (anti-fizzle action). Every gate must pass: proven sign-alternation (a ghost
    //     fails osc), ~zero velocity (a mover fails stillGate), no disocclusion signal (a reveal fails the
    //     rejects), and low diff (essential — without it this lerp could RAISE trust on a changing pixel).
    //     Ceiling = half the effective blend factor (0.94->0.97 native, 0.97->0.985 at 0.5x), capped off the
    //     freeze asymptote. The 1.5-sigma variance clamp stays untouched — the hard bound under any failure.
    //     Known residual: TV/video textures equilibrate at osc~0.5 -> at most ~0.15 partial trust (slight
    //     smoothing, clamp-bounded); tuning lever = the smoothstep lower edge (0.4 -> 0.55 kills it). ---
    // SOFT diff gate: only LARGE diffs (a genuine content change) kill the trust — the hard (diff*3) gate
    // strangled thin geometry, whose render-res-diluted neighbourhood mean gives it a PERMANENT baseline
    // diff; but fully ungated trust let a little ghosting through on real changes. Knee 0.25 / slope 3.5
    // (was 0.35 / 2.5, which kept partial trust alive up to diff ~0.75 — genuine-change territory, where
    // the residual mover-ghost lived): trust now dies fully at diff ~0.54. The thin-geometry baseline diff
    // sits below ~0.2, so fine geometry keeps its lock. Ghost-safety backbone is still the oscillation
    // signal itself (monotonic ghosts decay the lock in ~5 frames).
    float oscTrust = oscLock * (1.0 - saturate((diff - 0.25) * 3.5));
    // EVIDENCE-SCALED, CYCLE-AWARE ceiling: the residual shimmer on a locked line IS its per-frame current
    // injection, and the visible "repeating jitter pattern" at extreme upscale is a CYCLE-VS-WINDOW
    // mismatch — the 72-frame Halton cycle at 1/3 scale exceeded the ~55-frame window a 0.982 ceiling
    // buys, so the converged limit cycle could never be averaged away (at 0.5x the 32-frame cycle fit
    // inside the window, which is why it was "barely visible" there). The deep end therefore scales with
    // the cycle it must hide: 1 - 1/(1.2*cycle), clamped to [0.965 (native baseline), 0.988]. Ghost-safety
    // gates unchanged: the deep end still needs osc >= 0.85 sustained alternation (a ghost cannot
    // alternate), stillness, and no reject/foreign/reactive; the Kalman collapse, honest disocclusion and
    // feature rectification all still override from below.
    // Clamp 0.985 / BlendFactor cap 0.5 (pulled back from 0.988 / 0.35: that extra depth read as a slight
    // ghost — the window lands a hair under the 72-frame cycle instead of over it, and Stage-2's input
    // consolidation covers the difference on the content that actually shows the pattern).
    float cycleCeil = clamp(1.0 - 1.0 / (1.2 * JitterPhases), 0.965, 0.99);
    // ENTRY ALIGNED WITH THE LOCK (low-scale only): oscLock engages at osc >= lerp(0.24,0.32,floorScale),
    // but this deep-window ceiling only began at 0.55 — so a PARTIALLY-locked pixel (osc 0.32..0.55, which
    // is exactly where distant canopy / leaf clutter equilibrates at 1/3 scale) got lock privileges
    // (floor/clamp bypass) yet only the shallow 0.965 ceiling = a ~28-frame window, far under the 72-frame
    // Halton cycle at 1/3 -> the residual repeating-jitter pattern on that content. Slide the lower edge
    // down to the lock band as render scale drops so a locked pixel's window is always >= the cycle it must
    // hide. NATIVE untouched (floorScale 1 -> edge 0.55, the TV/video partial-trust guard: at native the
    // 8-frame cycle fits any window, and content-changing screens are killed by diff, not this edge).
    float ceilLo = lerp(0.32, 0.55, floorScale); // 0.55 native -> 0.32 at <= 0.5x, matching the lock entry
    float oscCeil = min(1.0 - 0.5 * BlendFactor, lerp(0.965, cycleCeil, smoothstep(ceilLo, 0.85, osc)));
    // RAISE-ONLY: the Kalman deep end (N/(N+1), up to 0.992) can legitimately exceed the lock ceiling —
    // the lock lerp must never pull earned evidence-trust back DOWN.
    historyWeight = max(historyWeight, lerp(historyWeight, oscCeil, oscTrust));

    // EVIDENCE-GATED (was unconditional 0.22 by speed alone — raw insurance from before the structural
    // rejects existed): full boost only where something is actually suspicious (a reject, foreign
    // velocity, or velocity disparity). (suspicion hoisted above the osc detector.)
    // Clean-motion floor 0.35 -> 0.12 (flicker pass, 2026-07-05): on an evidence-silent pan the old
    // floor still injected ~7.7% raw per frame purely by speed — visible pan shimmer with no anti-ghost
    // payoff (real contamination drives suspicion -> 1, restoring the full 0.22). NOT zeroed: native has
    // no other anti-lag insurance (the sample-confidence motion regime below is upscale-gated).
    // Revert signature: soft-focus/lag trailing behind fast clean pans.
    float motionBoost = saturate(vmag * 20.0) * TuneMotionBoostMax * lerp(TuneMotionBoostFloor, 1.0, suspicion);
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

    // --- TAAU SAMPLE CONFIDENCE (upscale mode only — the standard temporal-upscaler mechanism). At render
    //     scale < 1, an output pixel's NEAREST real sample is sometimes dead-center and sometimes ~a full
    //     render texel away; on the far frames the reconstruction is pure interpolation, and blending it at
    //     full weight injects per-frame wobble (the residual TAAU flicker vs MSAA). Weight the current
    //     contribution by the nearest sample's kernel proximity: real-sample frames update strongly,
    //     in-between frames lean on the history that already integrated real samples from other jitter
    //     phases. At 1:1 every frame is a complete estimate, so this is OFF there (design-review verdict);
    //     under camera motion it's faded out (moveGate) so responsiveness/anti-ghosting are untouched. ---
    if (upscaleRatio > 1.001) // upscaleRatio/sampleConf hoisted above the Kalman counter
    {
        // Floor 0.14 (0.35 -> 0.25 -> 0.18 -> 0.14 as the kernel sharpened): with the output-sized kernel,
        // off-frames carry almost no real information for this pixel — injecting less of them disturbs
        // converged fine geometry less (the residual TAAU-only fizzle), and the motion gate still restores
        // full responsiveness the moment anything moves. Drops further toward 0.08 past 2x ratio: at 0.33x
        // EIGHT of nine frames are interpolation-only — that drip was the last visible thin-line jitter.
        // COVERAGE-SCALED (reference-upscaler behavior — a frame with no information contributes NOTHING):
        // on zero-coverage frames the floor previously injected the blurry bilinear fallback anyway, a
        // steady drip that was BOTH a blur and a flicker source (the fallback varies with jitter phase).
        // Pure history hold there; motion (moveGate) and the covering frames carry all responsiveness.
        float confFloor = lerp(TuneConfFloor, 0.08, saturate(upscaleRatio - 2.0)) * saturate(wsum / (0.3 * kscale));
        // LINEAR confidence curve (a squared curve was tried and REVERTED with the 192 deepening — the
        // combination starved converged pixels of correction and read as ghosting at 0.33x).
        // MOTION-SCALED, not motion-DISABLED (the "hard cutoff to raw on movement"): the motion end was a
        // binary switch to 100% unfiltered injection — the adversarial review identified this regime flip
        // as THE hard-raw cliff. Under motion, off-phase (information-free) frames now inject at 55% and
        // lean the rest on reprojected history, which keeps doing AA work while moving; dead-on samples
        // keep full weight, and every reject path still overrides from below. (An earlier x0.85 variant
        // regressed ghosting — but that was BEFORE the structural ghost fixes; the rejects now catch what
        // this retains.)
        // Regime keyed on SUSPICION-scaled motion: coherent pans (evidence silent) keep most of the
        // confidence-weighted accumulation — the AA rides through the pan; flagged pixels get the full
        // motion regime. This is what stops left/right pans from stripping to raw by speed alone.
        // TRUST-FADED off-phase throttle (2026-07-05 final round — "new detail resolves too slowly,
        // hazy while accumulating"): this multiplier protects CONVERGED history from information-free
        // off-phase injection — but it throttled REBUILDING pixels identically, so at 0.33x a fresh
        // pixel displayed mostly-old history on 8 of 9 frames and dripped the new content in at
        // confFloor: the indistinct/hazy accumulating phase, and the old-detail inertia. Same principle
        // as growK's prevN fade-in (a fresh pixel counts every frame — when you know nothing, every
        // sample is information): the throttle now fades IN with accumulated evidence (minN/12), so
        // young pixels inject their crisp reconstruction at full blend every frame (fast, clean
        // resolve-in) and converged pixels keep the validated off-phase protection bit-exactly.
        float confMul = lerp(lerp(confFloor, 1.0, sampleConf), lerp(0.55, 1.0, sampleConf), moveGate * lerp(0.3, 1.0, suspicion));
        blend *= lerp(1.0, confMul, saturate(minN / TuneConfFadeN));// 12->20 (2026-07-07 "slow to fill"): full-speed reconstruction injection persists deeper into accumulation before the converged-pixel off-phase throttle takes over
    }

    // WARMUP RAMP (counter-driven): with no accumulated history (fresh clear / off-screen reset) the
    // history buffer is BLACK, and blending any of it darkens the image. Seed from the current frame:
    // full current on the first frame, then 1/2, 1/3, ... — detail builds ON TOP of a correct base, and
    // the ramp is a no-op once it drops below the BlendFactor floor (~16 frames). KEYED OFF min(prevN,newN):
    // prevN is the evidence that actually EXISTS in the history (prevN=0 on frame one -> blend=1 -> output
    // IS the raw frame; using newN here was an off-by-one that blended 50% cleared-black into frame one —
    // the "starts darkened" bug), while newN keeps the ghost/reactive soft-caps' responsiveness boost (a
    // capped newN=2 still floors blend at 1/3 to scrub contaminated history). Ghost-safe BY DIRECTION:
    // max() can only push toward MORE current frame, never deepen history trust.
    // LOCK EXEMPTION: oscillation-locked pixels skip the evidence floor — the lock is the sanctioned deep
    // trust for converged fine geometry, and a lock structurally cannot coexist with cleared or invalid
    // history (its evidence resets with the meta), so the ramp has no legitimate job on a locked pixel.
    // Without this, a Kalman-collapsed N re-injected raw jitter OVER the lock's 0.965 trust on trees.
    blend = max(blend, (1.0 / (min(prevN, newN) + 1.0)) * (1.0 - oscLock));

    // HONEST DISOCCLUSION (reference-upscaler behavior — DLSS/FSR2 discard, not fade): a positively
    // identified disocclusion means the history is INVALID, and blending any of it is wrong by
    // construction. Full-strength reject = raw frame immediately + the counter reset above rebuilds
    // through the warmup ramp. Uniformly-SCALED softenings (0.7, then 0.85 with a history seed) were
    // BOTH user-rejected — retained history on a POSITIVELY-identified reveal reads as ghost mix. But
    // SHAPING is different from scaling: a confident reject still buys the full raw frame (the honest
    // reveal that killed the trailing ghost), while grazing partial rejects — constant along every moving
    // depth edge because the stored depth is dilated — stop injecting fractional raw every frame (the
    // post-ghost-fix edge aliasing). Placed after the warmup floor.
    // Knee 0.65..0.98: raw injection is now reserved for NEAR-CERTAIN rejection only — the whole
    // mid-evidence band is handled by clamp TIGHTENING instead (see rejTighten at the clamp: the stale
    // color snaps to the filtered neighborhood statistics — ghost scrubbed AND smooth, no raw needed).
    // Knee settled at 0.55..0.9 (2026-07-05/07 tuning arc): 0.65..0.98 (near-certainty-only) left
    // mid-evidence mover-edge trails to clamp-tightening alone = edge ghost; the 0.45..0.85 push then
    // over-rawed animated silhouettes once the direct motion clamp landed (which scrubs the same trails
    // itself, ghost-free). Middle ground: moderate-confidence rejects still buy reconstruction
    // injection, grazing partials lean on the (now hard) motion clamp — treated edges, no ghost, no raw.
    blend = max(blend, smoothstep(0.55, 0.9, max(depthReject, ghostReject)));

    // TEXTURE-DETAIL blend floor (pairs with the raw-sample input lean above): even with a raw input, the
    // CONVERGED value is the temporal mean over the jitter footprint, which wipes single-texel texture
    // detail (sand speckles) that edge-only spatial AA (FXAA/SMAA) never touches. On low-variance texture
    // regions, keep the blend responsive (~3-4 frame window) so the per-frame raw sample dominates — the
    // point-sampled "crunchy" texture look survives, at the cost of a small residual shimmer there.
    // ALL SCALES (was faded out by floorScale at <= 0.5x to let sand accumulate under TAAU — REVERTED):
    // this floor doubles as the anti-ghost backstop on low-variance surfaces. Removing it at low scale
    // gave ghost contamination ON those surfaces (exactly where movers walk) the full deep accumulation
    // window — user-identified as the persistent low-res mover ghosting. Sand churn at low scale is the
    // accepted cost until the INPUT-side fix (terrain-noise mip bias). Foliage/edges (high sigma): ~0.
    // LOCK BYPASS: pixels with a PROVEN oscillation lock escape the floor — semi-uniform fine detail
    // (distant tree canopy) can read low-variance at render res, and the floor re-churned it forever under
    // intense TAAU. Ghost-safe by the lock's own argument: ghost residue is monotonic, cannot earn the
    // lock, so the anti-ghost backstop stands exactly where it matters; the lock also dies on motion,
    // rejects, and foreign velocity, so a bypassed pixel reverts the moment anything real happens.
    blend = max(blend, texDetail * TuneTexDetailFloor * (1.0 - oscLock));

    // RAW-STATE SPATIAL SOFTENING (upscale only; ZERO ghost risk — current-frame data only, the FSR2
    // treatment of disoccluded/reactive pixels, which output the full FILTERED upsample rather than
    // point samples): when the floors/rejects legitimately force a pixel mostly-raw (reveals, motion),
    // display the smooth bilinear current estimate instead of the near-point reconstruction — honest
    // content with FSR1-smooth edges instead of sharply-upscaled jaggies (the "non anti-aliased mover
    // edges"). Converged pixels (low blend) keep the crisp reconstruction bit-exactly; the lever CANNOT
    // re-ghost because it never touches history or trust.
    float3 dispCurr = curr;
#if SM4 // ps_3_0 temp-register budget (CI X4505 on OGL) — SM3 displays the sharp reconstruction as-is
    if (upscaleRatio > 1.001)
    {
        // Onset 0.12 / slope 2.2 (2026-07-05). MOTION-SUPPRESSED (same day, final user direction:
        // "moving content should be raw or well spatially anti-aliased, not indistinct"): the Mitchell
        // soften stays for STATIC raw states (reveals at rest, warmup, low-coverage phases — where
        // per-phase speckle was the complaint), but under COHERENT MOTION the display now stays on the
        // crisp Lanczos+hull reconstruction — which IS the spatial AA (jittered 3x3 kernel, deringed),
        // pairing with the motion trust cap above (~28% current/frame while moving) so the moving state
        // reads sharp-and-refreshing rather than soft-and-stale. Motion masks residual per-phase
        // variance the way it masks film grain.
        float rawSoften = saturate((blend - TuneRawSoftenOnset) * TuneRawSoftenSlope) * (1.0 - moveGate * TuneRawSoftenMotionSup);
        // Soften target = the render-texel-scale Mitchell reconstruction (was bilinear cboxC): a real
        // smooth upscale — edge-coherent, no cross-texel mush — which is the reference response for
        // legitimately-revealed content (rotation/pan disocclusion is GENUINE every frame at speed; the
        // detection was verified correct via the diagnostic split — display quality was the problem).
        // Full-strength lerp: the soft reconstruction is sharper than bilinear was at 0.7.
        dispCurr = lerp(curr, filtSoft / max(wsumSoft, 1e-4), rawSoften);
    }
#endif

    // Anti-flicker (Karis): inverse-luma weighting so bright sub-pixel samples don't dominate/sparkle.
    // MOTION-FADED (2026-07-07 — the dark-to-light ghost fizzle): this weighting is structurally
    // DARK-BIASED — history weight scales by 1/(1+lumaH), so DARK stale history gets BOOSTED exactly
    // where the current content is bright: on dark->light reveals during motion the ghost was
    // over-weighted, scrubbed, re-boosted — fizzle from dark into light regions. The sparkle it
    // suppresses is a REST-state artifact (converged sub-pixel glints); under motion fade to a plain
    // energy-honest lerp so a bright reveal displaces dark history at its true blend weight.
    float lumaFade = max(moveGate, storedMove) * TuneKarisFade;
    float wc = blend * lerp(1.0 / (1.0 + max(lumaC, 0.0)), 1.0, lumaFade);
    float wh = (1.0 - blend) * lerp(1.0 / (1.0 + max(lumaH, 0.0)), 1.0, lumaFade);
    float3 blended = (dispCurr * wc + history * wh) / max(wc + wh, 1e-5);

    float3 outYCoCg = reprojectable ? blended : dispCurr;

    // Sentinel 2.0 ("no velocity anywhere in the 3x3") must NOT survive into an fp16 history alpha: next
    // frame it would read as outside every valid depth range and paint a permanent depthReject ring around
    // all unwritten-velocity content. (The old RGBA8 storage clamped it to 1.0 implicitly; fp16 would not.)
    float depthForHistory = min(closestDepth, 1.0);
    // saturate: YCoCg->RGB can slightly under/overshoot [0,1]. The old RGBA8 history clamped this
    // implicitly on store; fp16 does NOT, and unclamped values would compound through the feedback loop.
    o.color = float4(saturate(YCoCg_to_RGB(outYCoCg)), depthForHistory);

    if (debugMeta)
    {
        // Diagnostic encode for the debug blit. CRITICAL: R must stay the ACCUMULATION COUNTER even in
        // debug — the next frame's resolve decodes meta.R as prevN in BOTH techniques, and a previous
        // debug encode that wrote "effective trust" into R corrupted the feedback loop (fresh pixels wrote
        // R=0 -> read prevN=0 -> warmup pinned blend=1 -> R=0 forever: newly revealed areas never regained
        // trust WHILE DEBUGGING, which read as a scary real bug). Layout: R = counter (feedback-correct),
        // G = reject strength (depth or ghost), B = EFFECTIVE HISTORY TRUST this frame (1 - blend: the
        // honest "is the blend actually deep here" signal — bright blue = converged), A = 0 (no stale
        // oscillation trust on toggle-off).
        // Shipping debug encode. INSTRUMENT LAW learned the hard way (a buggy fallback in a diagnostic
        // variant painted giant false "phantoms" that cost hours of chasing): any diagnostic that
        // substitutes a fallback value for missing data (e.g. unwritten-velocity centers) MUST mask
        // those pixels out instead — a diagnostic may show nothing, never a fabrication.
        o.meta = float4(newN / MaxAccum, max(depthReject, ghostReject), 1.0 - blend, 0.0);
    }
    else
    {
        // Shipping encode: R = N, GB = this frame's dilated velocity for next frame's disparity reactive,
        // A = packed oscillation state (sign bit + 7-bit EMA; 0 on non-reprojectable = evidence reset).
        o.meta = float4(newN / MaxAccum,
                        velocity.x * 10.0 + 0.5,
                        velocity.y * 10.0 + 0.5,
                        packedA);
    }
    return o;
}

TAAOut TAA_PS(VSOut input)      { return TAA_Core(input, false); }
TAAOut TAA_DebugPS(VSOut input) { return TAA_Core(input, true); }

// ============================================================================================
// TAALite — the user-selectable "Cosmic TAA Lite" tier: a lighter TAA + TAAU resolve for weaker
// GPUs, offered on BOTH backends via the AA dropdown (in addition to its history as the GL
// fallback below). ONE upscale-general path; native 1:1 is the degenerate case.
//
// WHY THIS EXISTS (historically): the full TAA_Core above (Cosmic TAA / Cosmic TAAU) had a persistent
// reprojection "warble" on this project's GL path (ps_3_0 via MojoShader) that extensive
// investigation could not root-cause. Velocity and packed-depth buffers were confirmed correct
// on GL; jitter-sign, MRT-binding, and comparison-mis-evaluation hypotheses were all tried and
// refuted. What WAS confirmed during that investigation is a MojoShader ps_3_0 compiler bug
// class: certain uniform-value comparisons (`if (someUniform > 0.5)`-style branches) silently
// always took one branch (fixed in VelocityViz.fx's debug view by replacing such a branch with
// lerp/saturate arithmetic instead of an `if`). TAA_Core's "SM3" path is NOT free of this risk —
// the `#if SM4` gates above exist ONLY for ps_3_0 TEMP-REGISTER BUDGET, not for comparison
// correctness, so its scalar-ALU logic with many uniform-keyed `if`s still runs on ps_3_0/GL.
//
// THE BRANCH-FREE LAW (non-negotiable in this body): NO `if`/ternary keyed on uniform- or
// texture-derived values. Everything data-dependent is lerp/saturate/smoothstep/step arithmetic,
// which sidesteps the whole suspected MojoShader bug class BY CONSTRUCTION. The only allowed
// exceptions: the loop-local `if (d < closestDepth)` nearest-wins reduction inside the
// [unroll]ed 3x3 (an ordinary per-tap compare, not a uniform-threshold compare) and the
// [unroll] loop structure itself. Even the final out-of-bounds select is a step()/lerp().
//
// UPSCALE-GENERAL FORM: upscaleRatio = InvColorSize.x / InvScreenSize.x — 1.0 at native, > 1
// under Cosmic TAAU (history/output native, color/velocity render-res). All upscale-specific
// terms fade out ARITHMETICALLY at ratio 1 (kernel scaling degenerates, sample confidence hits
// exactly 1 — see the wC note below), never via `if`. At native this is therefore the same
// algorithm, though NOT byte-identical to the previous TAALite: it gains the jitter-relative
// Mitchell reconstruction, the meta accumulation counter, packed depth in history alpha, and
// depth disocclusion. Backbone kept from the validated lite: YCoCg space, 1.5-sigma variance
// AABB + ClipAABB, dilated-velocity reprojection with JitterDelta, Catmull-Rom history fetch,
// luma-feedback diff, motion boost, Karis inverse-luma anti-flicker.
//
// DELIBERATELY EXCLUDED vs TAA_Core (register budget + the branch law + their tuning histories
// are full of reverts/regressions that would have to be re-fought on GL): oscillation
// anti-fizzle locks, Kalman evidence conditioning, ring-contamination + feature-level (gradient)
// rejection, anisotropic/clutter-adaptive kernels, split (own-velocity) reprojection, the
// velocityless-overlay class handling, firefly/texture-detail/soft-display shaping. The four
// TAAU essentials it DOES keep: jitter-relative Mitchell reconstruction on the output grid,
// center-sample confidence, meta counter + warmup, depth-in-alpha range disocclusion.
//
// The old closestMask `reprojectable` gate is REMOVED (review-panel-approved): it permanently
// raw-jittered unwritten-velocity content (overlays/backdrops). Unwritten velocity decodes as
// zero = identity reproject — correct for static content; actual motion is caught by the
// variance clamp + luma feedback (same reasoning as TAA_Core's reprojection comment).
//
// Compiles identically for ps_3_0 and ps_4_0 (no internal #if SM4 forks); C# (TAAResolve.cs)
// selects it on any backend when the user picks "Cosmic TAA Lite" (TAAResolve.LiteMode).
// ============================================================================================
TAAOut TAALite_PS(VSOut input)
{
    TAAOut o;
    float2 uv = input.Coord;

    float2 colSize = 1.0 / InvColorSize; // INPUT color texels (reconstruction/box/velocity taps)
    // outputRes / renderRes: 1 at native, > 1 under TAAU. All ratio-driven terms are arithmetic.
    float upscaleRatio = InvColorSize.x / InvScreenSize.x;
    float kscale = upscaleRatio; // output-sized Mitchell kernel (the TAAU sharpness mechanism)

    // JITTER-RELATIVE MITCHELL RECONSTRUCTION base (TAA_Core's TAAU-general formulation): the
    // output pixel center in input-pixel coords vs this frame's jittered sample positions.
    // buffer[p] holds content that un-jittered belongs at p + SampleJitterUV, so the nearest
    // sample's texel is floor(oPx - sPx); fracd = its offset from the output center (input px).
    // (The 4-step warble bisect of 2026-07 exonerated this jitter-relative term, the counter
    // deep end, and the warmup ramp — the culprit was the aliased point-sampler depth fetch;
    // see the POINT-EMULATED comment below.)
    float2 oPx = uv * colSize;
    float2 sPx = SampleJitterUV * colSize;
    float2 baseTexel = floor(oPx - sPx);
    float2 fracd = (baseTexel + 0.5 + sPx) - oPx;
    // Separable weights, distances scaled by kscale so the kernel is OUTPUT-pixel sized. At
    // native (kscale 1, zero jitter) these degenerate to the fixed 0.8889/0.0556 constants.
    float3 kx3 = float3(MitchellK(abs(fracd.x - 1.0) * kscale), MitchellK(abs(fracd.x) * kscale), MitchellK(abs(fracd.x + 1.0) * kscale));
    float3 ky3 = float3(MitchellK(abs(fracd.y - 1.0) * kscale), MitchellK(abs(fracd.y) * kscale), MitchellK(abs(fracd.y + 1.0) * kscale));

    // VARIANCE BOX — 5-tap plus pattern at the content-stationary boxUV (uv - SampleJitterUV,
    // bilinear does the shift): the clamp box stays spatially stationary under jitter (validated
    // fizzle fix — a wobbling box re-clips converged history every frame). Pure arithmetic.
    float2 boxUV = uv - SampleJitterUV;
    float3 cboxC = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV, 0, 0)).rgb);
    float3 cboxW = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxE = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxN = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 cboxS = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 m1 = (cboxC + cboxW + cboxE + cboxN + cboxS) * (1.0 / 5.0);
    float3 m2 = (cboxC * cboxC + cboxW * cboxW + cboxE * cboxE + cboxN * cboxN + cboxS * cboxS) * (1.0 / 5.0);
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0));
    // 1.5-sigma box at native, RESOLUTION-SCALED to 3.0 at ratio 3 (matches TAA_Core — lab-validated
    // 2026-07-05: the fixed width re-clipped render-res-diluted converged detail at upscale; wider box =
    // the clean supersampled look). Branch-free ramp; native bit-exact at ratio 1. Live-tunable
    // (LiteTune promotion 2026-07-07).
    float GAMMA = LiteGamma * lerp(1.0, LiteGammaScale, saturate((upscaleRatio - 1.0) * 0.5));
    float3 cmin = m1 - GAMMA * sigma;
    float3 cmax = m1 + GAMMA * sigma;

    // 3x3 loop: RECONSTRUCTION at raw texel centers around the nearest jittered sample (all 9
    // taps), plus VELOCITY DILATION + valid-depth RANGE on the 5-tap plus pattern at the
    // jitter-stable boxUV (per TAA_Core's vCen rationale: raw-uv velocity taps read per-phase
    // different fragments on sub-pixel geometry and churn the depth tests). The corner-tap
    // exclusion is a compile-time literal test — folds under [unroll], no runtime branch.
    float3 filt = 0;
    float wsum = 0;
    float3 crawC = 0; // nearest raw jittered sample (center recon tap) — thin-coverage fallback
    float wC = 0;     // center tap's actual kernel weight — sample confidence below
    float2 dilatedVel = float2(0, 0);
    float closestDepth = 1e9; // smaller = nearer; init far beyond any valid depth
    float dmin = 1e9, dmax = -1e9; // valid-tap depth range for the disocclusion test
    float anyValid = 0.0;          // any velocity written in the plus pattern (arithmetic mask)
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        if (dx == 0 || dy == 0) // compile-time: plus pattern only
        {
            float4 v = tex2Dlod(velocitySampler, float4(boxUV + float2(dx, dy) * InvColorSize, 0, 0));
            float validTap = step(0.5, v.a);
            // Unwritten velocity pushed to sentinel depth 2.0 (beyond valid [0,1]) so a
            // genuinely-far valid pixel still wins the nearest-depth tiebreak. lerp, not ternary.
            float d = lerp(2.0, v.b, validTap);
            if (d < closestDepth) { closestDepth = d; dilatedVel = v.rg; } // allowed reduction
            dmin = min(dmin, lerp(1e9, v.b, validTap));
            dmax = max(dmax, lerp(-1e9, v.b, validTap));
            anyValid = max(anyValid, validTap);
        }
        // Reconstruction tap: RAW texel center (bilinear at an exact center = point fetch),
        // weighted by its true distance to the output pixel center via the separable kernel.
        float2 tapUV = (baseTexel + float2(dx, dy) + 0.5) * InvColorSize;
        float3 craw = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(tapUV, 0, 0)).rgb);
        float w = kx3[dx + 1] * ky3[dy + 1];
        filt += craw * w;
        wsum += w;
        if (dx == 0 && dy == 0) { crawC = craw; wC = w; } // folds under [unroll]
    }

    // Thin-coverage fallback: with the output-sized kernel some frames leave a pixel with almost
    // no in-support sample. Smoothly fall back to the nearest RAW texel (single-surface by
    // construction — crisp under motion, and sample confidence keeps such frames history-leaning).
    float3 curr = lerp(crawC, filt / max(wsum, 1e-4), saturate(wsum / (0.15 * kscale)));

    // SAMPLE CONFIDENCE: how much this frame's nearest sample actually covers this output pixel.
    // 1.2656 = 1 / MitchellK(0)^2 = 81/64, so at native (kscale 1, zero jitter: wC = 0.7901)
    // sampleConf saturates at exactly 1.0 and the blend multiplier below is exactly 1 — the
    // native path is unaffected by this term by construction.
    float sampleConf = saturate(wC * 1.2656);

    // Reproject with the dilated velocity; JitterDelta cancels the jitter baked into the velocity
    // buffer for an exact reprojection. Unwritten velocity = zero = identity reproject (correct
    // for static overlays/backdrops — the old closestMask gate is gone, see header).
    float2 velocity = dilatedVel;
    float2 histUV = uv - velocity + JitterDelta;
    float inBounds = step(0.0, histUV.x) * step(histUV.x, 1.0) * step(0.0, histUV.y) * step(histUV.y, 1.0);
    float velPx = length(velocity / InvScreenSize) * VelGatePxScale; // NATIVE px
    float moveGate = smoothstep(LiteMoveGateLo, LiteMoveGateHi, velPx);

    // Bicubic (Catmull-Rom) history fetch for detail preservation + a POINT tap for the packed
    // depth in alpha (LINEAR would mix two surfaces' depths at every silhouette — see sampler
    // comment) and the meta counter.
    float3 history = RGB_to_YCoCg(SampleHistoryBicubic(histUV));
    history = ClipAABB(cmin, cmax, history);
    // POINT-EMULATED depth fetch (THE GL warble root cause, bisect-convicted here first — see the
    // full writeup at the top of the file where historyDepthSampler used to be declared).
    float historyDepth = FetchHistoryPoint(histUV).a;
    float prevN = tex2Dlod(metaHistorySampler, float4(histUV, 0, 0)).r * MaxAccum;

    // DEPTH RANGE DISOCCLUSION (TAA_Core's test, radically simplified, branch-free): history
    // depth outside the current 3x3 valid-depth range = the surface that wrote it left. Range
    // (not point) compare so static edges — where jitter flips the nearest-depth winner — never
    // self-reject. MOTION-GATED (a genuine disocclusion requires motion; at rest any mismatch is
    // sampling noise — sub-pixel foliage would otherwise permanently reject) and masked by
    // anyValid (no valid taps -> no evidence; dmin/dmax sentinels never fire through the mask).
    float outside = max(max(dmin - historyDepth, historyDepth - dmax), 0.0);
    float depthReject = saturate(outside / max(historyDepth, DepthRejectParams.w) * DepthRejectParams.y - DepthRejectParams.z)
                        * moveGate * anyValid;

    // META ACCUMULATION COUNTER: N grows on agreement, collapses on rejection, zeroes off-screen.
    float newN = min(prevN + 1.0, MaxAccum) * inBounds;
    newN = lerp(newN, 0.0, depthReject);
    float minN = min(prevN, newN);

    // Luminance feedback: stable pixels keep deep accumulation; changed pixels drop toward
    // current. diff also inherits the depth evidence.
    float lumaC = curr.x;
    float lumaH = history.x;
    float diff = saturate(abs(lumaC - lumaH) / max(0.2, max(lumaC, lumaH)));
    diff = max(diff, depthReject);
    // Counter-driven deep end replaces the flat floor: proven-stable pixels earn N/(N+1) trust
    // (capped), never below the baseline 1-BlendFactor. Live-tunable (LiteTune 2026-07-07).
    float deepEnd = min(max(1.0 - BlendFactor, minN / (minN + 1.0)), LiteDeepCap);
    float historyWeight = lerp(deepEnd, LiteRespEnd, diff);

    // Motion-adaptive: lean more on current when moving fast (less lag/ghosting under motion).
    // This speed-proportional boost IS the "raw motion resolve" character (the user's Switch-2-
    // DLSS-lite anchor) — tune to taste, not to zero.
    float motionBoost = saturate(length(velocity) * 20.0) * LiteMotionBoost;
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

    // Sample-confidence injection gate: frames whose kernel barely covers this pixel inject
    // little (their estimate is an amplified tail) — except under motion, where responsiveness
    // wins. Exactly 1 at native (see sampleConf note).
    blend *= lerp(lerp(LiteConfFloor, 1.0, sampleConf), 1.0, moveGate);
    // Honest disocclusion: strong depth evidence forces the current frame through regardless of
    // accumulated trust.
    blend = max(blend, smoothstep(LiteHonestLo, LiteHonestHi, depthReject));
    // Warmup: a young history (small N) cannot claim deep trust yet.
    blend = max(blend, 1.0 / (minN + 1.0));

    // Anti-flicker (Karis) inverse-luma weighting so bright sub-pixel samples don't dominate.
    float wc = blend * (1.0 / (1.0 + max(lumaC, 0.0)));
    float wh = (1.0 - blend) * (1.0 / (1.0 + max(lumaH, 0.0)));
    float3 blended = (curr * wc + history * wh) / max(wc + wh, 1e-5);

    // Out-of-bounds reproject -> current frame. Arithmetic select (branch-free law).
    float3 outYCoCg = lerp(curr, blended, inBounds);

    // History alpha carries this pixel's dilated depth (unwritten sentinel clamps to 1.0) for
    // next frame's disocclusion test. Meta GB = the zero-velocity encode (v*10+0.5) so nothing
    // downstream misdecodes; A unused here (no oscillation state in the lite path).
    o.color = float4(saturate(YCoCg_to_RGB(outYCoCg)), min(closestDepth, 1.0));
    o.meta  = float4(newN / MaxAccum, 0.5, 0.5, 0.0);
    return o;
}

technique TAALite
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 TAALite_PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 TAALite_PS();
#endif
    }
}

technique TAA
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 TAA_PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 TAA_PS();
#endif
    }
}

technique TAADebug
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 TAA_DebugPS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 TAA_DebugPS();
#endif
    }
}
