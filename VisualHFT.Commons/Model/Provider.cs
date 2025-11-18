using Newtonsoft.Json;
using VisualHFT.Commons.Helpers;
using VisualHFT.Enums;

namespace VisualHFT.Model
{
    public partial class Provider
    {
        private string _cachedTooltip;
        private eSESSIONSTATUS _lastTooltipStatus;

        public int ProviderID
        {
            get { return this.ProviderCode; }
            set { this.ProviderCode = value; }
        }
        public int ProviderCode { get; set; }
        public string ProviderName { get; set; }
        public eSESSIONSTATUS Status { get; set; }
        public DateTime LastUpdated { get; set; }

        public string Tooltip
        {
            get
            {
                // Cache tooltip to avoid repeated queries to notification manager
                if (_lastTooltipStatus != Status || string.IsNullOrEmpty(_cachedTooltip))
                {
                    _cachedTooltip = GenerateTooltip();
                    _lastTooltipStatus = Status;
                }
                return _cachedTooltip;
            }
        }

        private string GenerateTooltip()
        {
            string lastNotificationText = "";
            try
            {
                var lastNonReadNotification = HelperNotificationManager.Instance.GetAllNotifications()
                    .OrderByDescending(x => x.Timestamp)
                    .ThenBy(x => x.NotificationType)
                    .FirstOrDefault(x => x.Title.IndexOf(this.ProviderName) > -1);
                if (lastNonReadNotification != null)
                    lastNotificationText = lastNonReadNotification.Title + " " + lastNonReadNotification.Message;
            }
            catch (Exception ex)
            {
                lastNotificationText = "[[Err reading notifications]]" + ex.ToString();
            }

            if (Status == eSESSIONSTATUS.CONNECTING)
                return "Connecting...";
            if (Status == eSESSIONSTATUS.CONNECTED)
                return "Connected";
            if (Status == eSESSIONSTATUS.CONNECTED_WITH_WARNINGS)
                return "Connected with limitations" + (string.IsNullOrEmpty(lastNotificationText) ? "" : $": ({lastNotificationText})");
            if (Status == eSESSIONSTATUS.DISCONNECTED_FAILED)
                return "Failure Disconnection" + (string.IsNullOrEmpty(lastNotificationText) ? "" : $": ({lastNotificationText})");
            if (Status == eSESSIONSTATUS.DISCONNECTED)
                return "Disconnected";


            return "";
        }

        [JsonIgnore]
        public VisualHFT.PluginManager.IPlugin Plugin { get; set; } //reference to a plugin (if any)
    }
}
