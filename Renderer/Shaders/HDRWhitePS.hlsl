// Pixel shader for rendering a static HDR white color.

// The output is a floating-point color value suitable for an R16G16B16A16_Float render target.
// The color (0.7219, 0.7219, 0.7219) corresponds to 1000 nits
// in the ST.2084 (Perceptual Quantizer or PQ) electro-optical transfer function.
struct PS_INPUT
{
    float4 Pos : SV_POSITION;
    float2 Tex : TEXCOORD0;
};

float4 main(PS_INPUT input) : SV_Target
{
    float gradient = input.Tex.y;
    float3 color = lerp(float3(0,0,0), float3(0.7219f, 0.7219f, 0.7219f), gradient);
    return float4(color, 1.0f);
}