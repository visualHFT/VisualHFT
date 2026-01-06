using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using VisualHFT.Helpers;

namespace MarketConnector.Template.ViewModels
{
    /// <summary>
    /// ViewModel for the plugin settings window. Handles validation, commands,
    /// and data binding between the settings view and the settings model.
    /// </summary>
    public class PluginSettingsViewModel : INotifyPropertyChanged, IDataErrorInfo
    {
        private readonly Model.PlugInSettings _settings;
        private string _validationMessage;
        private string _successMessage;
        private readonly Action _closeWindow;

        public ICommand OkCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
        public ICommand TestConnectionCommand { get; private set; }

        /// <summary>
        /// Gets or sets the API key for the exchange.
        /// </summary>
        public string ApiKey
        {
            get => _settings.ApiKey;
            set
            {
                if (_settings.ApiKey != value)
                {
                    _settings.ApiKey = value;
                    OnPropertyChanged(nameof(ApiKey));
                }
            }
        }

        /// <summary>
        /// Gets or sets the API secret for the exchange.
        /// </summary>
        public string ApiSecret
        {
            get => _settings.ApiSecret;
            set
            {
                if (_settings.ApiSecret != value)
                {
                    _settings.ApiSecret = value;
                    OnPropertyChanged(nameof(ApiSecret));
                }
            }
        }

        /// <summary>
        /// Gets or sets the comma-separated list of symbols to subscribe to.
        /// </summary>
        public string Symbols
        {
            get => _settings.Symbols;
            set
            {
                if (_settings.Symbols != value)
                {
                    _settings.Symbols = value;
                    OnPropertyChanged(nameof(Symbols));
                }
            }
        }

        /// <summary>
        /// Gets or sets the number of depth levels for the order book.
        /// </summary>
        public int DepthLevels
        {
            get => _settings.DepthLevels;
            set
            {
                if (_settings.DepthLevels != value)
                {
                    _settings.DepthLevels = value;
                    OnPropertyChanged(nameof(DepthLevels));
                }
            }
        }

        /// <summary>
        /// Gets or sets the aggregation level in milliseconds.
        /// </summary>
        public int AggregationLevel
        {
            get => (int)_settings.AggregationLevel;
            set
            {
                if ((int)_settings.AggregationLevel != value)
                {
                    _settings.AggregationLevel = (VisualHFT.Enums.AggregationLevel)value;
                    OnPropertyChanged(nameof(AggregationLevel));
                }
            }
        }

        /// <summary>
        /// Gets or sets validation messages to display to the user.
        /// </summary>
        public string ValidationMessage
        {
            get => _validationMessage;
            set
            {
                if (_validationMessage != value)
                {
                    _validationMessage = value;
                    OnPropertyChanged(nameof(ValidationMessage));
                }
            }
        }

        /// <summary>
        /// Gets or sets success messages to display to the user.
        /// </summary>
        public string SuccessMessage
        {
            get => _successMessage;
            set
            {
                if (_successMessage != value)
                {
                    _successMessage = value;
                    OnPropertyChanged(nameof(SuccessMessage));
                }
            }
        }

        public PluginSettingsViewModel(Model.PlugInSettings settings, Action closeWindow)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _closeWindow = closeWindow ?? throw new ArgumentNullException(nameof(closeWindow));

            // Initialize commands
            OkCommand = new RelayCommand<object>(ExecuteOkCommand, CanExecuteOkCommand);
            CancelCommand = new RelayCommand<object>(ExecuteCancelCommand);
            TestConnectionCommand = new RelayCommand<object>(ExecuteTestConnectionCommand, CanExecuteTestConnectionCommand);
        }

        #region Command Implementations

        private bool CanExecuteOkCommand(object obj)
        {
            // Enable OK button if there are no validation errors
            return string.IsNullOrEmpty(this[nameof(ApiKey)]) &&
                   string.IsNullOrEmpty(this[nameof(ApiSecret)]) &&
                   string.IsNullOrEmpty(this[nameof(Symbols)]) &&
                   string.IsNullOrEmpty(this[nameof(DepthLevels)]) &&
                   string.IsNullOrEmpty(this[nameof(AggregationLevel)]);
        }

        private void ExecuteOkCommand(object obj)
        {
            // Save settings and close window
            ValidationMessage = string.Empty;
            SuccessMessage = "Settings saved successfully!";
            
            // TODO: Add any additional validation or processing here
            
            _closeWindow?.Invoke();
        }

        private void ExecuteCancelCommand(object obj)
        {
            // Close window without saving
            ValidationMessage = string.Empty;
            SuccessMessage = string.Empty;
            _closeWindow?.Invoke();
        }

        private bool CanExecuteTestConnectionCommand(object obj)
        {
            // Enable test connection if API credentials are provided
            return !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
        }

        private void ExecuteTestConnectionCommand(object obj)
        {
            try
            {
                ValidationMessage = string.Empty;
                SuccessMessage = "Testing connection...";

                // TODO: Implement actual connection test logic here
                // Example:
                // var client = new TemplateExchangeClient(ApiKey, ApiSecret);
                // var isConnected = await client.TestConnectionAsync();
                // 
                // if (isConnected)
                // {
                //     SuccessMessage = "Connection successful!";
                // }
                // else
                // {
                //     ValidationMessage = "Connection failed. Please check your credentials.";
                // }

                // For now, simulate a test
                System.Threading.Tasks.Task.Delay(1000).ContinueWith(t =>
                {
                    if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret))
                    {
                        ValidationMessage = "Please provide valid API credentials.";
                        SuccessMessage = string.Empty;
                    }
                    else
                    {
                        SuccessMessage = "Connection test successful! (Mock)";
                    }
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                ValidationMessage = $"Connection test failed: {ex.Message}";
                SuccessMessage = string.Empty;
            }
        }

        #endregion

        #region IDataErrorInfo Implementation

        public string Error => null;

        public string this[string columnName]
        {
            get
            {
                string result = null;

                switch (columnName)
                {
                    case nameof(ApiKey):
                        if (string.IsNullOrWhiteSpace(ApiKey))
                        {
                            result = "API Key is required when using authenticated endpoints.";
                        }
                        break;

                    case nameof(ApiSecret):
                        if (string.IsNullOrWhiteSpace(ApiSecret))
                        {
                            result = "API Secret is required when using authenticated endpoints.";
                        }
                        break;

                    case nameof(Symbols):
                        if (string.IsNullOrWhiteSpace(Symbols))
                        {
                            result = "At least one symbol is required.";
                        }
                        else
                        {
                            // Validate symbol format
                            var symbols = Symbols.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            if (symbols.Length == 0)
                            {
                                result = "Invalid symbol format. Use comma-separated symbols (e.g., BTC-USD,ETH-USD).";
                            }
                        }
                        break;

                    case nameof(DepthLevels):
                        if (DepthLevels <= 0)
                        {
                            result = "Depth levels must be greater than 0.";
                        }
                        else if (DepthLevels > 1000)
                        {
                            result = "Depth levels cannot exceed 1000.";
                        }
                        break;

                    case nameof(AggregationLevel):
                        if (AggregationLevel < 0)
                        {
                            result = "Aggregation level cannot be negative.";
                        }
                        else if (AggregationLevel > 60000)
                        {
                            result = "Aggregation level cannot exceed 60000 ms (60 seconds).";
                        }
                        break;
                }

                return result;
            }
        }

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            
            // Re-evaluate command states when properties change
            if (propertyName != nameof(ValidationMessage) && propertyName != nameof(SuccessMessage))
            {
                ((RelayCommand<object>)OkCommand).RaiseCanExecuteChanged();
                ((RelayCommand<object>)TestConnectionCommand).RaiseCanExecuteChanged();
            }
        }

        #endregion
    }
}
