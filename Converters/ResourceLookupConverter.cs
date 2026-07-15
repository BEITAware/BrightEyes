using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BrightEyes.Converters;

public sealed class ResourceLookupConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? System.Windows.Application.Current?.TryFindResource(key) ?? key : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
