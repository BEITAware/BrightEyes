using Vortice.Direct3D11;

namespace BrightEyes.Renderer;

/// <summary>
/// Owns one physical GPU image produced by the process graph. Logical decoder
/// normalization may remain deferred until the first shader that consumes it.
/// </summary>
public sealed class GpuImageFrame : IDisposable
{
    internal GpuImageFrame(
        ID3D11Texture2D texture,
        ID3D11ShaderResourceView shaderResourceView,
        int width,
        int height,
        ColorSourceMetadata colorMetadata,
        bool isCanonicalIntermediate,
        bool requiresHlgOotf)
    {
        Texture = texture;
        ShaderResourceView = shaderResourceView;
        Width = width;
        Height = height;
        ColorMetadata = colorMetadata.Normalize();
        IsCanonicalIntermediate = isCanonicalIntermediate;
        RequiresHlgOotf = requiresHlgOotf;
    }

    internal ID3D11Texture2D Texture { get; }
    internal ID3D11ShaderResourceView ShaderResourceView { get; }
    public int Width { get; }
    public int Height { get; }
    public ColorSourceMetadata ColorMetadata { get; }
    public bool IsCanonicalIntermediate { get; }
    public bool RequiresHlgOotf { get; }
    public long ApproximateByteCount => (long)Width * Height * (IsCanonicalIntermediate ? 8 : 4);

    public void Dispose()
    {
        ShaderResourceView.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// Identifies one renderer-owned display submission. RetainedSource exists only
/// to keep the submitted processing texture alive; it is not the display target.
/// </summary>
public sealed record DisplayTargetFrame(long PresentationId, GpuImageFrame RetainedSource);
