using System.Numerics;
using System.Runtime.InteropServices;
using System.IO;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using BrightEyes.Processing.Contracts;

namespace BrightEyes.Renderer;

public sealed class GpuOscilloscopeEffect : IDisposable
{
    public const int MaximumPendingReadbacks = 16;
    public const int WaveformPlaneBinCount = 256 * 128;
    public const int MaximumBinCount = WaveformPlaneBinCount * 3;
    public const int ReadbackByteCount = MaximumBinCount * sizeof(uint);

    private readonly ID3D11Device _device;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _histogram;
    private readonly ID3D11UnorderedAccessView _histogramView;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly Queue<PendingReadback> _pendingReadbacks = new();

    private sealed record PendingReadback(
        ID3D11Buffer Buffer,
        AnalysisKind Kind,
        AnalysisChannel Channel);

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
        public uint AlphaMode;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
    }

    public GpuOscilloscopeEffect(ID3D11Device device)
    {
        _device = device;
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Renderer/Shaders/OscilloscopeCS.hlsl");
        _shader = device.CreateComputeShader(ShaderBytecodeCache.Get(shaderPath, "main", "cs_5_0"));
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

    public bool Dispatch(
        ID3D11DeviceContext context,
        ID3D11ShaderResourceView source,
        int width,
        int height,
        int sampleRatePercent,
        AnalysisKind kind,
        AnalysisChannel channel,
        ColorSourceMetadata metadata)
    {
        if (_pendingReadbacks.Count >= MaximumPendingReadbacks) return false;
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
            ChannelMode = (uint)channel,
            AlphaMode = (uint)metadata.Alpha
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
        context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        ID3D11Buffer readback = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = ReadbackByteCount,
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = sizeof(uint)
        });
        try
        {
            context.CopyResource(readback, _histogram);
            _pendingReadbacks.Enqueue(new(readback, kind, channel));
        }
        catch
        {
            readback.Dispose();
            throw;
        }
        return true;
    }

    public bool TryReadback(ID3D11DeviceContext context, out float[] values)
    {
        values = Array.Empty<float>();
        if (_pendingReadbacks.Count == 0) return false;
        PendingReadback pending = _pendingReadbacks.Peek();
        MappedSubresource mapped;
        try
        {
            mapped = context.Map(pending.Buffer, MapMode.Read, MapFlags.DoNotWait);
        }
        catch
        {
            return false;
        }

        try
        {
            int resultCount = pending.Kind switch
            {
                AnalysisKind.Waveform when pending.Channel == AnalysisChannel.Rgb => WaveformPlaneBinCount * 3,
                AnalysisKind.Waveform => WaveformPlaneBinCount,
                AnalysisKind.Vectorscope => 128 * 128,
                _ => 256
            };
            int[] counts = new int[MaximumBinCount];
            Marshal.Copy(mapped.DataPointer, counts, 0, counts.Length);
            int maximum = Math.Max(counts.Take(resultCount).Max(), 1);
            values = counts.Take(resultCount).Select(count => count / (float)maximum).ToArray();
        }
        finally
        {
            context.Unmap(pending.Buffer, 0);
        }
        _pendingReadbacks.Dequeue();
        pending.Buffer.Dispose();
        return true;
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        while (_pendingReadbacks.TryDequeue(out PendingReadback? pending))
            pending.Buffer.Dispose();
        _histogramView.Dispose();
        _histogram.Dispose();
        _shader.Dispose();
    }
}
