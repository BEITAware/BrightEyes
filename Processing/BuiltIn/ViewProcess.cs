using BrightEyes.Processing.Contracts;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.View,
    "Process_View_Name",
    "Process_View_Description",
    "View",
    ProcessDataType.BrightEyesIntermediateImage,
    ProcessDataType.BrightEyesIntermediateImage,
    RibbonTab = "Process",
    RibbonGroup = "View",
    RibbonOrder = 30,
    IconResourceKey = "ProcessIcon.View",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    ExecutionDomain = ProcessExecutionDomain.Observer,
    Version = 2)]
public sealed class ViewProcess : IntermediateImageProcessBase
{
    public override string Summary => "透传观察 / SDR 或 PQ-Rec.2020 终端编码";

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        ImageSemantics input = context.UpstreamSemantics ?? ImageSemantics.DefaultRelative;
        SetInputAutomatic(input);
        SetOutputAutomatic(input, "View pass-through identity");
        Diagnostic = context.Display.IsHdr
            ? $"HDR / {context.Display.ColorSpace} / R10G10B10A2_UNORM / {context.Display.EffectivePeakNits:0.#} nit"
            : $"SDR / {context.Display.ColorSpace} / R10G10B10A2_UNORM";
        NotifySummaryChanged();
    }
}
