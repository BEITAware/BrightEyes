using BrightEyes.Processing.BuiltIn;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.Execution;

public interface IDecoderExecutionService
{
    bool IsNormalizationDeferred { get; }
    ValueTask<GpuImageFrame> GetGpuImageAsync(DecoderProcess decoder, CancellationToken cancellationToken);
}
