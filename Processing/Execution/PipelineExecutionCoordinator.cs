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

        var services = new PipelineExecutionServices(renderer, _decoder, document, forceDecode, generation);
        var context = new ProcessExecutionContext(document.Source, document.DisplayMetadata, services);
        var transientImages = new List<GpuImageFrame>();
        IImageProcess? runningProcess = null;

        try
        {
            foreach (IImageProcess process in document.Processes.Where(process => process.IsEnabled))
                process.State = ProcessState.Pending;

            foreach (IImageProcess process in document.Processes.Where(process => process.IsEnabled))
            {
                token.ThrowIfCancellationRequested();
                if (generation != document.ExecutionGeneration) throw new OperationCanceledException(token);
                runningProcess = process;
                process.State = ProcessState.Running;
                ProcessDefinitionAttribute definition = process.Descriptor.Definition;
                if (context.DisplayTarget is null &&
                    context.PendingColorConversions.Count > 0 &&
                    (definition.RequiresIntermediateTexture || definition.Role == ProcessRole.Observer))
                {
                    GpuImageFrame input = context.Image ?? throw new InvalidOperationException("点运算队列没有可用的 GPU 图像。");
                    GpuImageFrame materialized = renderer.ApplyColorSpaceConversions(
                        input,
                        context.TakeColorConversions(),
                        token);
                    context.SetImage(materialized);
                    TrackTransient(materialized);
                }

                GpuImageFrame? inputImage = context.Image;
                DisplayTargetFrame? inputTarget = context.DisplayTarget;
                int inputPointwiseCount = context.PendingColorConversions.Count;
                await process.ExecuteAsync(context, token).ConfigureAwait(true);

                if (definition.Role == ProcessRole.Observer &&
                    (!ReferenceEquals(inputImage, context.Image) ||
                     !ReferenceEquals(inputTarget, context.DisplayTarget) ||
                     inputPointwiseCount != context.PendingColorConversions.Count))
                {
                    throw new InvalidOperationException(
                        $"观察过程 {definition.StableId} 修改了图像管线。观察过程必须保持图像和点运算队列不变。");
                }
                if (definition.Role == ProcessRole.Image &&
                    definition.RequiresIntermediateTexture &&
                    ReferenceEquals(inputImage, context.Image))
                    throw new InvalidOperationException($"过程 {definition.StableId} 声明需要中间纹理，但没有生成新的 GPU 图像。");
                if (definition.Role == ProcessRole.Image &&
                    !definition.RequiresIntermediateTexture &&
                    !ReferenceEquals(inputImage, context.Image))
                    throw new InvalidOperationException($"过程 {definition.StableId} 生成了 GPU 图像，但 Attribute 未声明 RequiresIntermediateTexture。");

                if (context.Image is GpuImageFrame produced) TrackTransient(produced);

                if (process.State == ProcessState.Running)
                    process.State = ProcessState.Valid;
            }

            if (generation != document.ExecutionGeneration || token.IsCancellationRequested)
                throw new OperationCanceledException(token);
            if (context.DisplayTarget is not DisplayTargetFrame displayTarget)
                throw new InvalidOperationException("过程没有到达固定的视图渲染终端；保留最后有效画面。");

            GpuImageFrame? oldFinal = document.FinalGpuFrame;
            document.FinalGpuFrame = displayTarget.RetainedSource;
            foreach (GpuImageFrame image in transientImages.Where(image => !ReferenceEquals(image, document.FinalGpuFrame)))
                image.Dispose();
            transientImages.Clear();
            if (oldFinal is not null && !ReferenceEquals(oldFinal, document.FinalGpuFrame))
                oldFinal.Dispose();

            document.HasExecuted = true;
            document.HasLastValidFrame = true;

            void TrackTransient(GpuImageFrame image)
            {
                if (!ReferenceEquals(image, document.DecodedGpuFrame) && !transientImages.Contains(image))
                    transientImages.Add(image);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (GpuImageFrame image in transientImages) image.Dispose();
            foreach (IImageProcess process in document.Processes.Where(process => process.State is ProcessState.Running or ProcessState.Pending))
                process.State = ProcessState.Stale;
        }
        catch (Exception ex)
        {
            foreach (GpuImageFrame image in transientImages) image.Dispose();
            MarkFailure(document, runningProcess, ex.GetBaseException().Message);
        }
    }

    private static void MarkFailure(ImageDocument document, IImageProcess? failedProcess, string message)
    {
        if (failedProcess is not null)
        {
            failedProcess.State = ProcessState.Error;
            failedProcess.Diagnostic = message;
        }
        foreach (IImageProcess process in document.Processes.Where(process =>
                     !ReferenceEquals(process, failedProcess) && process.State is ProcessState.Running or ProcessState.Pending))
        {
            process.State = document.HasLastValidFrame ? ProcessState.Stale : ProcessState.NotRun;
            process.Diagnostic = document.HasLastValidFrame ? "执行失败，中央区域保留最后有效画面。" : "上游执行失败。";
        }
    }

    private sealed class PipelineExecutionServices : IServiceProvider, IDecoderExecutionService
    {
        private readonly RendererD3D11 _renderer;
        private readonly ImageDecodeService _decoder;
        private readonly ImageDocument _document;
        private readonly bool _forceDecode;
        private readonly long _generation;

        public PipelineExecutionServices(
            RendererD3D11 renderer,
            ImageDecodeService decoder,
            ImageDocument document,
            bool forceDecode,
            long generation)
        {
            _renderer = renderer;
            _decoder = decoder;
            _document = document;
            _forceDecode = forceDecode;
            _generation = generation;
        }

        public async ValueTask<GpuImageFrame> GetGpuImageAsync(
            DecoderProcess decoder,
            CancellationToken cancellationToken)
        {
            bool requiresDecode = _forceDecode || _document.DecodedGpuFrame is null;
            if (!requiresDecode)
            {
                decoder.State = ProcessState.Cached;
                decoder.Diagnostic = "复用该文档的解码 GPU 源纹理；未发生 CPU 解码或重复上传。";
                return _document.DecodedGpuFrame!;
            }

            decoder.Diagnostic = "正在执行压缩解码；尚未提交 GPU 源纹理。";
            DecodedImageFrame decoded = await _decoder.DecodeAsync(_document.Source, decoder, cancellationToken).ConfigureAwait(true);
            if (_generation != _document.ExecutionGeneration)
                throw new OperationCanceledException(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            GpuImageFrame gpuImage = _renderer.CreateGpuImageFrame(decoded);
            GpuImageFrame? previousDecoded = _document.DecodedGpuFrame;
            _document.DecodedGpuFrame = gpuImage;
            if (previousDecoded is not null && !ReferenceEquals(previousDecoded, _document.FinalGpuFrame))
                previousDecoded.Dispose();

            decoder.ActualBackend = decoded.ActualBackend;
            decoder.BackendFallbackDetail = decoded.UsedFallback ? decoded.Diagnostic : "";
            decoder.Diagnostic = decoded.Diagnostic +
                                 $" CPU 临时缓冲：{decoded.CpuByteCount / (1024.0 * 1024.0):0.##} MiB；已规范化为 RGBA16F scRGB/Linear，CPU 像素缓冲不驻留。";
            if (decoded.UsedFallback) decoder.State = ProcessState.Warning;
            return gpuImage;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType.IsInstanceOfType(_renderer)) return _renderer;
            if (serviceType.IsInstanceOfType(_decoder)) return _decoder;
            if (serviceType.IsInstanceOfType(this)) return this;
            return null;
        }
    }
}
