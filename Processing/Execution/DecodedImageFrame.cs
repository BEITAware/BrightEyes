using BrightEyes.Processing.BuiltIn;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.Execution;

public enum DecodedPixelStorage
{
    Rgba8,
    Bgra8,
    Rgba32Float
}

public sealed record DecodedImageFrame(
    int Width,
    int Height,
    DecodedPixelStorage Storage,
    byte[]? PixelBytes,
    float[]? Rgba32Float,
    ColorSourceMetadata EncodedColorMetadata,
    DecoderBackend ActualBackend,
    string Diagnostic,
    bool UsedFallback)
{
    public long CpuByteCount => Storage is DecodedPixelStorage.Rgba8 or DecodedPixelStorage.Bgra8
        ? PixelBytes?.LongLength ?? 0
        : (Rgba32Float?.LongLength ?? 0) * sizeof(float);
}
