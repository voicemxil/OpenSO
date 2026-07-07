// LabVelocity.fx — FSO.TAALab ONLY (the standalone TAA tuning harness under TSOClient/FSO.TAALab).
// NOT used by the game. It rides the shared shader compile (.github/scripts/compile-shaders.sh globs
// ContentSrc/Effects/*.fx) because DesktopGL cannot compile effects at runtime.
//
// Matrix-pair velocity writer mirroring the game's convention (see SkyVelocity.fx / RCObject.fx):
// each object is drawn as an explicit quad transformed by BOTH its current and previous frame's
// World*View*Projection; the PS derives per-pixel screen velocity from the clip-space delta, so
// arbitrary transforms (rotation included) produce an honest, spatially-varying velocity field.
// Output (HalfVector4 target, the game's velocity-buffer contract — see TAA.fx header):
//   rg = per-pixel screen-space velocity in UV/frame (y-down UV). Jitter-free: LabGame builds
//        CurrentWVP and PreviousWVP with the SAME current-frame jitter, so the jitter translation
//        cancels exactly in the NDC delta (the game instead subtracts JitterNDC — same result).
//   b  = normalized linear depth (0 = near .. 1 = far). Technique "Velocity" (flat 2D quads):
//        constant uniform. Technique "VelocityMasked" (real 3D game meshes): per-pixel
//        saturate(clip.w / 800) — the game's PackDepth convention (RCObject.fx).
//   a  = 1 (velocity written / valid)
//
// Technique "VelocityMasked" additionally samples MeshTex and discards texels with alpha < 0.5,
// so the velocity coverage of alpha-cutout game meshes (e.g. tree foliage) matches the color pass.
float4x4 CurrentWVP;   // current model * (jittered view) * projection
float4x4 PreviousWVP;  // previous frame's model * the SAME view/projection
float Depth;           // normalized linear depth for the whole object ("Velocity" technique only)

texture MeshTex;
sampler MeshTexSampler = sampler_state
{
    texture = <MeshTex>;
    AddressU = CLAMP;
    AddressV = CLAMP;
    MinFilter = LINEAR;
    MagFilter = LINEAR;
    MipFilter = NONE;
};

struct VSOutput
{
    float4 position : SV_Position;
    float4 currClip : TEXCOORD0;
    float4 prevClip : TEXCOORD1;
};

VSOutput VS(float4 position : POSITION0)
{
    VSOutput output;
    float4 curr = mul(position, CurrentWVP);
    output.position = curr;
    output.currClip = curr;
    output.prevClip = mul(position, PreviousWVP);
    return output;
}

// Guard tiny w like the game's velocity writers (avoids INF at grazing/degenerate projections).
float SafeW(float w)
{
    return max(abs(w), 1e-4) * sign(w);
}

float4 PS(VSOutput input) : COLOR0
{
    float2 currNDC = input.currClip.xy / SafeW(input.currClip.w);
    float2 prevNDC = input.prevClip.xy / SafeW(input.prevClip.w);
    // NDC delta -> UV/frame (y-down UV), clamped +/-0.5 like the game writers.
    float2 vel = clamp((currNDC - prevNDC) * float2(0.5, -0.5), -0.5, 0.5);
    return float4(vel.x, vel.y, Depth, 1.0);
}

// ---- Textured/masked variant for real game meshes (alpha cutout + per-pixel linear depth) ----

struct VSOutputTex
{
    float4 position : SV_Position;
    float4 currClip : TEXCOORD0;
    float4 prevClip : TEXCOORD1;
    float2 texCoord : TEXCOORD2;
};

VSOutputTex VSTex(float4 position : POSITION0, float2 texCoord : TEXCOORD0)
{
    VSOutputTex output;
    float4 curr = mul(position, CurrentWVP);
    output.position = curr;
    output.currClip = curr;
    output.prevClip = mul(position, PreviousWVP);
    output.texCoord = texCoord;
    return output;
}

float4 PSTex(VSOutputTex input) : COLOR0
{
    float4 tex = tex2D(MeshTexSampler, input.texCoord);
    clip(tex.a - 0.5); // match the color pass's alpha-test cutout coverage

    float2 currNDC = input.currClip.xy / SafeW(input.currClip.w);
    float2 prevNDC = input.prevClip.xy / SafeW(input.prevClip.w);
    float2 vel = clamp((currNDC - prevNDC) * float2(0.5, -0.5), -0.5, 0.5);
    // Per-pixel normalized LINEAR view distance — the game's PackDepth (RCObject.fx): clip.w / 800.
    return float4(vel.x, vel.y, saturate(input.currClip.w / 800.0), 1.0);
}

technique Velocity
{
    pass P0
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader = compile ps_4_0 PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader = compile ps_3_0 PS();
#endif
    }
}

technique VelocityMasked
{
    pass P0
    {
#if SM4
        VertexShader = compile vs_4_0 VSTex();
        PixelShader = compile ps_4_0 PSTex();
#else
        VertexShader = compile vs_3_0 VSTex();
        PixelShader = compile ps_3_0 PSTex();
#endif
    }
}
