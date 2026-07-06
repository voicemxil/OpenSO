#include "LightingCommon.fx"

float4x4 World;
float4x4 ViewProjection;

// Previous-frame transforms, used only by the velocity (TAA/motion blur) techniques.
float4x4 PreviousWorld;
float4x4 PreviousViewProjection;

float ObjectID;
float2 UVScale;
float4 AmbientLight;
float SideMask;

texture MeshTex;
sampler TexSampler = sampler_state {
	texture = <MeshTex>;
	MinFilter = Linear;
	MagFilter = Linear;
	MipFilter = Linear;
	AddressU = Clamp;
	AddressV = Clamp;
};

// Anisotropic view of MeshTex for the velocity path's mip-biased sampling (SM4 only). The negative
// MipBias assumes aniso filtering; under trilinear the sharper mip re-aliases on minified content.
#if SM4
sampler MeshAnisoSampler = sampler_state {
	texture = <MeshTex>;
	MinFilter = Anisotropic;
	MagFilter = Anisotropic;
	MipFilter = Anisotropic;
	AddressU = Clamp;
	AddressV = Clamp;
	MaxAnisotropy = 16;
};
#endif

texture AnisoTex;
sampler AnisoSampler = sampler_state {
	texture = <AnisoTex>;
	MipFilter = Anisotropic;
	MagFilter = Anisotropic;
	MinFilter = Anisotropic;
	AddressU = Clamp;
	AddressV = Clamp;
	MaxAnisotropy = 4;
};

texture MaskTex;
sampler MaskSampler = sampler_state {
	texture = <MaskTex>;
	MinFilter = Linear;
	MagFilter = Linear;
	MipFilter = Linear;
	AddressU = Clamp;
	AddressV = Clamp;
};

struct VertexIn
{
	float4 position : SV_Position0;
	float2 texCoord : TEXCOORD0;
	float3 normal : TEXCOORD1;
};

struct VertexOut
{
	float4 position : SV_Position0;
	float2 texCoord : TEXCOORD0;
	float4 modelPos : TEXCOORD1;
	float3 normal : TEXCOORD2;
};

VertexOut vsRC(VertexIn v) {
	VertexOut result;

	result.texCoord = v.texCoord * UVScale;

	float4 wPos = mul(v.position, World);
	float4 finalPos = mul(wPos, ViewProjection);
	result.position = finalPos;
	result.modelPos = wPos;
	result.normal = mul(v.normal, (float3x3)World);

	return result;
}

float4 psRC(VertexOut v) : COLOR0
{
	float4 color = gammaMul(tex2D(TexSampler, v.texCoord), lightProcess(v.modelPos));
	if (color.a < 0.01) discard;
	return color;
}

float4 psDirRC(VertexOut v) : COLOR0
{
	float4 color = gammaMul(tex2D(TexSampler, v.texCoord), lightProcessDirection(v.modelPos, normalize(v.normal)));
	if (color.a < 0.01) discard;
	return color;
}

float4 psDummy(VertexOut v) : COLOR0
{
	return float4(1,1,1,1);
}

float4 psDepthClear(VertexOut v, out float depth : DEPTH0) : COLOR0
{
	depth = 1;
	return float4(1,1,1,1);
}

float4 psLMapRC(VertexOut v) : COLOR0
{
	return float4(1,1,1,1) * (1 - (v.modelPos.y / (3 * 2.95)) / 5);
}

float4 psDisabledRC(VertexOut v) : COLOR0
{
	float4 color = gammaMul(tex2D(TexSampler, v.texCoord), lightProcess(v.modelPos));
	float gray = dot(color.xyz, float3(0.2989, 0.5870, 0.1140));
	color = float4(gray, gray, gray, color.a);
	return color;
}

struct WallVertexIn
{
	float4 position : SV_Position0;
	float4 color : COLOR0;
	float3 texCoord : TEXCOORD0;
};

struct WallVertexOut
{
	float4 position : SV_Position0;
	float4 color : COLOR0;
	float3 texCoord : TEXCOORD0;
	float4 modelPos : TEXCOORD1;
};

SamplerState g_samPoint
{
	Filter = POINT;
	AddressU = Wrap;
	AddressV = Wrap;
};

WallVertexOut vsWallRC(WallVertexIn v) {
	WallVertexOut result;

	result.texCoord = v.texCoord;

	float4 wPos = mul(v.position, World);

	/*if (v.texCoord.y > CurrentLevel + 0.1) {
		//can be subject to cutaway
		if (CutawayTex.SampleLevel(g_samPoint, wPos.xz * WorldToLightFactor.xz + CutawayOffset, 0).a > 0.5f) wPos.y -= 2.45f;
	}*/

	float4 finalPos = mul(wPos, ViewProjection);
	result.color = v.color;
	result.position = finalPos;
	result.modelPos = wPos;

	return result;
}

float4 psWallRC(WallVertexOut v) : COLOR0
{
	float4 mPos = v.modelPos;
	mPos.y = v.texCoord.y*2.95*3;
	float2 texC = v.texCoord.xy;
	texC.x = frac(texC.x);
	texC.y = frac(((v.texCoord.y % 1)-1/240)/-1.04);
#if SIMPLE
	float4 color = gammaMul(v.color * tex2D(TexSampler, texC), lightInterp(mPos, v.texCoord.z)); // version for no mipmaps
#else
	float4 color = gammaMul(v.color * tex2Dgrad(AnisoSampler, texC, ddx(v.texCoord.xy), ddy(v.texCoord.xy)), lightInterp(mPos, v.texCoord.z));
#endif
	if (SideMask != 0) {
		//our mask is actually a texture of a top right wall.
		//skew the texcoord appropriately.

		texC.x = frac(texC.x);
		texC.y = frac((frac(v.texCoord.y)*0.970)*(-(1-0.1185))+(1-texC.x)*0.1185*SideMask - 0.117);
	}
	float4 maskC = tex2D(MaskSampler, texC);
	color.a *= maskC.a;
	if (color.a < 0.1) discard;
	return color;
}

WallVertexOut vsWallLMap(WallVertexIn v) {
	WallVertexOut result;

	float4 position = v.position;
	float2 tc = v.texCoord.xy;
	//we don't care about the terrain elevation of walls in this mode, only their level...
	//first we want to remove cutaways. this is easy - ceiling the y component of the texcoord
	tc.y = ceil(tc.y - 0.001);
	position.z = tc.y; //this makes a wall's height equal to its level. of course, two 
	result.texCoord = float3(tc, v.texCoord.z);

	float4 wPos = mul(position, World);
	float4 finalPos = mul(wPos, ViewProjection);
	result.color = v.color;
	result.position = finalPos;
	result.modelPos = wPos;

	return result;
}

float4 psWallLMap(WallVertexOut v) : COLOR0
{
	float3 texC = v.texCoord;
	if (texC.y - 0.001 < Level) discard; //ignore under current level
	//fade out as we get further away from the floor.
	//of course, lightmaps for upper levels
	float4 color = float4(1, 1, 1, 1) * (1 - (texC.y - Level) / 5); 

	//still want to mask, of course...
	texC.x = frac(texC.x);
	texC.y = frac(((v.texCoord.y % 1) - 1 / 240) / -1.04);

	if (SideMask != 0) {
		//our mask is actually a texture of a top right wall.
		//skew the texcoord appropriately.

		texC.x = frac(texC.x);
		texC.y = frac((frac(v.texCoord.y)*0.970)*(-(1 - 0.1185)) + (1 - texC.x)*0.1185*SideMask - 0.117);
	}
	float4 maskC = tex2D(MaskSampler, texC.xy);
	color.a *= maskC.a;
	if (color.a < 0.02) discard;
	return color;
}

technique Draw
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRC();
		PixelShader = compile ps_4_0_level_9_3 psRC();
#else
		VertexShader = compile vs_3_0 vsRC();
		PixelShader = compile ps_3_0 psRC();
#endif;
	}

	pass PassDirectional
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRC();
		PixelShader = compile ps_4_0_level_9_3 psDirRC();
#else
		VertexShader = compile vs_3_0 vsRC();
		PixelShader = compile ps_3_0 psDirRC();
#endif;
	}
}

// ---------------------------------------------------------------------------- DrawWithVelocity
// Same as Draw, but also emits screen-space velocity (COLOR1) and normal (COLOR2) for TAA/motion blur.
// Pass layout matches Draw so the PassOffset/DirPassOffset selection still works.
struct VertexOutV
{
	float4 position : SV_Position0;
	float2 texCoord : TEXCOORD0;
	float4 modelPos : TEXCOORD1;
	float3 normal : TEXCOORD2;
	float4 currClip : TEXCOORD3;
	float4 prevClip : TEXCOORD4;
};

struct PSOutputV
{
	float4 color    : COLOR0;
	float4 velocity : COLOR1;
	float4 normal   : COLOR2;
};

VertexOutV vsRCV(VertexIn v)
{
	VertexOutV r;
	r.texCoord = v.texCoord * UVScale;
	float4 wPos = mul(v.position, World);
	float4 finalPos = mul(wPos, ViewProjection);
	r.position = finalPos;
	r.modelPos = wPos;
	r.normal = mul(v.normal, (float3x3)World);
	r.currClip = finalPos;
	float4 prevWPos = mul(v.position, PreviousWorld);
	r.prevClip = mul(prevWPos, PreviousViewProjection);
	return r;
}

// Current-frame TAA sub-pixel jitter (NDC). currClip is rasterized jittered, but velocity must use
// un-jittered NDC, so it is subtracted; PreviousViewProjection is supplied un-jittered.
float2 JitterNDC;
// Negative texture LOD bias under TAA at render scale < 1 (mirrors GrassShader.MipBias); 0 when TAA is off.
float MipBias;

// NDC delta -> UV delta (*0.5, Y flipped). Positive w floor: w==0 would produce NaN velocity.
float2 ComputeVelocity(float4 curr, float4 prev)
{
	float currW = max(curr.w, 1e-4);
	float prevW = max(prev.w, 1e-4);
	float2 currNDC = curr.xy / currW - JitterNDC;
	float2 prevNDC = prev.xy / prevW;
	float2 v = (currNDC - prevNDC) * float2(0.5, -0.5);
	return clamp(v, -0.5, 0.5); // fp16 buffer holds +/-0.5 losslessly; the meta encode saturates itself on store
}

// velocity.b = normalized linear view distance (clip.w / far plane, BasicCamera.FarPlane = 800), [0,1]
// near..far. Linear rather than NDC depth: half-float NDC quantization caused depth banding in SSAO.
// velocity.a is the valid-velocity mask.
float PackDepth(float4 clip) { return saturate(clip.w / 800.0); }

PSOutputV psRCV(VertexOutV v)
{
	PSOutputV o;
#if SM4
	float4 tex = tex2Dgrad(MeshAnisoSampler, v.texCoord, ddx(v.texCoord) * exp2(MipBias), ddy(v.texCoord) * exp2(MipBias));
#else
	float4 tex = tex2Dbias(TexSampler, float4(v.texCoord, 0, MipBias));
#endif
	float4 color = gammaMul(tex, lightProcess(v.modelPos));
	if (color.a < 0.01) discard;
	o.color = color;
	o.velocity = float4(ComputeVelocity(v.currClip, v.prevClip), PackDepth(v.currClip), 1);
	// World-space normal for screen-space AO.
	o.normal = float4(normalize(v.normal), 1);
	return o;
}

PSOutputV psDirRCV(VertexOutV v)
{
	PSOutputV o;
	float3 n = normalize(v.normal);
#if SM4
	float4 tex = tex2Dgrad(MeshAnisoSampler, v.texCoord, ddx(v.texCoord) * exp2(MipBias), ddy(v.texCoord) * exp2(MipBias));
#else
	float4 tex = tex2Dbias(TexSampler, float4(v.texCoord, 0, MipBias));
#endif
	float4 color = gammaMul(tex, lightProcessDirection(v.modelPos, n));
	if (color.a < 0.01) discard;
	o.color = color;
	o.velocity = float4(ComputeVelocity(v.currClip, v.prevClip), PackDepth(v.currClip), 1);
	o.normal = float4(n, 1);
	return o;
}

technique DrawWithVelocity
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRCV();
		PixelShader = compile ps_4_0_level_9_3 psRCV();
#else
		VertexShader = compile vs_3_0 vsRCV();
		PixelShader = compile ps_3_0 psRCV();
#endif;
	}

	pass PassDirectional
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRCV();
		PixelShader = compile ps_4_0_level_9_3 psDirRCV();
#else
		VertexShader = compile vs_3_0 vsRCV();
		PixelShader = compile ps_3_0 psDirRCV();
#endif;
	}
}

// ---------------------------------------------------------------------------- Instanced draws
// Per-instance World (and PreviousWorld for the velocity variant) arrives as stream-1 vertex data,
// one full matrix row per TEXCOORD, row-major exactly as written by DGRPRenderer.DrawInstanced.
struct VertexInInstanced
{
	float4 position : SV_Position0;
	float2 texCoord : TEXCOORD0;
	float3 normal : TEXCOORD1;
	float4 instRow0 : TEXCOORD2;
	float4 instRow1 : TEXCOORD3;
	float4 instRow2 : TEXCOORD4;
	float4 instRow3 : TEXCOORD5;
};

VertexOut vsRCInstanced(VertexInInstanced v)
{
	VertexOut result;
	float4x4 instWorld = float4x4(v.instRow0, v.instRow1, v.instRow2, v.instRow3);

	result.texCoord = v.texCoord * UVScale;

	float4 wPos = mul(v.position, instWorld);
	float4 finalPos = mul(wPos, ViewProjection);
	result.position = finalPos;
	result.modelPos = wPos;
	result.normal = mul(v.normal, (float3x3)instWorld);

	return result;
}

technique DrawInstanced
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRCInstanced();
		PixelShader = compile ps_4_0_level_9_3 psRC();
#else
		VertexShader = compile vs_3_0 vsRCInstanced();
		PixelShader = compile ps_3_0 psRC();
#endif;
	}

	pass PassDirectional
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRCInstanced();
		PixelShader = compile ps_4_0_level_9_3 psDirRC();
#else
		VertexShader = compile vs_3_0 vsRCInstanced();
		PixelShader = compile ps_3_0 psDirRC();
#endif;
	}
}

struct VertexInInstancedV
{
	float4 position : SV_Position0;
	float2 texCoord : TEXCOORD0;
	float3 normal : TEXCOORD1;
	float4 instRow0 : TEXCOORD2;
	float4 instRow1 : TEXCOORD3;
	float4 instRow2 : TEXCOORD4;
	float4 instRow3 : TEXCOORD5;
	float4 prevRow0 : TEXCOORD6;
	float4 prevRow1 : TEXCOORD7;
	float4 prevRow2 : TEXCOORD8;
	float4 prevRow3 : TEXCOORD9;
};

VertexOutV vsRCInstancedV(VertexInInstancedV v)
{
	VertexOutV r;
	float4x4 instWorld = float4x4(v.instRow0, v.instRow1, v.instRow2, v.instRow3);
	float4x4 instPrevWorld = float4x4(v.prevRow0, v.prevRow1, v.prevRow2, v.prevRow3);

	r.texCoord = v.texCoord * UVScale;
	float4 wPos = mul(v.position, instWorld);
	float4 finalPos = mul(wPos, ViewProjection);
	r.position = finalPos;
	r.modelPos = wPos;
	r.normal = mul(v.normal, (float3x3)instWorld);
	r.currClip = finalPos;
	float4 prevWPos = mul(v.position, instPrevWorld);
	r.prevClip = mul(prevWPos, PreviousViewProjection);
	return r;
}

technique DrawInstancedWithVelocity
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRCInstancedV();
		PixelShader = compile ps_4_0_level_9_3 psRCV();
#else
		VertexShader = compile vs_3_0 vsRCInstancedV();
		PixelShader = compile ps_3_0 psRCV();
#endif;
	}

	pass PassDirectional
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRCInstancedV();
		PixelShader = compile ps_4_0_level_9_3 psDirRCV();
#else
		VertexShader = compile vs_3_0 vsRCInstancedV();
		PixelShader = compile ps_3_0 psDirRCV();
#endif;
	}
}

technique DepthClear
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRC();
		PixelShader = compile ps_4_0_level_9_3 psDummy();
#else
		VertexShader = compile vs_3_0 vsRC();
		PixelShader = compile ps_3_0 psDummy();
#endif;
	}

	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRC();
		PixelShader = compile ps_4_0_level_9_3 psDepthClear();
#else
		VertexShader = compile vs_3_0 vsRC();
		PixelShader = compile ps_3_0 psDepthClear();
#endif;
	}
}

technique DisabledDraw
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRC();
		PixelShader = compile ps_4_0_level_9_3 psDisabledRC();
#else
		VertexShader = compile vs_3_0 vsRC();
		PixelShader = compile ps_3_0 psDisabledRC();
#endif;
	}
}

technique WallDraw
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsWallRC();
		PixelShader = compile ps_4_0_level_9_3 psWallRC();
#else
		VertexShader = compile vs_3_0 vsWallRC();
		PixelShader = compile ps_3_0 psWallRC();
#endif;
	}
}

technique WallLMap
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsWallLMap();
		PixelShader = compile ps_4_0_level_9_3 psWallLMap();
#else
		VertexShader = compile vs_3_0 vsWallLMap();
		PixelShader = compile ps_3_0 psWallLMap();
#endif;
	}
}

// ---------------------------------------------------------------------------- Wall velocity
// vsWallRC/psWallRC plus velocity output. Walls are static, so PreviousWorld is not used — velocity is
// camera-only (ViewProjection vs PreviousViewProjection).
struct WallVertexOutV
{
    float4 position : SV_Position0;
    float4 color : COLOR0;
    float3 texCoord : TEXCOORD0;
    float4 modelPos : TEXCOORD1;
    float4 currClip : TEXCOORD2;
    float4 prevClip : TEXCOORD3;
};

WallVertexOutV vsWallRCV(WallVertexIn v)
{
    WallVertexOutV result;
    result.texCoord = v.texCoord;
    float4 wPos = mul(v.position, World);
    float4 finalPos = mul(wPos, ViewProjection);
    result.color = v.color;
    result.position = finalPos;
    result.modelPos = wPos;
    result.currClip = finalPos;
    result.prevClip = mul(wPos, PreviousViewProjection);
    return result;
}

PSOutputV psWallRCV(WallVertexOutV v)
{
    PSOutputV o;
    float4 mPos = v.modelPos;
    mPos.y = v.texCoord.y*2.95*3;
    float2 texC = v.texCoord.xy;
    texC.x = frac(texC.x);
    texC.y = frac(((v.texCoord.y % 1)-1/240)/-1.04);
#if SIMPLE
    float4 color = gammaMul(v.color * tex2D(TexSampler, texC), lightInterp(mPos, v.texCoord.z));
#else
    float4 color = gammaMul(v.color * tex2Dgrad(AnisoSampler, texC, ddx(v.texCoord.xy) * exp2(MipBias), ddy(v.texCoord.xy) * exp2(MipBias)), lightInterp(mPos, v.texCoord.z));
#endif
    if (SideMask != 0) {
        texC.x = frac(texC.x);
        texC.y = frac((frac(v.texCoord.y)*0.970)*(-(1-0.1185))+(1-texC.x)*0.1185*SideMask - 0.117);
    }
    float4 maskC = tex2D(MaskSampler, texC);
    color.a *= maskC.a;
    if (color.a < 0.1) discard;
    o.color = color;
    o.velocity = float4(ComputeVelocity(v.currClip, v.prevClip), PackDepth(v.currClip), 1);
    // Walls are planar, so a derivative-reconstructed face normal is safe here.
    o.normal = float4(normalize(cross(ddy(v.modelPos.xyz), ddx(v.modelPos.xyz))), 1);
    return o;
}

technique WallDrawWithVelocity
{
    pass Pass1
    {
#if SM4
        VertexShader = compile vs_4_0_level_9_3 vsWallRCV();
        PixelShader = compile ps_4_0_level_9_3 psWallRCV();
#else
        VertexShader = compile vs_3_0 vsWallRCV();
        PixelShader = compile ps_3_0 psWallRCV();
#endif;
    }
}

technique LMapDraw
{
	pass Pass1
	{
#if SM4
		VertexShader = compile vs_4_0_level_9_3 vsRC();
		PixelShader = compile ps_4_0_level_9_3 psLMapRC();
#else
		VertexShader = compile vs_3_0 vsRC();
		PixelShader = compile ps_3_0 psLMapRC();
#endif;
	}
}
