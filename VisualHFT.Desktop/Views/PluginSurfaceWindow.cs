using Avalonia.Controls;
using Avalonia.Media;
using VisualHFT.Desktop.ViewModels;

namespace VisualHFT.Desktop.Views;

public sealed class PluginSurfaceWindow : Window
{
    public PluginSurfaceWindow(PluginRow row)
    {
        Title = row.Name;
        Width = 520;
        Height = 280;
        Background = new SolidColorBrush(Color.Parse("#0E1116"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = row.Name,
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = row.CustomUi,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Windows hosts this as a live WPF UserControl on the dashboard. Avalonia cannot load that control, so Mac shows this description instead of embedding the XAML panel.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0)
                }
            }
        };
    }
}
