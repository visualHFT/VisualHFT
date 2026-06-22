using System.Windows;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Implied probability mass function for one event, derived live from the
    /// strike ladder. Opens for the event of whichever strike row was selected.
    /// </summary>
    public partial class KalshiPMFWindow : Window
    {
        public KalshiPMFWindow(string eventTicker)
        {
            InitializeComponent();
            var vm = new vmKalshiPMF(eventTicker);
            DataContext = vm;
            this.Closed += (_, _) => vm.Dispose();
        }
    }
}
