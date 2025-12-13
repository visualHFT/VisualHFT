using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.Studies.VPIN.Model;
using VisualHFT.Studies.VPIN.UserControls;
using VisualHFT.Studies.VPIN.ViewModel;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies
{
    /// <summary>
    /// The VPIN (Volume-Synchronized Probability of Informed Trading) value is a measure of the imbalance between buy and sell volumes in a given bucket. It's calculated as the absolute difference between buy and sell volumes divided by the total volume (buy + sell) for that bucket.
    /// 
    /// Given this definition, the range of VPIN values is between 0 and 1:
    ///     0: This indicates a perfect balance between buy and sell volumes in the bucket. In other words, the number of buy trades is equal to the number of sell trades.
    ///     1: This indicates a complete imbalance, meaning all the trades in the bucket are either all buys or all sells.
    /// Most of the time, the VPIN value will be somewhere between these two extremes, indicating some level of imbalance between buy and sell trades. The closer the VPIN value is to 1, the greater the imbalance, and vice versa.
    /// </summary>
    public class VPINStudy : BasePluginStudy
    {
        private const string ValueFormat = "N1";
        private const string colorGreen = "Green";
        private const string colorWhite = "White";

        private bool _disposed = false; // to track whether the object has been disposed
        private PlugInSettings _settings;

        //variables for calculation
        private decimal _bucketVolumeSize; // The volume size of each bucket
        private decimal _currentBucketVolume; // The volume size of each bucket
        private decimal _lastMarketMidPrice = 0; //keep track of market price
        private decimal _currentBuyVolume = 0;
        private decimal _currentSellVolume = 0;


        // Event declaration
        public override event EventHandler<decimal> OnAlertTriggered;

        public override string Name { get; set; } = "VPIN Study Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Volume-Synchronized Probability of Informed Trading (VPIN) measures buy/sell volume imbalance in fixed buckets. Provides real-time risk assessment (0-1 scale) for market instability detection.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }
        public override string TileTitle { get; set; } = "VPIN";
        public override string TileToolTip { get; set; } = "<b>Volume-Synchronized Probability of Informed Trading</b> (VPIN) is a real-time metric that measures the imbalance between buy and sell volumes, reflecting potential market risk or instability. <br/>VPIN is crucial for traders and analysts to gauge market sentiment and anticipate liquidity and volatility shifts.<br/><br/>" +
                "VPIN is calculated through the accumulation of trade volumes into fixed-size buckets. Each bucket captures a snapshot of trading activity, enabling ongoing analysis of market dynamics:<br/>" +
                "1. <b>Trade Classification:</b> Trades are categorized as buys or sells based on their relation to the market mid-price at execution.<br/>" +
                "2. <b>Volume Accumulation:</b> Buy and sell volumes are accumulated separately until reaching a pre-set bucket size.<br/>" +
                "3. <b>VPIN Calculation:</b> VPIN is the absolute difference between buy and sell volumes in a bucket, normalized to total volume, ranging from 0 (balanced trading) to 1 (high imbalance).<br/><br/>" +
                "To enhance real-time relevance, VPIN values are updated with 'Interim Updates' during the filling of each bucket, providing a more current view of market conditions. These updates offer a dynamic and timely insight into market liquidity and informed trading activity. VPIN serves as an early warning indicator of market turbulence, particularly valuable in high-frequency trading environments.";

        public decimal BucketVolumeSize => _bucketVolumeSize;

        public VPINStudy()
        {
        }
        ~VPINStudy()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {
            await base.StartAsync();//call the base first
            ResetBucket();

            HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);
            HelperTrade.Instance.Subscribe(TRADES_OnDataReceived);
            DoCalculation(false); //initial value

            log.Info($"{this.Name} Plugin has successfully started.");
            Status = ePluginStatus.STARTED;
        }

        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");

            HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
            HelperTrade.Instance.Unsubscribe(TRADES_OnDataReceived);

            await base.StopAsync();
        }


        private void TRADES_OnDataReceived(Trade e)
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
            if (_settings.Provider.ProviderID != e.ProviderId || _settings.Symbol != e.Symbol)
                return;
            if (!e.IsBuy.HasValue) //we do not know what it is
                return;
            if (_bucketVolumeSize == 0)
                _bucketVolumeSize = (decimal)_settings.BucketVolSize;

            decimal bucketOverflow = 0;


            _currentBucketVolume += e.Size;
            if (_currentBucketVolume > _bucketVolumeSize) //We have overflow
            {
                bucketOverflow = _currentBucketVolume - _bucketVolumeSize;
                _currentBucketVolume = _bucketVolumeSize; //Cap it
                if (e.IsBuy.Value)
                    _currentBuyVolume += e.Size - bucketOverflow;
                else
                    _currentSellVolume += e.Size - bucketOverflow;
            }
            else //NO OVERFLOW
            {
                if (e.IsBuy.Value)
                    _currentBuyVolume += e.Size;
                else
                    _currentSellVolume += e.Size;
            }

            DoCalculation(bucketOverflow > 0); // will update vpin


            //assign overfowed volume to its proper variable.
            if (bucketOverflow > 0)
            {
                if (e.IsBuy.Value)
                    _currentBuyVolume = bucketOverflow;
                else
                    _currentSellVolume = bucketOverflow;
                _currentBucketVolume = bucketOverflow;
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

            if (e == null)
                return;
            if (_settings.Provider.ProviderID != e.ProviderID || _settings.Symbol != e.Symbol)
                return;

            _lastMarketMidPrice = (decimal)e.MidPrice;
            DoCalculation(false); //Interim update -> Just to send update.
        }
        private void DoCalculation(bool isNewBucket)
        {
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED) return;
            string valueColor = isNewBucket ? colorGreen : colorWhite;

            decimal vpin = 0;
            if ((_currentBuyVolume + _currentSellVolume) > 0)
                vpin = Math.Abs(_currentBuyVolume - _currentSellVolume) / (_currentBuyVolume + _currentSellVolume);

            // Add to rolling window and remove oldest if size exceeded
            var newItem = new BaseStudyModel();
            newItem.Value = vpin;
            newItem.Format = ValueFormat;
            newItem.Timestamp = HelperTimeProvider.Now;
            newItem.MarketMidPrice = _lastMarketMidPrice;
            newItem.ValueColor = valueColor;
            newItem.AddItemSkippingAggregation = isNewBucket;
            AddCalculation(newItem);
        }
        private void ResetBucket()
        {
            _bucketVolumeSize = 0;
            _currentSellVolume = 0;
            _currentBuyVolume = 0;
            _currentBucketVolume = 0;
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

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    // Dispose managed resources here
                    HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
                    HelperTrade.Instance.Unsubscribe(TRADES_OnDataReceived);
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
            _settings.AggregationLevel = AggregationLevel.S1; //force to 1 second
        }

        protected override void SaveSettings()
        {
            SaveToUserSettings(_settings);
        }

        protected override void InitializeDefaultSettings()
        {
            _settings = new PlugInSettings()
            {
                BucketVolSize = 1,
                Symbol = "",
                Provider = new ViewModel.Model.Provider(),
                AggregationLevel = AggregationLevel.S1
            };
            SaveToUserSettings(_settings);
        }
        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.BucketVolumeSize = _settings.BucketVolSize;
            viewModel.SelectedSymbol = _settings.Symbol;
            viewModel.SelectedProviderID = _settings.Provider.ProviderID;
            viewModel.AggregationLevelSelection = _settings.AggregationLevel;

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.BucketVolSize = viewModel.BucketVolumeSize;
                _settings.Symbol = viewModel.SelectedSymbol;
                _settings.Provider = viewModel.SelectedProvider;
                _settings.AggregationLevel = viewModel.AggregationLevelSelection;
                _bucketVolumeSize = (decimal)_settings.BucketVolSize;
                SaveSettings();

                // Start the Reconnection 
                //  It will allow to reload with the new values
                Task.Run(() =>
                {
                    ResetBucket();
                });
            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
        }

    }
}
