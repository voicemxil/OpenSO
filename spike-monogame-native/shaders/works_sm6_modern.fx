// COMPILES for DesktopVK + WindowsDX12 (DXC -> SPIR-V/DXIL). SM6 modern HLSL.
float4x4 WorldViewProjection;
Texture2D Src;
SamplerState LinSampler;
struct VSOut { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; };
struct PSOut { float4 c0 : SV_Target0; float4 c1 : SV_Target1; float4 c2 : SV_Target2; };
VSOut VS(float4 pos : POSITION0, float2 uv : TEXCOORD0) { VSOut o; o.Position = mul(pos, WorldViewProjection); o.UV = uv; return o; }
PSOut PS(VSOut i) { PSOut o; o.c0 = Src.SampleLevel(LinSampler, i.UV, 0); o.c1 = float4(i.UV,0,1); o.c2 = Src.Sample(LinSampler, i.UV); return o; }
technique T { pass P { VertexShader = compile vs_6_0 VS(); PixelShader = compile ps_6_0 PS(); } }
