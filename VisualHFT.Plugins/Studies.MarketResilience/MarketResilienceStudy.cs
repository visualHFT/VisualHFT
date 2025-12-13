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
using VisualHFT.Studies.MarketResilience.UserControls;
using VisualHFT.Studies.MarketResilience.ViewModel;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies
{
    public class MarketResilienceStudy : BasePluginStudy
    {
        private const string ValueFormat = "N1";
        private bool _disposed = false; // to track whether the object has been disposed
        private PlugInSettings _settings;


        private MarketResilienceCalculator mrCalc;
        private HelperCustomQueue<OrderBookSnapshot> _QUEUE;



        // Event declaration
        public override event EventHandler<decimal> OnAlertTriggered;


        public override string Name { get; set; } = "Market Resilience Study";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Measures market recovery speed after large trades using time, spread, and depth recovery metrics. Provides real-time resilience scoring (0-1) to assess market stability and sentiment for trading decisions.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }
        public override string TileTitle { get; set; } = "MR";
        public override string TileToolTip { get; set; } =
            "<b>Market Resilience</b> (MR) is a real-time metric that quantifies how quickly a market rebounds after experiencing a large trade.<br/>" +
            "It's an invaluable tool for traders to gauge market stability and sentiment.<br/><br/>" +
            "The <b>MktRes</b> score is a composite index derived from three key market behaviors:<br/>" +
            "1. <b>Time Recovery:</b> Measures the elapsed time since the large trade, normalized between 0 (immediate recovery) and 1 (close to timeout).<br/>" +
            "2. <b>Spread Recovery:</b> Measures how quickly the gap between buying and selling prices returns to its normal state after a large trade.<br/>" +
            "3. <b>Depth Recovery:</b> Assesses how fast the consumed levels of the Limit Order Book (LOB) are replenished post-trade.<br/><br/>" +
            "The <b>MktRes</b> score is calculated by taking a weighted average of these three normalized metrics, ranging from 0 (no recovery) to 1 (full recovery). The score is then adjusted based on whether the market has recovered on the same side as the depletion.<br/><br/>" +
            "<b>Resilience Strength</b><br/>" +
            "1. <b>Strong Resilience:</b> Resilience Score ≥ 0.7<br/>" +
            "2. <b>Moderate Resilience:</b> 0.3 ≤ Resilience Score &lt; 0.7<br/>" +
            "3. <b>Weak Resilience:</b> Resilience Score &lt; 0.3<br/><br/>" +
            "<b>Market Resilience Bias</b><br/>" +
            "- If the market has a strong resilience and recovers on the same side as the depletion, it indicates a bias in the opposite direction of the recovery.<br/>" +
            "- If the market has a weak resilience and recovers on the opposite side of the depletion, it indicates a bias in the same direction as the recovery.<br/><br/>" +
            "<b>Quick Guidance for Traders:</b><br/>" +
            "- Use the <b>MR</b> score to gauge the market's reaction to large trades.<br/>" +
            "- A high score indicates a robust market that recovers quickly, ideal for entering or exiting positions.<br/>" +
            "- A low score suggests the market is more vulnerable to large trades, so exercise caution.<br/>" +
            "- Adjust your trading strategy based on the resilience strength: strong, moderate, or weak.<br/>" +
            "- Pay attention to the Market Resilience Bias to understand market sentiment and potential directional moves.";

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
            mrCalc.OnTrade(e);
            DoCalculationAndSend();
        }
        private void QUEUE_onRead(OrderBookSnapshot e)
        {
            mrCalc.OnOrderBookUpdate(e);
            DoCalculationAndSend();
            // ✅ CHANGED: Dispose snapshot to return arrays to pool
            e.Dispose();
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
            //we want to average the aggregations
            var existing = dataCollection[^1]; // Get the last item in the collection
            existing.Value = ((existing.Value * (lastItemAggregationCount - 1)) + newItem.Value) / lastItemAggregationCount;
            existing.Format = ValueFormat;
            existing.MarketMidPrice = newItem.MarketMidPrice;

            base.onDataAggregation(dataCollection, newItem, lastItemAggregationCount);
        }

        protected void DoCalculationAndSend()
        {
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED) return;

            // Trigger any events or updates based on the new T2O ratio
            var newItem = new BaseStudyModel
            {
                Value = mrCalc.CurrentMRScore,
                Format = ValueFormat,
                MarketMidPrice = (decimal)mrCalc.MidMarketPrice,
                Timestamp = HelperTimeProvider.Now
            };

            AddCalculation(newItem);
        }



        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
                    HelperTrade.Instance.Unsubscribe(TRADE_OnDataReceived);
                    // ❌ REMOVED: OrderBookSnapshotPool no longer exists
                    // OrderBookSnapshotPool.Instance.Dispose();

                    _QUEUE.Dispose();
                    mrCalc.Dispose();

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
        }

    }
}
