// MotionBlur.fx — modern reconstruction-filter motion blur for OpenSO 3D mode.
//
// Implements the McGuire et al. 2012 "A Reconstruction Filter for Plausible Motion Blur" pipeline (the
// same lineage used by Jimenez's COD:AW filter and most modern engines):
//
//   1. TileMax       — reduce the full-res velocity buffer to KxK tiles, keeping the max-magnitude
//                      velocity per tile.
//   2. NeighborMax   — dilate TileMax over a 3x3 tile neighborhood so a fast object's blur can reach
//                      into adjacent tiles (otherwise the streak would be clipped at tile borders).
//   3. Reconstruction — for each pixel, take jittered samples along the tile's dominant velocity and
//                      weight them with DEPTH-AWARE cone/cylinder functions. This is what makes the blur
//                      one-directional AND silhouette-correct: a background pixel only picks up a moving
//                      foreground's color when that foreground's velocity actually reaches it and it is
//                      in front (depth test), so there's no symmetric double-streak and no ghost halo.
//
// Velocity buffer layout (HalfVector4, MRT1):  .rg = per-frame screen-space velocity (UV units),
//   .b = NORMALIZED LINEAR view distance (clip.w / far=800), 0 near .. 1 far. .a = valid-velocity mask.
// Unwritten pixels are cleared to (0,0,1,0): zero velocity, FAR depth, mask 0 — i.e. static far bg.

#define TILEK 20          // tile size in velocity-buffer texels
#define SAMPLES 15        // reconstruction taps along the dominant velocity
#define SOFT_Z 0.0125     // depth-compare softness in normalized-linear-depth units (~10 world units / far 800)
#define HALF_PI 1.5707963

float2 SourceTexel;       // 1 / velocity-buffer resolution      (TileMax)
float2 TileTexel;         // 1 / TileMax resolution              (NeighborMax)
float2 ScreenSizePx;      // reconstruction output resolution in px
float  ShutterScale;      // shutter fraction (0..1): velocity is per-frame, * this = exposure motion

texture colorTex;
sampler colorSampler = sampler_state {
    texture = <colorTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture velocityTex;
sampler velocitySampler = sampler_state {
    texture = <velocityTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

texture tileMaxTex;
sampler tileMaxSampler = sampler_state {
    texture = <tileMaxTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

texture neighborMaxTex;
sampler neighborMaxSampler = sampler_state {
    texture = <neighborMaxTex>;
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

// ----------------------------------------------------------------------------- Pass 1: TileMax
// Single pass: each output tile reads the KxK velocity block centered on its UV (source and tile targets
// share [0,1] UV, so no index math needed) and keeps the largest-magnitude velocity. Output .b = that
// magnitude (UV units) so NeighborMax can compare cheaply. Total work is O(W*H) — one full-res read.
float4 TileMax_PS(VSOut input) : COLOR0
{
    float2 best = float2(0, 0);
    float bestMag2 = 0;
    [loop] for (int x = 0; x < TILEK; x++)
    {
        [loop] for (int y = 0; y < TILEK; y++)
        {
            float2 off = (float2(x, y) - (TILEK * 0.5)) * SourceTexel;
            float4 s = tex2Dlod(velocitySampler, float4(input.Coord + off, 0, 0));
            if (s.a < 0.5) continue;            // unwritten = zero velocity, skip
            float m2 = dot(s.rg, s.rg);
            if (m2 > bestMag2) { bestMag2 = m2; best = s.rg; }
        }
    }
    return float4(best, sqrt(bestMag2), 1);
}

// ----------------------------------------------------------------------------- Pass 2: NeighborMax
// 3x3 dilation of TileMax: take the max-magnitude tile velocity in the neighborhood so a fast streak
// originating in an adjacent tile is still reconstructed in this tile.
float4 NeighborMax_PS(VSOut input) : COLOR0
{
    float2 best = float2(0, 0);
    float bestMag = 0;
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        [unroll] for (int dy = -1; dy <= 1; dy++)
        {
            float4 s = tex2Dlod(tileMaxSampler, float4(input.Coord + float2(dx, dy) * TileTexel, 0, 0));
            if (s.b > bestMag) { bestMag = s.b; best = s.rg; }
        }
    }
    return float4(best, bestMag, 1);
}

// ----------------------------------------------------------------------------- Pass 3: Reconstruction
// Interleaved gradient noise (Jimenez) — cheap per-pixel jitter to break up banding between the discrete
// taps without a texture lookup.
float IGN(float2 p)
{
    return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
}

// McGuire soft depth compare: ~1 when za is clearly in front of zb (smaller depth = nearer), ramps to 0
// as they approach co-planarity within SOFT_Z.
float SoftDepth(float za, float zb) { return saturate(1.0 - (za - zb) / SOFT_Z); }
// Cone: a blurry sample of velocity magnitude `mag` (px) covers points up to `mag` away, falling off.
float Cone(float distPx, float magPx) { return saturate(1.0 - distPx / magPx); }
// Cylinder: ~1 inside the blur extent, falling to 0 right at the edge — for two mutually-blurry samples.
float Cylinder(float distPx, float magPx) { return 1.0 - smoothstep(0.95 * magPx, 1.05 * magPx + 1e-3, distPx); }

float4 Reconstruction_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float4 centerColor = tex2D(colorSampler, uv);

    // Dominant (neighborhood) velocity for this tile, scaled by the shutter fraction.
    float4 nm = tex2Dlod(neighborMaxSampler, float4(uv, 0, 0));
    float2 nmVel = nm.rg * ShutterScale;
    float nmMagPx = length(nmVel * ScreenSizePx);

    // No meaningful motion in this neighborhood -> keep the pixel sharp.
    if (nmMagPx < 0.5) return centerColor;

    float4 cVel4 = tex2Dlod(velocitySampler, float4(uv, 0, 0));
    float2 cVel = cVel4.rg * ShutterScale;
    float cDepth = (cVel4.a < 0.5) ? 1.0 : cVel4.b;          // unwritten center = far bg
    float cMagPx = max(length(cVel * ScreenSizePx), 0.5);

    float jitter = IGN(uv * ScreenSizePx) - 0.5;

    // Center contributes weighted by 1/velocity so slow/static pixels stay crisp; fast pixels let the
    // gathered samples dominate (= more blur). This is the McGuire center-tap weighting.
    float weight = 1.0 / cMagPx;
    float4 sum = centerColor * weight;

    [loop] for (int i = 0; i < SAMPLES; i++)
    {
        // t spans [-1, 1] across the dominant velocity (both directions sampled, but each sample is
        // accepted ONLY by the depth+cone test, so the result is a correct one-sided trail rather than
        // a naive symmetric smear).
        float t = lerp(-1.0, 1.0, (float(i) + jitter + 1.0) / (float(SAMPLES) + 1.0));
        float2 sampUV = uv + nmVel * t;

        float4 sVel4 = tex2Dlod(velocitySampler, float4(sampUV, 0, 0));
        float2 sVel = sVel4.rg * ShutterScale;
        float sDepth = (sVel4.a < 0.5) ? 1.0 : sVel4.b;     // unwritten sample = far bg
        float sMagPx = max(length(sVel * ScreenSizePx), 0.5);

        float distPx = abs(t) * nmMagPx;

        float front = SoftDepth(cDepth, sDepth);  // center in front of sample
        float back  = SoftDepth(sDepth, cDepth);  // sample in front of center

        float w = 0;
        // (a) sample is a blurry FOREGROUND object whose motion smears over the center pixel.
        w += back  * Cone(distPx, sMagPx);
        // (b) center is a blurry foreground; we're gathering the BACKGROUND it streaks across.
        w += front * Cone(distPx, cMagPx);
        // (c) both are blurry and overlapping in the same depth range.
        w += 2.0 * Cylinder(distPx, sMagPx) * Cylinder(distPx, cMagPx);

        sum += tex2Dlod(colorSampler, float4(sampUV, 0, 0)) * w;
        weight += w;
    }

    return sum / max(weight, 1e-4);
}

technique TileMax
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 TileMax_PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 TileMax_PS();
#endif
    }
}

technique NeighborMax
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 NeighborMax_PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 NeighborMax_PS();
#endif
    }
}

technique Reconstruction
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 Reconstruction_PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 Reconstruction_PS();
#endif
    }
}
