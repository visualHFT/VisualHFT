using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Commons.Helpers;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.Studies.Template.Model;
using VisualHFT.Studies.Template.UserControls;
using VisualHFT.Studies.Template.ViewModel;
using VisualHFT.UserSettings;
using VisualHFT.Commons.Model;

namespace VisualHFT.Studies.Template
{
    /// <summary>
    /// Template for a study plugin. Implement IPlugin and derive from BasePluginStudy
    /// to perform calculations on market data and push results into the VisualHFT framework.
    /// </summary>
    public class TemplateStudyPlugin : BasePluginStudy
    {
        #region Private Fields
        
        private new bool _disposed = false; // to track whether the object has been disposed
        private PlugInSettings _settings;
        
        // Study-specific data
        private double _calculatedValue = 0;
        private readonly object _lockObject = new object();
        
        // Queue for non-blocking order book processing
        private HelperCustomQueue<OrderBookSnapshot> _QUEUE;
        
        #endregion

        public TemplateStudyPlugin()
        {
            _QUEUE = new HelperCustomQueue<OrderBookSnapshot>($"<OrderBookSnapshot>_{this.Name}", QUEUE_onRead, QUEUE_onError);
        }

        ~TemplateStudyPlugin()
        {
            Dispose(false);
        }

        #region IPlugin Implementation

        public override async Task StartAsync()
        {
            await base.StartAsync(); // Call the base first
            
            try
            {
                _QUEUE.Clear();
                HelperOrderBook.Instance.Subscribe(OnOrderBookDataReceived);
                
                log.Info($"{this.Name} Plugin has successfully started.");
                Status = ePluginStatus.STARTED;
            }
            catch (Exception ex)
            {
                log.Error($"Error starting {Name} study plugin", ex);
                Status = ePluginStatus.STOPPED_FAILED;
                throw;
            }
        }

        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");
            
            try
            {
                HelperOrderBook.Instance.Unsubscribe(OnOrderBookDataReceived);
                
                await base.StopAsync();
                
                Status = ePluginStatus.STOPPED;
                log.Info($"{this.Name} study plugin stopped successfully");
            }
            catch (Exception ex)
            {
                log.Error($"Error stopping {Name} study plugin", ex);
                Status = ePluginStatus.STOPPED_FAILED;
            }
        }

        #endregion

        #region BasePluginStudy Implementation

        public override string Name { get; set; } = "TemplateStudy";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Template study plugin for custom calculations on market data.";
        public override string Author { get; set; } = "Developer Name";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }

        public override string TileTitle { get; set; } = "Template Study";
        public override string TileToolTip { get; set; } = "The <b>Template Study</b> performs custom calculations on market data.<br/><br/>" +
                "This template demonstrates how to create study plugins that analyze market conditions.<br/>" +
                "Override the calculation methods to implement your specific analysis logic.";

        public override Action CloseSettingWindow { get; set; }

        public override event EventHandler<decimal> OnAlertTriggered;

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
            _settings = new PlugInSettings
            {
                Symbol = "BTC/USD",
                Provider = new Provider { ProviderID = 1, ProviderName = "Default" },
                AggregationLevel = AggregationLevel.None
            };
        }

        private void OnOrderBookDataReceived(OrderBook orderBook)
        {
            /*
             * ***************************************************************************************************
             * TRANSFORM the incoming object (decouple it)
             * DO NOT hold this call back, since other components depends on the speed of this specific call back.
             * DO NOT BLOCK
             * IDEALLY, USE QUEUES TO DECOUPLE
             * ***************************************************************************************************
             */

            if (orderBook == null)
                return;
            if (_settings.Provider.ProviderID != orderBook.ProviderID || _settings.Symbol != orderBook.Symbol)
                return;

            // Create a snapshot and enqueue for processing
            var snapshot = OrderBookSnapshot.Create();
            snapshot.UpdateFrom(orderBook);
            _QUEUE.Add(snapshot);
        }

        private void QUEUE_onRead(OrderBookSnapshot snapshot)
        {
            // Process the order book snapshot
            // TODO: Implement your study calculation logic here
            // Example: Calculate a simple spread
            if (snapshot.Bids != null && snapshot.Bids.Length > 0 && 
                snapshot.Asks != null && snapshot.Asks.Length > 0)
            {
                var bestBid = snapshot.Bids[0];
                var bestAsk = snapshot.Asks[0];
                
                if (bestBid != null && bestAsk != null)
                {
                    lock (_lockObject)
                    {
                        _calculatedValue = (bestAsk.Price ?? 0) - (bestBid.Price ?? 0);
                    }
                    
                    DoCalculationAndSend();
                }
            }
            
            // Dispose snapshot to return arrays to pool
            snapshot.Dispose();
        }

        private void QUEUE_onError(Exception ex)
        {
            var _error = $"Unhandled error in the Queue: {ex.Message}";
            log.Error(_error, ex);
            HelperNotificationManager.Instance.AddNotification(this.Name, _error,
                HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);

            Task.Run(() => HandleRestart(_error, ex));
        }

        private void HandleRestart(string error, Exception ex)
        {
            try
            {
                log.Warn($"Restarting plugin due to error: {error}");
                // TODO: Implement restart logic if needed
            }
            catch (Exception restartEx)
            {
                log.Error("Failed to restart plugin", restartEx);
            }
        }

        private void DoCalculationAndSend()
        {
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED) return;
            
            var newItem = new BaseStudyModel();
            newItem.Value = (decimal)_calculatedValue;
            newItem.Timestamp = HelperTimeProvider.Now;
            newItem.MarketMidPrice = 0; // Set if you have mid-price

            AddCalculation(newItem);
            
            // Check for alert conditions
            CheckAlertCondition(_calculatedValue);
        }

        /// <summary>
        /// Check if alert conditions are met
        /// </summary>
        /// <param name="value">Calculated value to check</param>
        private void CheckAlertCondition(double value)
        {
            // TODO: Implement your alert conditions
            // Example: Alert if spread is too wide
            const double alertThreshold = 100.0;
            
            if (value > alertThreshold)
            {
                OnAlertTriggered?.Invoke(this, (decimal)value);
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
        protected override void onDataAggregation(List<BaseStudyModel> dataCollection, BaseStudyModel newItem, int lastItemAggregationCount)
        {
            //Aggregation: last
            var existing = dataCollection[^1]; // Get the last item in the collection
            existing.Value = newItem.Value;
            existing.Timestamp = newItem.Timestamp;
            existing.MarketMidPrice = newItem.MarketMidPrice;

            base.onDataAggregation(dataCollection, newItem, lastItemAggregationCount);
        }

        #endregion

        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(_settings);
            
            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.Symbol = viewModel.SelectedSymbol;
                _settings.Provider = viewModel.SelectedProvider;
                _settings.AggregationLevel = viewModel.AggregationLevelSelection;
                _settings.TimePeriodMs = viewModel.TimePeriodMs;
                _settings.MinVolumeThreshold = viewModel.MinVolumeThreshold;
                _settings.AlertThreshold = viewModel.AlertThreshold;
                _settings.EnableAlerts = viewModel.EnableAlerts;
                _settings.CustomParameter1 = viewModel.CustomParameter1;
                _settings.CustomParameter2 = viewModel.CustomParameter2;
                
                SaveSettings();
            };

            view.DataContext = viewModel;
            return view;
        }

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    CloseSettingWindow?.Invoke();
                    _QUEUE?.Dispose();
                }
                
                // Dispose unmanaged resources
                _disposed = true;
            }
            
            base.Dispose(disposing);
        }

        #endregion
    }
}
