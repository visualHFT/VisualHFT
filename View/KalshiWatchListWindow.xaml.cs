using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisualHFT.Helpers;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Watch List for Kalshi tickers. Mirrors KalshiBrowserPoller's dynamic
    /// ticker set with live top-of-book and add/remove buttons. Anything
    /// here also flows into the strike ladder via HelperOrderBook.
    /// </summary>
    public partial class KalshiWatchListWindow : Window
    {
        private readonly vmKalshiWatchList _vm;

        public KalshiWatchListWindow()
        {
            InitializeComponent();
            _vm = new vmKalshiWatchList();
            DataContext = _vm;
            this.Closed += (_, _) => _vm.Dispose();
        }

        private void AddBtn_Click(object sender, RoutedEventArgs e)
        {
            var t = AddInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(t)) return;
            _vm.AddManual(t);
            AddInput.Text = "";
        }

        private void RemoveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is WatchListRow row)
                _vm.Remove(row);
        }

        private void WatchGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (WatchGrid.SelectedItem is WatchListRow row && !string.IsNullOrEmpty(row.Ticker))
                KalshiViewRequest.Show(row.Ticker, KalshiBrowserPoller.KalshiProviderId);
        }
    }
}
