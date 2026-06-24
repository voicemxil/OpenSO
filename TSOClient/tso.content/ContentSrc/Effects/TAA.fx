// TAA.fx — temporal anti-aliasing for OpenSO 3D mode.
//
// Reads:
//   colorTex     — this frame's rendered color (post-scale-resolve, pre-blur).
//   historyTex   — previous frame's TAA output, sampled via velocity-based reprojection.
//   velocityTex  — per-pixel screen-space velocity (RCObject's / Vitaboy's DrawWithVelocity).
//
// Outputs the temporally-blended color to COLOR0. The C# helper writes that to the "current" history
// buffer; next frame it becomes the "previous" history.
//
// Algorithm (Karis 2014 / UE4 / Playdead-INSIDE recipe):
//   1. Velocity dilation: reproject with the nearest-depth motion vector in a 3x3 neighbourhood.
//   2. Jitter-free reprojection: histUV = uv - velocity + JitterDelta (cancels the jitter baked into
//      the velocity buffer, which is computed from the jittered projection).
//   3. Catmull-Rom (bicubic) history fetch — preserves detail across reprojection.
//   4. YCoCg variance AABB clip (mean +/- gamma*stddev), soft line-clip toward the neighbourhood centre.
//   5. Luminance-feedback + motion-adaptive blend (deep accumulation when stable, responsive on change).
//   6. Anti-flicker inverse-luma weighting on the final blend.

float2 InvScreenSize;
float  BlendFactor;     // 0..1, stable-area current weight. shader widens it on change/motion.
// Per-frame jitter delta (UV), in the same units as the velocity buffer. The velocity is computed from
// the JITTERED projection, so it carries the frame-to-frame jitter offset. Adding this back when
// reprojecting history cancels it -> exact (jitter-free) reprojection, which removes the wobble and the
// blur that come from sampling history a fraction of a pixel off every frame.
float2 JitterDelta;

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

// YCoCg color space — perceptually-cleaner than RGB for neighborhood comparison (luma dominates Y;
// chroma in Co/Cg has lower variance). Used for variance-based history clipping per Karis / Salvi.
float3 RGB_to_YCoCg(float3 c) { return float3(0.25*c.r + 0.5*c.g + 0.25*c.b, 0.5*c.r - 0.5*c.b, -0.25*c.r + 0.5*c.g - 0.25*c.b); }
float3 YCoCg_to_RGB(float3 c) { return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }

// Clip the history toward the AABB box in YCoCg space — much smoother than a hard min/max clamp,
// because it draws a line from history to current and intersects with the variance ellipse instead of
// snapping to the box's nearest face.
float3 ClipAABB(float3 cmin, float3 cmax, float3 hist)
{
    float3 center = 0.5 * (cmax + cmin);
    float3 extent = 0.5 * (cmax - cmin) + 1e-5;
    float3 d = hist - center;
    float3 unit = d / extent;
    float u = max(max(abs(unit.x), abs(unit.y)), abs(unit.z));
    return (u > 1) ? center + d / u : hist;
}

// Catmull-Rom (bicubic) history sampling. THE key to detail reconstruction: plain bilinear history
// fetch is a low-pass filter applied every frame, so accumulated detail decays into mush ("just
// blurring"). Catmull-Rom preserves high frequencies across reprojection, so the jittered samples
// actually build a sharp supersampled image. 9-tap form using the bilinear-offset trick (weights sum
// to 1 exactly). Overshoot from the negative lobes is bounded by the neighborhood AABB clip afterward.
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
    return max(r, 0.0); // clamp bicubic ringing undershoot to valid color
}

float4 TAA_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;

    // SHARP current sample — single center tap. The previous build averaged a jitter-weighted 3x3
    // neighborhood ("filtered current"), which softened every frame. Temporal accumulation across the
    // jittered history is what resolves detail, so the per-frame input should stay crisp.
    float3 curr = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(uv, 0, 0)).rgb);

    // 3x3 neighborhood pass — does double duty:
    //  (1) variance stats (m1,m2) for the history clamp AABB.
    //  (2) VELOCITY DILATION: pick the velocity of the CLOSEST-depth pixel in the 3x3 (depth packed in
    //      velocity.b, 0=near..1=far). Reference TAA reprojects with the dilated (nearest) motion vector
    //      so thin/edge foreground objects don't ghost (a background centre pixel would otherwise
    //      reproject wrong while a moving silhouette passes through its neighbourhood).
    float3 m1 = 0, m2 = 0;
    float2 dilatedVel = float2(0, 0);
    float closestDepth = 1e9;    // smaller = nearer; init far beyond any valid depth
    float closestMask = 0.0;
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float2 o = float2(dx, dy) * InvScreenSize;
        float3 c = RGB_to_YCoCg(tex2Dlod(colorSampler, float4(uv + o, 0, 0)).rgb);
        m1 += c;
        m2 += c * c;

        float4 v = tex2Dlod(velocitySampler, float4(uv + o, 0, 0));
        // "No velocity written" is pushed to a sentinel BEYOND the valid [0,1] depth range (2.0) so that a
        // genuinely-far valid pixel (perspective depth packs near 1.0) always wins the nearest-depth
        // tiebreak over an unwritten neighbour — otherwise far objects would be marked non-reprojectable
        // and never converge.
        float d = (v.a >= 0.5) ? v.b : 2.0;
        if (d < closestDepth) { closestDepth = d; dilatedVel = v.rg; closestMask = v.a; }
    }
    m1 *= (1.0 / 9.0);
    m2 *= (1.0 / 9.0);
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0));
    // Clamp width in stddevs. Tighter rejects ghosting harder; too tight rejects valid jittered history on
    // high-frequency content (grass/foliage) every frame, so it never accumulates and the raw jitter shows
    // through. 1.5 is a looser box that lets foliage resolve while still bounding ghosting.
    const float GAMMA = 1.5;
    float3 cmin = m1 - GAMMA * sigma;
    float3 cmax = m1 + GAMMA * sigma;

    // Reproject with the dilated velocity (+ jitter delta cancels the jitter baked into the buffer).
    float2 velocity = dilatedVel;
    float2 histUV = uv - velocity + JitterDelta;
    bool valid = (histUV.x >= 0) && (histUV.x <= 1) && (histUV.y >= 0) && (histUV.y <= 1);
    bool reprojectable = closestMask >= 0.5;

    // Bicubic (Catmull-Rom) history fetch — preserves detail across reprojection. Then clip to the
    // YCoCg neighborhood AABB to reject ghosting / disocclusion.
    float3 history = RGB_to_YCoCg(SampleHistoryBicubic(histUV));
    history = ClipAABB(cmin, cmax, history);

    // Luminance feedback (Inside/Playdead): stable pixels (history matches current luma) keep deep
    // accumulation; pixels that changed drop toward more-current so edges/disocclusions stay responsive.
    float lumaC = curr.x;       // Y in YCoCg
    float lumaH = history.x;
    float diff = saturate(abs(lumaC - lumaH) / max(0.2, max(lumaC, lumaH)));
    float historyWeight = lerp(1.0 - BlendFactor, 0.6, diff);

    // Motion-adaptive: more current when moving fast (less lag/ghosting under motion).
    float vmag = length(velocity);
    float motionBoost = saturate(vmag * 20.0) * 0.35;
    float blend = saturate((1.0 - historyWeight) + motionBoost); // current-frame weight

    // Anti-flicker (Karis): weight current/history by inverse luma so bright sub-pixel samples don't
    // dominate and sparkle. Reduces shimmer on high-contrast fine lines. When the two lumas match this is
    // identical to a plain lerp (so accumulation/sharpness on stable surfaces is unaffected).
    float wc = blend * (1.0 / (1.0 + max(lumaC, 0.0)));
    float wh = (1.0 - blend) * (1.0 / (1.0 + max(lumaH, 0.0)));
    float3 blended = (curr * wc + history * wh) / max(wc + wh, 1e-5);

    float3 outYCoCg = (valid && reprojectable) ? blended : curr;
    return float4(YCoCg_to_RGB(outYCoCg), 1.0);
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
