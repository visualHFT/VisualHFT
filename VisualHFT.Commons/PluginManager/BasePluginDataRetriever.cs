using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VisualHFT.Commons.Extensions;
using VisualHFT.Commons.Helpers;
using VisualHFT.DataRetriever;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
using static System.Runtime.InteropServices.JavaScript.JSType;
using IPlugin = VisualHFT.PluginManager.IPlugin;

namespace VisualHFT.Commons.PluginManager
{
    public abstract class BasePluginDataRetriever : IDataRetriever, IPlugin, IDisposable
    {
        protected bool _disposed = false; // to track whether the object has been disposed
        private Dictionary<string, string> parsedNormalizedSymbols;

        private const int maxReconnectionAttempts = 5;
        private const int MaxReconnectionPendingRequests = 3; // Increased from 1 to handle multi-symbol scenarios
        private int _reconnectionAttempt = 0;
        private int _pendingReconnectionRequests = 0;
        private SemaphoreSlim _reconnectionSemaphore = new SemaphoreSlim(1, 1);
        
        // Changed from bool to int for atomic operations (0=false, 1=true)
        private int _isReconnectingFlag = 0;

        // 1 once StopAsync ran from outside the reconnection loop (a user or host stop), 0 again on StartAsync. A
        // connector that marks itself STOPPED on a socket drop does not go through StopAsync, so it still reconnects.
        private int _stopRequested = 0;

        // True while the reconnection loop runs its own StopAsync, which is not a stop request.
        private volatile bool _engineStopping;

        protected static bool _ARE_ALL_DATA_RETRIEVERS_ENABLE = true;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);


        public abstract string Name { get; set; }
        public abstract string Version { get; set; }
        public abstract string Description { get; set; }
        public abstract string Author { get; set; }
        public abstract ISetting Settings { get; set; }
        public ePluginStatus Status { get; set; }
        public abstract Action CloseSettingWindow { get; set; }
        public ePluginType PluginType
        {
            get { return ePluginType.MARKET_CONNECTOR; }
        }
        public virtual eLicenseLevel RequiredLicenseLevel => eLicenseLevel.COMMUNITY; // Default to Community, override in derived classes if needed

        protected abstract void LoadSettings();
        protected abstract void SaveSettings();
        protected abstract void InitializeDefaultSettings();





        public BasePluginDataRetriever()
        {
            // Ensure settings are loaded before starting
            LoadSettings();
            if (Settings == null)
                throw new InvalidOperationException($"{Name} plugin settings has not been loaded.");
            Status = ePluginStatus.LOADED;
        }

        public virtual async Task StartAsync()
        {
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTING));
            Status = ePluginStatus.STARTING;
            log.Info($"{this.Name} is starting.");
            _reconnectionAttempt = 0; // Reset on successful connection
            _pendingReconnectionRequests = 0;
            Volatile.Write(ref _stopRequested, 0);
        }
        public virtual async Task StopAsync()
        {
            if (!_engineStopping)
                Volatile.Write(ref _stopRequested, 1);
            Status = ePluginStatus.STOPPED;
            log.Info($"{this.Name} has stopped.");
        }

        protected VisualHFT.Model.Provider GetProviderModel(eSESSIONSTATUS status)
        {
            return new VisualHFT.Model.Provider()
            {
                ProviderCode = Settings.Provider.ProviderID,
                ProviderID = Settings.Provider.ProviderID,
                ProviderName = Settings.Provider.ProviderName,
                Status = status,
                Plugin = this
            };
        }

        [Obsolete]
        protected virtual void RaiseOnDataReceived(DataEventArgs args, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                HandleData(args);
                //OnDataReceived?.Invoke(this, args);
            }
        }
        protected virtual void RaiseOnDataReceived(OrderBook orderBook, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                if (orderBook != null)
                {
                    HelperSymbol.Instance.UpdateData(orderBook.Symbol);
                    HelperOrderBook.Instance.UpdateData(orderBook);
                }
            }
        }
        protected virtual void RaiseOnDataReceived(IEnumerable<OrderBook> orderBooks, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                if (orderBooks != null)
                {
                    var allSymbols = orderBooks.Select(x => x.Symbol);

                    HelperSymbol.Instance.UpdateData(allSymbols);
                    HelperOrderBook.Instance.UpdateData(orderBooks);
                }
            }
        }
        protected virtual void RaiseOnDataReceived(VisualHFT.Model.Order executedOrderModel, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                HelperPosition.Instance.UpdateData(executedOrderModel);
            }
        }
        protected virtual void RaiseOnDataReceived(List<Strategy> strategies, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                HelperCommon.ACTIVESTRATEGIES.UpdateData(strategies);
            }
        }
        protected virtual void RaiseOnDataReceived(List<VisualHFT.Model.Provider> heartBeatsModel, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                HelperProvider.Instance.UpdateData(heartBeatsModel);
            }
        }
        protected virtual void RaiseOnDataReceived(VisualHFT.Model.Provider heartBeatModel, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                HelperProvider.Instance.UpdateData(heartBeatModel);
            }
        }
        protected virtual void RaiseOnDataReceived(VisualHFT.Model.Trade tradeModel, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                HelperTrade.Instance.UpdateData(tradeModel);
            }
        }
        protected virtual void RaiseOnDataReceived(IEnumerable<VisualHFT.Model.Trade> tradesModel, bool overrideDisabiityFromOtherDataRetrievers = false)
        {
            if (_ARE_ALL_DATA_RETRIEVERS_ENABLE || overrideDisabiityFromOtherDataRetrievers)
            {
                HelperTrade.Instance.UpdateData(tradesModel);
            }
        }

        private void HandleData(DataEventArgs e)
        {
            if (e.ParsedModel == null)
                return;
            switch (e.DataType)
            {
                case "Market":
                    var orderBooks = e.ParsedModel as IEnumerable<OrderBook>;
                    if (orderBooks != null)
                    {
                        var allSymbols = orderBooks.Select(x => x.Symbol);

                        HelperSymbol.Instance.UpdateData(allSymbols);
                        HelperOrderBook.Instance.UpdateData(orderBooks);
                    }
                    break;
                case "ExecutedOrder":
                    HelperPosition.Instance.UpdateData(e.ParsedModel as VisualHFT.Model.Order);
                    break;
                //case "Execution":
                //    ParseExecution(e.ParsedModel as VisualHFT.Model.Execution);
                //    break;
                case "Strategies":
                    HelperCommon.ACTIVESTRATEGIES.UpdateData(e.ParsedModel as List<Strategy>);
                    break;
                case "HeartBeats":
                    HelperProvider.Instance.UpdateData(e.ParsedModel as List<VisualHFT.Model.Provider>);
                    break;
                case "Trades":
                    HelperTrade.Instance.UpdateData(e.ParsedModel as IEnumerable<VisualHFT.Model.Trade>);
                    break;
                default:
                    break;
            }
        }


        private Func<Task> _internalStartAsync;
        /// <summary>
        /// To use the internal and automated reconnection process, we need to define and pass a local method, which have defined all the steps required to do the connection.
        /// The reconnection engine does NOT invoke this action itself: every connector's StartAsync already
        /// awaits the same internal start, so invoking it here too ran two full connect bursts per attempt.
        /// </summary>
        /// <param name="internalStartAsync">The internal start async.</param>
        protected void SetReconnectionAction(Func<Task> internalStartAsync)
        {
            _internalStartAsync = internalStartAsync;
        }

        /// <summary>
        /// The reconnection retry loop's exponential backoff. Production uses a real wall-clock delay;
        /// this is a seam so a test can override it (return a completed task) to exercise the retry/cap/
        /// backoff orchestration deterministically without waiting 2-32s per attempt. Behavior is
        /// unchanged in production.
        /// </summary>
        protected virtual Task ReconnectBackoffDelayAsync(int milliseconds)
        {
            return Task.Delay(milliseconds);
        }

        /// <summary>
        /// Handles connection loss with automatic reconnection logic.
        /// Thread-safe: Can be called from multiple threads simultaneously.
        /// Idempotent: Multiple calls are coalesced into a single reconnection attempt.
        /// Uses retry loop instead of recursion for better reliability.
        /// </summary>
        protected async Task HandleConnectionLost(string reason = null, Exception exception = null, bool forceStartRegardlessStatus = false)
        {
            // ✅ ATOMIC CHECK-AND-SET: Prevents race conditions
            if (Interlocked.CompareExchange(ref _isReconnectingFlag, 1, 0) == 1)
            {
                // Already reconnecting - this is expected when multiple threads detect the same failure
                log.Debug($"{this.Name} Already reconnecting, ignoring duplicate request. Reason: {reason}");
                return;
            }

            try
            {
                // Early exit checks (after acquiring lock to avoid partial state)
                if (!forceStartRegardlessStatus)
                {
                    if (Status == ePluginStatus.STOPPED_FAILED)
                    {
                        log.Debug($"{this.Name} Skip reconnection because Status = STOPPED_FAILED");
                        return;
                    }

                    if (Status == ePluginStatus.STOPPING)
                    {
                        log.Debug($"{this.Name} Skip reconnection because Status = STOPPING");
                        return;
                    }
                }

                // Log and notify the error or reason
                var _msg = $"Trying to reconnect. Reason: {reason}";
                LogAndNotify(_msg, exception);

                // ✅ REQUEST THROTTLING: Prevents reconnection storms
                if (Interlocked.Increment(ref _pendingReconnectionRequests) > MaxReconnectionPendingRequests)
                {
                    Interlocked.Decrement(ref _pendingReconnectionRequests);
                    log.Warn($"{this.Name} Too many pending reconnection requests ({MaxReconnectionPendingRequests} max). Ignoring this request.");
                    return;
                }

                try
                {
                    // ✅ SEMAPHORE: Ensures only one reconnection executes at a time
                    await _reconnectionSemaphore.WaitAsync();

                    try
                    {
                        Random jitter = new Random();
                        
                        // ✅ RETRY LOOP: Replaces recursion for better reliability
                        while (_reconnectionAttempt < maxReconnectionAttempts)
                        {
                            if (!forceStartRegardlessStatus &&
                                (Status == ePluginStatus.STOPPED_FAILED || Status == ePluginStatus.STOPPING))
                            {
                                log.Debug($"{this.Name} Skip reconnection because Status = {Status}");
                                break;
                            }
                            try
                            {
                                // Re-check status before each attempt
                                if (!forceStartRegardlessStatus && Status == ePluginStatus.STOPPED_FAILED)
                                {
                                    log.Debug($"{this.Name} Skip reconnection because Status = STOPPED_FAILED");
                                    break;
                                }

                                // ✅ EXPONENTIAL BACKOFF WITH JITTER
                                _reconnectionAttempt++;
                                int backoffDelay = (int)Math.Pow(2, _reconnectionAttempt) * 1000 + jitter.Next(0, 1000);
                                
                                log.Info($"{this.Name} Reconnection attempt {_reconnectionAttempt} of {maxReconnectionAttempts} after delay {backoffDelay} ms. Reason: {reason}");
                                await ReconnectBackoffDelayAsync(backoffDelay);

                                // Final status check before attempting reconnection
                                if (!forceStartRegardlessStatus && Status == ePluginStatus.STOPPED_FAILED)
                                {
                                    log.Debug($"{this.Name} Skip reconnection because Status = STOPPED_FAILED");
                                    break;
                                }

                                // Stopped on purpose while the loop waited (or before it started): restarting it would
                                // undo that stop a few seconds later. Announce the status it was left in and stop here.
                                // Only while it is still stopped: a stop that lost a race to a start leaves the flag set
                                // on a running connector, and that connector must still recover from a real drop.
                                if (!forceStartRegardlessStatus && Volatile.Read(ref _stopRequested) == 1
                                    && (Status == ePluginStatus.STOPPED || Status == ePluginStatus.STOPPING))
                                {
                                    log.Info($"{this.Name} Reconnection ended without retry: the plugin was stopped (Status = {Status}).");
                                    RaiseOnDataReceived(GetProviderModel(Status.ToSessionStatus()));
                                    _pendingReconnectionRequests = 0;
                                    _reconnectionAttempt = 0;
                                    break;
                                }

                                // Execute reconnection sequence
                                _engineStopping = true;
                                try
                                {
                                    await StopAsync();
                                }
                                finally
                                {
                                    _engineStopping = false;
                                }

                                // Preserve reconnection state across StartAsync
                                var _reconnect = _reconnectionAttempt;
                                var _pendingRec = _pendingReconnectionRequests;
                                await StartAsync();
                                _reconnectionAttempt = _reconnect;
                                _pendingReconnectionRequests = _pendingRec;

                                // A connector signals a failed start by calling HandleConnectionLost from its own
                                // catch block, and inside this loop that call is a no-op (the re-entry guard at the
                                // top of this method already owns the reconnection flag), so StartAsync returns
                                // normally either way. The status it leaves behind is the only reliable signal.
                                // The rail shows the LAST provider status announced, so each exit below announces one --
                                // a silent give-up would keep Gemini's CONNECTED, raised right before it stamps STOPPED_FAILED.
                                if (Status == ePluginStatus.STARTED)
                                {
                                    // ✅ SUCCESS: Reset counters and exit loop
                                    log.Info($"{this.Name} Reconnection successful.");
                                    RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                                    Status = ePluginStatus.STARTED;
                                    _pendingReconnectionRequests = 0;
                                    _reconnectionAttempt = 0;
                                    break; // Exit retry loop on success
                                }
                                else if (Status == ePluginStatus.LOADED)
                                {
                                    // The plugin declined to start and stays idle by its own decision (e.g. no configured
                                    // session): retrying is pointless and stamping STARTED over it would be a lie.
                                    log.Info($"{this.Name} declined to start (Status = LOADED). Reconnection ends without marking it started.");
                                    RaiseOnDataReceived(GetProviderModel(Status.ToSessionStatus()));
                                    _pendingReconnectionRequests = 0;
                                    _reconnectionAttempt = 0;
                                    break;
                                }
                                else if (Status == ePluginStatus.STOPPED_FAILED)
                                {
                                    // Deliberately NOT gated on forceStartRegardlessStatus: that flag exists to bypass
                                    // the ENTRY guard above, so a plugin that had previously failed can be restarted at
                                    // all. It must not override the verdict THIS attempt just produced -- Binance and
                                    // Gemini set STOPPED_FAILED on a [CantConnectError] deliberately and without retry.
                                    // Counters are reset here because this is now also the FORCED exit: the settings
                                    // dialog reloads through HandleConnectionLost(force: true), and leaving the attempt
                                    // count standing would make the loop start mid-ladder on the next reload and do
                                    // nothing at all once it reached the cap. Before, the forced case fell through to
                                    // the throw and was reset by HandleMaxReconnectionAttempts.
                                    log.Debug($"{this.Name} Reconnection completed but Status = STOPPED_FAILED");
                                    RaiseOnDataReceived(GetProviderModel(Status.ToSessionStatus()));
                                    _pendingReconnectionRequests = 0;
                                    _reconnectionAttempt = 0;
                                    break;
                                }
                                else if (Status == ePluginStatus.STARTING)
                                {
                                    // STARTING is the expected shape of a failed start: base.StartAsync() stamps it
                                    // before any connector work, and a connector reports failure by handing off to
                                    // HandleConnectionLost, which is a no-op inside this loop (the re-entry guard at the
                                    // top already owns the flag). Fail the attempt so the catch below counts it and
                                    // retries, or gives up at the cap.
                                    throw new InvalidOperationException($"{this.Name} StartAsync returned with Status = {Status}; the attempt did not reach STARTED.");
                                }

                                // Anything else is NOT a failed attempt, so it is neither retried nor overwritten.
                                // STOPPED/STOPPING mean somebody stopped the plugin on purpose while it was starting:
                                // the provider rail calls the retriever's StopAsync() and force-sets STOPPED on a user
                                // toggle-off. Retrying that fights the user for ~62s of backoff and ends in
                                // STOPPED_FAILED, which the entry guard above then refuses to restart. Any other
                                // status takes this same exit because it is not evidence of a failed start either:
                                // without that evidence the loop neither retries it nor overwrites what it reports.
                                log.Info($"{this.Name} Reconnection ended without retry: StartAsync returned with Status = {Status} (not a failed start).");
                                RaiseOnDataReceived(GetProviderModel(Status.ToSessionStatus()));
                                _pendingReconnectionRequests = 0;
                                _reconnectionAttempt = 0;
                                break;
                            }
                            catch (Exception ex)
                            {
                                // Log the failure and continue to next iteration
                                var msgErr = $"Reconnection failed. Attempt {_reconnectionAttempt} of {maxReconnectionAttempts}";
                                LogException(ex, msgErr, true);
                                
                                // Check if we've exhausted all attempts
                                if (_reconnectionAttempt >= maxReconnectionAttempts)
                                {
                                    HandleMaxReconnectionAttempts();
                                    _pendingReconnectionRequests = 0;
                                    _reconnectionAttempt = 0;
                                    break; // Exit retry loop on max attempts
                                }
                                
                                // Loop continues to next attempt
                                log.Debug($"{this.Name} Will retry reconnection (attempt {_reconnectionAttempt + 1} of {maxReconnectionAttempts})");
                            }
                        }
                    }
                    finally
                    {
                        _reconnectionSemaphore.Release();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingReconnectionRequests);
                }
            }
            finally
            {
                // ✅ ATOMIC RESET: Always release the reconnection lock
                Interlocked.Exchange(ref _isReconnectingFlag, 0);
            }
        }

        public void LogException(Exception? ex, string? context = null, bool NotifyToUI = false)
        {
            if (ex == null)
                return;
            var msg = $"{this.Name} Exception";
            if (!string.IsNullOrEmpty(context))
                msg += $" Context: {context}";
            log.Error(msg, ex);
            
            if (NotifyToUI)
            {
                LogAndNotify(msg, ex);
            }
        }
        private void LogAndNotify(string reason, Exception? exception = null)
        {
            if (!string.IsNullOrEmpty(reason) && exception == null)
            {
                HelperNotificationManager.Instance.AddNotification(this.Name, reason,
                    HelprNorificationManagerTypes.WARNING, HelprNorificationManagerCategories.PLUGINS);
            }
            else if (!string.IsNullOrEmpty(reason) && exception != null)
            {
                HelperNotificationManager.Instance.AddNotification(this.Name, reason,
                    HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, exception);
            }
        }

        /// <summary>
        /// Per-frame freshness guard: if a received frame's exchange timestamp is more than
        /// <paramref name="toleranceSeconds"/> behind the (virtual) clock, raise a WARNING notification.
        /// Reads HelperTimeProvider.Now so it is deterministic under test, and shared so every
        /// connector's hot-path freshness check is identical.
        /// Returns true when a warning was raised.
        /// </summary>
        protected bool CheckFrameFreshnessAndWarn(DateTime frameTimeLocal, double toleranceSeconds = 1.0)
        {
            double lateBySeconds = Math.Abs(HelperTimeProvider.Now.Subtract(frameTimeLocal).TotalSeconds);
            if (lateBySeconds > toleranceSeconds)
            {
                var _msg = $"Rates are coming late at {lateBySeconds} seconds.";
                log.Warn(_msg);
                HelperNotificationManager.Instance.AddNotification(this.Name, _msg,
                    HelprNorificationManagerTypes.WARNING, HelprNorificationManagerCategories.PLUGINS);
                return true;
            }
            return false;
        }

        private void HandleMaxReconnectionAttempts()
        {
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED_FAILED));
            Status = ePluginStatus.STOPPED_FAILED;
            var msg = $"{this.Name} Reconnection aborted. All {maxReconnectionAttempts} attempts failed.";
            log.Fatal(msg);
            HelperNotificationManager.Instance.AddNotification(this.Name, msg, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS);
        }
        protected void SaveToUserSettings(ISetting settings)
        {
            UserSettings.SettingsManager.Instance.SetSetting(SettingKey.PLUGIN, GetPluginUniqueID(), settings);
        }
        protected T LoadFromUserSettings<T>() where T : class
        {
            var jObject = UserSettings.SettingsManager.Instance.GetSetting<object>(SettingKey.PLUGIN, GetPluginUniqueID()) as Newtonsoft.Json.Linq.JObject;
            if (jObject != null)
            {
                return jObject.ToObject<T>();
            }
            return null;
        }
        public virtual string GetPluginUniqueID()
        {
            // Get the fully qualified name of the assembly
            string assemblyName = GetType().Assembly.FullName;

            // Concatenate the attributes
            string combinedAttributes = $"{Name}{Author}{Version}{Description}{assemblyName}";

            // Compute the SHA256 hash
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combinedAttributes));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }
        public abstract object GetUISettings(); //using object type because this csproj doesn't support UI
        public virtual object GetCustomUI()
        {
            return null;
        }


        #region Symbol Normalization functions
        // 1. Parsing Method
        protected void ParseSymbols(string input)
        {
            parsedNormalizedSymbols = new Dictionary<string, string>();

            var entries = input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var parts = entry.Split(new[] { '(' }, StringSplitOptions.RemoveEmptyEntries);

                var symbol = parts[0].Trim();
                var normalizedSymbol = parts.Length > 1 ? parts[1].Trim(' ', ')') : null;

                parsedNormalizedSymbols[symbol] = !string.IsNullOrEmpty(normalizedSymbol) ? normalizedSymbol : symbol;
            }
        }

        protected List<string> GetAllNonNormalizedSymbols()
        {
            if (parsedNormalizedSymbols == null)
                return new List<string>();
            return parsedNormalizedSymbols.Keys.ToList();
        }
        protected List<string> GetAllNormalizedSymbols()
        {
            if (parsedNormalizedSymbols == null)
                return new List<string>();
            return parsedNormalizedSymbols.Values.ToList();
        }
        // 2. Normalization Method
        protected string GetNormalizedSymbol(string inputSymbol)
        {
            if (parsedNormalizedSymbols == null)
                return string.Empty;
            if (parsedNormalizedSymbols.ContainsKey(inputSymbol))
            {
                return parsedNormalizedSymbols[inputSymbol] ?? inputSymbol;
            }
            return inputSymbol;  // return the original symbol if no normalization is found.
        }
        #endregion
        /// <summary>
        /// Allocation-free decimal places detection from Span of BookItems.
        /// </summary>
        protected int RecognizeDecimalPlacesFromSpan(ReadOnlySpan<BookItem> items)
        {
            int maxDecimalPlaces = 0;

            foreach (var item in items)
            {
                if (!item.Size.HasValue)
                    continue;

                decimal value = (decimal)item.Size.Value;
                int decimalPlaces = GetDecimalPlaces(value);

                if (decimalPlaces > maxDecimalPlaces)
                    maxDecimalPlaces = decimalPlaces;
            }

            return Math.Max(1, maxDecimalPlaces);
        }
        /// <summary>
        /// Allocation-free decimal places calculation using bitwise operations.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int GetDecimalPlaces(decimal value)
        {
            // Extract scale from decimal bits (avoiding string allocation)
            int[] bits = decimal.GetBits(value);
            return (bits[3] >> 16) & 0x00FF;
        }
        protected int RecognizeDecimalPlacesAutomatically(IEnumerable<decimal> values)
        {
            int maxDecimalPlaces = 0;
            NumberFormatInfo nfi = CultureInfo.CurrentCulture.NumberFormat;

            foreach (decimal value in values)
            {
                // Convert the value to a string using the current culture's number format
                string valueAsString = value.ToString(CultureInfo.CurrentCulture);

                // Find the decimal separator of the current culture
                string decimalSeparator = nfi.NumberDecimalSeparator;
                int indexOfDecimal = valueAsString.IndexOf(decimalSeparator);

                if (indexOfDecimal != -1)
                {
                    // Count the decimal places after the decimal separator
                    int decimalPlaces = valueAsString.Substring(indexOfDecimal + decimalSeparator.Length).TrimEnd('0').Length;

                    // Update maxDecimalPlaces if this value has more decimal places
                    if (decimalPlaces > maxDecimalPlaces)
                    {
                        maxDecimalPlaces = decimalPlaces;
                    }
                }
            }

            return Math.Max(1, maxDecimalPlaces);
        }
        protected int RecognizeDecimalPlacesAutomatically(IEnumerable<double> values)
        {
            int maxDecimalPlaces = 0;
            NumberFormatInfo nfi = CultureInfo.CurrentCulture.NumberFormat;

            foreach (decimal value in values)
            {
                // Convert the value to a string using the current culture's number format
                string valueAsString = value.ToString(CultureInfo.CurrentCulture);

                // Find the decimal separator of the current culture
                string decimalSeparator = nfi.NumberDecimalSeparator;
                int indexOfDecimal = valueAsString.IndexOf(decimalSeparator);

                if (indexOfDecimal != -1)
                {
                    // Count the decimal places after the decimal separator
                    int decimalPlaces = valueAsString.Substring(indexOfDecimal + decimalSeparator.Length).TrimEnd('0').Length;

                    // Update maxDecimalPlaces if this value has more decimal places
                    if (decimalPlaces > maxDecimalPlaces)
                    {
                        maxDecimalPlaces = decimalPlaces;
                    }
                }
            }

            return Math.Max(1, maxDecimalPlaces);
        }




        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                parsedNormalizedSymbols?.Clear();
            }

        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

    }

}
