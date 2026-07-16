using BrightEyes.Renderer;
using System.Collections.ObjectModel;
using System.IO;

namespace BrightEyes.Processing.Contracts;

public sealed record MetadataFact(
    string StableId,
    string Name,
    string Value,
    string Source,
    MetadataConfidence Confidence,
    bool IsAssumption = false,
    string? Conflict = null);

public sealed record BackendCapability(
    string BackendId,
    bool IsAvailable,
    string Detail,
    bool SupportsHdr,
    bool SupportsAlpha,
    int MaximumBitDepth);

public sealed record DetectedImageMetadata(
    string ContainerFormat,
    int PixelWidth,
    int PixelHeight,
    int FrameCount,
    int ExifOrientation,
    int BitDepth,
    bool HasAlpha,
    bool HasIccProfile,
    bool HasCicp,
    bool HasUltraHdrGainMap,
    ColorPrimaries Primaries,
    ColorTransferFunction Transfer,
    PhotometricModel PhotometricModel,
    ColorRange Range,
    float ReferenceWhiteNits,
    float NominalPeakNits,
    float? MaxCllNits,
    float? MaxFallNits,
    IReadOnlyList<MetadataFact> Facts,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<BackendCapability> BackendCapabilities)
{
    public bool HasAssumptions => Facts.Any(fact => fact.IsAssumption);

    public static DetectedImageMetadata Unknown(string extension) => new(
        string.IsNullOrWhiteSpace(extension) ? "Unknown" : extension.TrimStart('.').ToUpperInvariant(),
        0,
        0,
        1,
        1,
        8,
        false,
        false,
        false,
        false,
        ColorPrimaries.Rec709,
        ColorTransferFunction.Srgb,
        PhotometricModel.DisplayLinearRelative,
        ColorRange.Full,
        100.0f,
        100.0f,
        null,
        null,
        Array.Empty<MetadataFact>(),
        Array.Empty<string>(),
        Array.Empty<BackendCapability>());
}

public sealed class RawImage
{
    private readonly byte[] _compressedData;

    public RawImage(
        string filePath,
        byte[] compressedData,
        DetectedImageMetadata detectedMetadata,
        string contentIdentity)
    {
        FilePath = Path.GetFullPath(filePath);
        _compressedData = compressedData ?? throw new ArgumentNullException(nameof(compressedData));
        DetectedMetadata = detectedMetadata ?? throw new ArgumentNullException(nameof(detectedMetadata));
        ContentIdentity = contentIdentity;
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public long FileSize => _compressedData.LongLength;
    public ReadOnlyMemory<byte> CompressedData => _compressedData;
    public DetectedImageMetadata DetectedMetadata { get; }
    public string ContentIdentity { get; }
}

public sealed record CurrentImageMetadata(
    ImageSemantics Semantics,
    string PixelFormat,
    string Storage,
    IReadOnlyList<string> TransformHistory);

public sealed record BrightEyesIntermediateImage(
    object GpuResourceIdentity,
    DetectedImageMetadata SourceMetadata,
    CurrentImageMetadata CurrentMetadata,
    object? AuxiliaryPlanes = null)
{
    public const string DefaultPixelFormat = "R32G32B32A32_FLOAT";
    public const float NitsPerScRgbUnit = 80.0f;
}

public sealed record AnalysisData(
    AnalysisKind Kind,
    AnalysisChannel Channel,
    AnalysisPresentation Presentation,
    bool LogarithmicScale,
    ReadOnlyMemory<float> Values,
    ImageSemantics SignalSemantics,
    DateTimeOffset Timestamp);
