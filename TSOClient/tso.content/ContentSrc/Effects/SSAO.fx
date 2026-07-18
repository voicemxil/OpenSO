// SSAO.fx — Scalable Ambient Obscurance (McGuire, Mara, Luebke — HPG 2012), pixel-shader port.
// Reference: http://graphics.cs.williams.edu/papers/SAOHPG12/ (BSD). Differences from the reference:
// no depth mip pyramid (single-level reads; the screen-space tap radius is clamped instead), and a
// temporal accumulation pass replaces raw per-pixel supersampling.
//
// View space is right-handed: z is negative for visible points. Inputs:
//   velocityTex .b = normalized linear view distance (clip.w / FarPlane), .a = valid-geometry mask.
//                   fp32 (Vector4 target) while AO is enabled — the packed key + estimator need the precision.
//   normalTex   .a = 0 for no geometry; else 0.5 + 0.5 * ambient light fraction (directional scene
//                   shaders encode it, non-directional ones write 1 = fully ambient). Face normals
//                   are derived from depth, not read from here.
// Chain: SAO -> BlurH -> BlurV -> Temporal -> Composite. The AO buffer carries SAO's packed depth
// key in .gb so the bilateral blur never re-reads the depth texture (reference behaviour).

float2 InvScreenSize;    // 1 / AO-target resolution
float  FarPlane;         // velocity.b is distance/FarPlane; multiply back for view-space distance
float  Radius;           // world-space occlusion radius (world units)
float  Bias;             // view-space bias against self-shadowing on flat surfaces
float  IntensityDivR6;   // SAO estimator gain: Intensity / Radius^6
float  ProjScalePx;      // pixels per view-space unit at z = -1 (0.5 * Projection.M22 * screenH)
float  FrameAngle;       // per-frame spiral rotation offset (golden-angle steps, temporal pass converges it)
float2 NoiseScale;       // (screenW/4, screenH/4): tiles the 4x4 noise texture across the screen
float  TemporalBlend;    // history weight in the temporal pass
float4x4 View;           // world->view (debug normal view only)
float4x4 InvProjection;  // clip->view, reconstructs view-space position from uv+depth

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

texture aoTex;
sampler aoSampler = sampler_state {
    texture = <aoTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};
// linear view of the same AO buffer: the composite runs on the OUTPUT grid, which is finer than the
// AO grid under upscaling — bilinear hides the resolution difference (AO is low-frequency)
sampler aoUpSampler = sampler_state {
    texture = <aoTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture aoHistoryTex;
sampler aoHistorySampler = sampler_state {
    texture = <aoHistoryTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture normalTex;
sampler normalSampler = sampler_state {
    texture = <normalTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

texture noiseTex;
sampler noiseSampler = sampler_state {
    texture = <noiseTex>;
    AddressU = WRAP; AddressV = WRAP;     // 4x4 noise, tiled across the screen
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

struct VSIn  { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };
struct VSOut { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };

VSOut VS(VSIn input)
{
    VSOut o = (VSOut)0;
    o.Position = input.Position;
    o.Coord = input.Coord;
    o.Coord.y = 1 - o.Coord.y; // match the rest of the post chain's fullscreen convention
    return o;
}

// View-space position from uv + linear view distance via InvProjection (handles FOV, aspect and Zoom).
// Result z is negative (RH view space).
float3 ReconstructViewPos(float2 uv, float linDist)
{
    float2 ndc = uv * 2.0 - 1.0; // Coord.y already flipped in VS, no extra flip
    float4 viewH = mul(float4(ndc, 0.0, 1.0), InvProjection);
    float3 ray = viewH.xyz / viewH.w;
    return ray * (-linDist / ray.z);
}

// ============================================================================ Pass 1: SAO
// Reference constants: 11 taps on a 7-turn spiral (coprime -> even angular coverage).
#define NUM_SAMPLES 11
#define NUM_SPIRAL_TURNS 7
#define TWO_PI 6.28318530718
// SAO packKey: depth key split across two 8-bit channels so the blur can run bilateral without a
// second texture read. Key precision ~ FarPlane/65536 view units.
float2 PackKey(float key)
{
    float hi = floor(key * 255.999) / 255.0;
    float lo = (key * 255.999 - floor(key * 255.999));
    return float2(hi, lo);
}
float UnpackKey(float2 p) { return p.x * (255.0 / 256.0) + p.y * (1.0 / 256.0); }

float4 SAO_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float4 vel = tex2D(velocitySampler, uv);
    float  nMask = tex2D(normalSampler, uv).a;
    float key = vel.b;
    if (vel.a < 0.5 || nMask < 0.5 || key >= 0.9999) return float4(1, PackKey(1.0), 1);

    float zC = key * FarPlane;                       // positive view distance
    float3 C = ReconstructViewPos(uv, zC);           // view space, z negative

    // Face normal from position derivatives (reference reconstructCSFaceNormal): immune to the
    // smooth-normal-vs-flat-facet false occlusion the previous implementation dodged the same way.
    float3 n_C = normalize(cross(ddx(C), ddy(C)));
    n_C *= -sign(dot(n_C, C));                       // orient toward the camera, branchless

    // Screen-space disk radius for the world-space Radius at this depth, clamped: with no depth mip
    // pyramid, giant near-camera disks would thrash the cache and alias badly.
    float ssDiskRadius = min(ProjScalePx * Radius / zC, 96.0);

    // Spiral phase: tiled 4x4 noise + per-frame golden-angle step (temporal pass integrates over frames).
    float2 nz = tex2D(noiseSampler, uv * NoiseScale).xy;
    float phi = nz.x * TWO_PI + FrameAngle;

    float radius2 = Radius * Radius;
    float sum = 0.0;
    [loop] for (int i = 0; i < NUM_SAMPLES; i++)
    {
        // tapLocation: fraction along the spiral + rotation
        float alpha = (float(i) + 0.5) * (1.0 / NUM_SAMPLES);
        float angle = alpha * (NUM_SPIRAL_TURNS * TWO_PI) + phi;
        float ssR = max(alpha * ssDiskRadius, 1.0);  // >= 1px so taps never all land on the centre texel
        float2 unitOffset;
        sincos(angle, unitOffset.y, unitOffset.x);

        float2 tapUV = uv + unitOffset * ssR * InvScreenSize;
        float4 tapVel = tex2Dlod(velocitySampler, float4(tapUV, 0, 0));
        // Invalid / sky taps read as far background; their vv is huge so the falloff zeroes them —
        // no branch needed (branchless by design: MojoShader has miscompiled branchy loops before).
        float zTap = min(tapVel.b, 0.9999) * FarPlane;
        float3 Q = ReconstructViewPos(tapUV, zTap);

        // Alchemy/SAO estimator, falloff variant 4 (reference default):
        // f^3 * max((v.n - bias) / (eps + v.v), 0) with f = max(r^2 - v.v, 0)
        float3 v = Q - C;
        float vv = dot(v, v);
        float vn = dot(v, n_C);
        float f = max(radius2 - vv, 0.0);
        sum += f * f * f * max((vn - Bias) / (0.01 + vv), 0.0);
    }

    float A = max(0.0, 1.0 - sum * IntensityDivR6 * (5.0 / NUM_SAMPLES));
    return float4(A, PackKey(key), 1);
}

// ============================================================================ Pass 2+3: bilateral blur
// Reference SAO_blur: separable 7-tap Gaussian at stride 2, weighted by the packed depth key so AO
// never bleeds across silhouettes. Two techniques share the kernel via the axis uniform.
float2 BlurAxis; // (1,0) horizontal pass, (0,1) vertical

float4 Blur_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float4 c = tex2D(aoSampler, uv);
    float key = UnpackKey(c.gb);

    // reference gaussian[R=4], scale 2; centre weight boosted by BASE (0.3) like the reference
    float sum = c.r * (0.3 + 0.153170);
    float totalWeight = 0.3 + 0.153170;
    static const float w[3] = { 0.144893, 0.122649, 0.092902 }; // static const: SM3 must fold the indexing at unroll

    // PLANE-AWARE bilateral (replaces the reference's flat 1/2000-of-far cutoff): on grazing ground
    // the per-tap depth step grows ~quadratically with distance (perspective compresses more ground
    // into each pixel), so ANY fixed or depth-proportional cutoff has a distance where flat ground
    // stops blurring — the unblurred spiral noise then starts along iso-depth contours and reads as
    // banding. Instead, estimate the local depth slope from the innermost tap pair and weight every
    // tap by its deviation from that PLANE at the tight reference cutoff: flat surfaces blur fully
    // at any distance and angle (their deviation is curvature, ~0), while true discontinuities
    // deviate by whole objects and stay crisp. The slope clamp bounds extrapolation at edges, where
    // the pair estimate is corrupted anyway and small weights are the correct outcome.
    float slope = clamp((UnpackKey(tex2D(aoSampler, uv + BlurAxis * 2.0 * InvScreenSize).gb)
                       - UnpackKey(tex2D(aoSampler, uv - BlurAxis * 2.0 * InvScreenSize).gb)) * 0.5,
                        -0.005, 0.005);

    [unroll] for (int r = 1; r <= 3; r++)
    {
        float2 o = BlurAxis * (r * 2.0) * InvScreenSize;
        float4 tapA = tex2D(aoSampler, uv + o);
        float4 tapB = tex2D(aoSampler, uv - o);
        float wA = w[r - 1] * max(0.0, 1.0 - 2000.0 * abs(UnpackKey(tapA.gb) - (key + slope * r)));
        float wB = w[r - 1] * max(0.0, 1.0 - 2000.0 * abs(UnpackKey(tapB.gb) - (key - slope * r)));
        sum += tapA.r * wA + tapB.r * wB;
        totalWeight += wA + wB;
    }
    return float4(sum / max(totalWeight, 1e-4), c.gb, 1);
}

// ============================================================================ Pass 4: Temporal
// Reproject previous-frame AO via the velocity buffer, neighbourhood-clamp, blend. Converges the
// per-frame spiral rotation into an effectively much higher tap count.
float4 Temporal_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float4 c = tex2D(aoSampler, uv);
    float current = c.r;
    float4 vel = tex2D(velocitySampler, uv);
    // Velocity is already un-jittered at the writers (ComputeVelocity subtracts JitterNDC), so this
    // reprojection needs no jitter correction — and at rest it is exactly identity.
    float2 histUV = uv - vel.rg;
    bool valid = (vel.a >= 0.5) && (histUV.x >= 0.0) && (histUV.x <= 1.0) && (histUV.y >= 0.0) && (histUV.y <= 1.0);
    if (!valid) return float4(current, c.gb, 1);
    float history = tex2D(aoHistorySampler, histUV).r;
    // Same-surface validity, MOTION-GATED (the TAA's oldest law): under TAA the depth buffer is
    // rasterized on a jittered projection, so at rest a depth-key mismatch at edges and fine
    // geometry is pure jitter sampling noise — ungated, accumulation died exactly there and the
    // spiral noise showed raw. A real trail needs motion, and the un-jittered velocity buffer means
    // jitter cannot arm the gate. The key fetch is POINT-SNAPPED through the same LINEAR sampler
    // (GL sampler-aliasing law): the packed two-channel key decodes to garbage if bilinearly mixed.
    float movePx = length(vel.rg / InvScreenSize);
    float moveGate = smoothstep(0.35, 1.5, movePx);
    float2 snapped = (floor(histUV / InvScreenSize) + 0.5) * InvScreenSize;
    float keyH = UnpackKey(tex2D(aoHistorySampler, snapped).gb);
    float depthValid = max(1.0 - moveGate * smoothstep(0.02, 0.06, abs(keyH - vel.b) / max(vel.b, 1e-3)), 0.25);
    float ao_min = current, ao_max = current;
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float s = tex2D(aoSampler, uv + float2(dx, dy) * InvScreenSize).r;
        ao_min = min(ao_min, s);
        ao_max = max(ao_max, s);
    }
    history = clamp(history, ao_min, ao_max);
    return float4(lerp(current, history, TemporalBlend * depthValid), c.gb, 1);
}

// ============================================================================ Pass 5: Composite
// Intensity already shaped the obscurance inside the estimator (IntensityDivR6), so the composite is
// a plain multiply — no double application. Occlusion is modulated by the pixel's ambient light
// fraction (from normalTex.a): fully occludes ambient light, and only DIRECT_AO of directly-lit
// light — sun/lamp-lit surfaces shouldn't go dark from sky/bounce occlusion.
#define DIRECT_AO 0.25
float4 Composite_PS(VSOut input) : COLOR0
{
    float3 c = tex2D(colorSampler, input.Coord).rgb;
    float ao = tex2D(aoUpSampler, input.Coord).r;
    float ambientFrac = saturate(tex2D(normalSampler, input.Coord).a * 2.0 - 1.0);
    ao = lerp(1.0, ao, lerp(DIRECT_AO, 1.0, ambientFrac));
    return float4(c * ao, 1.0);
}

// ============================================================================ Diagnostics
float4 CompositeDebug_PS(VSOut input) : COLOR0    // blurred AO as grayscale
{
    float ao = tex2D(aoUpSampler, input.Coord).r;
    return float4(ao, ao, ao, 1.0);
}
float4 NormalDebug_PS(VSOut input) : COLOR0       // view-space G-buffer normal
{
    float4 n = tex2D(normalSampler, input.Coord);
    if (n.a < 0.5) return float4(0, 0, 0, 1);
    float3 vn = normalize(mul(n.xyz, (float3x3)View));
    return float4(vn * 0.5 + 0.5, 1.0);
}
float4 DepthDebug_PS(VSOut input) : COLOR0        // depth the AO reads, scaled for visibility
{
    float4 vel = tex2D(velocitySampler, input.Coord);
    if (vel.a < 0.5) return float4(1, 0, 0, 1);
    float d = saturate(vel.b * 3.0);   // viz scale only
    return float4(d, d, d, 1.0);
}

#if SM4
#define VSP vs_4_0
#define PSP ps_4_0
#else
#define VSP vs_3_0
#define PSP ps_3_0
#endif

technique SAO           { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP SAO_PS(); } }
technique Blur          { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Blur_PS(); } }
technique Temporal      { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Temporal_PS(); } }
technique Composite     { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Composite_PS(); } }
technique CompositeDebug{ pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP CompositeDebug_PS(); } }
technique NormalDebug   { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP NormalDebug_PS(); } }
technique DepthDebug    { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP DepthDebug_PS(); } }
