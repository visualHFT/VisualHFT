using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using Kucoin.Net;
using Kucoin.Net.Clients;
using Kucoin.Net.Enums;
using Kucoin.Net.Interfaces.Clients;
using Kucoin.Net.Objects.Models.Spot;
using Kucoin.Net.Objects.Models.Spot.Socket;
using MarketConnectors.KuCoin.Model;
using MarketConnectors.KuCoin.UserControls;
using MarketConnectors.KuCoin.ViewModel;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Interfaces;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Commons.Pools;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
namespace MarketConnectors.KuCoin
{
    public class KuCoinPlugin : BasePluginDataRetriever, IDataRetrieverTestable
    {
        private bool _disposed = false; // to track whether the object has been disposed

        private PlugInSettings _settings;
        private IKucoinSocketClient _socketClient;
        private IKucoinRestClient _restClient;

        private Dictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks =
            new Dictionary<string, VisualHFT.Model.OrderBook>();
        private readonly ReaderWriterLockSlim _orderBooksLock = new ReaderWriterLockSlim();

        private readonly object _buffersLock = new object();
        private Dictionary<string, HelperCustomQueue<Tuple<DateTime, string, KucoinStreamOrderBook>>> _eventBuffers =
            new Dictionary<string, HelperCustomQueue<Tuple<DateTime, string, KucoinStreamOrderBook>>>();

        private Dictionary<string, HelperCustomQueue<Tuple<string, KucoinTrade>>> _tradesBuffers =
            new Dictionary<string, HelperCustomQueue<Tuple<string, KucoinTrade>>>();


        private Dictionary<string, VisualHFT.Model.Order> _localUserOrders =
            new Dictionary<string, VisualHFT.Model.Order>();
        private readonly object _userOrdersLock = new object();

        private readonly ConcurrentBag<CallResult<UpdateSubscription>> _allSubscriptions = new();

        private readonly CustomObjectPool<DeltaBookItem> DeltaBookItemPool = new CustomObjectPool<DeltaBookItem>(1_000);

        private int pingFailedAttempts = 0;
        private System.Timers.Timer _timerPing;
        private CallResult<UpdateSubscription> deltaSubscription;
        private CallResult<UpdateSubscription> tradesSubscription;

        private static readonly log4net.ILog log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly CustomObjectPool<VisualHFT.Model.Trade> tradePool =
            new CustomObjectPool<VisualHFT.Model.Trade>(); //pool of Trade objects

        public override string Name { get; set; } = "KuCoin Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Connects to KuCoin websockets.";
        public override string Author { get; set; } = "VisualHFT";

        public override ISetting Settings
        {
            get => _settings;
            set => _settings = (PlugInSettings)value;
        }

        public override Action CloseSettingWindow { get; set; }

        public KuCoinPlugin()
        {
            _socketClient = new KucoinSocketClient();
            _restClient = new KucoinRestClient();
            SetReconnectionAction(InternalStartAsync);
            log.Info($"{this.Name} has been loaded.");
        }

        ~KuCoinPlugin()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {
            if (Status == ePluginStatus.STARTED ||
                    Status == ePluginStatus.STARTING)
            {
                log.Warn("Already started or starting, ignoring duplicate Start request");
                return;
            }

            await base.StartAsync(); //call the base first
            
            // ✅ Dispose old clients first
            _socketClient?.Dispose();
            _restClient?.Dispose();

            _socketClient = new KucoinSocketClient(options =>
            {
                if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret) &&
                    !string.IsNullOrEmpty(_settings.APIPassPhrase))
                {
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey,
                        _settings.ApiSecret, _settings.APIPassPhrase);
                }

                options.Environment = KucoinEnvironment.Live;
            });


            _restClient = new KucoinRestClient(options =>
            {
                if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret) &&
                    !string.IsNullOrEmpty(_settings.APIPassPhrase))
                {
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey,
                        _settings.ApiSecret, _settings.APIPassPhrase);
                }

                options.Environment = KucoinEnvironment.Live;
            });


            try
            {
                await InternalStartAsync();
                if (Status == ePluginStatus.STOPPED_FAILED) //check again here for failure
                    return;
                log.Info($"Plugin has successfully started.");
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                Status = ePluginStatus.STARTED;


            }
            catch (Exception ex)
            {
                var _error = ex.Message;
                LogException(ex, _error);
                await HandleConnectionLost(_error, ex);
            }
        }

        private async Task InternalStartAsync()
        {
            await ClearAsync();
            lock (_buffersLock) // ✅ Protect initialization
            {
                // Initialize event buffer for each symbol
                foreach (var symbol in GetAllNormalizedSymbols())
                {
                    _eventBuffers.Add(symbol,
                        new HelperCustomQueue<Tuple<DateTime, string, KucoinStreamOrderBook>>(
                            $"<Tuple<DateTime, string, KucoinStreamOrderBookChanged>>_{this.Name.Replace(" Plugin", "")}",
                            eventBuffers_onReadAction, eventBuffers_onErrorAction));
                    _tradesBuffers.Add(symbol,
                        new HelperCustomQueue<Tuple<string, KucoinTrade>>(
                            $"<Tuple<DateTime, string, KucoinTrade>>_{this.Name.Replace(" Plugin", "")}",
                            tradesBuffers_onReadAction, tradesBuffers_onErrorAction));

                    _eventBuffers[symbol]
                        .PauseConsumer(); //this will allow collecting deltas (without delivering it), until we have the snapshot
                }
            }
            await InitializeDeltasAsync(); //must start collecting deltas before snapshot
            await InitializeSnapshotsAsync();
            await InitializeOpenOrders();
            await InitializeTradesAsync();
            await InitializePingTimerAsync();
            await InitializeUserPrivateOrders();
        }

        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");

            await ClearAsync();
            RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));

            await base.StopAsync();
        }

        public async Task ClearAsync()
        {
            var subscriptions = _allSubscriptions.ToList(); // ✅ Snapshot
            foreach (var sub in subscriptions)
            {
                UnattachEventHandlers(sub?.Data);
                if (sub?.Data != null)
                    await sub.Data.CloseAsync();
            }

            // Clear using TryTake loop
            while (_allSubscriptions.TryTake(out _)) { } // ✅ Thread-safe clear


            UnattachEventHandlers(deltaSubscription?.Data);
            UnattachEventHandlers(tradesSubscription?.Data);

            if (_socketClient != null)
                await _socketClient.UnsubscribeAllAsync();
            if (deltaSubscription != null && deltaSubscription.Data != null)
                await deltaSubscription.Data.CloseAsync();
            if (tradesSubscription != null && tradesSubscription.Data != null)
                await tradesSubscription.Data.CloseAsync();
            _timerPing?.Stop();
            _timerPing?.Dispose();
            lock (_buffersLock)
            {
                foreach (var q in _eventBuffers.Values)
                {
                    q?.PauseConsumer(); // ⬅️ ADD THIS: Ensure paused before stopping
                    q?.Stop();
                    q?.Dispose();
                }
                _eventBuffers.Clear();

                foreach (var q in _tradesBuffers.Values)
                {
                    q?.PauseConsumer(); // ⬅️ ADD THIS: Ensure paused before stopping
                    q?.Stop();
                    q?.Dispose(); 
                }
                _tradesBuffers.Clear();
            }

            // ✅ Don't dispose order books during reconnection
            // Only dispose during full shutdown (StopAsync/Dispose)
            if (Status == ePluginStatus.STOPPING || _disposed)
            {
                _orderBooksLock.EnterWriteLock();
                try
                {
                    if (_localOrderBooks != null)
                    {
                        foreach (var lob in _localOrderBooks.Values)
                        {
                            lob?.Dispose();
                        }
                        _localOrderBooks.Clear();
                    }
                }
                finally
                {
                    _orderBooksLock.ExitWriteLock();
                }
            }
        }

        private async Task InitializeTradesAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var _normalizedSymbol = GetNormalizedSymbol(symbol);
                var _traderQueueRef = _tradesBuffers[_normalizedSymbol];

                log.Info($"{this.Name}: sending WS Trades Subscription {_normalizedSymbol} ");
                tradesSubscription = await _socketClient.SpotApi.SubscribeToTradeUpdatesAsync(
                    symbol,
                    trade =>
                    {
                        // Buffer the trades
                        if (trade.Data != null)
                        {
                            try
                            {

                                KucoinTrade trade1 = new KucoinTrade();
                                trade1.Sequence = trade.Data.Sequence;
                                trade1.Price = trade.Data.Price;
                                trade1.Timestamp = trade.Data.Timestamp;
                                trade1.Quantity = trade.Data.Quantity;
                                trade1.Side = trade.Data.Side;
                                string _normalizedSymbol = "(null)";
                                if (trade != null && trade.Data != null)
                                {
                                    _normalizedSymbol = GetNormalizedSymbol(trade.Data.Symbol);
                                }

                                _traderQueueRef.Add(
                                    new Tuple<string, KucoinTrade>(_normalizedSymbol, trade1));

                            }
                            catch (Exception ex)
                            {

                                var _error =
                                    $"Will reconnect. Unhandled error while receiving trading data for {_normalizedSymbol}.";
                                LogException(ex, _error);
                                Task.Run(async () => await HandleConnectionLost(_error, ex));
                            }
                        }
                    });
                if (tradesSubscription.Success)
                {
                    AttachEventHandlers(tradesSubscription.Data);
                    _allSubscriptions.Add(tradesSubscription); // ✅ Track all subscriptions
                }
                else
                {
                    var _error =
                        $"Unsuccessful trades subscription for {_normalizedSymbol} error: {tradesSubscription.Error}";
                    throw new Exception(_error);
                }
            }
        }

        private async Task InitializeOpenOrders()
        {
            if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret) &&
                !string.IsNullOrEmpty(_settings.APIPassPhrase))
            {
                var orders = await _restClient.SpotApi.Trading.GetOrdersAsync();
                if (orders != null && orders.Success)
                {
                    foreach (KucoinOrder item in orders.Data.Items)
                    {
                        VisualHFT.Model.Order localuserOrder;
                        
                        lock (_userOrdersLock) // ✅ Protect dictionary operations
                        {
                            if (!this._localUserOrders.ContainsKey(item.Id))
                            {
                                localuserOrder = new VisualHFT.Model.Order();
                                localuserOrder.ClOrdId = item.Id;
                                localuserOrder.Currency = GetNormalizedSymbol(item.Symbol);
                                localuserOrder.CreationTimeStamp = item.CreateTime;
                                localuserOrder.OrderID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                                localuserOrder.ProviderName = _settings.Provider.ProviderName;
                                localuserOrder.CreationTimeStamp = item.CreateTime;
                                localuserOrder.Quantity = (double)item.Quantity;
                                localuserOrder.PricePlaced = (double)item.Price;
                                localuserOrder.Symbol = GetNormalizedSymbol(item.Symbol);
                                localuserOrder.TimeInForce = eORDERTIMEINFORCE.GTC;
                                
                                this._localUserOrders.Add(item.Id, localuserOrder);
                            }
                            else
                            {
                                localuserOrder = this._localUserOrders[item.Id];
                            }

                            if (item.Type == OrderType.Market)
                            {
                                localuserOrder.OrderType = eORDERTYPE.MARKET;
                            }
                            else if (item.Type == OrderType.Limit)
                            {
                                localuserOrder.OrderType = eORDERTYPE.LIMIT;
                            }
                            else
                            {
                                localuserOrder.OrderType = eORDERTYPE.PEGGED;
                            }

                            if (item.Side == OrderSide.Buy)
                            {
                                localuserOrder.PricePlaced = (double)item.Price;
                                localuserOrder.BestBid = (double)item.Price;
                                localuserOrder.Side = eORDERSIDE.Buy;
                            }

                            if (item.Side == OrderSide.Sell)
                            {
                                localuserOrder.Side = eORDERSIDE.Sell;
                                localuserOrder.BestAsk = (double)item.Price;
                                localuserOrder.Quantity = (double)item.Quantity;
                            }

                            localuserOrder.LastUpdated = DateTime.Now;
                            localuserOrder.FilledPercentage =
                                Math.Round((100 / localuserOrder.Quantity) * localuserOrder.FilledQuantity, 2);
                        } // ✅ Release lock before raising event
                        
                        RaiseOnDataReceived(localuserOrder);

                    }
                }
            }
        }

        private async Task InitializeUserPrivateOrders()
        {
            if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret) &&
                !string.IsNullOrEmpty(_settings.APIPassPhrase))
            {
                await _socketClient.SpotApi.SubscribeToOrderUpdatesAsync(
                    async neworder => { await UpdateUserOrder(neworder.Data); },
                    async onOrderData => { await UpdateUserOrder(onOrderData.Data); },
                    async onTradeData => { await UpdateUserOrder(onTradeData.Data); });
            }
        }

        private async Task InitializeDeltasAsync()
        {

            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);
                log.Info($"{this.Name}: sending WS Delta Subscription {normalizedSymbol} ");
                deltaSubscription = await _socketClient.SpotApi.SubscribeToAggregatedOrderBookUpdatesAsync(
                    symbol,
                    data =>
                    {
                        // Buffer the events
                        if (data.Data != null)
                        {
                            try
                            {
                                if (data.UpdateType == SocketUpdateType.Snapshot)
                                {
                                    return; //not valid condition
                                }
                                else
                                {
                                    if (Math.Abs(DateTime.Now.Subtract(data.ReceiveTime.ToLocalTime()).TotalSeconds) > 1)
                                    {
                                        var _msg =
                                            $"Rates are coming late at {Math.Abs(DateTime.Now.Subtract(data.ReceiveTime.ToLocalTime()).TotalSeconds)} seconds.";
                                        log.Warn(_msg);
                                        HelperNotificationManager.Instance.AddNotification(this.Name, _msg,
                                            HelprNorificationManagerTypes.WARNING,
                                            HelprNorificationManagerCategories.PLUGINS);
                                    }
                                    HelperCustomQueue <Tuple<DateTime, string, KucoinStreamOrderBook>> buffer;
                                    lock (_buffersLock) // ✅ Protect access
                                    {
                                        if (!_eventBuffers.TryGetValue(normalizedSymbol, out buffer))
                                            return; // Buffer was cleared
                                    }

                                    buffer.Add(
                                        new Tuple<DateTime, string, KucoinStreamOrderBook>(
                                            data.ReceiveTime.ToLocalTime(), normalizedSymbol, data.Data));
                                }
                            }
                            catch (Exception ex)
                            {
                                string _normalizedSymbol = "(null)";
                                if (data != null && data.Data != null)
                                    _normalizedSymbol = GetNormalizedSymbol(data.Symbol);

                                var _error = $"Will reconnect. Unhandled error while receiving delta market data for {_normalizedSymbol}: {ex.GetType().Name} - {ex.Message}";
                                LogException(ex, _error);

                                // ✅ Add throttling to prevent reconnection storm
                                if (Status == ePluginStatus.STARTED) // Only reconnect if we were actually running
                                {
                                    Task.Run(async () => await HandleConnectionLost(_error, ex));
                                }
                            }
                        }
                    }, new CancellationToken());
                if (deltaSubscription.Success)
                {
                    AttachEventHandlers(deltaSubscription.Data);
                    _allSubscriptions.Add(deltaSubscription);
                    await Task.Delay(1000); //to let deltas start collecting
                }
                else
                {
                    var _error =
                        $"Unsuccessful deltas subscription for {normalizedSymbol} error: {deltaSubscription.Error}";
                    throw new Exception(_error);
                }
            }
        }

        private async Task InitializeSnapshotsAsync()
        {
            try
            {

                foreach (var symbol in GetAllNonNormalizedSymbols())
                {
                    var normalizedSymbol = GetNormalizedSymbol(symbol);
                    
                    // ✅ Protect dictionary write with write lock
                    _orderBooksLock.EnterWriteLock();
                    try
                    {
                        if (!_localOrderBooks.ContainsKey(normalizedSymbol))
                        {
                            _localOrderBooks.Add(normalizedSymbol, null);
                        }
                    }
                    finally
                    {
                        _orderBooksLock.ExitWriteLock();
                    }

                    log.Info($"{this.Name}: Getting snapshot {normalizedSymbol} level 2");

                    // Fetch initial depth snapshot 
                    var depthSnapshot =
                        await _restClient.SpotApi.ExchangeData
                            .GetAggregatedPartialOrderBookAsync(symbol, 20);
                            
                    if (depthSnapshot.Success)
                    {
                        var orderBook = ToOrderBookModel(depthSnapshot.Data, normalizedSymbol);
                        
                        // ✅ Protect dictionary write with write lock
                        _orderBooksLock.EnterWriteLock();
                        try
                        {
                            _localOrderBooks[normalizedSymbol] = orderBook;
                        }
                        finally
                        {
                            _orderBooksLock.ExitWriteLock();
                        }
                        
                        log.Info($"{this.Name}: LOB {normalizedSymbol} level 2 Successfully loaded.");
                        
                        // ✅ Protect buffer access with lock
                        lock (_buffersLock)
                        {
                            if (_eventBuffers.TryGetValue(normalizedSymbol, out var buffer))
                                buffer.ResumeConsumer();
                        }
                    }
                    else
                    {
                        var _error =
                            $"Unsuccessful snapshot request for {normalizedSymbol} error: {depthSnapshot.ResponseStatusCode} - {depthSnapshot.Error}";
                        throw new Exception(_error);
                    }

                }
            }
            catch (Exception ex)
            {
                LogException(ex, "Error getting snapshots.");
            }
        }

        private async Task InitializePingTimerAsync()
        {
            _timerPing?.Stop();
            _timerPing?.Dispose();

            _timerPing = new System.Timers.Timer(3000); // Set the interval to 3000 milliseconds (3 seconds)
            _timerPing.Elapsed += async (sender, e) => await DoPingAsync();
            _timerPing.AutoReset = true;
            _timerPing.Enabled = true; // Start the timer
        }

        private void eventBuffers_onReadAction(Tuple<DateTime, string, KucoinStreamOrderBook> eventData)
        {
            UpdateOrderBook(eventData.Item3, eventData.Item2, eventData.Item1);
        }

        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";

            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }

        private void tradesBuffers_onReadAction(Tuple<string, KucoinTrade> item)
        {
            var trade = tradePool.Get();
            try
            {
                trade.Price = item.Item2.Price;
                trade.Size = Math.Abs(item.Item2.Quantity);
                trade.Symbol = item.Item1;
                trade.Timestamp = item.Item2.Timestamp.ToLocalTime();
                trade.ProviderId = _settings.Provider.ProviderID;
                trade.ProviderName = _settings.Provider.ProviderName;
                trade.IsBuy = item.Item2.Side == OrderSide.Buy;
                
                // ✅ Protect OrderBook access with read lock
                _orderBooksLock.EnterReadLock();
                try
                {
                    trade.MarketMidPrice = _localOrderBooks.TryGetValue(item.Item1, out var lob) && lob != null
                        ? lob.MidPrice
                        : 0;
                }
                finally
                {
                    _orderBooksLock.ExitReadLock();
                }

                RaiseOnDataReceived(trade);
            }
            finally
            {
                tradePool.Return(trade); // ✅ Always return
            }
        }

        private void tradesBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Trades Queue: {ex.Message}";

            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }


        #region Websocket Deltas Callbacks

        private void AttachEventHandlers(UpdateSubscription data)
        {
            if (data == null)
                return;
            data.Exception += deltaSubscription_Exception;
            data.ConnectionLost += deltaSubscription_ConnectionLost;
            data.ConnectionClosed += deltaSubscription_ConnectionClosed;
            data.ConnectionRestored += deltaSubscription_ConnectionRestored;
            data.ActivityPaused += deltaSubscription_ActivityPaused;
            data.ActivityUnpaused += deltaSubscription_ActivityUnpaused;
        }

        private void UnattachEventHandlers(UpdateSubscription data)
        {
            if (data == null)
                return;

            data.Exception -= deltaSubscription_Exception;
            data.ConnectionLost -= deltaSubscription_ConnectionLost;
            data.ConnectionClosed -= deltaSubscription_ConnectionClosed;
            data.ConnectionRestored -= deltaSubscription_ConnectionRestored;
            data.ActivityPaused -= deltaSubscription_ActivityPaused;
            data.ActivityUnpaused -= deltaSubscription_ActivityUnpaused;
        }

        private void deltaSubscription_ActivityUnpaused()
        {
            //throw new NotImplementedException();
        }

        private void deltaSubscription_ActivityPaused()
        {
            //throw new NotImplementedException();
        }

        private void deltaSubscription_ConnectionRestored(TimeSpan obj)
        {
            //throw new NotImplementedException();
        }

        private void deltaSubscription_ConnectionClosed()
        {
            if (Status != ePluginStatus.STOPPING &&
                Status != ePluginStatus.STOPPED) //avoid executing this if we are actually trying to disconnect.
                Task.Run(async () =>
                    await HandleConnectionLost("Websocket has been closed from the server (no informed reason)."));
        }

        private void deltaSubscription_ConnectionLost()
        {
            Task.Run(async () =>
                await HandleConnectionLost("Websocket connection has been lost (no informed reason)."));
        }

        private void deltaSubscription_Exception(Exception obj)
        {
            string _error = $"Websocket error: {obj.Message}";
            LogException(obj, _error, true);

            Task.Run(StopAsync);

            Status = ePluginStatus.STOPPED_FAILED;
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED_FAILED));
        }

        #endregion

        private void UpdateOrderBook(KucoinStreamOrderBook lob_update, string symbol, DateTime ts)
        {
            _orderBooksLock.EnterReadLock();

            try
            {
                if (!_localOrderBooks.ContainsKey(symbol))
                    return;
                if (lob_update == null)
                    return;

                var local_lob = _localOrderBooks[symbol];
                if (local_lob == null)
                {
                    // ❌ This should NEVER happen - log and bail out
                    log.Error($"OrderBook for {symbol} is null - snapshot initialization failed. Skipping delta update.");
                    return; // Don't try to recover
                }

                if (lob_update.SequenceStart > local_lob.Sequence + 1)
                {
                    lock (_buffersLock)
                    {
                        if (_eventBuffers.ContainsKey(symbol))
                        {
                            _eventBuffers[symbol]?.PauseConsumer(); // CRITICAL to allow stop
                            _eventBuffers[symbol]?.Clear(); // Clear buffer to avoid processing stale deltas
                        }
                    }
                    throw new Exception("Detected sequence gap.");
                }

                if (lob_update.SequenceStart <= local_lob.Sequence + 1 && lob_update.SequenceEnd > local_lob.Sequence)
                {
                    foreach (var item in lob_update.Changes.Bids)
                    {
                        if (item.Quantity == 0 && item.Price == 0)
                            continue;

                        var delta = DeltaBookItemPool.Get(); // ✅ Get from pool
                        try
                        {
                            delta.MDUpdateAction = item.Quantity == 0 ? eMDUpdateAction.Delete : eMDUpdateAction.None;
                            delta.Price = (double)item.Price;
                            delta.Size = (double)item.Quantity;
                            delta.IsBid = true;
                            delta.LocalTimeStamp = DateTime.Now;
                            delta.ServerTimeStamp = ts;
                            delta.Symbol = symbol;

                            if (item.Quantity == 0)
                                local_lob.DeleteLevel(delta);
                            else
                                local_lob.AddOrUpdateLevel(delta);
                        }
                        finally
                        {
                            DeltaBookItemPool.Return(delta); // ✅ Always return
                        }
                    }

                    foreach (var item in lob_update.Changes.Asks)
                    {
                        if (item.Quantity == 0 && item.Price == 0)
                            continue;
                        var delta = DeltaBookItemPool.Get(); // ✅ Get from pool
                        try
                        {
                            delta.MDUpdateAction = item.Quantity == 0 ? eMDUpdateAction.Delete : eMDUpdateAction.None;
                            delta.Price = (double)item.Price;
                            delta.Size = (double)item.Quantity;
                            delta.IsBid = false;
                            delta.LocalTimeStamp = DateTime.Now;
                            delta.ServerTimeStamp = ts;
                            delta.Symbol = symbol;

                            if (item.Quantity == 0)
                                local_lob.DeleteLevel(delta);
                            else
                                local_lob.AddOrUpdateLevel(delta);
                        }
                        finally
                        {
                            DeltaBookItemPool.Return(delta); // ✅ Always return
                        }
                    }

                    local_lob.Sequence = lob_update.SequenceEnd;
                    local_lob.LastUpdated = ts;
                    RaiseOnDataReceived(local_lob);
                }
            }
            finally
            {
                _orderBooksLock.ExitReadLock();
            }
        }

        private async Task DoPingAsync()
        {
            try
            {
                if (Status == ePluginStatus.STOPPED || Status == ePluginStatus.STOPPING ||
                    Status == ePluginStatus.STOPPED_FAILED)
                    return; //do not ping if any of these statues

                bool isConnected = _socketClient.CurrentConnections > 0;
                if (!isConnected)
                {
                    throw new Exception("The socket seems to be disconnected.");
                }


                DateTime ini = DateTime.Now;
                var result = await _restClient.SpotApi.ExchangeData.GetServerTimeAsync();
                if (result != null)
                {
                    var timeLapseInMicroseconds = DateTime.Now.Subtract(ini).TotalMicroseconds;


                    // Connection is healthy
                    Interlocked.Exchange(ref pingFailedAttempts, 0); // ✅ Atomic Reset the failed attempts on a successful ping

                    RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                }
                else
                {
                    // Consider the ping failed
                    throw new Exception("Ping failed, result was null.");
                }
            }
            catch (Exception ex)
            {

                if (Interlocked.Increment(ref pingFailedAttempts) >= 5) //5 attempts
                {
                    var _error =
                        $"Will reconnect. Unhandled error in DoPingAsync. Initiating reconnection. {ex.Message}";

                    LogException(ex, _error);

                    Task.Run(async () => await HandleConnectionLost(_error, ex));
                }
            }

        }

        private VisualHFT.Model.OrderBook ToOrderBookModel(KucoinOrderBook data, string symbol)
        {
            var identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.Asks.Select(x => x.Price));

            var lob = new VisualHFT.Model.OrderBook(symbol, identifiedPriceDecimalPlaces, _settings.DepthLevels);
            lob.ProviderID = _settings.Provider.ProviderID;
            lob.ProviderName = _settings.Provider.ProviderName;
            lob.SizeDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.Asks.Select(x => x.Quantity));
            lob.Sequence = data.Sequence ?? 0;

            var _asks = new List<VisualHFT.Model.BookItem>();
            var _bids = new List<VisualHFT.Model.BookItem>();
            data.Asks.ToList().ForEach(x =>
            {
                _asks.Add(new VisualHFT.Model.BookItem()
                {
                    IsBid = false,
                    Price = (double)x.Price,
                    Size = (double)x.Quantity,
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = lob.Symbol,
                    PriceDecimalPlaces = lob.PriceDecimalPlaces,
                    SizeDecimalPlaces = lob.SizeDecimalPlaces,
                    ProviderID = lob.ProviderID,
                });
            });
            data.Bids.ToList().ForEach(x =>
            {
                _bids.Add(new VisualHFT.Model.BookItem()
                {
                    IsBid = true,
                    Price = (double)x.Price,
                    Size = (double)x.Quantity,
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = lob.Symbol,
                    PriceDecimalPlaces = lob.PriceDecimalPlaces,
                    SizeDecimalPlaces = lob.SizeDecimalPlaces,
                    ProviderID = lob.ProviderID,
                });
            });
            lob.Sequence = data.Sequence.Value;
            lob.LoadData(
                _asks.OrderBy(x => x.Price).Take(_settings.DepthLevels),
                _bids.OrderByDescending(x => x.Price).Take(_settings.DepthLevels)
            );
            return lob;
        }


        private VisualHFT.Model.Order GetOrCreateUserOrder(KucoinStreamOrderBaseUpdate item)
        {

            lock (_userOrdersLock)
            {
                VisualHFT.Model.Order localuserOrder;
                if (!this._localUserOrders.ContainsKey(item.OrderId))
                {
                    localuserOrder = new VisualHFT.Model.Order();
                    localuserOrder.OrderID = Math.Abs(item.OrderId.ToString().GetHashCode());
                    localuserOrder.ClOrdId = item.OrderId;
                    localuserOrder.CreationTimeStamp = item.OrderTime.Value;

                    localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                    localuserOrder.ProviderName = _settings.Provider.ProviderName;
                    if (item.OriginalQuantity != 0)
                        localuserOrder.Quantity = item.OriginalQuantity.ToDouble();
                    else if (item.OrderType == OrderType.Market && item.OriginalValue != 0)
                    {
                        localuserOrder.Quantity = item.OriginalValue.ToDouble();
                    }
                    if (item.OrderType != OrderType.Market)
                    {
                        localuserOrder.PricePlaced = item.Price.ToDouble();
                    }

                    localuserOrder.Side = item.Side == OrderSide.Buy ? eORDERSIDE.Buy : eORDERSIDE.Sell;
                    localuserOrder.Symbol = GetNormalizedSymbol(item.Symbol);

                    if (item.OrderType == OrderType.Market || item.OrderType == OrderType.MarketStop)
                        localuserOrder.OrderType = eORDERTYPE.MARKET;
                    else
                        localuserOrder.OrderType = eORDERTYPE.LIMIT;
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.GTC; //default


                    this._localUserOrders.Add(item.OrderId, localuserOrder);
                }
                else
                {
                    localuserOrder = this._localUserOrders[item.OrderId];
                }
                if (item.Status == ExtendedOrderStatus.New || item.Status == ExtendedOrderStatus.Open)
                    localuserOrder.Status = eORDERSTATUS.NEW;
                return localuserOrder;
            }
        }


        private async Task UpdateUserOrder(KucoinStreamOrderNewUpdate item)
        {
            VisualHFT.Model.Order localuserOrder = GetOrCreateUserOrder(item);

            localuserOrder.LastUpdated = DateTime.Now;
            RaiseOnDataReceived(localuserOrder);
        }
        private async Task UpdateUserOrder(KucoinStreamOrderUpdate item)
        {
            VisualHFT.Model.Order localuserOrder = GetOrCreateUserOrder(item);
            localuserOrder.FilledQuantity = item.QuantityFilled.ToDouble();
            if (item.UpdateType == MatchUpdateType.Update)
            {
                localuserOrder.Quantity = item.OriginalQuantity.ToDouble();
                localuserOrder.PricePlaced = item.Price.ToDouble();
            }

            localuserOrder.FilledQuantity = item.QuantityFilled.ToDouble();
            localuserOrder.FilledPrice = item.Price.ToDouble();
            if (localuserOrder.FilledQuantity > 0)
                localuserOrder.Status = (localuserOrder.PendingQuantity > 0 ? eORDERSTATUS.PARTIALFILLED : eORDERSTATUS.FILLED);

            if (item.UpdateType == MatchUpdateType.Canceled)
            {
                localuserOrder.Status = eORDERSTATUS.CANCELED;
            }
            // ✅ ADD CLEANUP LOGIC
            if (localuserOrder.Status == eORDERSTATUS.FILLED ||
                localuserOrder.Status == eORDERSTATUS.CANCELED)
            {
                // Schedule removal after brief delay (for potential queries)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    lock (_userOrdersLock)
                    {
                        if (_localUserOrders.ContainsKey(item.OrderId))
                        {
                            _localUserOrders.Remove(item.OrderId);
                            log.Debug($"Cleaned up completed order: {item.OrderId}");
                        }
                    }
                });
            }
            localuserOrder.LastUpdated = DateTime.Now;
            RaiseOnDataReceived(localuserOrder);
        }
        private async Task UpdateUserOrder(KucoinStreamOrderMatchUpdate item)
        {
            VisualHFT.Model.Order localuserOrder = GetOrCreateUserOrder(item);
            localuserOrder.Quantity = item.OriginalQuantity.ToDouble();
            localuserOrder.PricePlaced = item.Price.ToDouble();
            localuserOrder.FilledQuantity = item.QuantityFilled.ToDouble();
            localuserOrder.FilledPrice = item.MatchPrice.ToDouble();
            if (item.UpdateType.HasValue && item.UpdateType == MatchUpdateType.Canceled)
            {
                localuserOrder.Status = eORDERSTATUS.CANCELED;
            }
            else 
            {
                if (localuserOrder.FilledQuantity > 0)
                    localuserOrder.Status = (localuserOrder.PendingQuantity > 0 ? eORDERSTATUS.PARTIALFILLED : eORDERSTATUS.FILLED);
            }
            // ✅ ADD CLEANUP LOGIC
            if (localuserOrder.Status == eORDERSTATUS.FILLED ||
                localuserOrder.Status == eORDERSTATUS.CANCELED)
            {
                // Schedule removal after brief delay (for potential queries)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    lock (_localUserOrders)
                    {
                        if (_localUserOrders.ContainsKey(item.OrderId))
                        {
                            _localUserOrders.Remove(item.OrderId);
                            log.Debug($"Cleaned up completed order: {item.OrderId}");
                        }
                    }
                });
            }
            localuserOrder.LastUpdated = DateTime.Now;
            RaiseOnDataReceived(localuserOrder);

        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // ✅ Synchronous cleanup first
                    _timerPing?.Stop();

                    // ✅ Clean event handlers
                    foreach (var sub in _allSubscriptions)
                    {
                        UnattachEventHandlers(sub?.Data);
                    }

                    // ✅ Async cleanup (block on it)
                    var cleanupTask = Task.Run(async () =>
                    {
                        foreach (var sub in _allSubscriptions)
                        {
                            if (sub?.Data != null)
                                await sub.Data.CloseAsync();
                        }
                        if (_socketClient != null)
                            await _socketClient.UnsubscribeAllAsync();
                    });

                    if (!cleanupTask.Wait(TimeSpan.FromSeconds(5)))
                    {
                        log.Warn("Dispose timeout - resources may leak");
                    }

                    // ✅ Dispose managed resources
                    _timerPing?.Dispose();
                    _socketClient?.Dispose();
                    _restClient?.Dispose();

                    foreach (var q in _eventBuffers.Values)
                    {
                        q?.Stop();
                        q?.Dispose();
                    }
                    _eventBuffers.Clear();

                    foreach (var q in _tradesBuffers.Values)
                    {
                        q?.Stop();
                        q?.Dispose();
                    }
                    _tradesBuffers.Clear();

                    foreach (var lob in _localOrderBooks.Values)
                        lob?.Dispose();
                    _localOrderBooks.Clear();

                    lock (_userOrdersLock)
                        _localUserOrders.Clear(); // ✅ Clear order dictionary
                    _orderBooksLock?.Dispose();

                    base.Dispose(disposing);
                }
                _disposed = true;
            }
        }

        protected override void LoadSettings()
        {
            _settings = LoadFromUserSettings<PlugInSettings>();
            if (_settings == null)
            {
                InitializeDefaultSettings();
            }
            if (_settings.Provider == null) //To prevent back compability with older setting formats
            {
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = 4, ProviderName = "KuCoin" };
            }
            ParseSymbols(string.Join(',', _settings.Symbols.ToArray())); //Utilize normalization function
        }

        protected override void SaveSettings()
        {
            SaveToUserSettings(_settings);
        }

        protected override void InitializeDefaultSettings()
        {
            _settings = new PlugInSettings()
            {
                ApiKey = "",
                ApiSecret = "",
                DepthLevels = 25,
                Provider = new VisualHFT.Model.Provider() { ProviderID = 4, ProviderName = "KuCoin" },
                Symbols = new List<string>() { "BTC-USDT(BTC/USD)" } // Add more symbols as needed
            };
            SaveToUserSettings(_settings);
        }
        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.ApiSecret = _settings.ApiSecret;
            viewModel.ApiKey = _settings.ApiKey;
            viewModel.APIPassPhrase = _settings.APIPassPhrase;
            viewModel.DepthLevels = _settings.DepthLevels;
            viewModel.ProviderId = _settings.Provider.ProviderID;
            viewModel.ProviderName = _settings.Provider.ProviderName;
            viewModel.Symbols = _settings.Symbols;
            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.ApiSecret = viewModel.ApiSecret;
                _settings.ApiKey = viewModel.ApiKey;
                _settings.APIPassPhrase = viewModel.APIPassPhrase;

                _settings.DepthLevels = viewModel.DepthLevels;
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = viewModel.ProviderId, ProviderName = viewModel.ProviderName };
                _settings.Symbols = viewModel.Symbols;
                SaveSettings();
                ParseSymbols(string.Join(',', _settings.Symbols.ToArray()));

                Task.Run(async () =>
                {
                    await StopAsync();  // Full cleanup
                    await StartAsync(); // Fresh start
                });

            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
        }




        //FOR UNIT TESTING PURPOSES
        public void InjectSnapshot(VisualHFT.Model.OrderBook snapshotModel, long sequence)
        {
            var localModel = new KucoinOrderBook(); //transform to KucoinOrderBook
            localModel.Symbol = snapshotModel.Symbol;
            localModel.Asks = snapshotModel.Asks.Select(x => new KucoinOrderBookEntry()
            {
                Price = x.Price.ToDecimal(),
                Quantity = x.Size.ToDecimal()
            }).ToArray();
            localModel.Bids = snapshotModel.Bids.Select(x => new KucoinOrderBookEntry()
            {
                Price = x.Price.ToDecimal(),
                Quantity = x.Size.ToDecimal()
            }).ToArray();
            localModel.Timestamp = DateTime.Now;
            localModel.Sequence = sequence;
            _settings.DepthLevels = snapshotModel.MaxDepth; //force depth received

            var symbol = snapshotModel.Symbol;

            if (!_localOrderBooks.ContainsKey(symbol))
            {
                _localOrderBooks.Add(symbol, ToOrderBookModel(localModel, symbol));
            }
            else
                _localOrderBooks[symbol] = ToOrderBookModel(localModel, symbol);


            RaiseOnDataReceived(_localOrderBooks[symbol]);
        }
        public void InjectDeltaModel(List<DeltaBookItem> bidDeltaModel, List<DeltaBookItem> askDeltaModel)
        {
            var symbol = bidDeltaModel?.FirstOrDefault()?.Symbol;
            if (symbol == null)
                symbol = askDeltaModel?.FirstOrDefault()?.Symbol;
            if (string.IsNullOrEmpty(symbol))
                throw new Exception("Couldn't find the symbol for this model.");
            var ts = DateTime.Now;

            var localModel = new KucoinStreamOrderBook(); //transform to KucoinStreamOrderBook
            localModel.Changes = new KucoinStreamOrderBookChanged()
            {
                Bids = bidDeltaModel?.Select(x => new KucoinStreamOrderBookEntry()
                {
                    Price = x.Price.ToDecimal(),
                    Quantity = x.Size.ToDecimal(),
                    Sequence = x.Sequence
                }).ToArray(),
                Asks = askDeltaModel?.Select(x => new KucoinStreamOrderBookEntry()
                {
                    Price = x.Price.ToDecimal(),
                    Quantity = x.Size.ToDecimal(),
                    Sequence = x.Sequence
                }).ToArray(),
                Timestamp = DateTime.Now
            };
            localModel.SequenceStart = Math.Min(bidDeltaModel.Min(x => x.Sequence), askDeltaModel.Min(x => x.Sequence));
            localModel.SequenceEnd = Math.Max(bidDeltaModel.Max(x => x.Sequence), askDeltaModel.Max(x => x.Sequence));
            UpdateOrderBook(localModel, symbol, ts);
        }

        public List<VisualHFT.Model.Order> ExecutePrivateMessageScenario(eTestingPrivateMessageScenario scenario)
        {

            //depending on the scenario, load its message(s)
            string _file = "";
            if (scenario == eTestingPrivateMessageScenario.SCENARIO_1)
                _file = "PrivateMessages_Scenario1.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_2)
                _file = "PrivateMessages_Scenario2.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_3)
                _file = "PrivateMessages_Scenario3.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_4)
                _file = "PrivateMessages_Scenario4.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_5)
                _file = "PrivateMessages_Scenario5.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_6)
                _file = "PrivateMessages_Scenario6.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_7)
                _file = "PrivateMessages_Scenario7.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_8)
                _file = "PrivateMessages_Scenario8.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_9)
            {
                _file = "PrivateMessages_Scenario9.json";
                throw new Exception("Messages collected for this scenario don't look good.");
            }
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_10)
            {
                _file = "PrivateMessages_Scenario10.json";
                throw new Exception("Messages were not collected for this scenario.");
            }

            string jsonString = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"kucoin_jsonMessages/{_file}"));




            //DESERIALIZE EXCHANGES MODEL
            List<KucoinStreamOrderBaseUpdate> modelList = new List<KucoinStreamOrderBaseUpdate>();
            var jsonArray = JArray.Parse(jsonString);

            foreach (var jsonObject in jsonArray)
            {
                JToken dataToken = jsonObject["data"];
                string dataJsonString = dataToken.ToString();
                var baseUpdate = JsonParser.Parse<KucoinStreamOrderBaseUpdate>(dataJsonString);
                if (baseUpdate.UpdateType == MatchUpdateType.Received || baseUpdate.UpdateType == MatchUpdateType.Open)
                {
                    var item = JsonParser.Parse<KucoinStreamOrderNewUpdate>(dataJsonString);
                    modelList.Add(item);
                }
                else if (baseUpdate.UpdateType == MatchUpdateType.Match)
                {
                    var item = JsonParser.Parse<KucoinStreamOrderMatchUpdate>(dataJsonString);
                    modelList.Add(item);
                }
                else
                {
                    var item = JsonParser.Parse<KucoinStreamOrderUpdate>(dataJsonString);
                    modelList.Add(item);
                }
            }

            //END DESERIALIZE EXCHANGES MODEL



            //UPDATE VISUALHFT CORE & CREATE MODEL TO RETURN
            if (!modelList.Any())
                throw new Exception("No data was found in the json file.");
            foreach (var item in modelList)
            {
                if (item is KucoinStreamOrderNewUpdate)
                    UpdateUserOrder(item as KucoinStreamOrderNewUpdate);
                else if (item is KucoinStreamOrderMatchUpdate)
                    UpdateUserOrder(item as KucoinStreamOrderMatchUpdate);
                else
                    UpdateUserOrder(item as KucoinStreamOrderUpdate);
            }
            //END UPDATE VISUALHFT CORE


            var dicOrders = new Dictionary<string, VisualHFT.Model.Order>(); //we need to use dictionary to identify orders (because exchanges orderId is string)

            foreach (var item in modelList)
            {

                VisualHFT.Model.Order localuserOrder;
                if (!dicOrders.ContainsKey(item.OrderId))
                {
                    localuserOrder = new VisualHFT.Model.Order();
                    localuserOrder.ClOrdId = item.OrderId;
                    localuserOrder.CreationTimeStamp = item.OrderTime.Value;
                    localuserOrder.OrderID = this._localUserOrders[item.OrderId].OrderID; //DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                    localuserOrder.ProviderName = _settings.Provider.ProviderName;
                    if (item.OriginalQuantity != 0)
                        localuserOrder.Quantity = item.OriginalQuantity.ToDouble();
                    else if (item.OrderType == OrderType.Market && item.OriginalValue != 0)
                    {
                        localuserOrder.Quantity = item.OriginalValue.ToDouble();
                    }
                    if (item.OrderType != OrderType.Market)
                    {
                        localuserOrder.PricePlaced = item.Price.ToDouble();
                    }
                    localuserOrder.Symbol = GetNormalizedSymbol(item.Symbol);
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.GTC;
                    localuserOrder.Side = item.Side == OrderSide.Buy ? eORDERSIDE.Buy : eORDERSIDE.Sell;
                    localuserOrder.OrderType = item.OrderType == OrderType.Market ? eORDERTYPE.MARKET : eORDERTYPE.LIMIT;

                    localuserOrder.Status = eORDERSTATUS.NEW;
                    localuserOrder.LastUpdated = DateTime.Now;
                    dicOrders.Add(item.OrderId, localuserOrder);
                }
                else
                {
                    localuserOrder = dicOrders[item.OrderId];
                }
                if (item.UpdateType == MatchUpdateType.Received || item.UpdateType == MatchUpdateType.Open)
                    localuserOrder.Status = eORDERSTATUS.NEW;
                else if (item.UpdateType == MatchUpdateType.Canceled)
                    localuserOrder.Status = eORDERSTATUS.CANCELED;
                else if (item.UpdateType == MatchUpdateType.Update)
                {
                    localuserOrder.Quantity = item.OriginalQuantity.ToDouble();
                    if (item.OrderType != OrderType.Market)
                    {
                        localuserOrder.PricePlaced = item.Price.ToDouble();
                    }
                }
                if (item is KucoinStreamOrderMatchUpdate matchedItem)
                {
                    localuserOrder.FilledPrice = matchedItem.MatchPrice.ToDouble();
                    localuserOrder.FilledQuantity = matchedItem.QuantityFilled.ToDouble();
                    if (localuserOrder.FilledQuantity > 0)
                        localuserOrder.Status = localuserOrder.PendingQuantity == 0 ? eORDERSTATUS.FILLED : eORDERSTATUS.PARTIALFILLED;
                }
                else if (item is KucoinStreamOrderUpdate newUpdate)
                {
                    localuserOrder.FilledPrice = newUpdate.Price.ToDouble();
                    localuserOrder.FilledQuantity = newUpdate.QuantityFilled.ToDouble();
                    if (localuserOrder.FilledQuantity > 0 && localuserOrder.Status != eORDERSTATUS.CANCELED)
                        localuserOrder.Status = localuserOrder.PendingQuantity == 0 ? eORDERSTATUS.FILLED : eORDERSTATUS.PARTIALFILLED;
                }
                //|| item is 

                localuserOrder.LastUpdated = DateTime.Now;
                //END CREATE MODEL TO RETURN
            }

            return dicOrders.Values.ToList();

        }
    }

}
