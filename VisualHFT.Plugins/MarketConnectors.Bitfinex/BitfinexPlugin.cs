using Bitfinex.Net;
using Bitfinex.Net.Clients;
using Bitfinex.Net.Objects.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using MarketConnectors.Bitfinex.Model;
using MarketConnectors.Bitfinex.UserControls;
using MarketConnectors.Bitfinex.ViewModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bitfinex.Net.Enums;
using VisualHFT.Commons.PluginManager;
using VisualHFT.UserSettings;
using VisualHFT.Commons.Pools;
using VisualHFT.Commons.Model;
using CryptoExchange.Net.Objects.Sockets;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.Commons.Interfaces;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using VisualHFT.Commons.Exceptions;

namespace MarketConnectors.Bitfinex
{
    public class BitfinexPlugin : BasePluginDataRetriever, IDataRetrieverTestable
    {
        private bool _disposed = false; // to track whether the object has been disposed
        
        // ✅ FIX: Add synchronization and reconnection flag
        private readonly SemaphoreSlim _startStopLock = new SemaphoreSlim(1, 1);
        private bool isReconnecting = false;

        private PlugInSettings _settings;
        private BitfinexSocketClient _socketClient;
        private BitfinexRestClient _restClient;
        private Dictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks = new Dictionary<string, VisualHFT.Model.OrderBook>();
        private Dictionary<string, HelperCustomQueue<Tuple<DateTime, string, BitfinexOrderBookEntry>>> _eventBuffers = new();
        private Dictionary<string, HelperCustomQueue<Tuple<string, BitfinexTradeSimple>>> _tradesBuffers = new();
        private readonly object _buffersLock = new object(); // ✅ ADD: Thread-safe buffer access

        private int pingFailedAttempts = 0;
        private System.Timers.Timer _timerPing;
        private CallResult<UpdateSubscription> deltaSubscription;  // Revert to single variable
        private CallResult<UpdateSubscription> tradesSubscription; // Revert to single variable

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly CustomObjectPool<VisualHFT.Model.Trade> tradePool = new CustomObjectPool<VisualHFT.Model.Trade>();//pool of Trade objects


        public override string Name { get; set; } = "Bitfinex Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Connects to Bitfinex websockets.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }

        public BitfinexPlugin()
        {
            SetReconnectionAction(InternalStartAsync);
            log.Info($"{this.Name} has been loaded.");
        }
        ~BitfinexPlugin()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {
            await base.StartAsync(); // ✅ This sets Status = STARTING

            _socketClient = new BitfinexSocketClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey, _settings.ApiSecret);
                options.Environment = BitfinexEnvironment.Live;
            });

            _restClient = new BitfinexRestClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey, _settings.ApiSecret);
                options.Environment = BitfinexEnvironment.Live;
            });

            // ✅ FIX: Explicitly report STARTING status so transition history captures it
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTING));

            try
            {
                await InternalStartAsync();
                if (Status == ePluginStatus.STOPPED_FAILED)
                    return;
            }
            catch (Exception ex)
            {
                var _error = ex.Message;
                LogException(ex);
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

                // Initialize event buffer for each symbol
                foreach (var symbol in GetAllNormalizedSymbols())
                {
                    _eventBuffers.Add(symbol, new HelperCustomQueue<Tuple<DateTime, string, BitfinexOrderBookEntry>>($"<Tuple<DateTime, string, BitfinexOrderBookEntry>>_{this.Name.Replace(" Plugin", "")}", eventBuffers_onReadAction, eventBuffers_onErrorAction));
                    _tradesBuffers.Add(symbol, new HelperCustomQueue<Tuple<string, BitfinexTradeSimple>>($"<Tuple<DateTime, string, BitfinexTradeSimple>>_{this.Name.Replace(" Plugin", "")}", tradesBuffers_onReadAction, tradesBuffers_onErrorAction));
                }

                await InitializeDeltasAsync();
                await InitializeSnapshotsAsync();
                await InitializeTradesAsync();
                await InitializeUserPrivateOrders();
                await InitializePingTimerAsync();
                
                // ✅ FIX: Set status to STARTED so tests can detect status transitions
                log.Info($"Plugin has successfully started.");
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                Status = ePluginStatus.STARTED;
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
                // ✅ FIX: Set status FIRST to prevent reconnection race condition
                Status = ePluginStatus.STOPPING;
                log.Info($"{this.Name} is stopping.");

                // ✅ FIX: Force cancel any pending reconnections by closing subscriptions first
                UnattachEventHandlers(deltaSubscription?.Data);
                UnattachEventHandlers(tradesSubscription?.Data);
                
                if (deltaSubscription != null && deltaSubscription.Data != null)
                    await deltaSubscription.Data.CloseAsync();
                if (tradesSubscription != null && tradesSubscription.Data != null)
                    await tradesSubscription.Data.CloseAsync();

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
            // ✅ Subscription cleanup is done in StopAsync to prevent reconnection races
            // Only unsubscribe here if not already done
            if (_socketClient != null)
                await _socketClient.UnsubscribeAllAsync();

            _timerPing?.Stop();
            _timerPing?.Dispose();

            // ✅ FIX: Pause queues before stopping to prevent processing during shutdown
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
                tradesSubscription = await _socketClient.SpotApi.SubscribeToTradeUpdatesAsync(
                    symbol,
                    trade =>
                    {
                        // Buffer the trades
                        if (trade.Data != null)
                        {
                            try
                            {
                                // ✅ FIX: Thread-safe buffer access
                                HelperCustomQueue<Tuple<string, BitfinexTradeSimple>> buffer;
                                lock (_buffersLock)
                                {
                                    if (!_tradesBuffers.TryGetValue(_normalizedSymbol, out buffer))
                                        return; // Buffer was cleared during reconnection
                                }
                                
                                foreach (var item in trade.Data)
                                {
                                    item.Timestamp = trade.ReceiveTime; //not sure why these are different
                                    buffer.Add(
                                        new Tuple<string, BitfinexTradeSimple>(_normalizedSymbol, item));
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
            if (string.IsNullOrEmpty(this._settings.ApiKey) && !string.IsNullOrEmpty(this._settings.ApiSecret))
            {
                await _socketClient.SpotApi.SubscribeToUserUpdatesAsync(async neworder =>
                {
                    log.Info(neworder.Data);
                    if (neworder.Data != null)
                    {
                        IEnumerable<BitfinexOrder> item = neworder.Data;

                        foreach (var order in item)
                        {
                            await UpdateUserOrder(order);
                        }
                    }
                });
            }
        }
        private async Task UpdateUserOrder(BitfinexOrder item)
        {
            var order = new VisualHFT.Model.Order()
            {
                OrderID = item.Id,
                CreationTimeStamp = item.CreateTime,
                PricePlaced = item.Price.ToDouble(),
                Quantity = item.Quantity.ToDouble(),
                ProviderId = _settings.Provider.ProviderID,
                ProviderName = _settings.Provider.ProviderName,
                Symbol = item.Symbol,
                Side = item.Side == OrderSide.Buy ? eORDERSIDE.Buy : eORDERSIDE.Sell,
                ClOrdId = item.ClientOrderId.ToString()
            };
            if (item.Type != OrderType.ExchangeMarket && item.Type != OrderType.Market)
                order.Quantity = item.Quantity.ToDouble();


            if (item.Type == OrderType.ExchangeFillOrKill || item.Type == OrderType.FillOrKill)
                order.TimeInForce = eORDERTIMEINFORCE.FOK;
            else if (item.Type == OrderType.ExchangeImmediateOrCancel || item.Type == OrderType.ImmediateOrCancel)
                order.TimeInForce = eORDERTIMEINFORCE.IOC;
            else
                order.TimeInForce = eORDERTIMEINFORCE.GTC;

            if (item.Type == OrderType.Market || item.Type == OrderType.ExchangeMarket)
                order.OrderType = eORDERTYPE.MARKET;
            else
                order.OrderType = eORDERTYPE.LIMIT;

            if (item.Status == OrderStatus.Active)
            {
                order.PricePlaced = item.Price.ToDouble();
                order.Quantity = item.Quantity.ToDouble();
                order.Status = eORDERSTATUS.NEW;
            }
            else if (item.Status == OrderStatus.Canceled)
                order.Status = eORDERSTATUS.CANCELED;
            else if (item.Status == OrderStatus.Executed || item.Status == OrderStatus.ForcefullyExecuted || item.Status == OrderStatus.PartiallyFilled)
            {
                order.Status = (item.Status == OrderStatus.PartiallyFilled ? eORDERSTATUS.PARTIALFILLED : eORDERSTATUS.FILLED);
                order.FilledQuantity = order.Quantity - item.QuantityRemaining.ToDouble();
                order.FilledPrice = item.PriceAverage.ToDouble();
            }

            order.LastUpdated = DateTime.Now;
            RaiseOnDataReceived(order);
        }
        private async Task InitializeDeltasAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);
                log.Info($"{this.Name}: sending WS Deltas Subscription {normalizedSymbol} ");
                deltaSubscription = await _socketClient.SpotApi.SubscribeToOrderBookUpdatesAsync(
                    symbol,
                    Precision.PrecisionLevel0, Frequency.Realtime,
                    _settings.DepthLevels,
                    data =>
                    {
                        // Buffer the events
                        if (data.Data != null)
                        {
                            try
                            {
                                if (data.UpdateType == SocketUpdateType.Snapshot)
                                {
                                    UpdateOrderBookSnapshot(data.Data, normalizedSymbol);
                                }
                                else
                                {
                                    // ✅ FIX: Thread-safe buffer access
                                    HelperCustomQueue<Tuple<DateTime, string, BitfinexOrderBookEntry>> buffer;
                                    lock (_buffersLock)
                                    {
                                        if (!_eventBuffers.TryGetValue(normalizedSymbol, out buffer))
                                            return; // Buffer was cleared during reconnection
                                    }
                                    
                                    foreach (var item in data.Data)
                                    {
                                        buffer.Add(
                                            new Tuple<DateTime, string, BitfinexOrderBookEntry>(
                                                data.ReceiveTime.ToLocalTime(), normalizedSymbol, item));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                var _error = $"Will reconnect. Unhandled error while receiving delta market data for {normalizedSymbol}.";
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
                    }, null, new CancellationToken());
                if (deltaSubscription.Success)
                {
                    AttachEventHandlers(deltaSubscription.Data);
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
                var depthSnapshot = await _restClient.SpotApi.ExchangeData.GetOrderBookAsync(symbol, Precision.PrecisionLevel0, _settings.DepthLevels);
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

        private void eventBuffers_onReadAction(Tuple<DateTime, string, BitfinexOrderBookEntry> eventData)
        {
            UpdateOrderBook(eventData.Item3, eventData.Item2, eventData.Item1);
        }
        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";

            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }
        private void tradesBuffers_onReadAction(Tuple<string, BitfinexTradeSimple> item)
        {
            if (!_localOrderBooks.ContainsKey(item.Item1))
                return;
            var trade = tradePool.Get();
            trade.Price = item.Item2.Price;
            trade.Size = Math.Abs(item.Item2.Quantity);
            trade.Symbol = item.Item1;
            trade.Timestamp = item.Item2.Timestamp.ToLocalTime();
            trade.ProviderId = _settings.Provider.ProviderID;
            trade.ProviderName = _settings.Provider.ProviderName;
            trade.IsBuy = item.Item2.Quantity > 0;
            trade.MarketMidPrice = _localOrderBooks[item.Item1]?.MidPrice ?? 0;

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
        private void UnattachEventHandlers(IEnumerable<UpdateSubscription> data)
        {
            if (data == null)
                return;

            foreach (var d in data)
            {
                if (d == null) continue;
                d.Exception -= deltaSubscription_Exception;
                d.ConnectionLost -= deltaSubscription_ConnectionLost;
                d.ConnectionClosed -= deltaSubscription_ConnectionClosed;
                d.ConnectionRestored -= deltaSubscription_ConnectionRestored;
                d.ActivityPaused -= deltaSubscription_ActivityPaused;
                d.ActivityUnpaused -= deltaSubscription_ActivityUnpaused;
            }
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

            // ✅ FIX: Use isReconnecting flag to prevent duplicate reconnection attempts
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
                var result = await _restClient.SpotApi.ExchangeData.GetPlatformStatusAsync();
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
        private VisualHFT.Model.OrderBook ToOrderBookModel(BitfinexOrderBook data, string symbol)
        {
            var identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.Asks.Select(x => x.Price));

            var lob = new VisualHFT.Model.OrderBook(symbol, identifiedPriceDecimalPlaces, _settings.DepthLevels);
            lob.ProviderID = _settings.Provider.ProviderID;
            lob.ProviderName = _settings.Provider.ProviderName;
            lob.SizeDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.Asks.Select(x => x.Quantity));

            data.Asks.ToList().ForEach(x =>
            {
                lob.AddOrUpdateLevel(new DeltaBookItem()
                {
                    IsBid = false, 
                    Price = (double)x.Price,
                    Size = (double)Math.Abs(x.Quantity), 
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = symbol,
                    MDUpdateAction = eMDUpdateAction.New,
                });
            });
            data.Bids.ToList().ForEach(x =>
            {
                lob.AddOrUpdateLevel(new DeltaBookItem()
                {
                    IsBid = true, 
                    Price = (double)x.Price,
                    Size = (double)Math.Abs(x.Quantity), 
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = symbol,
                    MDUpdateAction = eMDUpdateAction.New,
                });
            });

            return lob;
        }
        private void UpdateOrderBookSnapshot(IEnumerable<BitfinexOrderBookEntry> data, string symbol)
        {
            if (!_localOrderBooks.TryGetValue(symbol, out VisualHFT.Model.OrderBook? lob))
            {
                return;
            }
            lob.Clear(); //reset order book

            data.ToList().ForEach(x =>
            {
                lob.AddOrUpdateLevel(new DeltaBookItem()
                {
                    IsBid = (x.Quantity > 0),
                    Price = (double)x.Price,
                    Size = (double)Math.Abs(x.Quantity),
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = symbol,
                    MDUpdateAction = eMDUpdateAction.New,
                });
            });
        }
        private void UpdateOrderBook(BitfinexOrderBookEntry lob_update, string symbol, DateTime ts)
        {
            if (!_localOrderBooks.ContainsKey(symbol))
                return;
            if (lob_update == null)
                return;

            var local_lob = _localOrderBooks[symbol];

            if (local_lob != null)
            {
                bool isBid = lob_update.Quantity > 0;

                if (lob_update.Count == 0) //remove
                {
                    var delta = new DeltaBookItem()
                    {
                        Price = (double)lob_update.Price,
                        Size = (double)Math.Abs(lob_update.Quantity),
                        IsBid = isBid,
                        LocalTimeStamp = DateTime.Now,
                        ServerTimeStamp = ts,
                        Symbol = local_lob.Symbol,
                        MDUpdateAction = eMDUpdateAction.Delete,
                    };
                    local_lob.DeleteLevel(delta);
                }
                else
                {
                    var delta = new DeltaBookItem()
                    {
                        Price = (double)lob_update.Price,
                        Size = (double)Math.Abs(lob_update.Quantity),
                        IsBid = isBid,
                        LocalTimeStamp = DateTime.Now,
                        ServerTimeStamp = ts,
                        Symbol = local_lob.Symbol,
                        MDUpdateAction = eMDUpdateAction.Change,
                    };
                    local_lob.AddOrUpdateLevel(delta);
                }
                local_lob.LastUpdated = ts;
                RaiseOnDataReceived(local_lob);
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
                    
                    // ✅ FIX: Dispose semaphore
                    _startStopLock?.Dispose();

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
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = 2, ProviderName = "Bitfinex" };
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
                Provider = new VisualHFT.Model.Provider() { ProviderID = 2, ProviderName = "Bitfinex" },
                Symbols = new List<string>() { "BTCUSD(BTC/USD)", "ETHUSD(ETH/USD)" } // Add more symbols as needed
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
            var localModel = new BitfinexOrderBook();
            localModel.Asks = snapshotModel.Asks.Select(x => new BitfinexOrderBookEntry() { Price = (decimal)x.Price, Quantity = (decimal)x.Size }).ToArray(); // Positive for asks
            localModel.Bids = snapshotModel.Bids.Select(x => new BitfinexOrderBookEntry() { Price = (decimal)x.Price, Quantity = (decimal)x.Size }).ToArray(); // Positive for bids
            _settings.DepthLevels = snapshotModel.MaxDepth; //force depth received
            var symbol = snapshotModel.Symbol;

            if (!_localOrderBooks.ContainsKey(symbol))
            {
                _localOrderBooks.Add(symbol, ToOrderBookModel(localModel, symbol));
            }
            else
                _localOrderBooks[symbol] = ToOrderBookModel(localModel, symbol);
            _localOrderBooks[symbol].Sequence = sequence;// Bitfinex does not provide sequence numbers

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



            //************************************************************************************************************************
            //sequence is not provided by BITFINEX (then make adjustments to this method, so Unit tests don't fail)
            //************************************************************************************************************************
            long maxSequence = Math.Max(bidDeltaModel.Max(x => x.Sequence), askDeltaModel.Max(x => x.Sequence));
            long minSequence = Math.Min(bidDeltaModel.Min(x => x.Sequence), askDeltaModel.Min(x => x.Sequence));
            if (_localOrderBooks.ContainsKey(symbol))
            {
                if (minSequence < _localOrderBooks[symbol].Sequence)
                {
                    bidDeltaModel.RemoveAll(x => x.Sequence <= _localOrderBooks[symbol].Sequence);
                    askDeltaModel.RemoveAll(x => x.Sequence <= _localOrderBooks[symbol].Sequence);
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


            //transform to BitfinexOrderBookEntry
            bidDeltaModel?.ForEach(x =>
            {
                decimal _qty = 0;
                if (x.Size.HasValue)
                    _qty = x.Size.ToDecimal();
                else if (!x.Size.HasValue && x.MDUpdateAction == eMDUpdateAction.Delete)
                    _qty = 1;

                UpdateOrderBook(new BitfinexOrderBookEntry()
                {
                    Price = x.Price.ToDecimal(),
                    Quantity = _qty, // Positive for bids
                    Count = x.MDUpdateAction == eMDUpdateAction.Delete ? 0 : 1
                }, symbol, ts);
            });
            askDeltaModel?.ForEach(x =>
            {
                decimal _qty = 0;
                if (x.Size.HasValue)
                    _qty = x.Size.ToDecimal();
                else if (!x.Size.HasValue && x.MDUpdateAction == eMDUpdateAction.Delete)
                    _qty = 1;

                UpdateOrderBook(new BitfinexOrderBookEntry()
                {
                    Price = x.Price.ToDecimal(),
                    Quantity = -_qty, // Negative for asks  
                    Count = x.MDUpdateAction == eMDUpdateAction.Delete ? 0 : 1
                }, symbol, ts);
            });
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
            {
                _file = "PrivateMessages_Scenario8.json";
                throw new ExceptionScenarioNotSupportedByExchange();
            }
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_9)
            {
                _file = "PrivateMessages_Scenario9.json";
                throw new ExceptionScenarioNotSupportedByExchange();
            }
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_10)
            {
                _file = "PrivateMessages_Scenario10.json";
                throw new ExceptionScenarioNotSupportedByExchange();
            }

            string jsonString = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"bitfinex_jsonMessages/{_file}"));




            //DESERIALIZE EXCHANGES MODEL
            JArray outerArray = JArray.Parse(jsonString);
            var modelList = outerArray.Select(item =>
            {
                //Reference to documentation: https://docs.bitfinex.com/reference/ws-auth-orders
                var arr = (JArray)item[2];


                //parse status (ie: CANCELED, "EXECUTED @ 96211.0(0.0005)"

                OrderStatus status=OrderStatus.Unknown;
                if (arr[13].ToString() == "ACTIVE")
                    status= OrderStatus.Active;
                else if (arr[13].ToString() == "EXECUTED")
                    status = OrderStatus.Executed;
                else if (arr[13].ToString() == "CANCELED")
                    status = OrderStatus.Canceled;
                else if (arr[13].ToString() == "FORCED EXECUTED")
                    status = OrderStatus.ForcefullyExecuted;
                else if (arr[13].ToString() == "PARTIALLY FILLED")
                    status = OrderStatus.PartiallyFilled;

                else if (arr[13].ToString().Contains("CANCELED"))
                    status = OrderStatus.Canceled;

                else if (arr[13].ToString().Contains("EXECUTED @"))
                {
                    status = OrderStatus.Executed;
                }
                 
                return new BitfinexOrder
                {
                    Id = arr[0].Value<long>(),
                    GroupId = arr[1].Type == JTokenType.Null ? null : arr[1].Value<long?>(),
                    ClientOrderId = arr[2].Type == JTokenType.Null ? null : arr[2].Value<long?>(),
                    Symbol = arr[3].Value<string>(),
                    // Convert the Unix timestamps or apply your DateTimeConverter logic:
                    CreateTime = DateTimeOffset.FromUnixTimeMilliseconds(arr[4].Value<long>()).LocalDateTime,
                    UpdateTime = DateTimeOffset.FromUnixTimeMilliseconds(arr[5].Value<long>()).LocalDateTime,
                    QuantityRemainingRaw = arr[6].Value<decimal>(),
                    QuantityRaw = arr[7].Value<decimal>(),
                    //Type = (OrderType)Enum.Parse(typeof(OrderType), arr[8].Value<string>(), true),
                    //TypePrevious = (OrderType)Enum.Parse(typeof(OrderType), arr[9].Value<string>(), true),
                    //Status = (OrderStatus)Enum.Parse(typeof(OrderStatus), arr[13].Value<string>(), true),
                    Type = DeserializeEnumWithConverter<OrderType>(arr[8]),
                    TypePrevious = DeserializeEnumWithConverter<OrderType>(arr[9]),
                    Status = status,
                    Price = arr[16].Type == JTokenType.Null ? 0 : arr[16].Value<decimal>(),
                    PriceAverage = arr[17].Type == JTokenType.Null ? null : arr[17].Value<decimal?>(),
                    PriceTrailing = arr[18].Type == JTokenType.Null ? 0 : arr[18].Value<decimal>(),
                    Routing = arr[28].Type == JTokenType.Null ? string.Empty : arr[28].Value<string>(),
                };
            }).ToList();
            //END DESERIALIZE EXCHANGES MODEL





            //UPDATE VISUALHFT CORE & CREATE MODEL TO RETURN
            if (!modelList.Any())
                throw new Exception("No data was found in the json file.");
            foreach (var item in modelList)
            {
                UpdateUserOrder(item);
            }
            //END UPDATE VISUALHFT CORE


            //CREATE MODEL TO RETURN 
            var retOrders = new List<VisualHFT.Model.Order>();
            foreach (var item in modelList)
            {
                VisualHFT.Model.Order order = null;
                if (retOrders.All(x => x.ClOrdId != item.ClientOrderId.ToString()))
                {
                    order = new VisualHFT.Model.Order()
                    {
                        OrderID = item.Id,
                        CreationTimeStamp = item.CreateTime,
                        PricePlaced = item.Price.ToDouble(),
                        Quantity = item.Quantity.ToDouble(),
                        ProviderId = _settings.Provider.ProviderID,
                        ProviderName = _settings.Provider.ProviderName,
                        Symbol = item.Symbol,
                        Side = item.Side == OrderSide.Buy ? eORDERSIDE.Buy : eORDERSIDE.Sell,
                    };
                    retOrders.Add(order);
                }
                else
                {
                    order = retOrders.FirstOrDefault(x => x.ClOrdId == item.ClientOrderId.ToString());
                }

                order.ClOrdId = item.ClientOrderId.ToString();
                if (item.Type != OrderType.ExchangeMarket && item.Type != OrderType.Market)
                    order.Quantity = item.Quantity.ToDouble();


                if (item.Type == OrderType.ExchangeFillOrKill || item.Type == OrderType.FillOrKill)
                    order.TimeInForce = eORDERTIMEINFORCE.FOK;
                else if (item.Type == OrderType.ExchangeImmediateOrCancel || item.Type == OrderType.ImmediateOrCancel)
                    order.TimeInForce = eORDERTIMEINFORCE.IOC;
                else
                    order.TimeInForce = eORDERTIMEINFORCE.GTC;

                if (item.Type == OrderType.Market || item.Type == OrderType.ExchangeMarket)
                    order.OrderType = eORDERTYPE.MARKET;
                else
                    order.OrderType = eORDERTYPE.LIMIT;

                if (item.Status == OrderStatus.Active)
                {
                    order.PricePlaced = item.Price.ToDouble();
                    order.Quantity = item.Quantity.ToDouble();
                    order.Status = eORDERSTATUS.NEW;
                }
                else if (item.Status == OrderStatus.Canceled)
                    order.Status = eORDERSTATUS.CANCELED;
                else if (item.Status == OrderStatus.Executed || item.Status == OrderStatus.ForcefullyExecuted || item.Status == OrderStatus.PartiallyFilled)
                {
                    order.Status = (item.Status == OrderStatus.PartiallyFilled? eORDERSTATUS.PARTIALFILLED:  eORDERSTATUS.FILLED);
                    order.FilledQuantity = order.Quantity - item.QuantityRemaining.ToDouble();
                    order.FilledPrice = item.PriceAverage.ToDouble();
                }


                
            }
            //END CREATE MODEL TO RETURN


            return retOrders;
        }
        private T DeserializeEnumWithConverter<T>(JToken token)
        {
            var settings = new JsonSerializerSettings();

            if (typeof(T) == typeof(OrderType))
            {
                settings.Converters.Add(new BitfinexOrderTypeNewtonsoftConverter() as Newtonsoft.Json.JsonConverter);
            }
            else if (typeof(T) == typeof(OrderStatus))
            {
                settings.Converters.Add(new BitfinexOrderStatusNewtonsoftConverter() as Newtonsoft.Json.JsonConverter);
            }

            string jsonStringValue = token.Value<string>();
            string jsonToDeserialize = JsonConvert.SerializeObject(jsonStringValue);

            return JsonConvert.DeserializeObject<T>(jsonToDeserialize, settings);
        }
    }
}
