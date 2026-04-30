using System.Collections.Specialized;
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

            // Auto-scroll asks to bottom (best ask near mid divider) and bids to
            // top (best bid near mid divider) so the action stays in view as the
            // book updates.
            vm.Asks.CollectionChanged += (_, _) => ScrollAsksToBottom();
            vm.Bids.CollectionChanged += (_, _) => ScrollBidsToTop();

            this.Closed += (_, _) => vm.Dispose();
        }

        private void ScrollAsksToBottom()
        {
            if (AsksGrid?.Items.Count > 0)
                AsksGrid.ScrollIntoView(AsksGrid.Items[AsksGrid.Items.Count - 1]);
        }

        private void ScrollBidsToTop()
        {
            if (BidsGrid?.Items.Count > 0)
                BidsGrid.ScrollIntoView(BidsGrid.Items[0]);
        }
    }
}
