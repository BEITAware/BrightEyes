using BrightEyes.Renderer;
using BrightEyes.Processing.Parameters;
using System.ComponentModel;
using System.Reflection;

namespace BrightEyes.Processing.Contracts;

public enum ProcessExecutionDomain
{
    Cpu,
    Gpu,
    Hybrid,
    Observer
}

/// <summary>
/// Describes where a process belongs in an image pipeline without declaring
/// artificial input/output data types. Image processes all operate on the same
/// opaque GPU image stream; only the source, decoder and terminal have fixed
/// structural positions.
/// </summary>
public enum ProcessRole
{
    Source,
    Decoder,
    Image,
    Observer,
    Terminal
}

public enum ProcessState
{
    NotRun,
    Pending,
    Running,
    Valid,
    Warning,
    Error,
    Disabled,
    Cached,
    Stale
}

public enum AnalysisKind
{
    Histogram = 0,
    Waveform = 1,
    Vectorscope = 2,
    StrictHistogram = 3
}

public enum AnalysisChannel
{
    Luminance = 0,
    Red = 1,
    Green = 2,
    Blue = 3,
    Alpha = 4,
    Rgb = 5
}

public enum AnalysisPresentation
{
    Luminance,
    SingleChannel,
    RgbParade,
    RgbOverlay,
    Hue
}

public enum ProcessParameterEditor
{
    Number,
    Enum,
    Boolean,
    Text,
    ReadOnly,
    Color
}

public enum MetadataConfidence
{
    Unknown,
    Assumed,
    Low,
    Medium,
    High,
    Authoritative
}

public enum PhotometricModel
{
    SceneLinearRelative,
    DisplayLinearRelative,
    DisplayLinearAbsolute
}

public enum ColorRange
{
    Full,
    Limited,
    Unspecified
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ProcessDefinitionAttribute : Attribute
{
    public ProcessDefinitionAttribute(
        string stableId,
        string nameResourceKey,
        string descriptionResourceKey,
        string category)
    {
        StableId = stableId;
        NameResourceKey = nameResourceKey;
        DescriptionResourceKey = descriptionResourceKey;
        Category = category;
    }

    public string StableId { get; }
    public string NameResourceKey { get; }
    public string DescriptionResourceKey { get; }
    public string Category { get; }
    public string RibbonTab { get; set; } = "Process";
    public string RibbonGroup { get; set; } = "Utility";
    public int RibbonOrder { get; set; }
    public string IconResourceKey { get; set; } = "ProcessIcon.Utility";
    public bool AllowMultiple { get; set; }
    public bool CanAdd { get; set; } = true;
    public bool CanDelete { get; set; } = true;
    public bool CanDisable { get; set; } = true;
    public bool CanMove { get; set; } = true;

    public ProcessRole Role { get; set; } = ProcessRole.Image;
    /// <summary>
    /// True when the process is a real sampling/materialization boundary. The
    /// coordinator uses this declaration for texture lifetime and flushes any
    /// queued pointwise operations before executing the process.
    /// </summary>
    public bool RequiresIntermediateTexture { get; set; }
    public ProcessExecutionDomain ExecutionDomain { get; set; } = ProcessExecutionDomain.Gpu;
    public int Version { get; set; } = 1;
    public Type? ParameterPanelType { get; set; }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ProcessParameterAttribute : Attribute
{
    public ProcessParameterAttribute(
        string stableId,
        string nameResourceKey,
        ProcessParameterEditor editor,
        int order)
    {
        StableId = stableId;
        NameResourceKey = nameResourceKey;
        Editor = editor;
        Order = order;
    }

    public string StableId { get; }
    public string NameResourceKey { get; }
    public ProcessParameterEditor Editor { get; }
    public int Order { get; }
    public string Unit { get; set; } = "";
    public double Minimum { get; set; } = double.NaN;
    public double Maximum { get; set; } = double.NaN;
    public double Step { get; set; } = double.NaN;
    public string DefaultValue { get; set; } = "";
    public string AutoValueSource { get; set; } = "";
    public bool IsAdvanced { get; set; }
    public bool AffectsCache { get; set; } = true;
}

public sealed record ProcessDescriptor(Type ProcessType, ProcessDefinitionAttribute Definition)
{
    public IImageProcess Create() =>
        (IImageProcess)(Activator.CreateInstance(ProcessType) ??
        throw new InvalidOperationException($"Could not create process type {ProcessType.FullName}."));
}

public sealed record ProcessAutoContext(
    RawImage? Source,
    DisplayColorMetadata Display);

public sealed class ProcessExecutionContext
{
    public ProcessExecutionContext(
        RawImage source,
        DisplayColorMetadata display,
        IServiceProvider services)
    {
        Source = source;
        Display = display;
        Services = services;
    }

    public RawImage Source { get; }
    public DisplayColorMetadata Display { get; }
    public IServiceProvider Services { get; }
    public GpuImageFrame? Image { get; private set; }
    public DisplayTargetFrame? DisplayTarget { get; private set; }
    public IReadOnlyList<ColorSpaceConversionOperation> PendingColorConversions => _pendingColorConversions;
    internal bool HasPendingSourceNormalization => _pendingSourceNormalization is not null;
    internal bool HasPendingPointwiseOperations =>
        _pendingSourceNormalization is not null || _pendingColorConversions.Count > 0;

    private readonly List<ColorSpaceConversionOperation> _pendingColorConversions = new();
    private SourceNormalizationOperation? _pendingSourceNormalization;

    public void SetImage(GpuImageFrame image)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        DisplayTarget = null;
    }

    public void SetDisplayTarget(DisplayTargetFrame target)
    {
        DisplayTarget = target ?? throw new ArgumentNullException(nameof(target));
        _pendingSourceNormalization = null;
    }

    internal void QueueSourceNormalization(SourceNormalizationOperation operation)
    {
        if (_pendingSourceNormalization is not null)
            throw new InvalidOperationException("源规范化操作只能由固定解码节点声明一次。");
        _pendingSourceNormalization = operation;
    }

    public void QueueColorConversion(ColorSpaceConversionOperation operation)
    {
        if (Image is null)
            throw new InvalidOperationException("色彩空间转换需要已解码的 GPU 图像。");
        if (DisplayTarget is not null)
            throw new InvalidOperationException("显示终端之后不能再变换图像。");
        _pendingColorConversions.Add(operation);
    }

    internal PointwiseImageOperations TakePointwiseOperations()
    {
        var result = new PointwiseImageOperations(
            _pendingSourceNormalization,
            _pendingColorConversions.ToArray());
        _pendingSourceNormalization = null;
        _pendingColorConversions.Clear();
        return result;
    }
}

public sealed record ProcessDiagnostic(
    string Message,
    ProcessState State,
    string? Detail = null);

public interface IImageProcess : INotifyPropertyChanged
{
    Guid InstanceId { get; }
    ProcessDescriptor Descriptor { get; }
    IReadOnlyList<IProcessParameter> Parameters { get; }
    ProcessState State { get; set; }
    string Summary { get; }
    string Diagnostic { get; set; }
    bool IsEnabled { get; set; }
    bool HasUserOverrides { get; }
    void RecalculateAutomaticParameters(ProcessAutoContext context);
    ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken);
    void ResetParameters();
    void RestoreAutomaticParameters();
}

public static class ProcessReflection
{
    public static IEnumerable<(PropertyInfo Property, ProcessParameterAttribute Attribute)> GetParameterProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<ProcessParameterAttribute>(true)))
            .Where(item => item.Attribute is not null)
            .Select(item => (item.Property, item.Attribute!))
            .OrderBy(item => item.Item2.Order);
}
