using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies.Template.Model
{
    /// <summary>
    /// Settings for the Template Study plugin.
    /// Customize these properties to match your study's configuration needs.
    /// </summary>
    public class PlugInSettings : ISetting
    {
        /// <summary>
        /// The trading symbol to analyze (e.g., "BTC/USD", "EUR/USD")
        /// </summary>
        public string Symbol { get; set; }
        
        /// <summary>
        /// The data provider to use for market data
        /// </summary>
        public Provider Provider { get; set; }
        
        /// <summary>
        /// The aggregation level for data processing
        /// </summary>
        public AggregationLevel AggregationLevel { get; set; }
        
        // Add your custom study settings below
        // Example properties for common study configurations:
        
        /// <summary>
        /// Time period for calculations in milliseconds
        /// </summary>
        public int TimePeriodMs { get; set; } = 1000;
        
        /// <summary>
        /// Minimum volume threshold for calculations
        /// </summary>
        public double MinVolumeThreshold { get; set; } = 0.0;
        
        /// <summary>
        /// Alert threshold value
        /// </summary>
        public double AlertThreshold { get; set; } = 100.0;
        
        /// <summary>
        /// Enable/disable alerts
        /// </summary>
        public bool EnableAlerts { get; set; } = true;
        
        /// <summary>
        /// Custom parameter 1 - rename to something meaningful for your study
        /// </summary>
        public double CustomParameter1 { get; set; } = 1.0;
        
        /// <summary>
        /// Custom parameter 2 - rename to something meaningful for your study
        /// </summary>
        public int CustomParameter2 { get; set; } = 10;
    }
}
