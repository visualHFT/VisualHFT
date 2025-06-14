﻿using Binance.Net;
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
using VisualHFT.Commons.Interfaces;
using Binance.Net.Objects.Models;
using Newtonsoft.Json.Linq;
using System.IO;

namespace MarketConnectors.Binance
{

    public class BinancePlugin : BasePluginDataRetriever, IDataRetrieverTestable
    {
        private bool _disposed = false; // to track whether the object has been disposed

        private PlugInSettings _settings;
        private BinanceSocketClient _socketClient;
        private BinanceRestClient _restClient;
        private Dictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks = new Dictionary<string, VisualHFT.Model.OrderBook>();
        private HelperCustomQueue<IBinanceEventOrderBook> _eventBuffers;
        private HelperCustomQueue<IBinanceTrade> _tradesBuffers;
        private int pingFailedAttempts = 0;
        private System.Timers.Timer _timerPing;
        private System.Timers.Timer _timerListenKey;

        private CallResult<UpdateSubscription> deltaSubscription;
        private CallResult<UpdateSubscription> tradesSubscription;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly CustomObjectPool<VisualHFT.Model.Trade> tradePool = new CustomObjectPool<VisualHFT.Model.Trade>();//pool of Trade objects

        private Dictionary<string, VisualHFT.Model.Order> _localUserOrders = new Dictionary<string, VisualHFT.Model.Order>();
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
                log.Error(_error, ex);
                if (_error.IndexOf("[CantConnectError]") > -1)
                {
                    Status = ePluginStatus.STOPPED_FAILED;
                    HelperNotificationManager.Instance.AddNotification(this.Name, _error, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS);

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
            await ClearAsync();
            await SetupClientsAsync();


            _tradesBuffers = new HelperCustomQueue<IBinanceTrade>($"<IBinanceTrade>_{this.Name}", tradesBuffers_onReadAction, tradesBuffers_onErrorAction);
            _eventBuffers = new HelperCustomQueue<IBinanceEventOrderBook>($"<IBinanceEventOrderBook>_{this.Name}", eventBuffers_onReadAction, eventBuffers_onErrorAction);

            //Pause QUEUES until we get the snapshots ready
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

            _eventBuffers?.Clear();
            _tradesBuffers?.Clear();

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
                            log.Error(_error, ex);
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
                            log.Error(_error, ex);
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
            //initialized dics for LastUpdateId
            foreach (var normalizedSymbol in GetAllNonNormalizedSymbols())
            {
                if (!_localOrderBooks.ContainsKey(normalizedSymbol))
                    _localOrderBooks.Add(normalizedSymbol, null);
            }

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
                    _localOrderBooks[normalizedSymbol] = ToOrderBookModel(depthSnapshot.Data);
                }
                else
                {
                    var _error = $"Unsuccessful snapshot request for {normalizedSymbol} error: {depthSnapshot.ResponseStatusCode} - {depthSnapshot.Error}";
                    throw new Exception(_error);
                }
            }
            //Unpause QUEUES until we get the snapshots ready
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
            VisualHFT.Model.Order localuserOrder;
            if (!this._localUserOrders.ContainsKey(item.ClientOrderId))
            {
                localuserOrder = new VisualHFT.Model.Order();
                localuserOrder.OrderID = item.Id;
                localuserOrder.ClOrdId = !string.IsNullOrEmpty(item.ClientOrderId) ? item.ClientOrderId : item.Id.ToString();
                localuserOrder.Currency = GetNormalizedSymbol(item.Symbol);
                localuserOrder.CreationTimeStamp = item.CreateTime;
                localuserOrder.OrderID = item.Id;
                localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                localuserOrder.ProviderName = _settings.Provider.ProviderName;
                localuserOrder.CreationTimeStamp = item.CreateTime;
                localuserOrder.Quantity = (double)item.Quantity;
                localuserOrder.PricePlaced = (double)item.Price;
                localuserOrder.Symbol = GetNormalizedSymbol(item.Symbol);
                localuserOrder.TimeInForce = eORDERTIMEINFORCE.GTC;


                if (item.TimeInForce == TimeInForce.ImmediateOrCancel)
                {
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.IOC;
                }
                else if (item.TimeInForce == TimeInForce.FillOrKill)
                {
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.FOK;
                }
                this._localUserOrders.Add(item.ClientOrderId, localuserOrder);
            }
            else
            {
                localuserOrder = this._localUserOrders[item.ClientOrderId];
            }

            if (item.Type == SpotOrderType.Market)
            {
                localuserOrder.OrderType = eORDERTYPE.MARKET;
            }
            else if (item.Type == SpotOrderType.LimitMaker || item.Type == SpotOrderType.Limit)
            {
                localuserOrder.OrderType = eORDERTYPE.LIMIT;
            }
            else
            {
                localuserOrder.OrderType = eORDERTYPE.PEGGED;
            }


            if (item.Side == OrderSide.Buy)
            {
                localuserOrder.Side = eORDERSIDE.Buy;
            }
            if (item.Side == OrderSide.Sell)
            {
                localuserOrder.Side = eORDERSIDE.Sell;
            }

            if (item.Status == OrderStatus.New || item.Status == OrderStatus.PendingNew)
            {
                if (item.Side == OrderSide.Buy)
                {
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.PricePlaced = (double)item.Price;
                    localuserOrder.BestBid = (double)item.Price;
                    localuserOrder.Side = eORDERSIDE.Buy;
                }
                if (item.Side == OrderSide.Sell)
                {
                    localuserOrder.Side = eORDERSIDE.Sell;
                    localuserOrder.BestAsk = (double)item.Price;
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.Quantity = (double)item.Quantity;
                }
                localuserOrder.Status = eORDERSTATUS.NEW;
            }
            if (item.Status == OrderStatus.Filled)
            {

                localuserOrder.BestAsk = (double)item.Price;
                localuserOrder.BestBid = (double)item.Price;
                localuserOrder.FilledQuantity = (double)(item.QuantityFilled);
                localuserOrder.Status = eORDERSTATUS.FILLED;
            }
            if (item.Status == OrderStatus.Canceled)
            {
                localuserOrder.Status = eORDERSTATUS.CANCELED;
            }

            if (item.Status == OrderStatus.Rejected)
            {
                localuserOrder.Status = eORDERSTATUS.REJECTED;
            }

            if (item.Status == OrderStatus.PartiallyFilled)
            {
                localuserOrder.BestAsk = (double)item.Price;
                localuserOrder.BestBid = (double)item.Price;
                localuserOrder.Status = eORDERSTATUS.PARTIALFILLED;
            }


            if (item.Status == OrderStatus.PendingCancel)
            {
                localuserOrder.Status = eORDERSTATUS.CANCELEDSENT;
            }

            localuserOrder.LastUpdated = DateTime.Now;
            //CHECK IF IT IS BEING MODIFIED => REMOVE THE ORIGINAL, SO THE UNIT TEST GETS JUST THE LATEST
            if (!string.IsNullOrEmpty(item.OriginalClientOrderId) && item.OriginalClientOrderId != item.ClientOrderId)
            {
                if (_localUserOrders.TryGetValue(item.OriginalClientOrderId, out var originalOrder))
                    originalOrder.Status = localuserOrder.Status;
            }


            RaiseOnDataReceived(localuserOrder);
        }

        private void UpdateUserOrderBook(BinanceStreamOrderUpdate item )
        {
            VisualHFT.Model.Order localuserOrder;
            if (!this._localUserOrders.ContainsKey(item.ClientOrderId))
            {
                localuserOrder = new VisualHFT.Model.Order();
                localuserOrder.OrderID = item.Id;
                localuserOrder.ClOrdId = !string.IsNullOrEmpty(item.ClientOrderId) ? item.ClientOrderId : item.Id.ToString();
                localuserOrder.Currency = GetNormalizedSymbol(item.Symbol);
                localuserOrder.CreationTimeStamp = item.CreateTime;
                localuserOrder.OrderID = item.Id;
                localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                localuserOrder.ProviderName = _settings.Provider.ProviderName;
                localuserOrder.CreationTimeStamp = item.CreateTime;
                localuserOrder.Quantity = (double)item.Quantity;
                localuserOrder.PricePlaced = (double)item.Price;
                localuserOrder.Symbol = GetNormalizedSymbol(item.Symbol);
                localuserOrder.TimeInForce = eORDERTIMEINFORCE.GTC;


                if (item.TimeInForce == TimeInForce.ImmediateOrCancel)
                {
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.IOC;
                }
                else if (item.TimeInForce == TimeInForce.FillOrKill)
                {
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.FOK;
                }
                this._localUserOrders.Add(item.ClientOrderId, localuserOrder);
            }
            else
            {
                localuserOrder = this._localUserOrders[item.ClientOrderId];
            }

            if (item.Type == SpotOrderType.Market)
            {
                localuserOrder.OrderType = eORDERTYPE.MARKET;
            }
            else
            {
                localuserOrder.OrderType = eORDERTYPE.LIMIT;
            }


            if (item.Side == OrderSide.Buy)
            {
                localuserOrder.Side = eORDERSIDE.Buy;
            }
            if (item.Side == OrderSide.Sell)
            {
                localuserOrder.Side = eORDERSIDE.Sell;
            }

            if (item.Status == OrderStatus.New || item.Status == OrderStatus.PendingNew)
            {
                if (item.Side == OrderSide.Buy)
                {
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.PricePlaced = (double)item.Price;
                    localuserOrder.BestBid = (double)item.Price;
                    localuserOrder.Side = eORDERSIDE.Buy;
                }
                if (item.Side == OrderSide.Sell)
                {
                    localuserOrder.Side = eORDERSIDE.Sell;
                    localuserOrder.BestAsk = (double)item.Price;
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.Quantity = (double)item.Quantity;
                }
                localuserOrder.Status = eORDERSTATUS.NEW;
            }
            if (item.Status == OrderStatus.Filled)
            {

                localuserOrder.BestAsk = (double)item.Price;
                localuserOrder.BestBid = (double)item.Price;
                localuserOrder.FilledQuantity = (double)(item.QuantityFilled);
                localuserOrder.Status = eORDERSTATUS.FILLED;
            }
            if (item.Status == OrderStatus.Canceled)
            {
                localuserOrder.Status = eORDERSTATUS.CANCELED;
            }

            if (item.Status == OrderStatus.Rejected)
            {
                localuserOrder.Status = eORDERSTATUS.REJECTED;
            }

            if (item.Status == OrderStatus.PartiallyFilled)
            {
                localuserOrder.BestAsk = (double)item.Price;
                localuserOrder.BestBid = (double)item.Price;
                localuserOrder.Status = eORDERSTATUS.PARTIALFILLED;
            }


            if (item.Status == OrderStatus.PendingCancel)
            {
                localuserOrder.Status = eORDERSTATUS.CANCELEDSENT;
            }

            localuserOrder.LastUpdated = DateTime.Now;
            //CHECK IF IT IS BEING MODIFIED => REMOVE THE ORIGINAL, SO THE UNIT TEST GETS JUST THE LATEST
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

            log.Error(_error, ex);
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

            log.Error(_error, ex);
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
            log.Error(_error, obj);
            HelperNotificationManager.Instance.AddNotification(this.Name, _error, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS);

            Task.Run(StopAsync);

            Status = ePluginStatus.STOPPED_FAILED;
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED_FAILED));
        }
        #endregion

        private void UpdateOrderBook(IBinanceEventOrderBook lob_update, string normalizedSymbol)
        {
            
            if (!_localOrderBooks.ContainsKey(normalizedSymbol))
                return;


            var local_lob = _localOrderBooks[normalizedSymbol];
            DateTime ts = lob_update.EventTime.ToLocalTime();

            //SEQUENCE CHECK
            if (lob_update.LastUpdateId <= local_lob.Sequence)  //skip lower sequences
                return;

            if (lob_update.FirstUpdateId > local_lob.Sequence &&
                lob_update.FirstUpdateId != local_lob.Sequence + 1)
                throw new Exception("Detected sequence gap.");



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
                        LocalTimeStamp = DateTime.Now,
                        ServerTimeStamp = ts,
                        Symbol = normalizedSymbol
                    });
                }
                else
                    local_lob.DeleteLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.Delete,
                        Price = (double)item.Price,
                        //Size = (double)item.Quantity,
                        IsBid = true,
                        LocalTimeStamp = DateTime.Now,
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
                        LocalTimeStamp = DateTime.Now,
                        ServerTimeStamp = ts,
                        Symbol = normalizedSymbol
                    });
                }
                else
                    local_lob.DeleteLevel(new DeltaBookItem()
                    {
                        MDUpdateAction = eMDUpdateAction.Delete,
                        Price = (double)item.Price,
                        //Size = (double)item.Quantity,
                        IsBid = false,
                        LocalTimeStamp = DateTime.Now,
                        ServerTimeStamp = ts,
                        Symbol = normalizedSymbol
                    });
            }
            local_lob.Sequence = lob_update.LastUpdateId; //update the sequence

            RaiseOnDataReceived(local_lob);
        } 
        
         
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
                var result = await _restClient.SpotApi.ExchangeData.PingAsync();
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

                    log.Error(_error, ex);

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


                    _eventBuffers?.Dispose();
                    _tradesBuffers?.Dispose();

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
            localModel.Bids = snapshotModel.Bids.Select(x => new BinanceOrderBookEntry() {  Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal()}).ToArray();
            localModel.Asks = snapshotModel.Asks.Select(x => new BinanceOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray();
            localModel.LastUpdateId = sequence;
            _settings.DepthLevels = snapshotModel.MaxDepth; //force depth received

            var symbol = snapshotModel.Symbol;

            if (!_localOrderBooks.ContainsKey(symbol))
            {
                _localOrderBooks.Add(symbol, ToOrderBookModel(localModel));
            }
            _localOrderBooks[symbol] = ToOrderBookModel(localModel);

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

            var localModel = new BinanceEventOrderBook();
            localModel.Bids = bidDeltaModel?.Select(x => new BinanceOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray();
            localModel.Asks = askDeltaModel?.Select(x => new BinanceOrderBookEntry() { Price = x.Price.ToDecimal(), Quantity = x.Size.ToDecimal() }).ToArray();
            long minSequence = Math.Min(bidDeltaModel.Min(x => x.Sequence), askDeltaModel.Min(x => x.Sequence));
            long maxSequence = Math.Max(bidDeltaModel.Max(x => x.Sequence), askDeltaModel.Max(x => x.Sequence));
            localModel.FirstUpdateId = minSequence;
            localModel.LastUpdateId = maxSequence;


            UpdateOrderBook(localModel, symbol);
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

            string jsonString = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"Binance_JsonMessages/{_file}"));
             
            //DESERIALIZE EXCHANGES MODEL
            List<BinanceStreamOrderUpdate> modelList = new List<BinanceStreamOrderUpdate>();
            var dataEvents = new List<BinanceStreamOrderUpdate>();
            var jsonArray = JArray.Parse(jsonString);
            foreach (var jsonObject in jsonArray)
            {
                JToken dataToken = jsonObject["data"];
                string dataJsonString = dataToken.ToString();
                 
                BinanceStreamOrderUpdate _data = JsonParser.Parse(dataJsonString);

                if (_data != null)
                        modelList.Add(_data);
                 
            }
            //END UPDATE VISUALHFT CORE


            //UPDATE VISUALHFT CORE & CREATE MODEL TO RETURN
            if (!modelList.Any())
                throw new Exception("No data was found in the json file.");
            foreach (var item in modelList)
            {
                UpdateUserOrderBook(item);
            }
            //END UPDATE VISUALHFT CORE


            //CREATE MODEL TO RETURN (First, identify the order that was sent, then use that one with the updated values)
            var dicOrders = new Dictionary<string, VisualHFT.Model.Order>(); 
            foreach (var item in modelList)
            {

                VisualHFT.Model.Order localuserOrder;
                if (!dicOrders.ContainsKey(item.ClientOrderId))
                {
                    localuserOrder = new VisualHFT.Model.Order();
                    localuserOrder.OrderID = item.Id;
                    localuserOrder.ClOrdId = !string.IsNullOrEmpty(item.ClientOrderId) ? item.ClientOrderId : item.Id.ToString();
                    localuserOrder.Currency = GetNormalizedSymbol(item.Symbol);
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.OrderID = item.Id;
                    localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                    localuserOrder.ProviderName = _settings.Provider.ProviderName;
                    localuserOrder.CreationTimeStamp = item.CreateTime;
                    localuserOrder.Quantity = (double)item.Quantity;
                    localuserOrder.PricePlaced = (double)item.Price;
                    localuserOrder.Symbol = GetNormalizedSymbol(item.Symbol);
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.GTC;

                    if (item.TimeInForce == TimeInForce.ImmediateOrCancel)
                    {
                        localuserOrder.TimeInForce = eORDERTIMEINFORCE.IOC;
                    }
                    else if (item.TimeInForce == TimeInForce.FillOrKill)
                    {
                        localuserOrder.TimeInForce = eORDERTIMEINFORCE.FOK;
                    }
                    if (item.Type == SpotOrderType.Market)
                    {
                        localuserOrder.OrderType = eORDERTYPE.MARKET;
                    }
                    else
                    {
                        localuserOrder.OrderType = eORDERTYPE.LIMIT;
                    }

                    if (item.Side == OrderSide.Buy)
                    {
                        localuserOrder.Side = eORDERSIDE.Buy;
                    }
                    else if (item.Side == OrderSide.Sell)
                    {
                        localuserOrder.Side = eORDERSIDE.Sell;
                    }



                    dicOrders.Add(item.ClientOrderId, localuserOrder);
                }
                else
                {
                    localuserOrder = dicOrders[item.ClientOrderId];
                }


                if (item.Status == OrderStatus.New || item.Status == OrderStatus.PendingNew)
                {
                    if (item.Side == OrderSide.Buy)
                    {
                        localuserOrder.CreationTimeStamp = item.CreateTime;
                        localuserOrder.PricePlaced = (double)item.Price;
                        localuserOrder.BestBid = (double)item.Price;
                        localuserOrder.Side = eORDERSIDE.Buy;
                    }
                    if (item.Side == OrderSide.Sell)
                    {
                        localuserOrder.Side = eORDERSIDE.Sell;
                        localuserOrder.BestAsk = (double)item.Price;
                        localuserOrder.CreationTimeStamp = item.CreateTime;
                        localuserOrder.Quantity = (double)item.Quantity;
                    }
                    localuserOrder.Status = eORDERSTATUS.NEW;
                }
                if (item.Status == OrderStatus.Filled)
                {

                    localuserOrder.BestAsk = (double)item.Price;
                    localuserOrder.BestBid = (double)item.Price;
                    localuserOrder.FilledQuantity = (double)(item.QuantityFilled);
                    localuserOrder.Status = eORDERSTATUS.FILLED;
                }
                if (item.Status == OrderStatus.Canceled)
                {
                    localuserOrder.Status = eORDERSTATUS.CANCELED;
                }

                if (item.Status == OrderStatus.Rejected)
                {
                    localuserOrder.Status = eORDERSTATUS.REJECTED;
                }

                if (item.Status == OrderStatus.PartiallyFilled)
                {
                    localuserOrder.BestAsk = (double)item.Price;
                    localuserOrder.BestBid = (double)item.Price;
                    localuserOrder.Status = eORDERSTATUS.PARTIALFILLED;
                }


                if (item.Status == OrderStatus.PendingCancel)
                {
                    localuserOrder.Status = eORDERSTATUS.CANCELEDSENT;
                }
                localuserOrder.LastUpdated = DateTime.Now;

                
                //CHECK IF IT IS BEING MODIFIED => REMOVE THE ORIGINAL, SO THE UNIT TEST GETS JUST THE LATEST
                if (!string.IsNullOrEmpty(item.OriginalClientOrderId) && item.OriginalClientOrderId != item.ClientOrderId)
                {
                    if (dicOrders.TryGetValue(item.OriginalClientOrderId, out var originalOrder))
                        originalOrder.Status = localuserOrder.Status;
                }

            }
            //END CREATE MODEL TO RETURN


            return dicOrders.Values.ToList();
        }
    }
}
