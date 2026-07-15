using BrightEyes.Processing.Contracts;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace BrightEyes.Processing.Parameters;

public sealed record ProcessParameterState(bool IsAutomatic, object? UserOverrideValue);

public sealed class ProcessParameterChangedEventArgs : EventArgs
{
    public ProcessParameterChangedEventArgs(
        ProcessParameterState before,
        ProcessParameterState after,
        bool isUserInitiated)
    {
        Before = before;
        After = after;
        IsUserInitiated = isUserInitiated;
    }

    public ProcessParameterState Before { get; }
    public ProcessParameterState After { get; }
    public bool IsUserInitiated { get; }
}

public interface IProcessParameter : INotifyPropertyChanged
{
    ProcessParameterAttribute Definition { get; }
    Type ValueType { get; }
    object? DetectedValue { get; }
    object? AutomaticValue { get; }
    object? UserOverrideValue { get; }
    object? EffectiveValue { get; }
    string DetectedSource { get; }
    string AutomaticSource { get; }
    MetadataConfidence Confidence { get; }
    bool IsAutomatic { get; set; }
    bool HasUserOverride { get; }
    event EventHandler<ProcessParameterChangedEventArgs>? Changed;
    void AttachDefinition(ProcessParameterAttribute definition);
    void SetDetectedValue(object? value, string source, MetadataConfidence confidence);
    void SetAutomaticValue(object? value, string source);
    void SetUserValue(object? value);
    void RestoreAutomatic();
    void Reset();
    ProcessParameterState CaptureState();
    void RestoreState(ProcessParameterState state, bool isUserInitiated);
}

public sealed class ProcessParameter<T> : IProcessParameter
{
    private readonly T _defaultValue;
    private T _detectedValue;
    private T _automaticValue;
    private T _userOverrideValue;
    private bool _isAutomatic = true;
    private string _detectedSource = "Default";
    private string _automaticSource = "Default";
    private MetadataConfidence _confidence = MetadataConfidence.Assumed;
    private ProcessParameterAttribute? _definition;

    public ProcessParameter(T defaultValue)
    {
        _defaultValue = defaultValue;
        _detectedValue = defaultValue;
        _automaticValue = defaultValue;
        _userOverrideValue = defaultValue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ProcessParameterChangedEventArgs>? Changed;

    public ProcessParameterAttribute Definition => _definition ??
        throw new InvalidOperationException("The parameter has not been attached to a process declaration.");

    public Type ValueType => typeof(T);
    public object? DetectedValue => _detectedValue;
    public object? AutomaticValue => _automaticValue;
    public object? UserOverrideValue => _userOverrideValue;
    public object? EffectiveValue => Value;
    public string DetectedSource => _detectedSource;
    public string AutomaticSource => _automaticSource;
    public MetadataConfidence Confidence => _confidence;
    public bool HasUserOverride => !_isAutomatic;
    public T Value => _isAutomatic ? _automaticValue : _userOverrideValue;

    public bool IsAutomatic
    {
        get => _isAutomatic;
        set
        {
            if (_isAutomatic == value) return;
            ProcessParameterState before = CaptureState();
            if (!value) _userOverrideValue = _automaticValue;
            _isAutomatic = value;
            RaiseAll();
            Changed?.Invoke(this, new(before, CaptureState(), true));
        }
    }

    public void AttachDefinition(ProcessParameterAttribute definition) =>
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));

    public void SetDetectedValue(object? value, string source, MetadataConfidence confidence)
    {
        _detectedValue = ConvertValue(value);
        _detectedSource = source;
        _confidence = confidence;
        OnPropertyChanged(nameof(DetectedValue));
        OnPropertyChanged(nameof(DetectedSource));
        OnPropertyChanged(nameof(Confidence));
    }

    public void SetAutomaticValue(object? value, string source)
    {
        T converted = ConvertValue(value);
        if (EqualityComparer<T>.Default.Equals(_automaticValue, converted) && _automaticSource == source)
            return;
        _automaticValue = converted;
        _automaticSource = source;
        OnPropertyChanged(nameof(AutomaticValue));
        OnPropertyChanged(nameof(AutomaticSource));
        if (_isAutomatic)
        {
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(EffectiveValue));
        }
    }

    public void SetUserValue(object? value)
    {
        T converted = ConvertValue(value);
        ProcessParameterState before = CaptureState();
        if (!_isAutomatic && EqualityComparer<T>.Default.Equals(_userOverrideValue, converted))
            return;
        _userOverrideValue = converted;
        _isAutomatic = false;
        RaiseAll();
        Changed?.Invoke(this, new(before, CaptureState(), true));
    }

    public void RestoreAutomatic()
    {
        if (_isAutomatic) return;
        ProcessParameterState before = CaptureState();
        _isAutomatic = true;
        RaiseAll();
        Changed?.Invoke(this, new(before, CaptureState(), true));
    }

    public void Reset()
    {
        ProcessParameterState before = CaptureState();
        _userOverrideValue = _defaultValue;
        _isAutomatic = true;
        RaiseAll();
        Changed?.Invoke(this, new(before, CaptureState(), true));
    }

    public ProcessParameterState CaptureState() => new(_isAutomatic, _userOverrideValue);

    public void RestoreState(ProcessParameterState state, bool isUserInitiated)
    {
        ProcessParameterState before = CaptureState();
        _userOverrideValue = ConvertValue(state.UserOverrideValue);
        _isAutomatic = state.IsAutomatic;
        RaiseAll();
        Changed?.Invoke(this, new(before, CaptureState(), isUserInitiated));
    }

    private T ConvertValue(object? value)
    {
        if (value is T typed) return typed;
        if (value is null) return _defaultValue;
        Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsEnum)
        {
            if (value is string text) return (T)Enum.Parse(target, text, true);
            return (T)Enum.ToObject(target, value);
        }
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsAutomatic));
        OnPropertyChanged(nameof(HasUserOverride));
        OnPropertyChanged(nameof(UserOverrideValue));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(EffectiveValue));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
