// TAA.fx — temporal anti-aliasing for OpenSO 3D mode.
//
// Reads:
//   colorTex       — this frame's rendered color (post-scale-resolve, pre-blur).
//   historyTex     — previous frame's TAA output (RGB) + packed dilated depth (A), velocity-reprojected.
//   metaHistoryTex — previous frame's meta: R = accumulation count N (N/MaxAccum), GB = previous dilated
//                    velocity (v*10+0.5), A = packed oscillation state (sign 1 + osc 4 + amp 3 bits).
//   velocityTex    — per-pixel screen-space velocity (.rg) + normalized linear depth (.b) + valid mask (.a).
//
// Outputs to TWO render targets:
//   COLOR0 — displayed frame + next frame's history (RGB) with this pixel's dilated depth packed in A
//            (fp16 target when available — see PPXDepthEngine.HistoryIsFP16 / DepthRejectParams).
//   COLOR1 — next frame's meta: R = new N, GB = dilated velocity encode, A = oscillation pack (TAADebug
//            technique repurposes GB for diagnostics and disables their consumers).
//
// Structure of the resolve:
//   1. Velocity dilation: reproject with the nearest-depth motion vector in a 3x3 neighbourhood.
//   2. Jitter-free reprojection: histUV = uv - velocity + JitterDelta.
//   3. Catmull-Rom (bicubic) history fetch — preserves detail across reprojection.
//   4. YCoCg variance clamp re-anchors history to the current frame.
//   5. Depth-disocclusion rejection (range + ghost-side tests), motion-gated.
//   6. Blend = content-adaptive luminance-feedback weight + evidence-conditioned accumulation counter.
//   7. Anti-flicker inverse-luma weighting on the final blend (LDR pipeline; no tonemap step).

float2 InvScreenSize; // 1 / OUTPUT (history) resolution — the grid TAA resolves on
// 1 / INPUT color resolution. Equal to InvScreenSize normally; SMALLER-res (larger texels) under Cosmic
// TAAU, where this pass IS the upscaler: history/meta native, color/velocity render-res. The
// reconstruction below is written in input-pixel coordinates throughout; 1:1 is the degenerate case.
float2 InvColorSize;
float  BlendFactor;   // baseline deep-history floor (current weight ~= BlendFactor on a stable pixel).
float  MaxAccum;      // cap on the accumulation counter N. Matches TAAResolve.MAX_ACCUM.
// Per-frame jitter delta (UV). Velocity is computed from the jittered projection; adding this back when
// reprojecting history gives an exact (jitter-free) reproject.
float2 JitterDelta;
// Uniform camera-zoom jacobian (per-axis 1 - prevScale/currScale at the camera target, from
// WorldState.PrepareCulling; 0 when the camera isn't zooming). The reprojection velocity is fetched from
// the texel fracd away from the output pixel center; under zoom the velocity field has that constant
// gradient, so histUV += fracd * InvColorSize * ZoomJacobian recovers the pixel center's own previous
// position — exact for uniform screen zoom, the C#-side first step of TSR's reprojection field.
float2 ZoomJacobian;
// Depth-disocclusion tuning, set from C# by the ACTUALLY-ALLOCATED history format (fp16 vs RGBA8 fallback):
//   x = ghost dead-zone epsilon (storage quantization must never fire the ghost test by itself)
//   y = depthReject slope   z = depthReject offset   w = relative-compare denominator floor
float4 DepthRejectParams;
// This frame's jitter as a UV offset: the colour buffer was rendered with the projection translated by the
// jitter, so buffer[uv] holds content that UN-jittered belongs at uv + SampleJitterUV. Sampling the
// variance-box taps at uv - SampleJitterUV reads the un-jittered neighbourhood: the clamp box is spatially
// STATIONARY for static content (a wobbling box re-clips converged history every frame). The centre "curr"
// sample stays at the jittered uv: that offset IS the new sub-pixel information.
float2 SampleJitterUV;
// Motion-gate pixel scale: the velocity gates think in RESOLVE-GRID pixels, but in pre-upscale (FSR1) mode
// that grid is render-res. This rescales gate-space to NATIVE pixels: 1/renderScale in pre-upscale mode,
// 1 everywhere else (TAAU/native grids are already native).
float  VelGatePxScale;
// Jitter cycle length (frames), from R2Jitter.HaltonCycle: 8 native, 32 at 0.5x, 72 at 1/3. The locked
// accumulation window must EXCEED this for the converged limit cycle to be invisible.
float  JitterPhases;

// ---- LIVE-TUNING UNIFORMS (TAA_Core only — TAALite has its own set below). Single source of truth for
// the defaults is FSO.LotView.Utils.TAATuning, uploaded every frame by TAAResolve.Draw; the initializers
// here are only a fallback for drivers that don't set them. Do NOT retune here; retune in TAATuning. ----
float TuneMotionBoostFloor = 0.12;   // motionBoost suspicion floor (clean-motion raw drip share)
float TuneMotionBoostMax = 0.22;     // motionBoost peak scale (evidence-flagged motion raw boost)
float TuneStillGateFloor = 0.25;     // stillGate suspicion velocity scale floor (lock survival on clean pans)
float TuneMoveGateLo = 0.6;          // moveGate smoothstep lower edge (native px/frame)
float TuneMoveGateHi = 2.0;          // moveGate smoothstep upper edge (native px/frame)
float TuneRespEnd = 0.60;            // responsive end of the diff-driven blend lerp (full-diff history weight)
float TuneMotionTrustCap = 0.65;     // motion trust cap at upscale (interior-texture ghost lever)
float TuneMotionClampTighten = 0.72; // motion-scaled variance-clamp tighten at upscale (self-reveal lever)
float TuneRawSoftenOnset = 0.12;     // raw-state display soften: blend onset
float TuneRawSoftenSlope = 2.2;      // raw-state display soften: slope past onset
float TuneRawSoftenMotionSup = 0.85; // raw-state display soften: suppression under coherent motion
float TuneGamma = 1.5;               // variance clamp base width (sigma) — TAA_Core's GAMMA
float TuneTexDetailFloor = 0.28;     // texture-detail blend floor / low-variance anti-ghost backstop
float TuneConfFloor = 0.14;          // TAAU sample-confidence floor (the <=2x-ratio endpoint)
float TuneRingLo = 0.03;             // ringContam own-vs-dilated color knee, lower edge
float TuneRingHi = 0.10;             // ringContam own-vs-dilated color knee, upper edge
float TuneDirectClampMix = 0.75;     // motion direct-clamp share vs phase-coherent rectification
float TuneKarisFade = 1.0;           // scales the Karis anti-flicker motion fade (1 = full fade to plain lerp under motion)
float TuneGammaMotionDecay = 0.6;    // wide-box narrowing strength while in motion
float TuneConfFadeN = 20.0;          // evidence depth (minN) at which the off-phase confidence throttle is fully armed
float TuneGrowOffPhase = 0.3;        // off-phase growth discount floor for the evidence counter
float TuneDeepCapBase = 0.992;       // Kalman deep-end cap at native/mild upscale
float TuneThinLineEps = 0.02;        // thin-line depth-ridge relative step (TSR ErrorMultiplier analogue)

// ---- TAALite tunables (only consumed by TAALite_PS; TAATuning.cs is the C# single source). ----
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
// POINT history fetches (packed DEPTH in alpha + structure taps) are EMULATED through the LINEAR sampler
// by snapping the UV to the exact texel center — see FetchHistoryPoint. LAW: never alias one texture with
// two differently-filtered sampler_states in this engine — on OpenGL filter state is a property of the
// texture object, so one state silently wins for both samplers. Depth must never be bilinearly
// interpolated: at an edge, LINEAR mixes the two surfaces' depths into a value belonging to neither.

texture metaHistoryTex;
sampler metaHistorySampler = sampler_state {
    texture = <metaHistoryTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT; // counts must not cross-fade between texels
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
// k(0)=8/9=0.8889, k(0.5)=0.5347, k(1)=1/18=0.0556. The tiny negative lobe is clamped to 0 by the max():
// this keeps the 3x3 reconstruction a convex combination — no ringing overshoot on bright sub-pixel sparkle.
float MitchellK(float x)
{
    float x2 = x * x;
    float x3 = x2 * x;
    float inner = (7.0 * x3 - 12.0 * x2 + 16.0 / 3.0) / 6.0;                     // |x| < 1
    float outer = ((-7.0 / 3.0) * x3 + 12.0 * x2 - 20.0 * x + 32.0 / 3.0) / 6.0; // 1 <= |x| < 2
    return max((x < 1.0) ? inner : outer, 0.0);
}

#if SM4
// Lanczos2 polynomial approximation (FSR2's reconstruction kernel). Peak k(0)=1, zeros at x=1 and x=2,
// negative lobe ~-0.13 near x~1.4 — the passband distinctness the lobe-clamped Mitchell gives up. Ringing
// from the lobes is bounded at the use site by clamping the reconstructed value to the 3x3 tap hull.
// SM4/upscale-only: native and SM3/GL keep Mitchell (register budget; no hull clamp on SM3 to bound lobes).
// Cap x2 at 4 = kernel support edge.
float LanczosK(float x)
{
    float x2 = min(x * x, 4.0);
    float a = 0.4 * x2 - 1.0;
    float b = 0.25 * x2 - 1.0;
    return (1.5625 * a * a - 0.5625) * (b * b);
}
#endif

// Catmull-Rom (bicubic) history sampling — preserves high frequencies across reprojection so the jittered
// samples build a sharp supersampled image (plain bilinear would low-pass every frame).
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

    // Full 9-tap Catmull-Rom (a 5-tap diet renormalized the corner weight onto the axis taps and low-passed
    // diagonal detail slightly on every reprojection).
#if SM4
    // DERINGING HULL CLAMP: Catmull-Rom's negative lobes (w0/w3) overshoot around high-contrast content —
    // halo ringing on fine bright detail, re-rung by every resample and amplified by RCAS. Overshoot is
    // definitionally outside the local tap hull; clamping to the 9-tap min/max removes the ring exactly
    // without softening the reconstruction. ALU-only. SM4-ONLY: naming all 9 taps overflows ps_3_0's 32
    // temp registers; SM3 keeps the accumulate-and-globally-clamp form.
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

// POINT-emulated history fetch through the LINEAR sampler (see the sampler-aliasing law above): snap to
// the exact texel center, where bilinear degenerates to a point fetch on every backend.
float4 FetchHistoryPoint(float2 uv)
{
    float2 t = (floor(uv / InvScreenSize) + 0.5) * InvScreenSize;
    return tex2Dlod(historySampler, float4(t, 0, 0));
}

struct TAAOut
{
    float4 color : COLOR0; // displayed frame + next frame's history (RGB), dilated depth in A
    // Meta layout (RGBA8): R = new accumulation count N (N/MaxAccum), GB = this frame's dilated velocity
    // encoded v*10+0.5 (saturates at +/-0.05 UV on store — writers clamp at +/-0.5, so beyond that the
    // stored value pins and the velocity-disparity reactive fires during ultra-fast motion: desirable),
    // A = packed oscillation state (sign 1 + osc 4 + amp 3 bits; 0 on non-reprojectable / meta clear).
    // The TAADebug technique repurposes GB+A for diagnostics and disables their consuming logic.
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
    //  * VARIANCE BOX (m1/m2): taps at the un-jittered position (boxUV, bilinear does the shift) so the
    //    clamp box stays content-stationary under jitter. Statistics tolerate the bilinear low-pass.
    //  * FILTERED INPUT (filt/wsum): jitter-relative reconstruction. Taps at RAW texel centers, weighted
    //    by the kernel evaluated at each tap's distance to the un-jittered pixel center — each frame
    //    contributes genuinely new sub-pixel information so the converged history super-resolves.
    //    Sign derivation (pinned): buffer[p] holds content that un-jittered belongs at p + SampleJitterUV,
    //    so the tap at uv + ofs sits at displacement (ofs + SampleJitterUV) from the pixel center. At zero
    //    jitter the weights degenerate to the fixed Mitchell constants.
    //  * VELOCITY DILATION + depth range: jitter-compensated taps (boxUV) — the velocity buffer is
    //    rasterized jittered, so raw-uv taps read per-phase-different fragments on sub-pixel geometry
    //    and the depth tests would churn with the jitter.
    float2 texSize = 1.0 / InvScreenSize; // OUTPUT pixels (velocity gates, reactive thresholds)
    float2 colSize = 1.0 / InvColorSize;  // INPUT color pixels (reconstruction, box, velocity taps)
    float2 boxUV = uv - SampleJitterUV;
    // Nearest-jittered-sample reconstruction base (TAAU-general; 1:1 is the degenerate case). In INPUT-pixel
    // coordinates: the output pixel center sits at oPx; this frame's samples sit at texel centers + the
    // content shift sPx. The nearest sample's texel is floor(oPx - sPx) — a -0.5 variant biases sample
    // confidence low for ~half of output pixels.
    float2 oPx = uv * colSize;
    float2 sPx = SampleJitterUV * colSize;
    float2 baseTexel = floor(oPx - sPx);
    float2 fracd = (baseTexel + 0.5 + sPx) - oPx; // nearest sample's offset from the output center (input px)
    // OUTPUT-SIZED RECONSTRUCTION KERNEL: distances are scaled by the upscale ratio so the kernel is sized
    // for the OUTPUT pixel, not the input texel — over the Halton cycle every output pixel accumulates true
    // output-resolution detail. kscale = 1 at native (bit-identical weights). UNCLAMPED: the render-scale
    // floor of 1/3 bounds it at 3. A tight kernel means many zero-coverage frames, which is safe only
    // because zero-information frames inject nothing (coverage-scaled confidence floor in the blend section).
    float upscaleRatio = InvColorSize.x / InvScreenSize.x; // outputRes / renderRes, > 1 under TAAU
    float kscale = upscaleRatio;
    // VARIANCE BOX (5-tap plus pattern, hoisted out of the loop so the edge direction below is available
    // to the reconstruction weights). Fetched at the content-stationary boxUV; the center tap doubles as
    // the thin-coverage fallback sample.
    float3 cboxC = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV, 0, 0)).rgb);
    float3 cboxW = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxE = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxN = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 cboxS = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 m1 = (cboxC + cboxW + cboxE + cboxN + cboxS) * (1.0 / 5.0);
    float3 m2 = (cboxC * cboxC + cboxW * cboxW + cboxE * cboxE + cboxN * cboxN + cboxS * cboxS) * (1.0 / 5.0);
    // EDGE-DIRECTIONAL (ANISOTROPIC) RECONSTRUCTION: on a strong luma edge, stretch the kernel ALONG the
    // edge (distances along the tangent count half) so thin geometry gathers several real samples per frame
    // along its own length. Central-difference gradient from the stationary box taps (no extra fetches);
    // upscale-gated so the native kernel is untouched, faded in with edge strength.
    float2 grad = float2(cboxE.x - cboxW.x, cboxS.x - cboxN.x);
    float gmag = length(grad);
    float edgeAniso = smoothstep(0.15, 0.5, gmag) * saturate(kscale - 1.0);
    float2 en = grad / max(gmag, 1e-5);   // across-edge unit direction
    float2 et = float2(-en.y, en.x);      // along-edge unit direction
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0)); // neighborhood stddev (clamp box + clutter test below)
    // CLUTTER-ADAPTIVE KERNEL WIDTH: foliage is isotropic sub-pixel clutter — high variance, no coherent
    // gradient direction — and at high ratio the output-sized kernel is too tight to bridge neighbouring
    // fragments (fizzle instead of consolidation). Widen toward 0.78x on directionless high-variance
    // neighbourhoods at extreme upscale; lines (dirCoherence -> 1) keep the sharp anisotropic kernel; flat
    // regions and ratios <= 1.5 are bit-exact unchanged.
    float dirCoherence = smoothstep(0.15, 0.5, gmag);
    float clutter = smoothstep(0.10, 0.25, sigma.x) * (1.0 - dirCoherence);
    float kscaleEff = kscale * lerp(1.0, 0.78, clutter * saturate(upscaleRatio - 1.5));
    // Tap k in {-1,0,1} sits at distance fracd + k.
    // KERNEL SELECT: at upscale the reconstruction kernel is the FSR2 Lanczos2 approximation (negative
    // lobes = passband distinctness; ringing bounded by the 3x3 tap-hull clamp at the reconstruction
    // site). Native keeps Mitchell (anti-sparkle convexity matters more than passband there). SM3/GL keeps
    // Mitchell everywhere (register budget; no hull clamp there to bound lobes).
#if SM4
    float useLan = step(1.001, upscaleRatio); // branch-free select; SM4/DX only — MojoShader never sees it
    #define RECONK(x) lerp(MitchellK(x), LanczosK(x), useLan)
#else
    #define RECONK(x) MitchellK(x)
#endif
    float3 kx3 = float3(RECONK(abs(fracd.x - 1.0) * kscaleEff), RECONK(abs(fracd.x) * kscaleEff), RECONK(abs(fracd.x + 1.0) * kscaleEff));
    float3 ky3 = float3(RECONK(abs(fracd.y - 1.0) * kscaleEff), RECONK(abs(fracd.y) * kscaleEff), RECONK(abs(fracd.y + 1.0) * kscaleEff));
    // Render-texel-scale weights (kscale 1) for the SOFT display reconstruction (see loop).
    // SM4-ONLY: the soft display path and several other register-heavy features below overflow ps_3_0's
    // 32 temp registers (CI X4505 on OGL); SM3/OGL runs the lean resolve.
#if SM4
    float3 kx1 = float3(MitchellK(abs(fracd.x - 1.0)), MitchellK(abs(fracd.x)), MitchellK(abs(fracd.x + 1.0)));
    float3 ky1 = float3(MitchellK(abs(fracd.y - 1.0)), MitchellK(abs(fracd.y)), MitchellK(abs(fracd.y + 1.0)));
    float3 filtSoft = 0;
    float wsumSoft = 0;
    // 3x3 tap hull for the Lanczos dering clamp: the negative lobes may only sharpen WITHIN the local data
    // range, never overshoot past it. Taps are already fetched — ALU only.
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
    float4 plusD = float4(2.0, 2.0, 2.0, 2.0); // W,E,N,S plus-tap depths (unwritten = far sentinel) — thin-line prior
    // Center velocity tap PRE-FETCHED (the loop re-reads it as its (0,0) plus tap — cached, ~free): the
    // pixel's OWN velocity feeds the foreign-velocity signal, and its depth anchors the depth-aware
    // reconstruction weights. Jitter-compensated (boxUV) like the loop's velocity taps.
    float4 vCen = tex2Dlod(velocitySampler, float4(boxUV, 0, 0));
    float2 centerVel = vCen.rg; // unwritten decodes as zero
    float centerDepth = (vCen.a >= 0.5) ? vCen.b : -1.0; // -1 = no depth anchor (weighting disabled)
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float2 ofs = float2(dx, dy) * InvColorSize; // neighborhood spans INPUT texels
        // Velocity/depth tap at all 9 positions: the corners' depth feeds the depth-aware reconstruction
        // weights; the dilation/range statistics stay on the 5-tap plus pattern (corner contribution to
        // dilation is marginal — the [unroll]'d literal test compiles the corner taps out of it).
        float4 v = tex2Dlod(velocitySampler, float4(boxUV + ofs, 0, 0));
        if (dx == 0 || dy == 0)
        {
            // "No velocity written" -> depth sentinel 2.0 (beyond valid [0,1]) so genuinely-far valid
            // pixels still win the nearest-depth tiebreak over unwritten neighbours.
            float d = (v.a >= 0.5) ? v.b : 2.0;
            if (d < closestDepth) { closestDepth = d; dilatedVel = v.rg; closestMask = v.a; }
            if (v.a >= 0.5) { dmin = min(dmin, v.b); dmax = max(dmax, v.b); }
            // Capture the four side depths for the thin-line test (compile-time folds under [unroll]).
            if (dx == -1) plusD.x = d; else if (dx == 1) plusD.y = d;
            else if (dy == -1) plusD.z = d; else if (dy == 1) plusD.w = d;
        }
        // Reconstruction tap: RAW texel center (bilinear at an exact center = point fetch) around the
        // nearest jittered sample, weighted by its true distance to the output pixel center. Weight =
        // separable kernel blended toward the edge-elongated radial kernel by edgeAniso.
        float2 tapUV = (baseTexel + float2(dx, dy) + 0.5) * InvColorSize;
        float3 craw = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(tapUV, 0, 0)).rgb);
        // SOFT reconstruction (render-texel-scale Mitchell, no depth/aniso weighting): the display path
        // for legitimately-rejected pixels — a proper smooth upscale of the current frame instead of
        // near-raw. Same taps, ALU only.
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
        // DEPTH-AWARE KERNEL WEIGHT (upscale-only): unweighted, the 9 taps mix foreground and background
        // at silhouettes and each jitter phase mixes them differently — per-phase shimmer at thin-geometry
        // edges. Weight each tap by depth similarity to the pixel's OWN surface (the center tap). Unwritten
        // taps count as same-surface; the 0.15 floor keeps enough cross-depth coverage for the filtered
        // reconstruction (an aggressive floor collapses wsum at interior clothing/limb edges).
        if (upscaleRatio > 1.001 && centerDepth >= 0.0)
        {
            float dt = (v.a >= 0.5) ? v.b : centerDepth;
            w *= max(1.0 - saturate(abs(dt - centerDepth) / max(centerDepth, 0.02) * 5.0), 0.15);
        }
        filt += craw * w;
        wsum += w;
        if (dx == 0 && dy == 0) { crawC = craw; wC = w; } // folds under [unroll]
    }
    // DEPTH THIN-LINE PRIOR (TSR DetectThinGeometry's line test): a 1-px depth ridge — the center strictly
    // NEARER than BOTH opposite neighbours by a relative epsilon — is same-frame STRUCTURAL proof of thin
    // geometry, which a color ghost cannot fake. Uses the already-fetched plus-pattern depths (0 fetches,
    // 0 state). Unwritten taps keep the far sentinel: a thin object over velocity-less backdrop still
    // registers, while a solid region's edge fails the both-sides requirement. An unwritten CENTER decodes
    // as the far sentinel too, so the test is structurally 0 there. Consumers: oscLock entry ease +
    // biasPenalty exemption — acceleration only, every lock kill-gate stays in force.
    float thinDC = (vCen.a >= 0.5) ? vCen.b : 2.0;
    float thinEps = TuneThinLineEps * max(thinDC, DepthRejectParams.w); // relative step, floored denominator
    float thinLine = max(step(thinEps, plusD.x - thinDC) * step(thinEps, plusD.y - thinDC),
                         step(thinEps, plusD.z - thinDC) * step(thinEps, plusD.w - thinDC));
    // Thin-coverage fallback: with the output-sized kernel, some frames leave an output pixel with almost
    // no in-support sample (wsum ~ 0). Divide-guard + smooth fallback to the NEAREST RAW TEXEL (crawC,
    // point sample) — single-surface by construction. A bilinear fallback would mix the mover's color a
    // full render texel across every edge: contamination in the current frame itself, unreachable by any
    // history-side rejection. Threshold is kscale-aware: at high kscale a frame catching only the tail of
    // one sample yields filt/wsum with tiny wsum — one distant sample amplified.
    float3 stationaryC = crawC;
#if SM4
    // Negative-lobe wsum safety: with real Lanczos lobes, wsum can cancel toward zero on adversarial
    // jitter phases. The saturate() fallback factor returns 0 for wsum <= 0 (pure crawC — no real
    // coverage), the 1e-3 epsilon bounds the divide, and the hull clamp bounds the value absolutely.
    // At native useLan = 0: all-positive Mitchell weights, hull clamp is a no-op by construction.
    float3 recon = clamp(filt / max(wsum, 1e-3), reconHullMin, reconHullMax);
    float3 curr = lerp(stationaryC, recon, saturate(wsum / (0.15 * kscale)));
#else
    float3 curr = lerp(stationaryC, filt / max(wsum, 1e-4), saturate(wsum / (0.15 * kscale)));
#endif

    // TEXTURE-DETAIL PRESERVATION: TAA area-averages everywhere — unlike MSAA, which leaves texture
    // interiors alone — so fine texture detail converges to a mip-like blur. On LOW-VARIANCE
    // neighbourhoods lean the input toward the raw nearest sample so single-pixel texture energy survives
    // accumulation. NATIVE-ONLY (floorScale): at low render scales the nearest raw sample can sit far from
    // the output pixel (the lean would be a per-frame reconstruction error) and the mip-bias path supplies
    // texture detail there instead. High-variance content keeps the full reconstruction.
    float floorScale = saturate(BlendFactor / 0.03 - 1.0); // 1 at native, 0 at <= 0.5x render scale
    float texDetail = 1.0 - saturate(sigma.x * 12.0);
    curr = lerp(curr, crawC, texDetail * 0.75 * floorScale);

    // FIREFLY SUPPRESSION at upscale (input-side, zero ghost risk): a single bright or dark sub-pixel
    // sample otherwise strobes its output pixel once per jitter cycle. Bound the incoming estimate's luma
    // symmetrically against the neighborhood's own 2-sigma range. Native untouched.
    if (upscaleRatio > 1.5)
    {
        curr.x = clamp(curr.x, m1.x - 2.0 * sigma.x - 0.02, m1.x + 2.0 * sigma.x + 0.02);
        // SPECKLE CONSOLIDATION on directionless clutter: sub-render-pixel fragments exist-or-don't per
        // jitter phase and their quasi-random alternation never earns the full lock, so every covering
        // frame re-injects a different fragment pattern (dithering). Lean curr toward the stationary
        // neighborhood mean by clutter strength: injection variance drops BEFORE the blend, current-frame
        // data only. Lines are protected by construction (dirCoherence -> clutter = 0 on directed edges);
        // flat regions have ~zero sigma so the lean is a no-op.
        curr = lerp(curr, m1, 0.28 * clutter * saturate(upscaleRatio - 1.5));
    }

    // Reproject with the dilated velocity (+ jitter delta cancels the jitter baked into the velocity buffer).
    // NO velocity-validity gate: the buffer is un-jittered, so "velocity never written" decodes as zero
    // velocity = identity reproject — correct for static content (2D/backdrop art, alpha fringes that skip
    // the velocity MRT). Content that moves without writing velocity is caught by the variance clamp +
    // luma feedback instead.
    float2 velocity = dilatedVel;
    float velPx = length(velocity * texSize) * VelGatePxScale; // NATIVE px (see VelGatePxScale)
    // Slight motion (sub-pixel drift, slow pans) keeps its accumulation and reprojects through the
    // movement; walking-speed motion (~2-4 px/f) arms everything fully.
    float moveGate = smoothstep(TuneMoveGateLo, TuneMoveGateHi, velPx);
    // FOREIGN-VELOCITY SIGNAL: the nearest-depth dilation gives the ring of background pixels around a
    // mover's silhouette the MOVER's velocity — their history reprojects from the wrong place while
    // passing every depth test. foreign measures the dilated-vs-own-velocity disagreement; it feeds
    // suspicion, a trust cap, and the lock exclusion. Camera pans share motion everywhere (foreign stays
    // ~0 screen-wide). Reprojection itself stays DILATED (the DLSS/FSR2 standard — switching ring pixels
    // to their own velocity makes adjacent pixels reproject differently during parallax pans and tears
    // the edge history); ring contamination is scrubbed by the later machinery instead.
    float velFgnPx = length((velocity - centerVel) * texSize) * VelGatePxScale;
    float foreign = smoothstep(0.75, 2.5, velFgnPx);
    float vmag = length(velocity);
    // SPLIT REPROJECTION: COLOR reprojects with the dilated velocity (edge coherence); the DEPTH/STRUCTURE
    // TESTS reproject with the pixel's OWN velocity, so a background pixel tests its stored depth at the
    // position its own surface actually occupied (the stored depth is the dilated fattened-foreground
    // silhouette moving at foreground speed — own-velocity anchoring is what keeps the tests honest).
    // MAGNITUDE-GATED own-velocity color reprojection: inside a deforming character, limbs hand interior
    // pixels a neighboring bone's velocity — a small systematic reprojection error every frame. Small
    // disagreement -> the pixel's own velocity (exact reprojection); large disagreement (true silhouette
    // parallax) -> dilated. The 1.5..3.0 native-px knee sits above foreign's arm point. Invalid centers
    // (unwritten velocity) keep dilated.
    float2 histVel = lerp((centerDepth >= 0.0) ? centerVel : velocity, velocity, smoothstep(1.5, 3.0, velFgnPx));
    // Zoom-jacobian correction (see the ZoomJacobian uniform): the fetched velocity belongs to the texel
    // fracd away from this output pixel's center; under camera zoom that offset is a systematic sub-texel
    // reprojection error. Color reprojection only — the depth/structure tests are range-based.
    float2 histUV = uv - histVel + JitterDelta + fracd * InvColorSize * ZoomJacobian;
    float2 ownVel = (centerDepth >= 0.0) ? centerVel : velocity;
    float2 histUVDepth = uv - ownVel + JitterDelta;
    bool reprojectable = (histUV.x >= 0) && (histUV.x <= 1) && (histUV.y >= 0) && (histUV.y <= 1);

    // History fetch (bicubic for detail) + a POINT tap for the packed depth in alpha (see sampler comment).
    float4 historyPoint = FetchHistoryPoint(histUVDepth); // OWN-velocity anchor (split reprojection)
    float3 historyRaw = RGB_to_YCoCg(SampleHistoryBicubic(histUV));

    // RING-CONTAMINATION SIGNAL (the split-reprojection blind spot): at a moving silhouette the color
    // (dilated-anchored) and the structural tests (own-anchored) inspect DIFFERENT surfaces — a background
    // ring pixel can read foreground-trail color while its own-velocity tests certify "valid". Measure the
    // disagreement directly: own-anchored history color (historyPoint.rgb, already fetched) vs the
    // dilated-anchored color. Gated by foreign (zero on pans and mover interiors); the color-diff knee
    // keeps clean pans silent. Upscale-only. Feeds diff + suspicion like a reject.
    float ringContam = 0.0;
#if SM4 // ps_3_0 temp-register budget — SM3 runs without this signal
    if (!debugMeta && upscaleRatio > 1.001)
    {
        float3 histOwn = RGB_to_YCoCg(historyPoint.rgb);
        ringContam = foreign * smoothstep(TuneRingLo, TuneRingHi, length(histOwn - historyRaw));
    }
#endif

    // Reprojected previous meta: accumulation count N (R) + previous frame's dilated velocity (GB).
    float4 pm = tex2Dlod(metaHistorySampler, float4(histUV, 0, 0));
    float prevN = pm.r * MaxAccum;

    // META.A DECODE — byte-exact layout sign(1) + osc(4) + amp(3): bit 7 = the last real luma delta's
    // sign, bits 3-6 = oscillation EMA (16 levels), bits 0-2 = witnessed alternating amplitude
    // (8 levels spanning 0.35 luma). Mirrors the pack at the bottom of the resolve; RESOLVE_VERSION
    // guards the layout. debugMeta repurposes meta bytes and must decode as zero state.
    float metaByte = debugMeta ? 0.0 : floor(pm.a * 255.0 + 0.5);
    float prevSgn = step(128.0, metaByte);
    float metaRem = metaByte - prevSgn * 128.0;
    float prevOscQ = floor(metaRem * (1.0 / 8.0));
    float prevOsc = prevOscQ * (1.0 / 15.0);
    float prevAmp = (metaRem - prevOscQ * 8.0) * (1.0 / 7.0);

    // Depth disocclusion (relative — depth is normalized linear 0=near..1=far). historyPoint.a holds the
    // dilated depth visible at this texel last frame. Compare against the whole 3x3 valid depth RANGE, not
    // the single dilated depth: at a static edge the jitter flips which neighbour wins the nearest-depth
    // contest, so a point compare rejects every silhouette every frame; the range always contains the
    // history depth at a static edge, while a true disocclusion still lands outside it.
    // ALL depth rejection is MOTION-GATED (moveGate): a genuine disocclusion requires something to have
    // moved; at rest any depth mismatch is sampling noise (sub-pixel foliage flips which fragments exist
    // at all each frame and would permanently self-reject). moveGate is structurally 0 at rest
    // (un-jittered velocity; unwritten = zero). Content appearing WITHOUT motion (cutaway toggles,
    // build-mode placement) is caught by the variance clamp + luma responsiveness.
    // VELOCITYLESS-CONTENT CLASS: the whole 3x3 wrote no velocity — every motion-evidence system is
    // structurally silent. This class is the diegetic overlays (headline icons, speech bubbles — drawn
    // after the velocity MRT unbinds; the sky dome DOES write velocity). They animate with zero motion
    // signal: cap their window (~8 frames, below) and bar them from oscillation locks.
    float noVel = (dmax < dmin) ? 1.0 : 0.0;

    // REJECTION AUTHORITY (color-evidence proportionality): depth evidence proves the history is
    // geometrically stale; color evidence measures how wrong it LOOKS. A camera-parallax reveal is stale
    // by a sub-texel sliver and full-raw rejection costs more than the error; a mover trail is stale by
    // the mover's entire color -> full authority. Scaled at the source so every consumer (diff, honest
    // floor, counter resets, suspicion, evidence wipe) inherits proportionality. The history render-texel
    // average fetched here is reused by the input-resolution rectification below. 1:1 keeps full authority.
    float3 hLow = historyRaw;
    float rejAuth = 1.0;
    // Static-reveal authority: same floor, slightly steeper color curve (full authority by 0.06
    // instead of 0.08) — a static reveal's depth evidence lives one frame (see staticGhost), so
    // modest-but-visible color differences must earn their scrub in that frame.
    float staticAuth = 1.0;
#if SM4 // ps_3_0 temp-register budget — SM3 keeps full rejection authority, direct clamp
    if (upscaleRatio > 1.5)
    {
        float2 hOfs = InvColorSize * 0.25;
        hLow = (RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2( hOfs.x,  hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2( hOfs.x, -hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2(-hOfs.x,  hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2(-hOfs.x, -hOfs.y), 0, 0)).rgb)) * 0.25;
        // Geometric staleness always keeps >= 30% scrub authority (bounded lag, never indefinite).
        // OSC-AWARE FLOOR: on pixels carrying sign-alternation evidence (a signature a ghost cannot fake),
        // a COLOR-SILENT depth reject is jitter-phase churn, not disocclusion — drop the color-silent
        // floor to 0.05 there. RELATIVE-MOTION OVERRIDE: a trail remembers the mover's velocity differing
        // from the pixel's own, while canopy during a pan shares the camera's motion — relative motion
        // restores authority regardless of oscillation evidence (a slow trail over osc-proven ground must
        // still scrub).
        float relFgnPxE = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1 - centerVel) * texSize) * VelGatePxScale;
        float relMotionE = smoothstep(0.75, 2.5, max(velFgnPx, relFgnPxE));
        float authFloor = max(lerp(0.3, 0.05, smoothstep(0.25, 0.6, prevOsc)), relMotionE * 0.65);
        float colorEvidence = length(m1 - hLow);
        rejAuth = lerp(authFloor, 1.0, smoothstep(0.02, 0.08, colorEvidence));
        staticAuth = lerp(authFloor, 1.0, smoothstep(0.02, 0.06, colorEvidence));
    }
#endif

    float historyDepth = historyPoint.a;
    float outside = max(max(dmin - historyDepth, historyDepth - dmax), 0.0);
    float depthReject = (dmax < dmin) ? 0.0 :
        saturate((outside / max(historyDepth, DepthRejectParams.w)) * DepthRejectParams.y - DepthRejectParams.z);
    depthReject *= moveGate * rejAuth;

    // GHOST-SIDE REJECTION: fires only on the ghost side — history depth NEARER than every valid current
    // tap = the surface that wrote it has left (trailing edge of a mover). Dead-zone epsilon
    // (DepthRejectParams.x) keeps storage quantization alone from ever firing it.
    float nearer = max(dmin - historyDepth - DepthRejectParams.x, 0.0);
    float ghost = (dmax < dmin) ? 0.0 : saturate(nearer / max(historyDepth, DepthRejectParams.w) * 12.0);
    // CENTER-DEPTH GHOST TEST: the range test cannot fire while the mover is still inside the 3x3 (the
    // stale history depth sits exactly at dmin), leaving the strip right behind a walking mover exempt.
    // Ask instead: is the history nearer than the surface actually at THIS pixel now (the center tap)?
    // Softer than the range test (slope 8, weight 0.8): the stored history depth is DILATED, so this test
    // also brushes a band of clean pixels around every moving edge. RELATIVE-MOTION GATED: depth alone
    // cannot distinguish "the mover left" from "camera panning past a static edge" — a true trailing band
    // has the near content moving RELATIVE to the background (current foreign velocity, or the REMEMBERED
    // meta velocity vs the pixel's own); a static edge shares the camera's motion. Invalid center falls
    // back to the range test.
    float storedFgnPx = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1 - centerVel) * texSize) * VelGatePxScale;
    float relMotion = smoothstep(0.75, 2.5, max(velFgnPx, storedFgnPx));
    float nearerC = (centerDepth >= 0.0) ? max(centerDepth - historyDepth - DepthRejectParams.x, 0.0) : 0.0;
    ghost = max(ghost, saturate(nearerC / max(historyDepth, DepthRejectParams.w) * 8.0) * 0.8 * relMotion);
    // Gated by CURRENT motion OR REMEMBERED motion (the stored meta velocity): the instant a mover exits
    // this pixel's 3x3, dilated velocity drops to zero and current-only gating would close before the
    // ghost evidence could ever fire. A trailing-reveal pixel remembers the mover's velocity from last
    // frame in pm.gb; resting foliage remembers zero, so it cannot fake this signal.
    float storedMovePx = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1) * texSize) * VelGatePxScale;
    float storedMove = smoothstep(0.35, 1.5, storedMovePx);
    float ghostReject = max(moveGate, storedMove) * ghost * rejAuth;
    // STATIC-REVEAL GHOST PATH (motion-exempt): cutaways, wall changes, and build-mode edits swap
    // geometry with ZERO velocity, so the motion gates stay closed while the ghost-side depth evidence
    // is real — the history depth sits nearer than the ENTIRE current valid range. Admit that evidence
    // at rest, shielded exactly where the churn explanation lives instead of by motion: RANGE test only
    // (a static silhouette's range contains its history depth by construction), alternation-evidenced
    // pixels stay gated (resting foliage flips which fragments exist per jitter phase — prevOsc is the
    // shield), an unwritten center is barred (overlay/alpha-fringe false fires), and rejAuth keeps
    // color-silent depth changes gentle. Consumers (diff, counter reset, evidence wipe, clamp tighten,
    // honest-disocclusion knee) all inherit through ghostReject. STEEP response on purpose: the
    // evidence lives exactly ONE frame (this resolve rewrites the history depth alpha immediately,
    // reveal or not), so a confirmed fire must clear the honest knee and reset the counter in that
    // frame — a partial fire leaves residue with no evidence left to finish the job.
    float staticGhost = (dmax < dmin) ? 0.0 :
        smoothstep(0.02, 0.08, nearer / max(historyDepth, DepthRejectParams.w))
        * (1.0 - smoothstep(0.12, 0.35, prevOsc))
        * ((centerDepth >= 0.0) ? 1.0 : 0.0) * staticAuth;
    ghostReject = max(ghostReject, staticGhost);

    // FEATURE-LEVEL HISTORY COMPARISON (structure, not value): a ghost carries its own edges at positions
    // the current frame does not confirm; value-diff misses contamination that preserves average
    // brightness. Two responsive-only signals (they can only increase diff, never protect): (a) both
    // gradients strong but pointing differently; (b) history has structure where the current frame is
    // FLAT. Motion-adjacent gated: at rest, converged fine geometry's history gradient legitimately
    // out-details the render-res current gradient. Cost: 4 point history taps.
    float featReject = 0.0;
    // FILTERED REJECTION VERDICT (TSR MeasureRejection core): compare BLURRED operands so the aliasing
    // difference between the render-res frame and the sharp converged history is suppressed and only a
    // genuine SHADING difference remains; normalize the history's clamp energy by how different the
    // frames actually are. filtReject = 1 means "shading changed" (SM3 keeps the min-neutral 1 so the
    // diff composition below is bit-identical there); shadingChange is its point-diff-gated share for
    // the clamp tighten (0 = neutral on SM3).
    float filtReject = 1.0;
    float shadingChange = 0.0;
#if SM4 // ps_3_0 temp-register budget — SM3 relies on the depth/ghost/reactive rejects
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
        // Blurred operands from taps that already exist: bC = the 5-tap plus mean (m1), bH = plus-blur
        // of the four history luma taps above + the point center (TSR Blur3x3 plus weights 1, 4x0.5).
        float hC = dot(historyPoint.rgb, lw);
        float bH = (hC + 0.5 * (hE + hW + hN + hS)) * (1.0 / 3.0);
        // Clamp box = the box-tap luma min/max expanded by the 2/255 storage-quantization floor (TSR's
        // MeasureBackbufferLDRQuantizationError analogue). The verdict: how much of the blurred history
        // would clamping destroy, relative to max(frame difference, the neighborhood's own spread) —
        // in-box history is indistinguishable from spatial variation and scores 0 rejection.
        float bmin = min(min(cboxC.x, cboxW.x), min(min(cboxE.x, cboxN.x), cboxS.x)) - (2.0 / 255.0);
        float bmax = max(max(cboxC.x, cboxW.x), max(max(cboxE.x, cboxN.x), cboxS.x)) + (2.0 / 255.0);
        float clampE = abs(clamp(bH, bmin, bmax) - bH);
        filtReject = saturate(clampE / max(abs(m1.x - bH), bmax - bmin));
        // Point-diff gate (first-cycle conservatism): the filtered verdict acts only where the point
        // comparison also sees change — it can suppress aliasing-as-rejection, never add rejection on
        // its own. Pre-clamp resolution-matched history luma (hLow; = historyRaw at <= 1.5x ratio).
        float pointDiffPre = saturate(abs(m1.x - hLow.x) / max(0.2, max(m1.x, hLow.x)));
        shadingChange = min(pointDiffPre, filtReject);
    }
#endif

    // VELOCITY-DISPARITY REACTIVE (FSR2 lock-break analogue): a mismatch between this frame's dilated
    // velocity and the velocity stored alongside the history means the history was written by content
    // moving differently — reveals after the mover left the 3x3, starts/stops, direction changes. Trust
    // modulation only. Zero-vs-zero on backdrop/fringes = no signal. Threshold is resolution-scaled: one
    // 8-bit LSB of the encode in pixels grows with resolution.
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
    // radial k(0) = 0.8889 (1/0.8889 = 1.125). Lanczos2 peaks at exactly 1; wC can go slightly negative
    // when the center tap lands in a lobe — saturate reads that as "no real coverage", truthfully.
    // WITNESS RULE input, used by the oscillation detector and the Kalman counter: under TAAU, off-phase
    // frames are interpolation, not observation — they may neither testify against history nor build/decay
    // alternation evidence.
#if SM4
    float sampleConf = saturate(wC * lerp(lerp(1.2656, 1.125, edgeAniso), 1.0, useLan));
#else
    float sampleConf = saturate(wC * lerp(1.2656, 1.125, edgeAniso)); // upscaleRatio hoisted to the kernel block
#endif
    float testify = (upscaleRatio > 1.001) ? sampleConf : 1.0;

    // SUSPICION: the union of every contamination/disocclusion detector — THE motion-trust variable.
    // Speed alone is the wrong regime key (a fast coherent pan reprojects exactly; slow creep can still
    // contaminate), so the trust-limiting gates scale their motion response by whether the evidence
    // actually flags anything. foreign is demoted to 0.35 weight here: it fires along every silhouette
    // during parallax pans where the history is valid (true reveals stay covered by the depth/ghost
    // rejects); ringContam joins at full weight — where it fires it is direct evidence of preserved trail.
    float suspicion = max(max(depthReject, ghostReject), max(max(foreign * 0.35, reactive), ringContam));

    // LUMA-OSCILLATION DETECTOR (anti-fizzle), state in meta.A (1 sign bit + 7-bit EMA).
    // Fizzle = a converged pixel whose curr-vs-history luma delta ALTERNATES SIGN at frame frequency
    // (jitter flipping which sub-pixel fragment covers the sample). A ghost's delta is MONOTONIC, so
    // sign-alternation is a signal a ghost structurally cannot produce. Measured on pre-blend curr vs
    // pre-clamp historyRaw so deeper trust doesn't extinguish its own evidence. Disabled under debugMeta.
    float osc = 0.0;
    float oscAmp = 0.0;
    float packedA = 0.0;
    if (!debugMeta)
    {
        float dl   = curr.x - historyRaw.x; // signed, pre-clamp history
        // Amplitude gate 0.03 keeps low-contrast texture shimmer out of trust-deepening: in the
        // small-delta regime, slight ghost residue over a textured surface sign-alternates exactly like
        // texture sampling noise, so a lower admission would protect ghosts.
        float mag  = step(0.03, abs(dl));
        float sgn  = step(0.0, dl);
        float flip = mag * abs(sgn - prevSgn); // 1 only when a real-amplitude delta reversed sign
        // WITNESS RULE on the EMA: off-phase frames' interpolation error is biased (no sign flip), so an
        // ungated EMA decays the alternation evidence between real samples — the update rate is scaled by
        // testify so off-frames neither build nor decay evidence.
        // WITNESS-RATE SCALING past 2x ratio (reference-equivalent): FSR2/TSR run their instability
        // detectors at RENDER resolution, where every pixel is witnessed every frame; this detector runs
        // at OUTPUT resolution, where a pixel is witnessed ~1 frame in 1/scale^2 (1-in-9 at 0.33x).
        // Scaling the per-witness rate with the ratio reproduces the references' wall-clock convergence.
        // ASYMMETRIC rates: evidence FOR alternation builds at the boosted rate; absence-of-flip decays at
        // the base rate (a converged limit cycle is not strictly alternating — symmetric boosted decay
        // made borderline clutter chatter across the lock thresholds). Ghost-safety: a monotonic ghost
        // emits flip=0 streams, which still decay to zero.
        float oscRateUp = 0.15 * lerp(1.0, 2.2, saturate(upscaleRatio - 2.0)); // 0.15 <=0.5x -> 0.33 at 0.33x
        // 4-BIT STORAGE AWARENESS: build steps (boosted rate toward 1) always exceed a quantization
        // level; decay is floored at one 4-bit level per witnessed frame — an unfloored EMA tail
        // rounds away in storage and would hold residual osc forever (a monotonic ghost must decay
        // to zero).
        float oscUp = lerp(prevOsc, 1.0, oscRateUp * testify);
        float oscDn = max(prevOsc - max(0.15 * prevOsc, 1.0 / 15.0) * testify, 0.0);
        osc = lerp(oscDn, oscUp, flip); // ~6-7 witnessed-flip build, base-rate decay
        // ALTERNATING AMPLITUDE (TSR Moire-lite state): the witnessed |delta| on flip frames, kept as
        // a 3-bit saturating counter — quantized one-level nudges instead of an EMA (3-bit EMA updates
        // round away in storage). Builds one level per witnessed flip whose observed amplitude exceeds
        // the stored level; decays one level on a witnessed ONE-SIDED real delta (a ghost's signature)
        // or when the observed flip amplitude drops a level; holds on quiet frames. Encode spans 0.35
        // luma (~0.05/level). Consumed as clamp-box slack at the box below.
        float obsQ = saturate(abs(dl) * (1.0 / 0.35)) * 7.0;
        float prevAmpQ = prevAmp * 7.0;
        float ampDir = flip * (step(prevAmpQ + 0.5, obsQ) - step(obsQ, prevAmpQ - 0.5)) - (1.0 - flip) * mag;
        oscAmp = saturate(prevAmp + ampDir * (1.0 / 7.0) * testify);
        // EVIDENCE WIPE: alternation evidence must not survive history invalidation, or locks re-engage on
        // contaminated history the moment motion stops (bypassing the warmup floor). Locks are RE-EARNED
        // after any invalidation event. Curved (knee 0.25): only meaningful invalidation wipes — grazing
        // partial rejects are noisy and constant near any motion and would nuke locks screen-adjacent to
        // movers every frame.
        float wipe = 1.0 - smoothstep(0.4, 0.85, max(max(depthReject, ghostReject), max(reactive, featReject)));
        osc *= wipe;
        oscAmp *= wipe; // amplitude slack must not survive invalidation either
        // Hold the sign bit through quiet/blind frames. BINARY select: a fractional sign bit under TAAU
        // partial-witness frames corrupts the packedA encode; step() keeps it MojoShader-safe.
        float newSgn = lerp(prevSgn, sgn, step(0.5, mag * testify));
        // PACK sign(1) + osc(4) + amp(3), byte-exact against RGBA8 rounding (see the decode at the meta
        // fetch). Off-screen = full evidence reset.
        packedA = reprojectable
            ? (newSgn * 128.0 + floor(osc * 15.0 + 0.5) * 8.0 + floor(oscAmp * 7.0 + 0.5)) * (1.0 / 255.0)
            : 0.0;
    }

    // EVIDENCE-CONDITIONED ACCUMULATION (Kalman-gain counter): N counts EVIDENCE, not frames. The
    // innovation |curr - history| is normalized by the neighbourhood stddev (the expected sampling noise
    // for THIS content). Verdicts:
    //   * agreement (innovation within the noise) grows N (+1/frame);
    //   * SIGN-ALTERNATING innovation counts as agreement regardless of size — zero-mean noise, the
    //     converged-fine-geometry signature (ghosts/content changes are biased one-way and cannot claim it);
    //   * persistent one-sided disagreement COLLAPSES N multiplicatively: deep trust unwinds in a few
    //     frames and stays responsive until the scene settles (catches content changes WITHOUT motion —
    //     TV screens, lighting, cutaway toggles).
    // TAAU WITNESS RULE: only frames whose nearest real sample covers this output pixel may testify
    // AGAINST history; off-frames still accrue trust weakly. Ghost-safe by direction: a collapsed N only
    // ever ADDS current weight via the ramp. Hard-reset only when history is off-screen; deliberately NOT
    // zeroed by depthReject (a noisy edge signal would pin silhouettes at N=0); ghost/depth/reactive caps
    // below.
    float inno = abs(curr.x - historyRaw.x) / max(sigma.x, 0.02);
    // Osc protection edge 0.12: off-phase interpolation error is biased against thin bright detail, so a
    // higher edge starves fine geometry's protection under TAAU. Collapse 0.75: a verdict should need a
    // few consistent frames. RELATIVE-MOTION CARVE-OUT on the osc branch (matching rejAuth/ghostReject):
    // without it, a slow trail over oscillation-proven ground (sand, canopy) holds deep N under the very
    // lock the osc signal granted; with it, the innovation branch alone governs there.
    float agreeK = max(1.0 - smoothstep(1.0, 2.5, inno), smoothstep(0.12, 0.35, osc) * (1.0 - relMotion));
    // SLIGHT-BIAS PENALTY (persistent-tail discriminator): a faint monotonic ghost has a small ONE-SIDED
    // innovation the agreement branch reads as ~full agreement, so N maxes and deep history holds the
    // residue. Dock agreeK only in the discriminating band: low osc (non-textured surfaces — high-osc
    // churn is protected by the anti-fizzle lock), a mid-innovation window (clear of both the
    // flat-converged floor and genuine large changes), NEAR-STATIC only (a slowly-drifting pixel shows the
    // same signature innocently — sub-pixel reproject error is small, one-sided, mid-inno), and LOW SIGMA
    // (a pixel 1-2px from a hard edge also sits in the band — the edge is in its own box statistics, a
    // ghost's defining venue is a genuinely flat surface). Never deepens (min()). Upscale-only.
    float biasPenalty = (1.0 - smoothstep(0.12, 0.35, osc))
                      * smoothstep(0.25, 0.7, inno) * (1.0 - smoothstep(1.0, 2.0, inno))
                      * (1.0 - smoothstep(0.05, 0.35, velPx))
                      * (1.0 - smoothstep(0.04, 0.12, sigma.x))
                      * saturate(upscaleRatio - 1.0)
                      * (1.0 - thinLine); // thin-line exemption: off-phase interpolation against a proven
                                          // depth ridge is one-sided INNOCENTLY — a ghost has no ridge
    agreeK = min(agreeK, 1.0 - 0.55 * biasPenalty);
    float collapse = lerp(1.0, lerp(0.75, 1.0, agreeK), testify); // testify hoisted above the osc detector
    // GROWTH is witness-gated only once EVIDENCE exists: the witness rule protects converged history from
    // off-phase false testimony, but a fresh pixel counts every frame (all samples are information when
    // you know nothing) — the off-phase discount fades in with minN.
    float growK = agreeK * lerp(1.0, lerp(TuneGrowOffPhase, 1.0, testify), saturate(prevN / 8.0));
    float newN = reprojectable ? min(prevN * collapse + growK, MaxAccum) : 0.0;
    // Ghost-side reject RESETS the counter: the surface that wrote the history has provably left, so the
    // honest treatment is the same as off-screen — raw current, then the warmup ramp rebuilds. SHAPED:
    // only CONFIDENT rejection collapses the evidence; grazing partial rejects — constant along every
    // silhouette during motion — leave the counter alone (their blend contribution already handles them).
    newN = lerp(newN, 0.0, smoothstep(0.35, 0.85, ghostReject));
    newN = lerp(newN, min(newN, 6.0), smoothstep(0.3, 0.8, depthReject));
    newN = lerp(newN, min(newN, 8.0), reactive);

    float lumaC = curr.x;                   // Y in YCoCg (current center sample)

    // VARIANCE CLAMP width. RESOLUTION-SCALED: at upscale the box statistics come from render-res taps,
    // so converged output-res detail is diluted in its own box and a fixed width re-clips it every frame.
    // Base TuneGamma at native -> 2x base at ratio 3. EVIDENCE-SCALED: the widening collapses toward the
    // base width on any contamination/disocclusion evidence (suspGamma — foreign at FULL weight here:
    // parallax edges are exactly where a wide box leaks). MOTION-DECAYED: osc-proven clutter has its
    // rejects deliberately authority-muted, so it is evidence-silent by design — narrow the widening while
    // the pixel is in motion (current or REMEMBERED — a just-stopped pixel keeps the narrowed box one
    // extra frame so the scrub finishes), full width returns at rest.
    float suspGamma = max(suspicion, foreign); // foreign at FULL weight for the box (0.35-demoted in suspicion)
    float gammaMotion = max(moveGate, storedMove);
    float GAMMA = TuneGamma * lerp(1.0, 2.0,
        saturate((upscaleRatio - 1.0) * 0.5) * (1.0 - suspGamma) * (1.0 - TuneGammaMotionDecay * gammaMotion));
    // OSCILLATION LOCK (FSR2-lock analogue via the oscillation signal): the clamp box is built from
    // render-res taps but the converged history holds output-res detail — a thin line that is sub-pixel
    // at render res is diluted in the box statistics and the clamp erodes it every frame. On pixels with
    // PROVEN sign-alternation (a ghost is monotonic — it cannot earn this), stillness, and no
    // disocclusion signals, let the locked history pass (see the lock escape after the clamp).
    // stillGate: locks survive sub-pixel drift and slow pans (suspicion-scaled velocity — coherent motion
    // counts at reduced speed for lock purposes; any flagged pixel counts at full speed).
    float stillGate = 1.0 - smoothstep(0.8, 2.0, velPx * lerp(TuneStillGateFloor, 1.0, suspicion));
    // Lock entry eases with render scale (floorScale): under heavy TAAU an output pixel is witnessed only
    // ~1 frame in 1/scale^2, so lock evidence accumulates that much slower — entry edge 0.24 at <= 0.5x,
    // 0.32 native (TV-static-like content equilibrates at ~0.5 osc and gains a bit of partial trust as
    // the cost; still clamp-bounded). THIN-LINE EASE: a same-frame depth ridge is structural evidence
    // standing in for the first witnessed flips, halving the entry edge — proven thin geometry reaches
    // partial lock in roughly half the frames; the kill-gates below are unaffected.
    float lockLo = lerp(0.24, 0.32, floorScale) * lerp(1.0, 0.5, thinLine);
    // Lock gate stack — shared by the lock and the amplitude slack below: stillness plus every
    // disocclusion/contamination detector silent.
    float lockGates = stillGate
                    * (1.0 - depthReject) * (1.0 - ghostReject) * (1.0 - reactive) * (1.0 - foreign)
                    * (1.0 - featReject) * (1.0 - noVel);
    float oscLock = smoothstep(lockLo, 0.7, osc) * lockGates;
    // Locks do NOT widen the box (no reference does — FSR2's locks lerp toward unclamped history instead;
    // see the lock escape after the clamp below, bounded by its endpoints and unable to compound).
    float gammaEff = GAMMA;
    // RECTIFY, DON'T REJECT: blend-side rejection only chooses between keeping history (ghosts) and
    // injecting raw (aliased edges). TIGHTENING the clamp on reject evidence is the third option: the
    // stale color snaps toward the current neighborhood statistics — a filtered, anti-aliased value — so
    // mid-strength rejects both scrub the ghost and stay smooth. Raw injection below is reserved for
    // near-certain rejection only.
    // shadingChange joins the depth evidence here: a motion-silent content change (TV, lighting,
    // cutaway) confirmed by BOTH the point diff and the filtered verdict rectifies via the tighten —
    // scrubbed to current statistics — instead of waiting on the Kalman collapse. 0 on SM3.
    float rejTighten = smoothstep(0.12, 0.6, max(max(depthReject, ghostReject), shadingChange));
    gammaEff *= lerp(1.0, 0.3, rejTighten);
    // MOTION-SCALED CLAMP TIGHTENING (upscale-only): rotating geometry reveals surface with valid
    // same-object depth, coherent velocity, and similar colors — every structural detector is silent, so
    // only the variance clamp scrubs the stale color. Under motion the history's sub-pixel detail
    // advantage is smaller anyway, so a moderate tighten costs little AA while snapping self-reveal
    // residue within a frame or two. TIGHTEN-ONLY (the ghost-safe direction); locks unaffected at rest.
    gammaEff *= lerp(1.0, TuneMotionClampTighten, moveGate * 0.8 * smoothstep(1.0, 1.5, upscaleRatio));
    float3 cmin = m1 - gammaEff * sigma;
    float3 cmax = m1 + gammaEff * sigma;
    // AMPLITUDE-PROPORTIONAL FLICKER SLACK (TSR Moire-lite consumption; TSR: StableFilteredBoxMax =
    // max(BoxMax, BoxMin + MoireErrorSize)): widen the LUMA clamp by exactly the witnessed alternating
    // amplitude — a smooth, evidence-priced allowance replacing lock-threshold chatter on partial-osc
    // content. Gated by the full lock gate stack (any motion/disocclusion evidence removes it) and
    // structurally ghost-safe: a monotonic ghost cannot build or hold amplitude state. Chroma untouched.
    float ampSlackY = oscAmp * 0.35 * lockGates;
    cmin.x -= ampSlackY;
    cmax.x += ampSlackY;
    // INPUT-RESOLUTION RECTIFICATION under TAAU (TSR mechanism): the box statistics come from bilinear
    // taps whose mixture changes with jitter phase, so clamping the native-res history re-clips converged
    // output-res detail slightly differently every frame on pixels that can't fully lock. Split the
    // history into its render-texel AVERAGE (phase-stable low component) + the sub-pixel detail riding on
    // top; clamp only the low component and apply that correction to the full history. A ghost is wrong
    // in its low component -> still fully corrected; converged sub-pixel detail is zero-mean around it ->
    // passes untouched. A wide safety clip (2x the box) bounds the detail component. 1:1 keeps the classic
    // direct clamp (no domain mismatch there).
    float3 history;
    float lumaHCmp; // history luma FOR THE DIFF COMPARISON — resolution-matched to m1 (see below)
#if SM4 // ps_3_0 temp-register budget — SM3 always takes the direct-clamp else path
    if (upscaleRatio > 1.5)
    {
        // hLow fetched with the rejection-authority block above (same 4-tap render-texel average).
        float3 hLowC = ClipAABB(cmin, cmax, hLow);
        // MOTION-FADED SPLIT RECTIFICATION: the split protects sub-pixel detail inside a loose 2x safety
        // hull — correct at REST, but under MOTION a stale trail's own structure rides the protected
        // detail component through the hull, immune to every trust lever. Blend toward the direct full
        // clamp by motion (current or remembered — a just-stopped pixel keeps the direct clamp one extra
        // frame so the trail finishes scrubbing). PARTIAL direct share (TuneDirectClampMix): a FULL direct
        // clamp under motion re-clips history to a per-phase-changing box and churns at high-contrast
        // transitions; the remaining rectified share keeps the clamp value phase-coherent.
        float3 rectified = ClipAABB(m1 - 2.0 * gammaEff * sigma, m1 + 2.0 * gammaEff * sigma, historyRaw + (hLowC - hLow));
        float3 directCl  = ClipAABB(cmin, cmax, historyRaw);
        history = lerp(rectified, directCl, TuneDirectClampMix * max(moveGate, storedMove));
        // RESOLUTION-MATCHED DIFF (TSR: compare at input resolution): m1 is a render-res mean but the
        // sharp history is output-res — for converged thin geometry they disagree FOREVER (the line is
        // diluted in m1), a permanent phantom "content change". Compare against the history's render-texel
        // average instead: a converged line's low component matches m1 (deep trust holds); a ghost or real
        // change is wrong in its low component too, so diff still fires.
        lumaHCmp = hLowC.x;
    }
    else
#endif
    {
        history = ClipAABB(cmin, cmax, historyRaw);
        lumaHCmp = history.x;
    }

    // LOCK ESCAPE (FSR2-style, replaces lock box-widening): on pixels with a proven oscillation lock,
    // blend from the clamped history toward the RAW history — converged output-res detail passes the
    // clamp intact without the box ever growing. Bounded by its endpoints (raw history at worst),
    // phase-independent, and gated by everything oscLock already requires: proven sign-alternation,
    // stillness, and zero rejects — the exact gates that break FSR2 locks. Partial locks get a partial
    // escape. The diff below stays on the resolution-matched lumaHCmp (pre-escape) by design.
    // Escape share scales DOWN as measured amplitude rises: the box slack above already admits the
    // oscillation, and keeping both at full strength would license a ghost band.
    history = lerp(history, historyRaw, oscLock * (1.0 - 0.5 * oscAmp));

    // --- Blend: content-adaptive luminance-feedback weight, diff-driven. The counter (newN) feeds the
    //     deep end and the warmup ramp; it does not drive the clamp. ---
    // The confidence check uses the NEIGHBOURHOOD MEAN (m1.x), not the single raw jittered sample: a
    // single tap of high-frequency content flips between wildly different values every frame BY DESIGN
    // even once fully converged — comparing it to the smoothed history reads as permanent "change" and
    // pins the blend responsive forever. m1 averages out per-sample noise while still moving immediately
    // on a real change. Only the confidence signal changes — the displayed blend still uses the sharp curr.
    float lumaH = history.x; // display-side luma (Karis weights); the DIFF uses the resolution-matched lumaHCmp
    float diff = saturate(abs(m1.x - lumaHCmp) / max(0.2, max(m1.x, lumaHCmp)));
    // The filtered rejection verdict BOUNDS the point diff (min): where the blur-domain comparison says
    // the difference is aliasing, not shading, the point diff may not erode trust. Real changes fire
    // both and pass unchanged; SM3's filtReject stays 1 (bit-identical there). Structural rejects
    // still enter at full strength below.
    diff = min(diff, filtReject);
    diff = max(max(diff, depthReject), max(max(ghostReject, featReject), ringContam));
    // KALMAN DEEP END: the deep end follows the Kalman gain N/(N+1) once the EVIDENCE counter outgrows
    // the EMA baseline — reference upscalers converge fine detail because accumulation approaches an
    // equal-weight running average, which a fixed EMA floor never can. Safe because N counts witnessed
    // agreement (sigma-normalized innovation, sign-alternation aware, witness-ruled), collapses on
    // disagreement, and is reset/capped by every disocclusion path — a stale pixel structurally cannot
    // keep a large N. The diff term still lerps toward full responsiveness instantly on top.
    float minN = min(prevN, newN);
    // Deep-end cap = the CYCLE-HIDING WINDOW at upscale: excess depth beyond ~one Halton cycle buys
    // nothing visible on converged content but preserves slight sub-threshold reprojection residue behind
    // motion (small innovation reads as agreement, N maxes, deep history holds the residue after all
    // motion evidence is gone). cycleWindow mirrors the lock path's cycleCeil (JitterPhases-driven),
    // aligning the two deep paths. Fades in over ratio 1.2..1.8; native keeps TuneDeepCapBase.
    float cycleWindow = clamp(1.0 - 1.0 / (1.0 * JitterPhases), 0.965, 0.99);
    float deepCap = lerp(TuneDeepCapBase, cycleWindow, smoothstep(1.2, 1.8, upscaleRatio));
    float deepEnd = min(max(1.0 - BlendFactor, minN / (minN + 1.0)), deepCap);
    // Responsive end: the structural rejects (depth/ghost/center/foreign/feature) own disocclusion duty
    // and enter this lerp through the max() above at full strength; pure-luma responsiveness is gentler
    // (~40%/frame, fully responsive in ~3 frames).
    float historyWeight = lerp(deepEnd, TuneRespEnd, diff);
    // Velocity-disparity reactive caps the history trust directly (soft — keeps a moving-content pixel
    // from pulsing aliased when the camera stops).
    historyWeight = min(historyWeight, lerp(1.0, 0.85, reactive));
    // Foreign-velocity trust cap — MILD only: a safety net for imperfect own-velocity (e.g. unwritten
    // alpha fringes decoding as zero); a hard cap re-creates raw jitter crunch on the ring.
    historyWeight = min(historyWeight, lerp(1.0, 0.92, foreign));
    // Velocityless-overlay cap (see noVel above): headline icons / speech bubbles get an ~8-frame window
    // — fresh animation, no smear.
    historyWeight = min(historyWeight, lerp(1.0, 0.88, noVel));
    // MOTION TRUST CAP (upscale-only): a coherently-moving surface accumulates sub-pixel reprojection
    // error each frame that no detector can see (innovation below the texture's own sigma, foreign 0 on
    // mover interiors) — with a deep window the residue rides for seconds as surface ghosting. Cap trust
    // while the pixel itself is moving: the surface continuously refreshes and the moving state reads as
    // spatially-AA'd reconstruction (paired with the motion-suppressed soften at the display stage). At
    // rest the cap releases and full convergence resumes. Ghost-safe by direction. Native untouched.
    historyWeight = min(historyWeight, lerp(1.0, TuneMotionTrustCap, moveGate * smoothstep(1.0, 1.5, upscaleRatio)));

    // OSCILLATION TRUST (anti-fizzle action). Every gate must pass: proven sign-alternation (a ghost
    // fails osc), ~zero velocity (a mover fails stillGate), no disocclusion signal, and low diff
    // (essential — without it this lerp could RAISE trust on a changing pixel). SOFT diff gate: only
    // large diffs kill the trust — thin geometry's render-res-diluted neighbourhood mean gives it a
    // permanent baseline diff below ~0.2, so fine geometry keeps its lock; trust dies fully by diff ~0.54.
    // Known residual: TV/video textures equilibrate at osc~0.5 -> at most slight partial trust,
    // clamp-bounded.
    float oscTrust = oscLock * (1.0 - saturate((diff - 0.25) * 3.5));
    // CYCLE-AWARE ceiling: the visible repeating jitter pattern at extreme upscale is a cycle-vs-window
    // mismatch — the deep end must exceed the Halton cycle it has to hide (72 frames at 1/3 scale), so
    // the ceiling scales with the cycle: 1 - 1/(1.2*cycle), clamped. Ghost-safety gates unchanged: deep
    // trust still needs sustained alternation, stillness, and no rejects; the Kalman collapse, honest
    // disocclusion and feature rectification all override from below.
    float cycleCeil = clamp(1.0 - 1.0 / (1.2 * JitterPhases), 0.965, 0.99);
    // ENTRY ALIGNED WITH THE LOCK (low-scale only): a partially-locked pixel (osc 0.32..0.55 — where
    // distant canopy equilibrates at 1/3 scale) must not get lock privileges with only the shallow
    // ceiling (a window far under the cycle it must hide). Slide the lower edge down to the lock band as
    // render scale drops; native keeps 0.55 (the TV/video partial-trust guard — at native the 8-frame
    // cycle fits any window, and content-changing screens are killed by diff).
    float ceilLo = lerp(0.32, 0.55, floorScale); // 0.55 native -> 0.32 at <= 0.5x, matching the lock entry
    float oscCeil = min(1.0 - 0.5 * BlendFactor, lerp(0.965, cycleCeil, smoothstep(ceilLo, 0.85, osc)));
    // RAISE-ONLY: the Kalman deep end can legitimately exceed the lock ceiling — the lock lerp must never
    // pull earned evidence-trust back down.
    historyWeight = max(historyWeight, lerp(historyWeight, oscCeil, oscTrust));

    // EVIDENCE-GATED motion boost: full raw boost only where something is actually suspicious (a reject,
    // foreign velocity, or velocity disparity); an evidence-silent pan keeps only the small floor share
    // (native has no other anti-lag insurance — the sample-confidence motion regime below is upscale-gated).
    float motionBoost = saturate(vmag * 20.0) * TuneMotionBoostMax * lerp(TuneMotionBoostFloor, 1.0, suspicion);
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

    // TAAU SAMPLE CONFIDENCE (upscale only — the standard temporal-upscaler mechanism): an output pixel's
    // nearest real sample is sometimes dead-center and sometimes ~a full render texel away; on the far
    // frames the reconstruction is pure interpolation, and blending it at full weight injects per-frame
    // wobble. Weight the current contribution by the nearest sample's kernel proximity: real-sample
    // frames update strongly, in-between frames lean on history. Off at 1:1; faded under motion.
    if (upscaleRatio > 1.001) // upscaleRatio/sampleConf hoisted above the Kalman counter
    {
        // Floor decays toward 0.08 past 2x ratio (reference-equivalent: FSR2 weights each frame's
        // contribution by nearest-sample kernel proximity, continuously approaching zero on
        // information-free phases at heavy ratios — this floor is an anti-starvation invention with no
        // reference analogue, so decaying it moves toward the references' asymptotic zero).
        // COVERAGE-SCALED: a frame with no information contributes NOTHING — on zero-coverage frames the
        // floor would otherwise inject the bilinear fallback, a steady blur + flicker drip. Pure history
        // hold there; motion and the covering frames carry all responsiveness.
        float confFloor = lerp(TuneConfFloor, 0.08, saturate(upscaleRatio - 2.0)) * saturate(wsum / (0.3 * kscale));
        // MOTION-SCALED, not motion-DISABLED: a binary switch to full unfiltered injection under motion is
        // a hard-raw cliff. Off-phase frames inject at 55% under motion and lean the rest on reprojected
        // history (AA keeps working while moving); dead-on samples keep full weight; every reject path
        // still overrides from below. Regime keyed on SUSPICION-scaled motion: coherent pans keep most of
        // the confidence-weighted accumulation.
        // TRUST-FADED throttle: the off-phase protection exists for CONVERGED history; a fresh pixel
        // counts every frame (fast, clean resolve-in), so the throttle fades in with accumulated evidence.
        float confMul = lerp(lerp(confFloor, 1.0, sampleConf), lerp(0.55, 1.0, sampleConf), moveGate * lerp(0.3, 1.0, suspicion));
        blend *= lerp(1.0, confMul, saturate(minN / TuneConfFadeN));
    }

    // WARMUP RAMP (counter-driven): with no accumulated history the buffer is BLACK, and blending any of
    // it darkens the image. Seed from the current frame: full current on the first frame, then 1/2, 1/3...
    // KEYED OFF min(prevN,newN): prevN is the evidence that actually EXISTS in the history (prevN=0 on
    // frame one -> blend=1 -> output IS the raw frame), while newN keeps the ghost/reactive soft-caps'
    // responsiveness boost. Ghost-safe by direction: max() only ever pushes toward more current frame.
    // LOCK EXEMPTION: a lock structurally cannot coexist with cleared or invalid history (its evidence
    // resets with the meta), so the ramp has no legitimate job on a locked pixel — without this, a
    // Kalman-collapsed N re-injects raw jitter over the lock's deep trust.
    blend = max(blend, (1.0 / (min(prevN, newN) + 1.0)) * (1.0 - oscLock));

    // HONEST DISOCCLUSION (reference behavior — discard, not fade): a positively identified disocclusion
    // means the history is INVALID; blending any of it is wrong by construction. Full-strength reject =
    // raw frame immediately + the counter reset rebuilds through the warmup ramp. SHAPED knee: a
    // confident reject buys the full raw frame, while grazing partial rejects — constant along every
    // moving depth edge because the stored depth is dilated — lean on the motion clamp instead of
    // injecting fractional raw every frame.
    blend = max(blend, smoothstep(0.55, 0.9, max(depthReject, ghostReject)));

    // TEXTURE-DETAIL blend floor (pairs with the raw-sample input lean above): the converged value is the
    // temporal mean over the jitter footprint, which wipes single-texel texture detail. On low-variance
    // texture regions keep the blend responsive (~3-4 frame window) so the per-frame raw sample dominates.
    // ALL SCALES: this floor doubles as the anti-ghost backstop on low-variance surfaces (exactly where
    // movers walk). LOCK BYPASS: semi-uniform fine detail (distant canopy) can read low-variance at
    // render res and the floor would re-churn it forever under intense TAAU — ghost-safe by the lock's
    // own argument (ghost residue is monotonic, cannot earn the lock; the lock also dies on motion,
    // rejects, and foreign velocity).
    blend = max(blend, texDetail * TuneTexDetailFloor * (1.0 - oscLock));

    // RAW-STATE SPATIAL SOFTENING (upscale only; current-frame data only — zero ghost risk): when the
    // floors/rejects legitimately force a pixel mostly-raw, display the smooth reconstruction instead of
    // the near-point one — honest content with smooth edges instead of sharply-upscaled jaggies.
    // Converged pixels (low blend) keep the crisp reconstruction bit-exactly.
    float3 dispCurr = curr;
#if SM4 // ps_3_0 temp-register budget — SM3 displays the sharp reconstruction as-is
    if (upscaleRatio > 1.001)
    {
        // MOTION-SUPPRESSED: the soften covers STATIC raw states (reveals at rest, warmup, low-coverage
        // phases); under coherent motion the display stays on the crisp deringed reconstruction — which
        // IS the spatial AA — pairing with the motion trust cap so the moving state reads
        // sharp-and-refreshing rather than soft-and-stale.
        float rawSoften = saturate((blend - TuneRawSoftenOnset) * TuneRawSoftenSlope) * (1.0 - moveGate * TuneRawSoftenMotionSup);
        dispCurr = lerp(curr, filtSoft / max(wsumSoft, 1e-4), rawSoften);
    }
#endif

    // Anti-flicker (Karis): inverse-luma weighting so bright sub-pixel samples don't dominate/sparkle.
    // MOTION-FADED: the weighting is structurally dark-biased (history weight scales by 1/(1+lumaH)), so
    // on dark->light reveals during motion dark stale history gets boosted exactly where the current
    // content is bright. The sparkle it suppresses is a rest-state artifact; under motion fade to a plain
    // energy-honest lerp.
    float lumaFade = max(moveGate, storedMove) * TuneKarisFade;
    float wc = blend * lerp(1.0 / (1.0 + max(lumaC, 0.0)), 1.0, lumaFade);
    float wh = (1.0 - blend) * lerp(1.0 / (1.0 + max(lumaH, 0.0)), 1.0, lumaFade);
    float3 blended = (dispCurr * wc + history * wh) / max(wc + wh, 1e-5);

    float3 outYCoCg = reprojectable ? blended : dispCurr;

    // Sentinel 2.0 ("no velocity anywhere in the 3x3") must NOT survive into an fp16 history alpha: next
    // frame it would read as outside every valid depth range and paint a permanent depthReject ring
    // around all unwritten-velocity content (RGBA8 clamped it implicitly; fp16 does not).
    float depthForHistory = min(closestDepth, 1.0);
    // saturate: YCoCg->RGB can slightly under/overshoot [0,1]; fp16 does not clamp on store and unclamped
    // values would compound through the feedback loop.
    o.color = float4(saturate(YCoCg_to_RGB(outYCoCg)), depthForHistory);

    if (debugMeta)
    {
        // Diagnostic encode. R MUST stay the ACCUMULATION COUNTER even in debug — the next frame's
        // resolve decodes meta.R as prevN in BOTH techniques, so any other value corrupts the feedback
        // loop while debugging. Layout: R = counter, G = reject strength (depth or ghost), B = effective
        // history trust this frame (1 - blend), A = 0 (no stale oscillation trust on toggle-off).
        // Instrument law: a diagnostic must MASK missing data, never substitute a fabricated fallback.
        o.meta = float4(newN / MaxAccum, max(depthReject, ghostReject), 1.0 - blend, 0.0);
    }
    else
    {
        // Shipping encode: R = N, GB = this frame's dilated velocity for next frame's disparity reactive,
        // A = packed oscillation state (sign 1 + osc 4 + amp 3; 0 on non-reprojectable = evidence reset).
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
// GPUs, offered on BOTH backends via the AA dropdown. ONE upscale-general path; native 1:1 is
// the degenerate case.
//
// THE BRANCH-FREE LAW (non-negotiable in this body): NO `if`/ternary keyed on uniform- or
// texture-derived values — everything data-dependent is lerp/saturate/smoothstep/step
// arithmetic. This sidesteps a confirmed MojoShader ps_3_0 compiler bug class (uniform-value
// comparisons silently taking one branch) BY CONSTRUCTION. The only allowed exceptions: the
// loop-local `if (d < closestDepth)` nearest-wins reduction inside the [unroll]ed 3x3 (an
// ordinary per-tap compare, not a uniform-threshold compare) and the [unroll] loop structure
// itself. Even the final out-of-bounds select is a step()/lerp(). Note TAA_Core's `#if SM4`
// gates exist only for the ps_3_0 temp-register budget, not comparison correctness — its
// uniform-keyed `if`s still run on ps_3_0/GL.
//
// Deliberately excluded vs TAA_Core (register budget + the branch law): oscillation locks,
// Kalman evidence conditioning, ring-contamination + feature-level rejection, anisotropic /
// clutter-adaptive kernels, split (own-velocity) reprojection, velocityless-overlay handling,
// firefly/texture-detail/soft-display shaping. The four TAAU essentials it KEEPS:
// jitter-relative Mitchell reconstruction on the output grid, center-sample confidence, meta
// counter + warmup, depth-in-alpha range disocclusion.
//
// Unwritten velocity decodes as zero = identity reproject — correct for static content; actual
// motion is caught by the variance clamp + luma feedback (same reasoning as TAA_Core).
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

    // Jitter-relative Mitchell reconstruction base (TAA_Core's formulation): the output pixel center in
    // input-pixel coords vs this frame's jittered sample positions. buffer[p] holds content that
    // un-jittered belongs at p + SampleJitterUV, so the nearest sample's texel is floor(oPx - sPx).
    float2 oPx = uv * colSize;
    float2 sPx = SampleJitterUV * colSize;
    float2 baseTexel = floor(oPx - sPx);
    float2 fracd = (baseTexel + 0.5 + sPx) - oPx;
    // Separable weights, distances scaled by kscale so the kernel is OUTPUT-pixel sized. At native
    // (kscale 1, zero jitter) these degenerate to the fixed 0.8889/0.0556 constants.
    float3 kx3 = float3(MitchellK(abs(fracd.x - 1.0) * kscale), MitchellK(abs(fracd.x) * kscale), MitchellK(abs(fracd.x + 1.0) * kscale));
    float3 ky3 = float3(MitchellK(abs(fracd.y - 1.0) * kscale), MitchellK(abs(fracd.y) * kscale), MitchellK(abs(fracd.y + 1.0) * kscale));

    // VARIANCE BOX — 5-tap plus pattern at the content-stationary boxUV (uv - SampleJitterUV, bilinear
    // does the shift): the clamp box stays spatially stationary under jitter. Pure arithmetic.
    float2 boxUV = uv - SampleJitterUV;
    float3 cboxC = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV, 0, 0)).rgb);
    float3 cboxW = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxE = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxN = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 cboxS = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 m1 = (cboxC + cboxW + cboxE + cboxN + cboxS) * (1.0 / 5.0);
    float3 m2 = (cboxC * cboxC + cboxW * cboxW + cboxE * cboxE + cboxN * cboxN + cboxS * cboxS) * (1.0 / 5.0);
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0));
    // Base-sigma box at native, resolution-scaled wider at upscale (a fixed width re-clips
    // render-res-diluted converged detail). Branch-free ramp; native bit-exact at ratio 1.
    float GAMMA = LiteGamma * lerp(1.0, LiteGammaScale, saturate((upscaleRatio - 1.0) * 0.5));
    float3 cmin = m1 - GAMMA * sigma;
    float3 cmax = m1 + GAMMA * sigma;

    // 3x3 loop: RECONSTRUCTION at raw texel centers around the nearest jittered sample (all 9 taps), plus
    // VELOCITY DILATION + valid-depth RANGE on the 5-tap plus pattern at the jitter-stable boxUV (raw-uv
    // velocity taps read per-phase-different fragments on sub-pixel geometry and churn the depth tests).
    // The corner-tap exclusion is a compile-time literal test — folds under [unroll], no runtime branch.
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
            // Unwritten velocity pushed to sentinel depth 2.0 (beyond valid [0,1]) so a genuinely-far
            // valid pixel still wins the nearest-depth tiebreak. lerp, not ternary.
            float d = lerp(2.0, v.b, validTap);
            if (d < closestDepth) { closestDepth = d; dilatedVel = v.rg; } // allowed reduction
            dmin = min(dmin, lerp(1e9, v.b, validTap));
            dmax = max(dmax, lerp(-1e9, v.b, validTap));
            anyValid = max(anyValid, validTap);
        }
        // Reconstruction tap: RAW texel center (bilinear at an exact center = point fetch), weighted by
        // its true distance to the output pixel center via the separable kernel.
        float2 tapUV = (baseTexel + float2(dx, dy) + 0.5) * InvColorSize;
        float3 craw = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(tapUV, 0, 0)).rgb);
        float w = kx3[dx + 1] * ky3[dy + 1];
        filt += craw * w;
        wsum += w;
        if (dx == 0 && dy == 0) { crawC = craw; wC = w; } // folds under [unroll]
    }

    // Thin-coverage fallback: with the output-sized kernel some frames leave a pixel with almost no
    // in-support sample. Smoothly fall back to the nearest RAW texel (single-surface by construction;
    // sample confidence keeps such frames history-leaning).
    float3 curr = lerp(crawC, filt / max(wsum, 1e-4), saturate(wsum / (0.15 * kscale)));

    // SAMPLE CONFIDENCE: how much this frame's nearest sample actually covers this output pixel.
    // 1.2656 = 1 / MitchellK(0)^2 = 81/64, so at native (kscale 1, zero jitter: wC = 0.7901) sampleConf
    // saturates at exactly 1.0 and the blend multiplier below is exactly 1 — the native path is
    // unaffected by this term by construction.
    float sampleConf = saturate(wC * 1.2656);

    // Reproject with the dilated velocity; JitterDelta cancels the jitter baked into the velocity buffer.
    // Unwritten velocity = zero = identity reproject (correct for static overlays/backdrops).
    float2 velocity = dilatedVel;
    float2 histUV = uv - velocity + JitterDelta;
    float inBounds = step(0.0, histUV.x) * step(histUV.x, 1.0) * step(0.0, histUV.y) * step(histUV.y, 1.0);
    float velPx = length(velocity / InvScreenSize) * VelGatePxScale; // NATIVE px
    float moveGate = smoothstep(LiteMoveGateLo, LiteMoveGateHi, velPx);

    // Bicubic (Catmull-Rom) history fetch for detail preservation + a POINT tap for the packed depth in
    // alpha (LINEAR would mix two surfaces' depths at every silhouette — see sampler comment) and the
    // meta counter.
    float3 history = RGB_to_YCoCg(SampleHistoryBicubic(histUV));
    history = ClipAABB(cmin, cmax, history);
    float historyDepth = FetchHistoryPoint(histUV).a;
    float prevN = tex2Dlod(metaHistorySampler, float4(histUV, 0, 0)).r * MaxAccum;

    // DEPTH RANGE DISOCCLUSION (TAA_Core's test, simplified, branch-free): history depth outside the
    // current 3x3 valid-depth range = the surface that wrote it left. Range (not point) compare so static
    // edges — where jitter flips the nearest-depth winner — never self-reject. MOTION-GATED (a genuine
    // disocclusion requires motion; at rest any mismatch is sampling noise) and masked by anyValid (no
    // valid taps -> no evidence; the dmin/dmax sentinels never fire through the mask).
    float outside = max(max(dmin - historyDepth, historyDepth - dmax), 0.0);
    float depthReject = saturate(outside / max(historyDepth, DepthRejectParams.w) * DepthRejectParams.y - DepthRejectParams.z)
                        * moveGate * anyValid;

    // META ACCUMULATION COUNTER: N grows on agreement, collapses on rejection, zeroes off-screen.
    float newN = min(prevN + 1.0, MaxAccum) * inBounds;
    newN = lerp(newN, 0.0, depthReject);
    float minN = min(prevN, newN);

    // Luminance feedback: stable pixels keep deep accumulation; changed pixels drop toward current.
    // diff also inherits the depth evidence.
    float lumaC = curr.x;
    float lumaH = history.x;
    float diff = saturate(abs(lumaC - lumaH) / max(0.2, max(lumaC, lumaH)));
    diff = max(diff, depthReject);
    // Counter-driven deep end replaces the flat floor: proven-stable pixels earn N/(N+1) trust (capped),
    // never below the baseline 1-BlendFactor.
    float deepEnd = min(max(1.0 - BlendFactor, minN / (minN + 1.0)), LiteDeepCap);
    float historyWeight = lerp(deepEnd, LiteRespEnd, diff);

    // Motion-adaptive: lean more on current when moving fast (less lag/ghosting under motion). The
    // speed-proportional boost IS the lite tier's raw-motion character — tune to taste, not to zero.
    float motionBoost = saturate(length(velocity) * 20.0) * LiteMotionBoost;
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

    // Sample-confidence injection gate: frames whose kernel barely covers this pixel inject little (their
    // estimate is an amplified tail) — except under motion, where responsiveness wins. Exactly 1 at
    // native (see sampleConf note).
    blend *= lerp(lerp(LiteConfFloor, 1.0, sampleConf), 1.0, moveGate);
    // Honest disocclusion: strong depth evidence forces the current frame through regardless of trust.
    blend = max(blend, smoothstep(LiteHonestLo, LiteHonestHi, depthReject));
    // Warmup: a young history (small N) cannot claim deep trust yet.
    blend = max(blend, 1.0 / (minN + 1.0));

    // Anti-flicker (Karis) inverse-luma weighting so bright sub-pixel samples don't dominate.
    float wc = blend * (1.0 / (1.0 + max(lumaC, 0.0)));
    float wh = (1.0 - blend) * (1.0 / (1.0 + max(lumaH, 0.0)));
    float3 blended = (curr * wc + history * wh) / max(wc + wh, 1e-5);

    // Out-of-bounds reproject -> current frame. Arithmetic select (branch-free law).
    float3 outYCoCg = lerp(curr, blended, inBounds);

    // History alpha carries this pixel's dilated depth (unwritten sentinel clamps to 1.0) for next
    // frame's disocclusion test. Meta GB = the zero-velocity encode (v*10+0.5) so nothing downstream
    // misdecodes; A unused here (no oscillation state in the lite path).
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
