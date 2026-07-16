using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace BrightEyes.Processing;

public abstract class ProcessBase : IImageProcess
{
    private IReadOnlyList<IProcessParameter>? _parameters;
    private ProcessState _state = ProcessState.NotRun;
    private string _diagnostic = "尚未执行";
    private bool _isEnabled = true;
    private ConnectionSeverity _incomingConnectionSeverity;
    private string _incomingConnectionLabel = "";
    private string _incomingConnectionMessage = "";

    protected ProcessBase()
    {
        ProcessDefinitionAttribute definition = GetType().GetCustomAttribute<ProcessDefinitionAttribute>() ??
            throw new InvalidOperationException($"Process {GetType().FullName} has no ProcessDefinitionAttribute.");
        Descriptor = new ProcessDescriptor(GetType(), definition);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ProcessParameterChangedEventArgs>? ParameterChanged;

    public Guid InstanceId { get; } = Guid.NewGuid();
    public ProcessDescriptor Descriptor { get; }
    public IReadOnlyList<IProcessParameter> Parameters => _parameters ??= DiscoverParameters();
    public abstract string Summary { get; }
    public virtual ImageSemantics? OutputSemantics => null;

    public ProcessState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public string Diagnostic
    {
        get => _diagnostic;
        set => SetField(ref _diagnostic, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetField(ref _isEnabled, value))
            {
                State = value ? ProcessState.Stale : ProcessState.Disabled;
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public bool HasUserOverrides => Parameters.Any(parameter => parameter.HasUserOverride);

    public ConnectionSeverity IncomingConnectionSeverity
    {
        get => _incomingConnectionSeverity;
        set => SetField(ref _incomingConnectionSeverity, value);
    }

    public string IncomingConnectionLabel
    {
        get => _incomingConnectionLabel;
        set => SetField(ref _incomingConnectionLabel, value);
    }

    public string IncomingConnectionMessage
    {
        get => _incomingConnectionMessage;
        set => SetField(ref _incomingConnectionMessage, value);
    }

    public abstract void RecalculateAutomaticParameters(ProcessAutoContext context);

    public virtual IReadOnlyList<string> ValidateSemanticInput(ImageSemantics input) => Array.Empty<string>();

    public abstract ValueTask ExecuteAsync(ProcessExecutionContext context, CancellationToken cancellationToken);

    public void ResetParameters()
    {
        foreach (IProcessParameter parameter in Parameters)
            parameter.Reset();
    }

    public void RestoreAutomaticParameters()
    {
        foreach (IProcessParameter parameter in Parameters)
            parameter.RestoreAutomatic();
    }

    protected void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasUserOverrides));
        OnPropertyChanged(nameof(OutputSemantics));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    private IReadOnlyList<IProcessParameter> DiscoverParameters()
    {
        var result = new List<IProcessParameter>();
        foreach ((PropertyInfo property, ProcessParameterAttribute attribute) in ProcessReflection.GetParameterProperties(GetType()))
        {
            if (!typeof(IProcessParameter).IsAssignableFrom(property.PropertyType))
                throw new InvalidOperationException($"Parameter property {GetType().Name}.{property.Name} must implement IProcessParameter.");
            if (property.GetValue(this) is not IProcessParameter parameter)
                throw new InvalidOperationException($"Parameter property {GetType().Name}.{property.Name} is null.");
            parameter.AttachDefinition(attribute);
            parameter.Changed += OnParameterChanged;
            result.Add(parameter);
        }
        return result;
    }

    private void OnParameterChanged(object? sender, ProcessParameterChangedEventArgs e)
    {
        NotifySummaryChanged();
        ParameterChanged?.Invoke(sender, e);
    }
}
