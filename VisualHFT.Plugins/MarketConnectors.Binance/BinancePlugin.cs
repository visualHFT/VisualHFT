using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using MarketConnectors.Binance.Model;
#if WINDOWS
using MarketConnectors.Binance.ViewModel;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoExchange.Net.Objects.Sockets;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Commons.Pools;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
#if WINDOWS
using MarketConnectors.Binance.UserControls;
#endif
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Spot.Socket;
using Binance.Net.Objects.Models;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Collections.Concurrent;

namespace MarketConnectors.Binance
{

    public class BinancePlugin : BasePluginDataRetriever
    {
        private bool _disposed = false;
        private readonly SemaphoreSlim _startStopLock = new SemaphoreSlim(1, 1);
        private bool isReconnecting = false;

        private PlugInSettings _settings;
        private BinanceSocketClient _socketClient;
        private BinanceRestClient _restClient;
        
        // ✅ FIX: Use ConcurrentDictionary for thread safety
        internal readonly ConcurrentDictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks = 
            new ConcurrentDictionary<string, VisualHFT.Model.OrderBook>();
        
        private HelperCustomQueue<IBinanceEventOrderBook> _eventBuffers;
        private HelperCustomQueue<IBinanceTrade> _tradesBuffers;
        private int pingFailedAttempts = 0;
        private System.Timers.Timer _timerPing;

        private WebSocketResult<UpdateSubscription> deltaSubscription;
        private WebSocketResult<UpdateSubscription> tradesSubscription;
        private WebSocketResult<UpdateSubscription> futuresDeltaSubscription;
        private WebSocketResult<UpdateSubscription> futuresTradesSubscription;
        private BinanceSocketClient _futuresSocketClient;
        private BinanceRestClient _futuresRestClient;
        private HelperCustomQueue<IBinanceFuturesEventOrderBook> _futuresEventBuffers;
        private HelperCustomQueue<BinanceStreamAggregatedTrade> _futuresTradesBuffers;
        private Dictionary<string, string> _futuresNormalized = new Dictionary<string, string>();
        private readonly ConcurrentDictionary<string, byte> _futuresSynced = new ConcurrentDictionary<string, byte>();

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly CustomObjectPool<VisualHFT.Model.Trade> tradePool = new CustomObjectPool<VisualHFT.Model.Trade>();

        // ✅ FIX: Use ConcurrentDictionary for thread safety
        private readonly ConcurrentDictionary<string, VisualHFT.Model.Order> _localUserOrders = 
            new ConcurrentDictionary<string, VisualHFT.Model.Order>();

        public override string Name { get; set; } = "Binance Plugin";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Connects to Binance websockets.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action CloseSettingWindow { get; set; }

        public BinancePlugin()
        {
            SetReconnectionAction(InternalStartAsync);
            log.Info($"{this.Name} has been loaded.");
        }
        ~BinancePlugin()
        {
            Dispose(false);
        }



        public override async Task StartAsync()
        {
            await base.StartAsync();//call the base first


            try
            {
                await InternalStartAsync();

            }
            catch (Exception ex)
            {
                var _error = ex.Message;
                LogException(ex, null, _error.IndexOf("[CantConnectError]") > -1);

                if (_error.IndexOf("[CantConnectError]") > -1)
                {
                    Status = ePluginStatus.STOPPED_FAILED;
                    await ClearAsync();
                    RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
                    RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED_FAILED));
                }
                else
                {
                    await HandleConnectionLost(_error, ex);

                }
            }
        }
        private async Task InternalStartAsync()
        {
            // ✅ FIX: Add synchronization
            await _startStopLock.WaitAsync();
            try
            {
                await ClearAsync();
                LoadSymbolMaps();
                await SetupClientsAsync();

                _tradesBuffers = new HelperCustomQueue<IBinanceTrade>($"<IBinanceTrade>_{this.Name}", tradesBuffers_onReadAction, tradesBuffers_onErrorAction);
                _eventBuffers = new HelperCustomQueue<IBinanceEventOrderBook>($"<IBinanceEventOrderBook>_{this.Name}", eventBuffers_onReadAction, eventBuffers_onErrorAction);
                _futuresTradesBuffers = new HelperCustomQueue<BinanceStreamAggregatedTrade>($"<IBinanceTradeFutures>_{this.Name}", futuresTrades_onReadAction, tradesBuffers_onErrorAction);
                _futuresEventBuffers = new HelperCustomQueue<IBinanceFuturesEventOrderBook>($"<IBinanceFuturesEventOrderBook>_{this.Name}", futuresBuffers_onReadAction, eventBuffers_onErrorAction);

                _eventBuffers.PauseConsumer();
                _futuresEventBuffers.PauseConsumer();

                await InitializeDeltasAsync();
                await InitializeFuturesDeltasAsync();
                await InitializeSnapshotsAsync();
                await InitializeTradesAsync();
                await InitializeFuturesTradesAsync();
                await InitializePingTimerAsync();
                await InitializeUserPrivateOrders();

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
            UnattachEventHandlers(futuresDeltaSubscription?.Data);
            UnattachEventHandlers(futuresTradesSubscription?.Data);
            if (_socketClient != null)
                await _socketClient.UnsubscribeAllAsync();
            if (_futuresSocketClient != null)
                await _futuresSocketClient.UnsubscribeAllAsync();
            if (deltaSubscription != null && deltaSubscription.Data != null)
                await deltaSubscription.Data.CloseAsync();
            if (tradesSubscription != null && tradesSubscription.Data != null)
                await tradesSubscription.Data.CloseAsync();
            if (futuresDeltaSubscription != null && futuresDeltaSubscription.Data != null)
                await futuresDeltaSubscription.Data.CloseAsync();
            if (futuresTradesSubscription != null && futuresTradesSubscription.Data != null)
                await futuresTradesSubscription.Data.CloseAsync();
            futuresDeltaSubscription = null;
            futuresTradesSubscription = null;
            _futuresSocketClient?.Dispose();
            _futuresRestClient?.Dispose();
            _futuresSocketClient = null;
            _futuresRestClient = null;
            _futuresSynced.Clear();
            _timerPing?.Stop();
            _timerPing?.Dispose();

            // ✅ FIX: Dispose queues properly
            try
            {
                _eventBuffers?.Stop();
                _eventBuffers?.Dispose();
            }
            catch (Exception ex)
            {
                log.Debug($"Error disposing event buffer: {ex.Message}");
            }

            try
            {
                _tradesBuffers?.Stop();
                _tradesBuffers?.Dispose();
            }
            catch (Exception ex)
            {
                log.Debug($"Error disposing trades buffer: {ex.Message}");
            }

            try
            {
                _futuresEventBuffers?.Stop();
                _futuresEventBuffers?.Dispose();
            }
            catch (Exception ex)
            {
                log.Debug($"Error disposing futures event buffer: {ex.Message}");
            }

            try
            {
                _futuresTradesBuffers?.Stop();
                _futuresTradesBuffers?.Dispose();
            }
            catch (Exception ex)
            {
                log.Debug($"Error disposing futures trades buffer: {ex.Message}");
            }

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

        private async Task SetupClientsAsync()
        {
            _socketClient?.Dispose();
            _restClient?.Dispose();

            _socketClient = new BinanceSocketClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new BinanceCredentials(_settings.ApiKey, _settings.ApiSecret);
                if (_settings.IsNonUS)
                    options.Environment = BinanceEnvironment.Live;
                else
                    options.Environment = BinanceEnvironment.Us;
            });

            _restClient = new BinanceRestClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new BinanceCredentials(_settings.ApiKey, _settings.ApiSecret);
                if (_settings.IsNonUS)
                    options.Environment = BinanceEnvironment.Live;
                else
                    options.Environment = BinanceEnvironment.Us;
            });

            _futuresSocketClient?.Dispose();
            _futuresRestClient?.Dispose();
            _futuresSocketClient = null;
            _futuresRestClient = null;
            if (_futuresNormalized.Count == 0)
                return;

            // USD-M perpetuals exist on Binance.com only. Binance.US has no fapi host.
            _futuresSocketClient = new BinanceSocketClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new BinanceCredentials(_settings.ApiKey, _settings.ApiSecret);
                options.Environment = BinanceEnvironment.Live;
            });
            _futuresRestClient = new BinanceRestClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new BinanceCredentials(_settings.ApiKey, _settings.ApiSecret);
                options.Environment = BinanceEnvironment.Live;
            });
        }
        private async Task InitializeTradesAsync()
        {
            if (GetAllNonNormalizedSymbols().Count == 0)
                return;
            log.Info($"{this.Name}: sending WS Trades Subscription to all symbols ");
            tradesSubscription = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(
                GetAllNonNormalizedSymbols(),
                trade =>
                {
                    // Buffer the trades
                    if (trade.Data != null)
                    {
                        try
                        {
                            _tradesBuffers.Add(trade.Data);
                        }
                        catch (Exception ex)
                        {
                            string _normalizedSymbol = "(null)";
                            if (trade != null && trade.Data != null)
                                _normalizedSymbol = GetNormalizedSymbol(trade.Data.Symbol);

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
                var _error = $"Unsuccessful trades subscription error: {tradesSubscription.Error}";
                throw new Exception(_error);
            }
        }
        private async Task InitializeFuturesTradesAsync()
        {
            if (_futuresSocketClient == null || _futuresNormalized.Count == 0)
                return;
            log.Info($"{this.Name}: sending USD-M futures aggregated trade subscription.");
            futuresTradesSubscription = await _futuresSocketClient.UsdFuturesApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync(
                _futuresNormalized.Keys,
                trade =>
                {
                    if (trade.Data == null)
                        return;
                    try
                    {
                        _futuresTradesBuffers.Add(trade.Data);
                    }
                    catch (Exception ex)
                    {
                        var normalizedSymbol = NormalizeFutures(trade.Data.Symbol);
                        var error = $"Will reconnect. Unhandled error while receiving futures trades for {normalizedSymbol}.";
                        LogException(ex, error);
                        Task.Run(async () => await HandleConnectionLost(error, ex));
                    }
                });
            if (futuresTradesSubscription.Success)
                AttachEventHandlers(futuresTradesSubscription.Data);
            else
                throw new Exception($"Unsuccessful futures trades subscription error: {futuresTradesSubscription.Error}");
        }
        private async Task InitializeFuturesDeltasAsync()
        {
            if (_futuresSocketClient == null || _futuresNormalized.Count == 0)
                return;
            log.Info($"{this.Name}: sending USD-M futures depth subscription for {string.Join(",", _futuresNormalized.Keys)}.");
            futuresDeltaSubscription = await _futuresSocketClient.UsdFuturesApi.ExchangeData.SubscribeToOrderBookUpdatesAsync(
                _futuresNormalized.Keys,
                BinanceFuturesFeed.UpdateInterval(_settings.UpdateIntervalMs),
                data =>
                {
                    if (data.Data == null)
                        return;
                    try
                    {
                        if (Math.Abs(DateTime.Now.Subtract(data.ReceiveTime.ToLocalTime()).TotalSeconds) > 1)
                        {
                            var message = $"Futures rates are coming late at {Math.Abs(DateTime.Now.Subtract(data.ReceiveTime.ToLocalTime()).TotalSeconds)} seconds.";
                            log.Warn(message);
                            HelperNotificationManager.Instance.AddNotification(this.Name, message, HelprNorificationManagerTypes.WARNING, HelprNorificationManagerCategories.PLUGINS);
                        }
                        _futuresEventBuffers.Add(data.Data);
                    }
                    catch (Exception ex)
                    {
                        var normalizedSymbol = NormalizeFutures(data.Data.Symbol);
                        var error = $"Will reconnect. Unhandled error while receiving futures depth for {normalizedSymbol}.";
                        LogException(ex, error);
                        Task.Run(async () => await HandleConnectionLost(error, ex));
                    }
                },
                CancellationToken.None);
            if (futuresDeltaSubscription.Success)
                AttachEventHandlers(futuresDeltaSubscription.Data);
            else
                throw new Exception($"Unsuccessful futures deltas subscription error: {futuresDeltaSubscription.Error}");
        }
        private async Task InitializeDeltasAsync()
        {
            if (GetAllNonNormalizedSymbols().Count == 0)
                return;
            // *************** PARTIAL OR NOT???????
            // Subscribe to updates of a symbol
            //_socketClient.SpotApi.ExchangeData.SubscribeToOrderBookUpdatesAsync
            //_socketClient.SpotApi.ExchangeData.SubscribeToPartialOrderBookUpdatesAsync (looks like this is snapshots)
            log.Info($"{this.Name}: sending WS Trades Subscription to all symbols.");
            deltaSubscription = await _socketClient.SpotApi.ExchangeData.SubscribeToOrderBookUpdatesAsync(
                GetAllNonNormalizedSymbols(),
                _settings.UpdateIntervalMs,
                data =>
                {
                    // Buffer the events
                    if (data.Data != null)
                    {
                        try
                        {
                            // EventTime is the venue's own event timestamp ("E") — never overwrite it.
                            // The freshness warn keys on RECEIVE time: it measures in-process dispatch
                            // delay, and must not fire on venue/local clock offset.
                            if (Math.Abs(DateTime.Now.Subtract(data.ReceiveTime.ToLocalTime()).TotalSeconds) > 1)
                            {
                                var _msg = $"Rates are coming late at {Math.Abs(DateTime.Now.Subtract(data.ReceiveTime.ToLocalTime()).TotalSeconds)} seconds.";
                                log.Warn(_msg);
                                HelperNotificationManager.Instance.AddNotification(this.Name, _msg, HelprNorificationManagerTypes.WARNING, HelprNorificationManagerCategories.PLUGINS);
                            }
                            _eventBuffers.Add(data.Data);
                        }
                        catch (Exception ex)
                        {
                            string _normalizedSymbol = "(null)";
                            if (data != null && data.Data != null)
                                _normalizedSymbol = GetNormalizedSymbol(data.Data.Symbol);


                            var _error = $"Will reconnect. Unhandled error while receiving delta market data for {_normalizedSymbol}.";
                            LogException(ex, _error);
                            Task.Run(async () => await HandleConnectionLost(_error, ex));
                        }
                    }
                }, new CancellationToken());
            if (deltaSubscription.Success)
            {
                _eventBuffers.PauseConsumer(); //wait for the snapshots
                AttachEventHandlers(deltaSubscription.Data);
            }
            else
            {
                var _error = $"Unsuccessful deltas subscription error: {deltaSubscription.Error}";
                throw new Exception(_error);
            }
        }
        private async Task InitializeSnapshotsAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);
                
                // ✅ FIX: Use TryAdd for thread safety
                _localOrderBooks.TryAdd(normalizedSymbol, null);

                log.Info($"{this.Name}: Getting snapshot {normalizedSymbol} level 2");

                var depthSnapshot = await _restClient.SpotApi.ExchangeData.GetOrderBookAsync(symbol, _settings.DepthLevels);
                if (depthSnapshot.Success)
                {
                    _localOrderBooks[normalizedSymbol] = ToOrderBookModel(depthSnapshot.Data);
                }
                else
                {
                    var _error = $"Unsuccessful snapshot request for {normalizedSymbol} error: {depthSnapshot.ResponseStatusCode} - {depthSnapshot.Error}";
                    throw new Exception(_error);
                }
            }
            _eventBuffers.ResumeConsumer();

            if (_futuresRestClient == null)
            {
                _futuresEventBuffers?.ResumeConsumer();
                return;
            }

            var futuresDepth = BinanceFuturesFeed.DepthLimit(_settings.DepthLevels);
            foreach (var symbol in _futuresNormalized.Keys)
            {
                var normalizedSymbol = NormalizeFutures(symbol);
                _localOrderBooks.TryAdd(normalizedSymbol, null);
                _futuresSynced.TryRemove(normalizedSymbol, out _);
                log.Info($"{this.Name}: Getting USD-M futures snapshot {normalizedSymbol} level 2");
                var depthSnapshot = await _futuresRestClient.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, futuresDepth);
                if (depthSnapshot.Success)
                {
                    var futuresBook = ToOrderBookModel(depthSnapshot.Data, normalizedSymbol);
                    _localOrderBooks[normalizedSymbol] = futuresBook;
                    RaiseOnDataReceived(futuresBook);
                }
                else
                    throw new Exception($"Unsuccessful futures snapshot request for {normalizedSymbol} error: {depthSnapshot.ResponseStatusCode} - {depthSnapshot.Error}");
            }
            _futuresEventBuffers.ResumeConsumer();
        }
        private async Task InitializeUserPrivateOrders()
        {
            if (!string.IsNullOrEmpty(this._settings.ApiKey) && !string.IsNullOrEmpty(this._settings.ApiSecret))
            {
                var openOrders = await _socketClient.SpotApi.Trading.GetOpenOrdersAsync();
                if (openOrders != null && openOrders.Success)
                {
                    if(openOrders==null || openOrders.Data==null || openOrders.Data.Result==null || openOrders.Data.Result.Count()==0)
                    {

                    }
                    else
                    {
                        foreach (BinanceOrder item in openOrders.Data.Result)
                        {
                            InitialUserOrders(item);
                        }
                    }

                }
                // Subscribe to user data updates - authentication is handled automatically via websocket  
                // Note: The new API no longer requires listen keys
                var subscription = await _socketClient.SpotApi.Account.SubscribeToUserDataUpdatesAsync(
                    onOrderUpdateMessage: (orderUpdate) =>
                    { 
                        log.Info(orderUpdate.Data);
                        if (orderUpdate.Data != null)
                        {
                            UpdateUserOrderBook(orderUpdate.Data);
                        }
                    });
            }
        }

        private void InitialUserOrders(BinanceOrder item)
        {
            // ✅ FIX: Use GetOrAdd for thread-safe order creation
            var localuserOrder = _localUserOrders.GetOrAdd(item.ClientOrderId, _ =>
            {
                var order = new VisualHFT.Model.Order
                {
                    OrderID = item.Id,
                    ClOrdId = !string.IsNullOrEmpty(item.ClientOrderId) ? item.ClientOrderId : item.Id.ToString(),
                    Currency = GetNormalizedSymbol(item.Symbol),
                    CreationTimeStamp = item.CreateTime,
                    ProviderId = _settings!.Provider.ProviderID,
                    ProviderName = _settings.Provider.ProviderName,
                    Quantity = (double)item.Quantity,
                    PricePlaced = (double)item.Price,
                    Symbol = GetNormalizedSymbol(item.Symbol),
                    TimeInForce = eORDERTIMEINFORCE.GTC
                };

                if (item.TimeInForce == TimeInForce.ImmediateOrCancel)
                    order.TimeInForce = eORDERTIMEINFORCE.IOC;
                else if (item.TimeInForce == TimeInForce.FillOrKill)
                    order.TimeInForce = eORDERTIMEINFORCE.FOK;

                return order;
            });

            if (item.Type == SpotOrderType.Market)
                localuserOrder.OrderType = eORDERTYPE.MARKET;
            else if (item.Type == SpotOrderType.LimitMaker || item.Type == SpotOrderType.Limit)
                localuserOrder.OrderType = eORDERTYPE.LIMIT;
            else
                localuserOrder.OrderType = eORDERTYPE.PEGGED;

            if (item.Side == OrderSide.Buy)
                localuserOrder.Side = eORDERSIDE.Buy;
            if (item.Side == OrderSide.Sell)
                localuserOrder.Side = eORDERSIDE.Sell;

            if (item.Status == OrderStatus.New || item.Status == OrderStatus.PendingNew)
            {
                if (item.Side == OrderSide.Buy)
                {
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.PricePlaced = (double)item.Price;
                    localuserOrder.BestBid = (double)item.Price;
                }
                if (item.Side == OrderSide.Sell)
                {
                    localuserOrder.BestAsk = (double)item.Price;
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.Quantity = (double)item.Quantity;
                }
                localuserOrder.Status = eORDERSTATUS.NEW;
            }
            else if (item.Status == OrderStatus.Filled)
            {
                localuserOrder.BestAsk = (double)item.Price;
                localuserOrder.BestBid = (double)item.Price;
                localuserOrder.FilledQuantity = (double)(item.QuantityFilled);
                localuserOrder.Status = eORDERSTATUS.FILLED;
            }
            else if (item.Status == OrderStatus.Canceled)
                localuserOrder.Status = eORDERSTATUS.CANCELED;
            else if (item.Status == OrderStatus.Rejected)
                localuserOrder.Status = eORDERSTATUS.REJECTED;
            else if (item.Status == OrderStatus.PartiallyFilled)
            {
                localuserOrder.BestAsk = (double)item.Price;
                localuserOrder.BestBid = (double)item.Price;
                localuserOrder.Status = eORDERSTATUS.PARTIALFILLED;
            }
            else if (item.Status == OrderStatus.PendingCancel)
                localuserOrder.Status = eORDERSTATUS.CANCELEDSENT;

            localuserOrder.LastUpdated = DateTime.Now;
            
            if (!string.IsNullOrEmpty(item.OriginalClientOrderId) && item.OriginalClientOrderId != item.ClientOrderId)
            {
                if (_localUserOrders.TryGetValue(item.OriginalClientOrderId, out var originalOrder))
                    originalOrder.Status = localuserOrder.Status;
            }

            RaiseOnDataReceived(localuserOrder);
        }

        private void UpdateUserOrderBook(BinanceStreamOrderUpdate item)
        {
            // ✅ FIX: Use GetOrAdd for thread-safe order creation
            var localuserOrder = _localUserOrders.GetOrAdd(item.ClientOrderId, _ =>
            {
                var order = new VisualHFT.Model.Order
                {
                    OrderID = item.Id,
                    ClOrdId = !string.IsNullOrEmpty(item.ClientOrderId) ? item.ClientOrderId : item.Id.ToString(),
                    Currency = GetNormalizedSymbol(item.Symbol),
                    CreationTimeStamp = item.CreateTime,
                    ProviderId = _settings!.Provider.ProviderID,
                    ProviderName = _settings.Provider.ProviderName,
                    Quantity = (double)item.Quantity,
                    PricePlaced = (double)item.Price,
                    Symbol = GetNormalizedSymbol(item.Symbol),
                    TimeInForce = eORDERTIMEINFORCE.GTC
                };

                if (item.TimeInForce == TimeInForce.ImmediateOrCancel)
                    order.TimeInForce = eORDERTIMEINFORCE.IOC;
                else if (item.TimeInForce == TimeInForce.FillOrKill)
                    order.TimeInForce = eORDERTIMEINFORCE.FOK;

                return order;
            });

            if (item.Type == SpotOrderType.Market)
                localuserOrder.OrderType = eORDERTYPE.MARKET;
            else
                localuserOrder.OrderType = eORDERTYPE.LIMIT;

            if (item.Side == OrderSide.Buy)
                localuserOrder.Side = eORDERSIDE.Buy;
            if (item.Side == OrderSide.Sell)
                localuserOrder.Side = eORDERSIDE.Sell;

            if (item.Status == OrderStatus.New || item.Status == OrderStatus.PendingNew)
            {
                if (item.Side == OrderSide.Buy)
                {
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.PricePlaced = (double)item.Price;
                    localuserOrder.BestBid = (double)item.Price;
                }
                if (item.Side == OrderSide.Sell)
                {
                    localuserOrder.BestAsk = (double)item.Price;
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.Quantity = (double)item.Quantity;
                }
                localuserOrder.Status = eORDERSTATUS.NEW;
            }
            else if (item.Status == OrderStatus.Filled)
            {
                localuserOrder.BestAsk = (double)item.Price;
                localuserOrder.BestBid = (double)item.Price;
                localuserOrder.FilledQuantity = (double)(item.QuantityFilled);
                localuserOrder.Status = eORDERSTATUS.FILLED;
            }
            else if (item.Status == OrderStatus.Canceled)
                localuserOrder.Status = eORDERSTATUS.CANCELED;
            else if (item.Status == OrderStatus.Rejected)
                localuserOrder.Status = eORDERSTATUS.REJECTED;
            else if (item.Status == OrderStatus.PartiallyFilled)
            {
                localuserOrder.BestAsk = (double)item.Price;
                localuserOrder.BestBid = (double)item.Price;
                localuserOrder.Status = eORDERSTATUS.PARTIALFILLED;
            }
            else if (item.Status == OrderStatus.PendingCancel)
                localuserOrder.Status = eORDERSTATUS.CANCELEDSENT;

            localuserOrder.LastUpdated = DateTime.Now;
            
            if (!string.IsNullOrEmpty(item.OriginalClientOrderId) && item.OriginalClientOrderId != item.ClientOrderId)
            {
                if (_localUserOrders.TryGetValue(item.OriginalClientOrderId, out var originalOrder))
                    originalOrder.Status = localuserOrder.Status;
            }

            RaiseOnDataReceived(localuserOrder);
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


        private void eventBuffers_onReadAction(IBinanceEventOrderBook eventData)
        {
            var symbol = GetNormalizedSymbol(eventData.Symbol);
            UpdateOrderBook(eventData, symbol);

        }
        private void futuresBuffers_onReadAction(IBinanceFuturesEventOrderBook eventData)
        {
            var symbol = NormalizeFutures(eventData.Symbol);
            if (!_localOrderBooks.TryGetValue(symbol, out var book) || book == null)
                return;

            var synced = _futuresSynced.ContainsKey(symbol);
            var firstUpdateId = eventData.FirstUpdateId ?? long.MaxValue;
            var step = BinanceFuturesFeed.Decide(book.Sequence, firstUpdateId, eventData.LastUpdateId, eventData.LastUpdateIdStream, synced);
            if (step == BinanceFuturesFeed.BookStep.Drop)
                return;
            if (step == BinanceFuturesFeed.BookStep.Gap)
                throw new Exception("Detected sequence gap.");

            ApplyBookDelta(eventData, symbol, book);
            _futuresSynced.TryAdd(symbol, 0);
        }
        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";

            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }

        internal void tradesBuffers_onReadAction(IBinanceTrade eventData)
        {
            PublishTrade(GetNormalizedSymbol(eventData.Symbol), eventData.Price, eventData.Quantity, eventData.TradeTime, eventData.BuyerIsMaker);
        }
        private void futuresTrades_onReadAction(BinanceStreamAggregatedTrade eventData)
        {
            PublishTrade(NormalizeFutures(eventData.Symbol), eventData.Price, eventData.Quantity, eventData.TradeTime, eventData.BuyerIsMaker);
        }
        private void PublishTrade(string symbol, decimal price, decimal quantity, DateTime tradeTime, bool buyerIsMaker)
        {
            var trade = tradePool.Get();
            trade.Price = price;
            trade.Size = quantity;
            trade.Symbol = symbol;
            trade.Timestamp = tradeTime.ToLocalTime();
            trade.ProviderId = _settings.Provider.ProviderID;
            trade.ProviderName = _settings.Provider.ProviderName;
            trade.IsBuy = !buyerIsMaker;
            trade.MarketMidPrice = _localOrderBooks.TryGetValue(symbol, out var book) && book != null
                ? book.MidPrice
                : 0;

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
            throw new NotImplementedException();
        }
        private void deltaSubscription_ActivityPaused()
        {
            throw new NotImplementedException();
        }
        private void deltaSubscription_ConnectionRestored(TimeSpan obj)
        {
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

        // ✅ FIX: Thread-safe UpdateOrderBook with TryGetValue
        /// <summary>
        /// The single decision point for the venue's book timestamp. Binance's diff-depth
        /// stream carries the exchange event time (wire field "E"); return it in local
        /// kind. A frame without it returns null — a default(DateTime) stamp would
        /// fabricate a colossal latency spike, and receive time must never masquerade
        /// as exchange time.
        /// </summary>
        public static DateTime? ResolveBookTimestamp(IBinanceEventOrderBook lob_update)
        {
            if (lob_update == null || lob_update.EventTime == default)
                return null;
            return lob_update.EventTime.ToLocalTime();
        }

        private void UpdateOrderBook(IBinanceEventOrderBook lob_update, string normalizedSymbol)
        {
            // ✅ Use TryGetValue for thread safety
            if (!_localOrderBooks.TryGetValue(normalizedSymbol, out var local_lob))
                return;

            if (local_lob == null)
            {
                log.Warn($"OrderBook for {normalizedSymbol} is null, skipping update");
                return;
            }

            if (lob_update.LastUpdateId <= local_lob.Sequence)
                return;

            if (lob_update.FirstUpdateId > local_lob.Sequence &&
                lob_update.FirstUpdateId != local_lob.Sequence + 1)
                throw new Exception("Detected sequence gap.");

            ApplyBookDelta(lob_update, normalizedSymbol, local_lob);
        }

        private void ApplyBookDelta(IBinanceEventOrderBook lob_update, string normalizedSymbol, VisualHFT.Model.OrderBook local_lob)
        {
            DateTime? exchangeTs = ResolveBookTimestamp(lob_update);
            // ✅ Cache DateTime.Now once
            var now = DateTime.Now;
            DateTime ts = exchangeTs ?? now;

            foreach (var item in lob_update.Bids)
            {
                if (item.Quantity != 0)
                {
                    local_lob.AddOrUpdateLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.None,
                        Price = (double)item.Price,
                        Size = (double)item.Quantity,
                        IsBid = true,
                        LocalTimeStamp = now,
                        ServerTimeStamp = ts,
                        Symbol = normalizedSymbol
                    });
                }
                else
                    local_lob.DeleteLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.Delete,
                        Price = (double)item.Price,
                        IsBid = true,
                        LocalTimeStamp = now,
                        ServerTimeStamp = ts,
                        Symbol = normalizedSymbol
                    });
            }
            
            foreach (var item in lob_update.Asks)
            {
                if (item.Quantity != 0)
                {
                    local_lob.AddOrUpdateLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.None,
                        Price = (double)item.Price,
                        Size = (double)item.Quantity,
                        IsBid = false,
                        LocalTimeStamp = now,
                        ServerTimeStamp = ts,
                        Symbol = normalizedSymbol
                    });
                }
                else
                    local_lob.DeleteLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.Delete,
                        Price = (double)item.Price,
                        IsBid = false,
                        LocalTimeStamp = now,
                        ServerTimeStamp = ts,
                        Symbol = normalizedSymbol
                    });
            }
            
            local_lob.Sequence = lob_update.LastUpdateId;
            // Venue event time when the frame carries one; null otherwise (never
            // receive time — Mkt Lat measures the venue clock, not our decode delay).
            local_lob.LastUpdated = exchangeTs;
            RaiseOnDataReceived(local_lob);
        }

        private async Task DoPingAsync()
        {
            try
            {
                if (Status == ePluginStatus.STOPPED || Status == ePluginStatus.STOPPING || Status == ePluginStatus.STOPPED_FAILED)
                    return;

                bool spotExpected = GetAllNonNormalizedSymbols().Count > 0;
                bool futuresExpected = _futuresSocketClient != null;
                bool spotUp = !spotExpected || (_socketClient != null && _socketClient.CurrentConnections > 0);
                bool futuresUp = !futuresExpected || _futuresSocketClient.CurrentConnections > 0;
                if (!spotUp || !futuresUp)
                {
                    throw new Exception("The socket seems to be disconnected.");
                }

                if (spotExpected)
                {
                    var result = await _restClient.SpotApi.ExchangeData.PingAsync();
                    if (result == null)
                        throw new Exception("Ping failed, result was null.");
                }
                if (futuresExpected)
                {
                    var futuresPing = await _futuresRestClient.UsdFuturesApi.ExchangeData.PingAsync();
                    if (futuresPing == null)
                        throw new Exception("Futures ping failed, result was null.");
                }

                Interlocked.Exchange(ref pingFailedAttempts, 0);
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
            }
            catch (Exception ex)
            {
                // ✅ FIX: Use Interlocked.Increment for thread safety
                if (Interlocked.Increment(ref pingFailedAttempts) >= 5)
                {
                    var _error = $"Will reconnect. Unhandled error in DoPingAsync. Initiating reconnection. {ex.Message}";
                    LogException(ex, _error);

                    Task.Run(async () => await HandleConnectionLost(_error, ex));
                }
            }
        }

        private VisualHFT.Model.OrderBook ToOrderBookModel(BinanceOrderBook data, string normalizedSymbol = null)
        {
            var identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.Asks.Select(x => x.Price));

            // USD-M depth snapshots hide Symbol on the derived book. Reading it through
            // the spot base type returns blank, and a blank book never reaches the symbol list.
            var symbol = !string.IsNullOrEmpty(normalizedSymbol)
                ? normalizedSymbol
                : GetNormalizedSymbol(data.Symbol);
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
            lob.Sequence = data.LastUpdateId;
            lob.LoadData(
                _asks.OrderBy(x => x.Price).Take(_settings.DepthLevels),
                _bids.OrderByDescending(x => x.Price).Take(_settings.DepthLevels)
            );
            return lob;
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
                    UnattachEventHandlers(futuresDeltaSubscription?.Data);
                    UnattachEventHandlers(futuresTradesSubscription?.Data);
                    _socketClient?.UnsubscribeAllAsync();
                    _socketClient?.Dispose();
                    _restClient?.Dispose();
                    _futuresSocketClient?.UnsubscribeAllAsync();
                    _futuresSocketClient?.Dispose();
                    _futuresRestClient?.Dispose();
                    _timerPing?.Dispose();

                    // ✅ FIX: Dispose semaphore
                    _startStopLock?.Dispose();

                    _eventBuffers?.Dispose();
                    _tradesBuffers?.Dispose();
                    _futuresEventBuffers?.Dispose();
                    _futuresTradesBuffers?.Dispose();

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
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = 1, ProviderName = "Binance" };
            }
            _settings.Symbols ??= new List<string>();
            _settings.FuturesSymbols ??= new List<string>();
            LoadSymbolMaps();
        }
        private void LoadSymbolMaps()
        {
            _settings.Symbols ??= new List<string>();
            _settings.FuturesSymbols ??= new List<string>();
            ParseSymbols(string.Join(',', _settings.Symbols));
            var spotRaw = new HashSet<string>(GetAllNonNormalizedSymbols());
            var spotNormalized = new HashSet<string>(GetAllNormalizedSymbols());
            _futuresNormalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in BinanceFuturesFeed.Parse(_settings.FuturesSymbols))
            {
                if (spotRaw.Contains(pair.Key) || spotNormalized.Contains(pair.Value))
                {
                    log.Warn($"{this.Name}: futures symbol {pair.Key}({pair.Value}) overlaps a spot symbol and was skipped.");
                    continue;
                }
                _futuresNormalized[pair.Key] = pair.Value;
            }
        }
        private string NormalizeFutures(string raw)
        {
            if (!string.IsNullOrEmpty(raw) && _futuresNormalized.TryGetValue(raw, out var normalized))
                return normalized;
            return raw ?? string.Empty;
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
                DepthLevels = 10,
                UpdateIntervalMs = 100,
                IsNonUS = false,
                Provider = new VisualHFT.Model.Provider() { ProviderID = 1, ProviderName = "Binance" },
                Symbols = new List<string>() { "BTCUSDT(BTC/USD)", "ETHUSDT(ETH/USD)" },
                FuturesSymbols = new List<string>()
            };
            SaveToUserSettings(_settings);
        }
        public override object GetUISettings()
        {
#if !WINDOWS
            return null;
#else
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.ApiSecret = _settings.ApiSecret;
            viewModel.ApiKey = _settings.ApiKey;
            viewModel.UpdateIntervalMs = _settings.UpdateIntervalMs;
            viewModel.DepthLevels = _settings.DepthLevels;
            viewModel.ProviderId = _settings.Provider.ProviderID;
            viewModel.ProviderName = _settings.Provider.ProviderName;
            viewModel.Symbols = _settings.Symbols;
            viewModel.FuturesSymbols = _settings.FuturesSymbols ?? new List<string>();
            viewModel.IsNonUS = _settings.IsNonUS;
            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.ApiSecret = viewModel.ApiSecret;
                _settings.ApiKey = viewModel.ApiKey;
                _settings.UpdateIntervalMs = viewModel.UpdateIntervalMs;
                _settings.DepthLevels = viewModel.DepthLevels;
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = viewModel.ProviderId, ProviderName = viewModel.ProviderName };
                _settings.Symbols = viewModel.Symbols;
                _settings.FuturesSymbols = viewModel.FuturesSymbols ?? new List<string>();
                _settings.IsNonUS = viewModel.IsNonUS;
                SaveSettings();
                LoadSymbolMaps();

                //run this because it will allow to reconnect with the new values
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTING));
                Status = ePluginStatus.STARTING;
                Task.Run(async () => await HandleConnectionLost($"{this.Name} is starting (from reloading settings).", null, true));

            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
#endif
        }


        //FOR UNIT TESTING PURPOSE
        public void InjectSnapshot(VisualHFT.Model.OrderBook snapshotModel, long sequence)
        {
            var localModel = new BinanceOrderBook();
            localModel.Symbol = snapshotModel.Symbol;
            localModel.Bids = snapshotModel.Bids.Select(x => new BinanceOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray();
            localModel.Asks = snapshotModel.Asks.Select(x => new BinanceOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray();
            localModel.LastUpdateId = sequence;
            _settings.DepthLevels = snapshotModel.MaxDepth;

            var symbol = snapshotModel.Symbol;

            // ✅ FIX: Use AddOrUpdate for ConcurrentDictionary
            _localOrderBooks.AddOrUpdate(
                symbol,
                ToOrderBookModel(localModel),
                (key, oldValue) => ToOrderBookModel(localModel)
            );

            RaiseOnDataReceived(_localOrderBooks[symbol]);
        }

        // ✅ FIX: Return actual orders from _localUserOrders
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

            string jsonString = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"Binance_JsonMessages/{_file}"));

            List<BinanceStreamOrderUpdate> modelList = new List<BinanceStreamOrderUpdate>();
            var jsonArray = JArray.Parse(jsonString);
            
            foreach (var jsonObject in jsonArray)
            {
                JToken dataToken = jsonObject["data"];
                string dataJsonString = dataToken.ToString();
                BinanceStreamOrderUpdate _data = JsonParser.Parse(dataJsonString);

                if (_data != null)
                    modelList.Add(_data);
            }

            if (!modelList.Any())
                throw new Exception("No data was found in the json file.");

            // Clear previous orders
            _localUserOrders.Clear();

            // Process all order updates
            foreach (var item in modelList)
            {
                UpdateUserOrderBook(item);
            }

            // ✅ FIX: Return actual orders from _localUserOrders
            return _localUserOrders.Values.ToList();
        }
    }
}
