// FSR.fx — AMD FidelityFX Super Resolution 1.0 (EASU + RCAS) for OpenSO.
//
// Faithful port of AMD's reference ffx_fsr1.h (MIT licensed, Copyright (c) 2021 Advanced Micro Devices,
// Inc.). The reference gathers 4 texels per fetch; the engine's effects target ps_4_0 (DX HiDef) / ps_3_0
// (GL), which don't guarantee gather4, so the same 12-/5-tap kernels are sampled individually instead —
// the arithmetic (FsrEasuSetF direction/length, FsrEasuTapF Lanczos window, dering, per-channel RCAS lobe)
// is unchanged from the reference. Two full-screen techniques over a source texture:
//   * EASU — Edge-Adaptive Spatial Upsampling (render scale < 1 upscale).
//   * RCAS — Robust Contrast-Adaptive Sharpening (sharpening slider; Sharpness 0..1).

float2 SourceSize;   // (1/srcW, 1/srcH) — inverse source texel size
float  Sharpness;    // RCAS strength 0..1 (FSR con.x; 0 = none)

texture tex : Diffuse;
sampler texSampler = sampler_state {
    texture = <tex>;
    AddressU = CLAMP; AddressV = CLAMP; AddressW = CLAMP;
    MIPFILTER = NONE; MINFILTER = LINEAR; MAGFILTER = LINEAR;
};

struct VSIn  { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };
struct VSOut { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };

VSOut VS(VSIn input)
{
    VSOut o = (VSOut)0;
    o.Position = input.Position;
    o.Coord = input.Coord;
    o.Coord.y = 1 - o.Coord.y; // match SSAA.fx/FXAA.fx fullscreen convention
    return o;
}

// ---------------------------------------------------------------------------- EASU
// Approximate luma * 2 (FSR's cheap luma).
float EasuLuma(float3 c) { return c.b * 0.5 + (c.r * 0.5 + c.g); }

// FsrEasuTapF — accumulate one tap with the anisotropic lanczos2-approx window.
void EasuTap(inout float3 aC, inout float aW, float2 off, float2 dir, float2 len2, float lob, float clp, float3 c)
{
    float2 v;
    v.x = (off.x * dir.x) + (off.y * dir.y);
    v.y = (off.x * -dir.y) + (off.y * dir.x);
    v *= len2;
    float d2 = v.x * v.x + v.y * v.y;
    d2 = min(d2, clp);
    float wB = (2.0 / 5.0) * d2 - 1.0;
    float wA = lob * d2 - 1.0;
    wB *= wB;
    wA *= wA;
    wB = (25.0 / 16.0) * wB - (25.0 / 16.0 - 1.0);
    float w = wB * wA;
    aC += c * w; aW += w;
}

// FsrEasuSetF — accumulate gradient direction and length for one quadrant.
void EasuSet(inout float2 dir, inout float len, float2 pp, bool biS, bool biT, bool biU, bool biV,
             float lA, float lB, float lC, float lD, float lE)
{
    float w = 0.0;
    if (biS) w = (1.0 - pp.x) * (1.0 - pp.y);
    if (biT) w =        pp.x  * (1.0 - pp.y);
    if (biU) w = (1.0 - pp.x) *        pp.y;
    if (biV) w =        pp.x  *        pp.y;
    float dc = lD - lC;
    float cb = lC - lB;
    float lenX = max(abs(dc), abs(cb));
    lenX = 1.0 / max(lenX, 1e-6);
    float dirX = lD - lB;
    dir.x += dirX * w;
    lenX = saturate(abs(dirX) * lenX);
    lenX *= lenX;
    len += lenX * w;
    float ec = lE - lC;
    float ca = lC - lA;
    float lenY = max(abs(ec), abs(ca));
    lenY = 1.0 / max(lenY, 1e-6);
    float dirY = lE - lA;
    dir.y += dirY * w;
    lenY = saturate(abs(dirY) * lenY);
    lenY *= lenY;
    len += lenY * w;
}

float4 EASU_PS(VSOut input) : COLOR0
{
    float2 rcpIn = SourceSize;        // 1/srcSize
    float2 inSize = 1.0 / SourceSize;
    float2 pp = input.Coord * inSize - 0.5; // source pixel space (input viewport == input size)
    float2 fp = floor(pp);
    float2 pf = pp - fp;              // fractional position within the 'f' texel
    float2 b0 = (fp + 0.5) * rcpIn;   // UV of 'f' texel center

    // 12-tap kernel (offsets relative to 'f', individual samples in place of gather4):
    //     b c
    //   e f g h
    //   i j k l
    //     n o
    float3 cb = tex2D(texSampler, b0 + float2( 0,-1) * rcpIn).rgb;
    float3 cc = tex2D(texSampler, b0 + float2( 1,-1) * rcpIn).rgb;
    float3 ce = tex2D(texSampler, b0 + float2(-1, 0) * rcpIn).rgb;
    float3 cf = tex2D(texSampler, b0 + float2( 0, 0) * rcpIn).rgb;
    float3 cg = tex2D(texSampler, b0 + float2( 1, 0) * rcpIn).rgb;
    float3 ch = tex2D(texSampler, b0 + float2( 2, 0) * rcpIn).rgb;
    float3 ci = tex2D(texSampler, b0 + float2(-1, 1) * rcpIn).rgb;
    float3 cj = tex2D(texSampler, b0 + float2( 0, 1) * rcpIn).rgb;
    float3 ck = tex2D(texSampler, b0 + float2( 1, 1) * rcpIn).rgb;
    float3 cl = tex2D(texSampler, b0 + float2( 2, 1) * rcpIn).rgb;
    float3 cn = tex2D(texSampler, b0 + float2( 0, 2) * rcpIn).rgb;
    float3 co = tex2D(texSampler, b0 + float2( 1, 2) * rcpIn).rgb;

    float bL = EasuLuma(cb), cL = EasuLuma(cc), eL = EasuLuma(ce), fL = EasuLuma(cf);
    float gL = EasuLuma(cg), hL = EasuLuma(ch), iL = EasuLuma(ci), jL = EasuLuma(cj);
    float kL = EasuLuma(ck), lL = EasuLuma(cl), nL = EasuLuma(cn), oL = EasuLuma(co);

    float2 dir = float2(0, 0);
    float len = 0.0;
    EasuSet(dir, len, pf, true,  false, false, false, bL, eL, fL, gL, jL);
    EasuSet(dir, len, pf, false, true,  false, false, cL, fL, gL, hL, kL);
    EasuSet(dir, len, pf, false, false, true,  false, fL, iL, jL, kL, nL);
    EasuSet(dir, len, pf, false, false, false, true,  gL, jL, kL, lL, oL);

    float2 dir2 = dir * dir;
    float dirR = dir2.x + dir2.y;
    bool zro = dirR < (1.0 / 32768.0);
    dirR = rsqrt(max(dirR, 1e-6));
    dirR = zro ? 1.0 : dirR;
    dir.x = zro ? 1.0 : dir.x;
    dir *= dirR;
    len = len * 0.5;
    len *= len;
    float stretch = (dir.x * dir.x + dir.y * dir.y) / max(abs(dir.x), abs(dir.y));
    float2 len2 = float2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
    float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
    float clp = 1.0 / lob;

    // Dering bounds from the 4 nearest (f, g, j, k).
    float3 mn4 = min(min(cf, cg), min(cj, ck));
    float3 mx4 = max(max(cf, cg), max(cj, ck));

    float3 aC = float3(0, 0, 0);
    float aW = 0.0;
    EasuTap(aC, aW, float2( 0,-1) - pf, dir, len2, lob, clp, cb);
    EasuTap(aC, aW, float2( 1,-1) - pf, dir, len2, lob, clp, cc);
    EasuTap(aC, aW, float2(-1, 1) - pf, dir, len2, lob, clp, ci);
    EasuTap(aC, aW, float2( 0, 1) - pf, dir, len2, lob, clp, cj);
    EasuTap(aC, aW, float2( 0, 0) - pf, dir, len2, lob, clp, cf);
    EasuTap(aC, aW, float2(-1, 0) - pf, dir, len2, lob, clp, ce);
    EasuTap(aC, aW, float2( 1, 1) - pf, dir, len2, lob, clp, ck);
    EasuTap(aC, aW, float2( 2, 1) - pf, dir, len2, lob, clp, cl);
    EasuTap(aC, aW, float2( 2, 0) - pf, dir, len2, lob, clp, ch);
    EasuTap(aC, aW, float2( 1, 0) - pf, dir, len2, lob, clp, cg);
    EasuTap(aC, aW, float2( 1, 2) - pf, dir, len2, lob, clp, co);
    EasuTap(aC, aW, float2( 0, 2) - pf, dir, len2, lob, clp, cn);

    float3 pix = min(mx4, max(mn4, aC / aW));
    return float4(pix, 1.0);
}

// ---------------------------------------------------------------------------- RCAS
float4 RCAS_PS(VSOut input) : COLOR0
{
    float2 inv = SourceSize; // source == screen resolution at this stage
    //    b
    //  d e f
    //    h
    float3 b = tex2D(texSampler, input.Coord + float2(0, -inv.y)).rgb;
    float3 d = tex2D(texSampler, input.Coord + float2(-inv.x, 0)).rgb;
    float3 e = tex2D(texSampler, input.Coord).rgb;
    float3 f = tex2D(texSampler, input.Coord + float2(inv.x, 0)).rgb;
    float3 h = tex2D(texSampler, input.Coord + float2(0, inv.y)).rgb;

    // Per-channel min/max of the ring, then a single shared lobe (FSR RCAS).
    float3 mn4 = min(min(b, d), min(f, h));
    float3 mx4 = max(max(b, d), max(f, h));
    float3 hitMin = min(mn4, e) / (4.0 * mx4 + 1e-4);
    float3 hitMax = (1.0 - max(mx4, e)) / (4.0 * mn4 - 4.0 - 1e-4);
    float3 lobeRGB = max(-hitMin, hitMax);
    float lobe = max(-(0.25 - 1.0 / 16.0), min(max(max(lobeRGB.r, lobeRGB.g), lobeRGB.b), 0.0)) * Sharpness;
    float rcpL = 1.0 / (4.0 * lobe + 1.0);
    float3 pix = (lobe * (b + d + h + f) + e) * rcpL;
    return float4(pix, 1.0);
}

technique EASU
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 EASU_PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 EASU_PS();
#endif
    }
}

technique RCAS
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 RCAS_PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 RCAS_PS();
#endif
    }
}
