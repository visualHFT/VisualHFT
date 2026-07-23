using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies.FXMacroData.Model
{
    public class MacroRiskSettings : ISetting
    {
        public string Symbol { get; set; } = string.Empty;
        public Provider Provider { get; set; } = new Provider();
        public AggregationLevel AggregationLevel { get; set; } = AggregationLevel.S1;
        public int MinutesBeforeRelease { get; set; } = 30;
        public int MinutesAfterRelease { get; set; } = 30;
        public int RefreshIntervalMinutes { get; set; } = 5;

        public string? GetConfigurationError() => null;
        public bool IsConfigured() => true;
    }
}
