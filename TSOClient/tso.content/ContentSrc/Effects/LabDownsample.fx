// LabDownsample.fx — FSO.TAALab ONLY (the standalone TAA tuning harness under TSOClient/FSO.TAALab).
// NOT used by the game. It rides the shared shader compile (.github/scripts/compile-shaders.sh globs
// ContentSrc/Effects/*.fx) because DesktopGL cannot compile effects at runtime.
//
// Ground-truth reference downsample chain (see LabGame.RenderControl), fixing two reference flaws
// found in the tuner-v2 arc (2026-07-07):
//   1. GAMMA-SPACE AVERAGING biased the reference dark on high-contrast edges — averaging must
//      happen in LINEAR light (pow-2.2 approx sRGB). Comparison space stays gamma: only the
//      filter's averaging space changes, so the metric still compares displayed 8-bit colors.
//   2. BOX FILTERING passed reference aliasing — stair-step combing the metric then treated as
//      truth, actively resisting tunings that remove stipple. A sigma 0.44 OUTPUT-px Gaussian
//      replaces the final box average (graded edge transitions, alias-free reference).
//
// Techniques (both 2:1 downsamples driven on the lab's fullscreen quad):
//   BoxDecode — first chain stage: 4-tap point box of the supersampled scene, decoded to linear.
//               Output goes to an fp16 target; later 2:1 stages are plain bilinear blits on fp16
//               (bilinear IS the exact linear-light box once the data is linear).
//   GaussDown — final stage: 2x-res linear input -> output res, 6x6 separable-weight Gaussian
//               around the output pixel center, then encode back to gamma.
//               Sigma: target 0.44 out-px TOTAL; the box prefilter down to the 2x grid contributes
//               width-0.5-out-px uniform variance (0.5^2/12 = 0.0208), leaving a Gaussian sigma of
//               sqrt(0.44^2 - 0.0208) = 0.4157 out-px = 0.8314 px on the 2x source grid. Tap
//               distances from the output center (which sits on a source texel CORNER) are
//               0.5/1.5/2.5 source px, mirrored -> 6 taps per axis.

texture srcTex;
sampler srcSampler = sampler_state
{
    texture = <srcTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

float2 InvSrcSize; // 1 / source texture dimensions

struct VSIn  { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };
struct VSOut { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };

VSOut VS(VSIn input)
{
    VSOut o = (VSOut)0;
    o.Position = input.Position;
    o.Coord = input.Coord;
    o.Coord.y = 1 - o.Coord.y; // match the TAA/SSAA fullscreen convention
    return o;
}

float3 Decode(float3 c) { return pow(abs(c), 2.2); }
float3 Encode(float3 c) { return pow(abs(c), 1.0 / 2.2); }

// 2:1 box + gamma decode. The dest texel center maps to the CORNER of its 2x2 source quad, so the
// four source texel centers sit at +/-0.5 source px.
float4 PSBoxDecode(VSOut input) : COLOR0
{
    float2 o = 0.5 * InvSrcSize;
    float3 c = Decode(tex2D(srcSampler, input.Coord + float2(-o.x, -o.y)).rgb)
             + Decode(tex2D(srcSampler, input.Coord + float2( o.x, -o.y)).rgb)
             + Decode(tex2D(srcSampler, input.Coord + float2(-o.x,  o.y)).rgb)
             + Decode(tex2D(srcSampler, input.Coord + float2( o.x,  o.y)).rgb);
    return float4(c * 0.25, 1);
}

// 2:1 box + gamma encode — the BOX-reference final stage (lab toggle): identical footprint to
// BoxDecode but the input is already linear, and the result re-encodes to the comparison space.
// Box keeps the crispest honest average (the tuner does not target Gaussian softness) at the cost
// of passing some reference aliasing as truth; the Gaussian below is the anti-alias-honest option.
float4 PSBoxEncode(VSOut input) : COLOR0
{
    float2 o = 0.5 * InvSrcSize;
    float3 c = tex2D(srcSampler, input.Coord + float2(-o.x, -o.y)).rgb
             + tex2D(srcSampler, input.Coord + float2( o.x, -o.y)).rgb
             + tex2D(srcSampler, input.Coord + float2(-o.x,  o.y)).rgb
             + tex2D(srcSampler, input.Coord + float2( o.x,  o.y)).rgb;
    return float4(Encode(c * 0.25), 1);
}

// 2:1 Gaussian + gamma encode. Separable weights for tap distances 0.5/1.5/2.5 src px at
// sigma = 0.8314 src px, pre-normalized (sum of the mirrored 6 = 1).
static const float GW[3] = { 0.400561f, 0.094230f, 0.005209f };

float4 PSGaussDown(VSOut input) : COLOR0
{
    float3 acc = 0;
    [unroll]
    for (int j = 0; j < 6; j++)
    {
        // mirrored offsets -2.5,-1.5,-0.5,0.5,1.5,2.5 and their axis weights
        float dy = (j - 2.5) * InvSrcSize.y;
        float wy = GW[abs(j * 2 - 5) / 2]; // j 0..5 -> |2j-5| = 5,3,1,1,3,5 -> /2 -> 2,1,0,0,1,2
        [unroll]
        for (int i = 0; i < 6; i++)
        {
            float dx = (i - 2.5) * InvSrcSize.x;
            float wx = GW[abs(i * 2 - 5) / 2];
            acc += wx * wy * tex2D(srcSampler, input.Coord + float2(dx, dy)).rgb;
        }
    }
    return float4(Encode(acc), 1);
}

technique BoxDecode
{
    pass P0
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader = compile ps_4_0 PSBoxDecode();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader = compile ps_3_0 PSBoxDecode();
#endif
    }
}

technique BoxEncode
{
    pass P0
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader = compile ps_4_0 PSBoxEncode();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader = compile ps_3_0 PSBoxEncode();
#endif
    }
}

technique GaussDown
{
    pass P0
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader = compile ps_4_0 PSGaussDown();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader = compile ps_3_0 PSGaussDown();
#endif
    }
}
