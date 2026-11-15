using Avalonia;
using Avalonia.Styling;

namespace RCForb.Gui;

public static class ThemeService
{
    public static void Apply(string? theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant =
            string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
    }

    public static string CurrentTheme =>
        Application.Current?.RequestedThemeVariant == ThemeVariant.Light ? "Light" : "Dark";
}
