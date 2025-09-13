// Present Pixel Shader
// Assumes the source texture is already in Rec.2020 + PQ (HDR10) normalized [0,1].
// Uses high-quality bicubic resampling for scaling.

Texture2D SourceTexture : register(t0);
SamplerState Sampler : register(s0);

cbuffer ScaleCB : register(b0)
{
    float2 SourceSize;      // (width, height)
    float2 SourceTexelSize; // (1/width, 1/height)
};

struct PS_INPUT
{
    float4 Pos : SV_POSITION;
    float2 Tex : TEXCOORD0;
};

// Cubic B-spline weights (fast 4-fetch bicubic, uses bilinear hardware filtering)
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

float4 main(PS_INPUT input) : SV_Target
{
    float4 color = SampleBicubic(SourceTexture, Sampler, input.Tex);
    return color;
}

