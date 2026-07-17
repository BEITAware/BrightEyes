Texture2D<float4> SourceTexture : register(t0);
StructuredBuffer<float4> Operations : register(t1);
SamplerState PointSampler : register(s0);

cbuffer ConversionConstants : register(b0)
{
    // transfer, gamut, alpha representation, reference white
    float4 SourceParameters;
    // operation count, nominal peak, reserved, reserved
    float4 ControlParameters;
};

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

static const float ScRgbNits = 80.0;

float SrgbToLinear(float value)
{
    return value <= 0.04045 ? value / 12.92 : pow(max((value + 0.055) / 1.055, 0.0), 2.4);
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
    return 10000.0 * pow(max(p - c1, 0.0) / max(c2 - c3 * p, 1e-6), 1.0 / m1);
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

float3 DecodeHlg(float3 e)
{
    e = max(e, 0.0);
    return float3(
        e.r <= 0.5 ? e.r * e.r / 3.0 : (exp((e.r - 0.5599107) / 0.1788328) + 0.2846689) / 12.0,
        e.g <= 0.5 ? e.g * e.g / 3.0 : (exp((e.g - 0.5599107) / 0.1788328) + 0.2846689) / 12.0,
        e.b <= 0.5 ? e.b * e.b / 3.0 : (exp((e.b - 0.5599107) / 0.1788328) + 0.2846689) / 12.0);
}

float3 EncodeHlg(float3 value)
{
    value = max(value, 0.0);
    return float3(
        value.r <= 1.0 / 12.0 ? sqrt(3.0 * value.r) : 0.1788328 * log(12.0 * value.r - 0.2846689) + 0.5599107,
        value.g <= 1.0 / 12.0 ? sqrt(3.0 * value.g) : 0.1788328 * log(12.0 * value.g - 0.2846689) + 0.5599107,
        value.b <= 1.0 / 12.0 ? sqrt(3.0 * value.b) : 0.1788328 * log(12.0 * value.b - 0.2846689) + 0.5599107);
}

float3 DecodeNodeTransfer(float3 value, float transfer)
{
    if (transfer < 0.5)
        return float3(SrgbToLinear(value.r), SrgbToLinear(value.g), SrgbToLinear(value.b));
    if (transfer < 1.5)
        return float3(PqToNits(value.r), PqToNits(value.g), PqToNits(value.b)) / ScRgbNits;
    if (transfer < 2.5) return DecodeHlg(value);
    if (transfer < 3.5) return value;
    return pow(max(value, 0.0), 2.2);
}

float3 EncodeNodeTransfer(float3 value, float transfer)
{
    if (transfer < 0.5)
        return float3(LinearToSrgb(value.r), LinearToSrgb(value.g), LinearToSrgb(value.b));
    if (transfer < 1.5)
        return float3(NitsToPq(value.r * ScRgbNits), NitsToPq(value.g * ScRgbNits), NitsToPq(value.b * ScRgbNits));
    if (transfer < 2.5) return EncodeHlg(value);
    if (transfer < 3.5) return value;
    return pow(max(value, 0.0), 1.0 / 2.2);
}

float3 Rec709ToRec2020(float3 c)
{
    return float3(dot(c, float3(0.6274040, 0.3292820, 0.0433136)), dot(c, float3(0.0690970, 0.9195400, 0.0113612)), dot(c, float3(0.0163916, 0.0880132, 0.8955950)));
}

float3 DisplayP3ToRec2020(float3 c)
{
    return float3(dot(c, float3(0.7538330, 0.1985970, 0.0475700)), dot(c, float3(0.0457440, 0.9417770, 0.0124790)), dot(c, float3(-0.0012100, 0.0176020, 0.9836090)));
}

float3 ProPhotoToRec2020(float3 c)
{
    return mul(c, float3x3(0.769012, 0.141793, 0.089195, 0.075162, 0.844427, 0.080411, 0.025677, 0.042351, 0.931972));
}

float3 ToRec2020(float3 c, float gamut)
{
    if (gamut < 0.5) return Rec709ToRec2020(c);
    if (gamut < 1.5) return DisplayP3ToRec2020(c);
    if (gamut < 2.5) return c;
    return ProPhotoToRec2020(c);
}

float3 Rec2020ToRec709(float3 c)
{
    return float3(dot(c, float3(1.660491, -0.587641, -0.072850)), dot(c, float3(-0.124550, 1.132900, -0.008349)), dot(c, float3(-0.018151, -0.100579, 1.118730)));
}

float3 Rec2020ToDisplayP3(float3 c)
{
    return float3(dot(c, float3(1.343578, -0.282180, -0.061398)), dot(c, float3(-0.065298, 1.075788, -0.010490)), dot(c, float3(0.002822, -0.019599, 1.016777)));
}

float3 Rec2020ToProPhoto(float3 c)
{
    return mul(c, float3x3(1.325198, -0.217101, -0.108097, -0.114976, 1.208218, -0.093242, -0.031286, -0.048923, 1.080209));
}

float3 FromRec2020(float3 c, float gamut)
{
    if (gamut < 0.5) return Rec2020ToRec709(c);
    if (gamut < 1.5) return Rec2020ToDisplayP3(c);
    if (gamut < 2.5) return c;
    return Rec2020ToProPhoto(c);
}

float3 ApplyOperation(float3 value, float4 operation)
{
    float3 linearInput = DecodeNodeTransfer(value, operation.x);
    float3 linearOutput = FromRec2020(ToRec2020(linearInput, operation.y), operation.w);
    return EncodeNodeTransfer(linearOutput, operation.z);
}

float3 NormalizeSource(float3 value)
{
    float transfer = SourceParameters.x;
    float referenceWhite = max(SourceParameters.w, 0.001);
    float3 linearValue = DecodeNodeTransfer(value, transfer);
    // HLG remains scene-relative until the application-injected render mapper
    // applies its display-dependent OOTF. Other relative display encodings are
    // scaled into the 80-nit scRGB contract here.
    if (transfer < 0.5 || transfer > 2.5)
        linearValue *= referenceWhite / ScRgbNits;
    return FromRec2020(ToRec2020(linearValue, SourceParameters.y), 0.0);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float4 source = SourceTexture.SampleLevel(PointSampler, input.TexCoord, 0.0);
    float alpha = SourceParameters.z > 1.5 ? 1.0 : saturate(source.a);
    float3 straight = source.rgb;
    if (SourceParameters.z > 0.5 && SourceParameters.z < 1.5 && alpha > 1e-6)
        straight /= alpha;

    float3 value = NormalizeSource(straight);
    int count = max((int)round(ControlParameters.x), 0);
    [loop]
    for (int index = 0; index < count; index++)
        value = ApplyOperation(value, Operations[index]);

    // Preserve the last CST's encoded values verbatim. Downstream process
    // nodes are representation-transparent; another CST must convert them.
    return float4(value * alpha, alpha);
}
