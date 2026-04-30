using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisualHFT.Helpers;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Tabbed browser of every open Kalshi event, grouped by API category.
    /// Double-click any event row → fetch its markets and add them to the
    /// dynamic poller (KalshiBrowserPoller) so they show up in the Provider/
    /// Symbol dropdown and the strike ladder.
    /// </summary>
    public partial class KalshiEventBrowserWindow : Window
    {
        private readonly vmKalshiEventBrowser _vm;

        public KalshiEventBrowserWindow()
        {
            InitializeComponent();
            _vm = new vmKalshiEventBrowser();
            DataContext = _vm;
            this.Loaded += async (_, _) => await _vm.RefreshAsync();
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshBtn.IsEnabled = false;
            try
            {
                KalshiEventCatalog.InvalidateCache();   // force a fresh fetch on Refresh
                await _vm.RefreshAsync();
            }
            finally { RefreshBtn.IsEnabled = true; }
        }

        private async void GroupsTabs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Find the DataGridRow that was double-clicked by walking the visual tree
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not DataGridRow)
                dep = VisualTreeHelper.GetParent(dep);
            if (dep is not DataGridRow row || row.Item is not KalshiEventInfo evt) return;
            if (string.IsNullOrEmpty(evt.EventTicker)) return;

            // Plain double-click keeps current behavior: watch + auto-load chart.
            await WatchEventAsync(evt, loadChart: true);
        }

        private async Task WatchEventAsync(KalshiEventInfo evt, bool loadChart)
        {
            this.Title = $"Kalshi — Events Browser  •  Loading markets for {evt.EventTicker}…";
            try
            {
                using var catalog = KalshiEventCatalog.ForProd();
                var markets = await catalog.GetEventMarketsAsync(evt.EventTicker);
                if (markets.Count == 0)
                {
                    this.Title = $"Kalshi — Events Browser  •  {evt.EventTicker}: no markets returned";
                    return;
                }
                KalshiBrowserPoller.Instance.Watch(markets);
                int total = KalshiBrowserPoller.Instance.WatchedTickers.Count;
                this.Title = $"Kalshi — Events Browser  •  Watching {markets.Count} markets from {evt.EventTicker} (total: {total})";

                if (loadChart)
                {
                    // Auto-load the first market into the main chart.
                    var first = markets.FirstOrDefault(m => !string.IsNullOrEmpty(m));
                    if (first != null)
                        KalshiViewRequest.Show(first, KalshiBrowserPoller.KalshiProviderId);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to watch {evt.EventTicker}:\n\n{ex.Message}",
                    "Events Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void AddToWatchList_Click(object sender, RoutedEventArgs e)
        {
            var evt = SelectedEvent();
            if (evt != null) await WatchEventAsync(evt, loadChart: false);
        }

        private async void WatchAndLoadChart_Click(object sender, RoutedEventArgs e)
        {
            var evt = SelectedEvent();
            if (evt != null) await WatchEventAsync(evt, loadChart: true);
        }

        private async void ShowInStrikeLadder_Click(object sender, RoutedEventArgs e)
        {
            var evt = SelectedEvent();
            if (evt == null) return;
            await WatchEventAsync(evt, loadChart: false);
            // Open / focus the strike ladder so the user can see all strikes side-by-side
            try
            {
                var ladder = new View.KalshiStrikeLadderWindow();
                ladder.Show();
            }
            catch { /* best effort */ }
        }

        /// <summary>Find the KalshiEventInfo for the currently-selected row across any tab.</summary>
        private KalshiEventInfo? SelectedEvent()
        {
            // The TabControl's SelectedContent is the per-category DataGrid; selected row lives there.
            var content = GroupsTabs?.SelectedContent;
            if (content is FrameworkElement fe)
            {
                var grid = FindDescendant<DataGrid>(fe);
                if (grid?.SelectedItem is KalshiEventInfo evt) return evt;
            }
            return null;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T match) return match;
                var deeper = FindDescendant<T>(c);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
