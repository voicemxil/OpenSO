// BillboardVelocity.fx — textured billboard (headline icons / speech bubbles) with velocity output.
//
// Replaces MonoGame's BasicEffect for the 3D headline billboards so they can write screen-space velocity
// + depth to MRT1 like every other scene element. Without it they were invisible to the TAA resolve's
// motion evidence: they animate (bob) and track their avatars with no velocity signal, so the temporal
// accumulation smeared them badly ("very ghosted" under TAAU). With true per-frame velocity (from the
// current vs previous billboard transform) they reproject exactly and accumulate cleanly.
//
// Conventions copied from SkyVelocity/Vitaboy:
//   velocity.rg = clamp((currNDC - JitterNDC - prevNDC) * (0.5, -0.5), +/-0.05)  (UV-space delta)
//   velocity.b  = normalized LINEAR view distance saturate(clip.w / 800)
//   velocity.a  = 1 (valid). MRT1 must be written OPAQUE (PPXDepthEngine.VelocityColorBlend).

float4x4 MVP;     // current World * View * Projection (jittered — TAA sampling)
float4x4 PrevMVP; // previous frame's billboard transform x previous UN-jittered ViewProjection
float2   JitterNDC;

texture BillboardTex;
sampler BillboardSampler = sampler_state {
    texture = <BillboardTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = LINEAR; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

struct VSIn  { float4 position : SV_Position0; float2 texCoord : TEXCOORD0; };
struct VSOut {
    float4 position : SV_Position0;
    float2 texCoord : TEXCOORD0;
    float4 currClip : TEXCOORD1;
    float4 prevClip : TEXCOORD2;
};
struct PSOut { float4 color : COLOR0; float4 velocity : COLOR1; };

VSOut BillboardVS(VSIn input)
{
    VSOut o = (VSOut)0;
    float4 p = mul(input.position, MVP);
    o.position = p;
    o.texCoord = input.texCoord;
    o.currClip = p;
    o.prevClip = mul(input.position, PrevMVP);
    return o;
}

float2 ComputeVelocity(float4 curr, float4 prev)
{
    float cw = max(curr.w, 1e-4);
    float pw = max(prev.w, 1e-4);
    float2 c = curr.xy / cw - JitterNDC;
    float2 p = prev.xy / pw;
    return clamp((c - p) * float2(0.5, -0.5), -0.05, 0.05);
}

PSOut BillboardPS(VSOut input)
{
    PSOut o;
    float4 c = tex2D(BillboardSampler, input.texCoord);
    // Fully transparent texels must not stamp velocity/depth over the scene behind them (the same
    // alpha-fringe rule as Vitaboy's velocity pass).
    if (c.a < 0.01) discard;
    o.color = c;
    o.velocity = float4(ComputeVelocity(input.currClip, input.prevClip), saturate(input.currClip.w / 800.0), 1.0);
    return o;
}

technique DrawBillboard
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 BillboardVS();
        PixelShader  = compile ps_4_0 BillboardPS();
#else
        VertexShader = compile vs_3_0 BillboardVS();
        PixelShader  = compile ps_3_0 BillboardPS();
#endif
    }
}
