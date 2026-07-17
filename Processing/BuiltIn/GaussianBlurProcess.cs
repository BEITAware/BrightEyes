using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.GaussianBlur,
    "Process_GaussianBlur_Name",
    "Process_GaussianBlur_Description",
    "Filter",
    RibbonTab = "Process",
    RibbonGroup = "Filter",
    RibbonOrder = 10,
    IconResourceKey = "ProcessIcon.Blur",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    CanMove = true,
    Role = ProcessRole.Image,
    RequiresIntermediateTexture = true,
    ExecutionDomain = ProcessExecutionDomain.Gpu,
    Version = 2)]
public sealed class GaussianBlurProcess : ProcessBase
{
    [ProcessParameter("radius", "Parameter_BlurRadius", ProcessParameterEditor.Number, 0, Unit = "px", Minimum = 0, Maximum = 64, Step = 1, AutoValueSource = "Gaussian blur default")]
    public ProcessParameter<int> Radius { get; } = new(8);

    [ProcessParameter("sigma", "Parameter_BlurSigma", ProcessParameterEditor.Number, 1, Unit = "px", Minimum = 0.1, Maximum = 32, Step = 0.1, AutoValueSource = "Gaussian blur default")]
    public ProcessParameter<float> Sigma { get; } = new(4.0f);

    public override string Summary => $"{Radius.Value}px / sigma {Sigma.Value:0.##} / RGBA16F linear extended-sRGB";

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        SetAutomatic(Radius, 8, "Gaussian blur default");
        SetAutomatic(Sigma, 4.0f, "Gaussian blur default");
        Diagnostic = "等待 GPU 执行；本过程会产生一张独立的线性中间纹理。";
        NotifySummaryChanged();
    }

    public override ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Image is not GpuImageFrame input)
            throw new InvalidOperationException("高斯模糊需要一张 GPU 中间图像。");
        var renderer = (RendererD3D11?)context.Services.GetService(typeof(RendererD3D11))
            ?? throw new InvalidOperationException("GPU 过程执行服务不可用。");
        GpuImageFrame output = renderer.ApplyGaussianBlur(input, Radius.Value, Sigma.Value, cancellationToken);
        context.SetImage(output);
        Diagnostic = $"GPU 两向高斯卷积完成；已生成 {output.Width} x {output.Height} RGBA16F 独立中间纹理（约 {output.ApproximateByteCount / (1024.0 * 1024.0):0.##} MiB）。";
        return ValueTask.CompletedTask;
    }

    private static void SetAutomatic<T>(ProcessParameter<T> parameter, T value, string source)
    {
        parameter.SetDetectedValue(value, source, MetadataConfidence.High);
        parameter.SetAutomaticValue(value, source);
    }
}
