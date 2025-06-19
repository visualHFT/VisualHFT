using System;
using System.Collections.Generic;
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
using VisualHFT.TriggerEngine.ViewModel;
using VisualHFT.ViewModel;

namespace VisualHFT.TriggerEngine.View
{
    /// <summary>
    /// Interaction logic for TriggerSettingsView.xaml
    /// </summary>
    public partial class TriggerSettingsView : Window
    {
        List<TriggerEngineRuleViewModel> lstCurrentRules = new List<TriggerEngineRuleViewModel>();
        vmDashboard dashboard;
        public TriggerSettingsView(vmDashboard _dashboard)
        {
            InitializeComponent();

            

            this.dashboard= _dashboard;

            LoadAllRules();

        }

        private void NewRule_Click(object sender, RoutedEventArgs e)
        {
            try
            {

                TriggerSettingAddOrUpdate frmRuleView = new TriggerSettingAddOrUpdate(null, dashboard);
                frmRuleView.ShowDialog();
                LoadAllRules();
            }
            catch (Exception ex)
            {
            }

        }

        private void LoadAllRules()
        {
            lstCurrentRules = new List<TriggerEngineRuleViewModel>();
            this.DataContext = null;
            lstRules.ItemsSource = null;

            TriggerEngineService.GetRules().ForEach(x =>
            {
                TriggerEngineRuleViewModel vm = new TriggerEngineRuleViewModel();
                vm.Name = x.Name;
                vm.Condition = new BindingList<TriggerConditionViewModel>();
                vm.Actions = new BindingList<TriggerActionViewModel>();
                vm.IsEnabled = x.IsEnabled;
                vm.RuleID = x.RuleID; 

                x.Condition.ForEach(y =>
                {
                    TriggerConditionViewModel vmCondition = new TriggerConditionViewModel();
                    vmCondition.Plugin = y.Plugin;
                    vmCondition.Metric = y.Metric;
                    vmCondition.Operator = y.Operator;
                    vmCondition.Threshold = y.Threshold;
                    vmCondition.Window = y.Window;
                    vmCondition.ConditionID= y.ConditionID;
                    vm.Condition.Add(vmCondition);
                });
                x.Actions.ForEach(x =>
                {
                    TriggerActionViewModel vmAction = new TriggerActionViewModel();
                    vmAction.Type = x.Type;
                    vmAction.CooldownDuration = x.CooldownDuration;
                    vmAction.CooldownUnit = x.CooldownUnit;
                    vmAction.ActionID = x.ActionID;
                    if (x.Type==ActionType.RestApi && x.RestApi != null)
                    {
                        vmAction.RestApi = new RestApiActionViewModel();
                        vmAction.RestApi.Url = x.RestApi.Url;
                        vmAction.RestApi.BodyTemplate = x.RestApi.BodyTemplate;
                        vmAction.RestApi.Url = x.RestApi.Url;
                        vmAction.RestApi.Headers = new System.Collections.ObjectModel.ObservableCollection<RestAPIHeaderViewModel>();
                        foreach (var item in x.RestApi.Headers)
                        {
                            RestAPIHeaderViewModel vmHeader = new RestAPIHeaderViewModel();
                            vmHeader.HeaderName = item.Key;
                            vmHeader.HeaderValue = item.Value;
                            vmAction.RestApi.Headers.Add(vmHeader);
                        }
                    }
                    vm.Actions.Add(vmAction);


                });

                lstCurrentRules.Add(vm);
            });

            this.DataContext = lstCurrentRules;
            lstRules.ItemsSource = lstCurrentRules;

        }

        private void UpdateRule(object sender, RoutedEventArgs e)
        {
            Button hyperlink = (Button)sender;
            TriggerEngineRuleViewModel selectedRule = (TriggerEngineRuleViewModel)hyperlink.DataContext;
            TriggerSettingAddOrUpdate frmRuleView = new TriggerSettingAddOrUpdate(selectedRule, dashboard);
            frmRuleView.Title = selectedRule.Name;
            frmRuleView.ShowDialog();
            LoadAllRules();
        }

        private void StopRule(object sender, RoutedEventArgs e)
        {
            Button hyperlink = (Button)sender;
            TriggerEngineRuleViewModel selectedRule = (TriggerEngineRuleViewModel)hyperlink.DataContext;
            MessageBoxResult result = MessageBox.Show($"Are you sure you want to stop the rule '{selectedRule.Name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                TriggerEngineService.StopRule(selectedRule.Name);
                LoadAllRules();
            }
        }

        private void StartRule(object sender, RoutedEventArgs e)
        {
           
                Button hyperlink = (Button)sender;
            TriggerEngineRuleViewModel selectedRule = (TriggerEngineRuleViewModel)hyperlink.DataContext;
            MessageBoxResult result = MessageBox.Show($"Are you sure you want to start the rule '{selectedRule.Name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                TriggerEngineService.StartRule(selectedRule.Name);
                LoadAllRules();
            }

        }

        private void RemoveRule(object sender, RoutedEventArgs e)
        {
            Button hyperlink = (Button)sender;
            TriggerEngineRuleViewModel selectedRule = (TriggerEngineRuleViewModel)hyperlink.DataContext;

            MessageBoxResult result = MessageBox.Show($"Are you sure you want to remove the rule '{selectedRule.Name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes && selectedRule.RuleID.HasValue)
            {
                TriggerEngineService.RemoveRule(selectedRule.RuleID.Value);
                LoadAllRules();
            }
        }
    }
}
