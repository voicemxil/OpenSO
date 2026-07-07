// TAA.fx — temporal anti-aliasing / temporal upscaling (TAAU) for OpenSO 3D mode.
//
// Reads:
//   colorTex       — this frame's rendered color.
//   historyTex     — previous TAA output (RGB) + packed dilated depth (A), velocity-reprojected.
//   metaHistoryTex — previous meta: R = accum count N/MaxAccum, GB = dilated velocity (v*10+0.5),
//                    A = luma-oscillation state (sign bit + 7-bit EMA).
//   velocityTex    — screen-space velocity (.rg) + normalized linear depth (.b) + valid mask (.a).
// Writes:
//   COLOR0 — displayed frame / next frame's history (RGB) + this pixel's dilated depth (A).
//   COLOR1 — next frame's meta (the TAADebug technique repurposes GB/A for diagnostics).
//
// Karis 2014 / UE4 / Playdead recipe, extended with a per-pixel evidence counter:
//   1. nearest-depth velocity dilation (3x3)
//   2. jitter-free reprojection: histUV = uv - velocity + JitterDelta
//   3. Catmull-Rom history fetch
//   4. tight YCoCg variance clamp
//   5. depth-based disocclusion rejection (no normal buffer — that MRT is written inconsistently)
//   6. content-adaptive luminance-feedback blend, deepened by the accumulation counter
//   7. inverse-luma anti-flicker weighting on the final blend (LDR pipeline, no tonemap step)

float2 InvScreenSize; // 1 / OUTPUT (history) resolution — the grid TAA resolves on
// 1 / INPUT color resolution. Equal to InvScreenSize normally; larger texels under TAAU, where this
// pass accumulates jittered render-res samples directly onto the native output grid.
float2 InvColorSize;
float  BlendFactor;   // baseline deep-history floor (current weight ~= BlendFactor on a stable pixel).
float  MaxAccum;      // cap on the accumulation counter N. Matches TAAResolve.MAX_ACCUM.
// Per-frame jitter delta (UV). Velocity is computed from the jittered projection, so adding this back
// when reprojecting history gives an exact (jitter-free) reproject.
float2 JitterDelta;
// Depth-disocclusion tuning, set from C# by the actually-allocated history format:
//   x = ghost dead-zone epsilon (storage quantization must never fire the ghost test by itself)
//   y = depthReject slope   z = depthReject offset   w = relative-compare denominator floor
// fp16 history: (0.0005, 12.0, 0.0, 0.02). RGBA8 fallback: (2/255, 6.0, 0.25, 0.05).
float4 DepthRejectParams;
// This frame's jitter as a UV offset. Variance-box taps sample at uv - SampleJitterUV so the clamp
// box stays stationary for static content; the centre sample stays at the jittered uv.
float2 SampleJitterUV;
// Rescales the velocity gates (moveGate/stillGate/reactive) to NATIVE pixels: 1/renderScale in
// pre-upscale (FSR1) mode, 1 everywhere else.
float  VelGatePxScale;
// Jitter cycle length (frames), from R2Jitter.HaltonCycle: 8 native, 32 at 0.5x, 72 at 1/3.
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
float TuneMotionTrustCap = 0.72;     // motion trust cap at upscale (interior-texture ghost lever)
float TuneMotionClampTighten = 0.72; // motion-scaled variance-clamp tighten at upscale (self-reveal lever)
float TuneRawSoftenOnset = 0.12;     // raw-state display soften: blend onset
float TuneRawSoftenSlope = 2.2;      // raw-state display soften: slope past onset
float TuneRawSoftenMotionSup = 0.85; // raw-state display soften: suppression under coherent motion
float TuneGamma = 1.5;               // variance clamp base width (sigma) — TAA_Core's GAMMA
float TuneTexDetailFloor = 0.28;     // texture-detail blend floor / low-variance anti-ghost backstop
float TuneConfFloor = 0.14;          // TAAU sample-confidence floor (the <=2x-ratio endpoint)
float TuneRingLo = 0.03;             // ringContam own-vs-dilated color knee, lower edge
float TuneRingHi = 0.10;             // ringContam own-vs-dilated color knee, upper edge

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

// Mitchell-Netravali (B=C=1/3) kernel at arbitrary distance, valid for x in [0, 2). The negative
// lobe is clamped to 0 by the max() so the 3x3 reconstruction stays convex (no ringing).
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

    // Full 9-tap Catmull-Rom (dropping the corner taps low-passes diagonal detail).
#if SM4
    // Dering hull clamp: Catmull-Rom's negative lobes ring around high-contrast detail; clamp to the
    // 9-tap min/max (standard TAA bicubic dering). SM4 only: naming all 9 taps overflows ps_3_0's
    // 32 temp registers (X4505 on OGL/MojoShader); SM3 accumulates + globally clamps below.
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
    return clamp(r, hullMin, hullMax); // weights sum to 1 exactly; hull also bounds fp16 overflow
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
    // Meta (RGBA8): R = accumulation count N/MaxAccum, GB = this frame's dilated velocity encoded
    // v*10+0.5 (saturates at +/-0.05 UV on store), A = packed luma-oscillation state (sign bit +
    // 7-bit EMA; 0 on non-reprojectable). TAADebug repurposes GB+A for diagnostics and disables
    // their consuming logic.
    float4 meta  : COLOR1;
};

// Shared TAA core. debugMeta is a compile-time uniform bool: when true, meta.GB carries diagnostics
// and the velocity-disparity reactive is forced off (never decode debug bytes as a velocity).
TAAOut TAA_Core(VSOut input, uniform bool debugMeta)
{
    TAAOut o;
    float2 uv = input.Coord;

    // 3x3 neighborhood, three tap sets: variance box at the un-jittered boxUV (a jitter-wobbling box
    // re-clips converged history), Mitchell reconstruction at raw texel centers with jitter-relative
    // weights (UE TAAU formulation), velocity dilation + depth range at the true pixel.
    float2 texSize = 1.0 / InvScreenSize; // OUTPUT pixels (velocity gates, reactive thresholds)
    float2 colSize = 1.0 / InvColorSize;  // INPUT color pixels (reconstruction, box, velocity taps)
    float2 boxUV = uv - SampleJitterUV;
    // Nearest jittered sample, in input-pixel coordinates: nearest sample's texel is floor(oPx - sPx).
    float2 oPx = uv * colSize;
    float2 sPx = SampleJitterUV * colSize;
    float2 baseTexel = floor(oPx - sPx);
    float2 fracd = (baseTexel + 0.5 + sPx) - oPx; // nearest sample's offset from the output center (input px)
    // Output-sized reconstruction kernel (reference TAAU / UE TSR): distances scaled by the upscale
    // ratio so the Mitchell kernel is sized for the OUTPUT pixel. Unclamped — the render-scale floor
    // of 1/3 bounds kscale at 3; zero-coverage frames inject nothing (see confidence floor).
    float upscaleRatio = InvColorSize.x / InvScreenSize.x; // outputRes / renderRes, > 1 under TAAU
    float kscale = upscaleRatio;
    // Variance box: 5-tap plus pattern at the content-stationary boxUV. The center tap doubles as
    // the thin-coverage fallback sample.
    float3 cboxC = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV, 0, 0)).rgb);
    float3 cboxW = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxE = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(InvColorSize.x, 0), 0, 0)).rgb);
    float3 cboxN = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV - float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 cboxS = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + float2(0, InvColorSize.y), 0, 0)).rgb);
    float3 m1 = (cboxC + cboxW + cboxE + cboxN + cboxS) * (1.0 / 5.0);
    float3 m2 = (cboxC * cboxC + cboxW * cboxW + cboxE * cboxE + cboxN * cboxN + cboxS * cboxS) * (1.0 / 5.0);
    // Edge-directional reconstruction: on a strong luma edge, stretch the kernel ALONG the edge
    // (tangent distances count half) so thin geometry gathers several real samples per frame.
    // Upscale-gated and faded in with edge strength — the native kernel is untouched.
    float2 grad = float2(cboxE.x - cboxW.x, cboxS.x - cboxN.x);
    float gmag = length(grad);
    float edgeAniso = smoothstep(0.15, 0.5, gmag) * saturate(kscale - 1.0);
    float2 en = grad / max(gmag, 1e-5);   // across-edge unit direction
    float2 et = float2(-en.y, en.x);      // along-edge unit direction
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0)); // neighborhood stddev (clamp box + clutter test below)
    // Clutter-adaptive kernel width: high variance with no coherent gradient direction (foliage)
    // widens the kernel so sub-pixel fragments consolidate instead of fizzling. Directed edges keep
    // the sharp anisotropic kernel; ratios <= 1.5 unchanged.
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
    // SM4 only: this and several other register-heavy paths below overflow ps_3_0's temp registers
    // (X4505 on OGL); SM3 runs the lean classic resolve.
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
    float3 crawC = 0; // raw nearest jittered sample (center recon tap)
    float wC = 0;     // center tap's kernel weight (sample confidence below)
    float2 dilatedVel = float2(0, 0);
    float closestDepth = 1e9;
    float closestMask = 0.0;
    float dmin = 1e9, dmax = -1e9; // valid-tap depth RANGE for the disocclusion test below
    // Center velocity tap: own velocity feeds the foreign-velocity reactive; own depth anchors the
    // depth-aware weights. Jitter-compensated (boxUV) — the velocity buffer is rasterized jittered,
    // so raw-uv taps flicker the depth tests on sub-pixel geometry.
    float4 vCen = tex2Dlod(velocitySampler, float4(boxUV, 0, 0));
    float2 centerVel = vCen.rg; // unwritten decodes as zero
    float centerDepth = (vCen.a >= 0.5) ? vCen.b : -1.0; // -1 = no depth anchor (weighting disabled)
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float2 ofs = float2(dx, dy) * InvColorSize; // neighborhood spans INPUT texels
        // Velocity/depth tap at the content-stationary boxUV. Dilation/range statistics use the 5-tap
        // plus pattern (Playdead's cross — corner contribution to dilation is marginal); corner depths
        // still feed the depth-aware reconstruction weights.
        float4 v = tex2Dlod(velocitySampler, float4(boxUV + ofs, 0, 0));
        if (dx == 0 || dy == 0)
        {
            // "No velocity written" -> depth sentinel 2.0 (beyond valid [0,1]) so genuinely-far valid
            // pixels still win the nearest-depth tiebreak over unwritten neighbours.
            float d = (v.a >= 0.5) ? v.b : 2.0;
            if (d < closestDepth) { closestDepth = d; dilatedVel = v.rg; closestMask = v.a; }
            if (v.a >= 0.5) { dmin = min(dmin, v.b); dmax = max(dmax, v.b); }
        }
        // Reconstruction tap: raw texel center (bilinear at an exact center = point fetch), weighted
        // by its distance to the output pixel center.
        float2 tapUV = (baseTexel + float2(dx, dy) + 0.5) * InvColorSize;
        float3 craw = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(tapUV, 0, 0)).rgb);
        // Soft render-texel-scale Mitchell reconstruction — display path for rejected pixels (reveals).
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
        // Depth-aware kernel weight (FSR2/DLSS, upscale only): weight each tap by depth similarity to
        // the pixel's own surface so fg/bg don't mix per jitter phase at silhouettes. Unwritten taps
        // count as same-surface; the 0.15 floor keeps cross-depth coverage at interior edges.
        if (upscaleRatio > 1.001 && centerDepth >= 0.0)
        {
            float dt = (v.a >= 0.5) ? v.b : centerDepth;
            w *= max(1.0 - saturate(abs(dt - centerDepth) / max(centerDepth, 0.02) * 5.0), 0.15);
        }
        filt += craw * w;
        wsum += w;
        if (dx == 0 && dy == 0) { crawC = craw; wC = w; } // folds under [unroll]
    }
    // Thin-coverage fallback (wsum ~ 0): fall back to the nearest raw texel — a point sample is
    // single-surface, where a bilinear fallback smears mover color across edges. Kscale-aware threshold.
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

    // Texture-detail preservation: on low-variance neighbourhoods lean the input toward the raw
    // nearest sample so single-pixel texture energy survives accumulation. Native only (floorScale) —
    // at low render scale the nearest sample sits far from the output pixel and the lean would flicker.
    float floorScale = saturate(BlendFactor / 0.03 - 1.0); // 1 at native, 0 at <= 0.5x render scale
    float texDetail = 1.0 - saturate(sigma.x * 12.0);
    curr = lerp(curr, crawC, texDetail * 0.75 * floorScale);

    // Firefly suppression at upscale: bound the incoming luma symmetrically against the neighborhood's
    // 2-sigma range so single bright/dark sub-pixel samples don't strobe. Input-side, zero ghost risk.
    if (upscaleRatio > 1.5)
    {
        curr.x = clamp(curr.x, m1.x - 2.0 * sigma.x - 0.02, m1.x + 2.0 * sigma.x + 0.02);
        // Speckle consolidation on directionless clutter: lean curr toward the stationary neighborhood
        // mean by clutter strength — the input's per-frame variance drops with zero ghost risk.
        // Directed edges keep the sharp reconstruction (clutter = 0); extreme upscale only.
        curr = lerp(curr, m1, 0.28 * clutter * saturate(upscaleRatio - 1.5));
    }

    // Reproject with the dilated velocity. No velocity-validity gate: unwritten decodes as zero =
    // identity reproject, correct for static content (2D/backdrop art, alpha fringes); moving content
    // that skips the velocity MRT is caught by the variance clamp + luma feedback.
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

    // Ring-contamination signal: a background ring pixel can keep foreground-trail color the
    // own-velocity structural tests are blind to. Measure own-anchored vs dilated-anchored history
    // color directly, gated by foreign (clean pans stay silent). Upscale only; feeds diff + suspicion.
    float ringContam = 0.0;
#if SM4 // ps_3_0 temp-register budget (X4505 on OGL) — SM3 runs without this signal
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

    // Reprojected previous meta: accumulation count N (R) + previous frame's dilated velocity (GB).
    float4 pm = tex2Dlod(metaHistorySampler, float4(histUV, 0, 0));
    float prevN = pm.r * MaxAccum;

    // Depth disocclusion (relative; depth is normalized linear 0=near..1=far). historyPoint.a is last
    // frame's dilated depth at this texel. Compare against the whole 3x3 depth RANGE — jitter flips
    // which neighbour wins the nearest-depth contest at a static edge, so a point compare would reset
    // every silhouette; a true disocclusion still lands outside the range. Motion-gated (moveGate):
    // at rest any depth mismatch is sampling noise (sub-pixel foliage flips fragments per phase);
    // content appearing without motion is caught by the variance clamp + luma responsiveness.
    // noVel: the whole 3x3 wrote no velocity — diegetic overlays (headline icons, speech bubbles;
    // the sky dome writes velocity via SkyVelocity so it is NOT in this class). Motion evidence is
    // structurally silent there, so cap their trust window and bar them from oscillation locks.
    float noVel = (dmax < dmin) ? 1.0 : 0.0;

    // Rejection authority: scale rejection by COLOR evidence — depth proves the history is stale,
    // color measures how wrong it looks (a parallax reveal is stale by a sub-texel sliver; a mover
    // trail by the mover's whole color). 1:1 keeps full authority.
    float3 hLow = historyRaw;
    float rejAuth = 1.0;
#if SM4 // ps_3_0 temp-register budget (X4505 on OGL) — SM3 keeps full rejection authority, direct clamp
    if (upscaleRatio > 1.5)
    {
        float2 hOfs = InvColorSize * 0.25;
        hLow = (RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2( hOfs.x,  hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2( hOfs.x, -hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2(-hOfs.x,  hOfs.y), 0, 0)).rgb)
              + RGB_to_YCoCg(tex2Dlod(historySampler, float4(histUV + float2(-hOfs.x, -hOfs.y), 0, 0)).rgb)) * 0.25;
        // hLow = render-texel average of the history (reused by the rectification below). Color-silent
        // staleness keeps a 0.3 authority floor; oscillation-proven pixels drop it to 0.05 (color-silent
        // depth rejects there are jitter phase churn, not disocclusion). Relative motion restores
        // authority regardless, so slow trails over oscillation-proven ground still scrub.
        float prevOscE = debugMeta ? 0.0 : saturate((pm.a - 0.5 * step(0.5, pm.a)) / 0.498);
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

    // Ghost-side rejection: history depth NEARER than every valid current tap = the surface that wrote
    // it has left (trailing edge of a mover). Dead-zone epsilon (DepthRejectParams.x) keeps storage
    // quantization alone from firing it.
    float nearer = max(dmin - historyDepth - DepthRejectParams.x, 0.0);
    float ghost = (dmax < dmin) ? 0.0 : saturate(nearer / max(historyDepth, DepthRejectParams.w) * 12.0);
    // Center-depth ghost test: the range test cannot fire while the mover is still inside the 3x3
    // (the stale depth sits at dmin), so test the history against the depth at THIS pixel instead.
    // Softer (slope 8, weight 0.8) — the stored depth is dilated and brushes clean pixels around
    // moving edges. Relative-motion gated: a true trailing band moves relative to its background;
    // a static edge under a pan shares the camera's motion and stays silent.
    float storedFgnPx = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1 - centerVel) * texSize) * VelGatePxScale;
    float relMotion = smoothstep(0.75, 2.5, max(velFgnPx, storedFgnPx));
    float nearerC = (centerDepth >= 0.0) ? max(centerDepth - historyDepth - DepthRejectParams.x, 0.0) : 0.0;
    ghost = max(ghost, saturate(nearerC / max(historyDepth, DepthRejectParams.w) * 8.0) * 0.8 * relMotion);
    // Gate by current OR remembered motion: when a mover exits the 3x3 the dilated velocity drops to
    // zero, but the trailing pixel still remembers the mover's velocity in pm.gb; resting foliage
    // remembers zero and cannot fake this.
    float storedMovePx = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1) * texSize) * VelGatePxScale;
    float storedMove = smoothstep(0.35, 1.5, storedMovePx); // matches moveGate's slow-mover arming
    float ghostReject = max(moveGate, storedMove) * ghost * rejAuth;

    // Feature-level history comparison (structure, not value): a ghost carries edges the current frame
    // does not confirm. Responsive-only (can only increase diff). Motion-adjacent gated — at rest,
    // converged native-res history legitimately out-details the render-res current gradient.
    float featReject = 0.0;
#if SM4 // ps_3_0 temp-register budget (X4505 on OGL) — SM3 relies on the depth/ghost/reactive rejects
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

    // Velocity-disparity reactive (FSR2 lock-break analogue): this frame's dilated velocity vs the
    // velocity stored with the history — catches reveals after the mover left the 3x3, starts/stops,
    // direction changes. Trust modulation only; threshold is resolution-scaled off the encode's LSB.
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

    // Suspicion: union of the detectors — trust gates scale their motion response by evidence, not raw
    // speed (a fast coherent pan reprojects exactly). Foreign is demoted to 0.35 (it fires along every
    // silhouette during parallax pans); ringContam is direct trail evidence, full weight.
    float suspicion = max(max(depthReject, ghostReject), max(max(foreign * 0.35, reactive), ringContam));

    // Luma-oscillation detector (Decima-style anti-fizzle), state in meta.A (sign bit + 7-bit EMA).
    // Fizzle = converged pixel whose curr-vs-history luma delta alternates sign at frame frequency;
    // a ghost's delta is monotonic and cannot produce this. Measured on pre-blend curr vs pre-clamp
    // historyRaw so deeper trust doesn't extinguish its own evidence. Disabled under debugMeta.
    float osc = 0.0;
    float packedA = 0.0;
    if (!debugMeta)
    {
        float prevSgn = step(0.5, pm.a);
        float prevOsc = saturate((pm.a - 0.5 * prevSgn) / 0.498);
        float dl   = curr.x - historyRaw.x; // signed, pre-clamp history
        // Amplitude gate 0.03 keeps low-contrast texture shimmer out of trust-deepening.
        float mag  = step(0.03, abs(dl));
        float sgn  = step(0.0, dl);
        float flip = mag * abs(sgn - prevSgn); // 1 only when a real-amplitude delta reversed sign
        // Witness-gated EMA: off-phase frames neither build nor decay evidence. Build rate rises with
        // upscale past 2x (locks earned in fewer witnessing frames); decay stays at the base rate, so
        // a monotonic ghost (flip = 0 stream) still decays to zero.
        float oscRateUp = 0.15 * lerp(1.0, 2.2, saturate(upscaleRatio - 2.0)); // 0.15 <=0.5x -> 0.33 at 0.33x
        float oscRate = lerp(0.15, oscRateUp, flip); // build boosted, decay base
        osc = lerp(prevOsc, flip, oscRate * testify); // ~6-7 frame EMA on witnessing frames
        // Evidence wipe: locks must be re-earned after any meaningful invalidation, or a ghost rides
        // the old lock. Curved so grazing partial rejects near movers don't nuke valid locks.
        osc *= 1.0 - smoothstep(0.4, 0.85, max(max(depthReject, ghostReject), max(reactive, featReject)));
        // Sign bit MUST stay binary (0 or 1): under TAAU testify is sampleConf, a continuous [0,1] weight,
        // so a lerp here produced a FRACTIONAL sign whenever 0 < mag*testify < 1 — that fraction then
        // aliased into the osc EMA field below (packedA mixes newSgn*0.5 with osc*0.498) and fabricated
        // lock-grade evidence (or lost the sign) on decode next frame. Select instead of lerp: hold the
        // sign through quiet/blind frames (mag*testify <= 0.5) and only adopt the new sign once the
        // weighted evidence tips past half.
        float newSgn = (mag * testify > 0.5) ? sgn : prevSgn;
        packedA = reprojectable ? saturate(newSgn * 0.5 + osc * 0.498) : 0.0; // off-screen = evidence reset
    }

    // Evidence-conditioned accumulation (Kalman-gain counter): N counts witnessed AGREEMENT, not
    // frames. Innovation is normalized by the neighbourhood stddev (expected sampling noise for this
    // content). Agreement grows N; sign-alternating innovation counts as agreement regardless of size
    // (zero-mean noise a one-sided ghost cannot claim); persistent one-sided disagreement collapses N
    // multiplicatively. Hard reset only when off-screen; deliberately NOT zeroed by depthReject (a
    // noisy edge signal that once pinned silhouettes at N=0).
    float inno = abs(curr.x - historyRaw.x) / max(sigma.x, 0.02);
    // Osc protection withdrawn under relative motion so a slow trail over locked ground still
    // collapses N; a static locked pixel keeps full protection.
    float agreeK = max(1.0 - smoothstep(1.0, 2.5, inno), smoothstep(0.12, 0.35, osc) * (1.0 - relMotion));
    // Slight-bias penalty: a faint monotonic ghost has a small one-sided innovation the agreement
    // branch reads as agreement. Dock agreeK only on low-osc + mid-innovation + near-static (slow
    // drift is an innocent one-sided error) + genuinely flat (edge halos have the edge in their own
    // box statistics) pixels. Upscale only; never deepens (min()).
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
    float growK = agreeK * lerp(1.0, lerp(0.3, 1.0, testify), saturate(prevN / 8.0));
    float newN = reprojectable ? min(prevN * collapse + growK, MaxAccum) : 0.0;
    // Shaped resets: only confident rejection collapses the evidence (partial band rejects are constant
    // along moving silhouettes). Ghost-side fully resets; depth/reactive soft-cap.
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
    float GAMMA = TuneGamma * lerp(1.0, 2.0, saturate((upscaleRatio - 1.0) * 0.5));
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
    // Locked widening scales past 2x ratio: a converged thin line is so diluted in its own box
    // statistics at ratio 3 that even 3 sigma clips it on some jitter phases.
    float gammaEff = GAMMA * (1.0 + oscLock * lerp(1.0, 1.6, saturate(upscaleRatio - 2.0)));
    // Rectify, don't reject: mid-strength rejects TIGHTEN the clamp instead of injecting raw — the
    // stale color snaps toward the neighborhood statistics. Raw is reserved for near-certain rejection.
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
    // Input-resolution rectification under TAAU (UE TSR): clamp only the history's render-texel
    // AVERAGE (low component) and apply the correction to the full history — a ghost is wrong in its
    // low component and still corrected; converged sub-pixel detail is zero-mean around it and passes
    // untouched. A wide 2x safety clip bounds the detail component. 1:1 keeps the direct clamp.
    float3 history;
    float lumaHCmp; // history luma FOR THE DIFF COMPARISON — resolution-matched to m1 (see below)
#if SM4 // ps_3_0 temp-register budget (X4505 on OGL) — SM3 always takes the direct-clamp else path
    if (upscaleRatio > 1.5)
    {
        float3 hLowC = ClipAABB(cmin, cmax, hLow);
        history = ClipAABB(m1 - 2.0 * gammaEff * sigma, m1 + 2.0 * gammaEff * sigma, historyRaw + (hLowC - hLow));
        // Resolution-matched diff: a converged thin line's sharp output-res history disagrees with the
        // render-res m1 forever — compare against the history's render-texel average instead.
        lumaHCmp = hLowC.x;
    }
    else
#endif
    {
        history = ClipAABB(cmin, cmax, historyRaw);
        lumaHCmp = history.x;
    }

    // Blend: content-adaptive luminance-feedback weight. The confidence check uses the neighbourhood
    // mean (m1.x), not the raw jittered sample — a single high-frequency tap flips every frame by
    // design and would read as permanent "change". The displayed blend still uses the sharp curr.
    float lumaH = history.x; // display-side luma (Karis weights); the DIFF uses the resolution-matched lumaHCmp
    float diff = saturate(abs(m1.x - lumaHCmp) / max(0.2, max(m1.x, lumaHCmp)));
    diff = max(max(diff, depthReject), max(max(ghostReject, featReject), ringContam));
    // Kalman deep end: once the evidence counter outgrows the EMA baseline, trust follows the Kalman
    // gain N/(N+1) (a fixed EMA floor can never fully average a high-variance jitter cycle). The
    // counter collapses on disagreement and is reset/capped by every disocclusion path.
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
    float deepCap = lerp(0.992, cycleWindow, smoothstep(1.2, 1.8, upscaleRatio));
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

    // Oscillation trust (anti-fizzle action): needs proven sign-alternation, stillness, no
    // disocclusion signal, and low diff. Soft diff gate — thin geometry carries a permanent baseline
    // diff (< ~0.2) from its render-res-diluted mean; trust dies fully at diff ~0.54.
    float oscTrust = oscLock * (1.0 - saturate((diff - 0.25) * 3.5));
    // Cycle-aware ceiling: the deep window must exceed the Halton cycle it hides (72 frames at 1/3
    // scale), so the ceiling scales with JitterPhases. The entry edge slides down to the lock band as
    // render scale drops; native keeps 0.55 (the TV/video partial-trust guard).
    float cycleCeil = clamp(1.0 - 1.0 / (1.2 * JitterPhases), 0.965, 0.99);
    float ceilLo = lerp(0.32, 0.55, floorScale); // 0.55 native -> 0.32 at <= 0.5x, matching the lock entry
    float oscCeil = min(1.0 - 0.5 * BlendFactor, lerp(0.965, cycleCeil, smoothstep(ceilLo, 0.85, osc)));
    // Raise-only: the lock lerp must never pull earned evidence-trust back down.
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

    // TAAU sample confidence (upscale only): weight the current contribution by the nearest sample's
    // kernel proximity — real-sample frames update strongly, interpolation-only frames lean on history.
    if (upscaleRatio > 1.001)
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
        blend *= lerp(lerp(confFloor, 1.0, sampleConf), lerp(0.55, 1.0, sampleConf), moveGate * lerp(0.3, 1.0, suspicion));
    }

    // Warmup ramp: with no accumulated history the buffer is BLACK — seed from the current frame
    // (1, 1/2, 1/3, ...). Keyed off min(prevN, newN): prevN is the evidence that actually exists in
    // the history (frame one must output the raw frame); newN keeps the soft-caps' responsiveness.
    // Lock exemption: a lock cannot coexist with cleared/invalid history (its evidence resets with
    // the meta), so the ramp has no job on a locked pixel.
    blend = max(blend, (1.0 / (min(prevN, newN) + 1.0)) * (1.0 - oscLock));

    // Honest disocclusion (DLSS/FSR2 discard, not fade): a near-certain reject buys the full raw
    // frame; the mid-evidence band is handled by clamp tightening (rejTighten), so grazing partial
    // rejects along dilated depth edges don't inject fractional raw every frame.
    blend = max(blend, smoothstep(0.65, 0.98, max(depthReject, ghostReject)));

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

    // Raw-state spatial softening (upscale only; current-frame data — zero ghost risk): when the
    // floors/rejects force a pixel mostly-raw, display the smooth Mitchell upsample instead of the
    // near-point reconstruction. Converged pixels (low blend) keep the crisp path.
    float3 dispCurr = curr;
#if SM4 // ps_3_0 temp-register budget (X4505 on OGL) — SM3 displays the sharp reconstruction as-is
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
    float wc = blend * (1.0 / (1.0 + max(lumaC, 0.0)));
    float wh = (1.0 - blend) * (1.0 / (1.0 + max(lumaH, 0.0)));
    float3 blended = (dispCurr * wc + history * wh) / max(wc + wh, 1e-5);

    float3 outYCoCg = reprojectable ? blended : dispCurr;

    // Sentinel 2.0 must not survive into an fp16 history alpha — it would read as a permanent
    // depthReject next frame (RGBA8 storage clamped it implicitly; fp16 does not).
    float depthForHistory = min(closestDepth, 1.0);
    // saturate: YCoCg->RGB can slightly overshoot [0,1]; fp16 history would compound it through the
    // feedback loop.
    o.color = float4(saturate(YCoCg_to_RGB(outYCoCg)), depthForHistory);

    if (debugMeta)
    {
        // Diagnostic encode. R must stay the accumulation counter — the next frame's resolve decodes
        // meta.R as prevN in BOTH techniques. G = reject strength, B = effective history trust
        // (1 - blend), A = 0.
        o.meta = float4(newN / MaxAccum, max(depthReject, ghostReject), 1.0 - blend, 0.0);
    }
    else
    {
        // Shipping encode: R = N, GB = this frame's dilated velocity for next frame's disparity
        // reactive, A = packed oscillation state (0 on non-reprojectable = evidence reset).
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
    // the clean supersampled look). Branch-free ramp; native bit-exact at ratio 1.
    float GAMMA = 1.5 * lerp(1.0, 2.0, saturate((upscaleRatio - 1.0) * 0.5));
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
    float moveGate = smoothstep(0.6, 2.0, velPx);

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
    // (capped 0.985), never below the baseline 1-BlendFactor.
    float deepEnd = min(max(1.0 - BlendFactor, minN / (minN + 1.0)), 0.985);
    float historyWeight = lerp(deepEnd, 0.68, diff);

    // Motion-adaptive: lean more on current when moving fast (less lag/ghosting under motion).
    float motionBoost = saturate(length(velocity) * 20.0) * 0.35;
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

    // Sample-confidence injection gate: frames whose kernel barely covers this pixel inject
    // little (their estimate is an amplified tail) — except under motion, where responsiveness
    // wins. Exactly 1 at native (see sampleConf note).
    blend *= lerp(lerp(0.14, 1.0, sampleConf), 1.0, moveGate);
    // Honest disocclusion: strong depth evidence forces the current frame through regardless of
    // accumulated trust.
    blend = max(blend, smoothstep(0.65, 0.98, depthReject));
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
