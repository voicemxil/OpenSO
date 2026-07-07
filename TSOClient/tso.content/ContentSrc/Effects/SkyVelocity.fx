// SkyVelocity.fx — sky dome (BasicEffect replacement) that also writes screen-space velocity to MRT1.
// Sky is at infinity: velocity is camera-rotation only, depth forced FAR (1).

float4x4 MVP;        // current World * View * Projection (dome uses translation-zeroed view)
float4x4 PrevMVP;    // previous frame's MVP — velocity comes from the delta (camera rotation)
float    Alpha;      // dome alpha (weather fade), matches BasicEffect.Alpha
float    Exposure;   // sky brightness scale (< 1 tames the sunrise/sunset glow band)

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

// Current-frame TAA jitter (NDC). MVP is jittered; subtract so velocity is jitter-free.
// PrevMVP is supplied un-jittered by AbstractSkyDome.
float2 JitterNDC;

float2 ComputeVelocity(float4 curr, float4 prev)
{
    float cw = max(curr.w, 1e-4);
    float pw = max(prev.w, 1e-4);
    float2 c = curr.xy / cw - JitterNDC;
    float2 p = prev.xy / pw;
    return clamp((c - p) * float2(0.5, -0.5), -0.5, 0.5); // wide clamp — a tight one saturates real motion (see Vitaboy.fx)
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
    c.rgb *= Exposure;
    c.a *= Alpha;
    // Triangular-PDF dither (~±1 LSB, two hashes) hides 8-bit banding in the smooth gradient.
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
