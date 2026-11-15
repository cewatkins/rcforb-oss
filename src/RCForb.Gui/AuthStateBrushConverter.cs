using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RCForb.Gui;

/// <summary>
/// Maps an auth-state label to a pill background colour so authorised/refused/
/// authenticating/pending are visually distinct (SPEC-SESS-004/011).
/// </summary>
public sealed class AuthStateBrushConverter : IValueConverter
{
    public static AuthStateBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "Authorised" => new SolidColorBrush(Color.Parse("#2f8f4f")),
            "Refused" => new SolidColorBrush(Color.Parse("#c92a2a")),
            "Authenticating…" => new SolidColorBrush(Color.Parse("#b45309")),
            _ => new SolidColorBrush(Color.Parse("#5c636b")),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}