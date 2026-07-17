using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.BuiltIn;

/// <summary>
/// An explicit color-space conversion comparable to a color-space-transform
/// node in a grading application. The node is self-contained: it never reads
/// an upstream declaration and never publishes output metadata. Adjacent
/// conversions are queued and fused into the next physical GPU pass.
/// </summary>
[ProcessDefinition(
    BuiltInProcessIds.ColorSpaceConversion,
    "Process_ColorSpaceConversion_Name",
    "Process_ColorSpaceConversion_Description",
    "Color",
    RibbonTab = "Process",
    RibbonGroup = "Color",
    RibbonOrder = 10,
    IconResourceKey = "ProcessIcon.Utility",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    CanMove = true,
    Role = ProcessRole.Image,
    RequiresIntermediateTexture = false,
    ExecutionDomain = ProcessExecutionDomain.Gpu,
    Version = 1)]
public sealed class ColorSpaceConversionProcess : ProcessBase
{
    [ProcessParameter("input.gamma", "Parameter_InputGamma", ProcessParameterEditor.Enum, 0, AutoValueSource = "scRGB decoder contract")]
    public ProcessParameter<ColorTransferFunction> InputGamma { get; } = new(ColorTransferFunction.Linear);

    [ProcessParameter("input.gamut", "Parameter_InputGamut", ProcessParameterEditor.Enum, 1, AutoValueSource = "scRGB decoder contract")]
    public ProcessParameter<ColorPrimaries> InputGamut { get; } = new(ColorPrimaries.Rec709);

    [ProcessParameter("output.gamma", "Parameter_OutputGamma", ProcessParameterEditor.Enum, 2, AutoValueSource = "Identity conversion default")]
    public ProcessParameter<ColorTransferFunction> OutputGamma { get; } = new(ColorTransferFunction.Linear);

    [ProcessParameter("output.gamut", "Parameter_OutputGamut", ProcessParameterEditor.Enum, 3, AutoValueSource = "Identity conversion default")]
    public ProcessParameter<ColorPrimaries> OutputGamut { get; } = new(ColorPrimaries.Rec709);

    public override string Summary =>
        $"{InputGamma.Value}/{InputGamut.Value} → {OutputGamma.Value}/{OutputGamut.Value} / fused pointwise";

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        SetAutomatic(InputGamma, ColorTransferFunction.Linear, "scRGB decoder contract");
        SetAutomatic(InputGamut, ColorPrimaries.Rec709, "scRGB decoder contract");
        SetAutomatic(OutputGamma, ColorTransferFunction.Linear, "Identity conversion default");
        SetAutomatic(OutputGamut, ColorPrimaries.Rec709, "Identity conversion default");
        Diagnostic = "显式转换输入/输出色彩空间；输出数值原样传递，不读取或发布过程元数据。";
        NotifySummaryChanged();
    }

    public override ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (InputGamma.Value == OutputGamma.Value && InputGamut.Value == OutputGamut.Value)
        {
            Diagnostic = "输入与输出契约相同；该原子 CST 为空操作，未加入 GPU 点运算队列。";
            return ValueTask.CompletedTask;
        }
        context.QueueColorConversion(new(
            InputGamma.Value,
            InputGamut.Value,
            OutputGamma.Value,
            OutputGamut.Value));
        Diagnostic = $"已排队第 {context.PendingColorConversions.Count} 个显式色彩转换；输出数值不会被自动转回 scRGB。";
        return ValueTask.CompletedTask;
    }

    private static void SetAutomatic<T>(ProcessParameter<T> parameter, T value, string source)
    {
        parameter.SetDetectedValue(value, source, MetadataConfidence.Authoritative);
        parameter.SetAutomaticValue(value, source);
    }
}
