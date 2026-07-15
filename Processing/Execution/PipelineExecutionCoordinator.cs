using BrightEyes.Processing.BuiltIn;
using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Documents;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.Execution;

public sealed class PipelineExecutionCoordinator
{
    private readonly ImageDecodeService _decoder = new();
    private readonly Func<RendererD3D11?> _getRenderer;

    public PipelineExecutionCoordinator(Func<RendererD3D11?> getRenderer)
    {
        _getRenderer = getRenderer;
    }

    public async Task ExecuteAsync(ImageDocument document, bool forceDecode = false)
    {
        RendererD3D11 renderer = _getRenderer() ?? throw new InvalidOperationException("D3D11 渲染器尚未初始化。");
        document.ExecutionCancellation?.Cancel();
        document.ExecutionCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        document.ExecutionCancellation = cancellation;
        long generation = ++document.ExecutionGeneration;
        CancellationToken token = cancellation.Token;

        IImageProcess? hardError = document.Processes.FirstOrDefault(process => process.IncomingConnectionSeverity == ConnectionSeverity.HardError);
        if (hardError is not null)
        {
            MarkFailure(document, hardError, hardError.IncomingConnectionMessage);
            return;
        }

        DecoderProcess? decoder = document.Processes.OfType<DecoderProcess>().FirstOrDefault(process => process.IsEnabled);
        ViewProcess? view = document.Processes.OfType<ViewProcess>().FirstOrDefault(process => process.IsEnabled);
        if (decoder is null)
        {
            MarkFailure(document, null, "流程没有可执行的解码器；保留最后有效画面。 ");
            return;
        }
        if (view is null)
        {
            MarkFailure(document, null, "流程没有启用的视图过程；保留最后有效画面。 ");
            return;
        }

        try
        {
            foreach (IImageProcess process in document.Processes.Where(process => process.IsEnabled))
            {
                if (process.State != ProcessState.Warning) process.State = ProcessState.Pending;
            }

            if (forceDecode || document.DecodedFrame is null)
            {
                decoder.State = ProcessState.Running;
                decoder.Diagnostic = "正在执行压缩解码；GPU 上传尚未发生。";
                DecodedImageFrame decoded = await _decoder.DecodeAsync(document.Source, decoder, token).ConfigureAwait(true);
                if (generation != document.ExecutionGeneration || token.IsCancellationRequested) return;
                decoder.ActualBackend = decoded.ActualBackend;
                decoder.BackendFallbackDetail = decoded.UsedFallback ? decoded.Diagnostic : "";
                decoder.Diagnostic = decoded.Diagnostic + $" CPU 解码缓冲：{decoded.CpuByteCount / (1024.0 * 1024.0):0.##} MiB。";
                decoder.State = decoded.UsedFallback ? ProcessState.Warning : ProcessState.Valid;
                document.DecodedFrame = decoded;
                renderer.UploadDecodedFrame(decoded);
            }

            if (generation != document.ExecutionGeneration || token.IsCancellationRequested) return;
            ApplyMapperBeforeView(document, view, renderer);
            var executionServices = new PipelineExecutionServices(renderer, _decoder);
            var executionContext = new ProcessExecutionContext(
                document.Source,
                document.DisplayMetadata,
                document.DecodedFrame,
                executionServices);
            int decoderIndex = document.Processes.IndexOf(decoder);
            foreach (IImageProcess process in document.Processes.Skip(decoderIndex + 1).Where(process => process.IsEnabled))
                await process.ExecuteAsync(executionContext, token).ConfigureAwait(true);
            SynchronizationContext? synchronizationContext = SynchronizationContext.Current;
            foreach (OscilloscopeProcess scope in document.Processes.OfType<OscilloscopeProcess>().Where(process => process.IsEnabled))
            {
                bool requested = renderer.RequestAnalysis(scope.SampleRatePercent.Value, scope.Kind.Value, scope.Channel.Value, values =>
                {
                    if (synchronizationContext is null)
                        scope.AcceptAnalysis(values);
                    else
                        synchronizationContext.Post(_ => scope.AcceptAnalysis(values), null);
                });
                if (!requested)
                {
                    scope.State = ProcessState.Warning;
                    scope.Diagnostic = "GPU 分析功能不可用；图像解码与显示不受影响。";
                }
            }
            bool reachedView = false;
            foreach (IImageProcess process in document.Processes)
            {
                if (!process.IsEnabled) continue;
                if (ReferenceEquals(process, view)) reachedView = true;
                if (process.State is ProcessState.Error or ProcessState.Warning) continue;
                process.State = ProcessState.Valid;
                if (process is OscilloscopeProcess)
                    process.Diagnostic = reachedView
                        ? "GPU 分析请求位于视图之后；观察透传数据而非 swap-chain 量化结果。"
                        : "GPU 分析请求已排队；仅紧凑统计允许回读。";
            }
            document.HasExecuted = true;
            document.HasLastValidFrame = true;
        }
        catch (OperationCanceledException)
        {
            foreach (IImageProcess process in document.Processes.Where(process => process.State == ProcessState.Running))
                process.State = ProcessState.Stale;
        }
        catch (Exception ex)
        {
            MarkFailure(document, decoder, ex.GetBaseException().Message);
        }
    }

    private static void ApplyMapperBeforeView(ImageDocument document, ViewProcess view, RendererD3D11 renderer)
    {
        int viewIndex = document.Processes.IndexOf(view);
        RenderMapperProcess? mapper = document.Processes.Take(viewIndex)
            .OfType<RenderMapperProcess>()
            .LastOrDefault(process => process.IsEnabled);
        if (mapper is null)
        {
            renderer.SetSdrCalibration(100.0f, 100.0f);
            return;
        }
        renderer.SetSdrCalibration(mapper.SdrWhiteNits.Value, Math.Min(mapper.TargetPeakNits.Value, 1000.0f));
        renderer.SetTargetPeak(mapper.TargetPeakNits.Value);
        renderer.SetRenderMapping(mapper.Preset.Value, mapper.GamutMapping.Value, mapper.ApplyHlgOotf.Value);
    }

    private static void MarkFailure(ImageDocument document, IImageProcess? failedProcess, string message)
    {
        if (failedProcess is not null)
        {
            failedProcess.State = ProcessState.Error;
            failedProcess.Diagnostic = message;
        }
        foreach (IImageProcess process in document.Processes.Where(process => !ReferenceEquals(process, failedProcess) && process.State is ProcessState.Running or ProcessState.Pending))
        {
            process.State = document.HasLastValidFrame ? ProcessState.Stale : ProcessState.NotRun;
            process.Diagnostic = document.HasLastValidFrame ? "执行失败，中央区域保留最后有效画面。" : "上游执行失败。";
        }
    }

    private sealed class PipelineExecutionServices : IServiceProvider
    {
        private readonly RendererD3D11 _renderer;
        private readonly ImageDecodeService _decoder;

        public PipelineExecutionServices(RendererD3D11 renderer, ImageDecodeService decoder)
        {
            _renderer = renderer;
            _decoder = decoder;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType.IsInstanceOfType(_renderer)) return _renderer;
            if (serviceType.IsInstanceOfType(_decoder)) return _decoder;
            return null;
        }
    }
}
