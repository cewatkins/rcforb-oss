using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace RCForb.Gui;

public sealed class CountToVisibilityConverter : IValueConverter
{
    public static CountToVisibilityConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        return count > 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
