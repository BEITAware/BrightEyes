using Vortice.Direct3D11;

namespace BrightEyes.Renderer;

/// <summary>
/// Owns one physical GPU image produced by the process graph. A decoder upload
/// may keep its atomic normalization deferred until the next sampling boundary;
/// the coordinator materializes it before any image process can sample it.
/// No per-node color metadata is carried by this object.
/// </summary>
public sealed class GpuImageFrame : IDisposable
{
    internal GpuImageFrame(
        ID3D11Texture2D texture,
        ID3D11ShaderResourceView shaderResourceView,
        int width,
        int height)
    {
        Texture = texture;
        ShaderResourceView = shaderResourceView;
        Width = width;
        Height = height;
    }

    internal ID3D11Texture2D Texture { get; }
    internal ID3D11ShaderResourceView ShaderResourceView { get; }
    public int Width { get; }
    public int Height { get; }
    public long ApproximateByteCount => (long)Width * Height * 8;

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
