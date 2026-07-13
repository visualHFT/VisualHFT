using System.Collections.Generic;
using VisualHFT.Enums;
using VisualHFT.Model;

namespace VisualHFT.UserSettings
{
    public interface ISetting
    {
        string Symbol { get; set; }
        Provider Provider { get; set; }
        AggregationLevel AggregationLevel { get; set; }

        // Returns null when the plugin is fully configured; otherwise a short,
        // user-facing reason listing ALL missing requirements (shown as the tooltip
        // on disabled rows in study-selection lists). Default covers the common case
        // (a provider + symbol must be chosen). Plugins with extra required fields
        // (e.g. VPIN's bucket size) override THIS on their own settings model, so the
        // rule and its message live in one place — reused by both the settings
        // dialog's save-gate and the study-selection lists.
        string? GetConfigurationError()
        {
            var missing = new List<string>();
            // Gate on the provider identity the emit path actually matches on (ProviderID),
            // not the display-only ProviderName — see StudyConfigPolicy. Fixes emit-capable/
            // running studies being grayed in the study pickers.
            if (StudyConfigPolicy.IsProviderMissing(Provider))
                missing.Add("data provider");
            if (string.IsNullOrEmpty(Symbol))
                missing.Add("symbol");
            return missing.Count == 0 ? null : "Missing: " + string.Join(", ", missing) + ".";
        }

        // Convenience: configured == no error. Derived so it can't drift from the
        // reason above (overriding GetConfigurationError updates both).
        bool IsConfigured() => GetConfigurationError() is null;
    }

}