using BrightEyes.Processing.Contracts;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.ImageSource,
    "Process_ImageSource_Name",
    "Process_ImageSource_Description",
    "Input",
    ProcessDataType.None,
    ProcessDataType.RawImage,
    RibbonTab = "Process",
    RibbonGroup = "Input",
    RibbonOrder = 0,
    IconResourceKey = "ProcessIcon.Image",
    AllowMultiple = false,
    CanDelete = false,
    CanDisable = false,
    ExecutionDomain = ProcessExecutionDomain.Cpu,
    Version = 1)]
public sealed class ImageSourceProcess : ProcessBase
{
    private RawImage? _source;

    public RawImage? Source
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            _source = value;
            NotifySummaryChanged();
        }
    }

    public override string Summary => Source is null
        ? "等待图像源"
        : $"{Source.FileName} / {Source.DetectedMetadata.ContainerFormat} / {Source.DetectedMetadata.PixelWidth} x {Source.DetectedMetadata.PixelHeight}";

    public override void RecalculateAutomaticParameters(ProcessAutoContext context)
    {
        Source ??= context.Source;
        State = Source is null ? ProcessState.Error : ProcessState.Valid;
        Diagnostic = Source is null ? "没有原始图像" : "仅持有压缩数据与不可变来源元数据；未执行像素解码。";
        NotifySummaryChanged();
    }
}
