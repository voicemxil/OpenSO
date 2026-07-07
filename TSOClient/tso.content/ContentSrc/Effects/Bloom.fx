// Bloom.fx — soft-knee bright-pass + Kawase dual-filter blur mips + additive composite (see BloomPass.cs).
// Mips are HalfVector4 so blurred highlights don't clip while accumulating (LDR engine).

float2 TexelSize;   // 1 / source-texture size (set per pass)
float  Threshold;   // luminance bright-pass threshold
float  Knee;        // soft-knee width around the threshold
float  Intensity;   // composite strength
float  UpsampleBlend; // per-mip upsample contribution (0..1, ~0.5-0.7) — keeps cascaded additive upsamples from compounding

texture sourceTex;
sampler sourceSampler = sampler_state {
    texture = <sourceTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

texture sceneTex;  // original scene color, for the composite pass
sampler sceneSampler = sampler_state {
    texture = <sceneTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
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

// Soft-knee bright-pass (Karis/COD).
float4 Prefilter_PS(VSOut input) : COLOR0
{
    float3 c = tex2D(sourceSampler, input.Coord).rgb;
    float br = max(c.r, max(c.g, c.b));
    float soft = br - Threshold + Knee;
    soft = clamp(soft, 0.0, 2.0 * Knee);
    soft = soft * soft / (4.0 * Knee + 1e-5);
    float contrib = max(soft, br - Threshold) / max(br, 1e-5);
    return float4(c * contrib, 1.0);
}

// Kawase dual-filter downsample: center (x4) + 4 diagonals, /8.
float4 Downsample_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float2 h = TexelSize;
    float4 sum = tex2D(sourceSampler, uv) * 4.0;
    sum += tex2D(sourceSampler, uv + float2(-h.x, -h.y));
    sum += tex2D(sourceSampler, uv + float2( h.x, -h.y));
    sum += tex2D(sourceSampler, uv + float2(-h.x,  h.y));
    sum += tex2D(sourceSampler, uv + float2( h.x,  h.y));
    return sum / 8.0;
}

// Kawase dual-filter upsample: 3x3 tent (corners x1, edges x2) /12, scaled by UpsampleBlend
// before the additive blend onto the destination mip.
float4 Upsample_PS(VSOut input) : COLOR0
{
    float2 uv = input.Coord;
    float2 h = TexelSize;
    float4 sum = tex2D(sourceSampler, uv + float2(-h.x,  0.0)) * 2.0;
    sum += tex2D(sourceSampler, uv + float2( h.x,  0.0)) * 2.0;
    sum += tex2D(sourceSampler, uv + float2( 0.0, -h.y)) * 2.0;
    sum += tex2D(sourceSampler, uv + float2( 0.0,  h.y)) * 2.0;
    sum += tex2D(sourceSampler, uv + float2(-h.x, -h.y));
    sum += tex2D(sourceSampler, uv + float2( h.x, -h.y));
    sum += tex2D(sourceSampler, uv + float2(-h.x,  h.y));
    sum += tex2D(sourceSampler, uv + float2( h.x,  h.y));
    return (sum / 12.0) * UpsampleBlend;
}

float4 Composite_PS(VSOut input) : COLOR0
{
    float3 scene = tex2D(sceneSampler, input.Coord).rgb;
    float3 bloom = tex2D(sourceSampler, input.Coord).rgb;
    return float4(scene + bloom * Intensity, 1.0);
}

#if SM4
#define VSP vs_4_0
#define PSP ps_4_0
#else
#define VSP vs_3_0
#define PSP ps_3_0
#endif

technique Prefilter  { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Prefilter_PS(); } }
technique Downsample { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Downsample_PS(); } }
technique Upsample   { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Upsample_PS(); } }
technique Composite  { pass P { VertexShader = compile VSP VS(); PixelShader = compile PSP Composite_PS(); } }
