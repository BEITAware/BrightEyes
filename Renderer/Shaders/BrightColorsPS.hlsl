Texture2D SourceTexture : register(t0);
SamplerState PointSampler : register(s0);
SamplerState LinearSampler : register(s1);

cbuffer ColorConstants : register(b0)
{
    float2 SourceSize;
    float2 SourceTexelSize;
    // transfer, primaries, alpha representation, source reference white
    float4 SourceParameters;
    // hdr output, hdr peak, SDR reference white, SDR display peak
    float4 OutputParameters;
    // source peak, tone mapper, gamut mapper, apply HLG OOTF
    float4 MappingParameters;
    // interpolation mode: BrightEyes bilinear, bilinear, bicubic, nearest neighbor, area
    float4 SamplingParameters;
    float4 SdrBackground;
    float4 HdrBackground;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

static const float PI = 3.14159265358979323846;

float SrgbToLinear(float value)
{
    return value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4);
}

float LinearToSrgb(float value)
{
    value = max(value, 0.0);
    return value <= 0.0031308 ? value * 12.92 : 1.055 * pow(value, 1.0 / 2.4) - 0.055;
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
    float n = max(p - c1, 0.0);
    float d = c2 - c3 * p;
    return 10000.0 * pow(d > 0.0 ? n / d : 0.0, 1.0 / m1);
}

float NitsToPq(float nits)
{
    const float m1 = 2610.0 / 16384.0;
    const float m2 = 2523.0 / 4096.0 * 128.0;
    const float c1 = 3424.0 / 4096.0;
    const float c2 = 2413.0 / 4096.0 * 32.0;
    const float c3 = 2392.0 / 4096.0 * 32.0;
    float p = pow(max(nits, 0.0) / 10000.0, m1);
    return pow((c1 + c2 * p) / (1.0 + c3 * p), m2);
}

float3 Rec709ToRec2020(float3 c)
{
    return float3(
        dot(c, float3(0.6274040, 0.3292820, 0.0433136)),
        dot(c, float3(0.0690970, 0.9195400, 0.0113612)),
        dot(c, float3(0.0163916, 0.0880132, 0.8955950)));
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
    // D50 ProPhoto is adapted to D65 before entering the Rec.2020 working space.
    return mul(c, float3x3(
        0.769012, 0.141793, 0.089195,
        0.075162, 0.844427, 0.080411,
        0.025677, 0.042351, 0.931972));
}

float3 Rec2020ToRec709(float3 c)
{
    return float3(
        dot(c, float3(1.660491, -0.587641, -0.072850)),
        dot(c, float3(-0.124550, 1.132900, -0.008349)),
        dot(c, float3(-0.018151, -0.100579, 1.118730)));
}

float3 DecodeTransfer(float3 encoded)
{
    float transfer = SourceParameters.x;
    float referenceWhite = max(SourceParameters.w, 0.001);
    if (transfer < 0.5)
        return float3(SrgbToLinear(encoded.r), SrgbToLinear(encoded.g), SrgbToLinear(encoded.b)) * referenceWhite;
    if (transfer < 1.5)
        return float3(PqToNits(encoded.r), PqToNits(encoded.g), PqToNits(encoded.b));
    if (transfer < 2.5)
    {
        // BT.2100 HLG inverse OETF. The display-dependent OOTF belongs to the
        // render mapper and is applied after scene-linear filtering.
        float3 e = max(encoded, 0.0);
        return float3(
            e.r <= 0.5 ? e.r * e.r / 3.0 : (exp((e.r - 0.5599107) / 0.1788328) + 0.2846689) / 12.0,
            e.g <= 0.5 ? e.g * e.g / 3.0 : (exp((e.g - 0.5599107) / 0.1788328) + 0.2846689) / 12.0,
            e.b <= 0.5 ? e.b * e.b / 3.0 : (exp((e.b - 0.5599107) / 0.1788328) + 0.2846689) / 12.0);
    }
    if (transfer < 3.5)
        return encoded * referenceWhite;
    return pow(max(encoded, 0.0), 2.2) * referenceWhite;
}

float3 ToLinearRec709(float3 linearColor)
{
    float primaries = SourceParameters.y;
    if (primaries < 0.5) return linearColor;
    if (primaries < 1.5) return Rec2020ToRec709(DisplayP3ToRec2020(linearColor));
    if (primaries < 2.5) return Rec2020ToRec709(linearColor);
    return Rec2020ToRec709(ProPhotoToRec2020(linearColor));
}

float4 LoadEncoded(int2 coordinate)
{
    coordinate = clamp(coordinate, int2(0, 0), int2(SourceSize) - 1);
    return SourceTexture.Load(int3(coordinate, 0));
}

float4 DecodeToPremultipliedLinear709(float4 encoded)
{
    float alpha = SourceParameters.z > 1.5 ? 1.0 : saturate(encoded.a);
    float3 color = encoded.rgb;
    if (SourceParameters.z > 0.5 && SourceParameters.z < 1.5 && alpha > 1e-6)
        color /= alpha;
    float3 linearColor = ToLinearRec709(DecodeTransfer(color));
    return float4(linearColor * alpha, alpha);
}

float4 LoadPremultipliedLinear709(int2 coordinate)
{
    return DecodeToPremultipliedLinear709(LoadEncoded(coordinate));
}

float4 SampleBrightEyesBilinear(float2 uv)
{
    float2 position = uv * SourceSize - 0.5;
    int2 base = int2(floor(position));
    float2 fraction = position - floor(position);
    float4 a = lerp(LoadPremultipliedLinear709(base), LoadPremultipliedLinear709(base + int2(1, 0)), fraction.x);
    float4 b = lerp(LoadPremultipliedLinear709(base + int2(0, 1)), LoadPremultipliedLinear709(base + int2(1, 1)), fraction.x);
    return lerp(a, b, fraction.y);
}

float4 SampleBilinear(float2 uv)
{
    // Conventional GPU bilinear filtering operates on stored code values.
    return DecodeToPremultipliedLinear709(SourceTexture.SampleLevel(LinearSampler, uv, 0.0));
}

float CubicCatmullRom(float offset)
{
    float x = abs(offset);
    if (x <= 1.0)
        return ((1.5 * x - 2.5) * x) * x + 1.0;
    if (x < 2.0)
        return ((-0.5 * x + 2.5) * x - 4.0) * x + 2.0;
    return 0.0;
}

float4 SampleBicubic(float2 uv)
{
    // Conventional Catmull-Rom bicubic filtering also operates on stored code values.
    float2 position = uv * SourceSize - 0.5;
    int2 base = int2(floor(position));
    float2 fraction = position - floor(position);
    float4 encoded = 0.0;
    float weightSum = 0.0;

    [unroll]
    for (int y = -1; y <= 2; y++)
    {
        float weightY = CubicCatmullRom(y - fraction.y);
        [unroll]
        for (int x = -1; x <= 2; x++)
        {
            float weight = CubicCatmullRom(x - fraction.x) * weightY;
            encoded += LoadEncoded(base + int2(x, y)) * weight;
            weightSum += weight;
        }
    }

    return DecodeToPremultipliedLinear709(encoded / max(weightSum, 1e-6));
}

float4 SampleNearestNeighbor(float2 uv)
{
    return DecodeToPremultipliedLinear709(SourceTexture.SampleLevel(PointSampler, uv, 0.0));
}

float4 SampleArea(float2 uv)
{
    // The UV derivatives describe the source-space rectangle covered by one
    // destination pixel. Area filtering is useful only for minification;
    // conventional bilinear remains the better reconstruction filter when
    // the image is displayed at its native size or enlarged.
    float2 footprint = fwidth(uv) * SourceSize;
    if (footprint.x <= 1.0 && footprint.y <= 1.0)
        return SampleBilinear(uv);

    // Preserve bilinear reconstruction on an enlarged axis while integrating
    // the complete footprint on a reduced axis. View rendering normally uses
    // a uniform scale, but this also keeps the sampler well-defined if that
    // constraint changes in the future.
    footprint = max(footprint, 1.0);
    float2 sourcePosition = uv * SourceSize;
    float2 areaStart = clamp(sourcePosition - footprint * 0.5, 0.0, SourceSize);
    float2 areaEnd = clamp(sourcePosition + footprint * 0.5, 0.0, SourceSize);
    int2 firstPixel = int2(floor(areaStart));
    int2 endPixel = int2(ceil(areaEnd));
    float4 encoded = 0.0;
    float weightSum = 0.0;

    [loop]
    for (int y = firstPixel.y; y < endPixel.y; y++)
    {
        float weightY = max(0.0, min(areaEnd.y, y + 1.0) - max(areaStart.y, (float)y));
        [loop]
        for (int x = firstPixel.x; x < endPixel.x; x++)
        {
            float weightX = max(0.0, min(areaEnd.x, x + 1.0) - max(areaStart.x, (float)x));
            float weight = weightX * weightY;
            encoded += LoadEncoded(int2(x, y)) * weight;
            weightSum += weight;
        }
    }

    return DecodeToPremultipliedLinear709(encoded / max(weightSum, 1e-6));
}

float4 SampleImage(float2 uv)
{
    float interpolation = SamplingParameters.x;
    if (interpolation < 0.5) return SampleBrightEyesBilinear(uv);
    if (interpolation < 1.5) return SampleBilinear(uv);
    if (interpolation < 2.5) return SampleBicubic(uv);
    if (interpolation < 3.5) return SampleNearestNeighbor(uv);
    return SampleArea(uv);
}

float3 ToneMapLuminance(float3 color, float targetPeak)
{
    if (MappingParameters.y > 0.5) return color;
    float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
    if (luminance <= 0.0) return color;
    float sourcePeak = max(MappingParameters.x, luminance);
    if (sourcePeak <= targetPeak && luminance <= targetPeak) return color;
    float knee = targetPeak * 0.72;
    if (luminance <= knee) return color;
    float shoulder = max(targetPeak - knee, 0.001);
    float mapped = knee + shoulder * (1.0 - exp(-(luminance - knee) / shoulder));
    return color * (mapped / luminance);
}

float3 CompressGamut(float3 color, float limit)
{
    if (MappingParameters.z > 0.5) return color;
    float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
    float3 chroma = color - luminance;
    float scale = 1.0;
    [unroll]
    for (int channel = 0; channel < 3; channel++)
    {
        if (color[channel] < 0.0 && chroma[channel] < 0.0)
            scale = min(scale, max(luminance, 0.0) / -chroma[channel]);
        if (color[channel] > limit && chroma[channel] > 0.0)
            scale = min(scale, max(limit - luminance, 0.0) / chroma[channel]);
    }
    // Smooth the boundary to avoid a hard per-channel clip.
    scale = saturate(scale);
    scale = scale * scale * (3.0 - 2.0 * scale);
    return luminance + chroma * scale;
}

float3 ApplyRenderMapper(float3 linear709)
{
    float targetPeak = max(OutputParameters.y, 1.0);
    if (MappingParameters.w > 0.5)
    {
        float sceneLuminance = dot(max(linear709, 0.0), float3(0.2126, 0.7152, 0.0722));
        float gamma = 1.2 + 0.42 * log10(max(targetPeak, 1000.0) / 1000.0);
        linear709 *= pow(max(sceneLuminance, 1e-6), gamma - 1.0) * targetPeak;
    }
    else if (SourceParameters.x < 0.5 || SourceParameters.x > 2.5)
    {
        linear709 *= OutputParameters.z / max(SourceParameters.w, 0.001);
    }
    return ToneMapLuminance(linear709, targetPeak);
}

float3 BackgroundLinear709()
{
    if (OutputParameters.x > 0.5)
    {
        float3 rec2020Nits = float3(PqToNits(HdrBackground.r), PqToNits(HdrBackground.g), PqToNits(HdrBackground.b));
        return Rec2020ToRec709(rec2020Nits);
    }
    return float3(SrgbToLinear(SdrBackground.r), SrgbToLinear(SdrBackground.g), SrgbToLinear(SdrBackground.b)) * OutputParameters.w;
}

float4 EncodeOutput(float3 linear709)
{
    if (OutputParameters.x > 0.5)
    {
        float3 linear2020 = CompressGamut(Rec709ToRec2020(linear709), OutputParameters.y);
        return float4(NitsToPq(linear2020.r), NitsToPq(linear2020.g), NitsToPq(linear2020.b), 1.0);
    }

    float3 mapped709 = CompressGamut(linear709, OutputParameters.w);
    float3 normalized = mapped709 / max(OutputParameters.w, 0.001);
    return float4(LinearToSrgb(saturate(normalized.r)), LinearToSrgb(saturate(normalized.g)), LinearToSrgb(saturate(normalized.b)), 1.0);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float4 sample = SampleImage(input.TexCoord);
    float alpha = saturate(sample.a);
    float3 straight = alpha > 1e-6 ? sample.rgb / alpha : 0.0;
    float3 mapped = ApplyRenderMapper(straight);
    float3 composited = mapped * alpha + BackgroundLinear709() * (1.0 - alpha);
    return EncodeOutput(composited);
}
