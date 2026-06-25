// GTAO.fx — Screen-Space Ambient Occlusion for OpenSO.
//
// FAITHFUL port of the canonical LearnOpenGL SSAO (https://learnopengl.com/Advanced-Lighting/SSAO):
//   - 64-sample hemisphere kernel (generated CPU-side in AOPass, accelerating distribution) in Samples[].
//   - 4x4 noise-rotation texture (noiseTex), tiled across the screen, rotates the kernel per pixel.
//   - per-pixel TBN from the view normal + random rotation (Gram-Schmidt).
//   - each kernel sample is placed in view space, projected with the real Projection matrix, and its
//     depth compared against the rasterized geometry depth at that pixel, with a smoothstep range check.
//   - 4x4 box blur removes the 4x4 noise tiling (the matched denoiser for this noise pattern).
//
// Two adaptations forced by THIS engine (not deviations from the algorithm, requirements of the data):
//   1. MonoGame is right-handed: view-space Z is NEGATIVE for visible points. Comparisons use that.
//   2. There is no view-position G-buffer and depth is a half-float (velocity.b = normalized linear
//      distance). We reconstruct view position via the inverse projection matrix, and the depth-compare
//      bias is scaled with distance to clear the half-float quantization step (~distance*2^-10).
//
// Inputs: velocityTex (.b = normalized linear view distance, .a = valid mask), normalTex (.xyz world
// normal, .a valid), colorTex (composite only). Pipeline: SSAO -> Blur -> Temporal -> Composite.

float2 InvScreenSize;
float  FarPlane;         // velocity.b is distance/FarPlane; multiply back to get world view distance
float  Radius;           // world-space sample radius (units)
float  Intensity;        // AO strength in the composite (0..1+)
float  AOBias;           // depth-compare bias floor (view-space units); scaled with distance in-shader
float2 NoiseScale;       // (screenW/4, screenH/4): tiles the 4x4 noise texture across the screen
float4x4 View;           // world->view, transforms G-buffer world normals into view space
float4x4 Projection;     // view->clip (includes Zoom+aspect), projects kernel samples back to screen
float4x4 InvProjection;  // clip->view, reconstructs view-space position from uv+depth
float3 Samples[64];      // hemisphere kernel in tangent space (z = along normal), CPU-generated

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
    AddressU = WRAP; AddressV = WRAP;     // tiled across the screen (the 4x4 noise pattern)
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

// velocity.b = normalized linear view distance (clip.w / FarPlane). World view distance = multiply back.
float LinearizeDepth(float linDepth) { return linDepth * FarPlane; }

// Reconstruct view-space position from uv + measured linear view distance via the inverse projection
// matrix (handles FOV, aspect AND camera Zoom exactly — consistent with the forward Projection used to
// re-project the kernel samples). result.z is NEGATIVE (RH view space).
float3 ReconstructViewPos(float2 uv, float linDist)
{
    float2 ndc = uv * 2.0 - 1.0; // VS already flipped Coord.y so uv.y=1 is top = NDC.y=+1, no extra flip
    float4 viewH = mul(float4(ndc, 0.0, 1.0), InvProjection);
    float3 ray = viewH.xyz / viewH.w;
    return ray * (-linDist / ray.z);
}

// ============================================================================ Pass 1: SSAO (HBAO-style)
#define AO_BIAS 0.10      // sin(elevation) bias — rejects shallow near-tangent neighbours (noise floor)
#define AO_STRENGTH 1.2   // obscurance gain (the user AOIntensity multiplies again in Composite)

float4 GTAO_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float4 vel = tex2D(velocitySampler, uv);
    if (vel.a < 0.5 || vel.b >= 0.9999) return 1.0;
    float dC = LinearizeDepth(vel.b);
    float3 fragPos = ReconstructViewPos(uv, dC);                 // view space, z negative

    float4 nSamp = tex2D(normalSampler, uv);
    if (nSamp.a < 0.5) return 1.0;
    // GEOMETRIC normal from the reconstructed view-position derivatives — exactly perpendicular to the
    // rendered surface. The smooth G-buffer vertex normal is tilted relative to each flat facet on low-poly
    // meshes (the sim), so the tangent-plane test saw false occlusion on flat facets. The geometric normal
    // matches the facet, so flat facets + flat terrain give height ~ 0 -> no false AO. Clean now that depth
    // is 32-bit linear (ddx of the old half-float NDC depth was what made derived normals speckle before).
    float3 normal = normalize(cross(ddx(fragPos), ddy(fragPos)));
    if (dot(normal, fragPos) > 0.0) normal = -normal;           // orient toward the camera

    // Random rotation vector from the tiled noise texture (z=0 -> rotation about the normal).
    float3 randomVec = normalize(float3(tex2D(noiseSampler, uv * NoiseScale).xy, 0.0));
    float3 tangent = normalize(randomVec - normal * dot(randomVec, normal));
    float3 bitangent = cross(normal, tangent);
    float3x3 TBN = float3x3(tangent, bitangent, normal);

    float occlusion = 0.0;
    [loop] for (int i = 0; i < 64; i++)
    {
        // Kernel sample only chooses WHERE on screen to look (hemisphere oriented by the normal).
        float3 samplePos = fragPos + mul(Samples[i], TBN) * Radius;
        float4 offset = mul(float4(samplePos, 1.0), Projection);
        if (offset.w <= 0.0) continue;
        float2 sUV = (offset.xy / offset.w) * 0.5 + 0.5;

        float4 sV = tex2Dlod(velocitySampler, float4(sUV, 0, 0));
        if (sV.a < 0.5 || sV.b >= 0.9999) continue;             // sky / nothing there -> not an occluder

        // HORIZON test: reconstruct the ACTUAL geometry position at that pixel and measure how far it rises
        // above THIS pixel's tangent plane. height ~ 0 for a coplanar (flat) neighbour at ANY viewing angle,
        // so flat surfaces never self-occlude — grazing-robust, no depth bias needed. height/dist is the sine
        // of the occluder's elevation above the surface; AO_BIAS rejects shallow near-tangent noise; the
        // falloff drops occluders beyond Radius (in 3D distance) so foreground objects don't halo the ground.
        float3 Pi = ReconstructViewPos(sUV, LinearizeDepth(sV.b));
        float3 diff = Pi - fragPos;
        float dist = length(diff) + 1e-4;
        float height = dot(diff, normal);
        float falloff = saturate(1.0 - dist / Radius);
        occlusion += max(0.0, height / dist - AO_BIAS) * falloff;
    }
    return saturate(1.0 - (occlusion / 64.0) * AO_STRENGTH);
}

// ============================================================================ Pass 2: Blur (4x4 bilateral)
// 4x4 footprint (cancels the 4x4 noise tiling) but DEPTH-AWARE: samples across a silhouette (big depth
// jump) are rejected, so AO doesn't bleed object<->background. A plain box blur there produces the bright
// rim/halo at object edges; the depth weight removes it.
float4 Blur_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float4 cVel = tex2D(velocitySampler, uv);
    if (cVel.a < 0.5) return 1.0;
    float cZ = LinearizeDepth(cVel.b);
    float sumW = 0.0, sumAO = 0.0;
    [unroll] for (int x = -2; x <= 1; x++)
    [unroll] for (int y = -2; y <= 1; y++)
    {
        float2 o = float2(x, y) * InvScreenSize;
        float4 sVel = tex2D(velocitySampler, uv + o);
        if (sVel.a < 0.5) continue;
        float sZ = LinearizeDepth(sVel.b);
        float w = exp(-abs(sZ - cZ) / max(cZ * 0.02, 0.05));   // edge-preserving
        sumW += w;
        sumAO += w * tex2D(aoSampler, uv + o).r;
    }
    return (sumW > 0.0 ? sumAO / sumW : 1.0).rrrr;
}

// ============================================================================ Pass 3: Temporal
// Reproject previous-frame AO via the velocity buffer and blend, to stabilize the TAA-jittered inputs.
float4 Temporal_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float current = tex2D(aoSampler, uv).r;
    float4 vel = tex2D(velocitySampler, uv);
    float2 histUV = uv - vel.rg;
    bool valid = (vel.a >= 0.5) && (histUV.x >= 0.0) && (histUV.x <= 1.0) && (histUV.y >= 0.0) && (histUV.y <= 1.0);
    if (!valid) return current.xxxx;
    float history = tex2D(aoHistorySampler, histUV).r;
    float ao_min = current, ao_max = current;
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        float s = tex2D(aoSampler, uv + float2(dx, dy) * InvScreenSize).r;
        ao_min = min(ao_min, s);
        ao_max = max(ao_max, s);
    }
    history = clamp(history, ao_min, ao_max);
    return lerp(current, history, 0.9).xxxx;
}

// ============================================================================ Pass 4: Composite
float4 Composite_PS(VSOut input) : COLOR0
{
    float3 c = tex2D(colorSampler, input.Coord).rgb;
    float ao = tex2D(aoSampler, input.Coord).r;
    float darken = saturate(1.0 - (1.0 - ao) * Intensity);
    return float4(c * darken, 1.0);
}

// ============================================================================ Diagnostics
float4 CompositeDebug_PS(VSOut input) : COLOR0    // raw AO as grayscale
{
    float ao = tex2D(aoSampler, input.Coord).r;
    return float4(ao, ao, ao, 1.0);
}
float4 NormalDebug_PS(VSOut input) : COLOR0       // VIEW-space normal (the one the AO orients its hemisphere by)
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
    float d = saturate(vel.b * 3.0);   // viz scale only; the AO reads the full-precision vel.b directly
    return float4(d, d, d, 1.0);
}

#if SM4
#define VSP vs_4_0
#define PSP ps_4_0
#else
#define VSP vs_3_0
#define PSP ps_3_0
#endif

technique GTAO          { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP GTAO_PS(); } }
technique Blur          { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Blur_PS(); } }
technique Temporal      { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Temporal_PS(); } }
technique Composite     { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Composite_PS(); } }
technique CompositeDebug{ pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP CompositeDebug_PS(); } }
technique NormalDebug   { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP NormalDebug_PS(); } }
technique DepthDebug    { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP DepthDebug_PS(); } }
