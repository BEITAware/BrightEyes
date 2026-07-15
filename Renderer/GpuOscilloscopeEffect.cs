using System.Numerics;
using System.Runtime.InteropServices;
using System.IO;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using BrightEyes.Processing.BuiltIn;

namespace BrightEyes.Renderer;

public sealed class GpuOscilloscopeEffect : IDisposable
{
    public const int MaximumBinCount = 256 * 128;
    public const int ReadbackByteCount = MaximumBinCount * sizeof(uint);

    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _histogram;
    private readonly ID3D11UnorderedAccessView _histogramView;
    private readonly ID3D11Buffer _readback;
    private readonly ID3D11Buffer _constantBuffer;
    private bool _pendingReadback;
    private OscilloscopeKind _pendingKind;

    [StructLayout(LayoutKind.Sequential)]
    private struct AnalysisConstants
    {
        public uint Width;
        public uint Height;
        public uint SampleStep;
        public float Transfer;
        public float ReferenceWhiteNits;
        public float NominalPeakNits;
        public uint AnalysisMode;
        public uint ChannelMode;
    }

    public GpuOscilloscopeEffect(ID3D11Device device)
    {
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Renderer/Shaders/OscilloscopeCS.hlsl");
        _shader = device.CreateComputeShader(Compiler.CompileFromFile(shaderPath, "main", "cs_5_0").Span);
        _histogram = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = ReadbackByteCount,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.UnorderedAccess,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint)
        });
        _histogramView = device.CreateUnorderedAccessView(_histogram);
        _readback = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = ReadbackByteCount,
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = sizeof(uint)
        });
        _constantBuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = Marshal.SizeOf<AnalysisConstants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
    }

    public void Dispatch(
        ID3D11DeviceContext context,
        ID3D11ShaderResourceView source,
        int width,
        int height,
        int sampleRatePercent,
        OscilloscopeKind kind,
        OscilloscopeChannel channel,
        ColorSourceMetadata metadata)
    {
        if (_pendingReadback) return;
        uint sampleStep = (uint)Math.Clamp((int)Math.Ceiling(100.0 / Math.Clamp(sampleRatePercent, 1, 100)), 1, 100);
        var constants = new AnalysisConstants
        {
            Width = (uint)width,
            Height = (uint)height,
            SampleStep = sampleStep,
            Transfer = (float)metadata.Transfer,
            ReferenceWhiteNits = metadata.ReferenceWhiteNits,
            NominalPeakNits = metadata.NominalPeakNits,
            AnalysisMode = (uint)kind,
            ChannelMode = (uint)channel
        };
        MappedSubresource mapped = context.Map(_constantBuffer, MapMode.WriteDiscard, MapFlags.None);
        try { Marshal.StructureToPtr(constants, mapped.DataPointer, false); }
        finally { context.Unmap(_constantBuffer, 0); }

        context.ClearUnorderedAccessView(_histogramView, new Int4(0, 0, 0, 0));
        context.CSSetShader(_shader);
        context.CSSetShaderResources(0, new[] { source });
        context.CSSetUnorderedAccessViews(0, new[] { _histogramView });
        context.CSSetConstantBuffers(0, new[] { _constantBuffer });
        context.Dispatch((width + 15) / 16, (height + 15) / 16, 1);
        context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        context.CopyResource(_readback, _histogram);
        _pendingKind = kind;
        _pendingReadback = true;
    }

    public bool TryReadback(ID3D11DeviceContext context, out float[] values)
    {
        values = Array.Empty<float>();
        if (!_pendingReadback) return false;
        MappedSubresource mapped;
        try
        {
            mapped = context.Map(_readback, MapMode.Read, MapFlags.DoNotWait);
        }
        catch
        {
            return false;
        }

        try
        {
            int resultCount = _pendingKind switch
            {
                OscilloscopeKind.Waveform => 256 * 128,
                OscilloscopeKind.Vectorscope => 128 * 128,
                _ => 256
            };
            int[] counts = new int[MaximumBinCount];
            Marshal.Copy(mapped.DataPointer, counts, 0, counts.Length);
            int maximum = Math.Max(counts.Take(resultCount).Max(), 1);
            values = counts.Take(resultCount).Select(count => count / (float)maximum).ToArray();
        }
        finally
        {
            context.Unmap(_readback, 0);
        }
        _pendingReadback = false;
        return true;
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _readback.Dispose();
        _histogramView.Dispose();
        _histogram.Dispose();
        _shader.Dispose();
    }
}
