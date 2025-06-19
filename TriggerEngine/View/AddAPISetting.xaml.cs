using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
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
using VisualHFT.TriggerEngine;
using VisualHFT.TriggerEngine.Actions;
using VisualHFT.TriggerEngine.ViewModel;
using VisualHFT.ViewModel;

namespace VisualHFT.TriggerEngine.View
{
    /// <summary>
    /// Interaction logic for TriggerSettingAddOrUpdate.xaml
    /// </summary>
    public partial class AddAPISetting : Window
    {
          
        public RestApiActionViewModel restApiAction = new RestApiActionViewModel();
         
        public AddAPISetting(RestApiActionViewModel _rule)
        {
          
            InitializeComponent();
         
            this.DataContext = _rule;

            if (_rule != null)
            {
                this.restApiAction = _rule; 
                lstHeaders.ItemsSource = _rule.Headers;
            }
            else
            {
                restApiAction.BodyTemplate = "{\r\n\r\n\"plugin\":\"{{plugin}}\",\r\n\"value\":\"{{value}}\",\r\n\"timestamp\":\"{{timestamp}}\", \r\n}";
            }
            this.DataContext = restApiAction;
        }



        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if(restApiAction.Url== null || restApiAction.Url == string.Empty || !CheckValidURL(restApiAction.Url))
            {
                MessageBox.Show("Please enter a valid URL.");
                return;
            }
            if (restApiAction.BodyTemplate == null || restApiAction.BodyTemplate == string.Empty)
            {
                MessageBox.Show("Please enter a valid Body Template.");
                return;
            }   
            
            if (restApiAction.CoolDownUnit == null)
            {
                MessageBox.Show("Please enter a valid Cooldown Unit.");
                return;
            }
            if (restApiAction.CooldownPeriod== null || restApiAction.CooldownPeriod<=0)
            {
                MessageBox.Show("Please enter a valid Cooldown Period.");
                return;
            }

            restApiAction.Url = txtURL.Text;
            restApiAction.BodyTemplate = txtTemplate.Text;
            restApiAction.Headers = [];
            for (var i = 0; i < lstHeaders.Items.Count; i++)
            {
                restApiAction.Headers.Add(lstHeaders.Items[i] as RestAPIHeaderViewModel);
            }
           
            DialogResult = true;           
        }

        private void btnAddNewHeader(object sender, RoutedEventArgs e)
        {
            if(this.restApiAction == null)
            {
                this.restApiAction = new RestApiActionViewModel();
            } 
            this.restApiAction.Headers.Add(new RestAPIHeaderViewModel());
            lstHeaders.ItemsSource = this.restApiAction.Headers;
            lstHeaders.InvalidateVisual();
        }

        private void Diag_Close(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        private bool CheckValidURL(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
           && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);


        }

        private void ShowCooldownToolTip(object sender, MouseEventArgs e)
        {
            CooldownToolTip.IsOpen = true;
        }
    }
     
}
