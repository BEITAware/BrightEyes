using System.Numerics;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BrightEyes.Renderer;

/// <summary>
/// Normalizes decoder uploads to the fixed scRGB texture contract and can
/// materialize an arbitrary chain of explicit color-space conversions in one
/// draw. Pointwise nodes themselves do not allocate textures; this effect is
/// invoked only at a real sampling boundary.
/// </summary>
internal sealed class ColorSpaceConversionEffect : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _pointSampler;
    private readonly ID3D11Buffer _constantBuffer;
    private ID3D11Buffer? _operationsBuffer;
    private ID3D11ShaderResourceView? _operationsView;
    private int _operationsCapacity;

    [StructLayout(LayoutKind.Sequential)]
    private struct ConversionConstants
    {
        public Vector4 SourceParameters;
        public Vector4 ControlParameters;
    }

    public ColorSpaceConversionEffect(ID3D11Device device)
    {
        _device = device;
        string baseDirectory = AppContext.BaseDirectory;
        _vertexShader = device.CreateVertexShader(Compiler.CompileFromFile(
            Path.Combine(baseDirectory, "Renderer/Shaders/FullScreenVS.hlsl"), "main", "vs_5_0").Span);
        _pixelShader = device.CreatePixelShader(Compiler.CompileFromFile(
            Path.Combine(baseDirectory, "Renderer/Shaders/ColorSpaceConversionPS.hlsl"), "main", "ps_5_0").Span);
        _pointSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp
        });
        _constantBuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = Marshal.SizeOf<ConversionConstants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });
    }

    public GpuImageFrame NormalizeDecoded(
        ID3D11DeviceContext context,
        ID3D11ShaderResourceView source,
        int width,
        int height,
        ColorSourceMetadata sourceMetadata) =>
        Apply(context, source, width, height, sourceMetadata, Array.Empty<ColorSpaceConversionOperation>());

    public GpuImageFrame Apply(
        ID3D11DeviceContext context,
        GpuImageFrame source,
        IReadOnlyList<ColorSpaceConversionOperation> operations) =>
        Apply(context, source.ShaderResourceView, source.Width, source.Height, ColorSourceMetadata.ScRgbLinear, operations);

    private GpuImageFrame Apply(
        ID3D11DeviceContext context,
        ID3D11ShaderResourceView source,
        int width,
        int height,
        ColorSourceMetadata sourceMetadata,
        IReadOnlyList<ColorSpaceConversionOperation> operations)
    {
        GpuImageFrame output = CreateTarget(width, height);
        try
        {
            UpdateConstants(context, sourceMetadata.Normalize(), operations);
            using ID3D11RenderTargetView target = _device.CreateRenderTargetView(output.Texture);
            context.RSSetViewport(new Viewport(0, 0, width, height, 0.0f, 1.0f));
            context.OMSetRenderTargets(target);
            context.VSSetShader(_vertexShader);
            context.PSSetShader(_pixelShader);
            context.PSSetShaderResources(0, new[] { source, _operationsView! });
            context.PSSetSamplers(0, new[] { _pointSampler });
            context.PSSetConstantBuffers(0, new[] { _constantBuffer });
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.Draw(3, 0);
            context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
            context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private void UpdateConstants(
        ID3D11DeviceContext context,
        ColorSourceMetadata source,
        IReadOnlyList<ColorSpaceConversionOperation> operations)
    {
        var constants = new ConversionConstants
        {
            SourceParameters = new Vector4(
                (float)source.Transfer,
                (float)source.Primaries,
                (float)source.Alpha,
                source.ReferenceWhiteNits),
            ControlParameters = new Vector4(operations.Count, source.NominalPeakNits, 0.0f, 0.0f)
        };
        MappedSubresource mappedConstants = context.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try { Marshal.StructureToPtr(constants, mappedConstants.DataPointer, false); }
        finally { context.Unmap(_constantBuffer, 0); }

        EnsureOperationsBuffer(operations.Count);
        float[] packed = new float[operations.Count * 4];
        for (int index = 0; index < operations.Count; index++)
        {
            ColorSpaceConversionOperation operation = operations[index];
            int offset = index * 4;
            packed[offset] = (float)operation.InputGamma;
            packed[offset + 1] = (float)operation.InputGamut;
            packed[offset + 2] = (float)operation.OutputGamma;
            packed[offset + 3] = (float)operation.OutputGamut;
        }
        if (packed.Length > 0)
        {
            MappedSubresource mappedOperations = context.Map(_operationsBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try { Marshal.Copy(packed, 0, mappedOperations.DataPointer, packed.Length); }
            finally { context.Unmap(_operationsBuffer!, 0); }
        }
    }

    private void EnsureOperationsBuffer(int count)
    {
        int required = Math.Max(count, 1);
        if (_operationsBuffer is not null && _operationsCapacity >= required) return;
        int capacity = 1;
        while (capacity < required) capacity = checked(capacity * 2);
        _operationsView?.Dispose();
        _operationsBuffer?.Dispose();
        _operationsBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = checked(capacity * Marshal.SizeOf<Vector4>()),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = Marshal.SizeOf<Vector4>()
        });
        _operationsView = _device.CreateShaderResourceView(_operationsBuffer);
        _operationsCapacity = capacity;
    }

    private GpuImageFrame CreateTarget(int width, int height)
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
        return new GpuImageFrame(texture, view, width, height);
    }

    public void Dispose()
    {
        _operationsView?.Dispose();
        _operationsBuffer?.Dispose();
        _constantBuffer.Dispose();
        _pointSampler.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
