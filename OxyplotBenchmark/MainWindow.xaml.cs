using OxyPlotBenchmark;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Threading.Tasks;

namespace OxyplotBenchmark
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            await StartBenchmarkAsync();
        }

        private async Task StartBenchmarkAsync()
        {
            const int iterations = 10000;
            const int maxChartPoints = 1000;

            // Disable the button while running to prevent multiple executions
            btnStart.IsEnabled = false;
            
            try
            {
                var official = await Task.Run(() => BenchmarkOfficialRealTimeChart.Run(iterations, maxChartPoints));
                lblResultsOffical.Content = official.ToString();

                var forked = await Task.Run(() => BenchmarkForkedRealTimeChart.Run(iterations, maxChartPoints));
                lblResultsForked.Content = forked;
            }
            finally
            {
                // Re-enable the button when done
                btnStart.IsEnabled = true;
            }
        }
    }
}