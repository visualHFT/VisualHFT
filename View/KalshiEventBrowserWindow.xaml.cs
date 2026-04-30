using System;
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
            try { await _vm.RefreshAsync(); }
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

            await WatchEventAsync(evt);
        }

        private async Task WatchEventAsync(KalshiEventInfo evt)
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
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to watch {evt.EventTicker}:\n\n{ex.Message}",
                    "Events Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
