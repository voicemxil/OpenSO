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
// 0 = velocity hue view (default). 1 = DEPTH view: grayscale of the packed normalized linear depth (v.b,
// 0=near..1=far); unwritten pixels stay black. Lets the same debug pass validate the depth the TAA
// disocclusion tests and motion blur's soft depth compare actually consume.
float DepthMode;

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

    // DEPTH view: grayscale of the packed normalized linear depth (near = dark, far = bright). Dither by
    // +-1/2 display LSB (interleaved gradient noise) so the 8-bit backbuffer's display quantization
    // dissolves; genuine source banding (steps far larger than one LSB) still shows.
    float ign = frac(52.9829189 * frac(dot(input.Coord * 1024.0, float2(0.06711056, 0.00583715))));
    float d = saturate(v.b + (ign - 0.5) / 255.0);
    float4 depthColor = float4(d, d, d, 1);

    // Velocity HUE view around mid-gray. R = vx*scale + 0.5, G = vy*scale + 0.5. Blue 0.5 = "written here".
    float4 hueColor = float4(saturate(v.r * Scale + 0.5), saturate(v.g * Scale + 0.5), 0.5, 1);

    // DepthMode is 0 (hue) or 1 (depth). Blend DIRECTLY by it — NO comparison. On MojoShader's ps_3_0
    // OpenGL path a uniform COMPARISON (`DepthMode > 0.5`, or `step(0.5, DepthMode)`) is mis-evaluated and
    // always fell through to the hue branch, even though the uniform's raw value arrives correctly (proven
    // by a direct-read diagnostic). Using the value straight in the lerp sidesteps the broken comparison.
    return lerp(hueColor, depthColor, saturate(DepthMode));
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
