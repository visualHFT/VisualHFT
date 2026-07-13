using VisualHFT.Model;

namespace VisualHFT.UserSettings
{
    /// <summary>
    /// Single source of truth for the study-picker "is a data provider configured?" rule.
    ///
    /// WHY THIS EXISTS
    ///   The runtime emit path matches a study against a live feed by <see cref="Provider.ProviderID"/>
    ///   (== <c>ProviderCode</c>, an int) + Symbol — e.g. LOBImbalanceStudy and VPINStudy both early-return
    ///   unless <c>_settings.Provider.ProviderID == e.ProviderID</c>. The study-selection config-gate
    ///   (ISetting.GetConfigurationError, and VPIN's override) previously keyed on the DISPLAY string
    ///   <see cref="Provider.ProviderName"/> instead. A provider carries a real ProviderID the instant it
    ///   is picked; ProviderName is a cosmetic label that can lag or be blank on a restored/programmatic
    ///   provider. That field mismatch grayed studies in the study pickers that were fully emit-capable
    ///   and running.
    ///
    ///   Aligning the config-gate with the field the emit path actually uses removes the divergence:
    ///   a provider is "present" whenever it carries a real ProviderID OR a non-empty display name.
    /// </summary>
    public static class StudyConfigPolicy
    {
        /// <summary>
        /// True when no usable data provider is configured — i.e. the provider is null, or it has
        /// neither a real <see cref="Provider.ProviderID"/> (&gt; 0) nor a non-empty
        /// <see cref="Provider.ProviderName"/>. Mirrors the identity the emit path matches on.
        /// </summary>
        public static bool IsProviderMissing(Provider? provider)
            => provider is null
               || (provider.ProviderID <= 0 && string.IsNullOrEmpty(provider.ProviderName));
    }
}
