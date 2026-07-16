using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BrightEyes.Renderer;

internal sealed class GaussianBlurEffect : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _pointSampler;
    private readonly ID3D11Buffer _constantBuffer;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurConstants
    {
        public Vector2 SourceSize;
        public Vector2 TexelStep;
        public Vector4 SourceParameters;
        public Vector4 BlurParameters;
    }

    public GaussianBlurEffect(ID3D11Device device)
    {
        _device = device;
        string baseDirectory = AppContext.BaseDirectory;
        _vertexShader = device.CreateVertexShader(Compiler.CompileFromFile(
            Path.Combine(baseDirectory, "Renderer/Shaders/FullScreenVS.hlsl"), "main", "vs_5_0").Span);
        _pixelShader = device.CreatePixelShader(Compiler.CompileFromFile(
            Path.Combine(baseDirectory, "Renderer/Shaders/GaussianBlurPS.hlsl"), "main", "ps_5_0").Span);
        _pointSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp
        });
        _constantBuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = Marshal.SizeOf<BlurConstants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });
    }

    public GpuImageFrame Apply(
        ID3D11DeviceContext context,
        GpuImageFrame source,
        int radius,
        float sigma)
    {
        radius = Math.Clamp(radius, 0, 64);
        sigma = float.IsFinite(sigma) ? Math.Clamp(sigma, 0.1f, 32.0f) : 4.0f;
        GpuImageFrame horizontal = CreateTarget(source.Width, source.Height, source.ColorMetadata, source.RequiresHlgOotf);
        try
        {
            DrawPass(context, source.ShaderResourceView, horizontal, source.ColorMetadata, radius, sigma, true, true);
            GpuImageFrame output = CreateTarget(source.Width, source.Height, source.ColorMetadata, source.RequiresHlgOotf);
            try
            {
                DrawPass(context, horizontal.ShaderResourceView, output, horizontal.ColorMetadata, radius, sigma, false, false);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        finally
        {
            horizontal.Dispose();
        }
    }

    public GpuImageFrame Materialize(ID3D11DeviceContext context, GpuImageFrame source)
    {
        GpuImageFrame output = CreateTarget(source.Width, source.Height, source.ColorMetadata, source.RequiresHlgOotf);
        try
        {
            DrawPass(context, source.ShaderResourceView, output, source.ColorMetadata, 0, 1.0f, true, true);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private GpuImageFrame CreateTarget(
        int width,
        int height,
        ColorSourceMetadata sourceMetadata,
        bool requiresHlgOotf)
    {
        var description = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        };
        ID3D11Texture2D texture = _device.CreateTexture2D(description);
        ID3D11ShaderResourceView view = _device.CreateShaderResourceView(texture);
        var outputMetadata = new ColorSourceMetadata(
            ColorTransferFunction.Linear,
            ColorPrimaries.Rec709,
            sourceMetadata.Alpha == AlphaRepresentation.Opaque ? AlphaRepresentation.Opaque : AlphaRepresentation.Premultiplied,
            sourceMetadata.ReferenceWhiteNits,
            sourceMetadata.NominalPeakNits);
        return new GpuImageFrame(texture, view, width, height, outputMetadata, true, requiresHlgOotf);
    }

    private void DrawPass(
        ID3D11DeviceContext context,
        ID3D11ShaderResourceView source,
        GpuImageFrame destination,
        ColorSourceMetadata sourceMetadata,
        int radius,
        float sigma,
        bool horizontal,
        bool decodeInput)
    {
        using ID3D11RenderTargetView target = _device.CreateRenderTargetView(destination.Texture);
        var constants = new BlurConstants
        {
            SourceSize = new Vector2(destination.Width, destination.Height),
            TexelStep = horizontal
                ? new Vector2(1.0f / destination.Width, 0.0f)
                : new Vector2(0.0f, 1.0f / destination.Height),
            SourceParameters = new Vector4(
                (float)sourceMetadata.Transfer,
                (float)sourceMetadata.Primaries,
                (float)sourceMetadata.Alpha,
                sourceMetadata.ReferenceWhiteNits),
            BlurParameters = new Vector4(radius, sigma, decodeInput ? 1.0f : 0.0f, sourceMetadata.NominalPeakNits)
        };
        MappedSubresource mapped = context.Map(
            _constantBuffer,
            MapMode.WriteDiscard,
            Vortice.Direct3D11.MapFlags.None);
        try { Marshal.StructureToPtr(constants, mapped.DataPointer, false); }
        finally { context.Unmap(_constantBuffer, 0); }

        context.RSSetViewport(new Viewport(0, 0, destination.Width, destination.Height, 0.0f, 1.0f));
        context.OMSetRenderTargets(target);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResources(0, new[] { source });
        context.PSSetSamplers(0, new[] { _pointSampler });
        context.PSSetConstantBuffers(0, new[] { _constantBuffer });
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
        context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _pointSampler.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
