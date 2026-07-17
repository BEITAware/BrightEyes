using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.BuiltIn;

public abstract class AnalysisProcessBase : ProcessBase
{
    private AnalysisData? _latestAnalysis;

    [ProcessParameter("sample-rate", "Parameter_SampleRate", ProcessParameterEditor.Number, 90, Unit = "%", Minimum = 1, Maximum = 100, Step = 1, AutoValueSource = "Interactive analysis budget")]
    public ProcessParameter<int> SampleRatePercent { get; } = new(25);

    public abstract AnalysisKind Kind { get; }
    public abstract AnalysisChannel Channel { get; }
    public abstract AnalysisPresentation Presentation { get; }
    public virtual bool LogarithmicScale => false;
    public bool IsStrictHistogram => Kind == AnalysisKind.StrictHistogram;
    public double PlotHeight => Kind == AnalysisKind.StrictHistogram ? 272.0 : 132.0;

    public AnalysisData? LatestAnalysis
    {
        get => _latestAnalysis;
        private set
        {
            _latestAnalysis = value;
            OnPropertyChanged(nameof(LatestAnalysis));
            NotifySummaryChanged();
        }
    }

    protected abstract string AnalysisSummary { get; }

    public override string Summary => LatestAnalysis is null
        ? $"{AnalysisSummary} / GPU compact readback"
        : $"{AnalysisSummary} / {LatestAnalysis.Values.Length} bins / cached";

    public override ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var renderer = (RendererD3D11?)context.Services.GetService(typeof(RendererD3D11))
            ?? throw new InvalidOperationException("GPU 分析服务不可用。");

        AnalysisKind kind = Kind;
        AnalysisChannel channel = Channel;
        AnalysisPresentation presentation = Presentation;
        bool logarithmicScale = LogarithmicScale;
        SynchronizationContext? synchronizationContext = SynchronizationContext.Current;
        Action<float[]> acceptResult = values =>
        {
            void Accept() => AcceptAnalysis(values, kind, channel, presentation, logarithmicScale);
            if (synchronizationContext is null)
                Accept();
            else
                synchronizationContext.Post(_ => Accept(), null);
        };

        bool displayTarget = context.DisplayTarget is not null;
        bool requested = context.DisplayTarget switch
        {
            null when context.Image is GpuImageFrame input => renderer.RequestAnalysis(
                input,
                SampleRatePercent.Value,
                kind,
                channel,
                acceptResult),
            DisplayTargetFrame target => renderer.RequestDisplayTargetAnalysis(
                target,
                SampleRatePercent.Value,
                kind,
                channel,
                acceptResult),
            _ => throw new InvalidOperationException("分析过程需要 GPU 处理图像或已提交的显示目标。")
        };

        if (!requested)
        {
            State = ProcessState.Warning;
            Diagnostic = displayTarget
                ? "显示目标尚不可分析或请求队列已满；本次只读统计未提交。"
                : "GPU 分析请求队列已满；本次只读统计未提交，管线图像保持不变。";
        }
        else
        {
            Diagnostic = displayTarget
                ? "已排队分析视图映射后的显示目标；不复制显示图像，仅回读紧凑统计。"
                : "已在当前处理纹理上排队只读 GPU 分析；不替换图像，仅回读紧凑统计。";
        }
        return ValueTask.CompletedTask;
    }

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        SetAutomatic(SampleRatePercent, 25, "Interactive analysis budget");
        RecalculateAnalysisParameters();
        Diagnostic = "GPU 观察过程；按所在位置分析处理纹理或显示目标，不修改图像。";
        NotifySummaryChanged();
    }

    protected virtual void RecalculateAnalysisParameters()
    {
    }

    private void AcceptAnalysis(
        float[] values,
        AnalysisKind kind,
        AnalysisChannel channel,
        AnalysisPresentation presentation,
        bool logarithmicScale)
    {
        LatestAnalysis = new AnalysisData(
            kind,
            channel,
            presentation,
            logarithmicScale,
            values,
            DateTimeOffset.UtcNow);
        State = ProcessState.Cached;
        Diagnostic = $"GPU compute 完成；回读 {values.Length * sizeof(float)} bytes 紧凑分析数据。";
    }

    protected static void SetAutomatic<T>(ProcessParameter<T> parameter, T value, string source)
    {
        parameter.SetDetectedValue(value, source, MetadataConfidence.High);
        parameter.SetAutomaticValue(value, source);
    }
}

[ProcessDefinition(
    BuiltInProcessIds.Waveform,
    "Process_Waveform_Name",
    "Process_Waveform_Description",
    "Analysis",
    RibbonTab = "Process",
    RibbonGroup = "Analysis",
    RibbonOrder = 40,
    IconResourceKey = "ProcessIcon.Scope",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    Role = ProcessRole.Observer,
    ExecutionDomain = ProcessExecutionDomain.Observer,
    Version = 1)]
public sealed class WaveformProcess : AnalysisProcessBase
{
    [ProcessParameter("mode", "Parameter_WaveformMode", ProcessParameterEditor.Enum, 0, AutoValueSource = "Luminance waveform default")]
    public ProcessParameter<WaveformDisplayMode> Mode { get; } = new(WaveformDisplayMode.Luminance);

    public override AnalysisKind Kind => AnalysisKind.Waveform;
    public override AnalysisChannel Channel => Mode.Value == WaveformDisplayMode.Luminance
        ? AnalysisChannel.Luminance
        : AnalysisChannel.Rgb;
    public override AnalysisPresentation Presentation => Mode.Value switch
    {
        WaveformDisplayMode.ParadeRgb => AnalysisPresentation.RgbParade,
        WaveformDisplayMode.OverlayRgb => AnalysisPresentation.RgbOverlay,
        _ => AnalysisPresentation.Luminance
    };
    protected override string AnalysisSummary => $"{Mode.Value} / linear amplitude";

    protected override void RecalculateAnalysisParameters() =>
        SetAutomatic(Mode, WaveformDisplayMode.Luminance, "Luminance waveform default");
}

[ProcessDefinition(
    BuiltInProcessIds.Histogram,
    "Process_Histogram_Name",
    "Process_Histogram_Description",
    "Analysis",
    RibbonTab = "Process",
    RibbonGroup = "Analysis",
    RibbonOrder = 41,
    IconResourceKey = "ProcessIcon.Scope",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    Role = ProcessRole.Observer,
    ExecutionDomain = ProcessExecutionDomain.Observer,
    Version = 1)]
public sealed class HistogramProcess : AnalysisProcessBase
{
    [ProcessParameter("channel", "Parameter_HistogramChannel", ProcessParameterEditor.Enum, 0, AutoValueSource = "Luminance histogram default")]
    public ProcessParameter<HistogramChannel> SelectedChannel { get; } = new(HistogramChannel.Luminance);

    [ProcessParameter("log-amplitude", "Parameter_LogAmplitude", ProcessParameterEditor.Boolean, 1, AutoValueSource = "Linear amplitude default")]
    public ProcessParameter<bool> LogAmplitude { get; } = new(false);

    public override AnalysisKind Kind => AnalysisKind.Histogram;
    public override AnalysisChannel Channel => SelectedChannel.Value switch
    {
        HistogramChannel.Red => AnalysisChannel.Red,
        HistogramChannel.Green => AnalysisChannel.Green,
        HistogramChannel.Blue => AnalysisChannel.Blue,
        HistogramChannel.Alpha => AnalysisChannel.Alpha,
        _ => AnalysisChannel.Luminance
    };
    public override AnalysisPresentation Presentation => Channel == AnalysisChannel.Luminance
        ? AnalysisPresentation.Luminance
        : AnalysisPresentation.SingleChannel;
    public override bool LogarithmicScale => LogAmplitude.Value;
    protected override string AnalysisSummary => $"{SelectedChannel.Value} / {(LogAmplitude.Value ? "log" : "linear")} amplitude";

    protected override void RecalculateAnalysisParameters()
    {
        SetAutomatic(SelectedChannel, HistogramChannel.Luminance, "Luminance histogram default");
        SetAutomatic(LogAmplitude, false, "Linear amplitude default");
    }
}

[ProcessDefinition(
    BuiltInProcessIds.Vectorscope,
    "Process_Vectorscope_Name",
    "Process_Vectorscope_Description",
    "Analysis",
    RibbonTab = "Process",
    RibbonGroup = "Analysis",
    RibbonOrder = 42,
    IconResourceKey = "ProcessIcon.Scope",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    Role = ProcessRole.Observer,
    ExecutionDomain = ProcessExecutionDomain.Observer,
    Version = 1)]
public sealed class VectorscopeProcess : AnalysisProcessBase
{
    [ProcessParameter("log-radius", "Parameter_LogRadius", ProcessParameterEditor.Boolean, 0, AutoValueSource = "Linear radius default")]
    public ProcessParameter<bool> LogRadius { get; } = new(false);

    public override AnalysisKind Kind => AnalysisKind.Vectorscope;
    public override AnalysisChannel Channel => AnalysisChannel.Rgb;
    public override AnalysisPresentation Presentation => AnalysisPresentation.Hue;
    public override bool LogarithmicScale => LogRadius.Value;
    protected override string AnalysisSummary => $"hue / {(LogRadius.Value ? "log" : "linear")} radius";

    protected override void RecalculateAnalysisParameters() =>
        SetAutomatic(LogRadius, false, "Linear radius default");
}

[ProcessDefinition(
    BuiltInProcessIds.StrictHistogram,
    "Process_StrictHistogram_Name",
    "Process_StrictHistogram_Description",
    "Analysis",
    RibbonTab = "Process",
    RibbonGroup = "Analysis",
    RibbonOrder = 43,
    IconResourceKey = "ProcessIcon.Scope",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    Role = ProcessRole.Observer,
    ExecutionDomain = ProcessExecutionDomain.Observer,
    Version = 1)]
public sealed class StrictHistogramProcess : AnalysisProcessBase
{
    [ProcessParameter("log-amplitude", "Parameter_LogAmplitude", ProcessParameterEditor.Boolean, 0, AutoValueSource = "Linear amplitude default")]
    public ProcessParameter<bool> LogAmplitude { get; } = new(false);

    public override AnalysisKind Kind => AnalysisKind.StrictHistogram;
    public override AnalysisChannel Channel => AnalysisChannel.Luminance;
    public override AnalysisPresentation Presentation => AnalysisPresentation.Luminance;
    public override bool LogarithmicScale => LogAmplitude.Value;
    protected override string AnalysisSummary => $"256 exact rows / {(LogAmplitude.Value ? "log" : "linear")} amplitude";

    protected override void RecalculateAnalysisParameters() =>
        SetAutomatic(LogAmplitude, false, "Linear amplitude default");
}
