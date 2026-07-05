// FAILS for DesktopVK/DX12. This is the style ALL ~20 OpenSO shaders use today.
// DXC errors: 'unknown type name sampler2D', 'effect syntax is deprecated', tex2D unknown.
// Also fails ValidateShaderModels: "Invalid Vulkan vertex profile 'vs_3_0'! Requires vs_6_0."
texture Tex;
sampler2D TexS = sampler_state { texture = <Tex>; MINFILTER = LINEAR; };
struct VSOut { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; };
VSOut VS(float4 pos : POSITION0, float2 uv : TEXCOORD0) { VSOut o; o.Position=pos; o.UV=uv; return o; }
float4 PS(VSOut i) : SV_Target { return tex2D(TexS, i.UV); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
