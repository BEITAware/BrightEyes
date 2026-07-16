using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Data;

namespace BrightEyes.Converters;

public sealed class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        Type type = value.GetType();
        if (!type.IsEnum) return value.ToString() ?? string.Empty;

        string name = Enum.GetName(type, value) ?? value.ToString() ?? string.Empty;
        return type.GetField(name, BindingFlags.Public | BindingFlags.Static)?
                   .GetCustomAttribute<DescriptionAttribute>()?.Description
               ?? name;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
