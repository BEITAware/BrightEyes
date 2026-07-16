using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace BrightEyes.ViewModels;

public sealed class ProcessParameterViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IProcessParameter _parameter;
    private string? _validationError;

    public ProcessParameterViewModel(IProcessParameter parameter)
    {
        _parameter = parameter;
        _parameter.PropertyChanged += OnParameterPropertyChanged;
        Name = ResourceText.Resolve(parameter.Definition.NameResourceKey);
        EnumValues = parameter.ValueType.IsEnum
            ? Enum.GetValues(parameter.ValueType).Cast<object>().ToArray()
            : Array.Empty<object>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string Unit => _parameter.Definition.Unit;
    public ProcessParameterEditor Editor => _parameter.Definition.Editor;
    public bool IsAdvanced => _parameter.Definition.IsAdvanced;
    public IReadOnlyList<object> EnumValues { get; }
    public double Minimum => double.IsNaN(_parameter.Definition.Minimum) ? 0.0 : _parameter.Definition.Minimum;
    public double Maximum => double.IsNaN(_parameter.Definition.Maximum) ? 1.0 : _parameter.Definition.Maximum;
    public double Step => double.IsNaN(_parameter.Definition.Step) ? 0.01 : _parameter.Definition.Step;
    public string DetectedText => Format(_parameter.DetectedValue);
    public string AutomaticText => Format(_parameter.AutomaticValue);
    public string EffectiveText => Format(_parameter.EffectiveValue);
    public string DetectedSource => _parameter.DetectedSource;
    public string AutomaticSource => _parameter.AutomaticSource;
    public string Confidence => _parameter.Confidence.ToString();
    public bool HasValidationError => !string.IsNullOrWhiteSpace(_validationError);
    public string? ValidationError => _validationError;

    public bool IsAutomatic
    {
        get => _parameter.IsAutomatic;
        set => _parameter.IsAutomatic = value;
    }

    public object? SelectedEnumValue
    {
        get => _parameter.EffectiveValue;
        set
        {
            if (value is not null) _parameter.SetUserValue(value);
        }
    }

    public bool BooleanValue
    {
        get => _parameter.EffectiveValue is true;
        set => _parameter.SetUserValue(value);
    }

    public string ValueText
    {
        get => Format(_parameter.EffectiveValue, includeUnit: false);
        set
        {
            try
            {
                object parsed = ParseNumber(value);
                double numeric = Convert.ToDouble(parsed, CultureInfo.InvariantCulture);
                if (!double.IsNaN(_parameter.Definition.Minimum) && numeric < _parameter.Definition.Minimum)
                    throw new ArgumentOutOfRangeException(nameof(value), $"最小值为 {_parameter.Definition.Minimum}。");
                if (!double.IsNaN(_parameter.Definition.Maximum) && numeric > _parameter.Definition.Maximum)
                    throw new ArgumentOutOfRangeException(nameof(value), $"最大值为 {_parameter.Definition.Maximum}。");
                _validationError = null;
                _parameter.SetUserValue(parsed);
            }
            catch (Exception ex)
            {
                _validationError = ex.GetBaseException().Message;
                OnPropertyChanged(nameof(ValidationError));
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public double NumberValue
    {
        get => _parameter.EffectiveValue is null ? 0.0 : Convert.ToDouble(_parameter.EffectiveValue, CultureInfo.InvariantCulture);
        set
        {
            Type type = Nullable.GetUnderlyingType(_parameter.ValueType) ?? _parameter.ValueType;
            object converted = type == typeof(int) ? (object)(int)Math.Round(value) :
                type == typeof(float) ? (float)value : value;
            _parameter.SetUserValue(converted);
        }
    }

    public void RestoreAutomatic() => _parameter.RestoreAutomatic();

    private object ParseNumber(string value)
    {
        Type type = Nullable.GetUnderlyingType(_parameter.ValueType) ?? _parameter.ValueType;
        if (type == typeof(int))
            return int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture);
        if (type == typeof(float))
            return float.Parse(value, NumberStyles.Float, CultureInfo.CurrentCulture);
        if (type == typeof(double))
            return double.Parse(value, NumberStyles.Float, CultureInfo.CurrentCulture);
        if (type == typeof(decimal))
            return decimal.Parse(value, NumberStyles.Float, CultureInfo.CurrentCulture);
        return Convert.ChangeType(value, type, CultureInfo.CurrentCulture)!;
    }

    private string Format(object? value, bool includeUnit = true)
    {
        string text = value switch
        {
            null => "未声明",
            float number => number.ToString("0.###", CultureInfo.CurrentCulture),
            double number => number.ToString("0.###", CultureInfo.CurrentCulture),
            _ => value.ToString() ?? ""
        };
        return includeUnit && !string.IsNullOrWhiteSpace(Unit) ? $"{text} {Unit}" : text;
    }

    private void OnParameterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IProcessParameter.IsAutomatic):
                OnPropertyChanged(nameof(IsAutomatic));
                break;
            case nameof(IProcessParameter.EffectiveValue):
                OnPropertyChanged(nameof(SelectedEnumValue));
                OnPropertyChanged(nameof(BooleanValue));
                OnPropertyChanged(nameof(ValueText));
                OnPropertyChanged(nameof(NumberValue));
                OnPropertyChanged(nameof(EffectiveText));
                break;
            case nameof(IProcessParameter.DetectedValue):
                OnPropertyChanged(nameof(DetectedText));
                break;
            case nameof(IProcessParameter.AutomaticValue):
                OnPropertyChanged(nameof(AutomaticText));
                break;
            case nameof(IProcessParameter.DetectedSource):
                OnPropertyChanged(nameof(DetectedSource));
                break;
            case nameof(IProcessParameter.AutomaticSource):
                OnPropertyChanged(nameof(AutomaticSource));
                break;
            case nameof(IProcessParameter.Confidence):
                OnPropertyChanged(nameof(Confidence));
                break;
            case null:
            case "":
                RaiseAllPropertiesChanged();
                break;
        }
    }

    private void RaiseAllPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsAutomatic));
        OnPropertyChanged(nameof(SelectedEnumValue));
        OnPropertyChanged(nameof(BooleanValue));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(NumberValue));
        OnPropertyChanged(nameof(DetectedText));
        OnPropertyChanged(nameof(AutomaticText));
        OnPropertyChanged(nameof(EffectiveText));
        OnPropertyChanged(nameof(DetectedSource));
        OnPropertyChanged(nameof(AutomaticSource));
        OnPropertyChanged(nameof(Confidence));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    public void Dispose() => _parameter.PropertyChanged -= OnParameterPropertyChanged;
}
