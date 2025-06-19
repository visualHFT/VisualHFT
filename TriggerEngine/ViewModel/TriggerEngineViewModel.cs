using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Ribbon;
using VisualHFT.TriggerEngine.Actions;

namespace VisualHFT.TriggerEngine.ViewModel
{
    public class TriggerEngineRuleViewModel : INotifyPropertyChanged
    {

        public long? RuleID { get; set; }
        private string _Name { get; set; }
     
        
        public BindingList<TriggerConditionViewModel> Condition { get; set; } = new BindingList<TriggerConditionViewModel>();
        public BindingList<TriggerActionViewModel> Actions { get; set; } = new BindingList<TriggerActionViewModel>();
         
        public bool IsEnabled { get; set; }


        public string Name
        {
            get => _Name;
            set { _Name = value; OnPropertyChanged(nameof(Name)); }
        }



        

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
         public TriggerRule FromViewModel(TriggerEngineRuleViewModel view)
        {
            TriggerRule rule = new TriggerRule();
            rule.Actions = new List<TriggerAction>();
            rule.Condition = new List<TriggerCondition>();

            rule.RuleID = view.RuleID.Value;
            rule.Name=view.Name;
            foreach (var item in view.Condition)
            {
                rule.Condition.Add(new TriggerCondition
                {
                    Plugin = item.Plugin,
                    Metric = item.Metric,
                    Operator = item.Operator,
                    Threshold = item.Threshold,
                    Window = item.Window,
                    ConditionID= item.ConditionID

                });
            }

            foreach (TriggerActionViewModel item in view.Actions)
            {
                var triggerAction = new TriggerAction();
                triggerAction.Type=item.Type;
                triggerAction.ActionID = item.ActionID;
                if(item.Type==ActionType.RestApi)
                {
                    triggerAction.RestApi = new RestApiAction
                    {
                        Url = item.RestApi.Url,
                        Method = item.RestApi.Method,
                        BodyTemplate = item.RestApi.BodyTemplate,
                        Headers = item.RestApi.Headers.ToDictionary(x => x.HeaderName, x => x.HeaderValue)
                    };
                }
                triggerAction.CooldownDuration = item.CooldownDuration;
                triggerAction.CooldownUnit = item.CooldownUnit;
                rule.Actions.Add(triggerAction);
            }
            return rule;
        }

    }

    public class TilesView
    {
        public string TileName { get; set; }


        public string PluginID { get; set; }
    }
    public class TriggerConditionViewModel : INotifyPropertyChanged
    {
        public long ConditionID { get; set; }
        private string _Plugin { get; set; }                 // e.g. "MarketMicrostructure"
        private string _Metric { get; set; }                 // e.g. "LOBImbalance"
        private ConditionOperator _Operator { get; set; }    // e.g. CrossesAbove, GreaterThan
        private double _Threshold { get; set; }              // e.g. 0.7
        private TimeWindow _Window { get; set; }             // Optional smoothing/aggregation logic


        public string Plugin
        {
            get => _Plugin;
            set { _Plugin = value; OnPropertyChanged(nameof(Plugin)); }
        }

        public string Metric
        {
            get => _Metric;
            set { _Metric = value; OnPropertyChanged(nameof(Metric)); }
        }
        public ConditionOperator Operator
        {
            get => _Operator;
            set { _Operator = value; OnPropertyChanged(nameof(Operator)); }
        }

        public double Threshold
        {
            get => _Threshold;
            set { _Threshold = value; OnPropertyChanged(nameof(Threshold)); }
        }
        
        public TimeWindow Window
        {
            get => _Window;
            set { _Window = value; OnPropertyChanged(nameof(Window)); }
        }


        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RestAPIHeaderViewModel
    {
        public string HeaderName { get; set; }
        public string HeaderValue { get; set; }
    }

    public class TriggerActionViewModel:INotifyPropertyChanged
    {
        public long ActionID { get; set; }
        private ActionType _Type { get; set; } = ActionType.RestApi;

        private RestApiActionViewModel? _RestApi { get; set; }         // Only required if Type == RestApi



        //Property Change implementation
        public int CooldownDuration { get; set; } = 0;
        public TimeWindowUnit CooldownUnit { get; set; } = TimeWindowUnit.Seconds; 

        //For UI
        public bool _IsEnabled { get; set; } = true;

        public bool IsEnabled
        {
            get => _IsEnabled;
            set { _IsEnabled = value; OnPropertyChanged(nameof(IsEnabled)); }
        }

        public ActionType Type
        {
            get => _Type;
            set { _Type = value; OnPropertyChanged(nameof(Type)); }
        }  
        public RestApiActionViewModel RestApi
        {
            get => _RestApi;
            set { _RestApi = value; OnPropertyChanged(nameof(RestApi)); }
        }

        //For UI use Only
        public string LinkText { get; set; } = "Set API"; 




        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    public class RestApiActionViewModel
    {
        public string Url { get; set; }                   // Destination API
        public string Method { get; set; } = "POST";       // POST or GET (for now)
        public string BodyTemplate { get; set; }           // JSON payload (e.g. includes {{metric}}, {{value}})
        public ObservableCollection<RestAPIHeaderViewModel> Headers { get; set; } = new ObservableCollection<RestAPIHeaderViewModel>();

        public int CooldownPeriod { get; set; }
        public TimeWindowUnit CoolDownUnit { get; set; }

    }
}