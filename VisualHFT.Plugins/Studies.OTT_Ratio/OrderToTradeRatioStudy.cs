using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.Studies.MarketRatios.Model;
using VisualHFT.Studies.MarketRatios.UserControls;
using VisualHFT.Studies.MarketRatios.ViewModel;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies
{
    /// <summary>
    /// The Order to Trade Ratio (OTR) 
    /// It is calculated by dividing the number of market orders events (new/update/delete) by the number of trades executed. 
    /// This ratio is often used by regulators to identify potentially manipulative or disruptive trading behavior. 
    /// 
    /// A high OTR may indicate that a trader is placing a large number of orders but executing very few, which could be a sign of market manipulation tactics like layering or spoofing.
    /// 
    /// DUAL MODE OPERATION (L2/L3 ORDERBOOK COMPATIBILITY):
    /// ---------------------------------------------------
    /// This plugin handles both Level 3 (full order book) and Level 2 (price level) market data:
    /// 
    /// - Level 3 Orderbook: Provides individual order-level data with unique order IDs
    ///   - Order events come directly from HelperMarketDataStats events
    ///   - Trades come from HelperMarketDataStats events
    ///   - Formula: OTR = (Adds + 2×Updates + Cancels) / max(Trades, 1) - 1
    /// 
    /// - Level 2 Orderbook: Provides aggregated price level data (no individual orders)
    ///   - Order events derived from HelperOrderBook.GetCounters() delta calculations
    ///   - Trades come from HelperMarketDataStats events
    ///   - Formula: OTR = (AddedΔ + 2×UpdatedΔ + DeletedΔ) / max(Trades, 1) - 1
    /// 
    /// MODE DETECTION MECHANISM:
    /// ------------------------
    /// The plugin automatically detects which type of data is available:
    /// 1. Initially assumes L3 data is present for the first 10 seconds
    /// 2. If no L3-specific order events are detected during this time, switches to L2 mode
    /// 3. Once in L2 mode, uses HelperOrderBook.GetCounters() for order metrics
    /// 4. Mode switching is permanent until plugin restart (no L2→L3 switching)
    /// 
    /// CALCULATION CONSISTENCY:
    /// -----------------------
    /// Both modes use identical formulas to ensure regulatory compliance:
    /// • Updates are weighted 2× to reflect their dual nature (cancel + add)
    /// • Floor value of 1 prevents division by zero
    /// • Subtraction of 1 normalizes the baseline ratio
    /// • Thread-safe operations using Interlocked for high-frequency environments
    /// 
    /// DATA FLOW:
    /// ----------
    /// L3 Mode:
    ///   HelperMarketDataStats → Individual Events → Accumulate → Calculate OTR
    /// 
    /// L2 Mode:
    ///   HelperOrderBook → Cumulative Counters → Delta Calculation → Accumulate → Calculate OTR
    ///   HelperMarketDataStats → Trade Events → Accumulate
    /// 
    /// THREAD SAFETY:
    /// --------------
    /// • volatile bool for mode detection
    /// • Interlocked operations for counter updates
    /// • State capture before mode switching decisions
    /// • Counter reset during L3→L2 transition to prevent contamination
    /// 
    /// AGGREGATION BEHAVIOR:
    /// --------------------
    /// • Counters accumulate during aggregation period (e.g., 1 second)
    /// • Final OTR calculated at end of period: total_events / total_trades
    /// • Counters reset at start of new aggregation period via onDataAdded()
    /// • "Last value" aggregation strategy overwrites intermediate calculations
    /// 
    /// EDGE CASE HANDLING:
    /// ------------------
    /// • Initial L2 call: Skip to prevent historical counter corruption
    /// • Zero trades: Use floor value of 1 to prevent division by zero
    /// • Mode switching: Reset all counters to ensure clean transition
    /// • Provider/symbol mismatch: Events ignored for filtering
    /// 
    /// REGULATORY COMPLIANCE:
    /// ---------------------
    /// • Consistent OTR values across L2/L3 modes for same market activity
    /// • Thread-safe calculations for high-frequency trading environments
    /// • Accurate event counting without double-counting or data loss
    /// • Proper aggregation respecting time-based measurement periods
    /// </summary>
    public class OrderToTradeRatioStudy : BasePluginStudy
    {
        private const string ValueFormat = "N1";
        private bool _disposed = false; // to track whether the object has been disposed
        private PlugInSettings _settings;

        private long _orderEvents = 0;
        private long _tradeCount = 0;
        private object _lock = new object();
        private decimal _lastMarketMidPrice = 0; //keep track of market price

        private long _prevAdded = 0;
        private long _prevDeleted = 0;
        private long _prevUpdated = 0;
        private long _floorNum = 1; // Default floor; configurable if needed
        private bool _isFirstL2Call = true;

        // L3/L2 Mode Detection Variables
        private volatile bool _IsCurrentLOB_L3 = false;
        private DateTime _startedCheckLOB_L3 = DateTime.MinValue;
        private TimeSpan _LOB3_Identification_Timespan = TimeSpan.FromSeconds(10);

        // Event declaration
        public override event EventHandler<decimal> OnAlertTriggered;

        public override string Name { get; set; } = "Order To Trade Ratio Study Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Order-to-Trade Ratio measures order book activity vs executed public trades. Regulatory metric for detecting potential market manipulation.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }
        public override string TileTitle { get; set; } = "OTR";
        public override string TileToolTip { get; set; } = "The <b> OTR</b> (Order-to-Trade Ratio) is a metric used to analyze order book activity and its relationship to executed trades. <br/> It's calculated using <i>aggregated  market data</i>, which provides snapshots of the total order volume at each price level, rather than individual order actions.  Because of this, the  OTR represents the <b>net change in order book depth</b> relative to the number of trades.<br/><br/>" +
                                                           "<b>Calculation:</b> <i> OTR Ratio = (Sum of Absolute Changes in the Limit Order Book at All Price Levels) / (Number of Executed Trades)</i><br/><br/>" +
                                                           "<b>Interpretation and Limitations:</b><br/>" +
                                                           "<ul>" +
                                                           "<li>A high  OTR *may* indicate low liquidity, high-frequency trading activity, or potential order book manipulation (e.g., spoofing). However, because it's based on aggregated data, it cannot definitively distinguish between these scenarios.</li>" +
                                                           "<li>A single large order that is partially filled multiple times will increase the  OTR, as each partial fill registers as a size change.</li>" +
                                                           "<li>The  OTR is a *proxy* for order activity and should be used in conjunction with other market microstructure metrics (spread, volume, order book imbalance) for a complete analysis.</li>" +
                                                           "</ul>" +
                                                           "Regulatory bodies often monitor similar metrics to identify potentially manipulative or disruptive trading activities, although they typically have access to more granular data.<br/>" +
                                                           "<b>Note:</b> This plugin automatically detects and uses Level 3 (individual order) data when available, falling back to Level 2 (price level) data otherwise.";
        public OrderToTradeRatioStudy()
        {
        }
        ~OrderToTradeRatioStudy()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {
            await base.StartAsync();//call the base first

            _startedCheckLOB_L3 = HelperTimeProvider.Now; //start checking for L3
            _IsCurrentLOB_L3 = true; //initial assumption is that L3 Orderbook is present

            HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);

            log.Info($"{this.Name} Plugin has successfully started.");
            Status = ePluginStatus.STARTED;
        }
        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");

            HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);

            await base.StopAsync();
        }

        private void LIMITORDERBOOK_OnDataReceived(OrderBook e)
        {
            if (e == null)
                return;
            if (_settings.Provider.ProviderID != e.ProviderID || _settings.Symbol != e.Symbol)
                return;

            // Check if we should switch from L3 to L2 mode
            bool currentMode = _IsCurrentLOB_L3; // Capture current state
            if (currentMode && (HelperTimeProvider.Now - _startedCheckLOB_L3) > _LOB3_Identification_Timespan)
            {
                _IsCurrentLOB_L3 = false;

                // Reset counters when switching modes
                Interlocked.Exchange(ref _orderEvents, 0);
                Interlocked.Exchange(ref _tradeCount, 0);

                log.Info($"{this.Name} switched from L3 to L2 mode after timeout.");
            }
            if (!_IsCurrentLOB_L3) // Process L2 data when L3 is not available
            {
                var counters = e.GetCounters();
                if (_isFirstL2Call)
                {
                    // Initialize previous counters on the first call
                    _prevAdded = counters.added;
                    _prevDeleted = counters.deleted;
                    _prevUpdated = counters.updated;
                    _isFirstL2Call = false;
                    return; // Skip calculation on the first call
                }
                long addedDelta = counters.added - _prevAdded;
                long deletedDelta = counters.deleted - _prevDeleted;
                long updatedDelta = counters.updated - _prevUpdated;

                _prevAdded = counters.added;
                _prevDeleted = counters.deleted;
                _prevUpdated = counters.updated;

                _lastMarketMidPrice = (decimal)e.MidPrice;
                Interlocked.Add(ref _orderEvents, addedDelta + deletedDelta + 2 * updatedDelta); // Accumulate deltas, double-count updates
                DoCalculationAndSend();
            }

        }
        private void TRADES_OnDataReceived(Trade e)
        {
            if (_settings.Provider.ProviderID != e.ProviderId || _settings.Symbol != e.Symbol)
                return;
            if (!e.IsBuy.HasValue) //we do not know what it is
                return;

            Interlocked.Increment(ref _tradeCount);
            DoCalculationAndSend();
        }

        private void ResetCalculations()
        {
            Interlocked.Exchange(ref _orderEvents, 0);
            Interlocked.Exchange(ref _tradeCount, 0);
        }
        protected void DoCalculationAndSend()
        {
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED) return;

            long orderEvents = Interlocked.Read(ref _orderEvents);
            long tradeCount = Interlocked.Read(ref _tradeCount);
            long denom = Math.Max(tradeCount, _floorNum);
            decimal orderToTradeRatio = denom == 0 ? 0 : (decimal)orderEvents / denom - 1; // Standard formula with floor

            var newItem = new BaseStudyModel();
            newItem.Value = orderToTradeRatio;
            newItem.Format = ValueFormat;
            newItem.MarketMidPrice = _lastMarketMidPrice;
            newItem.Timestamp = HelperTimeProvider.Now;

            AddCalculation(newItem);
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
        protected override void onDataAggregation(List<BaseStudyModel> dataCollection, BaseStudyModel newItem, int lastItemAggregationCount)
        {
            //Aggregation: last
            var existing = dataCollection[^1]; // Get the last item in the collection
            existing.Value = newItem.Value;
            existing.Format = newItem.Format;
            existing.MarketMidPrice = newItem.MarketMidPrice;

            base.onDataAggregation(dataCollection, newItem, lastItemAggregationCount);
        }

        /// <summary>
        /// Resets order events and trade count when a new data point is added to the aggregated series
        /// </summary>
        protected override void onDataAdded()
        {
            ResetCalculations();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    // Dispose managed resources here
                    HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
                    base.Dispose();
                }
            }
        }
        protected override void LoadSettings()
        {
            _settings = LoadFromUserSettings<PlugInSettings>();
            if (_settings == null)
            {
                InitializeDefaultSettings();
            }
            if (_settings.Provider == null) //To prevent back compability with older setting formats
            {
                _settings.Provider = new Provider();
            }
        }

        protected override void SaveSettings()
        {
            SaveToUserSettings(_settings);
        }

        protected override void InitializeDefaultSettings()
        {
            _settings = new PlugInSettings()
            {
                Symbol = "",
                Provider = new Provider(),
                AggregationLevel = AggregationLevel.Ms100
            };
            SaveToUserSettings(_settings);
        }
        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.SelectedSymbol = _settings.Symbol;
            viewModel.SelectedProviderID = _settings.Provider.ProviderID;
            viewModel.AggregationLevelSelection = _settings.AggregationLevel;

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.Symbol = viewModel.SelectedSymbol;
                _settings.Provider = viewModel.SelectedProvider;
                _settings.AggregationLevel = viewModel.AggregationLevelSelection;

                SaveSettings();
                //run this because it will allow to restart with the new values
                Task.Run(async () => await HandleRestart($"{this.Name} is starting (from reloading settings).", null, true));

            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
        }

    }
}
