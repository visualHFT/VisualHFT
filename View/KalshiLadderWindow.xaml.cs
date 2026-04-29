using System.Windows;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Per-ticker depth ladder for a single Kalshi market, styled to mirror
    /// kalshi.com's view (asks top in red, bids bottom in green, with cumulative
    /// dollar totals walking away from mid).
    /// </summary>
    public partial class KalshiLadderWindow : Window
    {
        public KalshiLadderWindow(string symbol)
        {
            InitializeComponent();
            var vm = new vmKalshiLadder(symbol);
            DataContext = vm;
            this.Closed += (_, _) => vm.Dispose();
        }
    }
}
