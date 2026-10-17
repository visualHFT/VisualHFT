using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VisualHFT.Desktop.Hosting;
using VisualHFT.PluginManager;
using HostPlugins = VisualHFT.PluginManager.PluginManager;
using VisualHFT.Desktop.ViewModels;
using VisualHFT.Desktop.Views;

namespace VisualHFT.Desktop;

public partial class App : Application
{
    private TerminalViewModel? _terminal;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _terminal = new TerminalViewModel();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = _terminal };
            desktop.ShutdownRequested += (_, _) =>
            {
                _terminal.Dispose();
                HostPlugins.UnloadPlugins();
            };
            desktop.MainWindow.Opened += async (_, _) =>
            {
                try
                {
                    await _terminal.StartAsync();
                }
                catch (Exception ex)
                {
                    _terminal.StatusText = "Startup failed: " + ex.Message;
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
