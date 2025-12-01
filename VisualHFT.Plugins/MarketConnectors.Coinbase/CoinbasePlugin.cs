using VisualHFT.Commons.PluginManager;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
using MarketConnectors.Coinbase.Model;
using MarketConnectors.Coinbase.UserControls;
using MarketConnectors.Coinbase.ViewModel;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.Objects;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Pools;
using Coinbase.Net.Clients;
using Coinbase.Net.Objects.Models;
using Coinbase.Net.Enums;
using Newtonsoft.Json.Linq;
using VisualHFT.Commons.Model;
using Coinbase.Net;
using CryptoExchange.Net.Authentication;
using System.IO;
using System.Collections.Concurrent;

namespace MarketConnectors.Coinbase
{
    public class CoinbasePlugin : BasePluginDataRetriever
    {
        private bool _disposed = false;
        private readonly SemaphoreSlim _startStopLock = new SemaphoreSlim(1, 1);
        private bool isReconnecting = false;

        private PlugInSettings _settings;
        private CoinbaseSocketClient _socketClient;
        private CoinbaseRestClient _restClient;

        // ✅ FIX: Use ConcurrentDictionary for thread safety
        private readonly ConcurrentDictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks =
            new ConcurrentDictionary<string, VisualHFT.Model.OrderBook>();

        private readonly ConcurrentDictionary<string, HelperCustomQueue<Tuple<DateTime, string, CoinbaseOrderBookUpdate>>> _eventBuffers =
            new ConcurrentDictionary<string, HelperCustomQueue<Tuple<DateTime, string, CoinbaseOrderBookUpdate>>>();

        private readonly ConcurrentDictionary<string, HelperCustomQueue<Tuple<string, CoinbaseTrade>>> _tradesBuffers =
            new ConcurrentDictionary<string, HelperCustomQueue<Tuple<string, CoinbaseTrade>>>();

        private readonly ConcurrentDictionary<string, VisualHFT.Model.Order> _localUserOrders =
            new ConcurrentDictionary<string, VisualHFT.Model.Order>();

        private int pingFailedAttempts = 0;
        private System.Timers.Timer _timerPing;
        private CallResult<UpdateSubscription> deltaSubscription;
        private CallResult<UpdateSubscription> tradesSubscription;

        private static readonly log4net.ILog log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly CustomObjectPool<VisualHFT.Model.Trade> tradePool =
            new CustomObjectPool<VisualHFT.Model.Trade>();


        public override string Name { get; set; } = "Coinbase Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Connects to Coinbase websockets.";
        public override string Author { get; set; } = "VisualHFT";

        public override ISetting Settings
        {
            get => _settings;
            set => _settings = (PlugInSettings)value;
        }

        public override Action CloseSettingWindow { get; set; }

        public CoinbasePlugin()
        {
            _socketClient = new CoinbaseSocketClient();
            _restClient = new CoinbaseRestClient();
            SetReconnectionAction(InternalStartAsync);
            log.Info($"{this.Name} has been loaded.");
        }

        ~CoinbasePlugin()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {

            await base.StartAsync(); //call the base first
            _socketClient = new CoinbaseSocketClient(options =>
            {
                if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret))
                {

                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey, _settings.ApiSecret);
                }

                options.Environment = CoinbaseEnvironment.Live;
            });


            _restClient = new CoinbaseRestClient(options =>
            {
                if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret))
                {
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey, _settings.ApiSecret);
                }

                options.Environment = CoinbaseEnvironment.Live;
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
            // ✅ FIX: Add synchronization
            await _startStopLock.WaitAsync();
            try
            {
                await ClearAsync();

                foreach (var symbol in GetAllNormalizedSymbols())
                {
                    _eventBuffers.TryAdd(symbol,
                        new HelperCustomQueue<Tuple<DateTime, string, CoinbaseOrderBookUpdate>>(
                            $"<Tuple<DateTime, string, CoinbaseStreamOrderBookChanged>>_{this.Name.Replace(" Plugin", "")}",
                            eventBuffers_onReadAction, eventBuffers_onErrorAction));
                    _tradesBuffers.TryAdd(symbol,
                        new HelperCustomQueue<Tuple<string, CoinbaseTrade>>(
                            $"<Tuple<DateTime, string, CoinbaseTrade>>_{this.Name.Replace(" Plugin", "")}",
                            tradesBuffers_onReadAction, tradesBuffers_onErrorAction));

                    _eventBuffers[symbol].PauseConsumer();
                }

                await InitializeDeltasAsync();
                await InitializeSnapshotsAsync();
                await InitializeOpenOrders();
                await InitializeTradesAsync();
                await InitializePingTimerAsync();
                await InitializeUserPrivateOrders();
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        public override async Task StopAsync()
        {
            // ✅ FIX: Add synchronization
            await _startStopLock.WaitAsync();
            try
            {
                Status = ePluginStatus.STOPPING;
                log.Info($"{this.Name} is stopping.");

                await ClearAsync();
                RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));

                await base.StopAsync();
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        public async Task ClearAsync()
        {
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

            // ✅ FIX: Dispose queues properly
            foreach (var q in _eventBuffers)
            {
                try
                {
                    q.Value?.Stop();
                    q.Value?.Dispose();
                }
                catch (Exception ex)
                {
                    log.Debug($"Error disposing event buffer: {ex.Message}");
                }
            }
            _eventBuffers.Clear();

            foreach (var q in _tradesBuffers)
            {
                try
                {
                    q.Value?.Stop();
                    q.Value?.Dispose();
                }
                catch (Exception ex)
                {
                    log.Debug($"Error disposing trades buffer: {ex.Message}");
                }
            }
            _tradesBuffers.Clear();

            // ✅ FIX: Safe OrderBook disposal with snapshot
            if (_localOrderBooks != null)
            {
                var orderBooksToDispose = _localOrderBooks.Values.ToArray();
                foreach (var lob in orderBooksToDispose)
                {
                    try
                    {
                        lob?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        log.Debug($"Error disposing order book: {ex.Message}");
                    }
                }
                _localOrderBooks.Clear();
            }
        }

        private async Task InitializeTradesAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var _normalizedSymbol = GetNormalizedSymbol(symbol);
                var _traderQueueRef = _tradesBuffers[_normalizedSymbol];

                log.Info($"{this.Name}: sending WS Trades Subscription {_normalizedSymbol} ");
                tradesSubscription = await _socketClient.AdvancedTradeApi.SubscribeToTradeUpdatesAsync(
                    symbol,
                    trade =>
                    {
                        // Buffer the trades
                        if (trade.Data != null)
                        {
                            try
                            {
                                foreach (var item in trade.Data)
                                {
                                    item.Timestamp = trade.ReceiveTime;
                                    _traderQueueRef.Add(
                                        new Tuple<string, CoinbaseTrade>(_normalizedSymbol, item));
                                }
                            }
                            catch (Exception ex)
                            {
                                var _error = $"Will reconnect. Unhandled error while receiving trading data for {_normalizedSymbol}.";
                                LogException(ex, _error);
                                Task.Run(async () => await HandleConnectionLost(_error, ex));
                            }
                        }
                    });
                if (tradesSubscription.Success)
                {
                    AttachEventHandlers(tradesSubscription.Data);
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

        }

        private async Task InitializeUserPrivateOrders()
        {
            if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret))
            {
                await _socketClient.AdvancedTradeApi.SubscribeToUserUpdatesAsync(async neworder => { await UpdateUserOrder(neworder.Data); });
            }
        }

        private async Task InitializeDeltasAsync()
        {

            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);
                log.Info($"{this.Name}: sending WS Delta Subscription {normalizedSymbol} ");
                deltaSubscription = await _socketClient.AdvancedTradeApi.SubscribeToOrderBookUpdatesAsync(
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

                                    _eventBuffers[normalizedSymbol].Add(
                                        new Tuple<DateTime, string, CoinbaseOrderBookUpdate>(
                                            data.ReceiveTime.ToLocalTime(), normalizedSymbol, data.Data));
                                }
                            }
                            catch (Exception ex)
                            {
                                string _normalizedSymbol = "(null)";
                                if (data != null && data.Data != null)
                                    _normalizedSymbol = GetNormalizedSymbol(data.Symbol);


                                var _error =
                                    $"Will reconnect. Unhandled error while receiving delta market data for {_normalizedSymbol}.";
                                LogException(ex, _error);
                                Task.Run(async () => await HandleConnectionLost(_error, ex));
                            }
                        }
                    }, new CancellationToken());
                if (deltaSubscription.Success)
                {
                    AttachEventHandlers(deltaSubscription.Data);
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
                    // ✅ FIX: Use TryAdd instead of ContainsKey + Add
                    _localOrderBooks.TryAdd(normalizedSymbol, null);

                    log.Info($"{this.Name}: Getting snapshot {normalizedSymbol} level 2");

                    // Fetch initial depth snapshot 
                    var depthSnapshot =
                        await _restClient.AdvancedTradeApi.ExchangeData.GetOrderBookAsync(symbol,
                                20);
                    if (depthSnapshot.Success)
                    {
                        _localOrderBooks[normalizedSymbol] = ToOrderBookModel(depthSnapshot.Data, normalizedSymbol);
                        log.Info($"{this.Name}: LOB {normalizedSymbol} level 2 Successfully loaded.");
                        _eventBuffers[normalizedSymbol].ResumeConsumer();
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
                LogException(ex, "InitializeSnapshotsAsync");
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

        private void eventBuffers_onReadAction(Tuple<DateTime, string, CoinbaseOrderBookUpdate> eventData)
        {
            UpdateOrderBook(eventData.Item3, eventData.Item2, eventData.Item1);
        }

        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";

            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }

        private void tradesBuffers_onReadAction(Tuple<string, CoinbaseTrade> item)
        {
            var trade = tradePool.Get();
            trade.Price = item.Item2.Price;
            trade.Size = Math.Abs(item.Item2.Quantity);
            trade.Symbol = item.Item1;
            trade.Timestamp = item.Item2.Timestamp.ToLocalTime();
            trade.ProviderId = _settings.Provider.ProviderID;
            trade.ProviderName = _settings.Provider.ProviderName;
            trade.IsBuy = item.Item2.OrderSide == OrderSide.Buy;
            trade.MarketMidPrice = _localOrderBooks[item.Item1] == null ? 0 : _localOrderBooks[item.Item1].MidPrice;

            RaiseOnDataReceived(trade);
            tradePool.Return(trade);
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

            if (!isReconnecting)
            {
                isReconnecting = true;
                Task.Run(async () =>
                {
                    await HandleConnectionLost(_error, obj);
                    isReconnecting = false;
                });
            }
        }

        #endregion

        // ✅ FIX: Thread-safe UpdateOrderBook
        private void UpdateOrderBook(CoinbaseOrderBookUpdate lob_update, string symbol, DateTime ts)
        {
            if (lob_update == null)
                return;

            // ✅ Use TryGetValue for thread safety
            if (!_localOrderBooks.TryGetValue(symbol, out var local_lob))
                return;

            if (local_lob == null)
            {
                log.Warn($"OrderBook for {symbol} is null, skipping update");
                return;
            }

            // ✅ Cache DateTime.Now once
            var now = DateTime.Now;

            foreach (var item in lob_update.Bids)
            {
                if (item.Quantity != 0 && item.Price > 0)
                {
                    local_lob.AddOrUpdateLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.None,
                        Price = (double)item.Price,
                        Size = (double)item.Quantity,
                        IsBid = true,
                        LocalTimeStamp = now,
                        ServerTimeStamp = ts,
                        Symbol = symbol
                    });
                }
                else if (item.Quantity == 0 && item.Price > 0)
                {
                    local_lob.DeleteLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.Delete,
                        Price = (double)item.Price,
                        IsBid = true,
                        LocalTimeStamp = now,
                        ServerTimeStamp = ts,
                        Symbol = symbol
                    });
                }
            }

            foreach (var item in lob_update.Asks)
            {
                if (item.Quantity != 0 && item.Price > 0)
                {
                    local_lob.AddOrUpdateLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.None,
                        Price = (double)item.Price,
                        Size = (double)item.Quantity,
                        IsBid = false,
                        LocalTimeStamp = now,
                        ServerTimeStamp = ts,
                        Symbol = symbol
                    });
                }
                else if (item.Quantity == 0 && item.Price > 0)
                {
                    local_lob.DeleteLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.Delete,
                        Price = (double)item.Price,
                        IsBid = false,
                        LocalTimeStamp = now,
                        ServerTimeStamp = ts,
                        Symbol = symbol
                    });
                }
            }
            local_lob.LastUpdated = ts;
            RaiseOnDataReceived(local_lob);
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
                var result = await _restClient.AdvancedTradeApi.ExchangeData.GetServerTimeAsync();
                if (result != null)
                {
                    var timeLapseInMicroseconds = DateTime.Now.Subtract(ini).TotalMicroseconds;


                    // Connection is healthy
                    pingFailedAttempts = 0; // Reset the failed attempts on a successful ping

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

                if (++pingFailedAttempts >= 5) //5 attempts
                {
                    var _error =
                        $"Will reconnect. Unhandled error in DoPingAsync. Initiating reconnection. {ex.Message}";

                    LogException(ex, _error);

                    Task.Run(async () => await HandleConnectionLost(_error, ex));
                }
            }

        }

        private VisualHFT.Model.OrderBook ToOrderBookModel(CoinbaseOrderBook data, string symbol)
        {
            var identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.Asks.Select(x => x.Price));

            var lob = new VisualHFT.Model.OrderBook(symbol, identifiedPriceDecimalPlaces, _settings.DepthLevels);
            lob.ProviderID = _settings.Provider.ProviderID;
            lob.ProviderName = _settings.Provider.ProviderName;
            lob.SizeDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.Asks.Select(x => x.Quantity));

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

            lob.LoadData(
                _asks.OrderBy(x => x.Price).Take(_settings.DepthLevels),
                _bids.OrderByDescending(x => x.Price).Take(_settings.DepthLevels)
            );
            return lob;
        }


        private List<VisualHFT.Model.Order> GetOrCreateUserOrder(CoinbaseUserUpdate order)
        {
            List<VisualHFT.Model.Order> lstlocaluserOrder = new List<VisualHFT.Model.Order>();
            foreach (var item in order.Orders)
            {
                var localuserOrder = _localUserOrders.GetOrAdd(item.OrderId, _ =>
                {
                    var newOrder = new VisualHFT.Model.Order
                    {
                        OrderID = Math.Abs(item.OrderId.ToString().GetHashCode()),
                        ClOrdId = item.OrderId,
                        CreationTimeStamp = item.CreateTime,
                        ProviderId = _settings!.Provider.ProviderID,
                        ProviderName = _settings.Provider.ProviderName,
                        Symbol = GetNormalizedSymbol(item.Symbol),
                        Side = item.OrderSide == OrderSide.Buy ? eORDERSIDE.Buy : eORDERSIDE.Sell,
                        OrderType = (item.OrderType == OrderType.Market || item.OrderType == OrderType.Stop)
                            ? eORDERTYPE.MARKET
                            : eORDERTYPE.LIMIT,
                        TimeInForce = eORDERTIMEINFORCE.GTC
                    };

                    if (item.QuantityFilled + item.QuantityRemaining != 0)
                        newOrder.Quantity = (item.QuantityFilled + item.QuantityRemaining).ToDouble();

                    if (item.OrderType != OrderType.Market)
                        newOrder.PricePlaced = item.Price.ToDouble();

                    return newOrder;
                });

                // Update status
                if (item.Status == OrderStatus.Queued || item.Status == OrderStatus.Open || item.Status == OrderStatus.Pending)
                    localuserOrder.Status = eORDERSTATUS.NEW;
                else if (item.Status == OrderStatus.Canceled || item.Status == OrderStatus.Expired)
                    localuserOrder.Status = eORDERSTATUS.CANCELED;
                else if (item.Status == OrderStatus.Filled && item.QuantityRemaining <= 0)
                    localuserOrder.Status = eORDERSTATUS.FILLED;
                else if (item.QuantityRemaining > 0)
                    localuserOrder.Status = eORDERSTATUS.PARTIALFILLED;

                lstlocaluserOrder.Add(localuserOrder);
            }
            return lstlocaluserOrder;
        }


        private async Task UpdateUserOrder(CoinbaseUserUpdate order)
        {
            var lstOrders = GetOrCreateUserOrder(order);
            foreach (var item in lstOrders)
            {
                item.LastUpdated = DateTime.Now;
                RaiseOnDataReceived(item);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    UnattachEventHandlers(deltaSubscription?.Data);
                    UnattachEventHandlers(tradesSubscription?.Data);
                    _socketClient?.UnsubscribeAllAsync();
                    _socketClient?.Dispose();
                    _restClient?.Dispose();
                    _timerPing?.Dispose();
                    _startStopLock?.Dispose();

                    foreach (var q in _eventBuffers)
                        q.Value?.Dispose();
                    _eventBuffers.Clear();

                    foreach (var q in _tradesBuffers)
                        q.Value?.Dispose();
                    _tradesBuffers.Clear();

                    if (_localOrderBooks != null)
                    {
                        var orderBooksToDispose = _localOrderBooks.Values.ToArray();
                        foreach (var lob in orderBooksToDispose)
                        {
                            lob?.Dispose();
                        }
                        _localOrderBooks.Clear();
                    }

                    base.Dispose();
                }
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
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = 7, ProviderName = "Coinbase" };
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
                Provider = new VisualHFT.Model.Provider() { ProviderID = 7, ProviderName = "Coinbase" },
                Symbols = new List<string>() { "BTC-USD(BTC/USD)", "ETH-USD(ETH/USD)" } // Add more symbols as needed
            };
            SaveToUserSettings(_settings);
        }
        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.ApiSecret = _settings.ApiSecret;
            viewModel.ApiKey = _settings.ApiKey;
            viewModel.DepthLevels = _settings.DepthLevels;
            viewModel.ProviderId = _settings.Provider.ProviderID;
            viewModel.ProviderName = _settings.Provider.ProviderName;
            viewModel.Symbols = _settings.Symbols;
            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.ApiSecret = viewModel.ApiSecret;
                _settings.ApiKey = viewModel.ApiKey;
                _settings.DepthLevels = viewModel.DepthLevels;
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = viewModel.ProviderId, ProviderName = viewModel.ProviderName };
                _settings.Symbols = viewModel.Symbols;
                SaveSettings();
                ParseSymbols(string.Join(',', _settings.Symbols.ToArray()));

                //run this because it will allow to reconnect with the new values
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTING));
                Status = ePluginStatus.STARTING;
                Task.Run(async () => await HandleConnectionLost($"{this.Name} is starting (from reloading settings).", null, true));


            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
        }




        //FOR UNIT TESTING PURPOSES
        public void InjectSnapshot(VisualHFT.Model.OrderBook snapshotModel, long sequence)
        {
            var localModel = new CoinbaseOrderBook(); //transform to CoinbaseOrderBook
            localModel.Symbol = snapshotModel.Symbol;
            localModel.Asks = snapshotModel.Asks.Select(x => new CoinbaseOrderBookEntry()
            {
                Price = x.Price.ToDecimal(),
                Quantity = x.Size.ToDecimal()
            }).ToArray();

            localModel.Bids = snapshotModel.Bids.Select(x => new CoinbaseOrderBookEntry()
            {
                Price = x.Price.ToDecimal(),
                Quantity = x.Size.ToDecimal()
            }).ToArray();
            localModel.Time = DateTime.Now;

            _settings.DepthLevels = snapshotModel.MaxDepth; //force depth received

            var symbol = snapshotModel.Symbol;

            // ✅ FIX: Use AddOrUpdate for ConcurrentDictionary
            _localOrderBooks.AddOrUpdate(
                symbol,
                ToOrderBookModel(localModel, symbol),
                (key, oldValue) => ToOrderBookModel(localModel, symbol)
            );

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

            var localModel = new CoinbaseOrderBookUpdate();
            localModel.Bids = bidDeltaModel?.Select(x => new CoinbaseOrderBookUpdateEntry()
            {
                Price = x.Price.ToDecimal(),
                Quantity = x.Size.ToDecimal()
            }).ToArray();

            localModel.Asks = askDeltaModel?.Select(x => new CoinbaseOrderBookUpdateEntry()
            {
                Price = x.Price.ToDecimal(),
                Quantity = x.Size.ToDecimal()
            }).ToArray();

            UpdateOrderBook(localModel, symbol, ts);
        }

        public List<VisualHFT.Model.Order> ExecutePrivateMessageScenario(eTestingPrivateMessageScenario scenario)
        {
            string _file = scenario switch
            {
                eTestingPrivateMessageScenario.SCENARIO_1 => "PrivateMessages_Scenario1.json",
                eTestingPrivateMessageScenario.SCENARIO_2 => "PrivateMessages_Scenario2.json",
                eTestingPrivateMessageScenario.SCENARIO_3 => "PrivateMessages_Scenario3.json",
                eTestingPrivateMessageScenario.SCENARIO_4 => "PrivateMessages_Scenario4.json",
                eTestingPrivateMessageScenario.SCENARIO_5 => "PrivateMessages_Scenario5.json",
                eTestingPrivateMessageScenario.SCENARIO_6 => "PrivateMessages_Scenario6.json",
                eTestingPrivateMessageScenario.SCENARIO_7 => "PrivateMessages_Scenario7.json",
                eTestingPrivateMessageScenario.SCENARIO_8 => "PrivateMessages_Scenario8.json",
                eTestingPrivateMessageScenario.SCENARIO_9 => throw new Exception("Messages collected for this scenario don't look good."),
                eTestingPrivateMessageScenario.SCENARIO_10 => throw new Exception("Messages were not collected for this scenario."),
                _ => throw new ArgumentException($"Unknown scenario: {scenario}")
            };

            string jsonString = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"coinbase_jsonMessages/{_file}"));

            var jsonArray = JArray.Parse(jsonString);

            // Clear previous orders
            _localUserOrders.Clear();

            // Process all order updates
            foreach (var jsonObject in jsonArray)
            {
                var orderUpdate = jsonObject.ToObject<CoinbaseUserUpdate>();
                if (orderUpdate != null)
                {
                    var orders = GetOrCreateUserOrder(orderUpdate);
                    foreach (var order in orders)
                    {
                        order.LastUpdated = DateTime.Now;
                        RaiseOnDataReceived(order);
                    }
                }
            }

            return _localUserOrders.Values.ToList();
        }
    }
}