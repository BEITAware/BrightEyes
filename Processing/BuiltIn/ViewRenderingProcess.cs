using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.ViewRendering,
    "Process_ViewRendering_Name",
    "Process_ViewRendering_Description",
    "View",
    ProcessDataType.BrightEyesIntermediateImage,
    ProcessDataType.DisplayTarget,
    RibbonTab = "Process",
    RibbonGroup = "View",
    RibbonOrder = 1000,
    IconResourceKey = "ProcessIcon.View",
    AllowMultiple = false,
    CanAdd = false,
    CanDelete = false,
    CanDisable = false,
    CanMove = false,
    ExecutionDomain = ProcessExecutionDomain.Gpu,
    Version = 5)]
public sealed class ViewRenderingProcess : IntermediateImageProcessBase
{
    [ProcessParameter("preset", "Parameter_RenderPreset", ProcessParameterEditor.Enum, 0, AutoValueSource = "BrightEyes default")]
    public ProcessParameter<ToneMappingMode> Preset { get; } = new(ToneMappingMode.ColorCorrectV1);

    [ProcessParameter("interpolation", "Parameter_Interpolation", ProcessParameterEditor.Enum, 1, AutoValueSource = "BrightEyes Bilinear")]
    public ProcessParameter<ViewInterpolationMode> Interpolation { get; } = new(ViewInterpolationMode.BrightEyesBilinear);

    [ProcessParameter("sdr-white", "Parameter_SdrWhite", ProcessParameterEditor.Number, 2, Unit = "nit", Minimum = 40, Maximum = 1000, Step = 1, AutoValueSource = "Windows SDR white")]
    public ProcessParameter<float> SdrWhiteNits { get; } = new(203.0f);

    [ProcessParameter("target-peak", "Parameter_TargetPeak", ProcessParameterEditor.Number, 3, Unit = "nit", Minimum = 80, Maximum = 10000, Step = 10, AutoValueSource = "DXGI display metadata")]
    public ProcessParameter<float> TargetPeakNits { get; } = new(1000.0f);

    [ProcessParameter("hlg-ootf", "Parameter_HlgOotf", ProcessParameterEditor.Boolean, 4, AutoValueSource = "Source photometric model")]
    public ProcessParameter<bool> ApplyHlgOotf { get; } = new(true);

    [ProcessParameter("gamut-mapping", "Parameter_GamutMapping", ProcessParameterEditor.Enum, 5, AutoValueSource = "Color correct v1")]
    public ProcessParameter<GamutMappingMode> GamutMapping { get; } = new(GamutMappingMode.PerceptualCompress);

    [ProcessParameter("background", "Parameter_Background", ProcessParameterEditor.Enum, 6, AutoValueSource = "Application theme")]
    public ProcessParameter<BackgroundMode> Background { get; } = new(BackgroundMode.ApplicationBackground);

    public override string Summary => $"{Preset.Value} / {InterpolationLabel(Interpolation.Value)} / {TargetPeakNits.Value:0.#} nit / {GamutMapping.Value}";

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
        SetAutomatic(Interpolation, ViewInterpolationMode.BrightEyesBilinear, "BrightEyes Bilinear");
        SetAutomatic(SdrWhiteNits, sdrWhite, context.Display.HasSystemSdrWhiteLevel ? "Windows SDR white" : "Display fallback");
        SetAutomatic(TargetPeakNits, targetPeak, context.Display.IsHdr ? "DXGI display metadata" : "SDR terminal");
        SetAutomatic(ApplyHlgOotf, input.PhotometricModel == PhotometricModel.SceneLinearRelative, "Input photometric model");
        SetAutomatic(GamutMapping, GamutMappingMode.PerceptualCompress, "Color correct v1");
        SetAutomatic(Background, BackgroundMode.ApplicationBackground, "Application theme");
        ImageSemantics output = context.Display.IsHdr
            ? new(
                Renderer.ColorPrimaries.Rec2020,
                Renderer.ColorTransferFunction.Pq,
                PhotometricModel.DisplayLinearAbsolute,
                sdrWhite,
                targetPeak,
                ColorRange.Full,
                Renderer.AlphaRepresentation.Opaque)
            : new(
                Renderer.ColorPrimaries.Rec709,
                Renderer.ColorTransferFunction.Srgb,
                PhotometricModel.DisplayLinearRelative,
                100.0f,
                100.0f,
                ColorRange.Full,
                Renderer.AlphaRepresentation.Opaque);
        SetOutputAutomatic(output, "Active display target");
        Diagnostic = "固定终端过程；把上一张扩展 sRGB 线性图像映射到视图空间并提交显示。";
        NotifySummaryChanged();
    }

    public override ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Value is not Renderer.GpuImageFrame input)
            throw new InvalidOperationException("视图渲染需要一张可用的 GPU 中间图像。");

        var renderer = (Renderer.RendererD3D11?)context.Services.GetService(typeof(Renderer.RendererD3D11))
            ?? throw new InvalidOperationException("视图渲染服务不可用。");
        renderer.SetSdrCalibration(SdrWhiteNits.Value, Math.Min(TargetPeakNits.Value, 1000.0f));
        renderer.SetTargetPeak(TargetPeakNits.Value);
        renderer.SetRenderMapping(Preset.Value, GamutMapping.Value, ApplyHlgOotf.Value, Interpolation.Value);
        renderer.SetPresentationBackground(Background.Value);
        context.Value = renderer.SetPresentationImage(input);
        Diagnostic = $"视图映射参数已提交；使用 {InterpolationLabel(Interpolation.Value)} 插值，终端规范化、映射和编码在一个最终 GPU Pass 中执行。";
        return ValueTask.CompletedTask;
    }

    private static string InterpolationLabel(ViewInterpolationMode mode) => mode switch
    {
        ViewInterpolationMode.BrightEyesBilinear => "BrightEyes Bilinear",
        _ => mode.ToString()
    };
}
