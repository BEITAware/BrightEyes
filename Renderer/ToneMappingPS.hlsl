// Copyright (c) BrightEyes. All rights reserved.
//
// Tonemapping Pixel Shader
// Converts a linear ProPhoto RGB (D65) image to a PQ-encoded Rec.2020 image.

//=================================================================================================
// INPUTS
//=================================================================================================

// The high-precision HDR scene texture (R32G32B32A32_Float)
Texture2D SourceTexture : register(t0);
SamplerState Sampler : register(s0);

cbuffer ScaleCB : register(b0)
{
    float2 SourceSize;      // (width, height)
    float2 SourceTexelSize; // (1/width, 1/height)
    float InputIsSdr;       // 1 when SourceTexture contains normalized sRGB pixels
    float3 Padding;
};

//=================================================================================================
// STRUCTS
//=================================================================================================

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

// Cubic B-spline weights (fast 4-fetch bicubic)
float4 CubicBSpline(float x)
{
    float4 n = float4(1.0, 2.0, 3.0, 4.0) - x;
    float4 s = n * n * n;
    float x0 = s.x;
    float x1 = s.y - 4.0 * s.x;
    float x2 = s.z - 4.0 * s.y + 6.0 * s.x;
    float x3 = 6.0 - x0 - x1 - x2;
    return (1.0 / 6.0) * float4(x0, x1, x2, x3);
}

float4 SampleBicubic(Texture2D tex, SamplerState samp, float2 uv)
{
    float2 tc = uv * SourceSize - 0.5;
    float2 base = floor(tc);
    float2 f = tc - base;

    float4 wx = CubicBSpline(f.x);
    float4 wy = CubicBSpline(f.y);

    float2 g0 = float2(wx.x + wx.y, wy.x + wy.y);
    float2 g1 = float2(wx.z + wx.w, wy.z + wy.w);
    float2 h0 = float2(g0.x > 0 ? wx.y / g0.x : 0.0, g0.y > 0 ? wy.y / g0.y : 0.0);
    float2 h1 = float2(g1.x > 0 ? wx.w / g1.x : 0.0, g1.y > 0 ? wy.w / g1.y : 0.0);

    float2 p0 = (base + h0 + 0.5) * SourceTexelSize;
    float2 p1 = (base + float2(1.0, 1.0) + h1 + 0.5) * SourceTexelSize;

    float4 s00 = tex.Sample(samp, float2(p0.x, p0.y));
    float4 s10 = tex.Sample(samp, float2(p1.x, p0.y));
    float4 s01 = tex.Sample(samp, float2(p0.x, p1.y));
    float4 s11 = tex.Sample(samp, float2(p1.x, p1.y));

    float2 k = g1 / (g0 + g1);
    float4 a = lerp(s00, s10, k.x);
    float4 b = lerp(s01, s11, k.x);
    return lerp(a, b, k.y);
}

//=================================================================================================
// COLOR SPACE CONVERSION MATRICES
//
// These matrices are defined for use with mul(vector, matrix), where the vector is treated as a row vector.
// This means each row in the matrix definition corresponds to a column in the mathematical representation.
//=================================================================================================

// Converts from ProPhoto RGB (ROMM RGB) with a D65 white point to CIE XYZ (D65).
// A D65-adapted matrix is used to avoid a separate chromatic adaptation step before converting to Rec.2020 (which also uses D65).
static const float3x3 PROPHOTO_D65_TO_XYZ_D65 =
{
    0.5293214, 0.3300859, 0.1405927,
    0.0983652, 0.8734682, 0.0281665,
    0.0168782, 0.0000000, 0.9831218
};

// Converts from CIE XYZ (D65) to linear Rec.2020 RGB (D65).
static const float3x3 XYZ_D65_TO_REC2020 =
{
     1.7166512, -0.6667243, -0.2533663,
    -0.6667243,  1.6164539,  0.0157680,
    -0.2533663,  0.0157680,  1.2111303
};

// Rec.709/sRGB linear to Rec.2020 linear. Transposed for
// mul(rowVector, matrix), matching the convention used below.
static const float3x3 REC709_TO_REC2020 =
{
    0.6274040, 0.0690970, 0.0163916,
    0.3292820, 0.9195400, 0.0880132,
    0.0433136, 0.0113612, 0.8955950
};

float SrgbToLinear(float value)
{
    return value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4);
}

//=================================================================================================
// PQ EOTF (PERCEPTUAL QUANTIZER)
//
// Implements the forward transformation of the SMPTE ST.2084 Electro-Optical Transfer Function.
// It converts a linear color component (in nits) to a non-linear, perceptually uniform value.
//=================================================================================================

// Converts a single linear Rec.2020 color component to its PQ non-linear value.
float LinearToPQ(float linearValue)
{
    // The PQ curve is defined for a peak luminance of 10,000 cd/m^2 (nits).
    // We normalize the input linear value (assumed to be in nits) by this peak.
    float L = linearValue / 10000.0;
    
    // Clamp to avoid issues with negative values that might result from gamut conversions.
    L = max(0.0, L);

    // ST.2084 constants
    const float m1 = 2610.0 / 16384.0;
    const float m2 = 2523.0 / 4096.0 * 128.0;
    const float c1 = 3424.0 / 4096.0;
    const float c2 = 2413.0 / 4096.0 * 32.0;
    const float c3 = 2413.0 / 4096.0 * 18.0;

    float L_pow_m1 = pow(L, m1);
    float num = c1 + c2 * L_pow_m1;
    float den = 1.0 + c3 * L_pow_m1;
    
    return pow(num / den, m2);
}

//=================================================================================================
// PIXEL SHADER MAIN
//=================================================================================================

float4 main(PS_INPUT input) : SV_TARGET
{
    float4 sourceColor = SampleBicubic(SourceTexture, Sampler, input.TexCoord);
    float3 rec2020Linear;
    if (InputIsSdr > 0.5)
    {
        float3 linear709 = float3(SrgbToLinear(sourceColor.r), SrgbToLinear(sourceColor.g), SrgbToLinear(sourceColor.b));
        // Map SDR reference white to 100 nits before encoding to PQ.
        rec2020Linear = mul(linear709, REC709_TO_REC2020) * 100.0;
    }
    else
    {
        float3 xyzColor = mul(sourceColor.rgb, PROPHOTO_D65_TO_XYZ_D65);
        rec2020Linear = mul(xyzColor, XYZ_D65_TO_REC2020);
    }

    // Step 4: Apply the PQ EOTF to each color component.
    float3 rec2020_PQ;
    rec2020_PQ.r = LinearToPQ(rec2020Linear.r);
    rec2020_PQ.g = LinearToPQ(rec2020Linear.g);
    rec2020_PQ.b = LinearToPQ(rec2020Linear.b);

    // Step 5: Output the final PQ-encoded Rec.2020 color, preserving the original alpha.
    return float4(rec2020_PQ, sourceColor.a);
}
