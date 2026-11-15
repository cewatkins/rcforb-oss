using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace RCForb.Gui;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var settings = new SettingsService().Load();
        ThemeService.Apply(settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var viewModel = new MainWindowViewModel();
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            viewModel.HandleStartupArgs(args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
