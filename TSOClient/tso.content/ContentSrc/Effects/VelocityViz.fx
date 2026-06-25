// VelocityViz.fx — diagnostic visualizer for the MRT1 velocity buffer.
//
// When enabled, the resolve chain skips everything else and renders this pass directly to the screen.
// The output encodes per-pixel velocity so you can see, at a glance, which shaders are writing valid
// velocity and which aren't:
//
//   alpha == 0  (no velocity written)            -> BLACK
//   alpha == 1, velocity ~= 0 (good stationary)  -> mid GRAY (~0.5, 0.5, 0.5)
//   alpha == 1, +X velocity (right)              -> RED bias
//   alpha == 1, -X velocity (left)               -> CYAN bias (low R)
//   alpha == 1, +Y velocity (down in UV)         -> GREEN bias
//   alpha == 1, -Y velocity (up in UV)           -> MAGENTA bias (low G)
//   Blue channel = validity tint (~0.5 where written, 0 where unwritten — separates "zero velocity"
//   from "no velocity").
//
// A stationary scene with correctly-wired velocity should show: gray on objects/sims/(terrain when
// wired), black on sky / unwired surfaces. Panning the camera should turn moving surfaces uniformly
// red/green/etc. depending on pan direction. Anything OTHER than that pattern is a bug — wrong
// magnitude, wrong direction, per-vertex flicker, etc.

float Scale; // amplifies tiny velocities so they're visible. 30 reads typical pan as a clear color shift.

texture velocityTex;
sampler velocitySampler = sampler_state {
    texture = <velocityTex>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = NONE; MINFILTER = POINT; MAGFILTER = POINT;
};

struct VSIn  { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };
struct VSOut { float4 Position : SV_Position0; float2 Coord : TEXCOORD0; };

VSOut VS(VSIn input)
{
    VSOut o = (VSOut)0;
    o.Position = input.Position;
    o.Coord = input.Coord;
    o.Coord.y = 1 - o.Coord.y;
    return o;
}

float4 Viz_PS(VSOut input) : COLOR0
{
    float4 v = tex2D(velocitySampler, input.Coord);

    // alpha == 0 -> pure black: nothing wrote velocity at this pixel.
    if (v.a < 0.5) return float4(0, 0, 0, 1);

    // Encode velocity around mid-gray. R = vx*scale + 0.5, G = vy*scale + 0.5.
    float r = saturate(v.r * Scale + 0.5);
    float g = saturate(v.g * Scale + 0.5);
    // Blue tint at 0.5 indicates "velocity was written here" — distinguishes a true-zero velocity
    // (perfectly stationary object with valid wiring) from "no velocity buffer written" (black).
    return float4(r, g, 0.5, 1);
}

technique VelocityViz
{
    pass MainPass
    {
#if SM4
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 Viz_PS();
#else
        VertexShader = compile vs_3_0 VS();
        PixelShader  = compile ps_3_0 Viz_PS();
#endif
    }
}
