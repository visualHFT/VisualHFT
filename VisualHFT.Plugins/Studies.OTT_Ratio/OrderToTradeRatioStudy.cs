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
#if WINDOWS
using VisualHFT.Studies.MarketRatios.UserControls;
#endif
#if WINDOWS
using VisualHFT.Studies.MarketRatios.ViewModel;
#endif
using VisualHFT.UserSettings;

namespace VisualHFT.Studies
{
    /// <summary>
    /// The Order-to-Trade Ratio (OTR), in either of two forms. The form is chosen in the plugin
    /// settings and cannot change while the study is running; changing it restarts the study.
    ///
    /// THE TWO FORMS
    /// Volume form:  OTR = (Ordered Volume / Traded Volume) - 1,
    ///               where Ordered Volume = max(Added + Removed - Traded, 0).
    ///               Added and Removed are the size that entered and left the tracked price
    ///               levels; Traded is the size of the public trade prints.
    /// Count form:   OTR = (Book changes / max(Trades, 1)) - 1, measured over each aggregation
    ///               window. Book changes counts additions, removals and modifications at the
    ///               tracked price levels, with a modification weighted twice.
    ///
    /// WHY THE VOLUME FORM ACCUMULATES OVER A WHOLE INTERVAL
    /// The volume form's three totals run from the moment the study arms, not per aggregation
    /// window. A per-window ratio goes negative under ordinary conditions: liquidity already
    /// resting in the book when the window opens is never counted as added size, yet its
    /// execution inside the window counts as both removed size and traded size. Accumulating
    /// from a single arming instant is what makes that subtraction balance. The totals reset
    /// when the study restarts and at the daily rollover.
    ///
    /// WHAT PRICE-LEVEL DATA CAN AND CANNOT SUPPORT
    /// Size leaving a price level is either a cancellation or a fill, and at this granularity
    /// the two are indistinguishable. Subtracting traded size separates them in aggregate, never
    /// order by order. Only price levels inside the configured depth are counted, so this is a
    /// market-wide figure built from public data — not a venue's per-member regulatory ratio.
    ///
    /// NO VALUE IS PUBLISHED WHEN THERE IS NOTHING TO DIVIDE BY
    /// In the volume form, no public trade means no ratio: the study publishes a model with no
    /// value assigned at all, and the tile shows a dot. The denominator is never floored to
    /// reach a number. This matters because -1 is itself a legitimate output — the count form
    /// emits exactly -1 when a window carries no book changes — so a value can never double as
    /// a "no data" marker. Absence of data is signalled by the absence of a value.
    ///
    /// THREAD SAFETY
    /// The order-book stream and the trade stream are independent dispatchers. Their callbacks
    /// can run at the same time on different threads, and nothing orders a trade print against
    /// the book change that produced it. Every total written from more than one callback is
    /// advanced with Interlocked and is an order-insensitive sum; the baseline readings used to
    /// turn cumulative counters into deltas are single-writer state, touched only by the
    /// order-book callback.
    /// </summary>
    public class OrderToTradeRatioStudy : BasePluginStudy
    {
        private const string ValueFormat = "N1";
        private PlugInSettings _settings;

        // The form this run started in. Latched once in StartAsync rather than read live, because
        // the settings object stays mutable while the study runs and a restart is scheduled
        // asynchronously: reading it live would let a run branch for one form while still
        // subscribed for the other.
        private bool _useVolumeForm;

        private long _orderEvents = 0;
        private long _tradeCount = 0;
        private object _lock = new object();

        private long _prevAdded = 0;
        private long _prevDeleted = 0;
        private long _prevUpdated = 0;
        private long _floorNum = 1; // Default floor; configurable if needed
        private bool _isFirstL2Call = true;

        // L3/L2 Mode Detection Variables
        private volatile bool _IsCurrentLOB_L3 = false;
        private DateTime _startedCheckLOB_L3 = DateTime.MinValue;
        private TimeSpan _LOB3_Identification_Timespan = TimeSpan.FromSeconds(10);

        // Volume form only. Size posted, withdrawn and executed over the current interval.
        private readonly VolumeIntervalAccumulator _volumes = new VolumeIntervalAccumulator();

        // Both forms. The mid price is written by the order-book callback and read whenever a
        // value is published, which can happen on another thread, and a decimal cannot be
        // assigned atomically — so it travels as the bit pattern of a double through Interlocked.
        private long _lastMidPriceBits;

        // Event declaration
        public override event EventHandler<decimal> OnAlertTriggered;

        // Emits a metric via AddCalculation -> the trigger picker lists this study (IStudy.EmitsMetric).
        public override bool EmitsMetric => true;

        public override string Name { get; set; } = "Order To Trade Ratio Study Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } =
            "Order-to-Trade Ratio computed from public market data. Reports either the volume form " +
            "— size posted against size executed — or the message-count form, selected in the plugin " +
            "settings. A public-data proxy, not a venue's per-member regulatory ratio.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings
        {
            get => _settings;
            set
            {
                _settings = (PlugInSettings)value;
                ApplyPresentationForSelectedForm();
            }
        }
        public override Action CloseSettingWindow { get; set; }
        public override string TileTitle { get; set; } = CountFormTitle;
        public override string TileToolTip { get; set; } = CountFormToolTip;

        private const string VolumeFormTitle = "OTR (vol)";
        private const string CountFormTitle = "OTR (count)";

        private const string VolumeFormToolTip =
            "<b>OTR (vol)</b> — the Order-to-Trade Ratio in its volume form: size posted against size executed.<br/><br/>" +
            "<b>Calculation:</b> <i>OTR = (Ordered Volume / Traded Volume) - 1</i>, where " +
            "<i>Ordered Volume = max(Added + Removed - Traded, 0)</i>.<br/><br/>" +
            "Added, Removed and Traded volume all accumulate from the moment the study starts. They reset " +
            "when the study restarts and at the daily rollover (00:00 UTC).<br/><br/>" +
            "<b>What this data can and cannot say:</b>" +
            "<ul>" +
            "<li>Size leaving a price level is either a cancellation or a fill, and the two look the same at " +
            "this granularity. Subtracting traded size separates them in aggregate, not order by order.</li>" +
            "<li>Only price levels inside the configured depth are counted.</li>" +
            "<li>This is a market-wide figure built from public market data. It is not a venue's per-member " +
            "regulatory ratio.</li>" +
            "</ul>" +
            "<b>Reading it:</b><br/>" +
            "0 means all the size that was posted also executed. Higher values mean more size was posted and " +
            "withdrawn for each unit executed. A high reading has several ordinary causes — market making, " +
            "quote refreshing, thin trading — and is not by itself evidence of manipulation.<br/><br/>" +
            "A dot means no public trade has been seen yet, so there is nothing to divide by.<br/>" +
            "A clamped reading — a plain 0 — means the underlying counters were reset or hit a precision edge " +
            "and the figure was floored rather than measured. It does not necessarily mean there was no activity.";

        private const string CountFormToolTip =
            "<b>OTR (count)</b> — the Order-to-Trade Ratio in its message-count form.<br/><br/>" +
            "<b>Calculation:</b> <i>OTR = (Book changes / max(Trades, 1)) - 1</i>, measured over each " +
            "aggregation window.<br/><br/>" +
            "Book changes counts additions, removals and modifications at the tracked price levels; a " +
            "modification counts twice, reflecting the cancel and replace it stands for.<br/><br/>" +
            "<b>What this data can and cannot say:</b>" +
            "<ul>" +
            "<li>Only price levels inside the configured depth are counted.</li>" +
            "<li>Both sides of the ratio are message counts, not size. For a size-based ratio, switch to the " +
            "volume form in Settings.</li>" +
            "<li>This is a market-wide figure built from public market data. It is not a venue's per-member " +
            "regulatory ratio.</li>" +
            "</ul>";

        /// <summary>
        /// Data-quality note attached to a reading that rests on a clamp rather than on
        /// measurement, so the tile can tell a floored zero from a measured one.
        /// </summary>
        private const string ClampedReadingNote =
            "Clamped reading: the underlying counters were reset or hit a precision edge, so this value is " +
            "floored rather than measured.";

        public OrderToTradeRatioStudy()
        {
        }

        /// <summary>
        /// Keeps the tile's title and tooltip describing the form the settings actually select.
        /// The two forms answer different questions, so a tile labelled for the wrong one is
        /// worse than no label.
        /// </summary>
        private void ApplyPresentationForSelectedForm()
        {
            bool useVolumeForm = _settings != null && _settings.UseVolumeForm;

            TileTitle = useVolumeForm ? VolumeFormTitle : CountFormTitle;
            TileToolTip = useVolumeForm ? VolumeFormToolTip : CountFormToolTip;
        }
        // No finalizer. Everything this study releases is a managed subscription on a shared
        // dispatcher, and those must not be touched from the finalizer thread, where the objects
        // holding them may already have been collected. Releasing happens in StopAsync and
        // Dispose, both of which run on a live object.

        public override async Task StartAsync()
        {
            await base.StartAsync();//call the base first

            // Latch the form for the whole run. Everything downstream reads this field, so a
            // settings change mid-run cannot make the callbacks branch one way while the
            // subscriptions point the other.
            _useVolumeForm = _settings.UseVolumeForm;

            // The label follows the same latch, so the tile never names one form over a value the
            // other produced. A restart is scheduled after a backoff, so relabelling at the moment
            // settings are saved would show the new name over old data until the run turned over.
            ApplyPresentationForSelectedForm();

            // A restart reuses this instance, possibly for another symbol: forget the old mid.
            Interlocked.Exchange(ref _lastMidPriceBits, 0);

            if (_useVolumeForm)
            {
                // The volume form reads size straight off the book and the public trade prints.
                // It needs neither the per-order statistics stream nor the level-3 probe, so it
                // arms neither.
                _volumes.Reset();
                HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);
                HelperTrade.Instance.Subscribe(TRADES_OnDataReceived);
            }
            else
            {
                _startedCheckLOB_L3 = HelperTimeProvider.Now; //start checking for L3
                _IsCurrentLOB_L3 = true; //initial assumption is that L3 Orderbook is present

                HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);
            }

            log.Info($"{this.Name} Plugin has successfully started.");
            Status = ePluginStatus.STARTED;
        }
        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");

            UnsubscribeAll();

            await base.StopAsync();
        }

        /// <summary>
        /// Releases every stream either form can hold. Dropping a subscription that was never
        /// taken is a no-op, so this is symmetric for both forms and stays correct even if the
        /// selected form changed after the run started.
        /// </summary>
        private void UnsubscribeAll()
        {
            HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
            HelperTrade.Instance.Unsubscribe(TRADES_OnDataReceived);
        }

        /// <summary>
        /// Whether an event belongs to the provider and symbol this study is configured for.
        /// The three market-data streams all ask the same question, and all three are reached
        /// from dispatcher threads that keep calling while settings are being replaced, so the
        /// null checks belong here rather than in whichever callback remembered them.
        /// </summary>
        private bool IsForThisStream(int providerId, string symbol)
        {
            // Both the settings object and its provider are snapshotted before use: a replacement
            // landing between two reads would otherwise slip past the null check it just passed.
            var settings = _settings;
            var provider = settings?.Provider;

            return provider != null
                   && provider.ProviderID == providerId
                   && settings.Symbol == symbol;
        }

        private void LIMITORDERBOOK_OnDataReceived(OrderBook e)
        {
            if (e == null)
                return;
            if (!IsForThisStream(e.ProviderID, e.Symbol))
                return;

            // Every book carries the mid the chart plots, whichever counters the ratio is built
            // from, so it is recorded before the form and the data mode split the work.
            Interlocked.Exchange(ref _lastMidPriceBits, BitConverter.DoubleToInt64Bits(e.MidPrice));

            if (_useVolumeForm)
            {
                var volumes = e.GetCountersVolume();
                _volumes.OnOrderBookCounters(volumes.addedVol, volumes.deletedVol, e.SizeDecimalPlaces,
                    HelperTimeProvider.Now);
                DoCalculationAndSend();
                return;
            }

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

                Interlocked.Add(ref _orderEvents, addedDelta + deletedDelta + 2 * updatedDelta); // Accumulate deltas, double-count updates
                DoCalculationAndSend();
            }

        }
        /// <summary>
        /// Public trade prints, subscribed by the volume form only. A print whose side is
        /// unknown still moved size, so it counts; only the provider and symbol filter it out.
        /// </summary>
        private void TRADES_OnDataReceived(Trade e)
        {
            if (e == null)
                return;
            if (!IsForThisStream(e.ProviderId, e.Symbol))
                return;

            _volumes.OnTrade(e.Size, HelperTimeProvider.Now);
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

            if (_useVolumeForm)
            {
                DoVolumeCalculationAndSend();
                return;
            }

            long orderEvents = Interlocked.Read(ref _orderEvents);
            long tradeCount = Interlocked.Read(ref _tradeCount);
            long denom = Math.Max(tradeCount, _floorNum);
            decimal orderToTradeRatio = denom == 0 ? 0 : (decimal)orderEvents / denom - 1; // Standard formula with floor

            var newItem = new BaseStudyModel();
            newItem.Value = orderToTradeRatio;
            newItem.Format = ValueFormat;
            newItem.MarketMidPrice = (decimal)BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastMidPriceBits));
            newItem.Timestamp = HelperTimeProvider.Now;

            AddCalculation(newItem);
        }

        /// <summary>
        /// Publishes the volume form. When no public trade has been seen there is nothing to
        /// divide by, so the model goes out with no value assigned at all and the tile shows a
        /// dot; the denominator is never floored to manufacture a number.
        /// </summary>
        private void DoVolumeCalculationAndSend()
        {
            var newItem = new BaseStudyModel();
            newItem.Format = ValueFormat;
            newItem.MarketMidPrice = (decimal)BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastMidPriceBits));
            newItem.Timestamp = HelperTimeProvider.Now;

            if (_volumes.TryGetIntervalVolumes(out decimal orderedVolume, out decimal tradedVolume, out bool numeratorClamped))
            {
                // The floor on the displayed ratio is this study's rule, applied here, next to
                // the formula it protects. The accumulator reports only what it can know on its
                // own — that it had to floor the numerator — and does not predict what a caller
                // will do with the volumes it hands back.
                decimal ratio = orderedVolume / tradedVolume - 1m;
                bool clamped = numeratorClamped || ratio < 0m;

                newItem.Value = clamped ? 0m : ratio;

                if (clamped)
                    newItem.Tooltip = ClampedReadingNote;
            }

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

            // Assigning Value is what marks a model as carrying data, so copying it from a model
            // that has none would turn "no public trade yet" into a fabricated zero.
            if (newItem.HasData)
                existing.Value = newItem.Value;

            existing.Format = newItem.Format;
            existing.MarketMidPrice = newItem.MarketMidPrice;
            existing.Tooltip = newItem.Tooltip;

            base.onDataAggregation(dataCollection, newItem, lastItemAggregationCount);
        }

        /// <summary>
        /// Resets the per-window counters of the count form when a new data point is added to the
        /// aggregated series. The volume form's totals are deliberately untouched here: they run
        /// for the whole counting interval, which is closed only by a restart, the daily rollover,
        /// or a change of size precision.
        /// </summary>
        protected override void onDataAdded()
        {
            if (_useVolumeForm)
                return;

            ResetCalculations();
        }

        /// <summary>
        /// Overrides the base implementation rather than declaring a second method of the same
        /// name: the public Dispose on the base type dispatches through this, so a study disposed
        /// without a preceding stop still releases the streams it subscribed to.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                UnsubscribeAll();
            }

            base.Dispose(disposing);
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

            ApplyPresentationForSelectedForm();
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
                AggregationLevel = AggregationLevel.Ms100,
                // Stated rather than left implicit: a fresh install gets the message-count form,
                // the same behaviour a client already in the field has.
                UseVolumeForm = false
            };
            ApplyPresentationForSelectedForm();
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
            viewModel.UseVolumeFormSelection = _settings.UseVolumeForm;

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.Symbol = viewModel.SelectedSymbol;
                _settings.Provider = viewModel.SelectedProvider;
                _settings.AggregationLevel = viewModel.AggregationLevelSelection;
                _settings.UseVolumeForm = viewModel.UseVolumeFormSelection;

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
