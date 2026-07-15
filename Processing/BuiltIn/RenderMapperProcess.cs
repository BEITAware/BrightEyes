using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.RenderMapper,
    "Process_RenderMapper_Name",
    "Process_RenderMapper_Description",
    "Color",
    ProcessDataType.BrightEyesIntermediateImage,
    ProcessDataType.BrightEyesIntermediateImage,
    RibbonTab = "Process",
    RibbonGroup = "Color",
    RibbonOrder = 20,
    IconResourceKey = "ProcessIcon.Mapper",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    ExecutionDomain = ProcessExecutionDomain.Gpu,
    Version = 2)]
public sealed class RenderMapperProcess : IntermediateImageProcessBase
{
    [ProcessParameter("preset", "Parameter_RenderPreset", ProcessParameterEditor.Enum, 0, AutoValueSource = "BrightEyes default")]
    public ProcessParameter<ToneMappingMode> Preset { get; } = new(ToneMappingMode.ColorCorrectV1);

    [ProcessParameter("sdr-white", "Parameter_SdrWhite", ProcessParameterEditor.Number, 1, Unit = "nit", Minimum = 40, Maximum = 1000, Step = 1, AutoValueSource = "Windows SDR white")]
    public ProcessParameter<float> SdrWhiteNits { get; } = new(203.0f);

    [ProcessParameter("target-peak", "Parameter_TargetPeak", ProcessParameterEditor.Number, 2, Unit = "nit", Minimum = 80, Maximum = 10000, Step = 10, AutoValueSource = "DXGI display metadata")]
    public ProcessParameter<float> TargetPeakNits { get; } = new(1000.0f);

    [ProcessParameter("hlg-ootf", "Parameter_HlgOotf", ProcessParameterEditor.Boolean, 3, AutoValueSource = "Source photometric model")]
    public ProcessParameter<bool> ApplyHlgOotf { get; } = new(true);

    [ProcessParameter("gamut-mapping", "Parameter_GamutMapping", ProcessParameterEditor.Enum, 4, AutoValueSource = "Color correct v1")]
    public ProcessParameter<GamutMappingMode> GamutMapping { get; } = new(GamutMappingMode.PerceptualCompress);

    [ProcessParameter("ultrahdr-reconstruct", "Parameter_UltraHdrReconstruct", ProcessParameterEditor.Boolean, 5, AutoValueSource = "Gain map metadata")]
    public ProcessParameter<bool> ReconstructUltraHdr { get; } = new(true);

    [ProcessParameter("background", "Parameter_Background", ProcessParameterEditor.Enum, 6, AutoValueSource = "Application theme")]
    public ProcessParameter<BackgroundMode> Background { get; } = new(BackgroundMode.ApplicationBackground);

    public override string Summary => $"{Preset.Value} / {TargetPeakNits.Value:0.#} nit / {GamutMapping.Value}";

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        ImageSemantics input = context.UpstreamSemantics ?? ImageSemantics.DefaultRelative;
        SetInputAutomatic(input);
        float sdrWhite = context.Display.HasSystemSdrWhiteLevel
            ? context.Display.SdrWhiteLevelNits
            : (context.Display.IsHdr ? 203.0f : 100.0f);
        float targetPeak = context.Display.IsHdr
            ? Math.Max(context.Display.EffectivePeakNits, 400.0f)
            : 100.0f;
        SetAutomatic(Preset, ToneMappingMode.ColorCorrectV1, "Versioned BrightEyes default");
        SetAutomatic(SdrWhiteNits, sdrWhite, context.Display.HasSystemSdrWhiteLevel ? "Windows SDR white" : "Display fallback");
        SetAutomatic(TargetPeakNits, targetPeak, context.Display.IsHdr ? "DXGI display metadata" : "SDR terminal");
        SetAutomatic(ApplyHlgOotf, input.PhotometricModel == PhotometricModel.SceneLinearRelative, "Input photometric model");
        SetAutomatic(GamutMapping, GamutMappingMode.PerceptualCompress, "Color correct v1");
        SetAutomatic(ReconstructUltraHdr, context.Source?.DetectedMetadata.HasUltraHdrGainMap == true, "Gain map metadata");
        SetAutomatic(Background, BackgroundMode.ApplicationBackground, "Application theme");
        var output = new ImageSemantics(
            Renderer.ColorPrimaries.Rec709,
            Renderer.ColorTransferFunction.Linear,
            PhotometricModel.DisplayLinearAbsolute,
            sdrWhite,
            targetPeak,
            ColorRange.Full,
            input.Alpha);
        SetOutputAutomatic(output, "Render mapper effective output");
        Diagnostic = "GPU 执行；连续颜色过程可与终端编码融合，逻辑过程仍独立显示。";
        NotifySummaryChanged();
    }
}
