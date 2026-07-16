Texture2D<float4> SourceTexture : register(t0);
SamplerState PointSampler : register(s0);

cbuffer BlurConstants : register(b0)
{
    float2 SourceSize;
    float2 TexelStep;
    // transfer, primaries, alpha representation, reference white
    float4 SourceParameters;
    // radius, sigma, decode input, nominal peak
    float4 BlurParameters;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

float SrgbToLinear(float value)
{
    return value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4);
}

float PqToNits(float value)
{
    value = saturate(value);
    const float m1 = 2610.0 / 16384.0;
    const float m2 = 2523.0 / 4096.0 * 128.0;
    const float c1 = 3424.0 / 4096.0;
    const float c2 = 2413.0 / 4096.0 * 32.0;
    const float c3 = 2392.0 / 4096.0 * 32.0;
    float p = pow(value, 1.0 / m2);
    return 10000.0 * pow(max(p - c1, 0.0) / max(c2 - c3 * p, 1e-6), 1.0 / m1);
}

float3 Rec2020ToRec709(float3 c)
{
    return float3(
        dot(c, float3(1.660491, -0.587641, -0.072850)),
        dot(c, float3(-0.124550, 1.132900, -0.008349)),
        dot(c, float3(-0.018151, -0.100579, 1.118730)));
}

float3 DisplayP3ToRec2020(float3 c)
{
    return float3(
        dot(c, float3(0.7538330, 0.1985970, 0.0475700)),
        dot(c, float3(0.0457440, 0.9417770, 0.0124790)),
        dot(c, float3(-0.0012100, 0.0176020, 0.9836090)));
}

float3 ProPhotoToRec2020(float3 c)
{
    return mul(c, float3x3(
        0.769012, 0.141793, 0.089195,
        0.075162, 0.844427, 0.080411,
        0.025677, 0.042351, 0.931972));
}

float3 DecodeTransfer(float3 value)
{
    float transfer = SourceParameters.x;
    float referenceWhite = max(SourceParameters.w, 0.001);
    if (transfer < 0.5)
        return float3(SrgbToLinear(value.r), SrgbToLinear(value.g), SrgbToLinear(value.b));
    if (transfer < 1.5)
        return float3(PqToNits(value.r), PqToNits(value.g), PqToNits(value.b)) / referenceWhite;
    if (transfer < 2.5)
    {
        float3 e = max(value, 0.0);
        return float3(
            e.r <= 0.5 ? e.r * e.r / 3.0 : (exp((e.r - 0.5599107) / 0.1788328) + 0.2846689) / 12.0,
            e.g <= 0.5 ? e.g * e.g / 3.0 : (exp((e.g - 0.5599107) / 0.1788328) + 0.2846689) / 12.0,
            e.b <= 0.5 ? e.b * e.b / 3.0 : (exp((e.b - 0.5599107) / 0.1788328) + 0.2846689) / 12.0);
    }
    if (transfer < 3.5) return value;
    return pow(max(value, 0.0), 2.2);
}

float3 ToLinearRec709(float3 color)
{
    float primaries = SourceParameters.y;
    if (primaries < 0.5) return color;
    if (primaries < 1.5) return Rec2020ToRec709(DisplayP3ToRec2020(color));
    if (primaries < 2.5) return Rec2020ToRec709(color);
    return Rec2020ToRec709(ProPhotoToRec2020(color));
}

float4 DecodeSample(float4 sampleValue)
{
    if (BlurParameters.z < 0.5) return sampleValue;
    float alpha = SourceParameters.z > 1.5 ? 1.0 : saturate(sampleValue.a);
    float3 straight = sampleValue.rgb;
    if (SourceParameters.z > 0.5 && SourceParameters.z < 1.5 && alpha > 1e-6)
        straight /= alpha;
    return float4(ToLinearRec709(DecodeTransfer(straight)) * alpha, alpha);
}

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
        float2 uv = input.TexCoord + TexelStep * offset;
        total += DecodeSample(SourceTexture.SampleLevel(PointSampler, uv, 0.0)) * weight;
        weightTotal += weight;
    }
    return total / max(weightTotal, 1e-6);
}
