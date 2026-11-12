using System;
using System.Collections.Generic;
using System.DirectoryServices.ActiveDirectory;
using System.Threading.Tasks;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.Studies.LOBImbalance.Model;
using VisualHFT.Studies.LOBImbalance.UserControls;
using VisualHFT.Studies.LOBImbalance.ViewModel;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies
{
    public class LOBImbalanceStudy : BasePluginStudy
    {
        private const string ValueFormat = "N1";

        private bool _disposed = false; // to track whether the object has been disposed
        private PlugInSettings _settings;

        private double _lobImbalance = 0;
        private double _lobMidPrice = 0;
        private P2Quantile _spreadMedian = new P2Quantile(0.5);

        // Event declaration
        public override event EventHandler<decimal> OnAlertTriggered;

        // Emits a metric via AddCalculation -> the trigger picker lists this study (IStudy.EmitsMetric).
        public override bool EmitsMetric => true;

        public override string Name { get; set; } = "LOB Imbalance Study Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Calculates Limit Order Book Imbalances.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }
        public override string TileTitle { get; set; } = "LOB Imbalance";
        public override string TileToolTip { get; set; } = "The <b>Limit Order Book Imbalance</b> compares total bid size to total ask size across the price levels your data feed captures (set by the connector's Depth Levels setting) - not just the best price.<br/><br/>" +
                "Depending on the <b>Weighting</b> setting below, every captured level counts equally, or levels farther from the best price count less.<br/>" +
                "A significant imbalance can indicate a strong buying or selling interest across that captured depth.";

        public LOBImbalanceStudy()
        { }
        ~LOBImbalanceStudy()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {
            await base.StartAsync();//call the base first

            // Fresh on every start: HandleRestart() (settings save, e.g. a Symbol/Provider change)
            // calls StopAsync()/StartAsync() on this SAME instance. A stale median from the old
            // instrument's spread scale would silently degenerate touch weighting on the new one -
            // mirrors MarketResilienceStudy.StartAsync() recreating its calculator the same way.
            _spreadMedian = new P2Quantile(0.5);

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

            // Computed locally, never through OrderBook.CalculateMetrics()/ImbalanceValue: that
            // state is shared across every subscriber of this book, so a per-study weighting mode
            // cannot be honoured there without corrupting the value other consumers depend on.
            double currentSpread = e.Spread;
            // Spread is 0 unless both sides have a best price. Feeding those zeros to the median
            // drags it to 0, and touch weighting then collapses onto the best level.
            if (currentSpread > 0)
                _spreadMedian.Observe(currentSpread);
            double spreadScale = SpreadScaleResolver.Resolve(e.PriceDecimalPlaces, currentSpread, _spreadMedian.Count, _spreadMedian.Estimate);

            // The imbalance is undefined when a side is empty or the captured sizes sum to zero.
            // NaN marks it, and the model then goes out with no value instead of a 0 that reads
            // as a balanced book.
            var bids = e.Bids;
            var asks = e.Asks;
            int bidsCount = bids?.Count() ?? 0;
            int asksCount = asks?.Count() ?? 0;
            _lobImbalance = double.NaN;
            if (bidsCount > 0 && asksCount > 0)
            {
                double imbalance = ImbalanceCalculator.Calculate(bids, asks, e.MaxDepth, _settings.Weighting, spreadScale);
                // The calculator also returns 0 for a zero total. A 0 with size on the bid side is a
                // balanced book (equal totals), so one side is enough to tell the two apart.
                if (imbalance != 0 || HasSizeWithinDepth(bids, bidsCount, e.MaxDepth))
                    _lobImbalance = imbalance;
            }
            _lobMidPrice = e.MidPrice;
            DoCalculationAndSend();
        }

        private static bool HasSizeWithinDepth(CachedCollection<BookItem> side, int count, int depth)
        {
            int n = Math.Min(count, depth);
            for (int i = 0; i < n; i++)
            {
                if (side[i].Size.GetValueOrDefault() > 0)
                    return true;
            }
            return false;
        }

        private void DoCalculationAndSend()
        {
            if (Status != VisualHFT.PluginManager.ePluginStatus.STARTED) return;
            var newItem = new BaseStudyModel();
            // Assigning Value is what marks the model as carrying data; leave it unset when undefined.
            if (!double.IsNaN(_lobImbalance))
                newItem.Value = (decimal)_lobImbalance;
            newItem.Format = ValueFormat;
            newItem.Timestamp = HelperTimeProvider.Now;
            // A model with no value keeps Value 0, so it also goes out with mid 0 (as a one-sided book already does):
            // the tile drops a model that repeats the last value, mid and colour, and would otherwise keep a held 0.
            newItem.MarketMidPrice = double.IsNaN(_lobImbalance) ? 0m : (decimal)_lobMidPrice;

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
            // The latest book decides the bucket, including whether it has a value at all.
            // The bucket keeps its start time: the collection measures the next bucket from it.
            var bucketStart = existing.Timestamp;
            existing.copyFrom(newItem);
            existing.Timestamp = bucketStart;

            base.onDataAggregation(dataCollection, newItem, lastItemAggregationCount);
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
                AggregationLevel = AggregationLevel.Ms100,
                Weighting = ImbalanceWeighting.EqualWeight
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
            viewModel.WeightingSelection = _settings.Weighting;

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.Symbol = viewModel.SelectedSymbol;
                _settings.Provider = viewModel.SelectedProvider;
                _settings.AggregationLevel = viewModel.AggregationLevelSelection;
                _settings.Weighting = viewModel.WeightingSelection;

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
