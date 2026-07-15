using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace BrightEyes.Renderer;

/// <summary>
/// Converts any supported source encoding to absolute linear light, composites
/// alpha over the configured blue presentation background, and encodes the
/// result for the existing swap-chain terminal (PQ/Rec.2020 or sRGB/Rec.709).
/// </summary>
public sealed class ColorManagementEffect : IDisposable
{
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _pointSampler;
    private readonly ID3D11Buffer _constantBuffer;
    private int _constantWidth = -1;
    private int _constantHeight = -1;
    private ColorSourceMetadata _constantSource;
    private float _constantSdrWhite = float.NaN;
    private float _constantSdrPeak = float.NaN;
    private DisplayColorMetadata _constantDisplay;
    private Vector4 _constantSdrBackground;
    private Vector4 _constantHdrBackground;

    [StructLayout(LayoutKind.Sequential)]
    private struct ColorConstants
    {
        public Vector2 SourceSize;
        public Vector2 SourceTexelSize;
        public Vector4 SourceParameters;
        public Vector4 OutputParameters;
        public Vector4 SdrBackground;
        public Vector4 HdrBackground;
    }

    public ColorManagementEffect(ID3D11Device device)
    {
        string baseDirectory = AppContext.BaseDirectory;
        string vertexPath = Path.Combine(baseDirectory, "Renderer/Shaders/FullScreenVS.hlsl");
        string pixelPath = Path.Combine(baseDirectory, "Renderer/Shaders/BrightColorsPS.hlsl");
        _vertexShader = device.CreateVertexShader(Compiler.CompileFromFile(vertexPath, "main", "vs_5_0").Span);
        _pixelShader = device.CreatePixelShader(Compiler.CompileFromFile(pixelPath, "main", "ps_5_0").Span);
        _pointSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp
        });
        _constantBuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = Marshal.SizeOf<ColorConstants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
    }

    public void Apply(
        ID3D11DeviceContext context,
        ID3D11ShaderResourceView source,
        ID3D11RenderTargetView destination,
        int sourceWidth,
        int sourceHeight,
        ColorSourceMetadata sourceMetadata,
        DisplayColorMetadata display,
        float sdrReferenceWhiteNits,
        float sdrDisplayPeakNits,
        Vector4 sdrBackground,
        Vector4 hdrBackground)
    {
        sourceMetadata = sourceMetadata.Normalize();
        sdrReferenceWhiteNits = NormalizePositive(sdrReferenceWhiteNits, 100.0f);
        sdrDisplayPeakNits = NormalizePositive(sdrDisplayPeakNits, 100.0f);
        float effectiveSdrWhiteNits = display.IsHdr
            ? NormalizePositive(display.SdrWhiteLevelNits, 203.0f)
            : sdrReferenceWhiteNits;
        context.OMSetRenderTargets(destination);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResources(0, new[] { source });

        bool changed = _constantWidth != sourceWidth ||
                       _constantHeight != sourceHeight ||
                       !_constantSource.Equals(sourceMetadata) ||
                       _constantSdrWhite != sdrReferenceWhiteNits ||
                       _constantSdrPeak != sdrDisplayPeakNits ||
                       !_constantDisplay.Equals(display) ||
                       _constantSdrBackground != sdrBackground ||
                       _constantHdrBackground != hdrBackground;
        if (changed)
        {
            var constants = new ColorConstants
            {
                SourceSize = new Vector2(sourceWidth, sourceHeight),
                SourceTexelSize = new Vector2(
                    sourceWidth > 0 ? 1.0f / sourceWidth : 0.0f,
                    sourceHeight > 0 ? 1.0f / sourceHeight : 0.0f),
                SourceParameters = new Vector4(
                    (float)sourceMetadata.Transfer,
                    (float)sourceMetadata.Primaries,
                    (float)sourceMetadata.Alpha,
                    sourceMetadata.ReferenceWhiteNits),
                OutputParameters = new Vector4(
                    display.IsHdr ? 1.0f : 0.0f,
                    NormalizePositive(display.IsHdr ? display.EffectivePeakNits : sdrDisplayPeakNits, 1000.0f),
                    effectiveSdrWhiteNits,
                    sdrDisplayPeakNits),
                SdrBackground = sdrBackground,
                HdrBackground = hdrBackground
            };
            var mapped = context.Map(_constantBuffer, 0, MapMode.WriteDiscard, MapFlags.None);
            try { Marshal.StructureToPtr(constants, mapped.DataPointer, false); }
            finally { context.Unmap(_constantBuffer, 0); }
            _constantWidth = sourceWidth;
            _constantHeight = sourceHeight;
            _constantSource = sourceMetadata;
            _constantSdrWhite = sdrReferenceWhiteNits;
            _constantSdrPeak = sdrDisplayPeakNits;
            _constantDisplay = display;
            _constantSdrBackground = sdrBackground;
            _constantHdrBackground = hdrBackground;
        }

        context.PSSetConstantBuffers(0, new[] { _constantBuffer });
        context.PSSetSamplers(0, new[] { _pointSampler });
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
    }

    private static float NormalizePositive(float value, float fallback) =>
        float.IsFinite(value) && value > 0.0f ? value : fallback;

    public void Dispose()
    {
        _vertexShader.Dispose();
        _pixelShader.Dispose();
        _pointSampler.Dispose();
        _constantBuffer.Dispose();
    }
}
