using Studies.MarketResilience.Model;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.Studies.MarketResilience.Model;
#if WINDOWS
using VisualHFT.Studies.MarketResilience.UserControls;
#endif
#if WINDOWS
using VisualHFT.Studies.MarketResilience.ViewModel;
#endif
using VisualHFT.UserSettings;

namespace VisualHFT.Studies
{
    public class MarketResilienceStudy : BasePluginStudy
    {
        private const string ValueFormat = "N1";
        private PlugInSettings _settings;


        private MarketResilienceCalculator mrCalc;
        private HelperCustomQueue<OrderBookSnapshot> _QUEUE;



        // Event declaration
        public override event EventHandler<decimal> OnAlertTriggered;

        // Emits a metric via AddCalculation -> the trigger picker lists this study (IStudy.EmitsMetric).
        public override bool EmitsMetric => true;


        public override string Name { get; set; } = "Market Resilience Study";
        public override string Version { get; set; } = "1.0.0";
        // 🛑 DO NOT EDIT THIS STRING. GetPluginUniqueID() hashes Name + Author + Version +
        // Description + assembly name, and that hash is the key under which this tile's settings
        // are saved and loaded. Changing a single character orphans every existing user's symbol
        // and provider selection: the tile reloads with an empty configuration and goes dead.
        // It also unhooks any alert rule registered against this study.
        // Corrected, user-facing wording lives in TileToolTip below, which is not hashed.
        public override string Description { get; set; } = "Measures market recovery speed after large trades using time, spread, and depth recovery metrics. Provides real-time resilience scoring (0-1) to assess market stability and sentiment for trading decisions.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }
        public override string TileTitle { get; set; } = "MR";
        // The tooltip is fixed text, so it is a constant rather than an instance initializer: the
        // tile can then read it from any instance, however that instance was created.
        private const string TileToolTipText =
            "<b>Market Resilience</b> (MR) measures how well the order book withstands and recovers from a large trade.<br/>" +
            "One event = one large print (more than 2 dispersions above the recent size mean) followed by a confirmed depth depletion and/or a spread widening.<br/><br/>" +
            "The score blends four components:<br/>" +
            "1. <b>Trade severity (30%)</b>: how far above normal the anchoring print was.<br/>" +
            "2. <b>Spread recovery (10%)</b>: how fast the spread returned to its mean, against this session's history.<br/>" +
            "3. <b>Depth recovery (50%)</b>: how fast the depleted side climbed back to 90% of its pre-shock depth, against this session's history.<br/>" +
            "4. <b>Spread magnitude (10%)</b>: how wide the shock spread was relative to the usual spread.<br/><br/>" +
            "Only the depleted side counts as recovery. If it does not regain 90% of its depth before the Max Shock Timeout, the event is scored as a non-recovery and the depth component reads 0 at its full weight.<br/>" +
            "<b>The price has to come back too.</b> A side counts as recovered only if its best price returns to within one typical spread of where it was quoted just before the depletion. Size that reappears further away than that is the market repricing, not the book recovering, and it is scored as a non-recovery.<br/>" +
            "The first recovery of a session is not published; it seeds the history the next one is compared to.<br/><br/>" +
            "<b>Warm-up:</b> the depth baseline needs 200 book updates before a depletion can be detected. The tile is flagged stale until then.<br/><br/>" +
            "<b>Reading the score.</b> It is <b>relative to this instrument's own recent behaviour</b>, not an absolute percentage. 0.7 means this recovery was faster than this book's own recent average — it does not mean 70% of the liquidity came back.<br/>" +
            "- Strong: MR ≥ 0.7<br/>" +
            "- Moderate: 0.3 ≤ MR &lt; 0.7<br/>" +
            "- Weak: MR &lt; 0.3<br/><br/>" +
            "<b>A bigger shock lowers the score.</b> Components 1 and 4 are penalties, so a book that absorbs a very large print perfectly still reads lower than one that absorbs a small print equally well. Read the number as how much stress this book is under, not only how well it bounced back.<br/><br/>" +
            "<b>What this is inferred from.</b> Depth comes from aggregated price-level sizes, so a cancelled order cannot be told apart from a filled one, and the reading depends on how many levels your venue publishes.<br/><br/>" +
            "The score persists until the next event replaces it.";
        private string _tileToolTip;
        public override string TileToolTip { get => _tileToolTip ?? TileToolTipText; set => _tileToolTip = value; }

        public MarketResilienceStudy()
        {
            _QUEUE = new HelperCustomQueue<OrderBookSnapshot>($"<OrderBookSnapshot>_{this.Name}", QUEUE_onRead, QUEUE_onError);
        }
        ~MarketResilienceStudy()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {
            await base.StartAsync();//call the base first

            mrCalc = new MarketResilienceCalculator(_settings);
            _QUEUE.Clear();

            HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);
            HelperTrade.Instance.Subscribe(TRADE_OnDataReceived);

            log.Info($"{this.Name} Plugin has successfully started.");
            Status = ePluginStatus.STARTED;
        }

        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");

            HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
            HelperTrade.Instance.Unsubscribe(TRADE_OnDataReceived);

            await base.StopAsync();
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

            if (e == null)
                return;
            if (_settings.Provider.ProviderID != e.ProviderID || _settings.Symbol != e.Symbol)
                return;

            // ✅ CHANGED: Use struct factory method instead of pool
            var snapshot = OrderBookSnapshot.Create();
            // Initialize its state based on the master OrderBook.
            snapshot.UpdateFrom(e);
            // Enqueue for processing.
            _QUEUE.Add(snapshot);
        }
        private void TRADE_OnDataReceived(Trade e)
        {
            if (e == null)
                return;
            if (_settings.Provider.ProviderID != e.ProviderId || _settings.Symbol != e.Symbol)
                return;

            mrCalc.OnTrade(e);
            DoCalculationAndSend();
        }
        private void QUEUE_onRead(OrderBookSnapshot e)
        {
            try
            {
                mrCalc.OnOrderBookUpdate(e);
                DoCalculationAndSend();
            }
            finally
            {
                // The calculator keeps no reference to this snapshot, so the pooled arrays are
                // returned here, on every path.
                e.Dispose();
            }
        }
        private void QUEUE_onError(Exception ex)
        {
            var _error = $"Unhandled error in the Queue: {ex.Message}";
            log.Error(_error, ex);
            HelperNotificationManager.Instance.AddNotification(this.Name, _error,
                HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);

            Task.Run(() => HandleRestart(_error, ex));
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
            // Aggregation: last.
            // The score changes only when an event completes, and is republished unchanged on every
            // book update and every trade in between. Averaging within the bucket therefore weights
            // the result by message rate rather than by time: the same score sitting through a busy
            // period is counted hundreds of times, and once in a quiet one. Two users watching the
            // same market on venues with different update rates would read different numbers. The
            // last value in the bucket is the score as it actually stood.
            var existing = dataCollection[^1]; // Get the last item in the collection
            existing.Value = newItem.Value;
            existing.Format = ValueFormat;
            existing.MarketMidPrice = newItem.MarketMidPrice;
            existing.IsStale = newItem.IsStale;
            existing.Tooltip = newItem.Tooltip;
            existing.ValueColor = newItem.ValueColor;

            base.onDataAggregation(dataCollection, newItem, lastItemAggregationCount);
        }

        protected void DoCalculationAndSend()
        {
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED) return;

            // Thread-safe read: snapshot all output values under lock
            var (mrScore, _, midPrice) = mrCalc.GetOutputSnapshot();

            var newItem = new BaseStudyModel
            {
                Value = mrScore,
                Format = ValueFormat,
                MarketMidPrice = midPrice,
                Timestamp = HelperTimeProvider.Now
            };

            // Until the depth baseline is warm no depletion can be detected, so the score is the
            // cold-start default rather than a measurement. Flag it the same way the platform
            // flags a provider that stopped sending data.
            if (!mrCalc.IsBaselineWarm)
            {
                newItem.IsStale = true;
                newItem.ValueColor = "Orange";
                newItem.Tooltip = "Warming up: " + mrCalc.WarmUpProgress + " of " +
                                  MarketResilienceCalculator.WarmUpSamplesRequired + " book updates";
            }

            AddCalculation(newItem);
        }



        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    // Stop queue FIRST to prevent new items arriving after disposal
                    _QUEUE.Dispose();

                    HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
                    HelperTrade.Instance.Unsubscribe(TRADE_OnDataReceived);

                    mrCalc?.Dispose();
                }

                base.Dispose(disposing);
            }
        }
        protected override void LoadSettings()
        {
            _settings = LoadFromUserSettings<PlugInSettings>();
            if (_settings == null)
            {
                InitializeDefaultSettings();
            }


            //To prevent back compability with older setting formats
            if (_settings.Provider == null)
            {
                _settings.Provider = new Provider();
            }
            if (_settings.MaxShockMsTimeout == null)//To prevent back compability with older setting formats
            {
                InitializeDefaultSettings();
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
                AggregationLevel = AggregationLevel.Ms500,
                MaxShockMsTimeout = 800,
            };
            SaveToUserSettings(_settings);
        }
        public override object GetUISettings()
        {
#if !WINDOWS
            return null;
#else
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.SelectedSymbol = _settings.Symbol;
            viewModel.SelectedProviderID = _settings.Provider.ProviderID;
            viewModel.AggregationLevelSelection = _settings.AggregationLevel;
            viewModel.MaxShockMsTimeout = _settings.MaxShockMsTimeout ?? 0;
            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.Symbol = viewModel.SelectedSymbol;
                _settings.Provider = viewModel.SelectedProvider;
                _settings.AggregationLevel = viewModel.AggregationLevelSelection;
                _settings.MaxShockMsTimeout = viewModel.MaxShockMsTimeout;
                SaveSettings();

                //run this because it will allow to restart with the new values
                Task.Run(async () => await HandleRestart($"{this.Name} is starting (from reloading settings).", null, true));
            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
#endif
        }

    }
}
