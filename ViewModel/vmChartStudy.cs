using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Input;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Pools;
using VisualHFT.Commons.Studies;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.UserSettings;
using VisualHFT.ViewModel;
using DateTimeAxis = OxyPlot.Axes.DateTimeAxis;
using Legend = OxyPlot.Legends.Legend;
using LinearAxis = OxyPlot.Axes.LinearAxis;
using LineSeries = OxyPlot.Series.LineSeries;


namespace VisualHFT.ViewModels
{
    public class vmChartStudy : BindableBase, IDisposable
    {
        private bool _disposed = false; // to track whether the object has been disposed
        private List<IStudy> _studies = new List<IStudy>();
        private ISetting _settings;
        private PluginManager.IPlugin _plugin;

        HelperCustomQueue<BaseStudyModel> _QUEUE;
        private Dictionary<string, AggregatedCollection<PlotInfo>> _dataByStudy;
        private Dictionary<string, OxyPlot.Series.LineSeries> _seriesByStudy;
        private OxyPlot.Series.LineSeries _seriesMarket;
        private bool _IS_DATA_AVAILABLE = false;

        private DateTimeAxis xAxe = null;
        private LinearAxis yAxe = null;
        private double _lastMarketMidPrice = 0;

        private const int _MAX_ITEMS = 1500;
        private UIUpdater uiUpdater;
        private readonly object _LOCK = new object();
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly CustomObjectPool<PlotInfo> _plotInfoPool =
            new CustomObjectPool<PlotInfo>(maxPoolSize: _MAX_ITEMS * 10);

        // Dynamic Y-Axis Support (Feature Flag)
        private readonly bool _enableDynamicYAxes = true; // Set to false to disable
        private Dictionary<string, LinearAxis> _dynamicYAxes = new Dictionary<string, LinearAxis>();
        private Dictionary<string, string> _seriesToAxisMap = new Dictionary<string, string>();
        private Dictionary<string, (double min, double max)> _axisRanges = new Dictionary<string, (double, double)>();
        private int _axisCounter = 0;
        private const double RANGE_THRESHOLD = 100.0; // Create new axis if range differs by 10x

        public vmChartStudy(IStudy study)
        {
            _QUEUE = new HelperCustomQueue<BaseStudyModel>($"<BaseStudyModel>_{study.TileTitle}", QUEUE_onReadAction, QUEUE_onErrorAction);
            _studies.Add(study);
            _settings = ((PluginManager.IPlugin)study).Settings;
            _plugin = (PluginManager.IPlugin)study;
            StudyAxisTitle = study.TileTitle;

            OpenSettingsCommand = new RelayCommand<vmTile>(OpenSettings);
            CreatePlotModel();
            InitializeData();
            RaisePropertyChanged(nameof(StudyAxisTitle));

            // Defer UIUpdater start to ensure OxyPlot has initialized
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_LOCK)
                {
                    if (!_disposed)
                    {
                        uiUpdater = new UIUpdater(uiUpdaterAction);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        public vmChartStudy(IMultiStudy multiStudy)
        {
            _QUEUE = new HelperCustomQueue<BaseStudyModel>($"<BaseStudyModel>_{multiStudy.TileTitle}", QUEUE_onReadAction, QUEUE_onErrorAction);
            foreach (var study in multiStudy.Studies)
            {
                _studies.Add(study);
            }
            _settings = ((PluginManager.IPlugin)multiStudy).Settings;
            _plugin = (PluginManager.IPlugin)multiStudy;
            StudyAxisTitle = multiStudy.TileTitle;

            OpenSettingsCommand = new RelayCommand<vmTile>(OpenSettings);
            CreatePlotModel();
            InitializeData();
            RaisePropertyChanged(nameof(StudyAxisTitle));

            // Defer UIUpdater start to ensure OxyPlot has initialized
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_LOCK)
                {
                    if (!_disposed)
                    {
                        uiUpdater = new UIUpdater(uiUpdaterAction);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);

        }

        ~vmChartStudy()
        {
            Dispose(false);
        }

        public string StudyTitle { get; set; }
        public string StudyAxisTitle { get; set; }
        public ICommand OpenSettingsCommand { get; set; }
        public OxyPlot.PlotModel MyPlotModel { get; set; }

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

            var newModel = new BaseStudyModel();
            newModel.copyFrom(e);
            if (string.IsNullOrEmpty(newModel.Tag))
                newModel.Tag = ((IStudy)sender).TileTitle;
            _QUEUE.Add(newModel);
        }

        private void QUEUE_onReadAction(BaseStudyModel item)
        {
            try
            {
                lock (_LOCK)
                {
                    // Check if we're disposed or clearing
                    if (_disposed || _seriesMarket == null || _dataByStudy == null || _seriesByStudy == null)
                        return;

                    string keyTitle = item.Tag;

                    if (!_dataByStudy.ContainsKey(keyTitle))
                    {
                        CreateNewSerie(keyTitle, OxyColors.Automatic);
                    }

                    if (item.MarketMidPrice > 0)
                        _lastMarketMidPrice = (double)item.MarketMidPrice;

                    // Reuse PlotInfo if possible (avoid allocation)
                    var pointToAdd = _plotInfoPool.Get();
                    pointToAdd.Date = item.Timestamp;
                    pointToAdd.Value = (double)item.Value;

                    bool isAddSuccess = _dataByStudy[keyTitle].Add(pointToAdd);

                    // If successfully added, proceed with adding it into the series
                    if (isAddSuccess)
                    {
                        double oaDate = pointToAdd.Date.ToOADate();

                        if (item.IsIndependentMetric)
                        {
                            // Update only the matching series for independent metrics
                            UpdateIndependentSeries(keyTitle, oaDate, pointToAdd, item);
                        }
                        else
                        {
                            // Update all series for synchronized metrics (backward compatible)
                            UpdateAllSynchronizedSeries(keyTitle, oaDate, pointToAdd, item);
                        }
                    }
                    else
                    {
                        _plotInfoPool.Return(pointToAdd);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error processing chart data for series {item.Tag}: {ex.Message}", ex);
                HelperNotificationManager.Instance.AddNotification(this.StudyTitle, $"Error processing chart data for series {item.Tag}: {ex.Message}", HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.CORE);
            }
        }
        private void QUEUE_onErrorAction(Exception ex)
        {
            var _error = $"{this.StudyTitle} Unhandled error in the Chart queue: {ex.Message}";
            log.Error(_error, ex);
            HelperNotificationManager.Instance.AddNotification(this.StudyTitle, _error, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.CORE);
        }

        private void UpdateIndependentSeries(string keyTitle, double oaDate, PlotInfo pointToAdd, BaseStudyModel item)
        {
            // Only update the series that matches this key
            if (_seriesByStudy.ContainsKey(keyTitle))
            {
                var series = _seriesByStudy[keyTitle];

                // Handle dynamic Y-axis assignment ONLY for independent metrics (like PerformanceCounters)
                // Regular multi-study plugins keep using the default yAxe
                if (_enableDynamicYAxes && item.IsIndependentMetric)
                {
                    if (series.YAxisKey == "yAxe")
                    {
                        // First data point for independent metric - assign proper axis
                        string axisKey = GetOrCreateAxisForSeries(keyTitle, pointToAdd.Value);

                        // Double-check the axis exists in the plot model
                        if (MyPlotModel.Axes.Any(a => a.Key == axisKey))
                        {
                            series.YAxisKey = axisKey;
                        }
                        else
                        {
                            // Fallback to default axis if something went wrong
                            series.YAxisKey = "yAxe";
                            log.Warn($"Dynamic axis {axisKey} not found, using default axis for series {keyTitle}");
                        }
                    }
                    else
                    {
                        // Check if value exceeds current axis range and update if needed
                        if (_dynamicYAxes.TryGetValue(series.YAxisKey, out var currentAxis))
                        {
                            double value = pointToAdd.Value;
                            if (value > currentAxis.ActualMaximum * 0.9) // Approaching max
                            {
                                currentAxis.Maximum = value * 1.5; // Expand range
                                _axisRanges[series.YAxisKey] = (_axisRanges[series.YAxisKey].min, value);
                            }
                            else if (value < currentAxis.ActualMinimum * 1.1) // Approaching min
                            {
                                currentAxis.Minimum = value * 0.8; // Expand range
                                _axisRanges[series.YAxisKey] = (value, _axisRanges[series.YAxisKey].max);
                            }
                        }
                    }
                }

                // Add point with error handling
                try
                {
                    series.Points.Add(new DataPoint(oaDate, pointToAdd.Value));
                    if (series.Points.Count > _MAX_ITEMS)
                        series.Points.RemoveAt(0);
                }
                catch (Exception ex)
                {
                    // Log error but don't crash - skip this point
                    log.Warn($"Failed to add point to series {keyTitle}: Value={pointToAdd.Value}, Axis={series.YAxisKey}, Error={ex.Message}");
                }
            }

            // Still update market price if available
            if (_seriesMarket != null && item.MarketMidPrice > 0)
            {
                _seriesMarket.Points.Add(new DataPoint(oaDate, (double)item.MarketMidPrice));
                if (_seriesMarket.Points.Count > _MAX_ITEMS)
                    _seriesMarket.Points.RemoveAt(0);
            }

            _IS_DATA_AVAILABLE = true;
        }

        private void UpdateAllSynchronizedSeries(string keyTitle, double oaDate, PlotInfo pointToAdd, BaseStudyModel item)
        {
            // Iterate directly over the dictionary to avoid allocation
            foreach (var kvp in _seriesByStudy)
            {
                var key = kvp.Key;
                var series = kvp.Value;

                if (keyTitle == key)
                {
                    // If the incoming item is the same as current series, add it
                    series.Points.Add(new DataPoint(oaDate, pointToAdd.Value));
                    if (series.Points.Count > _MAX_ITEMS)
                        series.Points.RemoveAt(0);
                }
                else
                {
                    // For all the other studies, add the existing last value again
                    var dataCollection = _dataByStudy[key];
                    var colCount = dataCollection.Count();
                    if (colCount > 0)
                    {
                        // Use indexer instead of LastOrDefault() for O(1) access
                        var lastPoint = dataCollection[colCount - 1];
                        // ✅ Create NEW instance from pool
                        var duplicatePoint = _plotInfoPool.Get();
                        duplicatePoint.Date = lastPoint.Date;
                        duplicatePoint.Value = lastPoint.Value;

                        dataCollection.Add(duplicatePoint);
                        series.Points.Add(new DataPoint(oaDate, duplicatePoint.Value));
                        if (series.Points.Count > _MAX_ITEMS)
                            series.Points.RemoveAt(0);
                    }
                }
            }

            // ADD MARKET PRICE if available
            if (_seriesMarket != null && item.MarketMidPrice > 0)
            {
                _seriesMarket.Points.Add(new DataPoint(oaDate, (double)item.MarketMidPrice));
                if (_seriesMarket.Points.Count > _MAX_ITEMS)
                    _seriesMarket.Points.RemoveAt(0);
            }
            _IS_DATA_AVAILABLE = true;
        }

        private void dataByStudy_OnRemoving(object? sender, PlotInfo e)
        {
            _plotInfoPool.Return(e);
        }

        private void uiUpdaterAction()
        {
            if (!_IS_DATA_AVAILABLE)
                return;

            // Check if disposed or disposing
            if (_disposed)
                return;

            lock (_LOCK)
            {
                // Double-check collections aren't disposed
                if (MyPlotModel == null || _dataByStudy == null)
                    return;

                lock (MyPlotModel.SyncRoot)
                {
                    RaisePropertyChanged(nameof(MyPlotModel));
                    MyPlotModel?.InvalidatePlot(true);
                }
            }

            _IS_DATA_AVAILABLE = false;
        }
        private void OpenSettings(object obj)
        {
            PluginManager.PluginManager.SettingPlugin(_plugin);
            Clear();
        }
        private void InitializeData()
        {
            lock (_LOCK)
            {
                _dataByStudy = new Dictionary<string, AggregatedCollection<PlotInfo>>();
                _seriesByStudy = new Dictionary<string, LineSeries>();
                foreach (IStudy study in _studies)
                {
                    study.OnCalculated += _study_OnCalculated;
                }
            }
            CreateMarketSeries();
            RefreshSettingsUI();
        }
        private void RefreshSettingsUI()
        {
            StudyTitle = StudyAxisTitle + " " +
                         _settings.Symbol + "-" +
                         _settings.Provider?.ProviderName + " " +
                         "[" + _settings.AggregationLevel.ToString() + "]";
            RaisePropertyChanged(nameof(StudyTitle));
        }
        private void CreatePlotModel()
        {
            MyPlotModel = new PlotModel();
            MyPlotModel.IsLegendVisible = true;
            MyPlotModel.Title = "";
            MyPlotModel.TitleColor = OxyColors.White;
            MyPlotModel.TitleFontSize = 20;
            MyPlotModel.PlotAreaBorderColor = OxyColors.White;
            MyPlotModel.PlotAreaBorderThickness = new OxyThickness(0);

            xAxe = new DateTimeAxis()
            {
                Key = "xAxe",
                Position = AxisPosition.Bottom,
                Title = "Timestamp",
                StringFormat = "HH:mm:ss", // Format time as hours:minutes:seconds
                IntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate interval type (seconds, minutes, hours)
                MinorIntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate minor interval type
                FontSize = 10,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                TitleColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false,

                TitleFontSize = 16,
            };
            yAxe = new LinearAxis()
            {
                Key = "yAxe",
                Position = AxisPosition.Left,
                Title = this.StudyAxisTitle,
                StringFormat = "N",
                FontSize = 10,
                TitleColor = OxyColors.Green,
                AxislineColor = OxyColors.Green,
                TicklineColor = OxyColors.Green,
                TextColor = OxyColors.Green,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false,

                TitleFontSize = 16,
            };
            var yAxeMarket = new OxyPlot.Axes.LinearAxis()
            {
                Key = "yAxeMarket",
                Position = OxyPlot.Axes.AxisPosition.Right,
                Title = "Market Mid Price",
                TitleColor = OxyPlot.OxyColors.White,
                TextColor = OxyPlot.OxyColors.White,
                StringFormat = "N",
                AxislineColor = OxyPlot.OxyColors.White,
                TicklineColor = OxyPlot.OxyColors.White,
                TitleFontSize = 16,
                FontSize = 12,
            };
            MyPlotModel.Axes.Add(xAxe);
            MyPlotModel.Axes.Add(yAxe);
            MyPlotModel.Axes.Add(yAxeMarket);

            MyPlotModel.Legends.Add(new Legend
            {
                LegendSymbolPlacement = LegendSymbolPlacement.Left,
                LegendItemAlignment = OxyPlot.HorizontalAlignment.Left,
                LegendPosition = LegendPosition.LeftTop,
                //TextColor = serieColor,
                //LegendTitleColor = serieColor,
                FontSize = 15,
                LegendFontSize = 15,
                LegendBorderThickness = 15,
                Selectable = false,
                LegendOrientation = LegendOrientation.Vertical,
                TextColor = OxyColors.WhiteSmoke,
                LegendTextColor = OxyColors.WhiteSmoke,
            });

        }
        private void CreateNewSerie(string title, OxyColor color)
        {
            //OxyColor serieColor = MapProviderCodeToOxyColor(providerId);

            //ADD The LINE SERIE
            var series = new OxyPlot.Series.LineSeries
            {
                Title = title,
                Color = color,
                MarkerType = MarkerType.None,
                DataFieldX = "Date",
                DataFieldY = "Value",
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                XAxisKey = "xAxe",
                YAxisKey = "yAxe", // Always use default axis for backward compatibility
                StrokeThickness = 2
            };
            MyPlotModel.Series.Add(series);

            _dataByStudy.Add(title, new AggregatedCollection<PlotInfo>(_settings.AggregationLevel, _MAX_ITEMS, x => x.Date, Aggregation));
            _dataByStudy[title].OnRemoving += dataByStudy_OnRemoving;
            _seriesByStudy.Add(title, series);

            // For dynamic Y-axes, we'll update on first data point if needed
        }


        private void CreateMarketSeries()
        {
            //ADD The LINE SERIE
            if (MyPlotModel.Series.Any(x => x.Title == "Market Mid Price"))
                return;
            _seriesMarket = new OxyPlot.Series.LineSeries
            {
                Title = "Market Mid Price",
                Color = OxyColors.White,
                MarkerType = MarkerType.None,
                DataFieldX = "Date",
                DataFieldY = "Value",
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                XAxisKey = "xAxe",
                YAxisKey = "yAxeMarket",
                StrokeThickness = 5
            };
            MyPlotModel.Series.Add(_seriesMarket);
        }
        private void Clear()
        {
            // Clear queue and wait for processing to finish
            _QUEUE.Clear(); //make this outside the LOCK, otherwise we could run into a deadlock situation when calling back 

            // Now safe to dispose collections
            lock (_LOCK)
            {
                // Stop UIUpdater before disposing collections to prevent race conditions
                uiUpdater?.Stop();

                // Give UI thread a moment to finish any pending timer events
                System.Windows.Application.Current?.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                // Now safe to dispose UIUpdater
                uiUpdater?.Dispose();

                // CRITICAL: Lock the PlotModel to prevent rendering during cleanup
                if (MyPlotModel != null)
                {
                    lock (MyPlotModel.SyncRoot)
                    {
                        MyPlotModel.Series.Clear();

                        // Clean up dynamic Y-axes
                        if (_enableDynamicYAxes && _dynamicYAxes != null)
                        {
                            foreach (var axis in _dynamicYAxes.Values)
                            {
                                MyPlotModel.Axes.Remove(axis);
                            }
                            _dynamicYAxes.Clear();
                            _axisRanges.Clear();
                            _seriesToAxisMap.Clear();
                        }
                    }
                }

                if (_dataByStudy != null)
                {
                    foreach (var data in _dataByStudy)
                    {
                        // ✅ Manually return all items before clearing
                        foreach (var item in data.Value.ToList())
                        {
                            _plotInfoPool.Return(item);
                        }
                        data.Value.Clear();
                        data.Value.Dispose();
                    }
                    _dataByStudy.Clear();
                }
                if (_seriesByStudy != null)
                {
                    _seriesByStudy.Clear();
                }
                _seriesMarket = null;

                // Re-create series and data collections
                _dataByStudy = new Dictionary<string, AggregatedCollection<PlotInfo>>();
                _seriesByStudy = new Dictionary<string, LineSeries>();
                CreateMarketSeries();
            }


            // Defer UIUpdater start to ensure OxyPlot has initialized
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_LOCK)
                {
                    if (!_disposed)
                    {
                        uiUpdater = new UIUpdater(uiUpdaterAction, _settings.AggregationLevel.ToTimeSpan().TotalMilliseconds);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);



            RefreshSettingsUI();
            // Force cleanup of old objects after settings change
            GC.Collect(0, GCCollectionMode.Optimized);
        }

        /// <summary>
        /// This method defines how the internal AggregatedCollection should aggregate incoming items.
        /// It is invoked whenever a new item is added to the collection and aggregation is required.
        /// The method takes the existing collection of items, the new incoming item, and a counter indicating
        /// how many times the last item has been aggregated. The aggregation logic should be implemented
        /// within this method to combine or process the items as needed.
        /// </summary>
        /// <param name="dataCollection">The existing internal collection of items.</param>
        /// <param name="newItem">The new incoming item to be aggregated.</param>
        /// <param name="lastItemAggregationCount">Counter indicating how many times the last item has been aggregated.</param>
        private void Aggregation(List<PlotInfo> dataCollection, PlotInfo newItem, int lastItemAggregationCount)
        {
            //dataCollection[^1].Date = newItem.Date;
            dataCollection[^1].Value = newItem.Value;
        }
        private OxyColor MapProviderCodeToOxyColor(int providerCode)
        {
            // Get all the OxyColors from the OxyColors class
            var allColors = typeof(OxyColors).GetFields(BindingFlags.Static | BindingFlags.Public)
                .Where(field => field.FieldType == typeof(OxyColor))
                .Select(field => (OxyColor)field.GetValue(null))
                .ToArray();

            // Exclude the Undefined and Automatic colors
            allColors = allColors.Where(color => color != OxyColors.Undefined && color != OxyColors.Automatic).ToArray();

            // Shuffle the colors using a seeded random number generator
            allColors = Shuffle(allColors, new Random(providerCode)).ToArray();

            // Return the first color from the shuffled array
            return allColors[0];
        }
        private IEnumerable<T> Shuffle<T>(IEnumerable<T> source, Random rng)
        {
            T[] elements = source.ToArray();
            for (int i = elements.Length - 1; i >= 0; i--)
            {
                int swapIndex = rng.Next(i + 1);
                yield return elements[swapIndex];
                elements[swapIndex] = elements[i];
            }
        }

        #region Dynamic Y-Axis Support (Can be disabled by setting _enableDynamicYAxes = false)

        private string GetOrCreateAxisForSeries(string seriesName, double value)
        {
            if (!_enableDynamicYAxes)
                return "yAxe";

            // Check if series already has an axis
            if (_seriesToAxisMap.TryGetValue(seriesName, out string existingAxis))
            {
                // Verify the axis still exists in the plot model
                if (MyPlotModel.Axes.Any(a => a.Key == existingAxis))
                    return existingAxis;
                else
                {
                    // Axis was removed, clean up mapping
                    _seriesToAxisMap.Remove(seriesName);
                }
            }

            // Find the best axis for this value range
            string bestAxis = FindBestAxisForValue(value);

            // Assign series to axis
            _seriesToAxisMap[seriesName] = bestAxis;

            return bestAxis;
        }

        private string FindBestAxisForValue(double value)
        {
            // Normalize value for comparison (handle negative values)
            double absValue = Math.Abs(value);
            if (absValue < 0.001) absValue = 0.001; // Avoid division by zero

            // Check existing axes
            foreach (var kvp in _axisRanges)
            {
                var axisRange = kvp.Value;
                double rangeRatio = Math.Max(absValue / axisRange.max, axisRange.max / absValue);

                // If this value fits within the range (within threshold), use this axis
                if (rangeRatio <= RANGE_THRESHOLD)
                {
                    // Update the range if necessary
                    if (absValue > axisRange.max)
                    {
                        _axisRanges[kvp.Key] = (axisRange.min, absValue);
                        // Update the actual axis maximum
                        if (_dynamicYAxes.TryGetValue(kvp.Key, out var axis))
                        {
                            axis.Maximum = absValue * 1.2; // Add 20% padding
                        }
                    }
                    return kvp.Key;
                }
            }

            // No suitable axis found, create new one
            return CreateNewAxis(absValue);
        }

        private string CreateNewAxis(double value)
        {
            string axisKey = $"yAxeDynamic{_axisCounter++}";

            // Determine axis properties based on value range
            string title = ""; // No title for dynamic axes to prevent overlap
            string format = "N";
            double min = 0;
            double max = Math.Max(value * 2, 10); // Ensure minimum range

            if (value > 1000000)
            {
                format = "N1";
                max = Math.Max(value / 1000000 * 2, 10);
            }
            else if (value > 1000)
            {
                format = "N1";
                max = Math.Max(value / 1000 * 2, 10);
            }
            else if (value <= 100 && value >= 0)
            {
                format = "N0";
                max = 100;
                min = 0;
            }
            else if (value < 0)
            {
                // Handle negative values
                min = value * 2;
                max = 0;
            }

            var newAxis = new LinearAxis()
            {
                Key = axisKey,
                Position = AxisPosition.Left,
                Title = title, // Empty title for dynamic axes
                StringFormat = format,
                FontSize = 10,
                TitleColor = OxyColors.LightSkyBlue,
                AxislineColor = OxyColors.LightSkyBlue,
                TicklineColor = OxyColors.LightSkyBlue,
                TextColor = OxyColors.LightSkyBlue,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false,
                TitleFontSize = 16,
                Minimum = min,
                Maximum = max
            };

            // Add axis to plot model
            MyPlotModel.Axes.Add(newAxis);
            _dynamicYAxes[axisKey] = newAxis;
            _axisRanges[axisKey] = (Math.Min(min, value), Math.Max(max, value));

            return axisKey;
        }

        private void UpdateSeriesAxis(string seriesName, LineSeries series)
        {
            if (!_enableDynamicYAxes)
            {
                series.YAxisKey = "yAxe";
                return;
            }

            // Get current data to determine axis
            if (_dataByStudy.TryGetValue(seriesName, out var dataCollection) && dataCollection.Count() > 0)
            {
                var lastValue = dataCollection.Last().Value;
                string axisKey = GetOrCreateAxisForSeries(seriesName, lastValue);
                series.YAxisKey = axisKey;
            }
        }

        #endregion

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Stop new data first - unsubscribe from events
                    lock (_LOCK)
                    {
                        if (_studies != null)
                        {
                            foreach (var s in _studies)
                            {
                                s.OnCalculated -= _study_OnCalculated;
                            }
                            _studies.Clear();
                        }
                    }

                    // Stop queue processing BEFORE UI cleanup
                    _QUEUE?.Stop();
                    Thread.Sleep(100); // Allow queue to finish current item
                    _QUEUE?.Clear();


                    // Stop UIUpdater on UI thread
                    try
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            uiUpdater?.Stop();
                            uiUpdater?.Dispose();
                        }, System.Windows.Threading.DispatcherPriority.Send);
                    }
                    catch { }

                    // Dispose queue
                    _QUEUE?.Dispose();

                    lock (_LOCK)
                    {
                        if (MyPlotModel != null)
                        {
                            lock (MyPlotModel.SyncRoot)
                            {
                                if (MyPlotModel?.Series != null)
                                {
                                    foreach (var s in MyPlotModel.Series)
                                    {
                                        (s as OxyPlot.Series.LineSeries)?.Points.Clear();
                                    }
                                    MyPlotModel.Series.Clear();
                                }
                            }
                        }

                        if (_dataByStudy != null)
                        {
                            foreach (var data in _dataByStudy)
                            {
                                // ✅ Safety: Return items before clearing
                                foreach (var item in data.Value.ToList())
                                {
                                    _plotInfoPool.Return(item);
                                }
                                data.Value.Clear();
                                data.Value.Dispose();
                            }
                            _dataByStudy.Clear();
                        }
                        if (_seriesByStudy != null)
                        {
                            foreach (var s in _seriesByStudy)
                            {
                                s.Value.Points.Clear();
                            }
                            _seriesByStudy.Clear();
                        }
                        _seriesMarket?.Points.Clear();
                        _seriesMarket = null;
                        MyPlotModel = null;
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
