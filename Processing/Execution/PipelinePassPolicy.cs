using BrightEyes.Processing.Contracts;

namespace BrightEyes.Processing.Execution;

internal static class PipelinePassPolicy
{
    public static bool RequiresMaterializationBefore(
        ProcessDefinitionAttribute process,
        bool hasPendingPointwiseOperations) =>
        hasPendingPointwiseOperations &&
        (process.RequiresIntermediateTexture || process.Role == ProcessRole.Observer);
}
