using BrightEyes.Processing.Contracts;

namespace BrightEyes.Processing.BuiltIn;

[ProcessDefinition(
    BuiltInProcessIds.ImageSource,
    "Process_ImageSource_Name",
    "Process_ImageSource_Description",
    "Input",
    RibbonTab = "Process",
    RibbonGroup = "Input",
    RibbonOrder = 0,
    IconResourceKey = "ProcessIcon.Image",
    AllowMultiple = false,
    CanAdd = false,
    CanDelete = false,
    CanDisable = false,
    CanMove = false,
    Role = ProcessRole.Source,
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

    public override ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Source is null)
            throw new InvalidOperationException("没有可用的图像源。");
        Diagnostic = "应用程序已读取压缩文件及不可变来源元数据；这些信息不会沿像素过程传递。";
        return ValueTask.CompletedTask;
    }
}
