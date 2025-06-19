using BitStamp.Net.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
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
using VisualHFT.TriggerEngine;
using VisualHFT.TriggerEngine.Actions;
using VisualHFT.TriggerEngine.ViewModel;
using VisualHFT.ViewModel;

namespace VisualHFT.TriggerEngine.View
{




    /// <summary>
    /// Interaction logic for TriggerSettingAddOrUpdate.xaml
    /// </summary>
    public partial class TriggerSettingAddOrUpdate : Window
    {
        TriggerEngineRuleViewModel model = new TriggerEngineRuleViewModel();
        public ConditionOperator ConditionOperator { get; set; }
        public List<TilesView> PluginNames { get; set; }

        public long selectedID { get; set; }

        public TriggerSettingAddOrUpdate(TriggerEngineRuleViewModel _rule, vmDashboard dashboard)
        {
            InitializeComponent();

            var vmDashboard = dashboard;
            PluginNames = new List<TilesView>();
            vmDashboard.Tiles.ToList().ForEach(x =>
            {
                TilesView vm = new TilesView();
                vm.TileName = x.Title + Environment.NewLine + x.SelectedProviderName + ": " + x.SelectedSymbol;
                vm.PluginID = x.PluginID;
                
                PluginNames.Add(vm);
            });

            if(_rule!=null)
            {
                this.model = _rule;
                inappCheck.IsChecked = this.model.Actions.Any(x => x.Type == ActionType.UIAlert);
                webhookCheck.IsChecked = this.model.Actions.Any(x => x.Type == ActionType.RestApi);
                
            } 
            DataContext = this.model;
           
        }

        private void btnAddNewCondition_Click(object sender, RoutedEventArgs e)
        {
            var triggerCondtion = new TriggerConditionViewModel();
            triggerCondtion.ConditionID = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            this.model.Condition.Add(triggerCondtion);
            lstData.InvalidateVisual();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(this.model.Name))
            {
                MessageBox.Show("Please enter a name for the rule", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (this.model.Condition.Count == 0)
            {
                MessageBox.Show("Please add at least one condition", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (this.model.Actions.Count == 0)
            {
                MessageBox.Show("Please add at least one action", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!this.model.RuleID.HasValue)
            {
                var alreadyAddedRuleName = TriggerEngineService.GetRules().Where(x => x.Name == this.model.Name).FirstOrDefault();
                if (alreadyAddedRuleName != null)
                {
                    MessageBox.Show("Rule Name already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            if (!this.model.RuleID.HasValue)
            {
                model.RuleID = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var item in this.model.Condition)
                { 
                    item.ConditionID= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }

            if (webhookCheck.IsChecked.HasValue && webhookCheck.IsChecked.Value)
            {
                var restAPI = this.model.Actions.Where(x => x.Type == ActionType.RestApi).FirstOrDefault();
                if (restAPI != null)
                {
                    if (restAPI.RestApi == null || string.IsNullOrEmpty(restAPI.RestApi.Url))
                    {
                        MessageBox.Show("Rest API is not in correct form", "Error", MessageBoxButton.OK);
                        return;
                    }
                }
            }
            TriggerEngineRuleViewModel rule = this.model;             
            TriggerRule triggerRule=rule.FromViewModel(rule);
           

            TriggerEngineService.AddOrUpdateRule(triggerRule);
            MessageBox.Show("Rule saved successfully", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            this.DialogResult = true;
           
            this.Close();
        }
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
            
        private void btnAddNewAction_Click(object sender, RoutedEventArgs e)
        {
            TriggerActionViewModel mod = new TriggerActionViewModel();
            mod.ActionID = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            this.model.Actions.Add(mod);
            //lstDataAction.InvalidateVisual();

        } 
        public T DeepClone<T>(T obj)
        {
            var json = JsonConvert.SerializeObject(obj);
            return JsonConvert.DeserializeObject<T>(json);
        }

        private void ClickSetAPI(object sender, RoutedEventArgs e)
        {
            if (sender is Hyperlink hyperlink)
            {
                var webHookAlert = model.Actions.Where(x => x.Type == ActionType.RestApi).FirstOrDefault();

                var restAPIAction = DeepClone<RestApiActionViewModel>(webHookAlert.RestApi);
                if (restAPIAction != null)
                {
                    restAPIAction.CooldownPeriod = webHookAlert.CooldownDuration;
                    restAPIAction.CoolDownUnit = webHookAlert.CooldownUnit;
                }
                AddAPISetting frmRuleView = new AddAPISetting(restAPIAction);
                frmRuleView.Title = this.Title + " " + "Webhook URL Configuration";
                var d = frmRuleView.ShowDialog();
                if (d == true)
                {
                    RestApiActionViewModel mod = frmRuleView.restApiAction;
                    List<TriggerActionViewModel> replaceMod = this.model.Actions.Where(e => e.Type == ActionType.RestApi).ToList();
                    if (replaceMod.Count > 0)
                    {
                        replaceMod[0].RestApi = mod;
                        replaceMod[0].CooldownUnit = mod.CoolDownUnit;
                        replaceMod[0].CooldownDuration = mod.CooldownPeriod;
                    }
                }
            }
        }
         private void ClickSetInAppNotification(object sender, RoutedEventArgs e)
        {
            if (sender is Hyperlink hyperlink )
            {
                TriggerActionViewModel? triggerAction = model.Actions.Where(x => x.Type == ActionType.UIAlert).FirstOrDefault();
                 
                AddUIAlertSetting frmRuleView = new AddUIAlertSetting(triggerAction);
                frmRuleView.Title = this.Title + " " + "In-App UI Alert Configuration";
                var d = frmRuleView.ShowDialog();
                if (d == true)
                {
                    TriggerActionViewModel mod = frmRuleView.rule;
                    List<TriggerActionViewModel> replaceMod = this.model.Actions.Where(e => e.Type == ActionType.UIAlert).ToList();
                    if (replaceMod.Count > 0)
                    {
                        replaceMod[0] = mod;
                    }
                }
            }
        }

        private void ComboBox_Selected(object sender, RoutedEventArgs e)
        {

        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cbox && cbox.DataContext is TriggerActionViewModel data)
            {
                ActionType? newValue = null;


                if (e.AddedItems.Count > 0)
                {
                     newValue = (ActionType?)e.AddedItems[0];  
                }

                List<TriggerActionViewModel> replaceMod = this.model.Actions.Where(e => e.ActionID == data.ActionID).ToList();
                if (replaceMod.Count > 0)
                {
                    if (newValue != null && newValue == ActionType.RestApi)
                    {
                        replaceMod[0].IsEnabled = true;
                        //lstDataAction.InvalidateVisual();
                    } else
                    {
                        replaceMod[0].IsEnabled = false;
                       // lstDataAction.InvalidateVisual();
                    } 
                }

                
            }

        }

        private void ComboBox_SelectionChanged_1(object sender, SelectionChangedEventArgs e)
        {

        }

        private void ShowConditionToolTip(object sender, MouseEventArgs e)
        {
            ConditionToolTip.IsOpen = true;

        }

        private void ShowActionToolTip(object sender, MouseEventArgs e)
        {
            ActionToolTip.IsOpen = true;
        }

        private void RemoveCondition(object sender, RoutedEventArgs e)
        {
            Hyperlink hyperlink = (Hyperlink)sender;
            TriggerConditionViewModel selectedCondition = (TriggerConditionViewModel)hyperlink.DataContext;
             
            if(selectedCondition!=null)
            {
                MessageBoxResult result = MessageBox.Show($"Are you sure you want to remove the condition ?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                   model.Condition.Remove(selectedCondition);
                }
            }
        } 
        private void RemoveAction(object sender, RoutedEventArgs e)
        {
            Hyperlink hyperlink = (Hyperlink)sender;
            TriggerActionViewModel selectedCondition = (TriggerActionViewModel)hyperlink.DataContext;             
            if(selectedCondition!=null)
            {
                MessageBoxResult result = MessageBox.Show($"Are you sure you want to remove the action ?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                   model.Actions.Remove(selectedCondition);
                }
            }
        }

        private void InAppChecked(object sender, RoutedEventArgs e)
        {
            var uialert=model.Actions.Where(x => x.Type == ActionType.UIAlert).FirstOrDefault();
            if(uialert==null)
            {
                model.Actions.Add(new TriggerActionViewModel
                {
                    IsEnabled = true,
                    ActionID = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    Type = ActionType.UIAlert,
                    CooldownDuration = 1,
                    CooldownUnit=TimeWindowUnit.Seconds
                });
                
            }
        }  
        private void InAppUnChecked(object sender, RoutedEventArgs e)
        {
            var uialert = model.Actions.Where(x => x.Type == ActionType.UIAlert).FirstOrDefault();
            if (uialert != null)
            {
                model.Actions.Remove(uialert);
            }
        }

        private void WebHookChecked(object sender, RoutedEventArgs e)
        {
            var webHookAlert = model.Actions.Where(x => x.Type == ActionType.RestApi).FirstOrDefault();
            if (webHookAlert == null)
            {
                model.Actions.Add(new TriggerActionViewModel
                {
                    IsEnabled = true,
                    ActionID = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    Type = ActionType.RestApi,
                    CooldownDuration = 1,
                    CooldownUnit = TimeWindowUnit.Seconds
                });

            }
        } 
        private void WebHookUnChecked(object sender, RoutedEventArgs e)
        {

            var uialert = model.Actions.Where(x => x.Type == ActionType.RestApi).FirstOrDefault();
            if (uialert != null)
            {
                model.Actions.Remove(uialert);
            }
        }
    }
     
}
