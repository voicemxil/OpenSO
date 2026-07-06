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
// Point-filtered view of the history for the packed depth in alpha: bilinear would mix surface
// depths at edges and permanently trip the disocclusion test on silhouettes.
sampler historyDepthSampler = sampler_state {
    texture = <historyTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

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

// Catmull-Rom (bicubic) history sampling — preserves high frequencies across reprojection so the
// jittered samples build a sharp supersampled image (plain bilinear would low-pass every frame).
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
    // Tap k in {-1,0,1} sits at distance fracd + k.
    float3 kx3 = float3(MitchellK(abs(fracd.x - 1.0) * kscaleEff), MitchellK(abs(fracd.x) * kscaleEff), MitchellK(abs(fracd.x + 1.0) * kscaleEff));
    float3 ky3 = float3(MitchellK(abs(fracd.y - 1.0) * kscaleEff), MitchellK(abs(fracd.y) * kscaleEff), MitchellK(abs(fracd.y + 1.0) * kscaleEff));
    // Render-texel-scale weights (kscale 1) for the SOFT display reconstruction (see loop).
    // SM4 only: this and several other register-heavy paths below overflow ps_3_0's temp registers
    // (X4505 on OGL); SM3 runs the lean classic resolve.
#if SM4
    float3 kx1 = float3(MitchellK(abs(fracd.x - 1.0)), MitchellK(abs(fracd.x)), MitchellK(abs(fracd.x + 1.0)));
    float3 ky1 = float3(MitchellK(abs(fracd.y - 1.0)), MitchellK(abs(fracd.y)), MitchellK(abs(fracd.y + 1.0)));
    float3 filtSoft = 0;
    float wsumSoft = 0;
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
#endif
        float w = kx3[dx + 1] * ky3[dy + 1];
        if (edgeAniso > 0.001)
        {
            float2 vtap = fracd + float2(dx, dy);
            float dAcross = dot(vtap, en);
            float dAlong = dot(vtap, et);
            float wAni = MitchellK(sqrt(dAcross * dAcross + dAlong * dAlong * 0.25) * kscaleEff);
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
    float3 curr = lerp(stationaryC, filt / max(wsum, 1e-4), saturate(wsum / (0.15 * kscale)));

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
    // Slow drift keeps its accumulation; walking speed (~2-4 px/f) arms rejection fully.
    float moveGate = smoothstep(0.6, 2.0, velPx);
    // Foreign velocity: nearest-depth dilation gives the ring of background pixels around a mover's
    // silhouette the mover's velocity. Suspicion signal / trust cap / lock exclusion only.
    float velFgnPx = length((velocity - centerVel) * texSize) * VelGatePxScale;
    float foreign = smoothstep(0.75, 2.5, velFgnPx);
    float vmag = length(velocity);
    // Split reprojection: COLOR reprojects with the dilated velocity (coherent edges — the DLSS/FSR2
    // standard); the DEPTH/STRUCTURE tests reproject with the pixel's OWN velocity, since at a
    // silhouette the dilated velocity is the foreground's and would misfire the tests.
    float2 histUV = uv - velocity + JitterDelta;
    float2 ownVel = (centerDepth >= 0.0) ? centerVel : velocity;
    float2 histUVDepth = uv - ownVel + JitterDelta;
    bool reprojectable = (histUV.x >= 0) && (histUV.x <= 1) && (histUV.y >= 0) && (histUV.y <= 1);

    // History fetch (bicubic for detail) + a POINT tap for the packed depth in alpha (see sampler comment).
    float4 historyPoint = tex2Dlod(historyDepthSampler, float4(histUVDepth, 0, 0)); // own-velocity anchor
    float3 historyRaw = RGB_to_YCoCg(SampleHistoryBicubic(histUV));

    // Ring-contamination signal: a background ring pixel can keep foreground-trail color the
    // own-velocity structural tests are blind to. Measure own-anchored vs dilated-anchored history
    // color directly, gated by foreign (clean pans stay silent). Upscale only; feeds diff + suspicion.
    float ringContam = 0.0;
#if SM4 // ps_3_0 temp-register budget (X4505 on OGL) — SM3 runs without this signal
    if (!debugMeta && upscaleRatio > 1.001)
    {
        float3 histOwn = RGB_to_YCoCg(historyPoint.rgb);
        ringContam = foreign * smoothstep(0.04, 0.15, length(histOwn - historyRaw));
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
        float hE = dot(tex2Dlod(historyDepthSampler, float4(histUVDepth + float2(InvScreenSize.x, 0), 0, 0)).rgb, lw);
        float hW = dot(tex2Dlod(historyDepthSampler, float4(histUVDepth - float2(InvScreenSize.x, 0), 0, 0)).rgb, lw);
        float hS = dot(tex2Dlod(historyDepthSampler, float4(histUVDepth + float2(0, InvScreenSize.y), 0, 0)).rgb, lw);
        float hN = dot(tex2Dlod(historyDepthSampler, float4(histUVDepth - float2(0, InvScreenSize.y), 0, 0)).rgb, lw);
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

    // Center tap's kernel weight normalized by the kernel peak (separable k(0)^2 = 0.7901, radial
    // k(0) = 0.8889). WITNESS RULE: under TAAU, off-phase frames are interpolation, not observation —
    // they may not testify against history nor build/decay oscillation evidence.
    float sampleConf = saturate(wC * lerp(1.2656, 1.125, edgeAniso));
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
        float newSgn = lerp(prevSgn, sgn, mag * testify); // hold the sign bit through quiet/blind frames
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
    agreeK = min(agreeK, 1.0 - 0.35 * biasPenalty);
    float collapse = lerp(1.0, lerp(0.75, 1.0, agreeK), testify);
    // Growth is witness-gated only once evidence exists: a fresh pixel counts every frame, so
    // rebuilding after reveals isn't throttled; the off-phase discount fades in with N.
    float growK = agreeK * lerp(1.0, lerp(0.3, 1.0, testify), saturate(prevN / 8.0));
    float newN = reprojectable ? min(prevN * collapse + growK, MaxAccum) : 0.0;
    // Shaped resets: only confident rejection collapses the evidence (partial band rejects are constant
    // along moving silhouettes). Ghost-side fully resets; depth/reactive soft-cap.
    newN = lerp(newN, 0.0, smoothstep(0.35, 0.85, ghostReject));
    newN = lerp(newN, min(newN, 6.0), smoothstep(0.3, 0.8, depthReject));
    newN = lerp(newN, min(newN, 8.0), reactive);

    float lumaC = curr.x;                   // Y in YCoCg (current center sample)

    // Variance clamp: fixed tight gamma — confidence-widened boxes reintroduced ghosting (a luma test
    // cannot tell "genuinely stable" from "ghost of similar brightness").
    const float GAMMA = 1.5;
    // FSR2-style LOCK via the oscillation signal: the clamp box is render-res but converged history
    // holds output-res detail, so the box erodes converged sub-pixel features — widen it on pixels
    // with proven sign-alternation, ~zero velocity, and no disocclusion signals. Suspicion-scaled
    // velocity: coherent motion counts at 40% speed, so locks ride through clean pans.
    float stillGate = 1.0 - smoothstep(0.8, 2.0, velPx * lerp(0.4, 1.0, suspicion));
    // Lock entry eases to 0.24 at low render scale (evidence builds unevenly when real samples are rare).
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
    // Deep-end cap at upscale = the cycle-hiding window (~1.2x the Halton cycle): extra depth buys
    // nothing visible but preserves faint residue after motion evidence is gone. Native keeps 0.992.
    float cycleWindow = clamp(1.0 - 1.0 / (1.2 * JitterPhases), 0.965, 0.99);
    float deepCap = lerp(0.992, cycleWindow, smoothstep(1.2, 1.8, upscaleRatio));
    float deepEnd = min(max(1.0 - BlendFactor, minN / (minN + 1.0)), deepCap);
    // Responsive end 0.68 (~32%/frame): the structural rejects own disocclusion duty and enter this
    // same lerp at full strength through the max() above; pure-luma responsiveness is gentler.
    float historyWeight = lerp(deepEnd, 0.68, diff);
    // Trust caps: reactive (soft — avoids an aliased pulse on camera stop); foreign (mild — a safety
    // net for imperfect own velocity); velocityless overlays (~8-frame window, fresh animation).
    historyWeight = min(historyWeight, lerp(1.0, 0.85, reactive));
    historyWeight = min(historyWeight, lerp(1.0, 0.92, foreign));
    historyWeight = min(historyWeight, lerp(1.0, 0.88, noVel));

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

    // Evidence-gated motion boost: full only where something is actually suspicious.
    float motionBoost = saturate(vmag * 20.0) * 0.22 * lerp(0.35, 1.0, suspicion);
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

    // TAAU sample confidence (upscale only): weight the current contribution by the nearest sample's
    // kernel proximity — real-sample frames update strongly, interpolation-only frames lean on history.
    if (upscaleRatio > 1.001)
    {
        // Coverage-scaled floor: a frame with no information contributes nothing. Drops toward 0.08
        // past 2x ratio, where 8 of 9 frames are interpolation-only.
        float confFloor = lerp(0.14, 0.08, saturate(upscaleRatio - 2.0)) * saturate(wsum / (0.3 * kscale));
        // Motion-scaled, not motion-disabled: off-phase frames inject at 55% under motion and lean the
        // rest on reprojected history; regime keyed on suspicion-scaled motion so coherent pans keep
        // their AA. Every reject path still overrides from below.
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

    // Texture-detail blend floor: keep low-variance texture regions responsive (~3-4 frame window) so
    // single-texel detail survives the temporal mean. All scales — it doubles as the anti-ghost
    // backstop on flat surfaces. Lock bypass: proven-oscillation pixels escape (semi-uniform fine
    // detail can read low-variance at render res); ghost residue is monotonic and cannot earn the lock.
    blend = max(blend, texDetail * 0.28 * (1.0 - oscLock));

    // Raw-state spatial softening (upscale only; current-frame data — zero ghost risk): when the
    // floors/rejects force a pixel mostly-raw, display the smooth Mitchell upsample instead of the
    // near-point reconstruction. Converged pixels (low blend) keep the crisp path.
    float3 dispCurr = curr;
#if SM4 // ps_3_0 temp-register budget (X4505 on OGL) — SM3 displays the sharp reconstruction as-is
    if (upscaleRatio > 1.001)
    {
        float rawSoften = saturate((blend - 0.22) * 1.6);
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
