// FXAA.fx — compact post-process anti-aliasing for the OpenSO decoupled AA pipeline.
//
// STAGED for the Windows shader build. This file is NOT yet wired into the runtime — to enable it:
//   1. Add it to the content pipeline (TSOClientContent*.mgcb) so it compiles to Content/Effects/FXAA.xnb.
//   2. Load it in WorldContent (FXAA = ContentManager.Load<Effect>("Effects/FXAA");) inside a try/catch.
//   3. Apply it from a PostProcessAA resolve helper (model it on SSAADownsample) hooked into
//      PPXDepthEngine.DrawBackbuffer, driven by WorldConfig.Current.PostAA.
// See GRAPHICS-AA-PLAN.md "Phase 2 / shader layer" for the full wiring spec.
//
// Algorithm: the classic compact luma-based FXAA (Timothy Lottes). Detects high-contrast edges from a
// 3x3 luma neighbourhood and blurs along the edge direction. Single pass; fits ps_3_0 and 9_1 profiles.

float2 InvViewportSize; // (1/width, 1/height) of the source texture

texture tex : Diffuse;
sampler texSampler = sampler_state {
    texture = <tex>;
    AddressU = CLAMP; AddressV = CLAMP; AddressW = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

#define FXAA_SPAN_MAX   8.0
#define FXAA_REDUCE_MUL (1.0 / 8.0)
#define FXAA_REDUCE_MIN (1.0 / 128.0)

struct VertexShaderInput
{
    float4 Position : SV_Position0;
    float2 Coord : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : SV_Position0;
    float2 Coord : TEXCOORD0;
};

VertexShaderOutput VertexShaderFunction(VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput)0;
    output.Position = input.Position;
    output.Coord = input.Coord;
    output.Coord.y = 1 - output.Coord.y; // match SSAA.fx fullscreen convention
    return output;
}

float4 PixelShaderFunction(VertexShaderOutput input) : COLOR0
{
    float2 inv = InvViewportSize;
    const float3 luma = float3(0.299, 0.587, 0.114);

    float3 rgbNW = tex2D(texSampler, input.Coord + float2(-1, -1) * inv).xyz;
    float3 rgbNE = tex2D(texSampler, input.Coord + float2( 1, -1) * inv).xyz;
    float3 rgbSW = tex2D(texSampler, input.Coord + float2(-1,  1) * inv).xyz;
    float3 rgbSE = tex2D(texSampler, input.Coord + float2( 1,  1) * inv).xyz;
    float4 center = tex2D(texSampler, input.Coord);
    float3 rgbM = center.xyz;

    float lumaNW = dot(rgbNW, luma);
    float lumaNE = dot(rgbNE, luma);
    float lumaSW = dot(rgbSW, luma);
    float lumaSE = dot(rgbSE, luma);
    float lumaM  = dot(rgbM,  luma);

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    // Edge direction perpendicular to the luma gradient.
    float2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * FXAA_REDUCE_MUL), FXAA_REDUCE_MIN);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, -FXAA_SPAN_MAX, FXAA_SPAN_MAX) * inv;

    float3 rgbA = 0.5 * (
        tex2D(texSampler, input.Coord + dir * (1.0 / 3.0 - 0.5)).xyz +
        tex2D(texSampler, input.Coord + dir * (2.0 / 3.0 - 0.5)).xyz);
    float3 rgbB = rgbA * 0.5 + 0.25 * (
        tex2D(texSampler, input.Coord + dir * -0.5).xyz +
        tex2D(texSampler, input.Coord + dir *  0.5).xyz);

    float lumaB = dot(rgbB, luma);
    // If the wider blur strayed outside the local luma range, fall back to the narrow blur.
    float3 result = ((lumaB < lumaMin) || (lumaB > lumaMax)) ? rgbA : rgbB;
    return float4(result, center.a);
}

technique FXAA
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0_level_9_1 VertexShaderFunction();
        PixelShader  = compile ps_4_0_level_9_1 PixelShaderFunction();
#else
        VertexShader = compile vs_3_0 VertexShaderFunction();
        PixelShader  = compile ps_3_0 PixelShaderFunction();
#endif
    }
}
