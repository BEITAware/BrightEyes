// Present Pixel Shader
// Assumes the source texture is already in Rec.2020 + PQ (HDR10) normalized [0,1].
// Simply samples and outputs to the HDR10 swap chain backbuffer.

Texture2D SourceTexture : register(t0);
SamplerState Sampler : register(s0);

struct PS_INPUT
{
    float4 Pos : SV_POSITION;
    float2 Tex : TEXCOORD0;
};

float4 main(PS_INPUT input) : SV_Target
{
    float4 color = SourceTexture.Sample(Sampler, input.Tex);
    return color;
}


