Texture2D<float4> SourceTexture : register(t0);
SamplerState PointSampler : register(s0);

cbuffer BlurConstants : register(b0)
{
    float2 SourceSize;
    float2 TexelStep;
    // radius, sigma, reserved, reserved
    float4 BlurParameters;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

float4 main(PS_INPUT input) : SV_TARGET
{
    int radius = clamp((int)round(BlurParameters.x), 0, 64);
    float sigma = max(BlurParameters.y, 0.1);
    float4 total = 0.0;
    float weightTotal = 0.0;
    [loop]
    for (int offset = -radius; offset <= radius; offset++)
    {
        float weight = exp(-0.5 * (offset * offset) / (sigma * sigma));
        total += SourceTexture.SampleLevel(PointSampler, input.TexCoord + TexelStep * offset, 0.0) * weight;
        weightTotal += weight;
    }
    return total / max(weightTotal, 1e-6);
}
