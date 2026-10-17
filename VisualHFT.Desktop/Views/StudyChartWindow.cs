using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VisualHFT.Desktop.ViewModels;

namespace VisualHFT.Desktop.Views;

public sealed class StudyChartWindow : Window
{
    private readonly StudyRow _row;
    private readonly SeriesChart _chart;
    private readonly DispatcherTimer _timer;

    public StudyChartWindow(StudyRow row)
    {
        _row = row;
        Title = row.Title;
        Width = 760;
        Height = 420;
        Background = new SolidColorBrush(Color.Parse("#0E1116"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _chart = new SeriesChart { Margin = new Avalonia.Thickness(12), ScaleEachSeries = true };
        var waiting = new TextBlock
        {
            Text = "Waiting for the first values from " + row.Title + ".",
            Margin = new Avalonia.Thickness(12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            [DockPanel.DockProperty] = Dock.Bottom
        };
        var settings = new Button { Content = "Settings", Classes = { "quiet" }, Margin = new Avalonia.Thickness(12, 8, 12, 0) };
        settings.Click += async (_, _) =>
        {
            var caption = Owner is MainWindow { DataContext: TerminalViewModel terminal } ? terminal.FocusCaption() : null;
            var window = new PluginSettingsWindow(row.Owner, caption);
            await window.ShowDialog(this);
            if (Owner is MainWindow { DataContext: TerminalViewModel desk })
                desk.RetargetStudies();
            row.RefreshContext();
        };
        var header = new DockPanel
        {
            [DockPanel.DockProperty] = Dock.Top,
            Children =
            {
                settings,
                new TextBlock
                {
                    Text = row.Title + "  vs market mid",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Avalonia.Thickness(12, 12, 12, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            }
        };
        DockPanel.SetDock(settings, Dock.Right);
        Content = new DockPanel
        {
            Children =
            {
                header,
                waiting,
                _chart
            }
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Paint(waiting);
        Opened += (_, _) =>
        {
            Paint(waiting);
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Paint(TextBlock waiting)
    {
        var values = _row.CopyHistory();
        var mids = _row.CopyMids();
        if (values.Length < 2)
        {
            waiting.IsVisible = true;
            waiting.Text = values.Length == 0
                ? "No values yet. " + (string.IsNullOrEmpty(_row.Context) ? _row.Title : _row.Context)
                : "One value so far. The line appears on the next update.";
            _chart.Series = Array.Empty<ChartSeries>();
            return;
        }

        waiting.IsVisible = false;

        var series = new List<ChartSeries>
        {
            new()
            {
                Name = _row.Title,
                Values = values,
                Color = Color.Parse("#61AFFE")
            }
        };
        if (mids.Any(mid => mid != 0))
        {
            series.Add(new ChartSeries
            {
                Name = "Market mid",
                Values = mids,
                Color = Color.Parse("#3DDC84")
            });
        }
        _chart.Series = series;
    }
}
