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
// POINT-filtered view of the history for the packed DEPTH in alpha. Depth must never be bilinearly
// interpolated: at an edge, LINEAR mixes the two surfaces' depths into a value that belongs to neither, and
// the sub-pixel jitter shifts the mixture every frame — the disocclusion test then sees a spurious "outside
// the neighbourhood range" depth at every silhouette and permanently resets accumulation there.
sampler historyDepthSampler = sampler_state {
    texture = <historyTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

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

    // 5-tap Catmull-Rom (Karis optimization): the 4 corner taps carry the least weight — drop them and
    // renormalize by the weight actually used. Visually near-identical to the 9-tap form at ~half the
    // history fetch cost (part of the resolve fetch diet; the resolve was TAA's hot spot vs MSAA 8x).
    float3 r = float3(0, 0, 0);
    r += tex2Dlod(historySampler, float4(tp12.x, tp0.y,  0, 0)).rgb * (w12.x * w0.y);
    r += tex2Dlod(historySampler, float4(tp0.x,  tp12.y, 0, 0)).rgb * (w0.x  * w12.y);
    r += tex2Dlod(historySampler, float4(tp12.x, tp12.y, 0, 0)).rgb * (w12.x * w12.y);
    r += tex2Dlod(historySampler, float4(tp3.x,  tp12.y, 0, 0)).rgb * (w3.x  * w12.y);
    r += tex2Dlod(historySampler, float4(tp12.x, tp3.y,  0, 0)).rgb * (w12.x * w3.y);
    float wtotal = (w12.x * w0.y) + (w0.x * w12.y) + (w12.x * w12.y) + (w3.x * w12.y) + (w12.x * w3.y);
    return clamp(r / wtotal, 0.0, 8.0); // renormalize + clamp ringing undershoot / fp16 overflow insurance
}

struct TAAOut
{
    float4 color : COLOR0; // displayed frame + next frame's history (RGB), dilated depth in A
    // Meta layout (RGBA8): R = new accumulation count N (N/MaxAccum), GB = this frame's dilated velocity
    // encoded v*10+0.5 (exact: all velocity writers clamp to +/-0.05 UV), A = packed luma-oscillation state
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
    // output-resolution detail. kscale = 1 at native (bit-identical weights to before); clamped at 2 so
    // samples stay reachable at extreme scales (0.33x).
    float kscale = min(InvColorSize.x / InvScreenSize.x, 2.0);
    // Tap k in {-1,0,1} sits at distance fracd + k; at 1:1, fracd == the jitter shift (old jShiftPx).
    float3 kx3 = float3(MitchellK(abs(fracd.x - 1.0) * kscale), MitchellK(abs(fracd.x) * kscale), MitchellK(abs(fracd.x + 1.0) * kscale));
    float3 ky3 = float3(MitchellK(abs(fracd.y - 1.0) * kscale), MitchellK(abs(fracd.y) * kscale), MitchellK(abs(fracd.y + 1.0) * kscale));
    float3 m1 = 0, m2 = 0;
    float3 filt = 0;
    float wsum = 0;
    float3 crawC = 0; // the raw nearest jittered sample (center recon tap) — see texture-detail lean below
    float2 dilatedVel = float2(0, 0);
    float2 centerVel = float2(0, 0); // this pixel's OWN velocity (un-dilated) — foreign-velocity reactive
    float closestDepth = 1e9;
    float closestMask = 0.0;
    float dmin = 1e9, dmax = -1e9; // valid-tap depth RANGE for the disocclusion test below
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float2 ofs = float2(dx, dy) * InvColorSize; // neighborhood spans INPUT texels
        // FETCH DIET (perf: the resolve was ~38 fetches/px): the variance box and the velocity dilation use
        // the 5-tap PLUS pattern instead of the full 3x3 — the reference-sanctioned reduction (Playdead's
        // cross; corner contribution to the statistics/dilation is marginal). The [unroll]'d literal test
        // compiles the corner taps out entirely. The RECONSTRUCTION keeps all 9 taps (kernel quality).
        if (dx == 0 || dy == 0)
        {
            float3 c = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + ofs, 0, 0)).rgb);
            m1 += c;
            m2 += c * c;

            float4 v = tex2Dlod(velocitySampler, float4(uv + ofs, 0, 0));
            // "No velocity written" -> depth sentinel 2.0 (beyond valid [0,1]) so genuinely-far valid
            // pixels still win the nearest-depth tiebreak over unwritten neighbours.
            float d = (v.a >= 0.5) ? v.b : 2.0;
            if (d < closestDepth) { closestDepth = d; dilatedVel = v.rg; closestMask = v.a; }
            if (v.a >= 0.5) { dmin = min(dmin, v.b); dmax = max(dmax, v.b); }
            if (dx == 0 && dy == 0) centerVel = v.rg; // folds under [unroll]; unwritten decodes as zero
        }
        // Reconstruction tap: RAW texel center (bilinear at an exact center = point fetch) around the
        // nearest jittered sample, weighted by its true distance to the output pixel center.
        float2 tapUV = (baseTexel + float2(dx, dy) + 0.5) * InvColorSize;
        float3 craw = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(tapUV, 0, 0)).rgb);
        float w = kx3[dx + 1] * ky3[dy + 1];
        filt += craw * w;
        wsum += w;
        if (dx == 0 && dy == 0) crawC = craw; // folds under [unroll]
    }
    m1 *= (1.0 / 5.0);
    m2 *= (1.0 / 5.0);
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0));
    // Thin-coverage fallback: with the output-sized kernel, some frames leave an output pixel with almost
    // no in-support sample (wsum ~ 0). Divide-guard + smooth fallback to the stationary bilinear estimate
    // at the content-aligned position (boxUV); sample confidence already keeps those pixels history-leaning,
    // so the fallback only ever feeds the small current-frame share. No-op at native (wsum ~ 1 there).
    float3 stationaryC = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV, 0, 0)).rgb);
    float3 curr = lerp(stationaryC, filt / max(wsum, 1e-4), saturate(wsum / 0.15));

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

    // Reproject with the dilated velocity (+ jitter delta cancels the jitter baked into the velocity buffer).
    // NO velocity-validity gate: the buffer is un-jittered now, so "velocity never written" decodes as zero
    // velocity = identity reproject — exactly right for static content (2D/backdrop art, alpha fringes that
    // skip the velocity MRT). Gating on the mask made every such pixel output the raw jittered frame forever.
    // Content that moves without writing velocity is caught by the variance clamp + luma feedback instead.
    float2 velocity = dilatedVel;
    float velPx = length(velocity * texSize) * VelGatePxScale; // NATIVE px (see VelGatePxScale)
    // 0.35..1.5 (was 0.5..2.0): slow movers are the worst haze producers — they spend the most frames
    // contaminating each pixel — so the rejects must arm at slow-walk speeds too. Rest (zero velocity,
    // un-jittered buffer) is still structurally below the gate.
    float moveGate = smoothstep(0.35, 1.5, velPx);
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
    float2 reproVel = lerp(velocity, centerVel, foreign);
    float vmag = length(velocity);
    float2 histUV = uv - reproVel + JitterDelta;
    bool reprojectable = (histUV.x >= 0) && (histUV.x <= 1) && (histUV.y >= 0) && (histUV.y <= 1);

    // History fetch (bicubic for detail) + a POINT tap for the packed depth in alpha (see sampler comment).
    float4 historyPoint = tex2Dlod(historyDepthSampler, float4(histUV, 0, 0));
    float3 historyRaw = RGB_to_YCoCg(SampleHistoryBicubic(histUV));

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
    float historyDepth = historyPoint.a;
    float outside = max(max(dmin - historyDepth, historyDepth - dmax), 0.0);
    float depthReject = (dmax < dmin) ? 0.0 :
        saturate((outside / max(historyDepth, DepthRejectParams.w)) * DepthRejectParams.y - DepthRejectParams.z);
    depthReject *= moveGate;

    // --- GHOST-SIDE REJECTION (the disocclusion centrepiece): fires only on the GHOST side — history depth
    //     NEARER than every valid current tap = the surface that wrote it has left (trailing edge of a mover).
    //     Dead-zone epsilon (DepthRejectParams.x) keeps storage quantization alone from ever firing it. ---
    float nearer = max(dmin - historyDepth - DepthRejectParams.x, 0.0);
    float ghost = (dmax < dmin) ? 0.0 : saturate(nearer / max(historyDepth, DepthRejectParams.w) * 12.0); // 8 -> 12: crisper full-strength rejection (partial rejects = partial haze)
    // Gated by CURRENT motion OR REMEMBERED motion (the stored meta velocity). Current-only gating had a
    // one-frame timing hole that made mover haze un-scrubbable: the instant the mover exits this pixel's
    // 3x3, dilated velocity drops to zero -> moveGate closes -> the ghost-depth evidence (history depth =
    // the mover's, provably nearer than the whole current range) could never fire, and the contaminated
    // color was left to the slow diff decay — the persistent object haze (debug view: haze pixels showed NO
    // green). A trailing-reveal pixel REMEMBERS the mover's velocity from last frame in pm.gb; resting
    // foliage (the reason the motion gate exists) remembers zero, so it cannot fake this signal.
    float storedMovePx = debugMeta ? 0.0 : length(((pm.gb - 0.5) * 0.1) * texSize) * VelGatePxScale;
    float storedMove = smoothstep(0.35, 1.5, storedMovePx); // matches moveGate's slow-mover arming
    float ghostReject = max(moveGate, storedMove) * ghost;

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
        osc = lerp(prevOsc, flip, 0.15);       // ~6-7 frame EMA (0.125 locked too slowly at low render scales,
                                               // where real samples arrive rarely and evidence builds unevenly)
        float newSgn = lerp(prevSgn, sgn, mag); // hold the sign bit through quiet frames
        packedA = reprojectable ? saturate(newSgn * 0.5 + osc * 0.498) : 0.0; // off-screen = evidence reset
    }

    // --- ACCUMULATION COUNTER: grows +1 per frame (cap MaxAccum); hard-resets only when history is off-
    //     screen. Deliberately NOT zeroed by depthReject (noisy edge signal pinned silhouettes at N=0).
    //     Ghost/reactive events SOFT-CAP it instead. Its job: the WARMUP RAMP in the blend section — at low
    //     N the pixel takes mostly-current (raw image first, detail builds on top) instead of blending in
    //     the cleared-black history (which looked darkened/blurry until filled). It does NOT deepen trust
    //     past the baseline blend (that direction ghosted in every variant tried). ---
    float newN = reprojectable ? min(prevN + 1.0, MaxAccum) : 0.0;
    // Ghost-side reject now RESETS the counter (was a soft-cap to 2): the surface that wrote the history has
    // provably left, so the honest treatment is the same as off-screen — raw current, then the warmup ramp
    // rebuilds (1 -> 1/2 -> 1/3...). The old cross-fade left a hazy 2-3 frame ghost mix, chunky at low res.
    newN = lerp(newN, 0.0, ghostReject);
    newN = lerp(newN, min(newN, 6.0), depthReject);
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
    const float GAMMA = 1.5;
    // FSR2-style "LOCK" via the oscillation signal (fine-geometry stability, matters most under TAAU): the
    // clamp box is built from RENDER-res taps, but the converged history holds OUTPUT-res detail — a thin
    // line that is sub-pixel at render res is DILUTED in the box statistics, so the box hugs the diluted
    // mean and the clamp erodes the converged sharp feature every frame (the dim/flicker cycling on fine
    // geometry). On pixels with PROVEN sign-alternation (a ghost is monotonic — it cannot earn this), ~zero
    // velocity, and no disocclusion signals, widen the box so the locked history passes through intact.
    // Ghost-safe by the exact argument that admitted the oscillation trust gate; every gate that breaks a
    // lock in FSR2 (motion, disocclusion, velocity disparity) breaks it here too.
    float stillGate = 1.0 - smoothstep(0.25, 0.5, velPx);
    // Lock threshold 0.32 (was 0.4): at low render scales the oscillation evidence builds unevenly (real
    // samples land on a given output pixel only on some phases), so fine geometry hovered under the lock
    // forever — the residual low-scale fizzle. TV-static-like content (~0.5 osc equilibrium) gains a bit
    // more partial trust as the cost; still clamp-bounded.
    float oscLock = smoothstep(0.32, 0.7, osc) * stillGate
                  * (1.0 - depthReject) * (1.0 - ghostReject) * (1.0 - reactive) * (1.0 - foreign);
    float gammaEff = GAMMA * (1.0 + oscLock); // up to 3 sigma on locked pixels (exonerated by the control build)
    float3 cmin = m1 - gammaEff * sigma;
    float3 cmax = m1 + gammaEff * sigma;
    float3 history = ClipAABB(cmin, cmax, historyRaw);

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
    float lumaH = history.x;
    float diff = saturate(abs(m1.x - lumaH) / max(0.2, max(m1.x, lumaH)));
    // NOTE (sand at low scale): a "noise-floor knee" was tried here — subtracting an expected sampling-noise
    // baseline from diff at upscale so converged noisy textures (sand) stop resetting their own accumulation.
    // REVERTED in every variant (raw, reject-split, motion-gated, oscillation-earned): in the small-delta
    // regime a slight ghost residue over a textured surface is indistinguishable from texture sampling noise
    // by ANY resolve-side signal (magnitude, motion, even sign-alternation — the texture's noise rides on top
    // of the residue), so the knee always slowed ghost cleanup somewhere (haze around mover silhouettes).
    // Sand detail at low scale is an INPUT-side problem (terrain-noise mip bias), not a trust-side one.
    diff = max(max(diff, depthReject), ghostReject);
    float historyWeight = lerp(1.0 - BlendFactor, 0.55, diff); // responsive end 0.6 -> 0.55: a full-diff pixel scrubs a shade faster
    // Velocity-disparity reactive caps the history trust directly (soft — 0.88 keeps a moving-content pixel
    // from pulsing aliased when the camera stops; tune toward 0.94 if a screen-wide stop-pulse shows).
    historyWeight = min(historyWeight, lerp(1.0, 0.85, reactive)); // 0.88 -> 0.85 (watch for a screen-wide pulse on camera stop; revert to 0.88 if seen)
    // Foreign-velocity trust cap — MILD only: the reprojection fix above already makes ring-pixel history
    // valid background (a hard 0.75 cap here just re-created raw jitter crunch on the ring). This is a
    // safety net for imperfect own-velocity (e.g. unwritten alpha fringes decoding as zero).
    historyWeight = min(historyWeight, lerp(1.0, 0.92, foreign));

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
    float oscCeil = min(1.0 - 0.5 * BlendFactor, 0.965); // 0.975+ let slight ghosting linger on locked pixels
    historyWeight = lerp(historyWeight, oscCeil, oscTrust);

    float motionBoost = saturate(vmag * 20.0) * 0.35; // more current when moving fast (less ghosting)
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

    // --- TAAU SAMPLE CONFIDENCE (upscale mode only — the standard temporal-upscaler mechanism). At render
    //     scale < 1, an output pixel's NEAREST real sample is sometimes dead-center and sometimes ~a full
    //     render texel away; on the far frames the reconstruction is pure interpolation, and blending it at
    //     full weight injects per-frame wobble (the residual TAAU flicker vs MSAA). Weight the current
    //     contribution by the nearest sample's kernel proximity: real-sample frames update strongly,
    //     in-between frames lean on the history that already integrated real samples from other jitter
    //     phases. At 1:1 every frame is a complete estimate, so this is OFF there (design-review verdict);
    //     under camera motion it's faded out (moveGate) so responsiveness/anti-ghosting are untouched. ---
    float upscaleRatio = InvColorSize.x / InvScreenSize.x; // outputRes / renderRes, > 1 under TAAU
    if (upscaleRatio > 1.001)
    {
        float sampleConf = saturate(kx3[1] * ky3[1] * 1.2656); // center-tap weight / k(0)^2: 1 = sample on-pixel
        // Floor 0.14 (0.35 -> 0.25 -> 0.18 -> 0.14 as the kernel sharpened): with the output-sized kernel,
        // off-frames carry almost no real information for this pixel — injecting less of them disturbs
        // converged fine geometry less (the residual TAAU-only fizzle), and the motion gate still restores
        // full responsiveness the moment anything moves.
        blend *= lerp(lerp(0.14, 1.0, sampleConf), 1.0, moveGate);
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
    blend = max(blend, 1.0 / (min(prevN, newN) + 1.0));

    // HONEST DISOCCLUSION (reference-upscaler behavior — DLSS/FSR2 discard, not fade): a positively
    // identified disocclusion means the history is INVALID, and blending any of it is wrong by construction
    // — the old path bottomed out at ~40% current per frame (the diff floor), cross-fading provably-stale
    // color over 2-3 frames: the lingering ghost/haze mix on reveals, chunky at low render scale. Reject
    // strength now buys current-frame weight directly: a full reject shows the raw frame immediately (the
    // "honest raw" reveal) and the counter reset above makes the next frames rebuild through the warmup
    // ramp. Placed after the warmup floor so nothing downstream re-attenuates it.
    blend = max(blend, max(depthReject, ghostReject));

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
    blend = max(blend, texDetail * 0.28);

    // Anti-flicker (Karis): inverse-luma weighting so bright sub-pixel samples don't dominate/sparkle.
    float wc = blend * (1.0 / (1.0 + max(lumaC, 0.0)));
    float wh = (1.0 - blend) * (1.0 / (1.0 + max(lumaH, 0.0)));
    float3 blended = (curr * wc + history * wh) / max(wc + wh, 1e-5);

    float3 outYCoCg = reprojectable ? blended : curr;

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
