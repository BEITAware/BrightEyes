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
    ProcessDataType.RawImage,
    ProcessDataType.BrightEyesIntermediateImage,
    RibbonTab = "Process",
    RibbonGroup = "Input",
    RibbonOrder = 10,
    IconResourceKey = "ProcessIcon.Decoder",
    AllowMultiple = false,
    CanDelete = true,
    CanDisable = false,
    ExecutionDomain = ProcessExecutionDomain.Hybrid,
    Version = 3)]
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

    [ProcessParameter("input.primaries", "Parameter_InputPrimaries", ProcessParameterEditor.Enum, 20, AutoValueSource = "Immutable source metadata", IsAdvanced = true)]
    public ProcessParameter<ColorPrimaries> InputPrimaries { get; } = new(ColorPrimaries.Rec709);

    [ProcessParameter("input.transfer", "Parameter_InputTransfer", ProcessParameterEditor.Enum, 21, AutoValueSource = "Immutable source metadata", IsAdvanced = true)]
    public ProcessParameter<ColorTransferFunction> InputTransfer { get; } = new(ColorTransferFunction.Srgb);

    [ProcessParameter("input.photometric", "Parameter_InputPhotometric", ProcessParameterEditor.Enum, 22, AutoValueSource = "Immutable source metadata", IsAdvanced = true)]
    public ProcessParameter<PhotometricModel> InputPhotometric { get; } = new(PhotometricModel.DisplayLinearRelative);

    [ProcessParameter("input.reference-white", "Parameter_InputReferenceWhite", ProcessParameterEditor.Number, 23, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 1, AutoValueSource = "Immutable source metadata", IsAdvanced = true)]
    public ProcessParameter<float> InputReferenceWhite { get; } = new(100.0f);

    [ProcessParameter("input.peak", "Parameter_InputPeak", ProcessParameterEditor.Number, 24, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 10, AutoValueSource = "Immutable source metadata", IsAdvanced = true)]
    public ProcessParameter<float> InputPeak { get; } = new(100.0f);

    [ProcessParameter("input.range", "Parameter_InputRange", ProcessParameterEditor.Enum, 25, AutoValueSource = "Immutable source metadata", IsAdvanced = true)]
    public ProcessParameter<ColorRange> InputRange { get; } = new(ColorRange.Full);

    [ProcessParameter("input.alpha", "Parameter_InputAlpha", ProcessParameterEditor.Enum, 26, AutoValueSource = "Immutable source metadata", IsAdvanced = true)]
    public ProcessParameter<AlphaRepresentation> InputAlpha { get; } = new(AlphaRepresentation.Straight);

    [ProcessParameter("output.primaries", "Parameter_OutputPrimaries", ProcessParameterEditor.Enum, 100, AutoValueSource = "BrightEyes intermediate contract", IsAdvanced = true)]
    public ProcessParameter<ColorPrimaries> OutputPrimaries { get; } = new(ColorPrimaries.Rec709);

    [ProcessParameter("output.transfer", "Parameter_OutputTransfer", ProcessParameterEditor.Enum, 101, AutoValueSource = "BrightEyes intermediate contract", IsAdvanced = true)]
    public ProcessParameter<ColorTransferFunction> OutputTransfer { get; } = new(ColorTransferFunction.Linear);

    [ProcessParameter("output.photometric", "Parameter_OutputPhotometric", ProcessParameterEditor.Enum, 102, AutoValueSource = "Detected source transfer", IsAdvanced = true)]
    public ProcessParameter<PhotometricModel> OutputPhotometric { get; } = new(PhotometricModel.DisplayLinearRelative);

    [ProcessParameter("output.reference-white", "Parameter_OutputReferenceWhite", ProcessParameterEditor.Number, 103, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 1, AutoValueSource = "Detected source metadata", IsAdvanced = true)]
    public ProcessParameter<float> OutputReferenceWhite { get; } = new(100.0f);

    [ProcessParameter("output.peak", "Parameter_OutputPeak", ProcessParameterEditor.Number, 104, Unit = "nit", Minimum = 1, Maximum = 10000, Step = 10, AutoValueSource = "Detected source metadata", IsAdvanced = true)]
    public ProcessParameter<float> OutputPeak { get; } = new(100.0f);

    [ProcessParameter("output.range", "Parameter_OutputRange", ProcessParameterEditor.Enum, 105, AutoValueSource = "BrightEyes intermediate contract", IsAdvanced = true)]
    public ProcessParameter<ColorRange> OutputRange { get; } = new(ColorRange.Full);

    [ProcessParameter("output.alpha", "Parameter_OutputAlpha", ProcessParameterEditor.Enum, 106, AutoValueSource = "Decoder normalization", IsAdvanced = true)]
    public ProcessParameter<AlphaRepresentation> OutputAlpha { get; } = new(AlphaRepresentation.Straight);

    public DecoderBackend ActualBackend { get; internal set; } = DecoderBackend.PhotoSauce;
    public string BackendFallbackDetail { get; internal set; } = "";

    public override string Summary => $"{ActualBackend} / GPU source / deferred linear extended-sRGB";

    public ColorSourceMetadata EffectiveInputColorMetadata => new(
        InputTransfer.Value,
        InputPrimaries.Value,
        AlphaMode.Value == DecoderAlphaMode.ForceOpaque ? AlphaRepresentation.Opaque : AlphaRepresentation.Straight,
        InputReferenceWhite.Value,
        InputPeak.Value);

    public override ImageSemantics OutputSemantics => new(
        OutputPrimaries.Value,
        OutputTransfer.Value,
        OutputPhotometric.Value,
        OutputReferenceWhite.Value,
        OutputPeak.Value,
        OutputRange.Value,
        OutputAlpha.Value);

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        DetectedImageMetadata metadata = context.Source?.DetectedMetadata ?? DetectedImageMetadata.Unknown("");
        DecoderBackend backend = SelectBackend(metadata);
        Set(Backend, backend, "Container metadata + backend capability", MetadataConfidence.High);
        Set(Frame, 0, "First frame", MetadataConfidence.Authoritative);
        Set(ApplyExifOrientation, metadata.ExifOrientation != 1, "EXIF orientation", metadata.ExifOrientation == 1 ? MetadataConfidence.Assumed : MetadataConfidence.High);
        Set(AlphaMode, metadata.HasAlpha ? DecoderAlphaMode.PreserveStraight : DecoderAlphaMode.ForceOpaque, "Container alpha", MetadataConfidence.High);
        Set(InputPrimaries, metadata.Primaries, metadata.HasCicp ? "CICP/nclx" : metadata.HasIccProfile ? "ICC/PhotoSauce" : "Format default", metadata.HasCicp ? MetadataConfidence.Authoritative : metadata.HasIccProfile ? MetadataConfidence.High : MetadataConfidence.Assumed);
        Set(InputTransfer, metadata.Transfer, metadata.HasCicp ? "CICP/nclx" : metadata.HasIccProfile ? "ICC/PhotoSauce" : "Format default", metadata.HasCicp ? MetadataConfidence.Authoritative : metadata.HasIccProfile ? MetadataConfidence.High : MetadataConfidence.Assumed);
        Set(InputPhotometric, metadata.PhotometricModel, metadata.HasCicp ? "CICP/nclx" : "Format default", metadata.HasCicp ? MetadataConfidence.Authoritative : MetadataConfidence.Assumed);
        Set(InputReferenceWhite, metadata.ReferenceWhiteNits, metadata.HasCicp ? "CICP/nclx" : "Format default", metadata.HasCicp ? MetadataConfidence.Authoritative : MetadataConfidence.Assumed);
        Set(InputPeak, metadata.NominalPeakNits, metadata.HasCicp || metadata.HasUltraHdrGainMap ? "Container metadata" : "Format default", metadata.HasCicp ? MetadataConfidence.Authoritative : MetadataConfidence.Assumed);
        Set(InputRange, metadata.Range, metadata.HasCicp ? "CICP/nclx" : "Format default", metadata.HasCicp ? MetadataConfidence.Authoritative : MetadataConfidence.Assumed);
        Set(InputAlpha, metadata.HasAlpha ? AlphaRepresentation.Straight : AlphaRepresentation.Opaque, "Container alpha", MetadataConfidence.High);
        Set(OutputPrimaries, ColorPrimaries.Rec709, "BrightEyes intermediate contract", MetadataConfidence.Authoritative);
        Set(OutputTransfer, ColorTransferFunction.Linear, "BrightEyes intermediate contract", MetadataConfidence.Authoritative);
        Set(OutputPhotometric, InputPhotometric.Value, "Effective decoder input interpretation", InputPhotometric.IsAutomatic ? MetadataConfidence.High : MetadataConfidence.Authoritative);
        Set(OutputReferenceWhite, InputReferenceWhite.Value, "Effective decoder input interpretation", InputReferenceWhite.IsAutomatic ? MetadataConfidence.High : MetadataConfidence.Authoritative);
        Set(OutputPeak, InputPeak.Value, "Effective decoder input interpretation", InputPeak.IsAutomatic ? MetadataConfidence.High : MetadataConfidence.Authoritative);
        Set(OutputRange, ColorRange.Full, "Decoded float contract", MetadataConfidence.Authoritative);
        Set(OutputAlpha, AlphaMode.Value == DecoderAlphaMode.ForceOpaque || InputAlpha.Value == AlphaRepresentation.Opaque
            ? AlphaRepresentation.Opaque
            : AlphaRepresentation.Straight, "Decoder normalization", MetadataConfidence.High);
        if (State == ProcessState.NotRun)
        {
            ActualBackend = Backend.Value;
            Diagnostic = "等待执行压缩解码；像素规范化可与第一个 GPU 消费过程融合。";
        }
        NotifySummaryChanged();
    }

    public override async ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Value is not RawImage)
            throw new InvalidOperationException("解码器需要图像文件提供的原始二进制输入。");
        var service = (IDecoderExecutionService?)context.Services.GetService(typeof(IDecoderExecutionService))
            ?? throw new InvalidOperationException("解码执行服务不可用。");
        context.Value = await service.GetGpuImageAsync(this, cancellationToken);
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
