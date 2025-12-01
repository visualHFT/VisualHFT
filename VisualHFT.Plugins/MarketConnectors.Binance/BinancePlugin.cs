using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using MarketConnectors.Binance.Model;
using MarketConnectors.Binance.ViewModel;
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
using MarketConnectors.Binance.UserControls;
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
        private readonly ConcurrentDictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks = 
            new ConcurrentDictionary<string, VisualHFT.Model.OrderBook>();
        
        private HelperCustomQueue<IBinanceEventOrderBook> _eventBuffers;
        private HelperCustomQueue<IBinanceTrade> _tradesBuffers;
        private int pingFailedAttempts = 0;
        private System.Timers.Timer _timerPing;
        private System.Timers.Timer _timerListenKey;

        private CallResult<UpdateSubscription> deltaSubscription;
        private CallResult<UpdateSubscription> tradesSubscription;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly CustomObjectPool<VisualHFT.Model.Trade> tradePool = new CustomObjectPool<VisualHFT.Model.Trade>();

        // ✅ FIX: Use ConcurrentDictionary for thread safety
        private readonly ConcurrentDictionary<string, VisualHFT.Model.Order> _localUserOrders = 
            new ConcurrentDictionary<string, VisualHFT.Model.Order>();
        private string ListenKey = string.Empty;

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
                await SetupClientsAsync();

                _tradesBuffers = new HelperCustomQueue<IBinanceTrade>($"<IBinanceTrade>_{this.Name}", tradesBuffers_onReadAction, tradesBuffers_onErrorAction);
                _eventBuffers = new HelperCustomQueue<IBinanceEventOrderBook>($"<IBinanceEventOrderBook>_{this.Name}", eventBuffers_onReadAction, eventBuffers_onErrorAction);

                _eventBuffers.PauseConsumer();

                await InitializeDeltasAsync();
                await InitializeSnapshotsAsync();
                await InitializeTradesAsync();
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
            if (_socketClient != null)
                await _socketClient.UnsubscribeAllAsync();
            if (deltaSubscription != null && deltaSubscription.Data != null)
                await deltaSubscription.Data.CloseAsync();
            if (tradesSubscription != null && tradesSubscription.Data != null)
                await tradesSubscription.Data.CloseAsync();
            _timerPing?.Stop();
            _timerPing?.Dispose();
            
            // ✅ FIX: Dispose _timerListenKey
            _timerListenKey?.Stop();
            _timerListenKey?.Dispose();

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
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey, _settings.ApiSecret);
                if (_settings.IsNonUS)
                    options.Environment = BinanceEnvironment.Live;
                else
                    options.Environment = BinanceEnvironment.Us;
            });

            _restClient = new BinanceRestClient(options =>
            {
                if (_settings.ApiKey != "" && _settings.ApiSecret != "")
                    options.ApiCredentials = new ApiCredentials(_settings.ApiKey, _settings.ApiSecret);
                if (_settings.IsNonUS)
                    options.Environment = BinanceEnvironment.Live;
                else
                    options.Environment = BinanceEnvironment.Us;
            });
        }
        private async Task InitializeTradesAsync()
        {
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
        private async Task InitializeDeltasAsync()
        {
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
                            data.Data.EventTime = data.ReceiveTime;
                            if (Math.Abs(DateTime.Now.Subtract(data.Data.EventTime.ToLocalTime()).TotalSeconds) > 1)
                            {
                                var _msg = $"Rates are coming late at {Math.Abs(DateTime.Now.Subtract(data.Data.EventTime.ToLocalTime()).TotalSeconds)} seconds.";
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
        }
        private async Task InitializePingForPrivateListenKeys()
        {
            _timerListenKey?.Stop();
            _timerListenKey?.Dispose();

            _timerListenKey = new System.Timers.Timer(1000 * 60 * 30); // Set the interval to 30 minutes for Refreshing Listen Key
            _timerListenKey.Elapsed += async (sender, e) => await DoRefreshListenKeysAsync();
            _timerListenKey.AutoReset = true;
            _timerListenKey.Enabled = true; // Start the timer
        }
        private async Task DoRefreshListenKeysAsync()
        {
            try
            {
                var data = await _socketClient.SpotApi.Account.KeepAliveUserStreamAsync(this.ListenKey);
            }
            catch (Exception ex)
            {
                LogException(ex, "Error trying to refresh Listen Key.");
            } 
        }
        private async Task InitializeUserPrivateOrders()
        {
            if (!string.IsNullOrEmpty(this._settings.ApiKey) && !string.IsNullOrEmpty(this._settings.ApiSecret))
            {
                var listenKey = await _socketClient.SpotApi.Account.StartUserStreamAsync(CancellationToken.None);
                if (!listenKey.Success)
                {
                    return;
                }

                this.ListenKey = listenKey.Data.Result;
                await InitializePingForPrivateListenKeys();

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
                await _socketClient.SpotApi.Account.SubscribeToUserDataUpdatesAsync(this.ListenKey, neworder =>
                { 
                    log.Info(neworder.Data);
                    if (neworder.Data != null)
                    {
                        BinanceStreamOrderUpdate item = neworder.Data;
                        UpdateUserOrderBook(item);
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
        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";

            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }

        private void tradesBuffers_onReadAction(IBinanceTrade eventData)
        {
            var _symbol = GetNormalizedSymbol(eventData.Symbol);
            // Get a Trade object from the pool.
            var trade = tradePool.Get();
            // Populate the Trade object with the necessary data.
            trade.Price = eventData.Price;
            trade.Size = eventData.Quantity;
            trade.Symbol = _symbol;
            trade.Timestamp = eventData.TradeTime.ToLocalTime();
            trade.ProviderId = _settings.Provider.ProviderID;
            trade.ProviderName = _settings.Provider.ProviderName;
            trade.IsBuy = eventData.BuyerIsMaker;
            trade.MarketMidPrice = _localOrderBooks[_symbol].MidPrice;

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

            DateTime ts = lob_update.EventTime.ToLocalTime();

            if (lob_update.LastUpdateId <= local_lob.Sequence)
                return;

            if (lob_update.FirstUpdateId > local_lob.Sequence &&
                lob_update.FirstUpdateId != local_lob.Sequence + 1)
                throw new Exception("Detected sequence gap.");

            // ✅ Cache DateTime.Now once
            var now = DateTime.Now;

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
            local_lob.LastUpdated = ts;
            RaiseOnDataReceived(local_lob);
        }

        private async Task DoPingAsync()
        {
            try
            {
                if (Status == ePluginStatus.STOPPED || Status == ePluginStatus.STOPPING || Status == ePluginStatus.STOPPED_FAILED)
                    return;

                bool isConnected = _socketClient.CurrentConnections > 0;
                if (!isConnected)
                {
                    throw new Exception("The socket seems to be disconnected.");
                }

                DateTime ini = DateTime.Now;
                var result = await _restClient.SpotApi.ExchangeData.PingAsync();
                if (result != null)
                {
                    var timeLapseInMicroseconds = DateTime.Now.Subtract(ini).TotalMicroseconds;

                    // ✅ FIX: Use Interlocked for thread safety
                    Interlocked.Exchange(ref pingFailedAttempts, 0);

                    RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                }
                else
                {
                    throw new Exception("Ping failed, result was null.");
                }
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

        private VisualHFT.Model.OrderBook ToOrderBookModel(BinanceOrderBook data)
        {
            var identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.Asks.Select(x => x.Price));

            var lob = new VisualHFT.Model.OrderBook(GetNormalizedSymbol(data.Symbol), identifiedPriceDecimalPlaces, _settings.DepthLevels);
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
                    _socketClient?.UnsubscribeAllAsync();
                    _socketClient?.Dispose();
                    _restClient?.Dispose();
                    _timerPing?.Dispose();
                    
                    // ✅ FIX: Dispose _timerListenKey
                    _timerListenKey?.Dispose();
                    
                    // ✅ FIX: Dispose semaphore
                    _startStopLock?.Dispose();

                    _eventBuffers?.Dispose();
                    _tradesBuffers?.Dispose();

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
                DepthLevels = 10,
                UpdateIntervalMs = 100,
                IsNonUS = false,
                Provider = new VisualHFT.Model.Provider() { ProviderID = 1, ProviderName = "Binance" },
                Symbols = new List<string>() { "BTCUSDT(BTC/USD)", "ETHUSDT(ETH/USD)" } // Add more symbols as needed
            };
            SaveToUserSettings(_settings);
        }
        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.ApiSecret = _settings.ApiSecret;
            viewModel.ApiKey = _settings.ApiKey;
            viewModel.UpdateIntervalMs = _settings.UpdateIntervalMs;
            viewModel.DepthLevels = _settings.DepthLevels;
            viewModel.ProviderId = _settings.Provider.ProviderID;
            viewModel.ProviderName = _settings.Provider.ProviderName;
            viewModel.Symbols = _settings.Symbols;
            viewModel.IsNonUS = _settings.IsNonUS;
            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.ApiSecret = viewModel.ApiSecret;
                _settings.ApiKey = viewModel.ApiKey;
                _settings.UpdateIntervalMs = viewModel.UpdateIntervalMs;
                _settings.DepthLevels = viewModel.DepthLevels;
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = viewModel.ProviderId, ProviderName = viewModel.ProviderName };
                _settings.Symbols = viewModel.Symbols;
                _settings.IsNonUS = viewModel.IsNonUS;
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
