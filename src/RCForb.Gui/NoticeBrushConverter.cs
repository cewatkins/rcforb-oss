using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RCForb.Gui;

public sealed class NoticeBrushConverter : IValueConverter
{
    public static NoticeBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value as string ?? "";
        return kind.ToLowerInvariant() switch
        {
            "error" => new SolidColorBrush(Colors.Salmon),
            "warning" => new SolidColorBrush(Colors.Khaki),
            "auth" => new SolidColorBrush(Colors.Plum),
            "sync" => new SolidColorBrush(Colors.LightSkyBlue),
            "restriction" => new SolidColorBrush(Colors.DarkOrange),
            "info" => new SolidColorBrush(Colors.LightBlue),
            _ => new SolidColorBrush(Colors.LightGray),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
