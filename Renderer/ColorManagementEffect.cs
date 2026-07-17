using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using BrightEyes.Processing.BuiltIn;

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
    private readonly ID3D11SamplerState _linearSampler;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11Device _device;
    private ID3D11Buffer? _operationsBuffer;
    private ID3D11ShaderResourceView? _operationsView;
    private int _operationsCapacity;
    private int _constantWidth = -1;
    private int _constantHeight = -1;
    private ColorSourceMetadata _constantSource;
    private float _constantSdrWhite = float.NaN;
    private float _constantSdrPeak = float.NaN;
    private float _constantSourcePeak = float.NaN;
    private float _constantSourceReferenceWhite = float.NaN;
    private bool _constantSourceIsRelative;
    private DisplayColorMetadata _constantDisplay;
    private Vector4 _constantSdrBackground;
    private Vector4 _constantHdrBackground;
    private ToneMappingMode _constantToneMapping;
    private GamutMappingMode _constantGamutMapping;
    private ViewInterpolationMode _constantInterpolation;
    private bool _constantApplyHlgOotf;
    private bool _constantInputRequiresHlgOotf;
    private bool _constantInputIsCanonicalScRgb;
    private ColorSpaceConversionOperation[] _constantColorConversions = Array.Empty<ColorSpaceConversionOperation>();

    [StructLayout(LayoutKind.Sequential)]
    private struct ColorConstants
    {
        public Vector2 SourceSize;
        public Vector2 SourceTexelSize;
        public Vector4 SourceParameters;
        public Vector4 OutputParameters;
        public Vector4 MappingParameters;
        public Vector4 SamplingParameters;
        public Vector4 SdrBackground;
        public Vector4 HdrBackground;
    }

    public ColorManagementEffect(ID3D11Device device)
    {
        _device = device;
        string baseDirectory = AppContext.BaseDirectory;
        string vertexPath = Path.Combine(baseDirectory, "Renderer/Shaders/FullScreenVS.hlsl");
        string pixelPath = Path.Combine(baseDirectory, "Renderer/Shaders/BrightColorsPS.hlsl");
        _vertexShader = device.CreateVertexShader(ShaderBytecodeCache.Get(vertexPath, "main", "vs_5_0"));
        _pixelShader = device.CreatePixelShader(ShaderBytecodeCache.Get(pixelPath, "main", "ps_5_0"));
        _pointSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp
        });
        _linearSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
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
        float sourcePeakNits,
        float sourceReferenceWhiteNits,
        bool sourceIsRelative,
        ToneMappingMode toneMapping,
        GamutMappingMode gamutMapping,
        ViewInterpolationMode interpolation,
        bool applyHlgOotf,
        bool inputRequiresHlgOotf,
        bool inputIsCanonicalScRgb,
        IReadOnlyList<ColorSpaceConversionOperation>? colorConversions,
        Vector4 sdrBackground,
        Vector4 hdrBackground)
    {
        sourceMetadata = sourceMetadata.Normalize();
        colorConversions ??= Array.Empty<ColorSpaceConversionOperation>();
        float defaultSdrWhiteNits = display.IsHdr
            ? NormalizePositive(display.SdrWhiteLevelNits, 203.0f)
            : 100.0f;
        sdrReferenceWhiteNits = NormalizePositive(sdrReferenceWhiteNits, defaultSdrWhiteNits);
        sdrDisplayPeakNits = NormalizePositive(sdrDisplayPeakNits, 100.0f);
        sourcePeakNits = NormalizePositive(sourcePeakNits, sourceMetadata.NominalPeakNits);
        sourceReferenceWhiteNits = NormalizePositive(sourceReferenceWhiteNits, 100.0f);
        context.PSSetShaderResources(1, new ID3D11ShaderResourceView[] { null! });
        context.OMSetRenderTargets(destination);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);

        bool changed = _constantWidth != sourceWidth ||
                       _constantHeight != sourceHeight ||
                       !_constantSource.Equals(sourceMetadata) ||
                       _constantSdrWhite != sdrReferenceWhiteNits ||
                       _constantSdrPeak != sdrDisplayPeakNits ||
                       _constantSourcePeak != sourcePeakNits ||
                       _constantSourceReferenceWhite != sourceReferenceWhiteNits ||
                       _constantSourceIsRelative != sourceIsRelative ||
                       !_constantDisplay.Equals(display) ||
                       _constantToneMapping != toneMapping ||
                       _constantGamutMapping != gamutMapping ||
                       _constantInterpolation != interpolation ||
                       _constantApplyHlgOotf != applyHlgOotf ||
                       _constantInputRequiresHlgOotf != inputRequiresHlgOotf ||
                       _constantInputIsCanonicalScRgb != inputIsCanonicalScRgb ||
                       !_constantColorConversions.SequenceEqual(colorConversions) ||
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
                    sdrReferenceWhiteNits,
                    sdrDisplayPeakNits),
                MappingParameters = new Vector4(
                    sourcePeakNits,
                    (float)toneMapping,
                    (float)gamutMapping,
                    applyHlgOotf && (inputIsCanonicalScRgb || inputRequiresHlgOotf) ? 1.0f : 0.0f),
                SamplingParameters = new Vector4(
                    (float)interpolation,
                    colorConversions.Count,
                    inputIsCanonicalScRgb ? 1.0f : 0.0f,
                    sourceIsRelative ? sourceReferenceWhiteNits : 0.0f),
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
            _constantSourcePeak = sourcePeakNits;
            _constantSourceReferenceWhite = sourceReferenceWhiteNits;
            _constantSourceIsRelative = sourceIsRelative;
            _constantDisplay = display;
            _constantToneMapping = toneMapping;
            _constantGamutMapping = gamutMapping;
            _constantInterpolation = interpolation;
            _constantApplyHlgOotf = applyHlgOotf;
            _constantInputRequiresHlgOotf = inputRequiresHlgOotf;
            _constantInputIsCanonicalScRgb = inputIsCanonicalScRgb;
            _constantColorConversions = colorConversions.ToArray();
            _constantSdrBackground = sdrBackground;
            _constantHdrBackground = hdrBackground;

            EnsureOperationsBuffer(colorConversions.Count);
            float[] packed = new float[colorConversions.Count * 4];
            for (int index = 0; index < colorConversions.Count; index++)
            {
                ColorSpaceConversionOperation operation = colorConversions[index];
                int offset = index * 4;
                packed[offset] = (float)operation.InputGamma;
                packed[offset + 1] = (float)operation.InputGamut;
                packed[offset + 2] = (float)operation.OutputGamma;
                packed[offset + 3] = (float)operation.OutputGamut;
            }
            if (packed.Length > 0)
            {
                var mappedOperations = context.Map(_operationsBuffer!, 0, MapMode.WriteDiscard, MapFlags.None);
                try { Marshal.Copy(packed, 0, mappedOperations.DataPointer, packed.Length); }
                finally { context.Unmap(_operationsBuffer!, 0); }
            }
        }

        context.PSSetShaderResources(0, new[] { source, _operationsView! });
        context.PSSetConstantBuffers(0, new[] { _constantBuffer });
        context.PSSetSamplers(0, new[] { _pointSampler, _linearSampler });
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.Draw(3, 0);
    }

    private static float NormalizePositive(float value, float fallback) =>
        float.IsFinite(value) && value > 0.0f ? value : fallback;

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

    public void Dispose()
    {
        _vertexShader.Dispose();
        _pixelShader.Dispose();
        _pointSampler.Dispose();
        _linearSampler.Dispose();
        _constantBuffer.Dispose();
        _operationsView?.Dispose();
        _operationsBuffer?.Dispose();
    }
}
