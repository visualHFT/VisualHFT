using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using VisualHFT.Desktop.Screens;
using VisualHFT.Desktop.ViewModels;

namespace VisualHFT.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OpenStudyChart(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StudyRow row })
            new StudyChartWindow(row).Show(this);
    }

    private void OpenPluginSurface(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PluginRow row })
            new PluginSurfaceWindow(row).Show(this);
    }

    private async void OpenPluginSettings(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginRow row })
            return;
        var window = new PluginSettingsWindow(row.Plugin, FocusCaption());
        await window.ShowDialog(this);
        if (DataContext is TerminalViewModel terminal)
            terminal.RetargetStudies();
        row.Refresh();
    }

    private async void AddTriggerRule(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalViewModel terminal)
            return;
        var window = new TriggerRuleWindow();
        await window.ShowDialog(this);
        terminal.RefreshRules();
    }

    private async void EditTriggerRule(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalViewModel terminal || sender is not Button { DataContext: RuleRow row })
            return;
        var window = new TriggerRuleWindow(row.Rule);
        await window.ShowDialog(this);
        terminal.RefreshRules();
    }

    private async void ReloadSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalViewModel terminal)
            return;
        if (terminal.SettingsDirty && !await ConfirmWindow.Ask(this, "Discard unsaved settings changes?"))
            return;
        terminal.LoadSettingsTree();
    }

    private void SelectSettingsGroup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal && sender is Button { DataContext: SettingsGroupRow row })
            terminal.SelectedSettings = row;
    }

    private void ToggleSecret(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SettingsFieldRow field })
            field.Revealed = !field.Revealed;
    }

    private void SaveSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal)
            terminal.SaveSettingsTree();
    }

    private async void ToggleProvider(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalViewModel terminal || sender is not Button { DataContext: ProviderSessionRow row })
            return;
        if (!SessionControl.TryNext(row.Status, out var connect))
            return;
        var verb = connect ? "connect" : "disconnect";
        if (!await ConfirmWindow.Ask(this, "Are you sure you want to " + verb + " '" + row.Name + "'?"))
            return;
        terminal.ToggleProvider(row);
    }

    private void FilterOrders(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal && sender is Button { Tag: string filter })
            terminal.OrderFilter = filter;
    }

    private void MarkNotificationsRead(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal)
            terminal.MarkNotificationsRead();
    }

    private void ClearNotifications(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal)
            terminal.ClearNotifications();
    }

    private void PickTicker(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal && sender is Button { DataContext: TickerHit hit })
            terminal.AddTicker(hit);
    }

    private void FocusWatch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal && sender is Button { DataContext: WatchItemRow row })
            terminal.FocusWatch(row);
    }

    private void RemoveWatch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal && sender is Button { DataContext: WatchItemRow row })
            terminal.RemoveWatch(row);
    }

    private void TickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TerminalViewModel terminal)
            return;
        if (sender is TextBox box)
            terminal.TickerQuery = box.Text ?? "";
        if (e.Key == Key.Enter)
        {
            terminal.AddTicker(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            terminal.HideTickerHits();
            e.Handled = true;
        }
    }

    private void OpenVenues(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalViewModel terminal)
            return;
        var focus = terminal.FocusedWatch();
        new MultiVenueWindow(terminal.Symbols, focus?.Native ?? "", focus?.Normalized ?? "", focus?.Venue ?? "").Show(this);
    }

    private void CycleLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel terminal)
            terminal.CycleBookLayout();
    }

    private void OpenAlerts(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalViewModel terminal)
            return;
        var notes = terminal.Notifications.Select(row => row.Note).ToList();
        new NotificationBell(terminal.MarkNotificationsRead, terminal.ClearNotifications, notes).Show(this);
    }

    private void PopDepth(object? sender, RoutedEventArgs e) =>
        Pop("Cumulative depth", new DepthChart
        {
            [!DepthChart.BidsProperty] = new Binding("DepthBids"),
            [!DepthChart.AsksProperty] = new Binding("DepthAsks"),
            DataContext = DataContext
        });

    private void PopPrice(object? sender, RoutedEventArgs e) =>
        Pop("Bid, ask, mid and resting size", new BookPriceChart
        {
            [!BookPriceChart.FrameProperty] = new Binding("PriceFrame"),
            DataContext = DataContext
        });

    private void PopSpread(object? sender, RoutedEventArgs e) =>
        Pop("Spread", new SeriesChart
        {
            [!SeriesChart.SeriesProperty] = new Binding("SpreadSeries"),
            DataContext = DataContext
        });

    private async void OpenStudySettings(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StudyRow row })
            return;
        var window = new PluginSettingsWindow(row.Owner, FocusCaption());
        await window.ShowDialog(this);
        if (DataContext is TerminalViewModel terminal)
            terminal.RetargetStudies();
        row.RefreshContext();
    }

    private async void OpenGroupSettings(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StudyGroupRow group })
            return;
        var window = new PluginSettingsWindow(group.Plugin, FocusCaption());
        await window.ShowDialog(this);
        if (DataContext is TerminalViewModel terminal)
            terminal.RetargetStudies();
        foreach (var study in group.Studies)
            study.RefreshContext();
    }

    private string? FocusCaption() =>
        DataContext is TerminalViewModel terminal ? terminal.FocusCaption() : null;

    private void Pop(string title, Control content)
    {
        new ChartPopoutWindow(title, content).Show(this);
    }
}
