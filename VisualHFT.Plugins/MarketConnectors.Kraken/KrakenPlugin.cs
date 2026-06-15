using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using Kraken.Net;
using Kraken.Net.Clients;
using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using Kraken.Net.Objects.Models.Socket;
using MarketConnectors.Kraken.Model;
using MarketConnectors.Kraken.UserControls;
using MarketConnectors.Kraken.ViewModel;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Interfaces;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Commons.Pools;
using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;

namespace MarketConnectors.Kraken
{
    public class KrakenPlugin : BasePluginDataRetriever, IDataRetrieverTestable
    {
        private bool _disposed = false; // to track whether the object has been disposed
        
        // ✅ FIX: Add synchronization and reconnection flag
        private readonly SemaphoreSlim _startStopLock = new SemaphoreSlim(1, 1);
        private bool isReconnecting = false;

        private PlugInSettings _settings;
        private KrakenSocketClient _socketClient;
        private KrakenRestClient _restClient;
        // CONC-1: thread-safe — written on the WS snapshot path, read on the book + trades consumer threads.
        private readonly ConcurrentDictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks = new();
        // ACC-1: per-symbol raw-DECIMAL mirror of the book, maintained solely to validate Kraken's v2 CRC32
        // integrity checksum (the double-based display book cannot reproduce the wire precision it needs).
        // Touched only on the single book-consumer thread (and cleared in ClearAsync after that thread is joined).
        private readonly ConcurrentDictionary<string, KrakenDecimalBook> _decimalBooks = new();
        // Per-frame CRC32 validation mode (2026-06-14):
        //   Off     — don't maintain/check the ladder.
        //   LogOnly — maintain + check + LOG mismatches, but DO NOT reconnect (safe soak; no feed disruption).
        //   Enforce — on a confirmed mismatch, drop the frame and resync via the bounded reconnect path.
        // ENFORCE since 2026-06-14: a ~1.5h live soak across a volatile session logged ZERO per-frame mismatches
        // and zero reconnects after the depth-truncation fix (ladder now trims to the subscribed depth, matching
        // Kraken's own book). Desync detection + resync is now armed. Revert to LogOnly if a regression surfaces.
        private enum ChecksumValidationMode { Off, LogOnly, Enforce }
        private ChecksumValidationMode _checksumMode = ChecksumValidationMode.Enforce;
        // PERF-5: value-tuple payloads avoid a per-frame heap Tuple allocation. The isSnapshot flag routes
        // snapshots through the SAME single-consumer queue as deltas (ACC-4/CONC-4: ordered, race-free book build).
        private Dictionary<string, HelperCustomQueue<(DateTime ts, string symbol, KrakenBookUpdate data, bool isSnapshot)>> _eventBuffers = new();
        private Dictionary<string, HelperCustomQueue<(string symbol, KrakenTradeUpdate trade)>> _tradesBuffers = new();
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
                {
                    options.ApiCredentials = new KrakenCredentials() { Spot = new HMACCredential(_settings.ApiKey, _settings.ApiSecret) };
                }
                options.Environment = KrakenEnvironment.Live;
            });

            _restClient = new KrakenRestClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                {
                    options.ApiCredentials = new KrakenCredentials() { Spot = new HMACCredential(_settings.ApiKey, _settings.ApiSecret) };
                }
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
            // ✅ FIX: Add synchronization
            await _startStopLock.WaitAsync();
            try
            {
                await ClearAsync();

                // Initialize event buffer for each symbol
                foreach (var symbol in GetAllNormalizedSymbols())
                {
                    _eventBuffers.Add(symbol, new HelperCustomQueue<(DateTime ts, string symbol, KrakenBookUpdate data, bool isSnapshot)>($"<BookEvent>_{this.Name.Replace(" Plugin", "")}", eventBuffers_onReadAction, eventBuffers_onErrorAction));
                    _tradesBuffers.Add(symbol, new HelperCustomQueue<(string symbol, KrakenTradeUpdate trade)>($"<TradeEvent>_{this.Name.Replace(" Plugin", "")}", tradesBuffers_onReadAction, tradesBuffers_onErrorAction));
                }

                //await InitializeSnapshotsAsync(); // Snapshots are now handled in the delta subscription callback
                await InitializeTradesAsync();
                await InitializeDeltasAsync();
                await InitializePingTimerAsync();
                await InitializeUserPrivateOrders();

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
            finally
            {
                _startStopLock.Release();
            }
        }

        private async Task ClearAsync()
        {
            // ✅ Subscription cleanup is done in StopAsync to prevent reconnection races
            // Only unsubscribe here if not already done
            if (_socketClient != null)
                await _socketClient.UnsubscribeAllAsync();
                
            _timerPing?.Stop();
            _timerPing?.Dispose();

            // CONC-2: the book/trade consumer threads mutate the OrderBooks (and the shared BookItemPool).
            // Stop() only signals cancellation — it does NOT join — so disposing the books while a consumer
            // is mid-AddOrUpdateLevel risks an ObjectDisposedException or a double-free back into the pool.
            // Collect + clear the maps under the lock, then Dispose() each queue OUTSIDE the lock (Dispose
            // JOINS the consumer, bounded to 5s) so producers aren't blocked for the whole join, and only
            // AFTER every consumer is joined do we dispose the books.
            var queuesToJoin = new List<IDisposable>();
            lock (_buffersLock)
            {
                foreach (var q in _eventBuffers.Values)
                {
                    q?.PauseConsumer();
                    if (q != null) queuesToJoin.Add(q);
                }
                _eventBuffers.Clear();

                foreach (var q in _tradesBuffers.Values)
                {
                    q?.PauseConsumer();
                    if (q != null) queuesToJoin.Add(q);
                }
                _tradesBuffers.Clear();
            }
            foreach (var q in queuesToJoin)
                q.Dispose(); // joins the consumer thread — guarantees no in-flight book mutation remains

            deltaSubscriptions.Clear();
            tradesSubscriptions.Clear();

            tradePool.Dispose();
            tradePool = new CustomObjectPool<Trade>();

            //CLEAR LOB — safe now: every consumer has been joined above.
            foreach (var lob in _localOrderBooks)
            {
                lob.Value?.Dispose();
            }
            _localOrderBooks.Clear();
            _decimalBooks.Clear();
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
                                HelperCustomQueue<(string symbol, KrakenTradeUpdate trade)> buffer;
                                lock (_buffersLock)
                                {
                                    if (!_tradesBuffers.TryGetValue(_normalizedSymbol, out buffer))
                                        return; // Buffer was cleared during reconnection
                                }

                                foreach (var item in trade.Data)
                                {
                                    // ACC-3: preserve the exchange's trade execution timestamp; do NOT overwrite it
                                    // with the local socket ReceiveTime (that destroys latency/replay fidelity).
                                    buffer.Add((_normalizedSymbol, item));
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
                                // ACC-4/CONC-4: route BOTH snapshot and update frames through the same
                                // single-consumer queue, so the book is seeded then mutated strictly in order
                                // on ONE thread (no WS-callback-vs-consumer race, no inline snapshot swap).
                                // PERF-5: value-tuple payload — no per-frame heap Tuple allocation.
                                bool isSnapshot = data.UpdateType != SocketUpdateType.Update;
                                var receiveLocal = data.ReceiveTime.ToLocalTime(); // computed once per frame
                                if (!isSnapshot)
                                    CheckFrameFreshnessAndWarn(receiveLocal);

                                HelperCustomQueue<(DateTime ts, string symbol, KrakenBookUpdate data, bool isSnapshot)> buffer;
                                lock (_buffersLock)
                                {
                                    if (!_eventBuffers.TryGetValue(normalizedSymbol, out buffer))
                                        return; // Buffer was cleared during reconnection
                                }

                                buffer.Add((receiveLocal, normalizedSymbol, data.Data, isSnapshot));
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
                _localOrderBooks.TryAdd(normalizedSymbol, null);
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

        private void eventBuffers_onReadAction((DateTime ts, string symbol, KrakenBookUpdate data, bool isSnapshot) e)
        {
            if (e.isSnapshot)
                ApplySnapshot(e.data, e.symbol, e.ts);
            else
                UpdateOrderBook(e.data, e.symbol, e.ts);
        }
        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";

            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }
        private void tradesBuffers_onReadAction((string symbol, KrakenTradeUpdate trade) item)
        {
            var trade = tradePool.Get();
            trade.Price = item.Item2.Price;
            trade.Size = Math.Abs(item.Item2.Quantity);
            trade.Symbol = item.Item1;
            trade.Timestamp = item.Item2.Timestamp.ToLocalTime();
            trade.ProviderId = _settings.Provider.ProviderID;
            trade.ProviderName = _settings.Provider.ProviderName;
            trade.IsBuy = item.Item2.Side == OrderSide.Buy;
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
        private VisualHFT.Model.OrderBook ToOrderBookModel(KrakenBookUpdate data, string symbol)
        {
            KrakenOrderBook snapshot = new KrakenOrderBook();
            //transform KrakenBookUpdate to KrakenOrderBook
            snapshot.Asks = data.Asks.Select(x => new KrakenOrderBookEntry() { Price = x.Price, Quantity = x.Quantity, Timestamp = DateTime.Now }).ToArray();
            snapshot.Bids = data.Bids.Select(x => new KrakenOrderBookEntry() { Price = x.Price, Quantity = x.Quantity, Timestamp = DateTime.Now }).ToArray();


            return ToOrderBookModel(snapshot, symbol);
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
        // Live snapshot handler. Runs on the single book-consumer thread, strictly in order with deltas
        // (ACC-4/CONC-4), so the display book and the decimal ladder are always built before any delta.
        private void ApplySnapshot(KrakenBookUpdate data, string symbol, DateTime ts)
        {
            var lob = ToOrderBookModel(data, symbol);
            lob.LastUpdated = ts;
            _localOrderBooks[symbol] = lob; // ConcurrentDictionary: atomic publish of the fresh book

            // Seed the decimal ladder from the RAW exchange decimals (used for the CRC32 integrity check).
            // Pass the symbol precision so the checksum reconstructs wire trailing zeros even if the library
            // deserializer dropped them (lob.PriceDecimalPlaces/SizeDecimalPlaces were just derived from this snapshot).
            var ladder = _decimalBooks.GetOrAdd(symbol, static _ => new KrakenDecimalBook());
            // Pass the subscribed depth so the ladder stays trimmed to exactly what Kraken retains (it drops
            // out-of-window levels without a qty=0 delete); otherwise ghost levels accumulate and break the CRC32.
            ladder.Reset(ToLevels(data.Asks), ToLevels(data.Bids), lob.PriceDecimalPlaces, lob.SizeDecimalPlaces, _settings.DepthLevels);

            RaiseOnDataReceived(lob);
        }

        private void UpdateOrderBook(KrakenBookUpdate lob_update, string symbol, DateTime ts)
        {
            if (!_localOrderBooks.TryGetValue(symbol, out VisualHFT.Model.OrderBook? local_lob) || local_lob == null)
                return;

            // ACC-1/ACC-2: when integrity validation is enabled, mirror each delta into the decimal ladder in
            // the SAME pass that updates the display book — zero extra allocation (no ToLevels iterator, no
            // second enumeration). Kraken provides NO sequence numbers, so the CRC32 is the only desync detector.
            // The offline test-injection seam never seeds the ladder, so this is inert there.
            KrakenDecimalBook? ladder = null;
            bool validate = _checksumMode != ChecksumValidationMode.Off
                            && _decimalBooks.TryGetValue(symbol, out ladder)
                            && ladder.IsSeeded;

            var now = DateTime.Now; // PERF-4: one wall-clock read per frame, reused across every level
            try
            {
                foreach (var item in lob_update.Bids)
                {
                    if (item.Quantity == 0 && item.Price == 0 || item.Quantity < 0)
                        continue;
                    // PERF-1: allocation-free primitive overloads (no per-level DeltaBookItem), mirroring KuCoin.
                    if (item.Quantity != 0)
                        local_lob.AddOrUpdateLevel(true, string.Empty, (double)item.Price, (double)item.Quantity, now, ts);
                    else
                        local_lob.DeleteLevel(true, string.Empty, (double)item.Price, (double)item.Quantity);
                    if (validate)
                        ladder!.ApplyBid(item.Price, item.Quantity);
                }
                foreach (var item in lob_update.Asks)
                {
                    if (item.Quantity == 0 && item.Price == 0 || item.Quantity < 0)
                        continue;
                    if (item.Quantity != 0)
                        local_lob.AddOrUpdateLevel(false, string.Empty, (double)item.Price, (double)item.Quantity, now, ts);
                    else
                        local_lob.DeleteLevel(false, string.Empty, (double)item.Price, (double)item.Quantity);
                    if (validate)
                        ladder!.ApplyAsk(item.Price, item.Quantity);
                }
            }
            catch (Exception e)
            {
                LogException(e, $"Error updating LOB for {symbol}.");
                throw;
            }

            if (validate)
            {
                // Kraken drops out-of-window levels without a qty=0 delete, so trim the ladder to the subscribed
                // depth every frame (once, after the whole frame is applied) — exactly what Kraken retains.
                ladder!.TrimToDepth();

                if (HasChecksum(lob_update))
                {
                    uint localChecksum = ladder.ComputeChecksum();
                    uint exchangeChecksum = unchecked((uint)lob_update.Checksum);
                    if (localChecksum != exchangeChecksum)
                    {
                        if (_checksumMode == ChecksumValidationMode.Enforce)
                        {
                            // Do NOT publish a corrupted book — drop it and resync via the bounded reconnect path.
                            var _error = $"Order book checksum mismatch for {symbol} (local={localChecksum}, exchange={exchangeChecksum}). Resyncing.";
                            log.Warn(_error);
                            ladder.Clear(); // stop re-triggering for this symbol until a fresh snapshot reseeds it
                            Task.Run(async () => await HandleConnectionLost(_error));
                            return;
                        }

                        // LogOnly soak: surface the divergence but keep the feed running (no reconnect). A clean
                        // soak (zero of these lines over a long volatile session) gates switching to Enforce.
                        log.Warn($"{this.Name}: checksum mismatch (LOG-ONLY, feed continues) for {symbol} (local={localChecksum}, exchange={exchangeChecksum}).");
                    }
                }
            }

            local_lob.LastUpdated = ts;
            RaiseOnDataReceived(local_lob);
        }

        // Projects Kraken's raw (decimal) book entries into (price, quantity) pairs for the integrity ladder,
        // preserving the wire scale the CRC32 depends on (no double round-trip).
        private static IEnumerable<KeyValuePair<decimal, decimal>> ToLevels(IEnumerable<KrakenBookUpdateEntry> entries)
        {
            if (entries == null)
                yield break;
            foreach (var e in entries)
                yield return new KeyValuePair<decimal, decimal>(e.Price, e.Quantity);
        }

        // Kraken omits/zeroes the checksum only when it is absent; the offline test seam also leaves it 0.
        private static bool HasChecksum(KrakenBookUpdate data) => unchecked((uint)data.Checksum) != 0u;

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
                    
                    // LIFE-4: wait (bounded) for the unsubscribe to actually complete before disposing the
                    // socket client, instead of fire-and-forget which abandons the unsubscribe. Mirrors KuCoin.
                    try
                    {
                        _socketClient?.UnsubscribeAllAsync().Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception) { /* best-effort during dispose */ }
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

                    foreach (var lob in _localOrderBooks)
                        lob.Value?.Dispose();
                    _localOrderBooks.Clear();
                    _decimalBooks.Clear();

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


        // FOR UNIT TESTING PURPOSES: simulate a connection interruption + recovery, fully offline.
        // Drives the REAL reconnect teardown (ClearAsync, which clears _localOrderBooks) then reseeds
        // via InjectSnapshot. Order MUST be Clear-then-Inject (Clear disposes books) — the same
        // teardown+reseed pair a live reconnect runs — so a test can assert the reconnect leaves a
        // FRESH book, not a stale one (see ReconnectionReseedTests).
        public async Task SimulateConnectionInterruption(VisualHFT.Model.OrderBook reseedSnapshot)
        {
            if (reseedSnapshot == null)
                throw new ArgumentNullException(nameof(reseedSnapshot));

            await ClearAsync();
            InjectSnapshot(reseedSnapshot, reseedSnapshot.Sequence);
            Status = ePluginStatus.STARTED;
        }

        //FOR UNIT TESTING PURPOSES
        public void InjectSnapshot(VisualHFT.Model.OrderBook snapshotModel, long sequence)
        {
            var localModel = new KrakenOrderBook();
            localModel.Bids = snapshotModel.Bids.Select(x => new KrakenOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal(), Timestamp = x.LocalTimeStamp}).ToArray();
            localModel.Asks = snapshotModel.Asks.Select(x => new KrakenOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal(), Timestamp = x.LocalTimeStamp}).ToArray();
            _settings.DepthLevels = snapshotModel.MaxDepth; //force depth received

            var symbol = snapshotModel.Symbol;

            _localOrderBooks[symbol] = ToOrderBookModel(localModel, symbol); // ConcurrentDictionary indexer: add-or-replace
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

