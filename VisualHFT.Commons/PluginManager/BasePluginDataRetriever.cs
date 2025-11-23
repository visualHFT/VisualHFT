using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        private const int MaxReconnectionPendingRequests = 1;
        private int _reconnectionAttempt = 0;
        private int _pendingReconnectionRequests = 0;
        private SemaphoreSlim _reconnectionSemaphore = new SemaphoreSlim(1, 1);


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
        }
        public virtual async Task StopAsync()
        {
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
        /// This way, the internal reconnection process, could take this, and perform its execution as required.
        /// </summary>
        /// <param name="internalStartAsync">The internal start async.</param>
        protected void SetReconnectionAction(Func<Task> internalStartAsync)
        {
            _internalStartAsync = internalStartAsync;
        }

        protected bool _isReconnecting = false;
        protected async Task HandleConnectionLost(string reason = null, Exception exception = null, bool forceStartRegardlessStatus = false)
        {
            if (_isReconnecting)
                return;
            _isReconnecting = true;
            if (!forceStartRegardlessStatus)
            {
                if (Status == ePluginStatus
                        .STOPPED_FAILED) //means that a fata error occurred, and user's attention is needed.
                {
                    log.Debug($"{this.Name} Skip reconnection because Status = STOPPED_FAILED");
                    _isReconnecting = false;
                    return;
                }

                if (Status == ePluginStatus.STOPPING)
                {
                    log.Debug($"{this.Name} Skip reconnection because Status = STOPPING");
                    _isReconnecting = false;
                    return;
                }
            }

            // Log and notify the error or reason
            var _msg = $"Trying to reconnect. Reason: {reason}";
            LogAndNotify(_msg, exception);

            if (Interlocked.Increment(ref _pendingReconnectionRequests) > MaxReconnectionPendingRequests)
            {
                Interlocked.Decrement(ref _pendingReconnectionRequests);
                log.Warn($"{this.Name} Too many pending requests.");
                _isReconnecting = false;
                return;
            }



            try
            {
                await _reconnectionSemaphore.WaitAsync();

                if (!forceStartRegardlessStatus && Status == ePluginStatus.STOPPED_FAILED) //means that a fata error occurred, and user's attention is needed.
                {
                    log.Debug($"{this.Name} Skip reconnection because Status = STOPPED_FAILED");
                    _isReconnecting = false;
                    return;
                }

                Random jitter = new Random();
                _reconnectionAttempt++;
                int backoffDelay = (int)Math.Pow(2, _reconnectionAttempt) * 1000 + jitter.Next(0, 1000);
                await Task.Delay(backoffDelay);
                log.Info($"{this.Name} Reconnection attempt {_reconnectionAttempt} of {maxReconnectionAttempts} after delay {backoffDelay} ms. Reason: {reason}");

                if (!forceStartRegardlessStatus && Status == ePluginStatus.STOPPED_FAILED) //means that a fata error occurred, and user's attention is needed.
                {
                    log.Debug($"{this.Name} Skip reconnection because Status = STOPPED_FAILED");
                    _isReconnecting = false;
                    return;
                }


                await StopAsync();
                if (_internalStartAsync != null)
                    await _internalStartAsync.Invoke();
                {
                    //avoid resetting these values
                    var _reconnect = _reconnectionAttempt;
                    var _pendingRec = _pendingReconnectionRequests;
                    await StartAsync();
                    _reconnectionAttempt = _reconnect;
                    _pendingReconnectionRequests = _pendingRec;
                }


                if (!forceStartRegardlessStatus && Status == ePluginStatus.STOPPED_FAILED) //means that a fata error occurred, and user's attention is needed.
                {
                    log.Debug($"{this.Name} Skip reconnection because Status = STOPPED_FAILED");
                    _isReconnecting = false;
                    return;
                }
                log.Info($"{this.Name} Reconnection successful.");
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                Status = ePluginStatus.STARTED;
                //reset
                _pendingReconnectionRequests = 0;
                _reconnectionAttempt = 0;
            }
            catch (Exception ex)
            {
                _reconnectionAttempt++;
                if (_reconnectionAttempt >= maxReconnectionAttempts)
                {
                    HandleMaxReconnectionAttempts();
                    //reset
                    _pendingReconnectionRequests = 0;
                    _reconnectionAttempt = 0;
                }
                else
                {
                    var msgErr = $"Reconnection failed. Attempt {_reconnectionAttempt} of {maxReconnectionAttempts}";
                    LogException(ex, msgErr, true);
                    _reconnectionSemaphore.Release();
                    await HandleConnectionLost(ex.Message, ex);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pendingReconnectionRequests);
                _reconnectionSemaphore.Release();
                _isReconnecting = false;
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
