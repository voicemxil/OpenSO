// TAA.fx — temporal anti-aliasing for OpenSO 3D mode.
//
// Reads:
//   colorTex       — this frame's rendered color (post-scale-resolve, pre-blur).
//   historyTex     — previous frame's TAA output (RGB) + packed dilated depth (A), velocity-reprojected.
//   metaHistoryTex — previous frame's meta: R = accumulation count N (N/MaxAccum), GB = previous dilated
//                    velocity (v*10+0.5), A = reserved instability EMA. Velocity-reprojected too.
//   velocityTex    — per-pixel screen-space velocity (.rg) + normalized linear depth (.b) + valid mask (.a).
//
// Outputs to TWO render targets:
//   COLOR0 — displayed frame + next frame's history (RGB) with this pixel's dilated depth packed in A
//            (fp16 target when available — see PPXDepthEngine.HistoryIsFP16 / DepthRejectParams).
//   COLOR1 — next frame's meta: R = new N, GB = dilated velocity encode, A = reserved (TAADebug technique
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

float2 InvScreenSize;
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
    return clamp(r, 0.0, 8.0); // clamp ringing undershoot + fp16-history overflow insurance
}

struct TAAOut
{
    float4 color : COLOR0; // displayed frame + next frame's history (RGB), dilated depth in A
    // Meta layout (RGBA8): R = new accumulation count N (N/MaxAccum), GB = this frame's dilated velocity
    // encoded v*10+0.5 (exact: all velocity writers clamp to +/-0.05 UV), A = reserved luma-instability EMA
    // (written 0 for now, 1 on non-reprojectable). The TAADebug technique repurposes GB for diagnostics and
    // correspondingly DISABLES the GB-consuming logic (self-consistent while debugging).
    float4 meta  : COLOR1;
};

// Shared TAA core. debugMeta is a compile-time uniform bool (folded per-technique): when true, meta.GB
// carries diagnostics (G = reject strength, B = non-reprojectable) instead of prev-velocity, and the
// velocity-disparity reactive is forced off so we never decode last frame's debug bytes as a velocity.
TAAOut TAA_Core(VSOut input, uniform bool debugMeta)
{
    TAAOut o;
    float2 uv = input.Coord;

    // 3x3 neighborhood pass: FILTERED current sample (reference-standard reconstruction), variance stats
    // (m1,m2) for the clamp AABB, and VELOCITY DILATION (reproject with the nearest-depth motion vector so
    // thin/edge foreground objects don't ghost). The COLOUR/box taps read at the UN-jittered position (see
    // SampleJitterUV) so the clamp box holds still frame-to-frame; the VELOCITY/depth taps stay at the true
    // pixel (the velocity buffer is indexed by rendered pixel).
    //
    // FILTERED INPUT (UE/Karis Blackman-Harris-style, Decima-adjacent): the sample fed to the blend is a
    // jitter-aware weighted average of the 3x3, NOT one raw jittered tap. On sub-pixel geometry (grass
    // blades, leaves, thin lines) a raw tap is a coin flip — blade or sky, 100% different every frame,
    // forever — which no safe history depth can average away; that unbounded per-frame input variance was
    // the root of the foliage flicker every trust-widening attempt failed to fix. Filtering the input
    // collapses the variance BEFORE the blend, so the normal accumulation window suffices. Spatial filtering
    // of the current frame only — zero extra history trust, so none of the ghosting failure modes apply.
    // Slight softening is by design and reclaimed by the RCAS pass that follows TAA (the reference pairing).
    // Because the taps are already positioned on the un-jittered content grid (boxUV), the weights are just
    // a function of the integer offsets; hardware bilinear handles the sub-pixel placement.
    float2 boxUV = uv - SampleJitterUV;
    float3 m1 = 0, m2 = 0;
    float3 filt = 0;
    float wsum = 0;
    float2 dilatedVel = float2(0, 0);
    float closestDepth = 1e9;
    float closestMask = 0.0;
    float dmin = 1e9, dmax = -1e9; // valid-tap depth RANGE for the disocclusion test below
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float2 ofs = float2(dx, dy) * InvScreenSize;
        float3 c = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(boxUV + ofs, 0, 0)).rgb);
        m1 += c;
        m2 += c * c;
        // Separable Mitchell-Netravali (B=C=1/3) reconstruction weights on the integer tap grid:
        // k(0)=0.8889, k(1)=0.0556 -> center 0.790 / edge 0.049 / corner 0.003 (sums to 1). Sharper than
        // the Gaussian/Blackman approximation (center 0.69) with all-positive weights at this support.
        float kx = (dx == 0) ? 0.8889 : 0.0556;
        float ky = (dy == 0) ? 0.8889 : 0.0556;
        float w = kx * ky;
        filt += c * w;
        wsum += w;

        float4 v = tex2Dlod(velocitySampler, float4(uv + ofs, 0, 0));
        // "No velocity written" -> depth sentinel 2.0 (beyond valid [0,1]) so genuinely-far valid pixels
        // still win the nearest-depth tiebreak over unwritten neighbours.
        float d = (v.a >= 0.5) ? v.b : 2.0;
        if (d < closestDepth) { closestDepth = d; dilatedVel = v.rg; closestMask = v.a; }
        if (v.a >= 0.5) { dmin = min(dmin, v.b); dmax = max(dmax, v.b); }
    }
    m1 *= (1.0 / 9.0);
    m2 *= (1.0 / 9.0);
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0));
    float3 curr = filt / wsum; // the filtered current sample (see comment above)

    // Reproject with the dilated velocity (+ jitter delta cancels the jitter baked into the velocity buffer).
    // NO velocity-validity gate: the buffer is un-jittered now, so "velocity never written" decodes as zero
    // velocity = identity reproject — exactly right for static content (2D/backdrop art, alpha fringes that
    // skip the velocity MRT). Gating on the mask made every such pixel output the raw jittered frame forever.
    // Content that moves without writing velocity is caught by the variance clamp + luma feedback instead.
    float2 velocity = dilatedVel;
    float vmag = length(velocity);
    float2 histUV = uv - velocity + JitterDelta;
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
    float2 texSize = 1.0 / InvScreenSize;
    float velPx = length(velocity * texSize);
    float moveGate = smoothstep(0.5, 2.0, velPx);

    float historyDepth = historyPoint.a;
    float outside = max(max(dmin - historyDepth, historyDepth - dmax), 0.0);
    float depthReject = (dmax < dmin) ? 0.0 :
        saturate((outside / max(historyDepth, DepthRejectParams.w)) * DepthRejectParams.y - DepthRejectParams.z);
    depthReject *= moveGate;

    // --- GHOST-SIDE REJECTION (the disocclusion centrepiece): fires only on the GHOST side — history depth
    //     NEARER than every valid current tap = the surface that wrote it has left (trailing edge of a mover).
    //     Dead-zone epsilon (DepthRejectParams.x) keeps storage quantization alone from ever firing it. ---
    float nearer = max(dmin - historyDepth - DepthRejectParams.x, 0.0);
    float ghost = (dmax < dmin) ? 0.0 : saturate(nearer / max(historyDepth, DepthRejectParams.w) * 8.0);
    float ghostReject = moveGate * ghost;

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
        float lsbPx = 3.922e-4 * texSize.x;               // one 8-bit LSB of the encode, in pixels
        float dispLo = max(1.5, 2.0 * lsbPx);             // resolution-scaled noise floor
        float velDispPx = length((velocity - prevVel) * texSize);
        reactive = smoothstep(dispLo, dispLo + 4.5, velDispPx);
    }

    // --- ACCUMULATION COUNTER: grows +1 per frame (cap MaxAccum); hard-resets only when history is off-
    //     screen. Deliberately NOT zeroed by depthReject (noisy edge signal pinned silhouettes at N=0).
    //     Ghost/reactive events SOFT-CAP it instead: low N keeps the pixel responsive AND collapses conf
    //     below, snapping the variance clamp back to tight (1.3 sigma) exactly where contaminated history
    //     needs scrubbing — while off-screen N=0 stays a true reset. ---
    float newN = reprojectable ? min(prevN + 1.0, MaxAccum) : 0.0;
    newN = lerp(newN, min(newN, 2.0), ghostReject);
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
    float3 cmin = m1 - GAMMA * sigma;
    float3 cmax = m1 + GAMMA * sigma;
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
    diff = max(max(diff, depthReject), ghostReject);
    float historyWeight = lerp(1.0 - BlendFactor, 0.6, diff);
    // Velocity-disparity reactive caps the history trust directly (soft — 0.88 keeps a moving-content pixel
    // from pulsing aliased when the camera stops; tune toward 0.94 if a screen-wide stop-pulse shows).
    historyWeight = min(historyWeight, lerp(1.0, 0.88, reactive));

    float motionBoost = saturate(vmag * 20.0) * 0.35; // more current when moving fast (less ghosting)
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

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
        // Diagnostic encode for the accumulation debug blit: red = converged N, green = reject strength
        // (depth or ghost), blue = non-reprojectable. GB-consuming logic is disabled in this technique, so
        // next frame never decodes these bytes as a velocity.
        o.meta = float4(newN / MaxAccum, max(depthReject, ghostReject), reprojectable ? 0.0 : 1.0, 1.0);
    }
    else
    {
        // Shipping encode: R = N, GB = this frame's dilated velocity for next frame's disparity reactive,
        // A = reserved instability EMA (0 for now; 1 flags non-reprojectable for the future consumer).
        o.meta = float4(newN / MaxAccum,
                        velocity.x * 10.0 + 0.5,
                        velocity.y * 10.0 + 0.5,
                        reprojectable ? 0.0 : 1.0);
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
