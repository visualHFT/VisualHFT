using Prism.Mvvm;
using System;
using System.Linq;
using System.Windows.Input;
using System.Windows;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.ViewModels;
using System.Windows.Media;
using VisualHFT.Commons.Studies;
using VisualHFT.View;
using System.Collections.ObjectModel;
using System.Windows.Controls;

namespace VisualHFT.ViewModel
{
    public class vmTile : BindableBase, IDisposable
    {
        private bool _disposed = false; // to track whether the object has been disposed
        private string _tile_id;
        private string _title;
        private string _tileToolTip;
        private bool _isGroup;
        private bool _isUserControl;
        private bool _DATA_AVAILABLE = false;
        private UIUpdater uiUpdater;
        private const int UI_UPDATE_TIME_MS = 500;

        private System.Windows.Visibility _settingButtonVisibility;
        private System.Windows.Visibility _chartButtonVisibility;
        private System.Windows.Visibility _valueVisibility = Visibility.Visible;
        private System.Windows.Visibility _ucVisibility = Visibility.Hidden;

        //*********************************************************
        //*********************************************************
        private IStudy _study;
        private IMultiStudy _multiStudy;
        private PluginManager.IPlugin _plugin;
        //*********************************************************
        //*********************************************************

        private string _value;
        private string _valueTooltip;
        private SolidColorBrush _valueColor = Brushes.White;
        private UserControl _customControl;
        private BaseStudyModel _localModel = new BaseStudyModel();
        private readonly object _lock = new object();
        public vmTile(IStudy study)
        {
            IsGroup = false;

            _study = study;
            _customControl = _study.GetCustomUI() as UserControl;
            IsUserControl = _customControl != null;
            _tile_id = ((PluginManager.IPlugin)_study).GetPluginUniqueID();
            Title = _study.TileTitle;
            Tooltip = _study.TileToolTip;

            _localModel.Tooltip = "Waiting for data...";

            _study.OnCalculated += _study_OnCalculated;

            OpenSettingsCommand = new RelayCommand<vmTile>(OpenSettings);
            OpenChartCommand = new RelayCommand<vmTile>(OpenChartClick);
            uiUpdater = new UIUpdater(uiUpdaterAction, UI_UPDATE_TIME_MS);

            if (IsUserControl)
            {
                IsGroup = true;
                ValueVisibility = Visibility.Hidden;
                UCVisibility = Visibility.Visible;

                OpenSettingsCommand = new RelayCommand<vmTile>(OpenSettings);
            }
            RaisePropertyChanged(nameof(SelectedSymbol));
            RaisePropertyChanged(nameof(SelectedProviderName));
            RaisePropertyChanged(nameof(IsGroup));
            SettingButtonVisibility = Visibility.Visible;
            ChartButtonVisibility = Visibility.Visible;
        }
        public vmTile(IMultiStudy multiStudy)
        {
            IsGroup = true;

            _multiStudy = multiStudy;
            ChildTiles = new ObservableCollection<vmTile>();
            foreach (var study in _multiStudy.Studies)
            {
                ChildTiles.Add(new vmTile(study) { SettingButtonVisibility = Visibility.Hidden, ChartButtonVisibility = Visibility.Hidden });
            }

            _tile_id = ((PluginManager.IPlugin)_multiStudy).GetPluginUniqueID();
            Title = _multiStudy.TileTitle;
            Tooltip = _multiStudy.TileToolTip;

            _localModel.Tooltip = "Waiting for data...";

            OpenSettingsCommand = new RelayCommand<vmTile>(OpenSettings);
            OpenChartCommand = new RelayCommand<vmTile>(OpenChartClick);
            uiUpdater = new UIUpdater(uiUpdaterAction, UI_UPDATE_TIME_MS);


            RaisePropertyChanged(nameof(SelectedSymbol));
            RaisePropertyChanged(nameof(SelectedProviderName));
            RaisePropertyChanged(nameof(IsGroup));
            SettingButtonVisibility = Visibility.Hidden;
            ChartButtonVisibility = Visibility.Hidden;
        }
        public vmTile(PluginManager.IPlugin plugin)
        {
            IsGroup = false;

            _plugin = plugin;
            _customControl = _plugin.GetCustomUI() as UserControl;
            IsUserControl = _customControl != null;

            _tile_id = _plugin.GetPluginUniqueID();
            Title = _plugin.Name;
            Tooltip = _plugin.Description;

            if (IsUserControl)
            {
                IsGroup = true;
                ValueVisibility = Visibility.Hidden;
                UCVisibility = Visibility.Visible;

                OpenSettingsCommand = new RelayCommand<vmTile>(OpenSettings);
            }
            RaisePropertyChanged(nameof(SelectedSymbol));
            RaisePropertyChanged(nameof(SelectedProviderName));
            RaisePropertyChanged(nameof(IsGroup));
            SettingButtonVisibility = Visibility.Hidden;
            ChartButtonVisibility = Visibility.Hidden;

        }


        private void _study_OnCalculated(object? sender, BaseStudyModel e)
        {
            /*
             * ***************************************************************************************************
             * TRANSFORM the incoming object (decouple it)
             * DO NOT hold this call back, since other components depends on the speed of this specific call back.
             * DO NOT BLOCK
             * IDEALLY, USE QUEUES TO DECOUPLE
             * ***************************************************************************************************
             */

            lock (_lock)
            {
                if (e.Value == _localModel.Value
                    && e.MarketMidPrice == _localModel.MarketMidPrice
                    && e.ValueColor == _localModel.ValueColor
                    )
                    return; //return if nothing has changed

                _localModel.copyFrom(e);
            }


            if (!_localModel.HasData && !_localModel.HasError && !_localModel.IsStale)
                _localModel.Tooltip = "Waiting for data...";
            else if (!string.IsNullOrEmpty(e.Tooltip))
                _localModel.Tooltip = e.Tooltip;
            else
                _localModel.Tooltip = null;

            _DATA_AVAILABLE = true;
        }


        ~vmTile() { Dispose(false); }

        // In uiUpdaterAction - format on demand:
        private void uiUpdaterAction()
        {
            if (_localModel == null || !_DATA_AVAILABLE)
                return;
            lock (_lock)
            {
                Value = GetDisplayValue(_localModel);
                ValueTooltip = _localModel.Tooltip;

                if (_localModel.ValueColor != null)
                {
                    if (_valueColor == null || _valueColor.ToString() != _localModel.ValueColor)
                        ValueColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_localModel.ValueColor));
                }

                _DATA_AVAILABLE = true;
            }
        }
        /// <summary>
        /// Gets the display value based on model state.
        /// Formatting happens here (UI layer) only when needed.
        /// </summary>
        private static string GetDisplayValue(BaseStudyModel model)
        {
            // Priority: Error > Stale > No Data > Normal
            if (model.HasError)
                return "Err";

            if (model.IsStale)
                return "...";

            if (!model.HasData)
                return ".";

            // Normal case: format the value
            if (model.CustomFormatter != null)
                return model.CustomFormatter(model.Value);

            if (string.IsNullOrEmpty(model.Format))
                return model.Value.ToString();

            return model.Value.ToString(model.Format);
        }


        public void UpdateAllUI()
        {
            _DATA_AVAILABLE = true;
            uiUpdaterAction();
            RaisePropertyChanged(nameof(SelectedSymbol));
            RaisePropertyChanged(nameof(SelectedProviderName));
        }

        public ICommand OpenSettingsCommand { get; set; }
        public ICommand OpenChartCommand { get; private set; }

        public string Value { get => _value; set => SetProperty(ref _value, value); }
        public string ValueTooltip { get => _valueTooltip; set => SetProperty(ref _valueTooltip, value); }
        public SolidColorBrush ValueColor { get => _valueColor; set => SetProperty(ref _valueColor, value); }
        public string Title { get => _title; set => SetProperty(ref _title, value); }
        public string PluginID
        {
            get => _tile_id;
            set => SetProperty(ref _tile_id, value); // raises PropertyChanged
        }
        public string Tooltip { get => _tileToolTip; set => SetProperty(ref _tileToolTip, value); }
        public string SelectedSymbol
        {
            get
            {
                if (_study != null)
                    return ((VisualHFT.PluginManager.IPlugin)_study).Settings.Symbol;
                else if (_multiStudy != null)
                    return ((VisualHFT.PluginManager.IPlugin)_multiStudy).Settings.Symbol;
                else if (_customControl != null && _plugin != null)
                    return _plugin.Settings.Symbol;
                else
                    return "";
            }
        }
        public string SelectedProviderName
        {
            get
            {
                if (_study != null)
                    return ((VisualHFT.PluginManager.IPlugin)_study).Settings.Provider.ProviderName;
                else if (_multiStudy != null)
                    return ((VisualHFT.PluginManager.IPlugin)_multiStudy).Settings.Provider.ProviderName;
                else if (_customControl != null && _plugin != null)
                    return _plugin.Settings.Provider.ProviderName;
                else
                    return "";
            }
        }
        public bool IsGroup { get => _isGroup; set => SetProperty(ref _isGroup, value); }
        public bool IsUserControl { get => _isUserControl; set => SetProperty(ref _isUserControl, value); }

        public System.Windows.Visibility SettingButtonVisibility
        {
            get { return _settingButtonVisibility; }
            set { SetProperty(ref _settingButtonVisibility, value); }
        }
        public System.Windows.Visibility ChartButtonVisibility
        {
            get { return _chartButtonVisibility; }
            set { SetProperty(ref _chartButtonVisibility, value); }
        }
        public UserControl CustomControl
        {
            get => _customControl;
            set => SetProperty(ref _customControl, value);
        }
        public System.Windows.Visibility UCVisibility
        {
            get { return _ucVisibility; }
            set { SetProperty(ref _ucVisibility, value); }
        }
        public System.Windows.Visibility ValueVisibility
        {
            get { return _valueVisibility; }
            set { SetProperty(ref _valueVisibility, value); }
        }
        public ObservableCollection<vmTile> ChildTiles { get; set; }

        private void OpenChartClick(object obj)
        {
            if (_study != null)
            {
                var winChart = new ChartStudy();
                winChart.DataContext = new vmChartStudy(_study);
                winChart.Show();
            }
            else if (_multiStudy != null)
            {
                var winChart = new ChartStudy();
                winChart.DataContext = new vmChartStudy(_multiStudy);
                winChart.Show();
            }
        }
        private void OpenSettings(object obj)
        {
            if (_study != null)
                PluginManager.PluginManager.SettingPlugin((PluginManager.IPlugin)_study);
            else if (_multiStudy != null)
            {
                PluginManager.PluginManager.SettingPlugin((PluginManager.IPlugin)_multiStudy);
                foreach (var child in ChildTiles)
                {
                    child.UpdateAllUI();
                }
            }
            else if (_plugin != null)
            {
                PluginManager.PluginManager.SettingPlugin(_plugin);
            }
            RaisePropertyChanged(nameof(SelectedSymbol));
            RaisePropertyChanged(nameof(SelectedProviderName));

        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        // Stop and dispose study plugins
                        if (_study != null)
                        {
                            _study.OnCalculated -= _study_OnCalculated;
                            //_study.StopAsync();// IMPORTANT: Stopping is handled by PluginManager
                            //_study.Dispose(); // IMPORTANT: Do not dispose study, as it is managed by PluginManager
                        }

                        // Dispose multi-study and all child tiles
                        if (_multiStudy != null)
                        {
                            // Dispose child tiles first
                            if (ChildTiles != null)
                            {
                                foreach (var childTile in ChildTiles.ToList())
                                {
                                    try
                                    {
                                        childTile?.Dispose();
                                    }
                                    catch (Exception ex)
                                    {
                                        // Log but continue disposing other children
                                        System.Diagnostics.Debug.WriteLine($"Error disposing child tile: {ex.Message}");
                                    }
                                }
                                ChildTiles.Clear();
                                ChildTiles = null;
                            }

                            // Dispose the multi-study itself
                            foreach (var study in _multiStudy.Studies)
                            {
                                try
                                {
                                    study.StopAsync();
                                    study.Dispose();
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Error disposing study: {ex.Message}");
                                }
                            }
                            _multiStudy.Dispose();
                            _multiStudy = null;
                        }

                        // Dispose plugin
                        if (_plugin != null)
                        {
                            // Note: Plugin disposal handled by PluginManager
                            _plugin = null;
                        }

                        // Dispose UI updater
                        if (uiUpdater != null)
                        {
                            uiUpdater.Dispose();
                            uiUpdater = null;
                        }

                        // Clear UI references
                        _customControl = null;
                        _localModel = null;

                        // Clear command references
                        OpenSettingsCommand = null;
                        OpenChartCommand = null;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error during vmTile disposal: {ex.Message}");
                    }
                }
                _disposed = true;
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
