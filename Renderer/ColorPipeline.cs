using System;

namespace BrightEyes.Renderer;

/// <summary>
/// Transfer functions understood by the BCM v1 bridge. Values are kept stable
/// because they are sent directly to the pixel shader.
/// </summary>
public enum ColorTransferFunction
{
    Srgb = 0,
    Pq = 1,
    Hlg = 2,
    Linear = 3,
    Gamma22 = 4
}

/// <summary>
/// RGB primaries understood by the BCM v1 bridge.
/// </summary>
public enum ColorPrimaries
{
    Rec709 = 0,
    DisplayP3 = 1,
    Rec2020 = 2,
    ProPhoto = 3
}

public enum AlphaRepresentation
{
    Straight = 0,
    Premultiplied = 1,
    Opaque = 2
}

/// <summary>
/// One explicit, self-contained color-space conversion. It intentionally does
/// not describe the image flowing between processes: every conversion carries
/// both sides of its contract, so neighboring nodes never exchange metadata.
/// </summary>
public readonly record struct ColorSpaceConversionOperation(
    ColorTransferFunction InputGamma,
    ColorPrimaries InputGamut,
    ColorTransferFunction OutputGamma,
    ColorPrimaries OutputGamut);

/// <summary>
/// Atomic work declared by the decoder from its own injected parameters. This
/// is an executable normalization operation, not image metadata published for
/// another process to interpret.
/// </summary>
public readonly record struct SourceNormalizationOperation(ColorSourceMetadata EncodedSource);

/// <summary>
/// Consecutive pointwise process work that can share one physical GPU pass.
/// Sampling processes never inspect this batch; the coordinator flushes it at
/// their declared materialization boundaries.
/// </summary>
public readonly record struct PointwiseImageOperations(
    SourceNormalizationOperation? SourceNormalization,
    IReadOnlyList<ColorSpaceConversionOperation> ColorConversions)
{
    public bool IsEmpty => SourceNormalization is null && ColorConversions.Count == 0;
}

/// <summary>
/// Describes the encoding of the pixels in an external texture. RGB values
/// remain in their encoded form until the bridge shader converts them to the
/// canonical linear Rec.2020/nits working space.
/// </summary>
public readonly record struct ColorSourceMetadata(
    ColorTransferFunction Transfer,
    ColorPrimaries Primaries,
    AlphaRepresentation Alpha = AlphaRepresentation.Straight,
    float ReferenceWhiteNits = 100.0f,
    float NominalPeakNits = 1000.0f)
{
    public static ColorSourceMetadata Sdr => new(
        ColorTransferFunction.Srgb,
        ColorPrimaries.Rec709,
        AlphaRepresentation.Straight,
        100.0f,
        100.0f);

    public static ColorSourceMetadata PqRec2020Opaque => new(
        ColorTransferFunction.Pq,
        ColorPrimaries.Rec2020,
        AlphaRepresentation.Opaque,
        100.0f,
        10000.0f);

    public static ColorSourceMetadata LegacyLinearProPhoto => new(
        ColorTransferFunction.Linear,
        ColorPrimaries.ProPhoto,
        AlphaRepresentation.Straight,
        100.0f,
        10000.0f);

    /// <summary>
    /// The decoder output and the default terminal interpretation. scRGB uses
    /// linear extended Rec.709 primaries and 80 nit per unit. Ordinary process
    /// nodes preserve texture values and do not track whether an explicit CST
    /// changed their representation.
    /// </summary>
    public static ColorSourceMetadata ScRgbLinear => new(
        ColorTransferFunction.Linear,
        ColorPrimaries.Rec709,
        AlphaRepresentation.Premultiplied,
        80.0f,
        10000.0f);

    public ColorSourceMetadata Normalize()
    {
        float referenceWhite = float.IsFinite(ReferenceWhiteNits) && ReferenceWhiteNits > 0.0f
            ? ReferenceWhiteNits
            : 100.0f;
        float nominalPeak = float.IsFinite(NominalPeakNits) && NominalPeakNits > 0.0f
            ? NominalPeakNits
            : (Transfer == ColorTransferFunction.Pq ? 10000.0f : 1000.0f);
        return this with { ReferenceWhiteNits = referenceWhite, NominalPeakNits = nominalPeak };
    }
}

/// <summary>
/// Display facts obtained from DXGI. The system SDR white level supplies the
/// default reference white and can be overridden by the render mapper.
/// </summary>
public readonly record struct DisplayColorMetadata(
    bool IsHdr,
    float MinLuminanceNits,
    float MaxLuminanceNits,
    float MaxFullFrameLuminanceNits,
    float SdrWhiteLevelNits,
    bool HasSystemSdrWhiteLevel,
    string ColorSpace)
{
    public float EffectivePeakNits => IsHdr
        ? (MaxFullFrameLuminanceNits > 0.0f ? MaxFullFrameLuminanceNits : MaxLuminanceNits)
        : 100.0f;

    public static DisplayColorMetadata SdrFallback => new(false, 0.1f, 100.0f, 100.0f, 100.0f, false, "RGB_FULL_G22_NONE_P709");
}

public static class ColorPipelineMath
{
    public static float SrgbToLinear(float value)
    {
        value = Math.Clamp(value, 0.0f, 1.0f);
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    public static float LinearToSrgb(float value)
    {
        value = Math.Max(0.0f, value);
        return value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1.0f / 2.4f) - 0.055f;
    }

    public static float PqToNits(float value)
    {
        value = Math.Clamp(value, 0.0f, 1.0f);
        const float m1 = 2610.0f / 16384.0f;
        const float m2 = 2523.0f / 4096.0f * 128.0f;
        const float c1 = 3424.0f / 4096.0f;
        const float c2 = 2413.0f / 4096.0f * 32.0f;
        const float c3 = 2392.0f / 4096.0f * 32.0f;
        float p = MathF.Pow(value, 1.0f / m2);
        float n = MathF.Max(p - c1, 0.0f);
        float d = c2 - c3 * p;
        return 10000.0f * MathF.Pow(d > 0.0f ? n / d : 0.0f, 1.0f / m1);
    }

    public static float NitsToPq(float nits)
    {
        const float m1 = 2610.0f / 16384.0f;
        const float m2 = 2523.0f / 4096.0f * 128.0f;
        const float c1 = 3424.0f / 4096.0f;
        const float c2 = 2413.0f / 4096.0f * 32.0f;
        const float c3 = 2392.0f / 4096.0f * 32.0f;
        float p = MathF.Pow(Math.Max(nits, 0.0f) / 10000.0f, m1);
        return MathF.Pow((c1 + c2 * p) / (1.0f + c3 * p), m2);
    }
}
