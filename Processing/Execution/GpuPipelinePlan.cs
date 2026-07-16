using BrightEyes.Processing.BuiltIn;
using BrightEyes.Processing.Contracts;

namespace BrightEyes.Processing.Execution;

/// <summary>
/// A process that contributes real, ordered GPU work to the document graph.
/// Logical operations remain separate here; the renderer is free to fuse
/// adjacent pointwise operations into a single physical pass.
/// </summary>
public interface IGpuPipelineNode
{
    void AppendGpuOperations(GpuPipelineBuilder builder);
}

public interface IDecodePipelineNode
{
    Task<DecodedImageFrame> DecodeAsync(
        ImageDecodeService service,
        RawImage source,
        CancellationToken cancellationToken);

    void AcceptDecodedFrame(DecodedImageFrame frame);
}

public abstract record GpuPipelineOperation(Guid ProcessId);

/// <summary>
/// Base contract for neighborhood, resampling, temporal, and other operations
/// that require a real working-texture boundary instead of pointwise fusion.
/// </summary>
public abstract record MaterializingGpuOperation(
    Guid ProcessId,
    int RequiredPassCount) : GpuPipelineOperation(ProcessId);

public sealed record RenderMappingGpuOperation(
    Guid ProcessId,
    ToneMappingMode ToneMapping,
    GamutMappingMode GamutMapping,
    float SdrWhiteNits,
    float TargetPeakNits,
    bool ApplyHlgOotf) : GpuPipelineOperation(ProcessId);

/// <summary>
/// A separable Gaussian convolution. It is deliberately represented as a
/// materializing operation so future neighborhood filters can use the same
/// pooled working-texture boundary.
/// </summary>
public sealed record GaussianBlurGpuOperation(
    Guid ProcessId,
    int Radius,
    float Sigma) : MaterializingGpuOperation(ProcessId, 2);

public sealed record AnalysisTapGpuOperation(
    Guid ProcessId,
    int SampleRatePercent,
    AnalysisKind Kind,
    AnalysisChannel Channel,
    Action<float[]> AcceptResult) : GpuPipelineOperation(ProcessId);

public sealed record PresentGpuOperation(Guid ProcessId) : GpuPipelineOperation(ProcessId);

public sealed class GpuPipelineBuilder
{
    private readonly List<GpuPipelineOperation> _operations = new();

    public GpuPipelineBuilder(PhotometricModel sourcePhotometricModel)
    {
        SourcePhotometricModel = sourcePhotometricModel;
    }

    public PhotometricModel SourcePhotometricModel { get; }

    public void Add(GpuPipelineOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_operations.OfType<PresentGpuOperation>().Any())
            throw new InvalidOperationException("视图过程必须是 GPU 流程的终点。 ");
        _operations.Add(operation);
    }

    public GpuPipelinePlan Build()
    {
        int presentCount = _operations.Count(operation => operation is PresentGpuOperation);
        if (presentCount != 1)
            throw new InvalidOperationException(presentCount == 0
                ? "流程没有可执行的视图终点。 "
                : "流程只能包含一个启用的视图终点。 ");
        return new GpuPipelinePlan(_operations.ToArray(), SourcePhotometricModel);
    }
}

public sealed class GpuPipelinePlan
{
    public const int MaximumFusedPointwiseOperations = 8;

    public GpuPipelinePlan(IReadOnlyList<GpuPipelineOperation> operations, PhotometricModel sourcePhotometricModel)
    {
        Operations = operations;
        SourcePhotometricModel = sourcePhotometricModel;
        LogicalNodeCount = operations.Select(operation => operation.ProcessId).Distinct().Count();
        MaterializingOperationCount = operations.Count(operation => operation is MaterializingGpuOperation);
        MaterializingPassCount = operations.OfType<MaterializingGpuOperation>().Sum(operation => operation.RequiredPassCount);
        AnalysisTapCount = operations.Count(operation => operation is AnalysisTapGpuOperation);
        int lastMaterializingIndex = -1;
        for (int index = 0; index < operations.Count; index++)
            if (operations[index] is MaterializingGpuOperation) lastMaterializingIndex = index;
        MaterializedCachePrefix = lastMaterializingIndex < 0
            ? Array.Empty<GpuPipelineOperation>()
            : operations.Take(lastMaterializingIndex + 1)
                .Where(operation => operation is RenderMappingGpuOperation or MaterializingGpuOperation)
                .ToArray();
        EstimatedRebuildGraphicsPassCount = EstimateRebuildGraphicsPasses(operations);
    }

    public IReadOnlyList<GpuPipelineOperation> Operations { get; }
    public PhotometricModel SourcePhotometricModel { get; }
    public int LogicalNodeCount { get; }
    public int MaterializingOperationCount { get; }
    public int MaterializingPassCount { get; }
    public int AnalysisTapCount { get; }
    public IReadOnlyList<GpuPipelineOperation> MaterializedCachePrefix { get; }
    public int EstimatedRebuildGraphicsPassCount { get; }

    public bool HasMaterializingOperations => MaterializingOperationCount > 0;

    private static int EstimateRebuildGraphicsPasses(IReadOnlyList<GpuPipelineOperation> operations)
    {
        int passes = 0;
        int pendingPointwise = 0;
        bool inputIsWorking = false;
        foreach (GpuPipelineOperation operation in operations)
        {
            switch (operation)
            {
                case RenderMappingGpuOperation:
                    if (pendingPointwise == MaximumFusedPointwiseOperations)
                    {
                        passes++;
                        inputIsWorking = true;
                        pendingPointwise = 0;
                    }
                    pendingPointwise++;
                    break;
                case MaterializingGpuOperation materializing:
                    if (!inputIsWorking || pendingPointwise > 0) passes++;
                    passes += materializing.RequiredPassCount;
                    inputIsWorking = true;
                    pendingPointwise = 0;
                    break;
                case AnalysisTapGpuOperation:
                    if (!inputIsWorking || pendingPointwise > 0) passes++;
                    break;
                case PresentGpuOperation:
                    passes++;
                    pendingPointwise = 0;
                    break;
            }
        }
        return passes;
    }
}

public sealed record GpuPlanExecutionResult(
    bool ReusedMaterializedCache,
    int GraphicsPassCount);
