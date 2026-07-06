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

    if (DepthMode > 0.5)
    {
        // Dither +-1/2 display LSB (interleaved gradient noise) to hide 8-bit backbuffer
        // quantization of the fp32 depth.
        float ign = frac(52.9829189 * frac(dot(input.Coord * 1024.0, float2(0.06711056, 0.00583715))));
        float d = saturate(v.b + (ign - 0.5) / 255.0);
        return float4(d, d, d, 1);
    }

    float r = saturate(v.r * Scale + 0.5);
    float g = saturate(v.g * Scale + 0.5);
    // b = 0.5 marks "velocity written" — separates true-zero velocity from unwritten (black).
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
