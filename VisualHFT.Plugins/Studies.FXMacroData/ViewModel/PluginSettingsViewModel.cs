using System;
using System.ComponentModel;
using System.Windows.Input;
using VisualHFT.Helpers;

namespace VisualHFT.Studies.FXMacroData.ViewModel
{
    public class PluginSettingsViewModel : INotifyPropertyChanged, IDataErrorInfo
    {
        private readonly Action _closeWindow;
        private int _minutesBeforeRelease;
        private int _minutesAfterRelease;
        private int _refreshIntervalMinutes;
        private string _validationMessage = string.Empty;

        public PluginSettingsViewModel(Action closeWindow)
        {
            _closeWindow = closeWindow;
            OkCommand = new RelayCommand<object>(ExecuteOkCommand, CanExecuteOkCommand);
            CancelCommand = new RelayCommand<object>(ExecuteCancelCommand);
        }

        public ICommand OkCommand { get; }
        public ICommand CancelCommand { get; }
        public Action? UpdateSettingsFromUI { get; set; }

        public int MinutesBeforeRelease
        {
            get => _minutesBeforeRelease;
            set
            {
                _minutesBeforeRelease = value;
                OnPropertyChanged(nameof(MinutesBeforeRelease));
                RaiseCanExecuteChanged();
            }
        }

        public int MinutesAfterRelease
        {
            get => _minutesAfterRelease;
            set
            {
                _minutesAfterRelease = value;
                OnPropertyChanged(nameof(MinutesAfterRelease));
                RaiseCanExecuteChanged();
            }
        }

        public int RefreshIntervalMinutes
        {
            get => _refreshIntervalMinutes;
            set
            {
                _refreshIntervalMinutes = value;
                OnPropertyChanged(nameof(RefreshIntervalMinutes));
                RaiseCanExecuteChanged();
            }
        }

        public string ValidationMessage
        {
            get => _validationMessage;
            set
            {
                _validationMessage = value;
                OnPropertyChanged(nameof(ValidationMessage));
            }
        }

        public string Error => string.Empty;

        public string this[string columnName]
        {
            get
            {
                return columnName switch
                {
                    nameof(MinutesBeforeRelease) when MinutesBeforeRelease < 0 => "Minutes before release cannot be negative.",
                    nameof(MinutesAfterRelease) when MinutesAfterRelease < 0 => "Minutes after release cannot be negative.",
                    nameof(RefreshIntervalMinutes) when RefreshIntervalMinutes is < 1 or > 60 => "Refresh interval must be between 1 and 60 minutes.",
                    _ => string.Empty
                };
            }
        }

        private void ExecuteOkCommand(object? obj)
        {
            UpdateSettingsFromUI?.Invoke();
            _closeWindow?.Invoke();
        }

        private void ExecuteCancelCommand(object? obj)
        {
            _closeWindow?.Invoke();
        }

        private bool CanExecuteOkCommand(object? obj)
        {
            var errors = new[]
            {
                this[nameof(MinutesBeforeRelease)],
                this[nameof(MinutesAfterRelease)],
                this[nameof(RefreshIntervalMinutes)]
            };
            ValidationMessage = Array.Find(errors, x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
            return string.IsNullOrEmpty(ValidationMessage);
        }

        private void RaiseCanExecuteChanged()
        {
            (OkCommand as RelayCommand<object>)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
