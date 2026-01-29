using System.Security.Cryptography;
using System.Text;
using VisualHFT.Commons.Extensions;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Studies;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;

namespace VisualHFT.Commons.PluginManager
{
    public abstract class BasePluginStudy : IStudy, VisualHFT.PluginManager.IPlugin, IDisposable
    {
        private HelperCustomQueue<BaseStudyModel> _QUEUE;
        private AggregatedCollection<BaseStudyModel> _AGG_DATA;
        protected bool _disposed = false; // to track whether the object has been disposed

        private const int maxReconnectionAttempts = 5;
        private const int MaxReconnectionPendingRequests = 1;
        private int _reconnectionAttempt = 0;
        private int _pendingReconnectionRequests = 0;
        private SemaphoreSlim _reconnectionSemaphore = new SemaphoreSlim(1, 1);


        protected static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public abstract event EventHandler<decimal> OnAlertTriggered;
        public event EventHandler<BaseStudyModel> OnCalculated;
        public abstract string Name { get; set; }
        public abstract string Version { get; set; }
        public abstract string Description { get; set; }
        public abstract string Author { get; set; }
        public abstract ISetting Settings { get; set; }
        public ePluginStatus Status { get; set; }
        public abstract Action CloseSettingWindow { get; set; }
        public abstract string TileTitle { get; set; }
        public abstract string TileToolTip { get; set; }
        public bool IsChartButtonVisible { get; set; } = true;
        public bool IsSettingsButtonVisisble { get; set; } = true;
        public bool IsFooterVisible { get; set; } = true;

        public ePluginType PluginType
        {
            get { return ePluginType.STUDY; }
        }
        public virtual eLicenseLevel RequiredLicenseLevel => eLicenseLevel.COMMUNITY; // Default to Community, override in derived classes if needed

        protected abstract void LoadSettings();
        protected abstract void SaveSettings();
        protected abstract void InitializeDefaultSettings();

        /// <summary>
        /// This method defines how the internal AggregatedCollection should aggregate incoming items.
        /// It is invoked whenever a new item is added to the collection and aggregation is required.
        /// The method takes the existing collection of items, the new incoming item, and a counter indicating
        /// how many times the last item has been aggregated. The aggregation logic should be implemented
        /// within this method to combine or process the items as needed.
        /// </summary>
        /// <param name="dataCollection">The existing internal collection of items.</param>
        /// <param name="newItem">The new incoming item to be aggregated.</param>
        /// <param name="lastItemAggregationCount">Counter indicating how many times the last item has been aggregated.</param>
        protected virtual void onDataAggregation(List<BaseStudyModel> dataCollection, BaseStudyModel newItem, int lastItemAggregationCount)
        {

            OnCalculated?.Invoke(this, dataCollection.LastOrDefault());
        }
        protected virtual void onDataAdded()
        {
        }



        public BasePluginStudy()
        {
            // Ensure settings are loaded before starting
            LoadSettings();
            if (Settings == null)
                throw new InvalidOperationException($"{Name} plugin settings has not been loaded.");
            HelperProvider.Instance.OnStatusChanged += Provider_OnStatusChanged;
            HelperProvider.Instance.OnProviderStale += Provider_OnProviderStale; //when no data for 30 seconds is received.
            HelperOrderBook.Instance.OnException += HelperOrderBookInstance_OnException;//subscribe and hear for exceptions on this Plugin
            Status = ePluginStatus.LOADED;
        }



        public virtual async Task StartAsync()
        {
            _QUEUE?.Dispose();
            _QUEUE = new HelperCustomQueue<BaseStudyModel>($"<BaseStudyModel>_{this.Name}", QUEUE_onReadAction, QUEUE_onErrorAction);
            _AGG_DATA?.Dispose();
            _AGG_DATA = new AggregatedCollection<BaseStudyModel>(
                Settings.AggregationLevel,
                1, //only one item needs to be hold
                (x => x.Timestamp),
                onDataAggregation
                );
            Status = ePluginStatus.STARTING;
            log.Info($"{this.Name} is starting.");
        }
        public virtual async Task StopAsync()
        {

            Status = ePluginStatus.STOPPED;
            log.Info($"{this.Name} has stopped.");
        }
        protected async Task HandleRestart(string reason = null, Exception exception = null, bool forceStartRegardlessStatus = false)
        {
            if (!forceStartRegardlessStatus)
            {
                if (Status == ePluginStatus.STOPPED_FAILED) //means that a fata error occurred, and user's attention is needed.
                {
                    log.Debug($"{this.Name} Skip restart because Status = STOPPED_FAILED");
                    return;
                }

                if (Status == ePluginStatus.STOPPING)
                {
                    log.Debug($"{this.Name} Skip restart because Status = STOPPING");
                    return;
                }
            }
            log.Debug(reason);

            // Log and notify the error or reason
            LogAndNotify(reason, exception);

            if (Interlocked.Increment(ref _pendingReconnectionRequests) > MaxReconnectionPendingRequests)
            {
                Interlocked.Decrement(ref _pendingReconnectionRequests);
                log.Warn($"{this.Name} Too many pending requests.");
                return;
            }



            try
            {
                await _reconnectionSemaphore.WaitAsync();

                if (!forceStartRegardlessStatus && Status == ePluginStatus.STOPPED_FAILED) //means that a fata error occurred, and user's attention is needed.
                {
                    log.Debug($"{this.Name} Skip restart because Status = STOPPED_FAILED");
                    return;
                }

                Random jitter = new Random();
                int backoffDelay = (int)Math.Pow(2, _reconnectionAttempt) * 1000 + jitter.Next(0, 1000);
                await Task.Delay(backoffDelay);
                log.Info($"{this.Name} Reconnection attempt {_reconnectionAttempt} of {maxReconnectionAttempts} after delay {backoffDelay} ms");

                if (!forceStartRegardlessStatus && Status == ePluginStatus.STOPPED_FAILED) //means that a fata error occurred, and user's attention is needed.
                {
                    log.Debug($"{this.Name} Skip restart because Status = STOPPED_FAILED");
                    return;
                }


                await StopAsync();
                await StartAsync();



                if (!forceStartRegardlessStatus && Status == ePluginStatus.STOPPED_FAILED) //means that a fata error occurred, and user's attention is needed.
                {
                    log.Debug($"{this.Name} Skip restart because Status = STOPPED_FAILED");
                    return;
                }
                log.Info($"{this.Name} Restart successful.");

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
                    var msgErr = $"{this.Name} Restart failed. Attempt {_reconnectionAttempt} of {maxReconnectionAttempts}";
                    log.Error(msgErr, ex);
                    LogAndNotify(msgErr, ex);

                    _reconnectionSemaphore.Release();
                    await HandleRestart(ex.Message, ex);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pendingReconnectionRequests);
                _reconnectionSemaphore.Release();
            }

        }
        private void LogAndNotify(string reason, Exception exception)
        {
            if (!string.IsNullOrEmpty(reason) && exception == null)
            {
                var _msg = $"Trying to restart. Reason: {reason}";
                HelperNotificationManager.Instance.AddNotification(this.Name, _msg,
                    HelprNorificationManagerTypes.WARNING, HelprNorificationManagerCategories.PLUGINS);
            }
            else if (!string.IsNullOrEmpty(reason) && exception != null)
            {
                var _msg = $"Trying to restart. Reason: {reason}.";
                HelperNotificationManager.Instance.AddNotification(this.Name, _msg,
                    HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, exception);
            }
        }
        private void HandleMaxReconnectionAttempts()
        {
            Status = ePluginStatus.STOPPED_FAILED;
            var msg = $"{this.Name} Restart aborted. All {maxReconnectionAttempts} attempts failed.";
            log.Fatal(msg);
            HelperNotificationManager.Instance.AddNotification(this.Name, msg, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS);
        }

        private void HelperOrderBookInstance_OnException(Model.ErrorEventArgs obj)
        {
            if (obj.Context is BasePluginStudy study && study == this)
            {
                StopAsync();


                Status = ePluginStatus.STOPPED_FAILED;
                HelperNotificationManager.Instance.AddNotification(this.Name, obj.Exception.Message, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, obj.Exception);

                OnCalculated?.Invoke(this, new BaseStudyModel()
                {
                    Value = 0,
                    Timestamp = HelperTimeProvider.Now,
                    ValueColor = "Red",
                    HasError = true,
                    Tooltip = obj.Exception.Message
                });

            }
        }
        private void Provider_OnStatusChanged(object? sender, Provider e)
        {
            if (Settings?.Provider == null && Settings?.Provider?.ProviderCode == e.ProviderID)
            {
                ePluginStatus newStatus = e.Status.ToPluginStatus();
                if (newStatus != Status)
                {
                    //handle status changed
                    if (newStatus == ePluginStatus.STOPPED || newStatus == ePluginStatus.STOPPED_FAILED)
                    {
                        bool isError = newStatus == ePluginStatus.STOPPED_FAILED;
                        AddCalculation(new BaseStudyModel()
                        {
                            HasError = isError,
                            IsStale = !isError,
                            Timestamp = HelperTimeProvider.Now,
                            Tooltip = "Provider status: " + newStatus.ToString(),
                            ValueColor = "Red"
                        });
                    }
                    Status = newStatus;
                }
            }
        }
        private void Provider_OnProviderStale(object? sender, Provider e)
        {
            if (Settings?.Provider?.ProviderCode == e.ProviderID)
            {
                // Send "stale data" indicator to UI
                AddCalculation(new BaseStudyModel()
                {
                    IsStale = true,
                    Timestamp = HelperTimeProvider.Now,
                    Tooltip = $"{e.ProviderName} is stale - no data for 30+ seconds",
                    ValueColor = "Orange", // Different from "Red" (error)
                });
            }
        }

        /// <summary>
        /// Adds the calculation.
        /// As soon as a new calculation is available, this method will process it internally, and raise proper events.
        /// If aggregation is set, it will make sure that events will be fired at proper time frames.
        /// See also “onDataAggregation”, where it must be defined how these items are going to be aggregated, if needed.
        /// </summary>
        /// <param name="e">The e.</param>
        protected void AddCalculation(BaseStudyModel e)
        {
            _QUEUE?.Add(e);
        }

        private void QUEUE_onReadAction(BaseStudyModel item)
        {
            if (!item.AddItemSkippingAggregation && _AGG_DATA.Add(item))
            {
                OnCalculated?.Invoke(this, item);
                onDataAdded();
            }
            else if (item.AddItemSkippingAggregation && _AGG_DATA.ForceAddSkippingAggregation(item))
            {
                OnCalculated?.Invoke(this, item);
                onDataAdded();
            }
        }
        private void QUEUE_onErrorAction(Exception ex)
        {
            var _error = $"{this.Name} Unhandled error in the consumer Queue: {ex.Message}";
            log.Error(_error, ex);
            HelperNotificationManager.Instance.AddNotification(this.Name, _error,
                HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS, ex);
        }


        protected void SaveToUserSettings(ISetting settings)
        {
            UserSettings.SettingsManager.Instance.SetSetting(SettingKey.TILE_STUDY, GetPluginUniqueID(), settings);
        }
        protected T LoadFromUserSettings<T>() where T : class
        {
            var jObject = UserSettings.SettingsManager.Instance.GetSetting<object>(SettingKey.TILE_STUDY, GetPluginUniqueID()) as Newtonsoft.Json.Linq.JObject;
            if (jObject != null)
            {
                return jObject.ToObject<T>();
            }
            return null;
        }

        private string _pluginUniqueID = null;
        public virtual string GetPluginUniqueID()
        {
            if (_pluginUniqueID != null)
                return _pluginUniqueID;

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
                _pluginUniqueID = builder.ToString();
            }
            return _pluginUniqueID;
        }
        public abstract object GetUISettings(); //using object type because this csproj doesn't support UI
        public virtual object GetCustomUI()
        {
            return null;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                HelperProvider.Instance.OnStatusChanged -= Provider_OnStatusChanged;
                HelperProvider.Instance.OnProviderStale -= Provider_OnProviderStale;
                HelperOrderBook.Instance.OnException -= HelperOrderBookInstance_OnException; ; //subscribe and hear for exceptions on this Plugin

                _QUEUE?.Dispose();
                _AGG_DATA?.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


    }
}
