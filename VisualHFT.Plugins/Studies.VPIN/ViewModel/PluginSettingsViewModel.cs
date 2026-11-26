using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.ViewModel.Model;

namespace VisualHFT.Studies.VPIN.ViewModel
{
    public class PluginSettingsViewModel : INotifyPropertyChanged, IDataErrorInfo
    {
        private double _bucketVolSize;
        private int _numberOfBuckets;
        private ObservableCollection<Provider> _providers;
        private ObservableCollection<string> _symbols;
        private VisualHFT.ViewModel.Model.Provider _selectedProvider;
        private int? _selectedProviderID;
        private string _selectedSymbol;
        private AggregationLevel _aggregationLevelSelection;
        private bool _useCorrectedFormSelection;
        private bool _autoBucketVolumeSelection;


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

            _symbols = new ObservableCollection<string>(HelperSymbol.Instance);
            _providers = Provider.CreateObservableCollection();
            OnPropertyChanged(nameof(Providers));
            OnPropertyChanged(nameof(Symbols));

            HelperProvider.Instance.OnDataReceived += PROVIDERS_OnDataReceived;
            HelperSymbol.Instance.OnCollectionChanged += ALLSYMBOLS_CollectionChanged;


            AggregationLevels = new ObservableCollection<Tuple<string, AggregationLevel>>();
            foreach (AggregationLevel level in Enum.GetValues(typeof(AggregationLevel)))
            {
                AggregationLevels.Add(new Tuple<string, AggregationLevel>(Commons.Helpers.HelperCommon.GetEnumDescription(level), level));
            }

            LoadSelectedProviderID();
        }


        public ObservableCollection<VisualHFT.ViewModel.Model.Provider> Providers { get => _providers; set => _providers = value; }
        public ObservableCollection<string> Symbols { get => _symbols; set => _symbols = value; }

        public int? SelectedProviderID
        {
            get { return _selectedProviderID; }
            set
            {
                _selectedProviderID = value;
                OnPropertyChanged(nameof(SelectedProviderID));
                RaiseCanExecuteChanged();
                LoadSelectedProviderID();
            }
        }
        public double BucketVolumeSize
        {
            get => _bucketVolSize;
            set
            {
                _bucketVolSize = value;
                OnPropertyChanged(nameof(BucketVolumeSize));
                RaiseCanExecuteChanged();
            }
        }
        public int NumberOfBuckets
        {
            get => _numberOfBuckets;
            set
            {
                _numberOfBuckets = value;
                OnPropertyChanged(nameof(NumberOfBuckets));
                RaiseCanExecuteChanged();
            }
        }
        public VisualHFT.ViewModel.Model.Provider SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                _selectedProvider = value;
                OnPropertyChanged(nameof(SelectedProvider));
                RaiseCanExecuteChanged();
                LoadSelectedProviderID();
            }
        }
        public string SelectedSymbol
        {
            get => _selectedSymbol;
            set
            {
                _selectedSymbol = value;
                OnPropertyChanged(nameof(SelectedSymbol));
                RaiseCanExecuteChanged();
            }
        }

        public AggregationLevel AggregationLevelSelection
        {
            get => _aggregationLevelSelection;
            set
            {
                _aggregationLevelSelection = value;
                OnPropertyChanged(nameof(AggregationLevelSelection));
            }
        }
        public ObservableCollection<Tuple<string, AggregationLevel>> AggregationLevels { get; set; }

        /// <summary>
        /// Selects the corrected form: tick-rule classification, a full window before any value
        /// appears, and no value at all while the bucket volume is too small to hold enough trades.
        /// </summary>
        public bool UseCorrectedFormSelection
        {
            get => _useCorrectedFormSelection;
            set
            {
                _useCorrectedFormSelection = value;
                RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(UseCorrectedFormSelection));
            }
        }


        /// <summary>
        /// Sizes the bucket automatically: 20 times the volume-weighted average size of the first 10,000 trades, fixed for
        /// the run. While it is on, the Bucket Volume Size box is not used and may be left at zero.
        /// </summary>
        public bool AutoBucketVolumeSelection
        {
            get => _autoBucketVolumeSelection;
            set
            {
                _autoBucketVolumeSelection = value;
                OnPropertyChanged(nameof(AutoBucketVolumeSelection));
                OnPropertyChanged(nameof(IsBucketVolumeEditable));
                OnPropertyChanged(nameof(BucketVolumeSize)); // re-runs its validation
                RaiseCanExecuteChanged();
            }
        }

        public bool IsBucketVolumeEditable => !_autoBucketVolumeSelection;


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
                    case nameof(BucketVolumeSize):
                        if (!AutoBucketVolumeSelection && BucketVolumeSize <= 0)
                            return "Bucket Volume Size must be greater than zero.";
                        break;
                    case nameof(NumberOfBuckets):
                        if (NumberOfBuckets <= 0)
                            return "Number of Buckets must be greater than zero.";
                        break;

                    case nameof(SelectedProvider):
                        if (SelectedProvider == null)
                            return "Select the Provider.";
                        break;
                    case nameof(SelectedSymbol):
                        if (string.IsNullOrWhiteSpace(SelectedSymbol))
                            return "Select the Symbol.";
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
            return string.IsNullOrWhiteSpace(this[nameof(BucketVolumeSize)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(NumberOfBuckets)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(SelectedProvider)]) &&
                   string.IsNullOrWhiteSpace(this[nameof(SelectedSymbol)]);

        }
        private void RaiseCanExecuteChanged()
        {
            (OkCommand as RelayCommand<object>)?.RaiseCanExecuteChanged();
        }
        private void LoadSelectedProviderID()
        {
            if (_selectedProvider != null)
            {
                _selectedProviderID = _selectedProvider.ProviderID;
                OnPropertyChanged(nameof(SelectedSymbol));
            }
            else if (_selectedProviderID.HasValue && _providers.Any())
            {
                _selectedProvider = _providers.FirstOrDefault(x => x.ProviderID == _selectedProviderID.Value);
                OnPropertyChanged(nameof(SelectedProvider));
            }
        }

        private void ALLSYMBOLS_CollectionChanged(object? sender, string e)
        {
            _symbols = new ObservableCollection<string>(HelperSymbol.Instance);
            OnPropertyChanged(nameof(Symbols));
        }
        private void PROVIDERS_OnDataReceived(object? sender, VisualHFT.Model.Provider e)
        {
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var item = new VisualHFT.ViewModel.Model.Provider(e);
                if (!_providers.Any(x => x.ProviderCode == e.ProviderCode))
                    _providers.Add(item);
                if (_selectedProvider == null && e.Status == eSESSIONSTATUS.CONNECTED) //default provider must be the first who's Active
                    SelectedProvider = item;
            }));
        }


        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

    }

}
