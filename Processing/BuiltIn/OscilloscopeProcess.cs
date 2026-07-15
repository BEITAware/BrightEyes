using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.Oscilloscope,
    "Process_Oscilloscope_Name",
    "Process_Oscilloscope_Description",
    "Analysis",
    ProcessDataType.BrightEyesIntermediateImage,
    ProcessDataType.BrightEyesIntermediateImage,
    RibbonTab = "Process",
    RibbonGroup = "Analysis",
    RibbonOrder = 40,
    IconResourceKey = "ProcessIcon.Scope",
    AllowMultiple = true,
    CanDelete = true,
    CanDisable = true,
    ExecutionDomain = ProcessExecutionDomain.Observer,
    Version = 2)]
public sealed class OscilloscopeProcess : IntermediateImageProcessBase
{
    private AnalysisData? _latestAnalysis;
    [ProcessParameter("kind", "Parameter_ScopeKind", ProcessParameterEditor.Enum, 0, AutoValueSource = "Process default")]
    public ProcessParameter<OscilloscopeKind> Kind { get; } = new(OscilloscopeKind.Histogram);

    [ProcessParameter("channel", "Parameter_ScopeChannel", ProcessParameterEditor.Enum, 1, AutoValueSource = "Process default")]
    public ProcessParameter<OscilloscopeChannel> Channel { get; } = new(OscilloscopeChannel.Rgb);

    [ProcessParameter("unit", "Parameter_SignalUnit", ProcessParameterEditor.Enum, 2, AutoValueSource = "Input photometric model")]
    public ProcessParameter<SignalUnit> Unit { get; } = new(SignalUnit.ScRgb);

    [ProcessParameter("show-negative", "Parameter_ShowNegative", ProcessParameterEditor.Boolean, 3, AutoValueSource = "Unbounded intermediate contract")]
    public ProcessParameter<bool> ShowNegative { get; } = new(true);

    [ProcessParameter("show-overrange", "Parameter_ShowOverrange", ProcessParameterEditor.Boolean, 4, AutoValueSource = "Unbounded intermediate contract")]
    public ProcessParameter<bool> ShowOverrange { get; } = new(true);

    [ProcessParameter("sample-rate", "Parameter_SampleRate", ProcessParameterEditor.Number, 5, Unit = "%", Minimum = 1, Maximum = 100, Step = 1, AutoValueSource = "Interactive analysis budget")]
    public ProcessParameter<int> SampleRatePercent { get; } = new(25);

    [ProcessParameter("refresh-rate", "Parameter_RefreshRate", ProcessParameterEditor.Number, 6, Unit = "Hz", Minimum = 1, Maximum = 60, Step = 1, AutoValueSource = "Interactive analysis budget")]
    public ProcessParameter<int> RefreshRate { get; } = new(15);

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

    public override string Summary => LatestAnalysis is null
        ? $"{Kind.Value} / {Channel.Value} / {Unit.Value} / GPU compact readback"
        : $"{Kind.Value} / {LatestAnalysis.Values.Length} bins / {Unit.Value} / cached";

    public void AcceptAnalysis(float[] values)
    {
        LatestAnalysis = new AnalysisData(
            Kind.Value.ToString(),
            values,
            OutputSemantics,
            DateTimeOffset.UtcNow);
        State = ProcessState.Cached;
        Diagnostic = $"GPU compute 完成；仅回读 {values.Length * sizeof(float)} bytes 紧凑分析数据，未回读完整图像。";
    }

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        ImageSemantics input = context.UpstreamSemantics ?? ImageSemantics.DefaultRelative;
        SetInputAutomatic(input);
        SetOutputAutomatic(input, "Observer pass-through identity");
        SetAutomatic(Unit, input.PhotometricModel == PhotometricModel.DisplayLinearAbsolute ? SignalUnit.Nits : SignalUnit.ScRgb, "Input photometric model");
        SetAutomatic(ShowNegative, true, "Unbounded intermediate contract");
        SetAutomatic(ShowOverrange, true, "Unbounded intermediate contract");
        SetAutomatic(SampleRatePercent, 25, "Interactive analysis budget");
        SetAutomatic(RefreshRate, 15, "Interactive analysis budget");
        Diagnostic = "GPU 观察过程；仅允许回读紧凑分析缓冲区，输出图像身份保持不变。";
        NotifySummaryChanged();
    }
}
