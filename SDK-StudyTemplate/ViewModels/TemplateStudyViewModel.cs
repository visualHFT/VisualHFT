using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VisualHFT.Helpers;
using System.Runtime.CompilerServices;

namespace VisualHFT.Studies.Template.ViewModel
{
    /// <summary>
    /// ViewModel for the custom study UI component.
    /// Handles data binding and UI updates for the study visualization.
    /// </summary>
    public class TemplateStudyViewModel : INotifyPropertyChanged
    {
        #region Private Fields
        
        private double _currentValue;
        private Brush _valueColor = Brushes.Gray;
        private Brush _indicatorColor = Brushes.DarkGray;
        private string _currentSymbol = "";
        private string _lastUpdateTime = "";
        private int _updateCount = 0;
        private readonly object _lockObject = new object();
        
        #endregion

        #region Public Properties
        
        public double CurrentValue
        {
            get => _currentValue;
            set
            {
                _currentValue = value;
                OnPropertyChanged();
                
                // Update color based on value
                ValueColor = value >= 0 ? Brushes.LimeGreen : Brushes.Red;
                
                // Update indicator (example: intensity based on value magnitude)
                var intensity = Math.Min(Math.Abs(value) / 100.0, 1.0);
                IndicatorColor = new SolidColorBrush(Color.FromRgb(
                    (byte)(255 * intensity),
                    (byte)(128 * (1 - intensity)),
                    (byte)(128 * (1 - intensity))
                ));
            }
        }

        public Brush ValueColor
        {
            get => _valueColor;
            set
            {
                _valueColor = value;
                OnPropertyChanged();
            }
        }

        public Brush IndicatorColor
        {
            get => _indicatorColor;
            set
            {
                _indicatorColor = value;
                OnPropertyChanged();
            }
        }

        public string CurrentSymbol
        {
            get => _currentSymbol;
            set
            {
                _currentSymbol = value;
                OnPropertyChanged();
            }
        }

        public string LastUpdateTime
        {
            get => _lastUpdateTime;
            set
            {
                _lastUpdateTime = value;
                OnPropertyChanged();
            }
        }

        public int UpdateCount
        {
            get => _updateCount;
            set
            {
                _updateCount = value;
                OnPropertyChanged();
            }
        }

        // Uncomment for chart support
        // public PlotModel ChartModel { get; set; }

        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;
        
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region Constructor
        
        public TemplateStudyViewModel()
        {
            // Initialize default values
            ValueColor = Brushes.Gray;
            IndicatorColor = Brushes.DarkGray;
            LastUpdateTime = DateTime.Now.ToString("HH:mm:ss.fff");
            
            // Initialize chart if needed
            // InitializeChart();
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// Update the view with new study data
        /// </summary>
        /// <param name="value">Calculated study value</param>
        /// <param name="timestamp">Timestamp of the calculation</param>
        public void UpdateValue(double value, DateTime timestamp)
        {
            lock (_lockObject)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    CurrentValue = value;
                    LastUpdateTime = timestamp.ToString("HH:mm:ss.fff");
                    UpdateCount++;
                    
                    // Update chart if needed
                    // UpdateChart(value, timestamp);
                });
            }
        }

        /// <summary>
        /// Clear all data
        /// </summary>
        public void Clear()
        {
            lock (_lockObject)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    CurrentValue = 0;
                    ValueColor = Brushes.Gray;
                    IndicatorColor = Brushes.DarkGray;
                    LastUpdateTime = DateTime.Now.ToString("HH:mm:ss.fff");
                    UpdateCount = 0;
                    
                    // Clear chart if needed
                    // ClearChart();
                });
            }
        }

        #endregion

        #region Private Methods
        
        /*
        // Example chart initialization - uncomment if using OxyPlot
        private void InitializeChart()
        {
            ChartModel = new PlotModel
            {
                Background = OxyColors.Transparent,
                TextColor = OxyColors.White
            };
            
            var lineSeries = new LineSeries
            {
                Color = OxyColors.LimeGreen,
                StrokeThickness = 2,
                MarkerType = MarkerType.None
            };
            
            ChartModel.Series.Add(lineSeries);
        }
        
        private void UpdateChart(double value, DateTime timestamp)
        {
            var lineSeries = ChartModel.Series[0] as LineSeries;
            if (lineSeries != null)
            {
                lineSeries.Points.Add(new DataPoint(
                    DateTimeAxis.ToDouble(timestamp),
                    value));
                
                // Keep only last 100 points
                if (lineSeries.Points.Count > 100)
                {
                    lineSeries.Points.RemoveAt(0);
                }
                
                ChartModel.InvalidatePlot(true);
            }
        }
        
        private void ClearChart()
        {
            var lineSeries = ChartModel.Series[0] as LineSeries;
            if (lineSeries != null)
            {
                lineSeries.Points.Clear();
                ChartModel.InvalidatePlot(true);
            }
        }
        */
        
        #endregion
    }
}
