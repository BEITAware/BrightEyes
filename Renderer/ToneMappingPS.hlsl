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

//=================================================================================================
// STRUCTS
//=================================================================================================

struct PS_INPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

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
    // Step 1: Sample the linear HDR color from the source texture.
    float4 proPhotoColor = SourceTexture.Sample(Sampler, input.TexCoord);

    // Step 2: Convert from linear ProPhoto RGB (D65) to CIE XYZ (D65).
    float3 xyzColor = mul(proPhotoColor.rgb, PROPHOTO_D65_TO_XYZ_D65);

    // Step 3: Convert from CIE XYZ (D65) to linear Rec.2020 RGB (D65).
    float3 rec2020Linear = mul(xyzColor, XYZ_D65_TO_REC2020);

    // Step 4: Apply the PQ EOTF to each color component.
    float3 rec2020_PQ;
    rec2020_PQ.r = LinearToPQ(rec2020Linear.r);
    rec2020_PQ.g = LinearToPQ(rec2020Linear.g);
    rec2020_PQ.b = LinearToPQ(rec2020Linear.b);

    // Step 5: Output the final PQ-encoded Rec.2020 color, preserving the original alpha.
    return float4(rec2020_PQ, proPhotoColor.a);
}