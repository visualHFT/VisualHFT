using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace MarketConnector.Template.UserControls
{
    /// <summary>
    /// Interaction logic for PluginSettingsView.xaml
    /// </summary>
    public partial class PluginSettingsView : UserControl
    {
        public PluginSettingsView()
        {
            InitializeComponent();
        }

        // Hyperlink click handler used to open the API creation page in the default browser.
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore errors when launching the browser.  VisualHFT will not crash.
            }
            e.Handled = true;
        }
    }
}