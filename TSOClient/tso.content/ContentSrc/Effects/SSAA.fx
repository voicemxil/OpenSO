// SSAA-style downscaling resolves for the supersampled (render scale > 1) backbuffer.
//  - DrawSSAA4: the exact 4-sample 2x2 box. Optimal for integer 2x supersampling (each output pixel is the
//    average of its 4 source pixels). Averaged in gamma space to match brightness with "supersampling off".
//  - DrawSSAAFootprint: a footprint-aware separable tent for NON-integer ratios (e.g. 1.5x). The fixed 2x2
//    box only ever samples a 2x2 source region regardless of ratio, so it under-covers at non-integer scales;
//    this widens the kernel to the actual SSAAScale x SSAAScale footprint using LINEAR taps.
// SSAADownsample.Draw picks the technique from the live render scale.

float2 SSAASize;   // 1 / source dimensions (source texel size in UV)
float SSAAScale;   // source/dest supersample ratio (>= 1); each output pixel covers SSAAScale source texels/axis

texture tex : Diffuse;
sampler texSampler = sampler_state {
	texture = <tex>;
	AddressU = CLAMP; AddressV = CLAMP; AddressW = CLAMP;
	MIPFILTER = POINT; MINFILTER = POINT; MAGFILTER = POINT;
};

// Linear variant for the footprint filter (each tap averages a 2x2 source quad in gamma space).
sampler texLinear = sampler_state {
	texture = <tex>;
	AddressU = CLAMP; AddressV = CLAMP; AddressW = CLAMP;
	MIPFILTER = LINEAR; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

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
	output.Coord.y = 1 - output.Coord.y;

    return output;
}

float4 SSAASample4(float2 uv) {
	// Exact 2x2 box. Average directly in gamma (sRGB-encoded) space — the engine composites in gamma space
	// and the non-supersampled resolve is a plain blit, so a gamma-space box keeps brightness identical to
	// "supersampling off".
	float4 result = float4(0, 0, 0, 0);
	uv += SSAASize / 2;
	result += tex2D(texSampler, uv);
	result += tex2D(texSampler, uv + float2(SSAASize.x, 0));
	result += tex2D(texSampler, uv + float2(0, SSAASize.y));
	result += tex2D(texSampler, uv + float2(SSAASize.x, SSAASize.y));
	return result / 4;
}

float4 SSAASampleFootprint(float2 uv) {
	// Separable tent (triangle) over the output pixel's SSAAScale x SSAAScale source footprint. Taps at
	// -1,0,+1 of a half-footprint step span +/- SSAAScale/2; tent weights {0.25, 0.5, 0.25} per axis (sum 1).
	float2 stp = SSAASize * (SSAAScale * 0.5);
	float w[3] = { 0.25, 0.5, 0.25 };
	float4 sum = float4(0, 0, 0, 0);
	[unroll] for (int y = -1; y <= 1; y++)
		[unroll] for (int x = -1; x <= 1; x++)
			sum += (w[x + 1] * w[y + 1]) * tex2D(texLinear, uv + float2(x, y) * stp);
	return sum;
}

float4 PixelShaderFunction(VertexShaderOutput input) : COLOR0
{
    return SSAASample4(input.Coord);
}

float4 PixelShaderFootprint(VertexShaderOutput input) : COLOR0
{
    return SSAASampleFootprint(input.Coord);
}

technique DrawSSAA4
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0_level_9_1 VertexShaderFunction();
        PixelShader = compile ps_4_0_level_9_1 PixelShaderFunction();
#else
        VertexShader = compile vs_3_0 VertexShaderFunction();
        PixelShader = compile ps_3_0 PixelShaderFunction();
#endif;

    }
}

technique DrawSSAAFootprint
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0_level_9_1 VertexShaderFunction();
        PixelShader = compile ps_4_0_level_9_1 PixelShaderFootprint();
#else
        VertexShader = compile vs_3_0 VertexShaderFunction();
        PixelShader = compile ps_3_0 PixelShaderFootprint();
#endif;

    }
}
