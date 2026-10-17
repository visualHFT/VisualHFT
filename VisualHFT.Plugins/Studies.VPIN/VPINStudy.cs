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
#if WINDOWS
using VisualHFT.Studies.VPIN.UserControls;
#endif
#if WINDOWS
using VisualHFT.Studies.VPIN.ViewModel;
#endif
using VisualHFT.UserSettings;

namespace VisualHFT.Studies
{
    /// <summary>
    /// VPIN (Volume-Synchronized Probability of Informed Trading), the volume-bucket order-flow
    /// imbalance of Easley, Lopez de Prado and O'Hara (2012).
    ///
    /// Formula: VPIN = (1/n) * SUM |V_buy_i - V_sell_i| / V_bucket, over n completed buckets.
    ///
    /// Range [0, 1]: 0 means every bucket in the window was balanced, 1 means every bucket was
    /// one-sided. It is not a probability, despite the name the literature gave it.
    ///
    /// Two forms ship side by side, selected by <c>UseCorrectedForm</c>.
    ///
    /// The LEGACY form is the default so that a settings file written before the option existed
    /// keeps the behaviour it already had. It classifies each print against the order-book mid
    /// price, averages over however many buckets have closed so far rather than waiting for a full
    /// window, and re-publishes on every order-book update. It accepts any positive bucket volume,
    /// including one small enough to hold a single print, which on a whole-lot instrument pins the
    /// reading at 1 permanently.
    ///
    /// The CORRECTED form is in <see cref="Model.VpinBucketEngine"/>, which documents the
    /// construction and the one place it departs from the paper.
    /// </summary>
    public class VPINStudy : BasePluginStudy
    {
        private const string ValueFormat = "N2";
        private const string colorGreen = "Green";
        private const string colorWhite = "White";
        private const int DEFAULT_NUMBER_OF_BUCKETS = 50;

        private bool _disposed = false; // to track whether the object has been disposed
        private PlugInSettings _settings;
        private readonly object _lockBucket = new object();

        //variables for calculation
        private decimal _bucketVolumeSize; // The volume size of each bucket
        private decimal _currentBucketVolume; // Running accumulated volume in current bucket
        private decimal _lastMarketMidPrice = 0; //keep track of market price
        private decimal _currentBuyVolume = 0;
        private decimal _currentSellVolume = 0;

        // Rolling window of completed bucket imbalances: |V_buy - V_sell| / V_bucket
        private decimal[] _bucketImbalances;
        private int _bufferIndex = 0;
        private int _bufferCount = 0;
        private decimal _rollingSum = 0; // Running sum for O(1) average calculation

        // Which form is selected, read once per reset. The legacy path is gated on THIS and never on
        // the engine being non-null: a corrected-form user whose bucket volume is unusable must get
        // nothing, not a silent fall-back to the other form's number under the other form's rules.
        private bool _useCorrectedForm;

        // Corrected form only. Owns the whole calculation, so none of the legacy fields above are
        // touched while it is running. Null when the legacy form is selected, and also when the
        // corrected form is selected but cannot be armed - see ResetBucket.
        private VpinBucketEngine? _engine;
        private bool _floorWarningLogged;
        private VpinVolumeFloor _volumeFloor = new();
        private readonly List<PendingPrint> _pendingPrints = new();
        private string _armedSymbol = "";
        private int _armedProviderId;
        private bool _bucketArmed;

        private readonly struct PendingPrint
        {
            public PendingPrint(decimal price, decimal size, bool isBuy)
            {
                Price = price;
                Size = size;
                IsBuy = isBuy;
            }
            public decimal Price { get; }
            public decimal Size { get; }
            public bool IsBuy { get; }
        }


        // Event declaration
        public override event EventHandler<decimal> OnAlertTriggered;

        // Emits a metric via AddCalculation -> the trigger picker lists this study (IStudy.EmitsMetric).
        public override bool EmitsMetric => true;

        public override string Name { get; set; } = "VPIN Study Plugin";
        public override string Version { get; set; } = "1.0.0";
        // 🛑 DO NOT EDIT Name, Author, Version or Description. BasePluginStudy.GetPluginUniqueID()
        // hashes all four together with the assembly name, and that hash is the key this plugin's
        // saved settings are stored under AND the key every emitted metric is registered with. Change
        // any of them and an existing installation stops finding its own settings - it falls back to
        // defaults with an empty symbol and provider, so the tile goes dead rather than merely resetting
        // - and every alert rule already built on this study stops matching, silently. The text a user
        // actually reads is TileTitle and TileToolTip below, neither of which is hashed; correct those
        // instead. Editing these four requires overriding GetPluginUniqueID() first to pin the existing
        // key, which is a separate change with its own regression test.
        public override string Description { get; set; } = "Volume-Synchronized Probability of Informed Trading (VPIN) measures buy/sell volume imbalance in fixed buckets. Provides real-time risk assessment (0-1 scale) for market instability detection.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }
        public override string TileTitle { get; set; } = "VPIN";
        public override string TileToolTip { get; set; } = "<b>VPIN</b> (Volume-Synchronized Probability of Informed Trading) measures order-flow imbalance on a volume clock. Trades are grouped into buckets of equal traded volume. For each bucket we take the volume bought by aggressors minus the volume sold by aggressors, and show the average absolute imbalance over the last n buckets. 0 means every bucket was balanced; 1 means every bucket was one-sided.<br/><br/>" +
                "<b>It is not a probability, and it is not a warning signal.</b> Andersen and Bondarenko (2014, 2015) showed that the metric's forecasting record came from its original classifier tracking volatility rather than from informed trading. Read it as a description of how one-sided recent flow has been, not as a forecast.<br/><br/>" +
                "<b>Bucket volume decides the number.</b> Small buckets read high on random flow by arithmetic alone: with four equally sized prints per bucket the average on coin-flip sides is 0.375, with two it is 0.500, and with one it is 1.000. Size the bucket from the instrument's daily volume - the paper divides average daily volume by the number of buckets - and aim for at least 20 trades per bucket.<br/><br/>" +
                "<b>Corrected form</b> (optional, off by default)<br/>" +
                "Classifies each trade by the tick rule - a print above the previous print is a buy, below is a sell, unchanged repeats the previous side - which Chakrabarty, Pascual and Shkilko (2015) found more accurate than the paper's own bulk classifier. The paper's method splits a bucket's volume fractionally between the two sides; assigning each whole print to one side can only produce buckets at least as one-sided, so expect this to read higher than a bulk-classified figure on the same tape. That follows from the two constructions - it is not a measurement, and no bulk classifier ships here to compare against. Compare ranks and percentiles rather than published absolute thresholds. No value is shown until a full window of buckets has closed, and none is shown while the bucket volume is too small to hold 20 trades.<br/><br/>" +
                "<b>Legacy form</b> (default)<br/>" +
                "Classifies each trade against the order-book mid price, starts averaging from the first completed bucket instead of waiting for a full window, and accepts any positive bucket volume. It is the default only so that an existing settings file keeps the behaviour it already had.";

        public decimal BucketVolumeSize => _bucketVolumeSize;

        public VPINStudy()
        {
            _bucketImbalances = new decimal[DEFAULT_NUMBER_OF_BUCKETS];
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

            lock (_lockBucket)
            {
                RetargetIfNeeded(e.Symbol, e.ProviderId);

                decimal size = e.Size;
                if (size <= 0m)
                    return;

                if (!_bucketArmed)
                {
                    _volumeFloor.Add(size);
                    if (!_volumeFloor.IsReady)
                    {
                        if (TryClassifyLegacy(e, out var pendingSide))
                            _pendingPrints.Add(new PendingPrint(e.Price, size, pendingSide));
                        else
                            _pendingPrints.Add(new PendingPrint(e.Price, size, e.IsBuy == true));
                        return;
                    }

                    ArmBucketVolume();
                    foreach (var print in _pendingPrints)
                        ApplyPrint(print.Price, print.Size, print.IsBuy);
                    _pendingPrints.Clear();
                }

                if (_useCorrectedForm)
                {
                    if (_engine != null)
                        ProcessTradeCorrected(e);
                    return;
                }

                if (!TryClassifyLegacy(e, out var isBuy))
                    return;
                ApplyPrint(e.Price, size, isBuy);
            }
        }

        private bool TryClassifyLegacy(Trade e, out bool isBuy)
        {
            if (_lastMarketMidPrice > 0)
            {
                isBuy = e.Price >= _lastMarketMidPrice;
                return true;
            }
            if (e.IsBuy.HasValue)
            {
                isBuy = e.IsBuy.Value;
                return true;
            }
            isBuy = false;
            return false;
        }

        private void ApplyPrint(decimal price, decimal size, bool isBuy)
        {
            if (_useCorrectedForm)
            {
                if (_engine == null)
                    return;
                _engine.AddTrade(price, size);
                if (_engine.BucketJustClosed)
                    PublishCorrected();
                return;
            }

            if (_bucketVolumeSize <= 0)
                return;

            if (isBuy)
                _currentBuyVolume += size;
            else
                _currentSellVolume += size;
            _currentBucketVolume += size;

            while (_currentBucketVolume >= _bucketVolumeSize)
            {
                decimal overflow = _currentBucketVolume - _bucketVolumeSize;
                if (isBuy)
                    _currentBuyVolume -= overflow;
                else
                    _currentSellVolume -= overflow;
                _currentBucketVolume = _bucketVolumeSize;
                DoCalculation(true);

                _currentBuyVolume = isBuy ? overflow : 0;
                _currentSellVolume = isBuy ? 0 : overflow;
                _currentBucketVolume = overflow;
            }

            DoCalculation(false);
        }

        private void RetargetIfNeeded(string symbol, int providerId)
        {
            if (_armedSymbol == symbol && _armedProviderId == providerId)
                return;
            _armedSymbol = symbol ?? "";
            _armedProviderId = providerId;
            ResetBucketUnlocked();
        }

        private void ArmBucketVolume()
        {
            var configured = (decimal)(_settings?.BucketVolSize ?? 0);
            _bucketVolumeSize = _volumeFloor.Recommend(configured);
            _bucketArmed = _bucketVolumeSize > 0;
            if (!_bucketArmed)
                return;

            if (_volumeFloor.Recommend(configured) > configured && configured > 0)
            {
                log.Info(
                    $"{this.Name}: bucket volume {configured} is too small for {_settings.Symbol} " +
                    $"(median print {_volumeFloor.MedianPrint}). Using {_bucketVolumeSize} so a bucket holds " +
                    $"{VpinVolumeFloor.PrintsPerBucket} prints.");
            }

            if (_useCorrectedForm)
                _engine = new VpinBucketEngine(_bucketVolumeSize, _bucketImbalances.Length, minimumPrintsPerBucket: 0);
        }

        private void PublishCorrected()
        {
            if (_engine == null || !_engine.HasPublishableValue)
            {
                WarnIfBucketSizeBelowFloor();
                return;
            }

            var newItem = new BaseStudyModel();
            newItem.Value = _engine.Value;
            newItem.Format = ValueFormat;
            newItem.Timestamp = HelperTimeProvider.Now;
            newItem.MarketMidPrice = _lastMarketMidPrice;
            newItem.ValueColor = colorGreen;
            newItem.AddItemSkippingAggregation = true;
            AddCalculation(newItem);
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

            lock (_lockBucket)
            {
                RetargetIfNeeded(e.Symbol, e.ProviderID);
                _lastMarketMidPrice = (decimal)e.MidPrice;

                // Corrected form: the book is read for the displayed mid price only. It never feeds
                // the calculation and never triggers a publish, so the value can only move when a
                // bucket closes - which is the only time the metric is defined to move. Leaving the
                // publish here would also put this dispatcher and the trade dispatcher, which are
                // independent and unsynchronized, in contention on every book update.
                if (_useCorrectedForm)
                    return;

                DoCalculation(false); //Interim update -> Just to send update.
            }
        }

        /// <summary>
        /// Corrected form. Runs under the same lock as the legacy path and publishes only when a
        /// bucket closes, and only once the value means what the tooltip says it means.
        /// </summary>
        private void ProcessTradeCorrected(Trade e)
        {
            // Caller must hold _lockBucket.
            // The dispatchers run inline on the connector's producer thread, so a print already inside
            // this callback keeps running after StopAsync has set the status and before it unsubscribes,
            // and again on a restart between the subscribe and the status being set back. The legacy
            // path drops those in DoCalculation; this one has to drop them here.
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED)
                return;

            _engine.AddTrade(e.Price, e.Size);

            if (!_engine.BucketJustClosed)
                return;

            if (!_engine.HasPublishableValue)
            {
                WarnIfBucketSizeBelowFloor();
                return;
            }

            var newItem = new BaseStudyModel();
            newItem.Value = _engine.Value;
            newItem.Format = ValueFormat;
            newItem.Timestamp = HelperTimeProvider.Now;
            newItem.MarketMidPrice = _lastMarketMidPrice;
            newItem.ValueColor = colorGreen;
            newItem.AddItemSkippingAggregation = true;

            AddCalculation(newItem);
        }

        private void WarnIfBucketSizeBelowFloor()
        {
            // Caller must hold _lockBucket
            if (_floorWarningLogged || !_engine.IsBucketSizeBelowFloor)
                return;

            _floorWarningLogged = true;
            log.Warn(
                $"{this.Name}: bucket volume size {_engine.BucketVolumeSize} is too small for {_settings.Symbol}. " +
                $"The median trade size observed is {_engine.MedianPrintSize}, so a bucket holding " +
                $"{_engine.MinimumPrintsPerBucket} trades needs at least {_engine.RequiredBucketVolumeSize}. " +
                "Below that the reading measures the bucket size rather than the order flow, so no value is published.");
        }
        /// <summary>
        /// Legacy form only. The corrected form owns its own publishing in
        /// <see cref="ProcessTradeCorrected"/>, so this path is closed while the engine is running.
        /// </summary>
        private void DoCalculation(bool isNewBucket)
        {
            // Caller must hold _lockBucket
            if (_useCorrectedForm) return;
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED) return;
            string valueColor = isNewBucket ? colorGreen : colorWhite;

            if (isNewBucket && _bucketVolumeSize > 0)
            {
                // Completed bucket: push imbalance into rolling window
                decimal bucketImbalance = Math.Abs(_currentBuyVolume - _currentSellVolume) / _bucketVolumeSize;

                // Subtract the value being evicted (if buffer is full)
                if (_bufferCount == _bucketImbalances.Length)
                    _rollingSum -= _bucketImbalances[_bufferIndex];
                else
                    _bufferCount++;

                _bucketImbalances[_bufferIndex] = bucketImbalance;
                _rollingSum += bucketImbalance;
                _bufferIndex = (_bufferIndex + 1) % _bucketImbalances.Length;
            }

            // VPIN = average of completed bucket imbalances in the rolling window
            decimal vpin = 0;
            if (_bufferCount > 0)
                vpin = _rollingSum / _bufferCount;

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
            lock (_lockBucket)
                ResetBucketUnlocked();
        }

        private void ResetBucketUnlocked()
        {
            _bucketVolumeSize = 0;
            _currentSellVolume = 0;
            _currentBuyVolume = 0;
            _currentBucketVolume = 0;
            _bucketArmed = false;
            _volumeFloor = new VpinVolumeFloor();
            _pendingPrints.Clear();

            int n = _settings?.NumberOfBuckets ?? DEFAULT_NUMBER_OF_BUCKETS;
            if (n <= 0) n = DEFAULT_NUMBER_OF_BUCKETS;
            _bucketImbalances = new decimal[n];
            _bufferIndex = 0;
            _bufferCount = 0;
            _rollingSum = 0;

            _floorWarningLogged = false;
            _engine = null;
            _useCorrectedForm = _settings?.UseCorrectedForm == true;
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
            if (!_settings.NumberOfBuckets.HasValue || _settings.NumberOfBuckets.Value <= 0)
            {
                _settings.NumberOfBuckets = DEFAULT_NUMBER_OF_BUCKETS;
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
                NumberOfBuckets = DEFAULT_NUMBER_OF_BUCKETS,
                Symbol = "",
                Provider = new VisualHFT.Model.Provider(),
                AggregationLevel = AggregationLevel.S1
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
            viewModel.BucketVolumeSize = _settings.BucketVolSize;
            viewModel.NumberOfBuckets = _settings.NumberOfBuckets ?? DEFAULT_NUMBER_OF_BUCKETS;
            viewModel.UseCorrectedFormSelection = _settings.UseCorrectedForm;
            viewModel.SelectedSymbol = _settings.Symbol;
            viewModel.SelectedProviderID = _settings.Provider.ProviderID;
            viewModel.AggregationLevelSelection = _settings.AggregationLevel;

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.BucketVolSize = viewModel.BucketVolumeSize;
                _settings.NumberOfBuckets = viewModel.NumberOfBuckets;
                _settings.UseCorrectedForm = viewModel.UseCorrectedFormSelection;
                _settings.Symbol = viewModel.SelectedSymbol;
                _settings.Provider = viewModel.SelectedProvider;
                _settings.AggregationLevel = viewModel.AggregationLevelSelection;
                _bucketVolumeSize = (decimal)_settings.BucketVolSize;
                SaveSettings();

                // Reload with the new values
                Task.Run(() =>
                {
                    ResetBucket();
                });
            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
#endif
        }

    }
}
