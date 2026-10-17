using Avalonia.Controls;
using Avalonia.Media;

namespace VisualHFT.Desktop.Views;

/// <summary>
/// Undocks an existing book, price, spread, or imbalance control into its own window.
/// The control must already be detached from the tree.
/// </summary>
public sealed class ChartPopoutWindow : Window
{
    public ChartPopoutWindow(string title, Control content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Parent != null)
        {
            throw new InvalidOperationException(
                "Cannot host '" + content.GetType().Name + "' in a pop-out window while it still has a parent. Detach it first.");
        }

        Title = string.IsNullOrWhiteSpace(title) ? "VisualHFT" : title;
        Width = 800;
        Height = 600;
        Background = new SolidColorBrush(Color.Parse("#0E1116"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = content;
    }
}
