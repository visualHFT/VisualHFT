using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VisualHFT.TriggerEngine.Actions;
using VisualHFT.TriggerEngine.ViewModel;

namespace VisualHFT.TriggerEngine.View
{
    /// <summary>
    /// Interaction logic for AddUIAlertSetting.xaml
    /// </summary>
    public partial class AddUIAlertSetting : Window
    {
        public TriggerActionViewModel rule;
        public AddUIAlertSetting(TriggerActionViewModel _rule)
        {
            InitializeComponent();
            this.rule = _rule;

            this.DataContext = this.rule;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (rule.CooldownUnit == null)
            {
                MessageBox.Show("Please enter a valid Cooldown Unit.");
                return;
            }
            if (rule.CooldownDuration == null || rule.CooldownDuration <= 0)
            {
                MessageBox.Show("Please enter a valid Cooldown Period.");
                return;
            }
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        private void ShowCooldownToolTip(object sender, MouseEventArgs e)
        {
            CooldownToolTip.IsOpen = true;
        }
    }
}
