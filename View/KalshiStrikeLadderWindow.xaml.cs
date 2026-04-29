using System.Windows;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Standalone window showing live Kalshi top-of-book per strike,
    /// grouped by event. Wired in Phase B of the Kalshi customization track.
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
    }
}
