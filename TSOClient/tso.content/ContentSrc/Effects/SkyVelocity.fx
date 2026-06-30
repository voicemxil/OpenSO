// SkyVelocity.fx — sky dome shader with velocity output for TAA / motion blur.
//
// Replaces MonoGame's BasicEffect for the sky dome so we can write screen-space velocity to MRT1. The
// dome is a textured gradient (no lighting, no vertex colour) — same visual as the BasicEffect path it
// replaces (TextureEnabled, LightingEnabled=false, DiffuseColor=1, Alpha). The sky is conceptually at
// infinity, so velocity is purely camera-rotation-induced and depth is forced FAR (1) so the motion-blur
// depth test treats it as background behind everything.

float4x4 MVP;        // current World * View * Projection (dome uses translation-zeroed view)
float4x4 PrevMVP;    // previous frame's MVP — velocity comes from the delta (camera rotation)
float    Alpha;      // dome alpha (weather fade), matches BasicEffect.Alpha
float    Exposure;   // sky brightness scale (< 1 tames the eye-burning white sun-glow band at sunrise/set)

texture SkyTex;
sampler SkyTexSampler = sampler_state {
    texture = <SkyTex>;
    AddressU = WRAP; AddressV = WRAP;
    MIPFILTER = LINEAR; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

struct VSIn  { float4 position : SV_Position0; float2 texCoord : TEXCOORD0; };
struct VSOut {
    float4 position : SV_Position0;
    float2 texCoord : TEXCOORD0;
    float4 currClip : TEXCOORD1;
    float4 prevClip : TEXCOORD2;
};
struct PSOut { float4 color : COLOR0; float4 velocity : COLOR1; float4 normal : COLOR2; };

VSOut SkyVS(VSIn input)
{
    VSOut o = (VSOut)0;
    float4 p = mul(input.position, MVP);
    o.position = p;
    o.texCoord = input.texCoord;
    o.currClip = p;
    o.prevClip = mul(input.position, PrevMVP);
    return o;
}

// Current-frame TAA jitter (NDC). MVP is jittered (TAA sampling); subtract the jitter from the current NDC
// so velocity is jitter-free. PrevMVP is supplied UN-jittered by AbstractSkyDome.
float2 JitterNDC;

float2 ComputeVelocity(float4 curr, float4 prev)
{
    float cw = max(curr.w, 1e-4);
    float pw = max(prev.w, 1e-4);
    float2 c = curr.xy / cw - JitterNDC;
    float2 p = prev.xy / pw;
    return clamp((c - p) * float2(0.5, -0.5), -0.05, 0.05);
}

// Cheap per-pixel hash (Dave Hoskins) -> [0,1), used for dither noise.
float DitherHash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

#if SM4
PSOut SkyPS(VSOut input)
{
    float2 ditherPx = input.position.xy; // SV_Position in the PS holds pixel coords
#else
PSOut SkyPS(VSOut input, float2 ditherPx : VPOS)
{
#endif
    PSOut o;
    float4 c = tex2D(SkyTexSampler, input.texCoord);
    c.rgb *= Exposure;       // tame the sunrise/sunset bright band (LDR; eyes burn at 1.0)
    c.a *= Alpha;
    // Kill 8-bit gradient banding: add triangular-PDF dither (~±1 LSB) before the framebuffer quantises the
    // smooth sky gradient. Two hashes -> triangular distribution (the distortion-free ideal for 1-LSB dither).
    float dth = (DitherHash(ditherPx) + DitherHash(ditherPx + 41.13) - 1.0) / 255.0;
    c.rgb = saturate(c.rgb + dth);
    o.color = c;
    // depth = 1 (FAR): the sky is at infinity / background. velocity.a = 1 marks it valid.
    o.velocity = float4(ComputeVelocity(input.currClip, input.prevClip), 1.0, 1.0);
    // Sky has no meaningful normal; mark invalid (.a=0) so GTAO skips it (treats as no-geometry).
    o.normal = float4(0, 1, 0, 0);
    return o;
}

technique DrawSky
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 SkyVS();
        PixelShader  = compile ps_4_0 SkyPS();
#else
        VertexShader = compile vs_3_0 SkyVS();
        PixelShader  = compile ps_3_0 SkyPS();
#endif
    }
}
