using System;
using System.Linq;
using System.Windows;
using System.Globalization;
using System.Windows.Markup;
using VisualHFT.ViewModel;
using VisualHFT.UserSettings;
using VisualHFT.View;
using VisualHFT.TriggerEngine.View;
using VisualHFT.Commons.Helpers;

namespace VisualHFT
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class Dashboard : Window
    {
        /*
         * 

            VisualHFT.

            Plugin = "PluginID1",
                        Metric = "LOB",
         * 
         */
        public Dashboard()
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.Name)));

            InitializeComponent();
            var context = new VisualHFT.ViewModel.vmDashboard(Helpers.HelperCommon.GLOBAL_DIALOGS);
            this.DataContext = context;


        }

        private void ButtonAppSettings_Click(object sender, RoutedEventArgs e)
        {
            var vm = new vmUserSettings();
            vm.LoadJson(SettingsManager.Instance.GetAllSettings());

            var form = new View.UserSettings();
            form.DataContext = vm;
            form.ShowDialog();
        }

        private void ButtonMultiVenuePrices_Click(object sender, RoutedEventArgs e)
        {
            var form = new View.MultiVenuePrices();
            form.DataContext = new vmMultiVenuePrices();
            form.Show();
        }

        private void ButtonPluginManagement_Click(object sender, RoutedEventArgs e)
        {
            var form = new View.PluginManagerWindow();
            form.DataContext = new vmPluginManager();
            form.Show();
        }

        private void triggerRules_Click(object sender, RoutedEventArgs e)
        {
            TriggerSettingsView frmView=new TriggerSettingsView((vmDashboard)this.DataContext);
            frmView.Show();
        }
    }
}
