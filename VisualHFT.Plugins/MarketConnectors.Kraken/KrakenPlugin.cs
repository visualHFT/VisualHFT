using Kraken.Net;
using Kraken.Net.Clients;
using Kraken.Net.Objects.Models;
using Kraken.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using MarketConnectors.Kraken.Model;
using MarketConnectors.Kraken.UserControls;
using MarketConnectors.Kraken.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Commons.PluginManager;
using VisualHFT.UserSettings;
using VisualHFT.Commons.Pools;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.Helpers;
using CryptoExchange.Net.Objects.Sockets;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using Kraken.Net.Objects.Models.Socket;
using VisualHFT.Commons.Interfaces;
using Newtonsoft.Json.Linq;
using System.IO;
using VisualHFT.Model;

namespace MarketConnectors.Kraken
{
    public class KrakenPlugin : BasePluginDataRetriever, IDataRetrieverTestable
    {
        private bool _disposed = false; // to track whether the object has been disposed

        private PlugInSettings _settings;
        private KrakenSocketClient _socketClient;
        private KrakenRestClient _restClient;
        private Dictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks = new Dictionary<string, VisualHFT.Model.OrderBook>();
        private Dictionary<string, HelperCustomQueue<Tuple<DateTime, string, KrakenBookUpdate>>> _eventBuffers = new();
        private Dictionary<string, HelperCustomQueue<Tuple<string, KrakenTradeUpdate>>> _tradesBuffers = new();
        private readonly object _buffersLock = new object(); // ✅ ADD: Thread-safe buffer access

        private int pingFailedAttempts = 0;
        private System.Timers.Timer _timerPing;
        private Dictionary<string, CallResult<UpdateSubscription>> deltaSubscriptions = new(); // ✅ FIX: Store per-symbol
        private Dictionary<string, CallResult<UpdateSubscription>> tradesSubscriptions = new(); // ✅ FIX: Store per-symbol

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private Dictionary<string, VisualHFT.Model.Order> _localUserOrders = new Dictionary<string, VisualHFT.Model.Order>();

        private CustomObjectPool<VisualHFT.Model.Trade> tradePool = new CustomObjectPool<VisualHFT.Model.Trade>();//pool of Trade objects


        public override string Name { get; set; } = "Kraken Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Connects to Kraken websockets.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }

        public KrakenPlugin()
        {
            SetReconnectionAction(InternalStartAsync);
            log.Info($"{this.Name} has been loaded.");
        }
        ~KrakenPlugin()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {

            await base.StartAsync();//call the base first
            _socketClient = new KrakenSocketClient(options =>
            {

                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey, _settings.ApiSecret);
                options.Environment = KrakenEnvironment.Live;
            });

            _restClient = new KrakenRestClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey, _settings.ApiSecret);
                options.Environment = KrakenEnvironment.Live;
            });


            try
            {
                await InternalStartAsync();
                if (Status == ePluginStatus.STOPPED_FAILED) //check again here for failure
                    return;
                // ✅ Status is now set in InternalStartAsync - no need to duplicate here
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

            // Initialize event buffer for each symbol
            foreach (var symbol in GetAllNormalizedSymbols())
            {
                _eventBuffers.Add(symbol, new HelperCustomQueue<Tuple<DateTime, string, KrakenBookUpdate>>($"<Tuple<DateTime, string, KrakenOrderBookEntry>>_{this.Name.Replace(" Plugin", "")}", eventBuffers_onReadAction, eventBuffers_onErrorAction));
                _tradesBuffers.Add(symbol, new HelperCustomQueue<Tuple<string, KrakenTradeUpdate>>($"<Tuple<DateTime, string, KrakenOrderBookEntry>>_{this.Name.Replace(" Plugin", "")}", tradesBuffers_onReadAction, tradesBuffers_onErrorAction));
            }

            await InitializeSnapshotsAsync();
            await InitializeTradesAsync();
            await InitializeDeltasAsync();
            await InitializePingTimerAsync();
            await InitializeUserPrivateOrders();

            // ✅ FIX: Set status to STARTED so tests can detect status transitions
            log.Info($"Plugin has successfully started.");
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
            Status = ePluginStatus.STARTED;
        }
        public override async Task StopAsync()
        {
            // ✅ FIX: Set status FIRST to prevent reconnection race condition
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");

            // ✅ FIX: Force cancel any pending reconnections by closing subscriptions first
            foreach (var sub in deltaSubscriptions.Values)
            {
                UnattachEventHandlers(sub?.Data);
                if (sub != null && sub.Data != null)
                    await sub.Data.CloseAsync();
            }
            foreach (var sub in tradesSubscriptions.Values)
            {
                UnattachEventHandlers(sub?.Data);
                if (sub != null && sub.Data != null)
                    await sub.Data.CloseAsync();
            }

            await ClearAsync();
            RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));

            await base.StopAsync();
        }

        private async Task ClearAsync()
        {
            // ✅ Subscription cleanup is done in StopAsync to prevent reconnection races
            // Only unsubscribe here if not already done
            if (_socketClient != null)
                await _socketClient.UnsubscribeAllAsync();
                
            _timerPing?.Stop();
            _timerPing?.Dispose();

            // ✅ FIX: Pause queues before stopping with thread-safe lock
            lock (_buffersLock)
            {
                foreach (var q in _eventBuffers.Values)
                {
                    q?.PauseConsumer();
                    q?.Stop();
                }
                _eventBuffers.Clear();

                foreach (var q in _tradesBuffers.Values)
                {
                    q?.PauseConsumer();
                    q?.Stop();
                }
                _tradesBuffers.Clear();
            }

            deltaSubscriptions.Clear();
            tradesSubscriptions.Clear();

            tradePool.Dispose();
            tradePool = new CustomObjectPool<Trade>();

            //CLEAR LOB
            if (_localOrderBooks != null)
            {
                foreach (var lob in _localOrderBooks)
                {
                    lob.Value?.Dispose();
                }
                _localOrderBooks.Clear();
            }
        }

        private async Task InitializeTradesAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var _normalizedSymbol = GetNormalizedSymbol(symbol);

                log.Info($"{this.Name}: sending WS Trades Subscription {_normalizedSymbol} ");
                var tradesSubscription = await _socketClient.SpotApi.SubscribeToTradeUpdatesAsync(
                    symbol,
                    trade =>
                    {
                        // Buffer the trades
                        if (trade.Data != null)
                        {
                            try
                            {
                                // ✅ FIX: Thread-safe buffer access (don't capture reference)
                                HelperCustomQueue<Tuple<string, KrakenTradeUpdate>> buffer;
                                lock (_buffersLock)
                                {
                                    if (!_tradesBuffers.TryGetValue(_normalizedSymbol, out buffer))
                                        return; // Buffer was cleared during reconnection
                                }
                                
                                foreach (var item in trade.Data)
                                {
                                    item.Timestamp = trade.ReceiveTime; 
                                    buffer.Add(new Tuple<string, KrakenTradeUpdate>(_normalizedSymbol, item));
                                }
                            }
                            catch (Exception ex)
                            {
                                var _error = $"Will reconnect. Unhandled error while receiving trading data for {_normalizedSymbol}.";
                                LogException(ex, _error);
                                
                                // ✅ FIX: Pause queue before reconnecting
                                lock (_buffersLock)
                                {
                                    if (_tradesBuffers.TryGetValue(_normalizedSymbol, out var buffer))
                                    {
                                        buffer?.PauseConsumer();
                                    }
                                }
                                
                                Task.Run(async () => await HandleConnectionLost(_error, ex));
                            }
                        }
                    });
                if (tradesSubscription.Success)
                {
                    AttachEventHandlers(tradesSubscription.Data);
                    tradesSubscriptions[_normalizedSymbol] = tradesSubscription; // ✅ FIX: Store by symbol
                }
                else
                {
                    var _error = $"Unsuccessful trades subscription for {_normalizedSymbol} error: {tradesSubscription.Error}";
                    throw new Exception(_error);
                }
            }
        }
        private async Task InitializeUserPrivateOrders()
        {
            if (!string.IsNullOrEmpty(this._settings.ApiKey) && !string.IsNullOrEmpty(this._settings.ApiSecret))
            {
                await _socketClient.SpotApi.SubscribeToOrderUpdatesAsync(async neworder =>
                {
                    log.Info(neworder.Data);
                    if (neworder.Data != null)
                    {
                        IEnumerable<KrakenOrderUpdate> item = neworder.Data;

                        foreach (var order in item)
                        {
                           await UpdateUserOrder(order);
                        }
                    }
                }, true, true);
            }
        }
        private async Task UpdateUserOrder(KrakenOrderUpdate item)
        {
            VisualHFT.Model.Order localuserOrder = null;
            if (!this._localUserOrders.ContainsKey(item.OrderId) && (item.OrderStatus == OrderStatusUpdate.New || item.OrderStatus == OrderStatusUpdate.Pending))
            {
                localuserOrder = new VisualHFT.Model.Order();
                localuserOrder.Currency = GetNormalizedSymbol(item.Symbol);
                localuserOrder.CreationTimeStamp = item.Timestamp;

                localuserOrder.OrderID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                localuserOrder.ProviderName = _settings.Provider.ProviderName;
                if (item.OrderQuantity.HasValue) //when orderType=MARKET does not inform qty
                    localuserOrder.Quantity = (double)item.OrderQuantity;
                localuserOrder.PricePlaced = (double)item.LimitPrice;
                localuserOrder.Symbol = GetNormalizedSymbol(item.Symbol);
                localuserOrder.TimeInForce = item.TimeInForce == TimeInForce.IOC? eORDERTIMEINFORCE.IOC : eORDERTIMEINFORCE.GTC;
                localuserOrder.OrderType = item.OrderType == OrderType.Market ? eORDERTYPE.MARKET : eORDERTYPE.LIMIT;
                localuserOrder.Side = item.OrderSide == OrderSide.Buy ? eORDERSIDE.Buy : eORDERSIDE.Sell;

                localuserOrder.Status = eORDERSTATUS.NEW;
                this._localUserOrders.Add(item.OrderId, localuserOrder);
            }
            else if (this._localUserOrders.ContainsKey(item.OrderId))
            {
                localuserOrder = this._localUserOrders[item.OrderId];
            }


            if (localuserOrder != null)
            {
                if (item.OrderStatus == OrderStatusUpdate.New)
                {
                    localuserOrder.Status = eORDERSTATUS.NEW;
                    if (item.OrderEventType == OrderEventType.Amended) //the order was amended
                    {
                        localuserOrder.PricePlaced = item.LimitPrice.ToDouble();
                        if (item.OrderQuantity.HasValue)
                            localuserOrder.Quantity = item.OrderQuantity.ToDouble();
                        localuserOrder.LastUpdated = item.Timestamp;
                        if (item.QuantityFilled.HasValue)
                            localuserOrder.FilledQuantity = item.QuantityFilled.ToDouble();
                        if (item.AveragePrice.HasValue)
                            localuserOrder.FilledPrice = item.AveragePrice.ToDouble();
                    }

                }
                else if (item.OrderStatus == OrderStatusUpdate.Expired || item.OrderStatus == OrderStatusUpdate.Canceled)
                {
                    //update needed fields only
                    localuserOrder.LastUpdated = item.Timestamp;
                    if (item.QuantityFilled.HasValue)
                        localuserOrder.FilledQuantity = item.QuantityFilled.ToDouble();
                    if (item.AveragePrice.HasValue)
                        localuserOrder.FilledPrice = item.AveragePrice.ToDouble();

                    localuserOrder.Status = eORDERSTATUS.CANCELED;

                }
                else if (item.OrderStatus == OrderStatusUpdate.Filled || item.OrderStatus == OrderStatusUpdate.PartiallyFilled)
                {
                    //update needed fields only
                    if (item.QuantityFilled.HasValue)
                        localuserOrder.FilledQuantity = item.QuantityFilled.ToDouble();
                    if (item.AveragePrice.HasValue)
                        localuserOrder.FilledPrice = item.AveragePrice.ToDouble();
                    localuserOrder.Status = (item.OrderStatus == OrderStatusUpdate.PartiallyFilled ? eORDERSTATUS.PARTIALFILLED : eORDERSTATUS.FILLED);
                    if (localuserOrder.OrderType == eORDERTYPE.MARKET) //update original order and price placed
                    {
                        localuserOrder.PricePlaced = localuserOrder.FilledPrice;
                        localuserOrder.Quantity = localuserOrder.FilledQuantity;
                    }

                }

                localuserOrder.LastUpdated = DateTime.Now;
                RaiseOnDataReceived(localuserOrder);
            }
        }
        private async Task InitializeDeltasAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);
                log.Info($"{this.Name}: sending WS Delta Subscription {normalizedSymbol} ");

                var deltaSubscription = await _socketClient.SpotApi.SubscribeToAggregatedOrderBookUpdatesAsync(
                    symbol,
                    _settings.DepthLevels,
                    data =>
                    {
                        // Buffer the events
                        if (data.Data != null)
                        {
                            try
                            {
                                if (data.UpdateType == SocketUpdateType.Update)
                                {
                                    if (Math.Abs(DateTime.Now.Subtract(data.ReceiveTime.ToLocalTime()).TotalSeconds) > 1)
                                    {
                                        var _msg = $"Rates are coming late at {Math.Abs(DateTime.Now.Subtract(data.ReceiveTime.ToLocalTime()).TotalSeconds)} seconds.";
                                        log.Warn(_msg);
                                        HelperNotificationManager.Instance.AddNotification(this.Name, _msg, HelprNorificationManagerTypes.WARNING, HelprNorificationManagerCategories.PLUGINS);
                                    }

                                    // ✅ FIX: Thread-safe buffer access
                                    HelperCustomQueue<Tuple<DateTime, string, KrakenBookUpdate>> buffer;
                                    lock (_buffersLock)
                                    {
                                        if (!_eventBuffers.TryGetValue(normalizedSymbol, out buffer))
                                            return; // Buffer was cleared during reconnection
                                    }
                                    
                                    buffer.Add(new Tuple<DateTime, string, KrakenBookUpdate>(
                                        data.ReceiveTime.ToLocalTime(), normalizedSymbol, data.Data));
                                }
                                else
                                {
                                    UpdateOrderBookSnapshot(data.Data, normalizedSymbol);
                                }
                            }
                            catch (Exception ex)
                            {
                                string _normalizedSymbol = "(null)";
                                if (data != null && data.Data != null)
                                    _normalizedSymbol = GetNormalizedSymbol(data.Data.Symbol);

                                var _error = $"Will reconnect. Unhandled error while receiving delta market data for {_normalizedSymbol}.";
                                LogException(ex, _error);
                                
                                // ✅ FIX: Pause queue before reconnecting
                                lock (_buffersLock)
                                {
                                    if (_eventBuffers.TryGetValue(normalizedSymbol, out var buffer))
                                    {
                                        buffer?.PauseConsumer();
                                    }
                                }
                                
                                Task.Run(async () => await HandleConnectionLost(_error, ex));
                            }
                        }
                    }, null, CancellationToken.None);
                if (deltaSubscription.Success)
                {
                    AttachEventHandlers(deltaSubscription.Data);
                    deltaSubscriptions[normalizedSymbol] = deltaSubscription; // ✅ FIX: Store by symbol
                }
                else
                {
                    var _error = $"Unsuccessful deltas subscription for {normalizedSymbol} error: {deltaSubscription.Error}";
                    throw new Exception(_error);
                }
            }
        }
        private async Task InitializeSnapshotsAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);
                if (!_localOrderBooks.ContainsKey(normalizedSymbol))
                {
                    _localOrderBooks.Add(normalizedSymbol, null);
                }
                log.Info($"{this.Name}: Getting snapshot {normalizedSymbol} level 2");

                // Fetch initial depth snapshot
                var depthSnapshot = await _restClient.SpotApi.ExchangeData.GetOrderBookAsync(symbol, _settings.DepthLevels);
                if (depthSnapshot.Success)
                {
                    _localOrderBooks[normalizedSymbol] = ToOrderBookModel(depthSnapshot.Data, normalizedSymbol);
                    log.Info($"{this.Name}: LOB {normalizedSymbol} level 2 Successfully loaded.");
                }
                else
                {
                    var _error = $"Unsuccessful snapshot request for {normalizedSymbol} error: {depthSnapshot.ResponseStatusCode} - {depthSnapshot.Error}";
                    throw new Exception(_error);
                }
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

        private void eventBuffers_onReadAction(Tuple<DateTime, string, KrakenBookUpdate> eventData)
        {
            UpdateOrderBook(eventData.Item3, eventData.Item2, eventData.Item1);
        }
        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";

            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }
        private void tradesBuffers_onReadAction(Tuple<string, KrakenTradeUpdate> item)
        {
            var trade = tradePool.Get();
            trade.Price = item.Item2.Price;
            trade.Size = Math.Abs(item.Item2.Quantity);
            trade.Symbol = item.Item1;
            trade.Timestamp = item.Item2.Timestamp.ToLocalTime();
            trade.ProviderId = _settings.Provider.ProviderID;
            trade.ProviderName = _settings.Provider.ProviderName;
            trade.IsBuy = item.Item2.Quantity > 0;
            if (_localOrderBooks.TryGetValue(item.Item1, out var lob) && lob != null)
                trade.MarketMidPrice = lob.MidPrice;

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
            if (Status != ePluginStatus.STOPPING && Status != ePluginStatus.STOPPED) //avoid executing this if we are actually trying to disconnect.
                Task.Run(async () => await HandleConnectionLost("Websocket has been closed from the server (no informed reason)."));
        }
        private void deltaSubscription_ConnectionLost()
        {
            Task.Run(async () => await HandleConnectionLost("Websocket connection has been lost (no informed reason)."));
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


        private async Task DoPingAsync()
        {
            try
            {
                if (Status == ePluginStatus.STOPPED || Status == ePluginStatus.STOPPING || Status == ePluginStatus.STOPPED_FAILED)
                    return; //do not ping if any of these statues

                bool isConnected = _socketClient.CurrentConnections > 0;
                if (!isConnected)
                {
                    throw new Exception("The socket seems to be disconnected.");
                }


                DateTime ini = DateTime.Now;
                var result = await _restClient.SpotApi.ExchangeData.GetSystemStatusAsync();
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
                    var _error = $"Will reconnect. Unhandled error in DoPingAsync. Initiating reconnection. {ex.Message}";

                    LogException(ex, _error);

                    Task.Run(async () => await HandleConnectionLost(_error, ex));
                }
            }

        }
        private VisualHFT.Model.OrderBook ToOrderBookModel(KrakenOrderBook data, string symbol)
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
        private void UpdateOrderBookSnapshot(KrakenBookUpdate data, string symbol)
        {
            if (!_localOrderBooks.TryGetValue(symbol, out VisualHFT.Model.OrderBook? lob) || lob == null)
            {
                return;
            }

            lob.Clear(); //reset order book
            foreach (var ask in data.Asks)
            {
                lob.AddOrUpdateLevel(new DeltaBookItem()
                {
                    IsBid = false,
                    Price = (double)ask.Price,
                    Size = (double)Math.Abs(ask.Quantity),
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = symbol,
                    MDUpdateAction = eMDUpdateAction.New,
                });
            }
            foreach (var bid in data.Bids)
            {
                lob.AddOrUpdateLevel(new DeltaBookItem()
                {
                    IsBid = true,
                    Price = (double)bid.Price,
                    Size = (double)Math.Abs(bid.Quantity),
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = symbol,
                    MDUpdateAction = eMDUpdateAction.New,
                });
            }
        }
        private void UpdateOrderBook(KrakenBookUpdate lob_update, string symbol, DateTime ts)
        {
            if (!_localOrderBooks.TryGetValue(symbol, out VisualHFT.Model.OrderBook? local_lob))
                return;
            if (local_lob != null)
            {
                try
                {
                    foreach (var item in lob_update.Bids)
                    {
                        if (item.Quantity != 0)
                        {
                            local_lob.AddOrUpdateLevel(new DeltaBookItem()
                            {
                                MDUpdateAction = eMDUpdateAction.New,
                                Price = (double)item.Price,
                                Size = (double)item.Quantity,
                                IsBid = true,
                                LocalTimeStamp = DateTime.Now,
                                ServerTimeStamp = ts,
                                Symbol = symbol
                            });
                        }
                        else if (item.Quantity == 0)
                            local_lob.DeleteLevel(new DeltaBookItem()
                            {
                                MDUpdateAction = eMDUpdateAction.Delete,
                                Price = (double)item.Price,
                                IsBid = true,
                                LocalTimeStamp = DateTime.Now,
                                ServerTimeStamp = ts,
                                Symbol = symbol
                            });
                    }
                    foreach (var item in lob_update.Asks)
                    {
                        if (item.Quantity != 0)
                        {
                            local_lob.AddOrUpdateLevel(new DeltaBookItem()
                            {
                                MDUpdateAction = eMDUpdateAction.New,
                                Price = (double)item.Price,
                                Size = (double)item.Quantity,
                                IsBid = false,
                                LocalTimeStamp = DateTime.Now,
                                ServerTimeStamp = ts,
                                Symbol = symbol
                            });
                        }
                        else if (item.Quantity == 0)
                            local_lob.DeleteLevel(new DeltaBookItem()
                            {
                                MDUpdateAction = eMDUpdateAction.Delete,
                                Price = (double)item.Price,
                                IsBid = false,
                                LocalTimeStamp = DateTime.Now,
                                ServerTimeStamp = ts,
                                Symbol = symbol
                            });
                    }

                }
                catch (Exception e)
                {
                    LogException(e, $"Error updating LOB for {symbol}.");
                    throw;
                }


                //CHECK IF THE CHECKSUM IS THE SAME
                /*var localCheckSum = GenerateLOBChecksum(local_lob);
                if (lob_update.Checksum != localCheckSum)
                {
                    return;
                }*/
                local_lob.LastUpdated = ts;
                RaiseOnDataReceived(local_lob);
            }
        }

        private string FormatValue(string strValue)
        {
            // Convert using InvariantCulture to ensure dot is used as decimal separator
            //string strValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Remove the decimal point
            strValue = strValue.Replace(".", "");
            // Remove all leading zeros
            strValue = strValue.TrimStart('0');
            // Ensure that an empty string becomes "0"
            return string.IsNullOrEmpty(strValue) ? "0" : strValue;
        }
        private string GenerateLevelString(IEnumerable<VisualHFT.Model.BookItem> levels)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (var item in levels)
            {
                // Format price and size following the instructions
                string formattedPrice = FormatValue(item.FormattedPrice);
                string formattedSize = FormatValue(item.FormattedSize);
                sb.Append(formattedPrice);
                sb.Append(formattedSize);
            }
            return sb.ToString();
        }

        private long GenerateLOBChecksum(VisualHFT.Model.OrderBook lob)
        {

            string asksString = GenerateLevelString(lob.Asks.Take(10));
            string bidsString = GenerateLevelString(lob.Bids.Take(10));

            // 1. Generate the concatenated checksum string from asks and bids
            string checksumInput = asksString + bidsString;

            // 2. Compute and return the CRC32 checksum of the concatenated string
            return Crc32Calculator.ComputeCrc32(checksumInput);
        }
        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    foreach (var sub in deltaSubscriptions.Values)
                        UnattachEventHandlers(sub?.Data);
                    foreach (var sub in tradesSubscriptions.Values)
                        UnattachEventHandlers(sub?.Data);
                    
                    _socketClient?.UnsubscribeAllAsync();
                    _socketClient?.Dispose();
                    _restClient?.Dispose();
                    _timerPing?.Dispose();

                    foreach (var q in _eventBuffers)
                        q.Value?.Dispose();
                    _eventBuffers.Clear();

                    foreach (var q in _tradesBuffers)
                        q.Value?.Dispose();
                    _tradesBuffers.Clear();

                    if (_localOrderBooks != null)
                    {
                        foreach (var lob in _localOrderBooks)
                        {
                            lob.Value?.Dispose();
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
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = 3, ProviderName = "Kraken" };
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
                Provider = new VisualHFT.Model.Provider() { ProviderID = 3, ProviderName = "Kraken" },
                Symbols = new List<string>() { "BTC/USD", "ETH/USD" } // Add more symbols as needed
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
            var localModel = new KrakenOrderBook();
            localModel.Bids = snapshotModel.Bids.Select(x => new KrakenOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal(), Timestamp = x.LocalTimeStamp}).ToArray();
            localModel.Asks = snapshotModel.Asks.Select(x => new KrakenOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal(), Timestamp = x.LocalTimeStamp}).ToArray();
            _settings.DepthLevels = snapshotModel.MaxDepth; //force depth received

            var symbol = snapshotModel.Symbol;

            if (!_localOrderBooks.ContainsKey(symbol))
            {
                _localOrderBooks.Add(symbol, ToOrderBookModel(localModel, symbol));
            }
            else
                _localOrderBooks[symbol] = ToOrderBookModel(localModel, symbol);
            //then this method is called from the delta websocket
            UpdateOrderBookSnapshot(new KrakenBookUpdate()
            {
                Symbol = symbol,
                Asks = snapshotModel.Asks.Select(x => new KrakenBookUpdateEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray(),
                Bids = snapshotModel.Bids.Select(x => new KrakenBookUpdateEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray()
            }, symbol);
            _localOrderBooks[symbol].Sequence = sequence;// KRAKEN does not provide sequence numbers

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

            var localModel = new KrakenBookUpdate();
            localModel.Bids = bidDeltaModel?.Select(x => new KrakenBookUpdateEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal()}).ToArray();
            localModel.Asks = askDeltaModel?.Select(x => new KrakenBookUpdateEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray();

            //************************************************************************************************************************
            //sequence is not provided by KRAKEN (then make adjustments to this method, so Unit tests don't fail)
            //************************************************************************************************************************
            long maxSequence = Math.Max(bidDeltaModel.Max(x => x.Sequence), askDeltaModel.Max(x => x.Sequence));
            long minSequence = Math.Min(bidDeltaModel.Min(x => x.Sequence), askDeltaModel.Min(x => x.Sequence));
            if (_localOrderBooks.ContainsKey(symbol))
            {
                if (minSequence < _localOrderBooks[symbol].Sequence)
                {
                    bidDeltaModel.RemoveAll(x => x.Sequence <= _localOrderBooks[symbol].Sequence);
                    askDeltaModel.RemoveAll(x => x.Sequence <= _localOrderBooks[symbol].Sequence);
                    localModel.Bids = bidDeltaModel?.Select(x => new KrakenBookUpdateEntry()
                        { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray();
                    localModel.Asks = askDeltaModel?.Select(x => new KrakenBookUpdateEntry()
                        { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray();
                }
                else if (minSequence != _localOrderBooks[symbol].Sequence + 1)
                {
                    throw new Exception("Sequence numbers are not in order.");
                }
                else
                    _localOrderBooks[symbol].Sequence = maxSequence;
            }
            //************************************************************************************************************************
            //************************************************************************************************************************

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

            string jsonString = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"kraken_JsonMessages/{_file}"));

            //DESERIALIZE EXCHANGES MODEL
            List<KrakenOrderUpdate> modelList = new List<KrakenOrderUpdate>();
            var dataEvents = new List<KrakenOrderUpdate>();
            var jsonArray = JArray.Parse(jsonString);
            foreach (var jsonObject in jsonArray)
            {
                JToken dataToken = jsonObject["data"];
                string dataJsonString = dataToken.ToString();
                var jsonArrayData = JArray.Parse(dataJsonString);
                foreach (var jsonObjectData in jsonArrayData)
                {
                    string jsonObjectDataString = jsonObjectData.ToString(); // Convert JObject to string for debugging
                    KrakenOrderUpdate _data = JsonParser.Parse(jsonObjectDataString);
                    if (_data != null)
                        modelList.Add(_data);
                }
            }
            //END DESERIALIZE EXCHANGES MODEL



            //UPDATE VISUALHFT CORE & CREATE MODEL TO RETURN
            if (!modelList.Any())
                throw new Exception("No data was found in the json file.");
            foreach (var item in modelList)
            {
                UpdateUserOrder(item);
            }
            //END UPDATE VISUALHFT CORE



            //CREATE MODEL TO RETURN (First, identify the order that was sent, then use that one with the updated values)
            var dicOrders = new Dictionary<string, VisualHFT.Model.Order>(); //we need to use dictionary to identify orders (because exchanges orderId is string)

            foreach (var item in modelList)
            {
                if (item.OrderStatus == OrderStatusUpdate.Pending) //identify the order sent
                {
                    var order = new VisualHFT.Model.Order()
                    {
                        CreationTimeStamp = item.Timestamp,
                        PricePlaced = item.LimitPrice.ToDouble(),
                        ProviderId = _settings.Provider.ProviderID,
                        ProviderName = _settings.Provider.ProviderName,
                        Symbol = item.Symbol,
                        Side = item.OrderSide == OrderSide.Buy ? eORDERSIDE.Buy : eORDERSIDE.Sell,
                    };
                    if (item.OrderType != OrderType.Market)
                        order.Quantity = item.OrderQuantity.ToDouble();

                    //Since OrderID needs to match (the one we are creating for this scenario vs the one created by this class) use the localUserOrders to get it
                    order.OrderID = this._localUserOrders[item.OrderId].OrderID; //DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (item.TimeInForce == TimeInForce.IOC)
                        order.TimeInForce = eORDERTIMEINFORCE.IOC;
                    else if (item.TimeInForce == TimeInForce.GTC || item.TimeInForce == TimeInForce.GTD)
                        order.TimeInForce = eORDERTIMEINFORCE.GTC;

                    if (item.OrderType == OrderType.Limit)
                        order.OrderType = eORDERTYPE.LIMIT;
                    else if (item.OrderType == OrderType.Market)
                        order.OrderType = eORDERTYPE.MARKET;
                    else
                        order.OrderType = eORDERTYPE.LIMIT;
                    order.Status = eORDERSTATUS.SENT;

                    dicOrders.Add(item.OrderId, order);
                }
                else if (item.OrderStatus == OrderStatusUpdate.New)
                {
                    var orderToUpdate = dicOrders[item.OrderId];
                    orderToUpdate.Status = eORDERSTATUS.NEW;
                    if (item.OrderEventType == OrderEventType.Amended) //the order was amended
                    {
                        orderToUpdate.PricePlaced = item.LimitPrice.ToDouble();
                        if (item.OrderQuantity.HasValue)
                            orderToUpdate.Quantity = item.OrderQuantity.ToDouble();
                        orderToUpdate.LastUpdated = item.Timestamp;
                        if (item.QuantityFilled.HasValue)
                            orderToUpdate.FilledQuantity = item.QuantityFilled.ToDouble();
                        if (item.AveragePrice.HasValue)
                            orderToUpdate.FilledPrice = item.AveragePrice.ToDouble();
                    }
                }
                else if (item.OrderStatus == OrderStatusUpdate.Canceled || item.OrderStatus == OrderStatusUpdate.Expired)
                {
                    var orderToUpdate = dicOrders[item.OrderId];

                    //update needed fields only
                    orderToUpdate.LastUpdated = item.Timestamp;
                    if (item.QuantityFilled.HasValue)
                        orderToUpdate.FilledQuantity = item.QuantityFilled.ToDouble();
                    if (item.AveragePrice.HasValue)
                        orderToUpdate.FilledPrice = item.AveragePrice.ToDouble();

                    orderToUpdate.Status = eORDERSTATUS.CANCELED;
                }
                else if (item.OrderStatus == OrderStatusUpdate.PartiallyFilled || item.OrderStatus == OrderStatusUpdate.Filled)
                {
                    var orderToUpdate = dicOrders[item.OrderId];

                    //update needed fields only
                    orderToUpdate.LastUpdated = item.Timestamp;
                    if (item.QuantityFilled.HasValue)
                        orderToUpdate.FilledQuantity = item.QuantityFilled.ToDouble();
                    if (item.AveragePrice.HasValue)
                        orderToUpdate.FilledPrice = item.AveragePrice.ToDouble();
                    orderToUpdate.Status = (item.OrderStatus == OrderStatusUpdate.PartiallyFilled ? eORDERSTATUS.PARTIALFILLED: eORDERSTATUS.FILLED);
                    if (orderToUpdate.OrderType == eORDERTYPE.MARKET) //update original order and price placed
                    {
                        orderToUpdate.PricePlaced = orderToUpdate.FilledPrice;
                        orderToUpdate.Quantity = orderToUpdate.FilledQuantity;
                    }
                }

            }
            //END CREATE MODEL TO RETURN


            return dicOrders.Values.ToList();

        }


    }





}

