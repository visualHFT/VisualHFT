using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using VisualHFT.Helpers;

namespace MarketConnectors.Binance.ViewModel
{
    public class PluginSettingsViewModel : INotifyPropertyChanged, IDataErrorInfo
    {
        private string _apiKey;
        private string _apiSecret;
        private int _depthLevels;
        private bool _isNonUS;
        private int _updateIntervalMs;
        private int _providerId;
        private string _providerName;
        private List<string> _symbols;
        private string _symbolsText;
        private List<string> _futuresSymbols;
        private string _futuresSymbolsText;
        private string _validationMessage;
        private string _successMessage;
        private Action _actionCloseWindow;
        public ICommand OkCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
        public Action UpdateSettingsFromUI { get; set; }

        public PluginSettingsViewModel(Action actionCloseWindow)
        {
            OkCommand = new RelayCommand<object>(ExecuteOkCommand, CanExecuteOkCommand);
            CancelCommand = new RelayCommand<object>(ExecuteCancelCommand);
            _actionCloseWindow = actionCloseWindow;
        }


        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value; OnPropertyChanged(nameof(ApiKey)); }
        }
        public string ApiSecret
        {
            get => _apiSecret;
            set { _apiSecret = value; OnPropertyChanged(nameof(ApiSecret)); }
        }
        public string SymbolsText
        {
            get => string.Join(",", Symbols);
            set
            {
                Symbols = value.Split(',').Select(s => s.Trim()).ToList();
                OnPropertyChanged(nameof(SymbolsText));
                OnPropertyChanged(nameof(Symbols));
                RaiseCanExecuteChanged();
            }
        }


        public List<string> Symbols
        {
            get => _symbols;
            set
            {
                _symbols = value;
                OnPropertyChanged(nameof(Symbols));
                RaiseCanExecuteChanged();
            }
        }

        public string FuturesSymbolsText
        {
            get => FuturesSymbols == null ? "" : string.Join(",", FuturesSymbols);
            set
            {
                FuturesSymbols = string.IsNullOrWhiteSpace(value)
                    ? new List<string>()
                    : value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                OnPropertyChanged(nameof(FuturesSymbolsText));
                OnPropertyChanged(nameof(FuturesSymbols));
                RaiseCanExecuteChanged();
            }
        }

        public List<string> FuturesSymbols
        {
            get => _futuresSymbols;
            set
            {
                _futuresSymbols = value ?? new List<string>();
                OnPropertyChanged(nameof(FuturesSymbols));
                OnPropertyChanged(nameof(FuturesSymbolsText));
                RaiseCanExecuteChanged();
            }
        }

        public int DepthLevels
        {
            get => _depthLevels;
            set
            {
                _depthLevels = value;
                OnPropertyChanged(nameof(DepthLevels));
                RaiseCanExecuteChanged();
            }
        }
        public bool IsNonUS
        {
            get => _isNonUS;
            set
            {
                _isNonUS = value;
                OnPropertyChanged(nameof(IsNonUS));
                RaiseCanExecuteChanged();
            }
        }

        public int UpdateIntervalMs
        {
            get => _updateIntervalMs;
            set
            {
                _updateIntervalMs = value;
                OnPropertyChanged(nameof(UpdateIntervalMs));
                RaiseCanExecuteChanged();
            }
        }

        public int ProviderId
        {
            get => _providerId;
            set
            {
                _providerId = value;
                OnPropertyChanged(nameof(ProviderId));
                RaiseCanExecuteChanged();
            }
        }

        public string ProviderName
        {
            get => _providerName;
            set
            {
                _providerName = value;
                OnPropertyChanged(nameof(ProviderName));
                RaiseCanExecuteChanged();
            }
        }

        public string ValidationMessage
        {
            get { return _validationMessage; }
            set { _validationMessage = value; OnPropertyChanged(nameof(ValidationMessage)); }
        }

        public string SuccessMessage
        {
            get { return _successMessage; }
            set { _successMessage = value; OnPropertyChanged(nameof(SuccessMessage)); }
        }
        public string Error => null;

        public string this[string columnName]
        {
            get
            {
                switch (columnName)
                {
                    /*case nameof(ApiKey):
                        if (string.IsNullOrWhiteSpace(ApiKey))
                            return "API Key cannot be empty.";
                        break;

                    case nameof(ApiSecret):
                        if (string.IsNullOrWhiteSpace(ApiSecret))
                            return "API Secret cannot be empty.";
                        break;*/

                    case nameof(SymbolsText):
                        if (Symbols != null && Symbols.Any(s => string.IsNullOrWhiteSpace(s)))
                            return "Symbols should be comma-separated.";
                        if ((Symbols == null || !Symbols.Any()) && (FuturesSymbols == null || !FuturesSymbols.Any()))
                            return "Enter at least one spot or USD-M futures symbol.";
                        break;

                    case nameof(FuturesSymbolsText):
                        if (FuturesSymbols != null && FuturesSymbols.Any(s => string.IsNullOrWhiteSpace(s)))
                            return "Futures symbols should be comma-separated.";
                        break;

                    case nameof(DepthLevels):
                        if (DepthLevels <= 0)
                            return "Depth levels should be a positive integer.";
                        break;

                    case nameof(UpdateIntervalMs):
                        if (UpdateIntervalMs <= 0)
                            return "Update interval should be a positive integer.";
                        break;

                    case nameof(ProviderId):
                        if (ProviderId <= 0)
                            return "Provider ID should be a positive integer.";
                        break;

                    case nameof(ProviderName):
                        if (string.IsNullOrWhiteSpace(ProviderName))
                            return "Provider name cannot be empty.";
                        break;

                    default:
                        return null;
                }
                return null;
            }
        }

        private void ExecuteOkCommand(object obj)
        {
            SuccessMessage = "Settings saved successfully!";
            UpdateSettingsFromUI?.Invoke();
            _actionCloseWindow?.Invoke();
        }
        private void ExecuteCancelCommand(object obj)
        {
            _actionCloseWindow?.Invoke();
        }
        private bool CanExecuteOkCommand(object obj)
        {
            // This checks if any validation message exists for any of the properties
            return string.IsNullOrWhiteSpace(this[nameof(ApiKey)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(ApiSecret)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(SymbolsText)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(FuturesSymbolsText)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(DepthLevels)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(UpdateIntervalMs)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(ProviderId)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(ProviderName)]);
        }
        private void RaiseCanExecuteChanged()
        {
            (OkCommand as RelayCommand<object>)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

    }

}
