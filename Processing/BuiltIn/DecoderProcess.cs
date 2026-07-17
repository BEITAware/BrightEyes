using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using BrightEyes.Processing.Execution;
using BrightEyes.Renderer;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.Decoder,
    "Process_Decoder_Name",
    "Process_Decoder_Description",
    "Input",
    RibbonTab = "Process",
    RibbonGroup = "Input",
    RibbonOrder = 10,
    IconResourceKey = "ProcessIcon.Decoder",
    AllowMultiple = false,
    CanAdd = false,
    CanDelete = true,
    CanDisable = false,
    CanMove = false,
    Role = ProcessRole.Decoder,
    ExecutionDomain = ProcessExecutionDomain.Hybrid,
    Version = 5)]
public sealed class DecoderProcess : ProcessBase
{
    [ProcessParameter("backend", "Parameter_DecoderBackend", ProcessParameterEditor.Enum, 0, AutoValueSource = "Container metadata and backend capability")]
    public ProcessParameter<DecoderBackend> Backend { get; } = new(DecoderBackend.PhotoSauce);

    [ProcessParameter("allow-fallback", "Parameter_AllowFallback", ProcessParameterEditor.Boolean, 1, DefaultValue = "true")]
    public ProcessParameter<bool> AllowFallback { get; } = new(true);

    [ProcessParameter("frame", "Parameter_Frame", ProcessParameterEditor.Number, 2, Minimum = 0, Maximum = 65535, Step = 1, AutoValueSource = "First frame")]
    public ProcessParameter<int> Frame { get; } = new(0);

    [ProcessParameter("apply-exif", "Parameter_ApplyExif", ProcessParameterEditor.Boolean, 3, AutoValueSource = "EXIF orientation")]
    public ProcessParameter<bool> ApplyExifOrientation { get; } = new(true);

    [ProcessParameter("alpha-mode", "Parameter_AlphaMode", ProcessParameterEditor.Enum, 4, AutoValueSource = "Container alpha")]
    public ProcessParameter<DecoderAlphaMode> AlphaMode { get; } = new(DecoderAlphaMode.PreserveStraight);

    [ProcessParameter("output.transfer", "Parameter_DecoderOutputTransfer", ProcessParameterEditor.Enum, 10, AutoValueSource = "Application source interpretation", IsAdvanced = true)]
    public ProcessParameter<ColorTransferFunction> OutputTransfer { get; } = new(ColorTransferFunction.Srgb);

    [ProcessParameter("output.primaries", "Parameter_DecoderOutputPrimaries", ProcessParameterEditor.Enum, 11, AutoValueSource = "Application source interpretation", IsAdvanced = true)]
    public ProcessParameter<ColorPrimaries> OutputPrimaries { get; } = new(ColorPrimaries.Rec709);

    [ProcessParameter("output.reference-white", "Parameter_DecoderOutputReferenceWhite", ProcessParameterEditor.Number, 12, Unit = "nit", Minimum = 0.01, Maximum = 10000, Step = 1, AutoValueSource = "Application source interpretation", IsAdvanced = true)]
    public ProcessParameter<float> OutputReferenceWhiteNits { get; } = new(100.0f);

    [ProcessParameter("output.nominal-peak", "Parameter_DecoderOutputPeak", ProcessParameterEditor.Number, 13, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 10, AutoValueSource = "Application source interpretation", IsAdvanced = true)]
    public ProcessParameter<float> OutputNominalPeakNits { get; } = new(100.0f);

    public DecoderBackend ActualBackend { get; internal set; } = DecoderBackend.PhotoSauce;
    public string BackendFallbackDetail { get; internal set; } = "";
    public ColorSourceMetadata DecodedOutputColorMetadata => new(
        OutputTransfer.Value,
        OutputPrimaries.Value,
        AlphaMode.Value == DecoderAlphaMode.ForceOpaque
            ? AlphaRepresentation.Opaque
            : AlphaRepresentation.Straight,
        OutputReferenceWhiteNits.Value,
        OutputNominalPeakNits.Value);
    internal bool HasOutputInterpretationOverrides =>
        OutputTransfer.HasUserOverride ||
        OutputPrimaries.HasUserOverride ||
        OutputReferenceWhiteNits.HasUserOverride ||
        OutputNominalPeakNits.HasUserOverride;

    public override string Summary => $"{ActualBackend} / RGBA16F scRGB / Linear";

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        DetectedImageMetadata metadata = context.Source?.DetectedMetadata ?? DetectedImageMetadata.Unknown("");
        DecoderBackend backend = SelectBackend(metadata);
        Set(Backend, backend, "Container metadata + backend capability", MetadataConfidence.High);
        Set(Frame, 0, "First frame", MetadataConfidence.Authoritative);
        Set(ApplyExifOrientation, metadata.ExifOrientation != 1, "EXIF orientation", metadata.ExifOrientation == 1 ? MetadataConfidence.Assumed : MetadataConfidence.High);
        Set(AlphaMode, metadata.HasAlpha ? DecoderAlphaMode.PreserveStraight : DecoderAlphaMode.ForceOpaque, "Container alpha", MetadataConfidence.High);
        ColorSourceMetadata output = Backend.Value == DecoderBackend.StarwardUltraHdr
            ? ColorSourceMetadata.PqRec2020Opaque
            : new(
                metadata.Transfer,
                metadata.Primaries,
                metadata.HasAlpha ? AlphaRepresentation.Straight : AlphaRepresentation.Opaque,
                metadata.ReferenceWhiteNits,
                metadata.NominalPeakNits);
        Set(OutputTransfer, output.Transfer, "Application source interpretation", MetadataConfidence.High);
        Set(OutputPrimaries, output.Primaries, "Application source interpretation", MetadataConfidence.High);
        Set(OutputReferenceWhiteNits, output.ReferenceWhiteNits, "Application source interpretation", MetadataConfidence.High);
        Set(OutputNominalPeakNits, output.NominalPeakNits, "Application source interpretation", MetadataConfidence.High);
        if (State == ProcessState.NotRun)
        {
            ActualBackend = Backend.Value;
            Diagnostic = "等待解码并规范化为固定 RGBA16F scRGB/Linear GPU 图像。";
        }
        NotifySummaryChanged();
    }

    public override async ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Image is not null)
            throw new InvalidOperationException("解码器必须是过程图中唯一的 GPU 图像生产者。");
        var service = (IDecoderExecutionService?)context.Services.GetService(typeof(IDecoderExecutionService))
            ?? throw new InvalidOperationException("解码执行服务不可用。");
        GpuImageFrame image = await service.GetGpuImageAsync(this, cancellationToken);
        context.SetImage(image);
        if (service.IsNormalizationDeferred)
            context.QueueSourceNormalization(new(DecodedOutputColorMetadata));
    }

    private static DecoderBackend SelectBackend(DetectedImageMetadata metadata)
    {
        bool Available(string id) => metadata.BackendCapabilities.Any(item => item.BackendId == id && item.IsAvailable);
        if (metadata.HasUltraHdrGainMap && Available("StarwardUltraHdr")) return DecoderBackend.StarwardUltraHdr;
        return DecoderBackend.PhotoSauce;
    }

    private static void Set<T>(ProcessParameter<T> parameter, T value, string source, MetadataConfidence confidence)
    {
        parameter.SetDetectedValue(value, source, confidence);
        parameter.SetAutomaticValue(value, source);
    }
}
