using System.Windows;
using System.Windows.Input;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Standalone window showing live Kalshi top-of-book per strike,
    /// grouped by event. Double-click a row to open the per-ticker ladder.
    /// </summary>
    public partial class KalshiStrikeLadderWindow : Window
    {
        public KalshiStrikeLadderWindow()
        {
            InitializeComponent();
            DataContext = new vmKalshiStrikeLadder();
            this.Closed += (_, _) =>
            {
                if (DataContext is vmKalshiStrikeLadder vm) vm.Dispose();
            };
        }

        private void StrikesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (StrikesGrid.SelectedItem is KalshiStrikeRow row && !string.IsNullOrEmpty(row.Ticker))
            {
                var ladder = new KalshiLadderWindow(row.Ticker);
                ladder.Show();
            }
        }
    }
}
