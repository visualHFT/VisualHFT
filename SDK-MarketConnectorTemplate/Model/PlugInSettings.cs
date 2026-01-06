using System.ComponentModel;
using VisualHFT.Enums;
using VisualHFT.UserSettings;
using VisualHFT.Model;

namespace MarketConnector.Template.Model
{
    /// <summary>
    /// Settings class for TemplateExchange plugin. Extend ISetting to expose
    /// configurable properties that the user can set via the settings UI.
    /// </summary>
    public class PlugInSettings : ISetting
    {
        [Description("API key issued by the exchange")]
        public string ApiKey { get; set; }

        [Description("API secret issued by the exchange")]
        public string ApiSecret { get; set; }

        [Description("Symbols to subscribe, comma-separated (e.g., BTC-USD,ETH-USD)")]
        public string Symbols { get; set; }

        [Description("Depth levels to request for the order book")]
        public int DepthLevels { get; set; }

        [Description("Enable debug logging for troubleshooting")]
        public bool EnableDebugLogging { get; set; } = false;

        [Description("Enable automatic reconnection on connection loss")]
        public bool EnableReconnection { get; set; } = true;

        [Description("Maximum number of reconnection attempts")]
        public int MaxReconnectionAttempts { get; set; } = 5;

        [Description("Connection timeout in milliseconds")]
        public int ConnectionTimeoutMs { get; set; } = 5000;

        [Description("Use testnet environment")]
        public bool UseTestnet { get; set; } = false;

        [Description("Environment (production/staging/testnet)")]
        public string Environment { get; set; } = "production";

        [Description("License level required to use this plugin")]
        public eLicenseLevel LicenseLevel { get; set; } = eLicenseLevel.COMMUNITY;

        // ISetting required properties
        public string Symbol { get; set; } = "BTC-USD";
        
        public Provider Provider { get; set; } = new Provider 
        { 
            ProviderID = 999, 
            ProviderName = "TemplateExchange" 
        };
        
        public AggregationLevel AggregationLevel { get; set; } = VisualHFT.Enums.AggregationLevel.Ms100;

        /// <summary>
        /// Gets the effective WebSocket URL based on settings.
        /// </summary>
        public string GetEffectiveWebSocketUrl()
        {
            // TODO: Implement based on your exchange's endpoints
            return "wss://api.example.com/ws";
        }
    }
}
