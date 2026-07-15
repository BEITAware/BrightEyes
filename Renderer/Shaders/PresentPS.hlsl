// Direct presentation of an already-normalized Rec.2020 PQ texture.
// Hardware linear sampling is substantially cheaper than the previous
// four-tap bicubic path and uses generated mip levels while minifying.

Texture2D SourceTexture : register(t0);
SamplerState Sampler : register(s0);

struct PS_INPUT
{
    float4 Pos : SV_POSITION;
    float2 Tex : TEXCOORD0;
};

float4 main(PS_INPUT input) : SV_Target
{
    return SourceTexture.Sample(Sampler, input.Tex);
}
