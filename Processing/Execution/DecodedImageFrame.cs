using BrightEyes.Processing.BuiltIn;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.Execution;

public enum DecodedPixelStorage
{
    Rgba8,
    Rgba32Float
}

public sealed record DecodedImageFrame(
    int Width,
    int Height,
    DecodedPixelStorage Storage,
    byte[]? Rgba8,
    float[]? Rgba32Float,
    ColorSourceMetadata EncodedColorMetadata,
    DecoderBackend ActualBackend,
    string Diagnostic,
    bool UsedFallback)
{
    public long CpuByteCount => Storage == DecodedPixelStorage.Rgba8
        ? Rgba8?.LongLength ?? 0
        : (Rgba32Float?.LongLength ?? 0) * sizeof(float);
}
