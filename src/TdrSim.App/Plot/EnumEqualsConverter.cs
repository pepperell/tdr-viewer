using System.Globalization;
using System.Windows.Data;

namespace TdrSim.App.Plot;

/// <summary>
/// Binds a RadioButton's IsChecked to "enum property == ConverterParameter". Unchecking
/// (the other radio taking over) does nothing, so only the checked radio writes back.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string name
            ? Enum.Parse(targetType, name)
            : Binding.DoNothing;
}
