using System.Windows;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Tabbed browser of every open Kalshi event, grouped by API category.
    /// Read-only. Use this to discover what's tradeable on Kalshi.
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
    }
}
