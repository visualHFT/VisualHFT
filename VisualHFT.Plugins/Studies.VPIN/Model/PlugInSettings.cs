using System.Collections.Generic;
using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies.VPIN.Model
{
    public class PlugInSettings : ISetting
    {
        public double BucketVolSize { get; set; }
        public int? NumberOfBuckets { get; set; } // Rolling window size (nullable for backward compat)

        /// <summary>
        /// Selects the corrected form of the metric: trades classified by the tick rule instead of
        /// against the order-book mid, no value published until a full window of buckets has closed,
        /// and no value published while the bucket volume is too small to hold enough prints for the
        /// imbalance to describe the flow. Off by default, so a settings file written before this
        /// option existed keeps the behaviour it already had.
        /// </summary>
        public bool UseCorrectedForm { get; set; }

        /// <summary>
        /// Sizes the bucket from the instrument itself: 20 times the volume-weighted average size of the first 10,000 trades
        /// (the sum of squared sizes divided by the sum of sizes), fixed for the run. On for new settings. A settings file
        /// written before this option existed reads it as off and keeps its stored bucket volume.
        /// </summary>
        public bool AutoBucketVolume { get; set; }

        public string Symbol { get; set; }
        public Provider Provider { get; set; }
        public AggregationLevel AggregationLevel { get; set; }

        // Overrides ISetting.GetConfigurationError: VPIN also needs a positive bucket
        // size and bucket count, not just provider + symbol. (IsConfigured is derived
        // from this on the interface, so it stays in sync automatically.)
        public string? GetConfigurationError()
        {
            var missing = new List<string>();
            // Same provider-identity rule as the base gate (StudyConfigPolicy):
            // match on ProviderID, not the display-only ProviderName.
            if (StudyConfigPolicy.IsProviderMissing(Provider))
                missing.Add("data provider");
            if (string.IsNullOrEmpty(Symbol))
                missing.Add("symbol");
            if (!AutoBucketVolume && BucketVolSize <= 0)
                missing.Add("bucket volume size");
            if (NumberOfBuckets is not > 0)
                missing.Add("number of buckets");
            return missing.Count == 0 ? null : "Missing: " + string.Join(", ", missing) + ".";
        }
    }
}