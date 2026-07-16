using BrightEyes.Renderer;
using BrightEyes.Processing.Parameters;
using System.ComponentModel;
using System.Reflection;

namespace BrightEyes.Processing.Contracts;

public enum ProcessDataType
{
    None,
    RawImage,
    BrightEyesIntermediateImage,
    AnalysisData,
    DisplayTarget
}

public enum ProcessExecutionDomain
{
    Cpu,
    Gpu,
    Hybrid,
    Observer
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

public enum ConnectionSeverity
{
    None,
    Compatible,
    SemanticWarning,
    HardError
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ProcessDefinitionAttribute : Attribute
{
    public ProcessDefinitionAttribute(
        string stableId,
        string nameResourceKey,
        string descriptionResourceKey,
        string category,
        ProcessDataType inputType,
        ProcessDataType outputType)
    {
        StableId = stableId;
        NameResourceKey = nameResourceKey;
        DescriptionResourceKey = descriptionResourceKey;
        Category = category;
        InputType = inputType;
        OutputType = outputType;
    }

    public string StableId { get; }
    public string NameResourceKey { get; }
    public string DescriptionResourceKey { get; }
    public string Category { get; }
    public ProcessDataType InputType { get; }
    public ProcessDataType OutputType { get; }
    public string RibbonTab { get; set; } = "Process";
    public string RibbonGroup { get; set; } = "Utility";
    public int RibbonOrder { get; set; }
    public string IconResourceKey { get; set; } = "ProcessIcon.Utility";
    public bool AllowMultiple { get; set; }
    public bool CanAdd { get; set; } = true;
    public bool CanDelete { get; set; } = true;
    public bool CanDisable { get; set; } = true;
    public bool CanMove { get; set; } = true;

    /// <summary>
    /// Declares a transparent observer of the current GPU image. The process
    /// may inspect the image but must preserve the pipeline value by reference.
    /// </summary>
    public bool ImageReadOnly { get; set; }
    public ProcessExecutionDomain ExecutionDomain { get; set; } = ProcessExecutionDomain.Gpu;
    public int Version { get; set; } = 1;
    public Type? ParameterPanelType { get; set; }
}

public static class ProcessConnectionPolicy
{
    public static bool CanAccept(ProcessDataType upstreamType, ProcessDefinitionAttribute downstream)
    {
        ArgumentNullException.ThrowIfNull(downstream);
        return upstreamType == downstream.InputType ||
               downstream.ImageReadOnly && IsGpuImageType(upstreamType) && IsGpuImageType(downstream.InputType);
    }

    public static ProcessDataType GetEffectiveOutputType(
        ProcessDataType upstreamType,
        ProcessDefinitionAttribute process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return process.ImageReadOnly && IsGpuImageType(upstreamType)
            ? upstreamType
            : process.OutputType;
    }

    public static bool IsGpuImageType(ProcessDataType type) =>
        type is ProcessDataType.BrightEyesIntermediateImage or ProcessDataType.DisplayTarget;
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

public sealed record ImageSemantics(
    ColorPrimaries Primaries,
    ColorTransferFunction Transfer,
    PhotometricModel PhotometricModel,
    float ReferenceWhiteNits,
    float NominalPeakNits,
    ColorRange Range,
    AlphaRepresentation Alpha)
{
    public static ImageSemantics DefaultRelative => new(
        ColorPrimaries.Rec709,
        ColorTransferFunction.Linear,
        PhotometricModel.DisplayLinearRelative,
        100.0f,
        100.0f,
        ColorRange.Full,
        AlphaRepresentation.Straight);
}

public sealed record ProcessAutoContext(
    RawImage? Source,
    DisplayColorMetadata Display,
    ImageSemantics? UpstreamSemantics);

public sealed class ProcessExecutionContext
{
    public ProcessExecutionContext(
        RawImage source,
        DisplayColorMetadata display,
        object? value,
        IServiceProvider services)
    {
        Source = source;
        Display = display;
        Value = value;
        Services = services;
    }

    public RawImage Source { get; }
    public DisplayColorMetadata Display { get; }
    public object? Value { get; set; }
    public IServiceProvider Services { get; }
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
    ConnectionSeverity IncomingConnectionSeverity { get; set; }
    string IncomingConnectionLabel { get; set; }
    string IncomingConnectionMessage { get; set; }
    ImageSemantics? OutputSemantics { get; }
    void RecalculateAutomaticParameters(ProcessAutoContext context);
    IReadOnlyList<string> ValidateSemanticInput(ImageSemantics input);
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
