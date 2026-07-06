// GTAO.fx — screen-space ambient occlusion (LearnOpenGL-style hemisphere SSAO, CPU-generated kernel).
// View space is right-handed: z is negative for visible points. Inputs: velocityTex (.b = normalized
// linear view distance, .a = valid mask), normalTex (.xyz world normal). SSAO -> Blur -> Temporal -> Composite.

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

float LinearizeDepth(float linDepth) { return linDepth * FarPlane; }

// View-space position from uv + linear view distance via InvProjection (handles FOV, aspect and Zoom).
// Result z is negative (RH view space).
float3 ReconstructViewPos(float2 uv, float linDist)
{
    float2 ndc = uv * 2.0 - 1.0; // Coord.y already flipped in VS, no extra flip
    float4 viewH = mul(float4(ndc, 0.0, 1.0), InvProjection);
    float3 ray = viewH.xyz / viewH.w;
    return ray * (-linDist / ray.z);
}

// ============================================================================ Pass 1: SSAO (HBAO-style)
#define AO_BIAS 0.10      // sin(elevation) floor — rejects shallow near-tangent occluders
#define AO_STRENGTH 1.2   // obscurance gain (Intensity multiplies again in Composite)

float4 GTAO_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float4 vel = tex2D(velocitySampler, uv);
    if (vel.a < 0.5 || vel.b >= 0.9999) return 1.0;
    float dC = LinearizeDepth(vel.b);
    float3 fragPos = ReconstructViewPos(uv, dC);                 // view space, z negative

    float4 nSamp = tex2D(normalSampler, uv);
    if (nSamp.a < 0.5) return 1.0;
    // Geometric normal from position derivatives, not the smooth G-buffer normal: smooth normals tilt
    // against flat facets on low-poly meshes and produce false occlusion there.
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
        float3 samplePos = fragPos + mul(Samples[i], TBN) * Radius;
        float4 offset = mul(float4(samplePos, 1.0), Projection);
        if (offset.w <= 0.0) continue;
        float2 sUV = (offset.xy / offset.w) * 0.5 + 0.5;

        float4 sV = tex2Dlod(velocitySampler, float4(sUV, 0, 0));
        if (sV.a < 0.5 || sV.b >= 0.9999) continue;             // sky / nothing there -> not an occluder

        // Horizon test: height of the reconstructed neighbour above this pixel's tangent plane. Coplanar
        // neighbours give height ~ 0, so flat surfaces never self-occlude; height/dist is sin(elevation).
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
// 4x4 footprint cancels the noise tiling; the depth weight stops AO bleeding across silhouettes.
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
float4 NormalDebug_PS(VSOut input) : COLOR0       // view-space normal
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

technique GTAO          { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP GTAO_PS(); } }
technique Blur          { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Blur_PS(); } }
technique Temporal      { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Temporal_PS(); } }
technique Composite     { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Composite_PS(); } }
technique CompositeDebug{ pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP CompositeDebug_PS(); } }
technique NormalDebug   { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP NormalDebug_PS(); } }
technique DepthDebug    { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP DepthDebug_PS(); } }
