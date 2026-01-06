using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.Studies.Template.Model;
using VisualHFT.UserSettings;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VisualHFT.Commons.Helpers;

namespace VisualHFT.Studies.Template.ViewModel
{
    /// <summary>
    /// ViewModel for the plugin settings UI.
    /// Handles validation and user interactions for the study configuration.
    /// </summary>
    public class PluginSettingsViewModel : INotifyPropertyChanged
    {
        #region Private Fields
        
        private PlugInSettings _settings;
        private string _selectedSymbol;
        private VisualHFT.ViewModel.Model.Provider _selectedProvider;
        private AggregationLevel _aggregationLevelSelection;
        private string _validationMessage;
        private int _timePeriodMs;
        private double _minVolumeThreshold;
        private double _alertThreshold;
        private bool _enableAlerts;
        private double _customParameter1;
        private int _customParameter2;
        
        public Action UpdateSettingsFromUI { get; set; }
        
        #endregion

        #region Public Properties
        
        public PlugInSettings Settings
        {
            get => _settings;
            set
            {
                _settings = value;
                OnPropertyChanged();
            }
        }

        public string SelectedSymbol
        {
            get => _selectedSymbol;
            set
            {
                _selectedSymbol = value;
                OnPropertyChanged();
                ValidateSettings();
            }
        }

        public VisualHFT.ViewModel.Model.Provider SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                _selectedProvider = value;
                OnPropertyChanged();
            }
        }

        public AggregationLevel AggregationLevelSelection
        {
            get => _aggregationLevelSelection;
            set
            {
                _aggregationLevelSelection = value;
                OnPropertyChanged();
                Settings.AggregationLevel = value;
                ValidateSettings();
            }
        }

        public int TimePeriodMs
        {
            get => _timePeriodMs;
            set
            {
                _timePeriodMs = value;
                OnPropertyChanged();
                Settings.TimePeriodMs = value;
                ValidateSettings();
            }
        }

        public double MinVolumeThreshold
        {
            get => _minVolumeThreshold;
            set
            {
                _minVolumeThreshold = value;
                OnPropertyChanged();
                Settings.MinVolumeThreshold = value;
                ValidateSettings();
            }
        }

        public double AlertThreshold
        {
            get => _alertThreshold;
            set
            {
                _alertThreshold = value;
                OnPropertyChanged();
                Settings.AlertThreshold = value;
                ValidateSettings();
            }
        }

        public bool EnableAlerts
        {
            get => _enableAlerts;
            set
            {
                _enableAlerts = value;
                OnPropertyChanged();
                Settings.EnableAlerts = value;
            }
        }

        public double CustomParameter1
        {
            get => _customParameter1;
            set
            {
                _customParameter1 = value;
                OnPropertyChanged();
                Settings.CustomParameter1 = value;
                ValidateSettings();
            }
        }

        public int CustomParameter2
        {
            get => _customParameter2;
            set
            {
                _customParameter2 = value;
                OnPropertyChanged();
                Settings.CustomParameter2 = value;
                ValidateSettings();
            }
        }

        public string ValidationMessage
        {
            get => _validationMessage;
            set
            {
                _validationMessage = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<VisualHFT.ViewModel.Model.Provider> Providers { get; set; } = new();
        public List<string> Symbols { get; set; } = new();
        public List<Tuple<string, AggregationLevel>> AggregationLevels { get; set; } = new();

        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;
        
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region Commands
        
        public ICommand OkCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
        
        #endregion

        #region Constructor
        
        public PluginSettingsViewModel(PlugInSettings settings)
        {
            Settings = settings ?? new PlugInSettings();
            
            // Initialize data
            InitializeData();
            
            // Initialize commands
            OkCommand = new RelayCommand<object>(OkCommand_Execute, OkCommand_CanExecute);
            CancelCommand = new RelayCommand<object>(CancelCommand_Execute);
            
            // Load initial values
            LoadSettings();
        }
        
        #endregion

        #region Private Methods
        
        private void InitializeData()
        {
            // Initialize providers list
            Providers = VisualHFT.ViewModel.Model.Provider.CreateObservableCollection();
            
            // Initialize symbols list
            Symbols = HelperSymbol.Instance.ToList();
            
            // Initialize aggregation levels
            AggregationLevels = new List<Tuple<string, AggregationLevel>>
            {
                Tuple.Create("None", AggregationLevel.None),
                Tuple.Create("1 Millisecond", AggregationLevel.Ms1),
                Tuple.Create("10 Milliseconds", AggregationLevel.Ms10),
                Tuple.Create("100 Milliseconds", AggregationLevel.Ms100),
                Tuple.Create("500 Milliseconds", AggregationLevel.Ms500),
                Tuple.Create("1 Second", AggregationLevel.S1),
                Tuple.Create("3 Seconds", AggregationLevel.S3),
                Tuple.Create("5 Seconds", AggregationLevel.S5),
                Tuple.Create("Daily", AggregationLevel.D1)
            };
        }

        private void LoadSettings()
        {
            SelectedSymbol = Settings.Symbol;
            // Convert Model.Provider to ViewModel.Model.Provider
            if (Settings.Provider != null)
            {
                SelectedProvider = Providers.FirstOrDefault(p => p.ProviderID == Settings.Provider.ProviderID);
            }
            AggregationLevelSelection = Settings.AggregationLevel;
            TimePeriodMs = Settings.TimePeriodMs;
            MinVolumeThreshold = Settings.MinVolumeThreshold;
            AlertThreshold = Settings.AlertThreshold;
            EnableAlerts = Settings.EnableAlerts;
            CustomParameter1 = Settings.CustomParameter1;
            CustomParameter2 = Settings.CustomParameter2;
        }

        private void ValidateSettings()
        {
            ValidationMessage = string.Empty;
            
            if (SelectedProvider == null)
            {
                ValidationMessage = "Please select a provider";
                return;
            }
            
            if (string.IsNullOrEmpty(SelectedSymbol))
            {
                ValidationMessage = "Please select a symbol";
                return;
            }
            
            if (TimePeriodMs <= 0)
            {
                ValidationMessage = "Time period must be greater than 0";
                return;
            }
            
            if (MinVolumeThreshold < 0)
            {
                ValidationMessage = "Minimum volume threshold cannot be negative";
                return;
            }
            
            if (AlertThreshold < 0)
            {
                ValidationMessage = "Alert threshold cannot be negative";
                return;
            }
            
            if (CustomParameter1 <= 0)
            {
                ValidationMessage = "Custom Parameter 1 must be greater than 0";
                return;
            }
            
            if (CustomParameter2 <= 0)
            {
                ValidationMessage = "Custom Parameter 2 must be greater than 0";
                return;
            }
        }

        #endregion

        #region Command Handlers
        
        private bool OkCommand_CanExecute(object obj)
        {
            return string.IsNullOrEmpty(ValidationMessage);
        }

        private void OkCommand_Execute(object obj)
        {
            // Save settings
            Settings.Symbol = SelectedSymbol;
            Settings.Provider = SelectedProvider;
            Settings.AggregationLevel = AggregationLevelSelection;
            
            // Close the window
            if (obj is Window window)
            {
                window.DialogResult = true;
                window.Close();
            }
        }

        private void CancelCommand_Execute(object obj)
        {
            // Close the window without saving
            if (obj is Window window)
            {
                window.DialogResult = false;
                window.Close();
            }
        }
        
        #endregion
    }
}
