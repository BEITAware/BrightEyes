using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.ViewRendering,
    "Process_ViewRendering_Name",
    "Process_ViewRendering_Description",
    "View",
    RibbonTab = "Process",
    RibbonGroup = "View",
    RibbonOrder = 1000,
    IconResourceKey = "ProcessIcon.View",
    AllowMultiple = false,
    CanAdd = false,
    CanDelete = false,
    CanDisable = false,
    CanMove = false,
    Role = ProcessRole.Terminal,
    ExecutionDomain = ProcessExecutionDomain.Gpu,
    Version = 7)]
public sealed class ViewRenderingProcess : ProcessBase
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

    [ProcessParameter("source-peak", "Parameter_SourcePeak", ProcessParameterEditor.Number, 5, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 10, AutoValueSource = "Application source interpretation")]
    public ProcessParameter<float> SourcePeakNits { get; } = new(100.0f);

    [ProcessParameter("gamut-mapping", "Parameter_GamutMapping", ProcessParameterEditor.Enum, 6, AutoValueSource = "Color correct v1")]
    public ProcessParameter<GamutMappingMode> GamutMapping { get; } = new(GamutMappingMode.PerceptualCompress);

    [ProcessParameter("background", "Parameter_Background", ProcessParameterEditor.Enum, 7, AutoValueSource = "Application theme")]
    public ProcessParameter<BackgroundMode> Background { get; } = new(BackgroundMode.ApplicationBackground);

    [ProcessParameter("source-reference-white", "Parameter_SourceReferenceWhite", ProcessParameterEditor.Number, 8, Unit = "nit", Minimum = 0.01, Maximum = 10000, Step = 1, AutoValueSource = "Application source interpretation", IsAdvanced = true)]
    public ProcessParameter<float> SourceReferenceWhiteNits { get; } = new(100.0f);

    [ProcessParameter("source-relative", "Parameter_SourceIsRelative", ProcessParameterEditor.Boolean, 9, AutoValueSource = "Application source interpretation", IsAdvanced = true)]
    public ProcessParameter<bool> SourceIsRelative { get; } = new(true);

    public override string Summary => $"{Preset.Value} / {InterpolationLabel(Interpolation.Value)} / {TargetPeakNits.Value:0.#} nit / {GamutMapping.Value}";

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
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
        bool sourceIsSceneLinear = context.Source?.DetectedMetadata.PhotometricModel == PhotometricModel.SceneLinearRelative;
        bool sourceIsRelative = context.Source?.DetectedMetadata.PhotometricModel == PhotometricModel.DisplayLinearRelative;
        float sourceReferenceWhite = context.Source?.DetectedMetadata.ReferenceWhiteNits ?? 100.0f;
        SetAutomatic(ApplyHlgOotf, sourceIsSceneLinear, "Application source interpretation");
        SetAutomatic(SourcePeakNits, context.Source?.DetectedMetadata.NominalPeakNits ?? 100.0f, "Application source interpretation");
        SetAutomatic(GamutMapping, GamutMappingMode.PerceptualCompress, "Color correct v1");
        SetAutomatic(Background, BackgroundMode.ApplicationBackground, "Application theme");
        SetAutomatic(SourceReferenceWhiteNits, sourceReferenceWhite, "Application source interpretation");
        SetAutomatic(SourceIsRelative, sourceIsRelative, "Application source interpretation");
        Diagnostic = "固定终端过程；把上一张扩展 sRGB 线性图像映射到视图空间并提交显示。";
        NotifySummaryChanged();
    }

    public override ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Image is not Renderer.GpuImageFrame input)
            throw new InvalidOperationException("视图渲染需要一张可用的 GPU 中间图像。");

        var renderer = (Renderer.RendererD3D11?)context.Services.GetService(typeof(Renderer.RendererD3D11))
            ?? throw new InvalidOperationException("视图渲染服务不可用。");
        renderer.SetSdrCalibration(SdrWhiteNits.Value, Math.Min(TargetPeakNits.Value, 1000.0f));
        renderer.SetTargetPeak(TargetPeakNits.Value);
        renderer.SetRenderMapping(
            Preset.Value,
            GamutMapping.Value,
            ApplyHlgOotf.Value,
            Interpolation.Value,
            SourcePeakNits.Value,
            SourceReferenceWhiteNits.Value,
            SourceIsRelative.Value);
        renderer.SetPresentationBackground(Background.Value);
        Renderer.PointwiseImageOperations operations = context.TakePointwiseOperations();
        context.SetDisplayTarget(renderer.SetPresentationImage(input, operations));
        Diagnostic = $"视图映射参数已提交；使用 {InterpolationLabel(Interpolation.Value)} 插值，终端规范化、映射和编码在一个最终 GPU Pass 中执行。";
        return ValueTask.CompletedTask;
    }

    private static string InterpolationLabel(ViewInterpolationMode mode) => mode switch
    {
        ViewInterpolationMode.BrightEyesBilinear => "BrightEyes Bilinear",
        _ => mode.ToString()
    };

    private static void SetAutomatic<T>(ProcessParameter<T> parameter, T value, string source)
    {
        parameter.SetDetectedValue(value, source, MetadataConfidence.High);
        parameter.SetAutomaticValue(value, source);
    }
}
