Texture2D SourceTexture : register(t0);
SamplerState PointSampler : register(s0);

cbuffer ColorConstants : register(b0)
{
    float2 SourceSize;
    float2 SourceTexelSize;
    // transfer, primaries, alpha representation, source reference white
    float4 SourceParameters;
    // hdr output, hdr peak, SDR reference white, SDR display peak
    float4 OutputParameters;
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
    value = saturate(value);
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
    float referenceWhite = max(transfer < 0.5 ? OutputParameters.z : SourceParameters.w, 0.001);
    if (transfer < 0.5)
        return float3(SrgbToLinear(encoded.r), SrgbToLinear(encoded.g), SrgbToLinear(encoded.b)) * referenceWhite;
    if (transfer < 1.5)
        return float3(PqToNits(encoded.r), PqToNits(encoded.g), PqToNits(encoded.b));
    if (transfer < 2.5)
    {
        // BT.2100 HLG inverse OETF followed by the display-referred OOTF.
        float3 e = max(encoded, 0.0);
        float3 scene = float3(
            e.r <= 0.5 ? e.r * e.r / 3.0 : (exp((e.r - 0.5599107) / 0.1788328) + 0.2846689) / 12.0,
            e.g <= 0.5 ? e.g * e.g / 3.0 : (exp((e.g - 0.5599107) / 0.1788328) + 0.2846689) / 12.0,
            e.b <= 0.5 ? e.b * e.b / 3.0 : (exp((e.b - 0.5599107) / 0.1788328) + 0.2846689) / 12.0);
        float y = dot(scene, float3(0.2627, 0.6780, 0.0593));
        float displayPeak = max(OutputParameters.y, 1000.0);
        float gamma = 1.2 + 0.42 * log10(displayPeak / 1000.0);
        float ootf = pow(max(y, 1e-6), gamma - 1.0);
        return scene * ootf * displayPeak;
    }
    if (transfer < 3.5)
        return max(encoded, 0.0) * referenceWhite;
    return pow(max(encoded, 0.0), 2.2) * referenceWhite;
}

float3 ToRec2020(float3 linearColor)
{
    float primaries = SourceParameters.y;
    if (primaries < 0.5) return Rec709ToRec2020(linearColor);
    if (primaries < 1.5) return DisplayP3ToRec2020(linearColor);
    if (primaries < 2.5) return linearColor;
    return ProPhotoToRec2020(linearColor);
}

float4 DecodeAndComposite(int2 coordinate)
{
    coordinate = clamp(coordinate, int2(0, 0), int2(SourceSize) - 1);
    float4 encoded = SourceTexture.Load(int3(coordinate, 0));
    float alpha = SourceParameters.z > 1.5 ? 1.0 : saturate(encoded.a);
    float3 color = encoded.rgb;
    if (SourceParameters.z > 0.5 && SourceParameters.z < 1.5 && alpha > 1e-6)
        color /= alpha;
    float3 linearColor = ToRec2020(DecodeTransfer(color));
    // Premultiply in linear light before interpolation and composite with the
    // exact blue clear color used by the existing terminal renderer.
    bool hdr = OutputParameters.x > 0.5;
    float3 background = hdr
        ? float3(PqToNits(HdrBackground.r), PqToNits(HdrBackground.g), PqToNits(HdrBackground.b))
        : Rec709ToRec2020(float3(SrgbToLinear(SdrBackground.r), SrgbToLinear(SdrBackground.g), SrgbToLinear(SdrBackground.b)) * OutputParameters.w);
    return float4(linearColor * alpha + background * (1.0 - alpha), 1.0);
}

float4 SampleLinearLight(float2 uv)
{
    float2 position = uv * SourceSize - 0.5;
    int2 base = int2(floor(position));
    float2 fraction = position - floor(position);
    float4 a = lerp(DecodeAndComposite(base), DecodeAndComposite(base + int2(1, 0)), fraction.x);
    float4 b = lerp(DecodeAndComposite(base + int2(0, 1)), DecodeAndComposite(base + int2(1, 1)), fraction.x);
    return lerp(a, b, fraction.y);
}

float4 EncodeOutput(float3 linear2020)
{
    if (OutputParameters.x > 0.5)
        return float4(NitsToPq(linear2020.r), NitsToPq(linear2020.g), NitsToPq(linear2020.b), 1.0);

    float3 rec709Nits = max(Rec2020ToRec709(linear2020), 0.0);
    float3 normalized = rec709Nits / max(OutputParameters.w, 0.001);
    return float4(LinearToSrgb(saturate(normalized.r)), LinearToSrgb(saturate(normalized.g)), LinearToSrgb(saturate(normalized.b)), 1.0);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    return EncodeOutput(SampleLinearLight(input.TexCoord).rgb);
}
