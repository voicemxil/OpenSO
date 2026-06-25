// SMAA.fx — Enhanced Subpixel Morphological Anti-Aliasing for OpenSO.
//
// Wraps Jorge Jimenez's reference SMAA.hlsl (Copyright (C) 2013 Jimenez/Echevarria/Masia/Navarro/Gutierrez,
// MIT licensed). Faithful integration: we set the porting macros for the MonoGame MGFX compiler and
// #include the reference unchanged, then expose 3 techniques + 3 entry-point pairs (EdgeDetect, BlendWeights,
// NeighborBlend) that map 1:1 to SMAA's pipeline. Built at HiDef for DX (ps_4_0_level_9_3) and Reach for
// OGL (ps_3_0); both UI presets (Low / High) currently route to this single PRESET_HIGH build.
//
// Pipeline (run by SMAAResolve in C#):
//   1. EdgeDetect      : color  -> edges (RG)     [luma edge detection]
//   2. BlendWeights    : edges  -> weights (RGBA) [needs AreaTex + SearchTex lookup textures]
//   3. NeighborBlend   : color+weights -> screen  [final blend]

// SMAA expects a porting macro selecting the shading language. SMAA_HLSL_3 uses the DX9-style tex2D/tex2Dlod
// path, which the MGFX compiler accepts at every profile we target (ps_3_0 and ps_4_0).
#define SMAA_HLSL_3
#define SMAA_PRESET_HIGH

// SMAA's HLSL_3 default selector reads AreaTex via (R, A) — legacy DX9 L8A8 layout where the alpha channel
// stores the second component. We pack AreaTex as RGBA8 with the two components in R and G (B=0, A=255), so
// override the selector to .rg to match. (Without this, the lookup reads G-data-ignored / A-always-255 ->
// area-table garbage -> wrong blend weights -> SMAA enhances edges instead of softening them.)
#define SMAA_AREATEX_SELECT(sample) sample.rg

// SMAA_RT_METRICS = (1/W, 1/H, W, H) of the render target. Set per-frame from C#.
float4 SMAA_RT_METRICS;

texture colorTex_t;
sampler colorTex = sampler_state {
    texture = <colorTex_t>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture edgesTex_t;
sampler edgesTex = sampler_state {
    texture = <edgesTex_t>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture blendTex_t;
sampler blendTex = sampler_state {
    texture = <blendTex_t>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture areaTex_t;
sampler areaTex = sampler_state {
    texture = <areaTex_t>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture searchTex_t;
sampler searchTex = sampler_state {
    texture = <searchTex_t>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

// Pull in the verbatim reference. All functions/macros are defined here.
#include "SMAA.hlsl"

// ---------------------------------------------------------------------------- Edge Detection
struct EdgeIn  { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };
struct EdgeOut { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; float4 Offset0 : TEXCOORD1; float4 Offset1 : TEXCOORD2; float4 Offset2 : TEXCOORD3; };

EdgeOut EdgeDetectVS(EdgeIn input)
{
    EdgeOut o = (EdgeOut)0;
    o.Position = input.Position;
    o.Coord = float2(input.Coord.x, 1 - input.Coord.y); // match SSAA/FXAA fullscreen convention
    float4 offset[3];
    SMAAEdgeDetectionVS(o.Coord, offset);
    o.Offset0 = offset[0];
    o.Offset1 = offset[1];
    o.Offset2 = offset[2];
    return o;
}

float4 EdgeDetectPS(EdgeOut input) : COLOR0
{
    float4 offset[3];
    offset[0] = input.Offset0;
    offset[1] = input.Offset1;
    offset[2] = input.Offset2;
    float2 edges = SMAALumaEdgeDetectionPS(input.Coord, offset, colorTex);
    return float4(edges, 0.0, 1.0);
}

// ---------------------------------------------------------------------------- Blend-Weight Calculation
struct WeightIn  { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };
struct WeightOut { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; float2 Pixcoord : TEXCOORD1; float4 Offset0 : TEXCOORD2; float4 Offset1 : TEXCOORD3; float4 Offset2 : TEXCOORD4; };

WeightOut BlendWeightsVS(WeightIn input)
{
    WeightOut o = (WeightOut)0;
    o.Position = input.Position;
    o.Coord = float2(input.Coord.x, 1 - input.Coord.y);
    float4 offset[3];
    SMAABlendingWeightCalculationVS(o.Coord, o.Pixcoord, offset);
    o.Offset0 = offset[0];
    o.Offset1 = offset[1];
    o.Offset2 = offset[2];
    return o;
}

float4 BlendWeightsPS(WeightOut input) : COLOR0
{
    float4 offset[3];
    offset[0] = input.Offset0;
    offset[1] = input.Offset1;
    offset[2] = input.Offset2;
    return SMAABlendingWeightCalculationPS(input.Coord, input.Pixcoord, offset, edgesTex, areaTex, searchTex, float4(0,0,0,0));
}

// ---------------------------------------------------------------------------- Neighborhood Blending
struct BlendIn  { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };
struct BlendOut { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; float4 Offset : TEXCOORD1; };

BlendOut NeighborBlendVS(BlendIn input)
{
    BlendOut o = (BlendOut)0;
    o.Position = input.Position;
    o.Coord = float2(input.Coord.x, 1 - input.Coord.y);
    SMAANeighborhoodBlendingVS(o.Coord, o.Offset);
    return o;
}

float4 NeighborBlendPS(BlendOut input) : COLOR0
{
    return SMAANeighborhoodBlendingPS(input.Coord, input.Offset, colorTex, blendTex);
}

technique EdgeDetect
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 EdgeDetectVS();
        PixelShader  = compile ps_4_0 EdgeDetectPS();
#else
        VertexShader = compile vs_3_0 EdgeDetectVS();
        PixelShader  = compile ps_3_0 EdgeDetectPS();
#endif
    }
}

technique BlendWeights
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 BlendWeightsVS();
        PixelShader  = compile ps_4_0 BlendWeightsPS();
#else
        VertexShader = compile vs_3_0 BlendWeightsVS();
        PixelShader  = compile ps_3_0 BlendWeightsPS();
#endif
    }
}

technique NeighborBlend
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 NeighborBlendVS();
        PixelShader  = compile ps_4_0 NeighborBlendPS();
#else
        VertexShader = compile vs_3_0 NeighborBlendVS();
        PixelShader  = compile ps_3_0 NeighborBlendPS();
#endif
    }
}
