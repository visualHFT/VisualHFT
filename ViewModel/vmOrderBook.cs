/*
    GOAL
    
    The vmOrderBook class serves as a high-performance, real-time visualization engine for limit order book data 
    with the following key objectives:

    1. Real-Time Market Data Visualization
       - Displays continuous updates of order book data with minimal latency
       - Visualizes multiple aspects of market microstructure:
         * Best Bid/Ask prices (Line series)
         * Mid price (Line series)
         * Order book depth through scatter points
         * Bid/Ask spread evolution
         * Volume distribution through cumulative charts
    
    2. Multi-dimensional Data Representation
       - Y-axis: Price levels of orders
       - X-axis: Time progression
       - Point size: Order volume at each price level
       - Color intensity: Relative volume significance
       - Separate visualizations for bid (green) and ask (red) sides
    
    3. Performance Optimization
       - Implements memory pooling to minimize GC pressure
       - Uses efficient data structures for real-time updates
       - Employs lock-free operations where possible
       - Implements smart rendering strategies to maintain UI responsiveness
    
    4. Flexible Data Aggregation
       - Supports multiple time-based aggregation levels:
         * From millisecond-level for high-frequency analysis
         * Up to second-level for broader market view
       - Allows dynamic switching between aggregation levels
       - Maintains data consistency across different time scales
    
    5. Market Depth Analysis
       - Visualizes complete order book depth
       - Shows price level clustering
       - Represents volume concentration at different price levels
       - Tracks order book imbalances
    
    This visualization model is particularly suited for:
    - High-frequency trading analysis
    - Market microstructure research
    - Real-time market monitoring
    - Order flow analysis
    - Liquidity analysis at different price levels
*/

/*
    SEQUENCE OF EVENTS
    1. This class receives market data subscribing to the HelperOrderBook.Instance. This is the entry point, and once subscribed, data starts arriving
    2. Data arrives in LIMITORDERBOOK_OnDataReceived (at a rate of 10000 messages per second), decouples the incoming data and pushes it to a queue for processing. 
       This decoupling needs to happen as efficient as possible so we avoid blocking the incoming data stream. 
       The queue is implemented in HelperCustomQueue<OrderBookSnapshot>
    3. The queue is processed by QUEUE_onReadAction, which adds the data to an AggregatedCollection
       that aggregates the data based on the selected aggregation level
    4. The AggregatedCollection raises events when data is added or removed, which are handled by _AGGREGATED_LOB_OnRemoved
    5. The _AGGREGATED_LOB_OnAdded method updates the local values, adds points to the charts series (RealTimePricePlotModel, RealTimeSpreadModel, CummulativeBidsChartModel, CummulativeAsksChartModel)
    6. The _AGGREGATED_LOB_OnRemoved method removes the last points from the charts series, maintaining the real-time nature of the data
    7. The uiUpdaterAction method is called periodically, by a timer, to raise the UI with the latest data from the already updated chart series
       RealTimePricePlotModel, RealTimeSpreadModel, CummulativeBidsChartModel, CummulativeAsksChartModel
 */
/*
    IMPLEMENTATION DETAILS
    
    KEY COMPONENTS:
    1. Data Management & Pooling
       - Uses CustomObjectPool<OrderBookSnapshot> to efficiently manage memory
       - Implements pooling for List<T> and ScatterPoint objects to reduce GC pressure
       - OrderBookSnapshotPool handles recycling of snapshots with configurable pool size
    
    2. UI Update & Threading Model
       - Uses dispatcher-based UI updates with configurable refresh rate (_MIN_UI_REFRESH_TS = 60ms)
       - Enforces minimum UI refresh interval of 60ms to maintain visual smoothness
       - Implements thread-safe data access using multiple lock objects (MTX_*)
       - Decouples data processing from UI updates via queue-based architecture
    
    3. Chart Management
       - Maintains 4 distinct chart models:
         * RealTimePricePlotModel: Current price movements with bid/ask visualization
         * RealTimeSpreadModel: Spread between bid and ask prices
         * CummulativeBidsChartModel: Depth visualization for bids
         * CummulativeAsksChartModel: Depth visualization for asks
       - Uses OxyPlot for efficient charting with custom series configurations
    
    4. Data Aggregation
       - Implements AggregatedCollection<T> for time-based data grouping
       - Supports multiple aggregation levels from 1ms to daily
       - Uses custom aggregation logic in _AGGREGATED_LOB_OnAggregating
    
    5. Memory Management
       - Implements IDisposable pattern for proper resource cleanup
       - Uses object pooling for frequently allocated objects
       - Maintains separate pools for different object types to optimize memory usage
    
    6. Event Handling
       - Subscribes to market data via HelperOrderBook.Instance
       - Handles provider status changes and symbol updates
       - Processes trade updates separately from order book updates
    
    7. Performance Considerations
       - Implements custom object pooling to reduce garbage collection
       - Uses lock-free operations where possible
       - Minimizes allocations in hot paths
       - Implements efficient grid updates by reusing existing objects
*/
/*
    LATEST FROM PROFILING = Jun-2025
    * Profiling shows that most of the pressure (memory allocation, CPU and GC) are coming from the Scatter plotting.
    * When chart component redraws the scatter series, it will redraw point by point, re-creating the entire plot.
 */


using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.Pools;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using AxisPosition = OxyPlot.Axes.AxisPosition;


namespace VisualHFT.ViewModel
{

    public class vmOrderBook : BindableBase, IDisposable
    {
        private static readonly int _MAX_CHART_POINTS = 5000;
        private static readonly int _MAX_TRADES_RECORDS = 100;
        private readonly TimeSpan _MIN_UI_REFRESH_TS = TimeSpan.FromMilliseconds(60); //For the UI: do not allow less than this, since it is not noticeble for human eye

        
        private static class OrderBookSnapshotPool
        {
            public static readonly CustomObjectPool<OrderBookSnapshot> Instance = new 
                CustomObjectPool<OrderBookSnapshot>(maxPoolSize: _MAX_CHART_POINTS + (int)(_MAX_CHART_POINTS*1.1));
        }
        private static class ScatterPointsPool
        {
            public static readonly CustomObjectPool<OxyPlot.Series.ScatterPoint> Instance = 
                new(maxPoolSize: _MAX_CHART_POINTS * 50 * 2);
        }
        private static class ScatterPointsListPool
        {
            public static readonly CustomObjectPool<List<OxyPlot.Series.ScatterPoint>> Instance =
                new(maxPoolSize: 200); // Pre-allocate 200 lists (enough for 100 simultaneous snapshots)
        }

        private bool _disposed = false; // to track whether the object has been disposed
        private readonly object MTX_TRADES = new object();
        private readonly object MTX_SNAPSHOTS = new object();

        private Dictionary<string, Func<string, String, bool>> _dialogs;
        private ObservableCollection<string> _symbols;
        private string _selectedSymbol;
        private VisualHFT.ViewModel.Model.Provider _selectedProvider = null;
        private AggregationLevel _aggregationLevelSelection;

        private List<BookItem> _bidsGrid;
        private List<BookItem> _asksGrid;
        private CachedCollection<BookItem> _depthGrid;

        private ObservableCollection<VisualHFT.ViewModel.Model.Provider> _providers;

        private BookItem _AskTOB = new BookItem();
        private BookItem _BidTOB = new BookItem();
        private double _MidPoint;
        private double _Spread;
        private int _decimalPlaces;


        private readonly Model.BookItemPriceSplit _BidTOB_SPLIT = null;
        private readonly Model.BookItemPriceSplit _AskTOB_SPLIT = null;

        private double _lobImbalanceValue = 0;
        private int _switchView = 0;

        private UIUpdater uiUpdater;

        private readonly Stack<VisualHFT.Model.Trade> _realTimeTrades;
        private HelperCustomQueue<OrderBookSnapshot> _QUEUE;
        private AggregatedCollection<OrderBookSnapshot> _AGGREGATED_LOB;


        private bool _MARKETDATA_AVAILABLE = false;
        private bool _TRADEDATA_AVAILABLE = false;

        private double _minScatterBubbleSize = double.MaxValue;
        private double _maxScatterBubbleSize = 0.0;
        private double _minScatterVisualSize = 1.0; // Example: Min marker radius of 1
        private double _maxScatterVisualSize = 12.0;

        public vmOrderBook(Dictionary<string, Func<string, string, bool>> dialogs)
        {
            this._dialogs = dialogs;
            RealTimePricePlotModel = new PlotModel();
            RealTimeSpreadModel = new PlotModel();
            CummulativeBidsChartModel = new PlotModel();
            CummulativeAsksChartModel = new PlotModel();

            _QUEUE = new HelperCustomQueue<OrderBookSnapshot>($"<OrderBookSnapshot>_vmOrderBook", QUEUE_onReadAction, QUEUE_onErrorAction);


            _realTimeTrades = new Stack<VisualHFT.Model.Trade>();
            TradesDisplay = new ObservableCollection<Trade>();

            InitializeRealTimePriceChart();
            InitializeRealTimeSpreadChart();
            InitializeCummulativeCharts();

            _bidsGrid = new List<BookItem>();
            _asksGrid = new List<BookItem>();
            _depthGrid = new CachedCollection<BookItem>((x, y) => y.Price.GetValueOrDefault().CompareTo(x.Price.GetValueOrDefault()));

            _symbols = new ObservableCollection<string>(HelperSymbol.Instance);
            _providers = VisualHFT.ViewModel.Model.Provider.CreateObservableCollection();
            AggregationLevels = new ObservableCollection<Tuple<string, AggregationLevel>>();
            foreach (AggregationLevel level in Enum.GetValues(typeof(AggregationLevel)))
            {
                if (level >= AggregationLevel.Ms10) //do not load less than 100ms. In order to save resources, we cannot go lower than 100ms (//TODO: in the future we must include lower aggregation levels)
                    AggregationLevels.Add(new Tuple<string, AggregationLevel>(Helpers.HelperCommon.GetEnumDescription(level), level));
            }
            _aggregationLevelSelection = AggregationLevel.Ms100; //DEFAULT
            uiUpdater = new UIUpdater(uiUpdaterAction, _aggregationLevelSelection.ToTimeSpan().TotalMilliseconds);
            _AGGREGATED_LOB = new AggregatedCollection<OrderBookSnapshot>(
                _aggregationLevelSelection, 
                _MAX_CHART_POINTS, 
                (x => x.LastUpdated), 
                _AGGREGATED_LOB_OnAggregating);
            _AGGREGATED_LOB.OnRemoved += _AGGREGATED_LOB_OnRemoved;
            _AGGREGATED_LOB.OnRemoving += _AGGREGATED_LOB_OnRemoving;
            HelperSymbol.Instance.OnCollectionChanged += ALLSYMBOLS_CollectionChanged;
            HelperProvider.Instance.OnDataReceived += PROVIDERS_OnDataReceived;
            HelperProvider.Instance.OnStatusChanged += PROVIDERS_OnStatusChanged;

            HelperTrade.Instance.Subscribe(TRADES_OnDataReceived);
            HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);


            _BidTOB_SPLIT = new Model.BookItemPriceSplit();
            _AskTOB_SPLIT = new Model.BookItemPriceSplit();

            RaisePropertyChanged(nameof(Providers));
            RaisePropertyChanged(nameof(BidTOB_SPLIT));
            RaisePropertyChanged(nameof(AskTOB_SPLIT));
            RaisePropertyChanged(nameof(TradesDisplay));

            SwitchView = 0;


        }


        ~vmOrderBook()
        {
            Dispose(false);
        }

        private void InitializeRealTimePriceChart()
        {
            RealTimePricePlotModel.DefaultFontSize = 8.0;
            RealTimePricePlotModel.Title = "";
            RealTimePricePlotModel.TitleColor = OxyColors.White;
            RealTimePricePlotModel.PlotAreaBorderColor = OxyColors.White;
            RealTimePricePlotModel.PlotAreaBorderThickness = new OxyThickness(0);
            RealTimePricePlotModel.EdgeRenderingMode = EdgeRenderingMode.PreferSpeed;
            var xAxis = new OxyPlot.Axes.DateTimeAxis()
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss", // Format time as hours:minutes:seconds
                IntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate interval type (seconds, minutes, hours)
                MinorIntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate minor interval type
                IntervalLength = 80, // Determines how much space each interval takes up, adjust as necessary
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsAxisVisible = false,
                IsPanEnabled = false,
                IsZoomEnabled = false,
            };

            var yAxis = new OxyPlot.Axes.LinearAxis()
            {
                Position = AxisPosition.Left,
                StringFormat = "N",

                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                IsPanEnabled = false,
                IsZoomEnabled = false,
                IsAxisVisible = true
            };

            // Add a color axis to map quantity to color
            var RedColorAxis = new LinearColorAxis
            {
                Position = AxisPosition.Right,
                Palette = OxyPalette.Interpolate(10, OxyColors.Pink, OxyColors.DarkRed),
                Minimum = 1,
                Maximum = 100,
                Key = "RedColorAxis",
                IsAxisVisible = false
            };

            var GreenColorAxis = new LinearColorAxis
            {
                Position = AxisPosition.Right,
                Palette = OxyPalette.Interpolate(10, OxyColors.LightGreen, OxyColors.DarkGreen),
                Minimum = 1,
                Maximum = 100,
                Key = "GreenColorAxis",
                IsAxisVisible = false
            };


            RealTimePricePlotModel.Axes.Add(xAxis);
            RealTimePricePlotModel.Axes.Add(yAxis);
            RealTimePricePlotModel.Axes.Add(RedColorAxis);
            RealTimePricePlotModel.Axes.Add(GreenColorAxis);


            //Add MID-PRICE Serie
            var lineMidPrice = new OxyPlot.Series.LineSeries
            {
                Title = "MidPrice",
                MarkerType = MarkerType.None,
                StrokeThickness = 2,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Gray,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };
            var lineAsk = new OxyPlot.Series.LineSeries
            {
                Title = "Ask",
                MarkerType = MarkerType.None,
                StrokeThickness = 6,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Red,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };
            var lineBid = new OxyPlot.Series.LineSeries
            {
                Title = "Bid",
                MarkerType = MarkerType.None,
                StrokeThickness = 6,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Green,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };
            //SCATTER SERIES
            var scatterAsks = new OxyPlot.Series.ScatterSeries
            {
                Title = "ScatterAsks",
                ColorAxisKey = "RedColorAxis",
                MarkerType = MarkerType.Circle,
                MarkerStrokeThickness = 0,
                //MarkerStroke = OxyColors.DarkRed,
                MarkerStroke = OxyColors.Transparent,
                //MarkerFill = OxyColor.Parse("#80FF0000"),
                MarkerSize = 10,
                RenderInLegend = false,
                Selectable = false,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                BinSize = 15 //smoothing the draw for speed performance
            };
            var scatterBids = new OxyPlot.Series.ScatterSeries
            {
                Title = "ScatterBids",
                ColorAxisKey = "GreenColorAxis",
                MarkerType = MarkerType.Circle,
                MarkerStroke = OxyColors.Transparent,
                //MarkerStroke = OxyColors.Green,
                MarkerStrokeThickness = 0,
                //MarkerFill = OxyColor.Parse("#8000FF00"),
                MarkerSize = 10,
                RenderInLegend = false,
                Selectable = false,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                BinSize = 15 //smoothing the draw for speed performance
            };


            // do not change the order of adding these series (The overlap between them will depend on the order they have been added)
            RealTimePricePlotModel.Series.Add(scatterBids);
            RealTimePricePlotModel.Series.Add(scatterAsks);
            RealTimePricePlotModel.Series.Add(lineMidPrice);
            RealTimePricePlotModel.Series.Add(lineAsk);
            RealTimePricePlotModel.Series.Add(lineBid);

        }
        private void InitializeRealTimeSpreadChart()
        {
            RealTimeSpreadModel.DefaultFontSize = 8.0;
            RealTimeSpreadModel.Title = "";
            RealTimeSpreadModel.TitleColor = OxyColors.White;
            RealTimeSpreadModel.PlotAreaBorderColor = OxyColors.White;
            RealTimeSpreadModel.PlotAreaBorderThickness = new OxyThickness(0);

            var xAxis = new OxyPlot.Axes.DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss", // Format time as hours:minutes:seconds
                IntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate interval type (seconds, minutes, hours)
                MinorIntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate minor interval type
                IntervalLength = 80, // Determines how much space each interval takes up, adjust as necessary
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false,
            };

            var yAxis = new OxyPlot.Axes.LinearAxis()
            {
                Position = AxisPosition.Left,
                StringFormat = "N",
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };
            RealTimeSpreadModel.Axes.Add(xAxis);
            RealTimeSpreadModel.Axes.Add(yAxis);



            //Add MID-PRICE Serie
            var lineSpreadSeries = new OxyPlot.Series.LineSeries
            {
                Title = "Spread",
                MarkerType = MarkerType.None,
                StrokeThickness = 4,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Blue,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,

            };

            RealTimeSpreadModel.Series.Add(lineSpreadSeries);
        }
        private void InitializeCummulativeCharts()
        {
            CummulativeBidsChartModel.DefaultFontSize = 8.0;
            CummulativeBidsChartModel.Title = "";
            CummulativeBidsChartModel.TitleColor = OxyColors.White;
            CummulativeBidsChartModel.PlotAreaBorderColor = OxyColors.White;
            CummulativeBidsChartModel.PlotAreaBorderThickness = new OxyThickness(0);
            CummulativeAsksChartModel.DefaultFontSize = 8.0;
            CummulativeAsksChartModel.Title = "";
            CummulativeAsksChartModel.TitleColor = OxyColors.White;
            CummulativeAsksChartModel.PlotAreaBorderColor = OxyColors.White;
            CummulativeAsksChartModel.PlotAreaBorderThickness = new OxyThickness(0);

            var xAxis = new OxyPlot.Axes.LinearAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "N", // Format time as hours:minutes:seconds
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };

            var yAxis = new OxyPlot.Axes.LinearAxis()
            {
                Position = AxisPosition.Left,
                StringFormat = "N",
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };
            var xAxis2 = new OxyPlot.Axes.LinearAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "N", // Format time as hours:minutes:seconds
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };

            var yAxis2 = new OxyPlot.Axes.LinearAxis()
            {
                Position = AxisPosition.Right,
                StringFormat = "N",
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };
            CummulativeBidsChartModel.Axes.Add(xAxis);
            CummulativeBidsChartModel.Axes.Add(yAxis);

            CummulativeAsksChartModel.Axes.Add(xAxis2);
            CummulativeAsksChartModel.Axes.Add(yAxis2);


            //AREA Series
            var areaSpreadSeriesBids = new OxyPlot.Series.TwoColorAreaSeries()
            {
                Title = "",
                MarkerType = MarkerType.None,
                StrokeThickness = 5,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.LightGreen,
                Fill = OxyColors.DarkGreen,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };
            var areaSpreadSeriesAsks = new OxyPlot.Series.TwoColorAreaSeries()
            {
                Title = "",
                MarkerType = MarkerType.None,
                StrokeThickness = 5,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Pink,
                Fill = OxyColors.DarkRed,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };

            CummulativeBidsChartModel.Series.Add(areaSpreadSeriesBids);
            CummulativeAsksChartModel.Series.Add(areaSpreadSeriesAsks);
        }
        private void uiUpdaterAction()
        {
            if (_selectedProvider == null || string.IsNullOrEmpty(_selectedSymbol))
                return;
            if (string.IsNullOrEmpty(_selectedSymbol) || _selectedSymbol == "-- All symbols --")
                return;

            if (_MARKETDATA_AVAILABLE)
            {
                // ✅ Level 1: Local data
                lock (MTX_SNAPSHOTS)
                {
                    _AskTOB_SPLIT?.RaiseUIThread();
                    _BidTOB_SPLIT?.RaiseUIThread();

                    RaisePropertyChanged(nameof(MidPoint));
                    RaisePropertyChanged(nameof(Spread));
                    RaisePropertyChanged(nameof(LOBImbalanceValue));

                    RaisePropertyChanged(nameof(Bids));
                    RaisePropertyChanged(nameof(Asks));
                    RaisePropertyChanged(nameof(Depth));
                }
                // ✅ Level 3: Price chart
                lock (RealTimePricePlotModel.SyncRoot)
                    RealTimePricePlotModel.InvalidatePlot(true);

                // ✅ Level 4: Spread chart
                lock (RealTimeSpreadModel.SyncRoot)
                    RealTimeSpreadModel.InvalidatePlot(true);

                // ✅ Level 5-6: Cumulative charts
                lock (CummulativeBidsChartModel.SyncRoot)
                {
                    lock (CummulativeAsksChartModel.SyncRoot)
                    {
                        CummulativeBidsChartModel.InvalidatePlot(true);
                        CummulativeAsksChartModel.InvalidatePlot(true);
                    }
                }

                _MARKETDATA_AVAILABLE = false; //to avoid ui update when no new data is coming in
            }

            //TRADES
            if (_TRADEDATA_AVAILABLE)
            {
                lock (MTX_TRADES)
                {
                    while (_realTimeTrades.TryPop(out var itemToAdd))
                    {
                        TradesDisplay.Insert(0, itemToAdd);
                        if (TradesDisplay.Count > _MAX_TRADES_RECORDS)
                        {
                            TradesDisplay.RemoveAt(TradesDisplay.Count - 1);
                        }
                    }
                }
                _TRADEDATA_AVAILABLE = false; //to avoid the ui updates when no new data is coming in
            }
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
            if (_selectedProvider == null || _selectedProvider.ProviderID <= 0 || _selectedProvider.ProviderID != e?.ProviderID)
                return;
            if (string.IsNullOrEmpty(_selectedSymbol) || _selectedSymbol != e?.Symbol)
                return;


            e.CalculateMetrics();
            OrderBookSnapshot snapshot = OrderBookSnapshotPool.Instance.Get();
            // Initialize its state based on the master OrderBook.
            snapshot.UpdateFrom(e);
            // Enqueue for processing.
            _QUEUE.Add(snapshot);
        }
        private void _AGGREGATED_LOB_OnRemoving(object? sender, OrderBookSnapshot e)
        {
            // Perform cleanup BEFORE returning the object to the pool
            lock (RealTimeSpreadModel.SyncRoot)
                RemoveLastPointToSpreadChart();
            lock (RealTimePricePlotModel.SyncRoot)
                RemoveLastPointsToScatterChart();

            // NOW it is safe to return the object to the pool
            OrderBookSnapshotPool.Instance.Return(e);
        }
        private void _AGGREGATED_LOB_OnRemoved(object? sender, int index)
        {
            //for current snapshot, make sure to return to the pool 
            if (index == -1)
                OrderBookSnapshotPool.Instance.Reset(); //reset the entire pool

            //remove last points on the chart
            if (index == 0) //make sure the item is the last
            {
                // This logic is now moved to _AGGREGATED_LOB_OnRemoving
                /*
                lock (RealTimeSpreadModel.SyncRoot)
                    RemoveLastPointToSpreadChart();
                lock (RealTimePricePlotModel.SyncRoot)
                    RemoveLastPointsToScatterChart();
                */
            }
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
        private void _AGGREGATED_LOB_OnAggregating(List<OrderBookSnapshot> dataCollection, OrderBookSnapshot newItem, int lastItemAggregationCount)
        {
            //DO NOTHING. (the adding (and pool return) logic is being handled in QUEUE_onReadAction already)
        }

        private void QUEUE_onReadAction(OrderBookSnapshot ob)
        {
            // ✅ PHASE 1: Update local data (Level 1 lock)
            lock (MTX_SNAPSHOTS)
            {
                UpdateLocalValues(ob);
                BidAskGridUpdate(ob);
            }

            // ✅ PHASE 2: Add to aggregation (Level 3 lock)
            bool addedOK = false;
            lock (RealTimePricePlotModel.SyncRoot)
            {
                addedOK = _AGGREGATED_LOB.Add(ob);
            }

            if (!addedOK)
            {
                OrderBookSnapshotPool.Instance.Return(ob);
                return;
            }

            var lobItemToDisplay = ob;
            double sharedTS = lobItemToDisplay.LastUpdated.ToOADate();

            // ✅ PHASE 3: Update cumulative charts (Level 5-6 locks)
            lock (CummulativeBidsChartModel.SyncRoot)
            {
                lock (CummulativeAsksChartModel.SyncRoot)
                {
                    double maxBid = AddPointsToCumulativeBidVolumeChart(
                        ToDataPointsCumulativeVolume(lobItemToDisplay.Bids, sharedTS));
                    double maxAsk = AddPointsToCumulativeAskVolumeChart(
                        ToDataPointsCumulativeVolume(lobItemToDisplay.Asks, sharedTS));
                    var maxAll = Math.Max(maxBid, maxAsk);

                    SetMaximumsToCumulativeBidVolumeCharts(maxAll);
                    SetMaximumsToCumulativeAskVolumeCharts(maxAll);
                }
            }

            // ✅ PHASE 4: Create scatter points OUTSIDE ANY LOCKS
            var bidLevelPoints = ToScatterPointsLevels(
                lobItemToDisplay.Bids.Where(x => x.Price >= _MidPoint * 0.99),
                sharedTS);
            var askLevelPoints = ToScatterPointsLevels(
                lobItemToDisplay.Asks.Where(x => x.Price <= _MidPoint * 1.01),
                sharedTS);

            try
            {
                // ✅ PHASE 5: Update scatter chart (Level 3 lock)
                lock (RealTimePricePlotModel.SyncRoot)
                {
                    AddPointsToScatterPriceChart(
                        ToDataPointBestBid(lobItemToDisplay, sharedTS),
                        ToDataPointBestAsk(lobItemToDisplay, sharedTS),
                        ToDataPointMidPrice(lobItemToDisplay, sharedTS),
                        bidLevelPoints,
                        askLevelPoints);
                }
            }
            finally
            {
                ScatterPointsListPool.Instance.Return(bidLevelPoints);
                ScatterPointsListPool.Instance.Return(askLevelPoints);
            }

            // ✅ PHASE 6: Update spread chart (Level 4 lock)
            lock (RealTimeSpreadModel.SyncRoot)
            {
                AddPointToSpreadChart(ToDataPointSpread(lobItemToDisplay, sharedTS));
            }

            _MARKETDATA_AVAILABLE = true;
        }
        private void QUEUE_onErrorAction(Exception ex)
        {
            Console.WriteLine("Error in queue processing: " + ex.Message);
            Clear();
        }



        private DataPoint? ToDataPointBestBid(OrderBookSnapshot? lob, double sharedTS)
        {
            if (lob != null && lob.Bids != null && lob.Bids.Count > 0 && lob.Bids[0].Price.HasValue)
                return new DataPoint(sharedTS, lob.Bids[0].Price.Value);
            else
                return null;
        }
        private DataPoint? ToDataPointBestAsk(OrderBookSnapshot? lob, double sharedTS)
        {
            if (lob != null && lob.Asks != null && lob.Asks.Count > 0 && lob.Asks[0].Price.HasValue)
                return new DataPoint(sharedTS, lob.Asks[0].Price.Value);
            else
                return null;
        }
        private DataPoint ToDataPointMidPrice(OrderBookSnapshot lob, double sharedTS)
        {
            return new DataPoint(sharedTS, lob.MidPrice);
        }
        private DataPoint ToDataPointSpread(OrderBookSnapshot lob, double sharedTS)
        {
            return new DataPoint(sharedTS, lob.Spread);
        }
        private List<OxyPlot.Series.ScatterPoint> ToScatterPointsLevels(
            IEnumerable<BookItem> lobList,
            double sharedTS)
        {
            if (lobList == null || !lobList.Any())
            {
                var emptyList = ScatterPointsListPool.Instance.Get();
                emptyList.Clear();
                return emptyList;
            }

            // Get pooled list
            var scatterPoints = ScatterPointsListPool.Instance.Get();
            scatterPoints.Clear();

            // Size normalization logic
            _minScatterBubbleSize = Math.Min(_minScatterBubbleSize, lobList.Min(x => x.Size.Value));
            _maxScatterBubbleSize = Math.Max(_maxScatterBubbleSize, lobList.Max(x => x.Size.Value));
            double bookSizeRange = _maxScatterBubbleSize - _minScatterBubbleSize;
            double visualSizeRange = _maxScatterVisualSize - _minScatterVisualSize;

            foreach (var lob in lobList)
            {
                double currentBookSize = lob.Size.Value;
                double visualSize = _minScatterVisualSize +
                    ((currentBookSize - _minScatterBubbleSize) / bookSizeRange) * visualSizeRange;
                visualSize = Math.Max(_minScatterVisualSize, Math.Min(_maxScatterVisualSize, visualSize));

                if (lob.Price > 0 && lob.Size != 0)
                {
                    // ✅ GET FROM POOL - these will be added to chart
                    var newScatter = ScatterPointsPool.Instance.Get();
                    newScatter.X = sharedTS;
                    newScatter.Y = lob.Price.Value;
                    newScatter.Size = visualSize;
                    newScatter.Value = lob.Size.Value;
                    scatterPoints.Add(newScatter);
                }
            }
            return scatterPoints; // ← List will be returned to pool, ScatterPoints will live in chart
        }
        private IEnumerable<DataPoint> ToDataPointsCumulativeVolume(List<BookItem> lobList, double sharedTS)
        {
            var retItems = new List<DataPoint>(lobList.Count);
            double cumulativeVol = 0;

            foreach (var level in lobList)
            {
                // ✅ FIX: Validate all inputs
                if (!level.Price.HasValue || !level.Size.HasValue)
                    continue;

                if (level.Price.Value <= 0 || level.Size.Value <= 0)
                    continue;
                // ✅ FIX: Check for NaN/Infinity
                if (double.IsNaN(level.Price.Value) || double.IsInfinity(level.Price.Value))
                    continue;

                if (double.IsNaN(level.Size.Value) || double.IsInfinity(level.Size.Value))
                    continue;

                cumulativeVol += level.Size.Value;

                // ✅ FIX: Validate cumulative volume
                if (double.IsNaN(cumulativeVol) || double.IsInfinity(cumulativeVol))
                    continue;

                retItems.Add(new DataPoint(level.Price.Value, cumulativeVol));
            }

            return retItems;
        }
        private void AddPointToSpreadChart(DataPoint spreadPoint)
        {
            if (RealTimeSpreadModel.Series[0] is OxyPlot.Series.LineSeries _spreadSeries)
            {
                _spreadSeries.Points.Add(spreadPoint);
            }
        }
        private double AddPointsToCumulativeAskVolumeChart(IEnumerable<DataPoint> cumulativePoints)
        {
            double retMaxValue = 0;
            var _cumSeriesAsks = CummulativeAsksChartModel.Series[0] as OxyPlot.Series.TwoColorAreaSeries;
            if (_cumSeriesAsks == null)
                return 0;

            var newPoints = new List<DataPoint>();
            foreach (var p in cumulativePoints)
            {
                if (p.IsDefined())
                {
                    newPoints.Add(p);
                    if (p.Y > retMaxValue)
                    {
                        retMaxValue = p.Y;
                    }
                }
            }
            _cumSeriesAsks.Points.Clear();
            _cumSeriesAsks.Points.AddRange(newPoints);


            return retMaxValue;
        }
        private double AddPointsToCumulativeBidVolumeChart(IEnumerable<DataPoint> cumulativePoints)
        {
            double retMaxValue = 0;
            var _cumSeriesBids = CummulativeBidsChartModel.Series[0] as OxyPlot.Series.TwoColorAreaSeries;
            if (_cumSeriesBids == null)
                return 0;

            var newPoints = new List<DataPoint>();
            foreach (var p in cumulativePoints)
            {
                if (p.IsDefined())
                {
                    newPoints.Add(p);
                    if (p.Y > retMaxValue)
                    {
                        retMaxValue = p.Y;
                    }
                }
            }
            _cumSeriesBids.Points.Clear();
            _cumSeriesBids.Points.AddRange(newPoints);

            return retMaxValue;
        }
        private void SetMaximumsToCumulativeBidVolumeCharts(double maxCumulativeVol)
        {
            var _cumSeriesBids = CummulativeBidsChartModel.Series[0] as OxyPlot.Series.TwoColorAreaSeries;
            if (_cumSeriesBids?.YAxis != null)
            {
                _cumSeriesBids.YAxis.Maximum = maxCumulativeVol;
            }
        }
        private void SetMaximumsToCumulativeAskVolumeCharts(double maxCumulativeVol)
        {
            var _cumSeriesAsks = CummulativeAsksChartModel.Series[0] as OxyPlot.Series.TwoColorAreaSeries;
            if (_cumSeriesAsks?.YAxis != null)
            {
                _cumSeriesAsks.YAxis.Maximum = maxCumulativeVol;
            }
        }
        private void AddPointsToScatterPriceChart(DataPoint? bidPricePoint, 
            DataPoint? askPricePoint,
            DataPoint midPricePoint,
            IEnumerable<OxyPlot.Series.ScatterPoint> bidLevelPoints,
            IEnumerable<OxyPlot.Series.ScatterPoint> askLevelPoints)
        {
            foreach (var serie in RealTimePricePlotModel.Series)
            {
                if (serie is OxyPlot.Series.LineSeries _serie)
                {
                    if (serie.Title == "MidPrice")
                        _serie.Points.Add(midPricePoint);
                    else if (serie.Title == "Ask" && askPricePoint != null)
                    {
                        _serie.Points.Add(askPricePoint.Value);
                    }
                    else if (serie.Title == "Bid" && bidPricePoint != null)
                        _serie.Points.Add(bidPricePoint.Value);
                }
                else if ( serie is OxyPlot.Series.ScatterSeries _scatter)
                {
                    if (_scatter.ColorAxis is LinearColorAxis colorAxis)
                    {
                        //we have defined min/max when normalizing the Size in "GenerateSinglePoint_RealTimePrice" method.
                        colorAxis.Minimum = 1;
                        colorAxis.Maximum = 10;
                    }
                    if (serie.Title == "ScatterAsks" && askLevelPoints != null)
                    {
                        _scatter.Points.AddRange(askLevelPoints);
                    }
                    else if (serie.Title == "ScatterBids" && bidLevelPoints != null)
                    {
                        _scatter.Points.AddRange(bidLevelPoints);
                    }
                }
            }
        }
        private void UpdateLocalValues(OrderBookSnapshot orderBook)
        {
            _decimalPlaces = orderBook.PriceDecimalPlaces;

            _BidTOB = orderBook.GetTOB(true);
            _AskTOB = orderBook.GetTOB(false);
            _MidPoint = orderBook.MidPrice;
            _Spread = orderBook.Spread;
            _lobImbalanceValue = orderBook.ImbalanceValue;

            if (_AskTOB != null && _AskTOB.Price.HasValue && _AskTOB.Size.HasValue)
                _AskTOB_SPLIT.SetNumber(_AskTOB.Price.Value, _AskTOB.Size.Value, _decimalPlaces);
            if (_BidTOB != null && _BidTOB.Price.HasValue && _BidTOB.Size.HasValue)
                _BidTOB_SPLIT.SetNumber(_BidTOB.Price.Value, _BidTOB.Size.Value, _decimalPlaces);
        }
        private void RemoveLastPointToSpreadChart()
        {
            if (RealTimeSpreadModel.Series[0] is OxyPlot.Series.LineSeries _spreadSeries && _spreadSeries.Points.Count > 0)
            {
                _spreadSeries.Points.RemoveAt(0);
            }
        }
        private void RemoveLastPointsToScatterChart()
        {
            double? tsToRemove = null;

            // First pass: Get timestamp from line series and remove points
            foreach (var serie in RealTimePricePlotModel.Series)
            {
                if (serie is OxyPlot.Series.LineSeries _serie && _serie.Points.Count > 0)
                {
                    if (serie.Title == "MidPrice" || serie.Title == "Ask" || serie.Title == "Bid")
                    {
                        if (tsToRemove == null)
                            tsToRemove = _serie.Points[0].X;
                        _serie.Points.RemoveAt(0);
                    }
                }
            }

            if (tsToRemove == null)
                return;

            // Second pass: Handle scatter series with bounds checking
            foreach (var serie in RealTimePricePlotModel.Series)
            {
                if (serie is OxyPlot.Series.ScatterSeries _scatter && _scatter.Points.Count > 0)
                {
                    if (serie.Title == "ScatterAsks" || serie.Title == "ScatterBids")
                    {
                        // ✅ CRITICAL: Return to pool ONLY when removing from chart
                        while (_scatter.Points.Count > 0 && _scatter.Points[0].X <= tsToRemove)
                        {
                            var pointToRemove = _scatter.Points[0];
                            _scatter.Points.RemoveAt(0); // ← Remove from chart FIRST
                            ScatterPointsPool.Instance.Return(pointToRemove); // ← THEN return to pool
                        }
                    }
                }
            }
        }

        private void Clear()
        {
            HelperTrade.Instance.Unsubscribe(TRADES_OnDataReceived);
            HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);



            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                lock (MTX_TRADES)
                {
                    _realTimeTrades.Clear();
                    TradesDisplay.Clear();
                    RaisePropertyChanged(nameof(TradesDisplay));
                }
            });

            _QUEUE.Clear(); //make this outside the LOCK, otherwise we could run into a deadlock situation when calling back 
            //clean series
            lock (MTX_SNAPSHOTS)
            { 
                RealTimeSpreadModel?.Series.OfType<OxyPlot.Series.LineSeries>().ToList().ForEach(x => x.Points.Clear());
                RealTimePricePlotModel?.Series.OfType<OxyPlot.Series.LineSeries>().ToList()
                    .ForEach(x => x.Points.Clear());
                RealTimePricePlotModel?.Series.OfType<OxyPlot.Series.ScatterSeries>().ToList()
                    .ForEach(x => x.Points.Clear());
                _bidsGrid.Clear();
                _asksGrid.Clear();

                CummulativeAsksChartModel?.Series.OfType<OxyPlot.Series.TwoColorAreaSeries>().ToList().ForEach(x => x.Points.Clear());
                CummulativeBidsChartModel?.Series.OfType<OxyPlot.Series.TwoColorAreaSeries>().ToList().ForEach(x => x.Points.Clear());

                _AskTOB = new BookItem();
                _BidTOB = new BookItem();
                _MidPoint = 0;
                _Spread = 0;
                _depthGrid.Clear();

                _AskTOB_SPLIT.Clear();
                _BidTOB_SPLIT.Clear();

                if (_AGGREGATED_LOB != null)
                {
                    _AGGREGATED_LOB.OnRemoved -= _AGGREGATED_LOB_OnRemoved;
                    _AGGREGATED_LOB.OnRemoving -= _AGGREGATED_LOB_OnRemoving;
                    _AGGREGATED_LOB.Dispose();
                }
                _AGGREGATED_LOB = new AggregatedCollection<OrderBookSnapshot>(_aggregationLevelSelection, _MAX_CHART_POINTS,
                    x => x.LastUpdated, _AGGREGATED_LOB_OnAggregating);
                _AGGREGATED_LOB.OnRemoved += _AGGREGATED_LOB_OnRemoved;
                _AGGREGATED_LOB.OnRemoving += _AGGREGATED_LOB_OnRemoving;
            }

            OrderBookSnapshotPool.Instance.Reset(); //reset the entire pool
            ScatterPointsPool.Instance.Reset();

            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                uiUpdaterAction(); //update ui before the Timer starts again.
                if (uiUpdater != null)
                {
                    uiUpdater.Stop();
                    uiUpdater.Dispose();
                }

                var _aggregationForUI = _aggregationLevelSelection.ToTimeSpan();
                if (_aggregationForUI < _MIN_UI_REFRESH_TS)
                    _aggregationForUI = _MIN_UI_REFRESH_TS;
                uiUpdater = new UIUpdater(uiUpdaterAction, _aggregationForUI.TotalMilliseconds);
                uiUpdater.Start();
            });

            HelperTrade.Instance.Subscribe(TRADES_OnDataReceived);
            HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);


        }


        /// <summary>
        /// Bids the ask grid update.
        /// Update our internal lists trying to re-use the current items on the list.
        /// Avoiding allocations as much as possible.
        /// </summary>
        /// <param name="orderBook">The order book.</param>
        private void BidAskGridUpdate(OrderBookSnapshot orderBook)
        {
            if (orderBook == null)
                return;

            GridListUpdate(_asksGrid, orderBook.Asks);
            GridListUpdate(_bidsGrid, orderBook.Bids);

            //commented out for now
            /*if (_asksGrid != null && _bidsGrid != null)
            {
                _depthGrid.Clear();
                foreach (var item in _asksGrid)
                    _depthGrid.Add(item);
                foreach (var item in _bidsGrid)
                    _depthGrid.Add(item);
            }*/
        }

        private void GridListUpdate(List<BookItem> currentList, List<BookItem> newList)
        {
            // Update existing items and add/remove as needed
            for (int i = 0; i < Math.Max(currentList.Count, newList.Count); i++)
            {
                if (i < newList.Count)
                {
                    if (i < currentList.Count)
                        UpdateBookItem(currentList[i], newList[i]); // Update existing item
                    else
                    {
                        var newItem = new BookItem();
                        UpdateBookItem(newItem, newList[i]);
                        currentList.Add(newItem); // Add new item
                    }
                }
                else if (i < currentList.Count)
                {
                    currentList.RemoveAt(currentList.Count - 1); // Remove extra items
                }
            }
        }
        private void UpdateBookItem(BookItem target, BookItem source)
        {
            target.Price = source?.Price;
            target.Size = source?.Size;
            target.PriceDecimalPlaces = source.PriceDecimalPlaces;
            target.SizeDecimalPlaces = source.SizeDecimalPlaces;
        }


        private void ALLSYMBOLS_CollectionChanged(object? sender, string e)
        {
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _symbols.Add(e);
            }));

        }
        private void TRADES_OnDataReceived(VisualHFT.Model.Trade e)
        {
            if (_selectedProvider == null || _selectedProvider.ProviderID <= 0 || _selectedProvider.ProviderID != e?.ProviderId)
                return;
            if (string.IsNullOrEmpty(_selectedSymbol) || _selectedSymbol != e?.Symbol)
                return;

            lock (MTX_TRADES)
            {
                _realTimeTrades.Push(e);
                _TRADEDATA_AVAILABLE = true;
            }
        }
        private void PROVIDERS_OnDataReceived(object? sender, VisualHFT.Model.Provider e)
        {
            if (_providers.All(x => x.ProviderCode != e.ProviderCode))
            {
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    var item = new ViewModel.Model.Provider(e);
                    if (_providers.All(x => x.ProviderCode != e.ProviderCode))
                        _providers.Add(item);
                    //if nothing is selected
                    if (_selectedProvider == null) //default provider must be the first who's Active
                        SelectedProvider = item;
                }));
            }
        }
        private void PROVIDERS_OnStatusChanged(object? sender, VisualHFT.Model.Provider e)
        {
            if (_selectedProvider == null || _selectedProvider.ProviderCode != e.ProviderCode)
                return;

            if (_selectedProvider.Status != e.Status)
            {
                _selectedProvider.Status = e.Status;
                Clear();
            }
        }

        public ObservableCollection<string> SymbolList => _symbols;
        public string SelectedSymbol
        {
            get => _selectedSymbol;
            set => SetProperty(ref _selectedSymbol, value, onChanged: () => Clear());
        }
        public VisualHFT.ViewModel.Model.Provider SelectedProvider
        {
            get => _selectedProvider;
            set => SetProperty(ref _selectedProvider, value, onChanged: () => Clear());
        }
        public string SelectedLayer { get; set; }
        public ObservableCollection<Tuple<string, AggregationLevel>> AggregationLevels { get; set; }

        public AggregationLevel AggregationLevelSelection
        {
            get => _aggregationLevelSelection;
            set => SetProperty(ref _aggregationLevelSelection, value, onChanged: () => Clear());
        }

        public ObservableCollection<VisualHFT.ViewModel.Model.Provider> Providers => _providers;
        public Model.BookItemPriceSplit BidTOB_SPLIT => _BidTOB_SPLIT;
        public Model.BookItemPriceSplit AskTOB_SPLIT => _AskTOB_SPLIT;

        public double LOBImbalanceValue => _lobImbalanceValue;
        public double MidPoint => _MidPoint;
        public double Spread => _Spread;


        public IReadOnlyList<BookItem> Asks
        {
            get
            {
                lock (MTX_SNAPSHOTS)
                {
                    return _asksGrid.AsReadOnly(); // ReadOnlyCollection<T> wrapper - no copy!
                }
            }
        }

        public IReadOnlyList<BookItem> Bids
        {
            get
            {
                lock (MTX_SNAPSHOTS)
                {
                    return _bidsGrid.AsReadOnly(); // ReadOnlyCollection<T> wrapper - no copy!
                }
            }
        }
        public IEnumerable<BookItem> Depth => _depthGrid;
        public ObservableCollection<VisualHFT.Model.Trade> TradesDisplay { get; }

        public PlotModel RealTimePricePlotModel { get; set; }
        public PlotModel RealTimeSpreadModel { get; set; }
        public PlotModel CummulativeBidsChartModel { get; set; }
        public PlotModel CummulativeAsksChartModel { get; set; }


        public int SwitchView
        {
            get => _switchView;
            set => SetProperty(ref _switchView, value);
        }


        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    uiUpdater?.Stop();
                    uiUpdater?.Dispose();

                    HelperSymbol.Instance.OnCollectionChanged -= ALLSYMBOLS_CollectionChanged;
                    HelperProvider.Instance.OnDataReceived -= PROVIDERS_OnDataReceived;
                    HelperProvider.Instance.OnStatusChanged -= PROVIDERS_OnStatusChanged;
                    HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
                    HelperTrade.Instance.Unsubscribe(TRADES_OnDataReceived);

                    _dialogs = null;
                    _realTimeTrades?.Clear();
                    _depthGrid?.Clear();
                    _bidsGrid?.Clear();
                    _asksGrid?.Clear();
                    _providers?.Clear();
                    ScatterPointsPool.Instance.Dispose();
                    OrderBookSnapshotPool.Instance.Dispose();
                    ScatterPointsListPool.Instance.Dispose();
                    _QUEUE?.Dispose();

                    if (_AGGREGATED_LOB != null)
                    {
                        _AGGREGATED_LOB.OnRemoved -= _AGGREGATED_LOB_OnRemoved;
                        _AGGREGATED_LOB.OnRemoving -= _AGGREGATED_LOB_OnRemoving;
                    }
                    _AGGREGATED_LOB?.Dispose();
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
