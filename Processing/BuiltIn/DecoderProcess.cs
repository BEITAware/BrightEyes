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
    Version = 4)]
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

    public DecoderBackend ActualBackend { get; internal set; } = DecoderBackend.PhotoSauce;
    public string BackendFallbackDetail { get; internal set; } = "";
    public ColorSourceMetadata EffectiveSourceColorMetadata { get; private set; } = ColorSourceMetadata.Sdr;

    public override string Summary => $"{ActualBackend} / RGBA16F scRGB / Linear";

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        DetectedImageMetadata metadata = context.Source?.DetectedMetadata ?? DetectedImageMetadata.Unknown("");
        DecoderBackend backend = SelectBackend(metadata);
        Set(Backend, backend, "Container metadata + backend capability", MetadataConfidence.High);
        Set(Frame, 0, "First frame", MetadataConfidence.Authoritative);
        Set(ApplyExifOrientation, metadata.ExifOrientation != 1, "EXIF orientation", metadata.ExifOrientation == 1 ? MetadataConfidence.Assumed : MetadataConfidence.High);
        Set(AlphaMode, metadata.HasAlpha ? DecoderAlphaMode.PreserveStraight : DecoderAlphaMode.ForceOpaque, "Container alpha", MetadataConfidence.High);
        EffectiveSourceColorMetadata = new(
            metadata.Transfer,
            metadata.Primaries,
            AlphaMode.Value == DecoderAlphaMode.ForceOpaque || !metadata.HasAlpha
                ? AlphaRepresentation.Opaque
                : AlphaRepresentation.Straight,
            metadata.ReferenceWhiteNits,
            metadata.NominalPeakNits);
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
        context.SetImage(await service.GetGpuImageAsync(this, cancellationToken));
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
