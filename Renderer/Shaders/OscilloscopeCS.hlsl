Texture2D<float4> SourceTexture : register(t0);
RWStructuredBuffer<uint> Histogram : register(u0);

cbuffer AnalysisConstants : register(b0)
{
    uint Width;
    uint Height;
    uint SampleStep;
    float Transfer;
    float ReferenceWhiteNits;
    float NominalPeakNits;
    uint AnalysisMode;
    uint ChannelMode;
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

float HlgToScene(float value)
{
    value = max(value, 0.0);
    return value <= 0.5
        ? value * value / 3.0
        : (exp((value - 0.5599107) / 0.1788328) + 0.2846689) / 12.0;
}

float DecodeNormalized(float value)
{
    if (Transfer < 0.5) return SrgbToLinear(value);
    if (Transfer < 1.5) return PqToNits(value) / max(NominalPeakNits, 1.0);
    if (Transfer < 2.5) return HlgToScene(value);
    if (Transfer < 3.5) return value;
    return pow(max(value, 0.0), 2.2);
}

[numthreads(16, 16, 1)]
void main(uint3 dispatchId : SV_DispatchThreadID)
{
    if (dispatchId.x >= Width || dispatchId.y >= Height) return;
    uint linearIndex = dispatchId.y * Width + dispatchId.x;
    if (linearIndex % max(SampleStep, 1u) != 0) return;

    float4 encodedSample = SourceTexture.Load(int3(dispatchId.xy, 0));
    float3 encoded = encodedSample.rgb;
    float3 linearRgb = float3(
        DecodeNormalized(encoded.r),
        DecodeNormalized(encoded.g),
        DecodeNormalized(encoded.b));
    float luminance = dot(linearRgb, float3(0.2126, 0.7152, 0.0722));
    float signal = ChannelMode == 2u ? linearRgb.r :
                   ChannelMode == 3u ? linearRgb.g :
                   ChannelMode == 4u ? linearRgb.b :
                   ChannelMode == 5u ? encodedSample.a : luminance;

    uint bin;
    if (AnalysisMode == 1u)
    {
        uint x = min(dispatchId.x * 256u / max(Width, 1u), 255u);
        uint y = (uint)round(saturate(signal) * 127.0);
        bin = y * 256u + x;
    }
    else if (AnalysisMode == 2u)
    {
        float u = saturate(0.5 + (linearRgb.b - luminance) * 0.5);
        float v = saturate(0.5 + (linearRgb.r - luminance) * 0.5);
        uint x = (uint)round(u * 127.0);
        uint y = (uint)round(v * 127.0);
        bin = y * 128u + x;
    }
    else
    {
        bin = (uint)round(saturate(signal) * 255.0);
    }
    InterlockedAdd(Histogram[bin], 1u);
}
