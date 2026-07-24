#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_3
#define PS_SHADERMODEL ps_4_0_level_9_3
#endif

#include "LightingCommon.fx"

float4x4 Projection;
float4x4 View;
float4x4 World;
float4x4 InvRotation;
float4x4 InvXZRotation;

float BaseAlt;
float Time;
float TimeRate;
float StopTime; //NANI
float Frequency;

float4 Parameters1;
float4 Parameters2;
float4 Parameters3;
float4 Parameters4;

float Stories;
float ClipLevel;
float2 BpSize;
float4 Color;
float4 SubColor;
float3 CameraVelocity;

texture BaseTex;
sampler TexSampler = sampler_state {
	texture = <BaseTex>;
	AddressU = Wrap;
	AddressV = Wrap;
	MIPFILTER = LINEAR; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture IndoorsTex;
sampler IndoorsSampler = sampler_state {
	texture = <IndoorsTex>;
	AddressU = CLAMP;
	AddressV = CLAMP;
	MIPFILTER = POINT; MINFILTER = POINT; MAGFILTER = POINT;
};

struct ParticleInput
{
	float4 Position : SV_POSITION;
	float3 ModelPosition : TEXCOORD0;
	float3 TexCoord : TEXCOORD1;
};

struct ParticleOutput
{
	float4 Position : SV_POSITION;
	float2 TexCoord : TEXCOORD0;
	float4 Color : TEXCOORD1;
	float4 ModelPos : TEXCOORD2;
	// Current/previous clip positions for the rain velocity pass (RainVS fills them; other VSes
	// zero-init). Both go through the SAME jittered VP, so the TAA jitter cancels in the velocity
	// subtraction — no JitterNDC term, unlike writers whose previous matrix is un-jittered.
	float4 CurrClip : TEXCOORD3;
	float4 PrevClip : TEXCOORD4;
};

float4 Billboard(float4 posIn, float4 modelPos) {
	return posIn + mul(modelPos, InvRotation);
}

float4 XZBillboard(float4 posIn, float4 modelPos) {
	return posIn + mul(modelPos, InvXZRotation);
}

float3 RotateXY(float3 posIn, float angle) {
	float s = sin(angle);
	float c = cos(angle);
	posIn.xy = float2(posIn.x * c + posIn.y * s, posIn.y * c - posIn.x * s);
	return posIn;
}

float2 NegMod(float2 value, float2 mod) {
	return ((value % mod) - mod) % mod;
}

//Parameters:
//miny, yrange, fall speed, fall speed variation
//wind x, wind z, wind variation, rotation variation
//minx, xrange, minz, zrange
//scale
ParticleOutput SnowVS(in ParticleInput input)
{
	ParticleOutput output = (ParticleOutput)0;

	float4 boxCtr = mul(float4(0, 0, 0, 1), World);

	float fallSpeed = Parameters1.z + sin(input.Position.y * 1000) * Parameters1.w;
	float rotSpeed = sin(input.Position.y * 1000) * Parameters2.w;
	float repeatTime = fallSpeed / Parameters1.y;
	float realTime = (Time / repeatTime) % 1;
	float yFrac = (input.Position.y - (Parameters1.x + boxCtr.y)) / Parameters1.y;
	yFrac = frac(yFrac - realTime);

	float newY = Parameters1.x + yFrac * Parameters1.y;
	
	float2 windSpeed = Parameters2.xy + float2(sin(input.Position.x * 1000), sin(input.Position.z * 1000)) * Parameters2.z;
	float2 xz = input.Position.xz + (realTime) * windSpeed;

	float2 xzbase = (Parameters3.xz + boxCtr.xz); //test
	xz = NegMod((xz - xzbase), Parameters3.yw) + xzbase;

	float flakeSize = (sin(input.Position.y * 1000)*0.15 + 1) * Parameters4.x;
	float4 realCtr = float4(xz.x, newY + boxCtr.y, xz.y, 1);
	float4 realPos = Billboard(realCtr, float4(RotateXY(input.ModelPosition, rotSpeed*realTime) * flakeSize, 1));

	output.TexCoord = input.TexCoord.xy;
	output.ModelPos = realPos / 2; //not sure why i need to do this
	output.ModelPos.y -= BaseAlt;
	output.Position = mul(realPos, mul(View, Projection));
	output.Color = float4(1, 1, 1, 1) * min(1, (0.5 - abs(yFrac - 0.5)) * 20) * min(1, ClipLevel*(2.95*3) - output.ModelPos.y) * Color;

	return output;
}

//Parameters:
//miny, yrange, fall speed, fall speed variation
//wind x, wind z, wind variation, width (0.3)
//minx, xrange, minz, zrange
ParticleOutput RainVS(in ParticleInput input)
{
	ParticleOutput output = (ParticleOutput)0;

	float4 boxCtr = mul(float4(0, 0, 0, 1), World);

	float fallSpeed = Parameters1.z + sin(input.Position.y * 1000) * Parameters1.w;
	float repeatTime = fallSpeed / Parameters1.y;
	float realTime = (Time / repeatTime) % 1;
	float yFrac = (input.Position.y - (Parameters1.x + boxCtr.y)) / Parameters1.y;
	yFrac = frac(yFrac - realTime);

	float newY = Parameters1.x + yFrac * Parameters1.y;

	float2 windSpeed = Parameters2.xy + float2(sin(input.Position.x * 1000), sin(input.Position.z * 1000)) * Parameters2.z;
	float2 xz = input.Position.xz + (realTime) * windSpeed;

	float2 xzbase = (Parameters3.xz + boxCtr.xz); //test
	xz = NegMod((xz - xzbase), Parameters3.yw) + xzbase;

	float4 realCtr = float4(xz.x, newY + boxCtr.y, xz.y, 1);
	float2 windDelta = (windSpeed * (TimeRate / repeatTime)) / -2;
	float3 delta = float3(windDelta.x, (Parameters1.y * TimeRate / repeatTime), windDelta.y) * 4 + CameraVelocity;
	float4 lastCtr = realCtr - float4(delta, 0);

	float3 newModelPosition = input.ModelPosition;
	newModelPosition.x *= Parameters2.w;
	newModelPosition.y = input.ModelPosition.y * delta.y + Parameters2.w;

	float4 realPos = XZBillboard(realCtr, float4(newModelPosition, 1));

	realPos.xz += input.ModelPosition.y * delta.xz;

	output.TexCoord = input.TexCoord.xy;
	output.ModelPos = realPos / 2; //not sure why i need to do this
	output.ModelPos.y -= BaseAlt;
	output.Position = mul(realPos, mul(View, Projection));
	// The streak's previous-frame position is the whole billboard translated back by the per-frame
	// delta already computed for the stretch — projected through the same VP for the velocity pass.
	output.CurrClip = output.Position;
	output.PrevClip = mul(realPos - float4(delta, 0), mul(View, Projection));
	output.Color = float4(1, 1, 1, 1) * min(1, (0.5 - abs(yFrac - 0.5)) * 20) * min(1, ClipLevel*(2.95 * 3) - output.ModelPos.y) * Color;

	return output;
}

//Parameters:
//(deltax, deltay, deltaz, gravity)
//(deltavar, rotdeltavar, size, sizevel)
//(duration, fadein, fadeout, sizevar)
ParticleOutput GenericBoxVS(in ParticleInput input)
{
	ParticleOutput output = (ParticleOutput)0;

	float4 boxCtr = mul(float4(0, 0, 0, 1), World);
	float repeatTime = Parameters3.x;
	//if all particles created and died at the same time things would be pretty stupid.
	//calculate a "random" delta to phase offset this particle's animation.
	float3 rands = float3(sin(input.Position.x * 1000), sin(input.Position.y * 1000), sin(input.Position.z * 1000));

	float timeDelta = input.TexCoord.z * Frequency;
	float ltTime = ((Time + timeDelta) / Frequency) % 1.0;
	ltTime *= Frequency / repeatTime;

	float realTime = ltTime * repeatTime;

	float3 positionMod = (Parameters1.xyz + Parameters2.x*rands) * realTime;
	positionMod.y += Parameters1.w * realTime*realTime;

	float rotSpeed = sin(input.Position.y * 1000) * Parameters2.y;

	float flakeSize = sin(input.Position.y * 1245)*Parameters2.z + Parameters3.w + realTime * Parameters2.w;
	float4 realCtr = mul((input.Position + float4(positionMod, 0)), World);
	if (ltTime > 1) flakeSize = 0;
	float4 realPos = Billboard(realCtr, float4(RotateXY(input.ModelPosition, (rands.x*3.14)+rotSpeed*realTime) * flakeSize, 1));

	output.TexCoord = input.TexCoord.xy;
	output.ModelPos = realPos / 2; //not sure why i need to do this
	output.ModelPos.y -= BaseAlt;
	output.Position = mul(realPos, mul(View, Projection));

	float opacity;
	float startTime = Time - realTime;
	if (startTime > StopTime || startTime < 0.0) { //if we started after this emitter stopped, then don't show us at all.
		opacity = 0.0;
	} else if (ltTime < Parameters3.y) {
		opacity = min(1.0, ltTime / Parameters3.y);
	} else {
		opacity = min(1.0, (1-ltTime) / Parameters3.z);
	}

	float baseColInt = Parameters4.w * rands.z;
	output.Color = opacity * float4(lerp(Color.xyz, Parameters4.xyz, baseColInt + ltTime*(1.0-baseColInt)), Color.w);

	return output;
}

float dpth(float4 v) {
#if SM4
	return v.a;
#else
	return v.r;
#endif
}

// Extra (0,0,0,0) to COLOR1: with MRT1 bound and ColorWriteChannels1=Alpha this invalidates the velocity
// mask at particle pixels, so motion blur falls through them instead of smearing them along the velocity
// of the object beneath. Rain (additive blend) keeps prev alpha — MonoGame exposes no per-RT blend.
struct PSOutputV { float4 color : COLOR0; float4 velocity : COLOR1; };

PSOutputV MainPS(ParticleOutput input)
{
	PSOutputV o;
	float level = (input.ModelPos.y) / (2.95*3);
	float indoorsLevel = round(dpth(tex2D(IndoorsSampler, input.ModelPos.xz / BpSize))*Stories);
	if (level >= ClipLevel || indoorsLevel > max(0.0, level)) discard;
	o.color = gammaMul(tex2D(TexSampler, input.TexCoord)*input.Color * Color, lightInterpClamp(input.ModelPos, 1)) - SubColor;
	o.velocity = float4(0, 0, 0, 0);
	return o;
}

PSOutputV SimplePS(ParticleOutput input)
{
	PSOutputV o;
	o.color = tex2D(TexSampler, input.TexCoord)*input.Color;
	o.velocity = float4(0, 0, 0, 0);
	return o;
}

PSOutputV RainPS(ParticleOutput input)
{
	PSOutputV o;
	float level = (input.ModelPos.y) / (2.95 * 3);
	float indoorsLevel = round(dpth(tex2D(IndoorsSampler, input.ModelPos.xz / BpSize))*Stories);
	if (level >= ClipLevel || indoorsLevel > max(0.0, level)) discard;
	o.color = gammaMul(((1-cos(input.TexCoord.y*3.1415*2)) * (1 - cos(input.TexCoord.x*3.1415 * 2))/4) *input.Color, lightInterpClamp(input.ModelPos, 1)) - SubColor;
	o.velocity = float4(0, 0, 0, 0);
	return o;
}

PSOutputV RainSimplePS(ParticleOutput input)
{
	PSOutputV o;
	o.color = ((1 - cos(input.TexCoord.y*3.1415 * 2)) * (1 - cos(input.TexCoord.x*3.1415 * 2)) / 4) * input.Color - SubColor;
	o.velocity = float4(0, 0, 0, 0);
	return o;
}

// Rain velocity pass (second draw, MRT1 only — RT0 is write-masked by the blend state): writes the
// streak's TRUE screen velocity + normalized linear depth + valid mask on the streak core, so the
// temporal resolve sees rain as fast moving geometry (motion caps arm, drops stop being averaged
// away against deeply-trusted background history) instead of maskless transient noise. Additive
// colour blending cannot coexist with a velocity overwrite in one pass: MonoGame blend functions
// are shared across render targets, only the write masks are per-target.
PSOutputV RainVelocityPS(ParticleOutput input)
{
	PSOutputV o;
	float level = (input.ModelPos.y) / (2.95 * 3);
	float indoorsLevel = round(dpth(tex2D(IndoorsSampler, input.ModelPos.xz / BpSize))*Stories);
	if (level >= ClipLevel || indoorsLevel > max(0.0, level)) discard;
	float shape = (1 - cos(input.TexCoord.y*3.1415 * 2)) * (1 - cos(input.TexCoord.x*3.1415 * 2)) / 4;
	// Low threshold ON PURPOSE: any visibly-covering part of a drop must declare itself, or the TAA
	// treats it as one-frame flicker and averages it into the converged history — small/distant
	// drops vanished and the rain read sparser than without TAA. Writing velocity/depth on these
	// faint pixels is harmless: the resolve treats the reactive band as structurally unwritten
	// (no dilation/range/thin-line participation), so only the reactive mask has effect.
	if (shape * input.Color.a < 0.05) discard;
	float cw = max(input.CurrClip.w, 1e-4);
	float pw = max(input.PrevClip.w, 1e-4);
	float2 v = clamp((input.CurrClip.xy / cw - input.PrevClip.xy / pw) * float2(0.5, -0.5), -0.5, 0.5);
	o.color = float4(0, 0, 0, 0);
	// Mask 0.5 = valid + FULL reactivity in the a = 1 - 0.5*r band (FSR's particle reactive mask):
	// the resolve caps trust to near-raw and drops the Karis dark-bias on drop pixels, so streaks
	// render at full presence instead of a two-frame half-intensity average.
	o.velocity = float4(v, saturate(input.CurrClip.w / 800.0), 0.5);
	return o;
}

PSOutputV ParticlePS(ParticleOutput input)
{
	PSOutputV o;
	if (input.Color.a == 0) discard;
	o.color = gammaMul(tex2D(TexSampler, input.TexCoord)*input.Color, lightProcess(input.ModelPos)) - SubColor;
	o.velocity = float4(0, 0, 0, 0);
	return o;
}

//snow particles that follow the camera
technique SnowParticle
{
	pass P0
	{
		VertexShader = compile VS_SHADERMODEL SnowVS();
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};

technique SnowParticleSimple
{
	pass P0
	{
		VertexShader = compile VS_SHADERMODEL SnowVS();
		PixelShader = compile PS_SHADERMODEL SimplePS();
	}
};

//rain particles that follow the camera
technique RainParticle
{
	pass P0
	{
		VertexShader = compile VS_SHADERMODEL RainVS();
		PixelShader = compile PS_SHADERMODEL RainPS();
	}
};

technique RainSimpleParticle
{
	pass P0
	{
		VertexShader = compile VS_SHADERMODEL RainVS();
		PixelShader = compile PS_SHADERMODEL RainSimplePS();
	}
};

//particles with fixed start location, but various deltas and duration. eg. smoke, fire
technique GenericBoxParticle
{
	pass P0
	{
		VertexShader = compile VS_SHADERMODEL GenericBoxVS();
		PixelShader = compile PS_SHADERMODEL ParticlePS();
	}
};

// APPENDED LAST: the mode*2+depth technique indexing above must stay stable. Selected by name from
// ParticleComponent's velocity pass (missing in an old xnb -> the pass is skipped, null-safe).
technique RainVelocity
{
	pass P0
	{
		VertexShader = compile VS_SHADERMODEL RainVS();
		PixelShader = compile PS_SHADERMODEL RainVelocityPS();
	}
};
