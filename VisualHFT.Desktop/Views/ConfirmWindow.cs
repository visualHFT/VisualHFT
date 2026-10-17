using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VisualHFT.Desktop.Views;

public sealed class ConfirmWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();

    private ConfirmWindow(string message)
    {
        Title = "VisualHFT";
        Width = 420;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0E1116"));
        var yes = new Button { Content = "Yes", MinWidth = 80 };
        var no = new Button { Content = "No", MinWidth = 80 };
        yes.Click += (_, _) => Finish(true);
        no.Click += (_, _) => Finish(false);
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no }
                }
            }
        };
        Closed += (_, _) => _result.TrySetResult(false);
    }

    public static async Task<bool> Ask(Window owner, string message)
    {
        var window = new ConfirmWindow(message);
        await window.ShowDialog(owner);
        return await window._result.Task;
    }

    private void Finish(bool accepted)
    {
        _result.TrySetResult(accepted);
        Close();
    }
}
