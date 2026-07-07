// VelocityViz.fx — debug visualizer for the MRT1 velocity buffer.
// R/G bias around mid-gray = velocity direction, blue tint = velocity written, black = unwritten.

float Scale; // velocity amplification (~30 reads a typical pan as a clear color shift)
// 0 = velocity hue view, 1 = grayscale of the packed normalized linear depth (v.b, 0=near..1=far).
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

    // alpha 0 = nothing wrote velocity here.
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
