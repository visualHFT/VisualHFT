using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Pools;
using VisualHFT.Helpers;
using VisualHFT.Model;
using DateTimeAxis = OxyPlot.Axes.DateTimeAxis;
using HelperCommon = VisualHFT.Helpers.HelperCommon;
using Legend = OxyPlot.Legends.Legend;
using LinearAxis = OxyPlot.Axes.LinearAxis;
using LineSeries = OxyPlot.Series.LineSeries;
using VisualHFT.Enums;

namespace VisualHFT.ViewModel
{
    public class vmMultiVenuePrices : BindableBase, IDisposable
    {
        private bool _disposed = false; // to track whether the object has been disposed
        private HelperCustomQueue<Tuple<int, string, double>> _QUEUE;
        private Dictionary<int, AggregatedCollection<PlotInfo>> _dataByProvider;
        private Dictionary<int, OxyPlot.Series.LineSeries> _seriesByProvider;

        private DateTimeAxis xAxe = null;
        private LinearAxis yAxe = null;

        private ObservableCollection<string> _symbols;
        private string _selectedSymbol;
        private AggregationLevel _aggregationLevelSelection;
        private const int _MAX_ITEMS = 1300;
        private bool _DATA_IS_AVAILABLE = false;

        private readonly object _LOCK = new object();
        private UIUpdater uiUpdater;

        // ✅ Object pool for PlotInfo to eliminate allocations
        private static readonly CustomObjectPool<PlotInfo> _plotInfoPool =
            new CustomObjectPool<PlotInfo>(maxPoolSize: _MAX_ITEMS * 10);
        public vmMultiVenuePrices()
        {
            _QUEUE = new HelperCustomQueue<Tuple<int, string, double>>($"<Tuple<int, string, double>>_vmMultiVenuePrices", QUEUE_onReadAction, QUEUE_onErrorAction);
            CreatePlotModel();

            _symbols = new ObservableCollection<string>(HelperSymbol.Instance);
            HelperSymbol.Instance.OnCollectionChanged += ALLSYMBOLS_CollectionChanged;
            RaisePropertyChanged(nameof(Symbols));

            HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);


            AggregationLevels = new ObservableCollection<Tuple<string, AggregationLevel>>();
            foreach (AggregationLevel level in Enum.GetValues(typeof(AggregationLevel)))
            {
                AggregationLevels.Add(new Tuple<string, AggregationLevel>(HelperCommon.GetEnumDescription(level), level));
            }
            AggregationLevelSelection = AggregationLevel.Ms100;

            _dataByProvider = new Dictionary<int, AggregatedCollection<PlotInfo>>();
            _seriesByProvider = new Dictionary<int, LineSeries>();

            // ✅ Defer UIUpdater initialization to ensure OxyPlot is fully loaded
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_LOCK)
                {
                    if (!_disposed)
                    {
                        uiUpdater = new UIUpdater(uiUpdaterAction, _aggregationLevelSelection.ToTimeSpan().TotalMilliseconds);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        ~vmMultiVenuePrices()
        {
            Dispose(false);
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
        private static void Aggregation(List<PlotInfo> dataCollection, PlotInfo newItem, int lastItemAggregationCount)
        {
            //dataCollection[^1].Date = newItem.Date;
            dataCollection[^1].Value = newItem.Value;
        }
        public ObservableCollection<string> Symbols { get => _symbols; set => _symbols = value; }
        public string Title { get; set; }

        public string SelectedSymbol
        {
            get => _selectedSymbol;
            set => SetProperty(ref _selectedSymbol, value, onChanged: () => Clear());

        }
        public AggregationLevel AggregationLevelSelection
        {
            get => _aggregationLevelSelection;
            set => SetProperty(ref _aggregationLevelSelection, value, onChanged: () => Clear());
        }
        public ObservableCollection<Tuple<string, AggregationLevel>> AggregationLevels { get; set; }
        public PlotModel MyPlotModel { get; private set; }


        private void uiUpdaterAction()
        {
            if (!_DATA_IS_AVAILABLE)
                return;
            if (xAxe == null || yAxe == null || MyPlotModel == null)
                return;
            if (string.IsNullOrEmpty(_selectedSymbol))
                return;

            lock (_LOCK)
            {
                RaisePropertyChanged(nameof(MyPlotModel));
                MyPlotModel.InvalidatePlot(true);
            }

            _DATA_IS_AVAILABLE = false;
        }
        private void ALLSYMBOLS_CollectionChanged(object? sender, string e)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _symbols.Add(e);
            }));
        }
        private void LIMITORDERBOOK_OnDataReceived(OrderBook e)
        {
            /*
             * ***************************************************************************************************
             * TRANSFORM the incoming object (decouple it)
             * DO NOT hold this call back, since other components depends on the speed of this specific call back.
             * DO NOT BLOCK
             * IDEALLY, USE QUEUES TO DECOUPLE
             * ***************************************************************************************************
             */
            if (string.IsNullOrEmpty(_selectedSymbol) || _selectedSymbol != e.Symbol)
                return;
            var _providerID = e.ProviderID;
            var _providerName = e.ProviderName;
            var _midPrice = e.MidPrice;
            _QUEUE.Add(new Tuple<int, string, double>(_providerID, _providerName, _midPrice));
        }

        private void QUEUE_onReadAction(Tuple<int, string, double> item)
        {
            bool isAddSuccess = false;
            int _providerID = item.Item1;
            string _providerName = item.Item2;
            double _midPrice = item.Item3;
            lock (_LOCK)
            {
                if (!_dataByProvider.ContainsKey(_providerID))
                {
                    CreateNewSerie(_providerID, _providerName);
                }

                // ✅ Get PlotInfo from pool instead of allocating
                var pointToAdd = _plotInfoPool.Get();
                pointToAdd.Date = HelperTimeProvider.Now;
                pointToAdd.Value = _midPrice;

                isAddSuccess = _dataByProvider[_providerID].Add(pointToAdd);

                //if is successfully added (according to its aggregation level), proceed with adding it into the series, and then all the other studies/series (to keep the same peace)
                if (isAddSuccess)
                {
                    double oaDate = pointToAdd.Date.ToOADate();

                    foreach (var key in _dataByProvider.Keys)
                    {
                        var series = _seriesByProvider[key];
                        if (_providerID == key) //if the incoming item is the same as current series, add it
                        {
                            series.Points.Add(new DataPoint(oaDate, pointToAdd.Value));
                            if (series.Points.Count > _MAX_ITEMS)
                                series.Points.RemoveAt(0);
                        }
                        else
                        {
                            //for all the other studies, add the existing last value again, so all series keep up
                            var lastPoint = _dataByProvider[key].LastOrDefault();
                            if (lastPoint != null)
                            {
                                // ✅ Create NEW instance from pool (not same reference)
                                var duplicatePoint = _plotInfoPool.Get();
                                duplicatePoint.Date = lastPoint.Date;
                                duplicatePoint.Value = lastPoint.Value;

                                _dataByProvider[key].Add(duplicatePoint);
                                series.Points.Add(new DataPoint(oaDate, duplicatePoint.Value));
                                if (series.Points.Count > _MAX_ITEMS)
                                    series.Points.RemoveAt(0);
                            }
                        }
                    }
                }
                else
                {
                    // ✅ Return to pool if not added (was aggregated)
                    _plotInfoPool.Return(pointToAdd);
                }

                if (!_DATA_IS_AVAILABLE)
                    _DATA_IS_AVAILABLE = isAddSuccess;
            }
        }

        private void QUEUE_onErrorAction(Exception ex)
        {
            Console.WriteLine("Error in queue processing: " + ex.Message);
            throw ex;
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

        private void CreatePlotModel()
        {
            MyPlotModel = new PlotModel();
            MyPlotModel.IsLegendVisible = true;
            MyPlotModel.Title = "Venue's Prices";
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
                Position = AxisPosition.Right,
                Title = "Mid-Price",
                StringFormat = "N",
                FontSize = 10,
                TitleColor = OxyColors.White,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false,

                TitleFontSize = 16,
            };

            MyPlotModel.Axes.Add(xAxe);
            MyPlotModel.Axes.Add(yAxe);

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
        private void CreateNewSerie(int providerId, string providerName)
        {
            OxyColor serieColor = MapProviderCodeToOxyColor(providerId);

            //ADD The LINE SERIE
            var series = new OxyPlot.Series.LineSeries
            {
                Title = providerName,
                //Color = serieColor,
                MarkerType = MarkerType.None,
                DataFieldX = "Date",
                DataFieldY = "Value",
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                XAxisKey = "xAxe",
                YAxisKey = "yAxe",
                StrokeThickness = 5
            };
            // Series are created by the queue worker. Synchronize with OxyPlot's
            // renderer and resolve the keyed axes before the series can render.
            lock (MyPlotModel.SyncRoot)
            {
                MyPlotModel.Series.Add(series);
                ((IPlotModel)MyPlotModel).Update(false);
            }

            var aggregatedCollection = new AggregatedCollection<PlotInfo>(_aggregationLevelSelection, _MAX_ITEMS, x => x.Date, Aggregation);

            // ✅ Subscribe to OnRemoving to return items to pool
            aggregatedCollection.OnRemoving += dataByProvider_OnRemoving;

            _dataByProvider.Add(providerId, aggregatedCollection);
            _seriesByProvider.Add(providerId, series);
        }

        // ✅ Event handler to return PlotInfo to pool when removed
        private void dataByProvider_OnRemoving(object? sender, PlotInfo e)
        {
            _plotInfoPool.Return(e);
        }
        private void Clear()
        {
            _QUEUE.Clear(); //make this outside the LOCK, otherwise we could run into a deadlock situation when calling back 

            lock (_LOCK)
            {
                uiUpdater?.Stop();

                // Give UI thread a moment to finish any pending timer events
                System.Windows.Application.Current?.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                uiUpdater?.Dispose();


                if (MyPlotModel.Series != null)
                {
                    foreach (var s in MyPlotModel.Series)
                    {
                        (s as OxyPlot.Series.LineSeries)?.Points.Clear();
                    }
                }

                if (_dataByProvider != null)
                {
                    foreach (var data in _dataByProvider)
                    {
                        // ✅ Return all items to pool BEFORE clearing
                        foreach (var item in data.Value.ToList())
                        {
                            _plotInfoPool.Return(item);
                        }

                        data.Value.Clear();
                        data.Value.Dispose();
                        _dataByProvider[data.Key] = new AggregatedCollection<PlotInfo>(_aggregationLevelSelection,
                            _MAX_ITEMS, x => x.Date, Aggregation);

                        // ✅ Re-subscribe to OnRemoving for new collection
                        _dataByProvider[data.Key].OnRemoving += dataByProvider_OnRemoving;
                    }
                }
                if (_seriesByProvider != null)
                {
                    foreach (var s in _seriesByProvider)
                    {
                        s.Value.Points.Clear();
                    }
                }
            }

            // ✅ Defer UIUpdater recreation
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_LOCK)
                {
                    if (!_disposed)
                    {
                        uiUpdater = new UIUpdater(uiUpdaterAction, _aggregationLevelSelection.ToTimeSpan().TotalMilliseconds);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            Title = _selectedSymbol + " - Multi Venue Prices";
            RaisePropertyChanged(nameof(Title));
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
                    HelperSymbol.Instance.OnCollectionChanged -= ALLSYMBOLS_CollectionChanged;

                    // Stop queue processing BEFORE UI cleanup
                    _QUEUE?.Stop();
                    System.Threading.Thread.Sleep(100); // Allow queue to finish current item
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
                        if (MyPlotModel?.Series != null)
                        {
                            foreach (var s in MyPlotModel.Series)
                            {
                                (s as OxyPlot.Series.LineSeries)?.Points.Clear();
                            }
                            MyPlotModel.Series.Clear();
                        }

                        if (_dataByProvider != null)
                        {
                            foreach (var data in _dataByProvider)
                            {
                                // ✅ Safety: Return items before clearing (defensive programming)
                                foreach (var item in data.Value.ToList())
                                {
                                    _plotInfoPool.Return(item);
                                }

                                data.Value.Clear();
                                data.Value.Dispose();
                            }
                            _dataByProvider.Clear();
                        }

                        if (_seriesByProvider != null)
                        {
                            foreach (var s in _seriesByProvider)
                            {
                                s.Value.Points.Clear();
                            }
                            _seriesByProvider.Clear();
                        }

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
