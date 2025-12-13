using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Studies.MarketResilience.Model;
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

    public class MarketResilienceBiasStudy : BasePluginStudy
    {
        private bool _disposed = false; // to track whether the object has been disposed
        private PlugInSettings _settings;
        private MarketResilienceWithBias _mrBiasCalc;
        private HelperCustomQueue<OrderBookSnapshot> _QUEUE;

        // Event declaration
        public override event EventHandler<decimal> OnAlertTriggered;

        public override string Name { get; set; } = "Market Resilience Bias";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Analyzes directional market sentiment after large trades by monitoring volume addition rates on bid/ask sides. Provides real-time bias scoring (+1 bullish, -1 bearish, 0 neutral) for sentiment analysis.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }
        public override string TileTitle { get; set; } = "MRB";
        public override string TileToolTip { get; set; } =
            "<b>Market Resilience Bias</b> (MRB) quantifies directional market sentiment following significant market stress events.<br/><br/>" +

            "<b>How It Works:</b><br/>" +
            "1. <b>Detection:</b> Identifies large trades (≥2σ above average) that cause order book depth depletion<br/>" +
            "2. <b>Recovery Tracking:</b> Monitors which side of the book (bid/ask) recovers first using immediacy-weighted depth<br/>" +
            "3. <b>Bias Classification:</b> Determines market sentiment based on recovery patterns<br/><br/>" +

            "<b>Signal Interpretation:</b><br/>" +
            "• <b>↑ Bullish (+1):</b> Ask side depleted → Bid side recovered (buyers in control)<br/>" +
            "• <b>↓ Bearish (-1):</b> Bid side depleted → Ask side recovered (sellers in control)<br/>" +
            "• <b>— Neutral (0):</b> Same-side recovery or insufficient Market Resilience (MR > 0.30)<br/><br/>" +

            "<b>Activation Requirements:</b><br/>" +
            "• Large trade detected (≥2σ threshold)<br/>" +
            "• Depth depletion confirmed (≥3σ drop in immediacy-weighted depth)<br/>" +
            "• Market Resilience (MR) score ≤ 0.30 (poor resilience)<br/>" +
            "• Recovery completes within timeout window (default: 5 seconds)<br/><br/>" +

            "<b>Hysteresis Behavior:</b><br/>" +
            "MRB activates when MR ≤ 0.30 and deactivates when MR ≥ 0.50, preventing signal oscillation during moderate resilience.<br/><br/>" +

            "<b>Practical Use:</b><br/>" +
            "Use MRB to identify which side (buyers/sellers) gains control after market shocks. Persistent directional bias may indicate institutional order flow or liquidity imbalances.";

        public MarketResilienceBiasStudy()
        {
            _QUEUE = new HelperCustomQueue<OrderBookSnapshot>($"<OrderBookSnapshot>_{this.Name}", QUEUE_onRead, QUEUE_onError);
        }

        ~MarketResilienceBiasStudy()
        {
            Dispose(false);
        }


        public override async Task StartAsync()
        {
            await base.StartAsync();//call the base first

            _mrBiasCalc = new MarketResilienceWithBias(_settings);
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
            _mrBiasCalc.OnTrade(e);
            DoCalculationAndSend();
        }
        private void QUEUE_onRead(OrderBookSnapshot e)
        {
            _mrBiasCalc.OnOrderBookUpdate(e);
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
            //Aggregation: last
            var existing = dataCollection[^1]; // Get the last item in the collection
            existing.Value = newItem.Value;
            existing.Format = newItem.Format;
            existing.MarketMidPrice = newItem.MarketMidPrice;

            base.onDataAggregation(dataCollection, newItem, lastItemAggregationCount);
        }


        private static readonly Func<decimal, string> BiasFormatter = FormatBias;

        private static string FormatBias(decimal value)
        {
            return value switch
            {
                1 => "↑",    // Bullish
                -1 => "↓",   // Bearish
                _ => "-"     // Neutral/Unknown
            };
        }

        // Then in DoCalculationAndSend():
        protected void DoCalculationAndSend()
        {
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED) return;

            eMarketBias _valueBias = _mrBiasCalc.CurrentMarketBias;
            string _valueColor = _valueBias == eMarketBias.Bullish ? "Green"
                               : (_valueBias == eMarketBias.Bearish ? "Red" : "White");

            var newItem = new BaseStudyModel
            {
                Value = _valueBias == eMarketBias.Bullish ? 1 : (_valueBias == eMarketBias.Bearish ? -1 : 0),
                CustomFormatter = BiasFormatter,  // ✅ Use CustomFormatter instead
                ValueColor = _valueColor,
                MarketMidPrice = _mrBiasCalc.MidMarketPrice,
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
                    _QUEUE.Dispose();
                    _mrBiasCalc.Dispose();
                    // ❌ REMOVED: OrderBookSnapshotPool no longer exists
                    // OrderBookSnapshotPool.Instance.Dispose();
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
                AggregationLevel = AggregationLevel.S1,
                MaxShockMsTimeout = 5000
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
