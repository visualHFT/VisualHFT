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

        // Provider ID for the Polymarket plugin. Matches
        // PolymarketPluginSettings.Provider.ProviderID in the visualhft-polymarket repo.
        private const int PolymarketProviderId = 11;

        /// <summary>True if the row originated from the Polymarket catalog.</summary>
        private static bool IsPolymarketRow(KalshiEventInfo evt) =>
            !string.IsNullOrEmpty(evt.PolymarketYesToken);

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshBtn.IsEnabled = false;
            try
            {
                // Invalidate whichever venue's cache is currently selected so the
                // refresh button actually re-fetches.
                if (_vm.IsPolymarket)
                    PolymarketBrowserPoller.InvalidateCache();
                else
                    KalshiEventCatalog.InvalidateCache();
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
            // Polymarket events route differently: there's no Kalshi-style
            // event→markets fan-out (each event already carries its first
            // market's YES clobTokenId). All we do is fire the cross-window
            // request so the main view can pick it up via the Polymarket
            // plugin (providerId 11).
            if (IsPolymarketRow(evt))
            {
                if (loadChart)
                {
                    KalshiViewRequest.Show(evt.PolymarketYesToken, PolymarketProviderId);
                    this.Title = $"Polymarket — Events Browser  •  Loaded {evt.EventTicker}";
                }
                else
                {
                    // "Add to Watch List (no chart)" — not yet wired for Polymarket;
                    // surface a friendly message rather than silently no-op.
                    MessageBox.Show(
                        "Watch List support for Polymarket events is not yet implemented.\n" +
                        "Use 'Watch + Load Chart' (double-click a row) instead.",
                        "Polymarket — Events Browser",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

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
            if (evt == null) return;
            if (IsPolymarketRow(evt))
            {
                MessageBox.Show(
                    "Add to Watch List is not yet supported for Polymarket events.\n" +
                    "Use 'Watch + Load Chart' instead (the chart loads the YES token directly).",
                    "Polymarket — Events Browser",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await WatchEventAsync(evt, loadChart: false);
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
            if (IsPolymarketRow(evt))
            {
                MessageBox.Show(
                    "Strike Ladder is not yet supported for Polymarket events.\n" +
                    "It's currently wired for Kalshi event markets only.",
                    "Polymarket — Events Browser",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
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
