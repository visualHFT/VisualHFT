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

        // A print flagged no-aggressor has no taker, so it has no side to count: an auction cross, for example. The flag is a
        // whole token in Trade.Flags, a comma- or space-separated list.
        private const string NoAggressorToken = "no-aggressor";

        // The largest double that converts to a decimal without overflow (decimal.MaxValue is about 7.9e28).
        private const double MaxDecimalMid = 7.9e28;

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
                "<b>Bucket volume decides the number.</b> Small buckets read high on random flow by arithmetic alone: with four equally sized prints per bucket the average on coin-flip sides is 0.375, with two it is 0.500, and with one it is 1.000. Size the bucket as the instrument's daily volume without its opening and closing auction crosses, divided by 200, and keep the window at 50 buckets: the window then covers about a quarter of the day, and the corrected form shows its first value once a quarter of the day's volume has traded. (The paper divides average daily volume by the number of buckets, so its window covers a whole day.) Aim for at least 20 trades per bucket.<br/><br/>" +
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
            // A print with no taker has no side to count, in either form. A reported side wins over the token.
            if (!e.IsBuy.HasValue && HasNoAggressorToken(e.Flags))
                return;

            lock (_lockBucket)
            {
                if (_useCorrectedForm)
                {
                    // Null engine means the corrected form is selected but could not be armed. Publish
                    // nothing at all rather than quietly running the other form's arithmetic.
                    if (_engine != null)
                        ProcessTradeCorrected(e);
                    return;
                }

                if (_bucketVolumeSize == 0)
                    _bucketVolumeSize = (decimal)_settings.BucketVolSize;

                // Quote rule: classify using a mid price
                // Price >= mid → buy (aggressor lifting the ask)
                // Price <  mid → sell (aggressor hitting the bid)
                // The mid stamped on the trade is the quote the print arrived against. The last book mid can already
                // include the print's own book change, when a feed publishes that change first, so it is the fallback.
                // Fallback to provider's IsBuy if no mid-price yet
                // A stamp too large for a decimal is not a price: the print is skipped, as when no mid is known at all.
                double tradeMid = e.MarketMidPrice;
                if (double.IsFinite(tradeMid) && tradeMid > MaxDecimalMid)
                    return;
                decimal mid = double.IsFinite(tradeMid) && tradeMid > 0d ? (decimal)tradeMid : _lastMarketMidPrice;
                bool isBuy;
                if (mid > 0)
                    isBuy = e.Price >= mid;
                else if (e.IsBuy.HasValue)
                    isBuy = e.IsBuy.Value;
                else
                    return; // No classification possible

                decimal remainingSize = e.Size;

                // Assign entire trade to buy or sell for the current bucket portion
                if (isBuy)
                    _currentBuyVolume += remainingSize;
                else
                    _currentSellVolume += remainingSize;
                _currentBucketVolume += remainingSize;

                // Only the last n buckets can stay in an n-bucket window. When this trade closes more than n, every one of
                // the last n holds this trade alone, so the buckets before them are skipped instead of closed and evicted:
                // the same final value, and at most n bucket closes for one trade however large it is.
                if (_bucketVolumeSize > 0 && _currentBucketVolume >= _bucketVolumeSize)
                {
                    decimal skipped = decimal.Floor(_currentBucketVolume / _bucketVolumeSize) - _bucketImbalances.Length;
                    if (skipped > 0)
                    {
                        _currentBucketVolume -= skipped * _bucketVolumeSize;
                        _currentBuyVolume = isBuy ? _currentBucketVolume : 0;
                        _currentSellVolume = isBuy ? 0 : _currentBucketVolume;
                    }
                }

                // Complete as many buckets as this trade fills
                while (_currentBucketVolume >= _bucketVolumeSize && _bucketVolumeSize > 0)
                {
                    decimal bucketOverflow = _currentBucketVolume - _bucketVolumeSize;

                    // Trim the overflow from whichever side received it
                    if (isBuy)
                        _currentBuyVolume -= bucketOverflow;
                    else
                        _currentSellVolume -= bucketOverflow;
                    _currentBucketVolume = _bucketVolumeSize;

                    DoCalculation(true); // Bucket completed

                    // Start new bucket with the overflow
                    _currentBuyVolume = 0;
                    _currentSellVolume = 0;
                    if (isBuy)
                        _currentBuyVolume = bucketOverflow;
                    else
                        _currentSellVolume = bucketOverflow;
                    _currentBucketVolume = bucketOverflow;
                }

                DoCalculation(false); // Interim update with current state
            }
        }
        // Span scan of the whole tokens in Trade.Flags, no allocation.
        private static bool HasNoAggressorToken(string flags)
        {
            if (string.IsNullOrEmpty(flags))
                return false;

            ReadOnlySpan<char> rest = flags;
            while (!rest.IsEmpty)
            {
                int end = rest.IndexOfAny(',', ' ');
                ReadOnlySpan<char> token = end < 0 ? rest : rest.Slice(0, end);
                if (token.SequenceEqual(NoAggressorToken))
                    return true;

                rest = end < 0 ? ReadOnlySpan<char>.Empty : rest.Slice(end + 1);
            }

            return false;
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
            {
                _bucketVolumeSize = 0;
                _currentSellVolume = 0;
                _currentBuyVolume = 0;
                _currentBucketVolume = 0;

                int n = _settings?.NumberOfBuckets ?? DEFAULT_NUMBER_OF_BUCKETS;
                if (n <= 0) n = DEFAULT_NUMBER_OF_BUCKETS;
                _bucketImbalances = new decimal[n];
                _bufferIndex = 0;
                _bufferCount = 0;
                _rollingSum = 0;

                // A form switch or a bucket-size change restarts the calculation from nothing:
                // buckets collected under one classification rule or one bucket volume say nothing
                // about the other, so carrying them across would blend two different measurements.
                _floorWarningLogged = false;
                _engine = null;
                _useCorrectedForm = _settings?.UseCorrectedForm == true;
                if (_useCorrectedForm)
                {
                    // The dialog rejects a non-positive bucket volume, but a settings file edited by
                    // hand or written by an older build does not go through the dialog.
                    if (_settings!.BucketVolSize > 0)
                        _engine = new VpinBucketEngine((decimal)_settings.BucketVolSize, n);
                    else
                        log.Warn($"{this.Name}: the corrected form is selected but the bucket volume size is {_settings.BucketVolSize}. " +
                                 "It must be greater than zero. No value will be published until it is set.");
                }
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
        }

    }
}
