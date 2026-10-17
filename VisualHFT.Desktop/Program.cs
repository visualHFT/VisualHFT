using Avalonia;
using VisualHFT.Desktop.Hosting;

namespace VisualHFT.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--validate-screens", StringComparison.OrdinalIgnoreCase)))
            return ScreenValidation.Run();

        if (args.Any(a => string.Equals(a, "--self-check", StringComparison.OrdinalIgnoreCase)))
            return SelfCheck.Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
