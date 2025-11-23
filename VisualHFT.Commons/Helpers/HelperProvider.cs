using VisualHFT.Model;
using System.Collections.Concurrent;
using VisualHFT.Enums;
using VisualHFT.Commons.Helpers;

namespace VisualHFT.Helpers
{
    public class HelperProvider : ConcurrentDictionary<int, Model.Provider>, IDisposable
    {
        // We use a timer to check when was the last time we received an update.
        // If we have not received any update in that timespan, we trigger an OnHeartBeatFail
        // The timespan we've chosen to check this is 30,000 milliseconds (30 sec)
        private int _MILLISECONDS_HEART_BEAT = 30000;
        private readonly System.Timers.Timer _timer_check_heartbeat;

        // Track which providers have already been marked as stale to avoid redundant cleanup
        private readonly ConcurrentDictionary<int, bool> _staleProviders = new ConcurrentDictionary<int, bool>();

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly HelperProvider instance = new HelperProvider();
        public static HelperProvider Instance => instance;


        public event EventHandler<Provider> OnDataReceived;
        public event EventHandler<Provider> OnStatusChanged;
        
        /// <summary>
        /// Event raised when a provider becomes stale (no data for 30+ seconds).
        /// UI subscribers can use this to clear/gray out displays for the affected provider.
        /// </summary>
        public event EventHandler<Provider> OnProviderStale;

        public HelperProvider()
        {
            _timer_check_heartbeat = new System.Timers.Timer(_MILLISECONDS_HEART_BEAT);
            _timer_check_heartbeat.Elapsed += _timer_check_heartbeat_Elapsed;
            _timer_check_heartbeat.Start();
        }
        private void _timer_check_heartbeat_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            foreach (var x in this)
            {
                // Skip if already fully disconnected
                if (x.Value.Status == eSESSIONSTATUS.DISCONNECTED || x.Value.Status == eSESSIONSTATUS.DISCONNECTED_FAILED)
                {
                    // Clear stale tracking if provider is explicitly disconnected
                    _staleProviders.TryRemove(x.Key, out _);
                    continue;
                }

                // Check if provider heartbeat has timed out
                if (HelperTimeProvider.Now.Subtract(x.Value.LastUpdated).TotalMilliseconds > _MILLISECONDS_HEART_BEAT)
                {
                    // Only process once per staleness event (until provider recovers)
                    if (_staleProviders.TryAdd(x.Key, true))
                    {
                        var _msg = $"{x.Value.ProviderName} hasn't received any provider's heartbeat. Last message received: {x.Value.LastUpdated}";
                        HelperNotificationManager.Instance.AddNotification(x.Value.ProviderName, _msg, HelprNorificationManagerTypes.WARNING, HelprNorificationManagerCategories.PLUGINS, null);
                        log.Warn(_msg);

                        // Update provider status
                        x.Value.Status = eSESSIONSTATUS.CONNECTED_WITH_WARNINGS;
                        OnStatusChanged?.Invoke(this, x.Value);

                        // ✅ NEW: Send cleanup signal for stale provider
                        SendStaleProviderCleanup(x.Value);
                    }
                }
                else
                {
                    // Provider has recovered - clear stale tracking
                    if (_staleProviders.TryRemove(x.Key, out _))
                    {
                        log.Info($"{x.Value.ProviderName} has recovered from stale state");
                    }
                }
            }
        }

        /// <summary>
        /// Sends cleanup messages to clear UI displays for a stale provider.
        /// This clears ALL symbols associated with the provider since the entire
        /// provider connection is considered stale.
        /// </summary>
        private void SendStaleProviderCleanup(Provider staleProvider)
        {
            try
            {
                log.Info($"Sending stale provider cleanup for: {staleProvider.ProviderName} (ID: {staleProvider.ProviderID})");

                // Get all active symbols for this provider from HelperSymbol
                var activeSymbols = HelperSymbol.Instance
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();

                // Send empty OrderBooks for each symbol to clear UI displays
                foreach (var symbol in activeSymbols)
                {
                    var emptyOrderBook = new OrderBook(symbol, 2, 10)
                    {
                        ProviderID = staleProvider.ProviderID,
                        ProviderName = staleProvider.ProviderName,
                        ProviderStatus = eSESSIONSTATUS.CONNECTED_WITH_WARNINGS,
                        LastUpdated = null // Indicates stale/no data
                    };

                    // Send empty order book through standard pipeline
                    HelperOrderBook.Instance.UpdateData(emptyOrderBook);
                }

                // Raise stale event for UI subscribers who want custom handling
                OnProviderStale?.Invoke(this, staleProvider);

                log.Info($"Stale provider cleanup completed for {staleProvider.ProviderName}. Cleared {activeSymbols.Count} symbols.");
            }
            catch (Exception ex)
            {
                log.Error($"Error during stale provider cleanup for {staleProvider.ProviderName}: {ex.Message}", ex);
            }
        }

        public List<Model.Provider> ToList()
        {
            return this.Values.ToList();
        }

        protected virtual void RaiseOnDataReceived(Provider provider)
        {
            EventHandler<Provider> _handler = OnDataReceived;
            if (_handler != null)
            {
                _handler(this, provider);
            }
        }
        public void UpdateData(IEnumerable<VisualHFT.Model.Provider> providers)
        {
            foreach (var provider in providers)
            {
                if (UpdateDataInternal(provider))
                    RaiseOnDataReceived(provider);//Raise all provs allways
            }
        }

        public void UpdateData(VisualHFT.Model.Provider provider)
        {
            if (UpdateDataInternal(provider))
                RaiseOnDataReceived(provider);//Raise all provs allways
        }
        private bool UpdateDataInternal(VisualHFT.Model.Provider provider)
        {
            if (provider != null)
            {
                //Check provider
                if (!this.ContainsKey(provider.ProviderCode))
                {
                    provider.LastUpdated = HelperTimeProvider.Now;
                    return this.TryAdd(provider.ProviderCode, provider);
                }
                else
                {
                    bool hasStatusChanged = provider.Status != this[provider.ProviderCode].Status;
                    this[provider.ProviderCode].LastUpdated = HelperTimeProvider.Now;
                    this[provider.ProviderCode].Status = provider.Status;
                    this[provider.ProviderCode].Plugin = provider.Plugin;
                    if (hasStatusChanged) //do something with the status that has changed
                    {
                        OnStatusChanged?.Invoke(this, this[provider.ProviderCode]);
                    }
                }
            }
            return false;
        }
        public void Dispose()
        {
            _timer_check_heartbeat?.Stop();
            _timer_check_heartbeat?.Dispose();
            _staleProviders?.Clear();
        }

    }
}
