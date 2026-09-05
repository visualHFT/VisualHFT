using Gemini.Net.Clients;
using Gemini.Net.Models;
using MarketConnectors.Gemini.Model;
using MarketConnectors.Gemini.UserControls;
using MarketConnectors.Gemini.ViewModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using VisualHFT.Commons.Extensions;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Interfaces;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.PluginManager;
using VisualHFT.DataRetriever.DataParsers;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
using Websocket.Client;
using JsonSerializerOptions = System.Text.Json.JsonSerializerOptions;
using OrderBook = VisualHFT.Model.OrderBook;
using Trade = VisualHFT.Model.Trade;

namespace MarketConnectors.Gemini
{
    public class GeminiPlugin : BasePluginDataRetriever, IDataRetrieverTestable
    {
        private new bool _disposed = false; // to track whether the object has been disposed
        GeminiSubscription geminiSubscription = new GeminiSubscription();

        private Timer _heartbeatTimer;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // ✅ Thread-safe dictionary
        private readonly ConcurrentDictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks
            = new ConcurrentDictionary<string, VisualHFT.Model.OrderBook>();

        private readonly ConcurrentDictionary<string, VisualHFT.Model.Order> _localUserOrders
            = new ConcurrentDictionary<string, VisualHFT.Model.Order>();
        private HelperCustomQueue<MarketUpdate> _eventBuffers;

        private IDisposable? _socketReconnectionSubscription;
        private IDisposable? _socketDisconnectionSubscription;
        private IDisposable? _socketMessageSubscription;
        private IDisposable? _userReconnectionSubscription;
        private IDisposable? _userDisconnectionSubscription;
        private IDisposable? _userMessageSubscription;

        private PlugInSettings? _settings;
        public override string Name { get; set; } = "Gemini";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Connects to Gemini.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action? CloseSettingWindow { get; set; }

        private IDataParser _parser;
        IWebsocketClient? _socketClient;
        IWebsocketClient? _userOrderEvents;
        GeminiHttpClient geminiHttpClient;
        // 0 = idle, 1 = a recovery has been requested and has NOT finished yet.
        // This replaces a plain bool that was set true, handed to a fire-and-forget
        // HandleConnectionLost, and set back to false on the very next line -- so it was false again
        // microseconds later while the recovery it guarded had barely begun. The websocket library
        // drives its own reconnection, so a venue refusing the connection publishes a disconnect event
        // every few hundred milliseconds; without a latch that actually holds, every one of them is
        // logged and escalated. Measured in production: 3,772 error reports from one failing feed.
        private int _reconnectInFlight;

        /// <summary>True only for the first caller while a recovery is in flight.</summary>
        private bool TryClaimReconnect()
        {
            return Interlocked.CompareExchange(ref _reconnectInFlight, 1, 0) == 0;
        }

        /// <summary>
        /// Runs the shared engine's recovery. A seam so a test can complete the recovery deterministically
        /// and observe that the in-flight claim is released afterwards -- the release is what lets the
        /// connector report a LATER outage, so it needs a test that actually reaches it.
        /// </summary>
        protected virtual Task RunReconnectAsync(string reason, Exception? exception)
        {
            return HandleConnectionLost(reason, exception);
        }

        /// <summary>
        /// Reports a lost connection and asks the shared engine to recover -- at most once while a
        /// recovery is still running. The claim is released when that recovery COMPLETES, never on the
        /// line after starting it.
        /// </summary>
        private void RequestReconnect(string reason, Exception? exception, Action? diagnostic = null)
        {
            if (!TryClaimReconnect())
                return;

            // Everything after the claim is inside try/finally: a throw before the recovery task is
            // handed off would otherwise leave the latch held for the life of the process, and the
            // connector would never ask to reconnect again.
            bool handedOff = false;
            try
            {
                diagnostic?.Invoke();
                LogException(exception, "Will reconnect. " + reason);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunReconnectAsync(reason, exception);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _reconnectInFlight, 0);
                    }
                });
                handedOff = true;
            }
            finally
            {
                if (!handedOff)
                    Interlocked.Exchange(ref _reconnectInFlight, 0);
            }
        }

        public GeminiPlugin()
        {

            _parser = new JsonParser();

            GeminiSubscription geminiSubscription = new GeminiSubscription();
            geminiSubscription.subscriptions = new List<Subscription>();

            geminiHttpClient = new GeminiHttpClient();
            SetReconnectionAction(InternalStartAsync);
        }

        ~GeminiPlugin()
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
                LogException(ex, _error);
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

        public async Task ClearAsync()
        {
            // ✅ FIX: Unsubscribe before disposing
            _socketReconnectionSubscription?.Dispose();
            _socketDisconnectionSubscription?.Dispose();
            _socketMessageSubscription?.Dispose();
            _userReconnectionSubscription?.Dispose();
            _userDisconnectionSubscription?.Dispose();
            _userMessageSubscription?.Dispose();

            _heartbeatTimer?.Dispose();

            // ✅ FIX: Pause and stop queue before clearing
            try
            {
                _eventBuffers?.PauseConsumer();
                _eventBuffers?.Stop();
                _eventBuffers?.Dispose();
            }
            catch (Exception ex)
            {
                log.Debug($"Error disposing event buffers: {ex.Message}");
            }

            //CLEAR LOB
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
        public async Task InternalStartAsync()
        {
            await ClearAsync();

            _eventBuffers = new HelperCustomQueue<MarketUpdate>($"<MarketUpdate>_{this.Name}", eventBuffers_onReadAction, eventBuffers_onErrorAction);

            await InitializeSnapshotAsync();
            await InitializeDeltasAsync();

            // Initialize the timer
            _heartbeatTimer = new Timer(CheckConnectionStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(5)); // Check every 5 seconds

            await InitializeUserPrivateOrders();

            // ✅ FIX: Set status here for consistency (status was previously set in InitializeDeltasAsync)
            log.Info($"Plugin has successfully started.");
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
            Status = ePluginStatus.STARTED;
        }

        private void eventBuffers_onReadAction(MarketUpdate eventData)
        {
            var symbol = GetNormalizedSymbol(eventData.Symbol);
            UpdateOrderBook(eventData, symbol, DateTime.Now);

        }
        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";
            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }


        /// <summary>Seam so a test can substitute the venue socket and observe its callbacks.</summary>
        protected virtual IWebsocketClient CreateSocketClient(Uri url)
        {
            return new WebsocketClient(url);
        }

        /// <summary>Seam so a test can substitute the private-order socket and observe its callbacks.</summary>
        protected virtual IWebsocketClient CreateUserOrderClient(Uri url, Func<ClientWebSocket> factory)
        {
            return new WebsocketClient(url, factory);
        }

        internal async Task InitializeDeltasAsync()
        {
            try
            {
                geminiSubscription.subscriptions = new List<Subscription>();
                geminiSubscription.subscriptions.Add(new Subscription()
                {
                    symbols = GetAllNonNormalizedSymbols()
                });

                var url = new Uri(_settings.WebSocketHostName);
                _socketClient = CreateSocketClient(url);

                _socketClient.ReconnectTimeout = TimeSpan.FromSeconds(10);
                _socketReconnectionSubscription = _socketClient.ReconnectionHappened.Subscribe(info =>
                {
                    if (info.Type == ReconnectionType.Initial)
                    {
                        foreach (var symbol in GetAllNonNormalizedSymbols())
                        {
                            var normalizedSymbol = GetNormalizedSymbol(symbol);
                            // TryAdd is atomic - safe if called concurrently
                            _localOrderBooks.TryAdd(normalizedSymbol, null);
                        }
                        return;
                    }

                    // ReconnectionHappened fires AFTER a connection has been established, so every type
                    // other than Initial describes a socket that is UP again -- the type names the CAUSE,
                    // not the outcome. The library's own documentation defines Error as "used after
                    // unsuccessful previous reconnection". Stamping STOPPED_FAILED here read a live socket
                    // as a dead one, and since the reconnection engine refuses to recover a STOPPED_FAILED
                    // plugin, nothing short of a manual restart could undo it.
                    //
                    // The new socket carries none of this connector's subscriptions -- those lived on the
                    // old one -- so it has to be re-subscribed here or the feed comes back permanently silent.
                    log.Info($"{this.Name} websocket reconnected ({info.Type}); re-sending the market-data subscription.");
                    SendMarketDataSubscription();
                    RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                });
                _socketDisconnectionSubscription = _socketClient.DisconnectionHappened.Subscribe(disconnected =>
                {
                    // Deliberately NOT stamping Status = STOPPED here: the reconnection engine reads
                    // STOPPED as "stopped on purpose -- do not retry", so stamping it from a socket
                    // callback parks the connector on a drop it should recover from. The engine owns
                    // the status for the whole recovery.
                    if (disconnected.CloseStatus == WebSocketCloseStatus.NormalClosure)
                    {
                        log.Warn($"{this.Name} websocket closed normally. Description: '{disconnected.CloseStatusDescription ?? "(none)"}'");
                        RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
                        RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));

                    }
                    else
                    {
                        var reason = !string.IsNullOrEmpty(disconnected.CloseStatusDescription)
                            ? disconnected.CloseStatusDescription
                            : $"Server disconnected (Type: {disconnected.Type})";

                        // The diagnostic line goes INSIDE the claim, not out here: the library republishes
                        // this event every few hundred milliseconds while a venue refuses the connection,
                        // and an unconditional log turns one outage into thousands of lines.
                        RequestReconnect(reason, disconnected.Exception, () => log.Warn(
                            $"{this.Name} websocket disconnected. Type: {disconnected.Type}, "
                            + $"CloseStatus: {disconnected.CloseStatus}, "
                            + $"Description: '{disconnected.CloseStatusDescription ?? "(none)"}', "
                            + $"Exception: {disconnected.Exception?.Message ?? "(none)"}"));
                    }
                });
                _socketMessageSubscription = _socketClient.MessageReceived.Subscribe(async msg =>
                {
                    try
                    {
                        string data = msg.ToString();
                        HandleMessage(data, DateTime.Now);
                        RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));

                    }
                    catch (Exception ex)
                    {

                        RequestReconnect("Unhandled error while receiving delta market data.", ex);
                    }
                });

                try
                {
                    await _socketClient.Start();
                    // ✅ Status is now set in InternalStartAsync - removed duplicate here
                    log.Info($"WebSocket connection established.");
                    RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                    SendMarketDataSubscription();
                }
                catch (Exception ex)
                {
                    RequestReconnect(ex.Message, ex);
                }
            }
            catch (Exception ex)
            {
                RequestReconnect(ex.Message, ex);
            }
        }
        /// <summary>
        /// Sends this connector's market-data subscription on the current socket. Called after the first
        /// connect AND after every library-driven reconnect: a reconnected socket is a NEW connection that
        /// carries none of the previous subscriptions, so skipping it leaves a connector that reports
        /// itself connected and never receives another update.
        /// </summary>
        private void SendMarketDataSubscription()
        {
            var socket = _socketClient;
            if (socket is null)
                return;

            string jsonToSubscribe = JsonConvert.SerializeObject(geminiSubscription);
            socket.Send(jsonToSubscribe);
        }

        private string CreateSignature(string b64)
        {
            using (var hmac = new HMACSHA384(Encoding.UTF8.GetBytes(_settings.ApiSecret)))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(b64));
                return BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
        }
        private async Task InitializeUserPrivateOrders()
        {
            if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret))
            {
                try
                {
                    var payload = new
                    {
                        request = "/v1/order/events",
                        nonce = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5
                    };

                    string payloadstring = JsonConvert.SerializeObject(payload);
                    var encodedPayload = Encoding.UTF8.GetBytes(payloadstring);
                    var b64 = Convert.ToBase64String(encodedPayload);
                    var signature = CreateSignature(b64);

                    var factory = new Func<ClientWebSocket>(() =>
                    {
                        var client = new ClientWebSocket
                        {
                            Options =
                        {
                        KeepAliveInterval = TimeSpan.FromSeconds(30),
                        }
                        };
                        client.Options.SetRequestHeader("X-GEMINI-APIKEY", _settings.ApiKey);
                        client.Options.SetRequestHeader("X-GEMINI-PAYLOAD", b64);
                        client.Options.SetRequestHeader("X-GEMINI-SIGNATURE", signature);

                        return client;
                    });

                    _userOrderEvents = CreateUserOrderClient(new Uri(_settings.WebSocketHostName_UserOrder), factory);
                    _userOrderEvents.ReconnectTimeout = TimeSpan.FromSeconds(10);
                    _userReconnectionSubscription = _userOrderEvents.ReconnectionHappened.Subscribe(info =>
                    {

                        if (info.Type != ReconnectionType.Initial)
                        {
                            // See the market-data handler: this fires AFTER a connection is established,
                            // so it is not a failure and must not stamp STOPPED_FAILED.
                            log.Info($"{this.Name} private-order websocket reconnected ({info.Type}).");
                        }
                        else if (info.Type == ReconnectionType.Initial)
                        {

                        }

                    });
                    _userDisconnectionSubscription = _userOrderEvents.DisconnectionHappened.Subscribe(disconnected =>
                    {
                        // See the market-data handler: the engine owns Status during a recovery.
                        if (disconnected.CloseStatus == WebSocketCloseStatus.NormalClosure)
                        {
                            RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
                            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));
                            base.StopAsync();
                        }
                        else
                        {
                            var reason = !string.IsNullOrEmpty(disconnected.CloseStatusDescription)
                                ? disconnected.CloseStatusDescription
                                : $"Server disconnected (Type: {disconnected.Type})";

                            RequestReconnect(reason, disconnected.Exception);
                        }
                    });
                    _userMessageSubscription = _userOrderEvents.MessageReceived.Subscribe(async msg =>
                    {
                        string data = msg.ToString();
                        HandleUserOrderMessage(data);
                    });
                    try
                    {

                        await _userOrderEvents.Start();

                        log.Info($"Plugin has successfully started.");
                    }
                    catch (Exception ex)
                    {
                        LogException(ex, "userOrderEvents.MessageReceived");
                    }
                }
                catch (Exception ex)
                {
                    LogException(ex, "socketClient.MessageReceived");
                }
            }
        }
        private async Task InitializeSnapshotAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);

                // Add placeholder (null is fine, will be replaced)
                _localOrderBooks.TryAdd(normalizedSymbol, null);

                var response = await geminiHttpClient.InitializeSnapshotAsync(symbol, _settings.DepthLevels);

                if (response != null)
                {
                    var orderBook = ToOrderBookModel(response, normalizedSymbol);

                    // Atomic replace
                    _localOrderBooks[normalizedSymbol] = orderBook;
                }
            }
        }

        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");

            await ClearAsync();

            // ✅ Dispose market data WebSocket
            if (_socketClient != null && _socketClient.IsRunning)
            {
                try
                {
                    await _socketClient.Stop(WebSocketCloseStatus.NormalClosure, "Manual Closing");
                }
                catch (ObjectDisposedException)
                {
                    log.Debug("Socket client already disposed, ignoring stop request");
                }
            }

            // ✅ FIX: Safe disposal with try-catch
            try
            {
                _socketClient?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, safe to ignore
                log.Debug("Socket client already disposed, ignoring disposal");
            }

            if (_userOrderEvents != null && _userOrderEvents.IsRunning)
            {
                try
                {
                    await _userOrderEvents.Stop(WebSocketCloseStatus.NormalClosure, "Manual Closing");
                }
                catch (ObjectDisposedException)
                {
                    log.Debug("User order events client already disposed, ignoring stop request");
                }
            }

            try
            {
                _userOrderEvents?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                log.Debug("User order events client already disposed, ignoring disposal");
            }

            RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));
            await base.StopAsync();
        }


        private VisualHFT.Model.OrderBook ToOrderBookModel(InitialResponse data, string symbol)
        {
            int identifiedPriceDecimalPlaces = -1;

            if (data.asks != null && data.asks.Count > 0)
                identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.asks.Select(x => x.price));
            else if (data.bids != null && data.bids.Count > 0)
                identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.bids.Select(x => x.price));
            else
                return null;

            var lob = new VisualHFT.Model.OrderBook(symbol, identifiedPriceDecimalPlaces, _settings.DepthLevels);
            lob.ProviderID = _settings.Provider.ProviderID;
            lob.ProviderName = _settings.Provider.ProviderName;
            lob.SizeDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.asks.Select(x => x.amount));
            lob.FilterBidAskByMaxDepth = true;


            /*
                Initialize the Limit Order Book "only" in this method.
                Do not load the snapshot data, since it will be given at deltas subscription in the first message.
                So, just initialize the LOB hete
             */


            /*
            var _asks = new List<VisualHFT.Model.BookItem>();
            var _bids = new List<VisualHFT.Model.BookItem>();
            data.asks.ForEach(x =>
            {
                _asks.Add(new VisualHFT.Model.BookItem()
                {
                    IsBid = false,
                    Price = (double)x.price,
                    Size = (double)x.amount,
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = lob.Symbol,
                    PriceDecimalPlaces = lob.PriceDecimalPlaces,
                    SizeDecimalPlaces = lob.SizeDecimalPlaces,
                    ProviderID = lob.ProviderID,
                    LayerName = "snapshot"
                });
            });
            data.bids.ForEach(x =>
            {
                _bids.Add(new VisualHFT.Model.BookItem()
                {
                    IsBid = true,
                    Price = (double)x.price,
                    Size = (double)x.amount,
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = lob.Symbol,
                    PriceDecimalPlaces = lob.PriceDecimalPlaces,
                    SizeDecimalPlaces = lob.SizeDecimalPlaces,
                    ProviderID = lob.ProviderID,
                    LayerName = "snapshot"
                });
            });
            lob.LoadData(_asks.OrderBy(x => x.Price).Take(_settings.DepthLevels),_bids.OrderByDescending(x => x.Price).Take(_settings.DepthLevels));
            */
            return lob;
        }

        private void HandleUserOrderMessage(string data)
        {
            string message = data;
            if (!string.IsNullOrEmpty(message) && message.Length > 2)
            {
                JToken token = JToken.Parse(message);
                if (token is JObject)
                {
                    dynamic dataType = JsonConvert.DeserializeObject<dynamic>(message);

                    if (dataType.type == "initial")
                    {
                        ProcessUserOrderData(message);
                    }
                    else if (dataType.type == "subscription_ack")
                    {

                    }
                    else if (dataType.type == "heartbeat")
                    {


                    }
                    else
                    {
                        string ss = message;
                    }
                }
                else if (token is JArray)
                {
                    ProcessUserOrderData(message);
                }

            }
        }
        private void ProcessUserOrderData(string message)
        {
            List<UserOrderData> _dataType = JsonConvert.DeserializeObject<List<UserOrderData>>(message);

            if (_dataType != null && _dataType.Count > 0)
            {
                foreach (var item in _dataType)
                {

                    UpdateUserOrderBook(item);

                }
            }
        }

        private void UpdateUserOrderBook(UserOrderData item)
        {
            var localuserOrder = _localUserOrders.GetOrAdd(item.client_order_id, _ =>
            {
                var order = new VisualHFT.Model.Order
                {
                    ClOrdId = item.client_order_id,
                    Currency = GetNormalizedSymbol(item.symbol),
                    OrderID = item.order_id,
                    ProviderId = _settings!.Provider.ProviderID,
                    ProviderName = _settings.Provider.ProviderName,
                    CreationTimeStamp = DateTimeOffset.FromUnixTimeSeconds(item.timestamp).DateTime,
                    Quantity = item.original_amount,
                    PricePlaced = item.price,
                    Symbol = GetNormalizedSymbol(item.symbol),
                    FilledQuantity = item.executed_amount,
                    TimeInForce = eORDERTIMEINFORCE.GTC
                };

                // Set TimeInForce based on behavior
                if (!string.IsNullOrEmpty(item.behavior))
                {
                    order.TimeInForce = item.behavior.ToLowerInvariant() switch
                    {
                        "immediate-or-cancel" => eORDERTIMEINFORCE.IOC,
                        "fill-or-kill" => eORDERTIMEINFORCE.FOK,
                        "maker-or-cancel" => eORDERTIMEINFORCE.MOK,
                        _ => eORDERTIMEINFORCE.GTC
                    };
                }

                return order;
            });

            // Update mutable properties (⚠️ Order object itself may need locking if accessed concurrently)
            localuserOrder.OrderType = item.order_type.ToLowerInvariant() switch
            {
                "limit" => eORDERTYPE.LIMIT,
                "exchange limit" => eORDERTYPE.PEGGED,
                "market buy" => eORDERTYPE.MARKET,
                _ => eORDERTYPE.LIMIT
            };

            localuserOrder.Quantity = item.original_amount;

            if (item.side.Equals("sell", StringComparison.OrdinalIgnoreCase))
            {
                localuserOrder.BestAsk = item.price;
                localuserOrder.Side = eORDERSIDE.Sell;
            }
            else if (item.side.Equals("buy", StringComparison.OrdinalIgnoreCase))
            {
                localuserOrder.PricePlaced = item.price;
                localuserOrder.BestBid = item.price;
                localuserOrder.Side = eORDERSIDE.Buy;
            }

            // Update status
            localuserOrder.Status = item.type.ToLowerInvariant() switch
            {
                "accepted" => eORDERSTATUS.NEW,
                "fill" => eORDERSTATUS.PARTIALFILLED,
                "closed" => item.is_cancelled ? eORDERSTATUS.CANCELED : eORDERSTATUS.FILLED,
                "rejected" => eORDERSTATUS.REJECTED,
                "cancelled" => eORDERSTATUS.CANCELED,
                "cancel_rejected" => eORDERSTATUS.CANCELED,
                _ => localuserOrder.Status
            };

            if (item.type.Equals("fill", StringComparison.OrdinalIgnoreCase))
            {
                localuserOrder.FilledQuantity = item.executed_amount;
            }

            localuserOrder.LastUpdated = DateTime.Now;
            // ✅ FIX: Correct order of operations to avoid floating-point errors
            if (localuserOrder.Quantity > 0)
            {
                localuserOrder.FilledPercentage = Math.Round((localuserOrder.FilledQuantity / localuserOrder.Quantity) * 100, 2);
            }
            else
            {
                localuserOrder.FilledPercentage = 0;
            }
            RaiseOnDataReceived(localuserOrder);
        }

        private void UpdateOrderBook(MarketUpdate lob_update, string symbol, DateTime serverTime)
        {

            if (!_localOrderBooks.TryGetValue(symbol, out var local_lob))
                return;

            if (local_lob == null)
            {
                log.Warn($"OrderBook for {symbol} is null, skipping update");
                return;
            }


            // Cache timestamp once
            var now = DateTime.Now;

            foreach (var item in lob_update.Changes)
            {
                bool isBid = item[0].Equals("buy", StringComparison.OrdinalIgnoreCase);
                if (!WireNumber.TryParseDouble(item[1], out double _price)) continue;
                if (!WireNumber.TryParseDouble(item[2], out double _qty)) continue;

                if (_qty == 0)
                {
                    local_lob.DeleteLevel(new DeltaBookItem()
                    {
                        Symbol = symbol,
                        Price = _price,
                        IsBid = isBid,
                        LocalTimeStamp = now,
                        ServerTimeStamp = serverTime,
                        MDUpdateAction = eMDUpdateAction.Delete
                    });
                }
                else
                {
                    local_lob.AddOrUpdateLevel(new DeltaBookItem()
                    {
                        Symbol = symbol,
                        IsBid = isBid,
                        LocalTimeStamp = now,
                        ServerTimeStamp = serverTime,
                        MDUpdateAction = eMDUpdateAction.New,
                        Price = _price,
                        Size = _qty,
                    });
                }
            }

            local_lob.LastUpdated = null; //Set to null since Gemini does not provide timstamp of their messages
            RaiseOnDataReceived(local_lob);

            if (lob_update.Trades != null)
            {
                foreach (var item in lob_update.Trades)
                {
                    if (item.Type.Equals("trade", StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateTrades(item);
                    }
                }
            }

        }

        private void UpdateTrades(MarketConnectors.Gemini.Model.Trade item)
        {

            RaiseOnDataReceived(new Trade()
            {
                Symbol = GetNormalizedSymbol(item.Symbol),
                Size = item.Quantity,
                Price = item.Price,
                IsBuy = item.Side.ToLower() == "buy",
                ProviderId = _settings.Provider.ProviderID,
                ProviderName = _settings.Provider.ProviderName,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(item.Timestamp).DateTime
            });
        }
        static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private void HandleMessage(string marketData, DateTime serverTime)
        {
            // heartbeat messages happen most often; skip any JSON work entirely
            if (marketData.Contains("\"type\":\"heartbeat\"", StringComparison.Ordinal))
            {
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                return;
            }

            // level-2 updates
            if (marketData.Contains("\"type\":\"l2_updates\"", StringComparison.Ordinal))
            {
                var update = System.Text.Json.JsonSerializer.Deserialize<MarketUpdate>(marketData, s_jsonOptions);
                // normalize once, on the object you actually use
                update.Symbol = GetNormalizedSymbol(update.Symbol);
                _eventBuffers.Add(update);
                return;
            }

            // trades
            if (marketData.Contains("\"type\":\"trade\"", StringComparison.Ordinal))
            {
                var trade = System.Text.Json.JsonSerializer.Deserialize<MarketConnectors.Gemini.Model.Trade>(marketData, s_jsonOptions);
                UpdateTrades(trade);
                return;
            }

            // to catch weird payloads:
            throw new Exception("Type not recognized in " + marketData);
        }


        private void CheckConnectionStatus(object state)
        {
            bool isConnected = _socketClient != null && _socketClient.IsRunning;
            if (isConnected)
            {
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
            }
            else
            {
                //throw new Exception("The socket seems to be disconnected.");
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));
            }
        }



        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.ApiSecret = _settings.ApiSecret;
            viewModel.ApiKey = _settings.ApiKey;
            viewModel.ProviderId = _settings.Provider.ProviderID;
            viewModel.ProviderName = _settings.Provider.ProviderName;
            viewModel.Symbols = _settings.Symbols;

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.ApiSecret = viewModel.ApiSecret;
                _settings.ApiKey = viewModel.ApiKey;
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = viewModel.ProviderId, ProviderName = viewModel.ProviderName };
                _settings.Symbols = viewModel.Symbols;

                SaveSettings();
                ParseSymbols(string.Join(',', _settings.Symbols.ToArray()));

                //run this because it will allow to reconnect with the new values
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTING));
                Status = ePluginStatus.STARTING;
                Task.Run(async () =>
                    await HandleConnectionLost($"{this.Name} is starting (from reloading settings).", null, true));
            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
        }
        protected override void InitializeDefaultSettings()
        {
            _settings = new PlugInSettings()
            {
                ApiKey = "",
                ApiSecret = "",
                HostName = "https://api.gemini.com/v1/book/",
                WebSocketHostName = "wss://api.gemini.com/v2/marketdata?heartbeat=true",
                WebSocketHostName_UserOrder = "wss://api.gemini.com/v1/order/events?heartbeat=true",
                Provider = new VisualHFT.Model.Provider() { ProviderID = 5, ProviderName = "Gemini" },
                Symbols = new List<string>() { "BTCUSD(BTC/USD)", "ETHUSD(ETH/USD)" }, // Add more symbols as needed
                DepthLevels = 20
            };
            SaveToUserSettings(_settings);
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
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = 5, ProviderName = "Gemini" };
            }
            ParseSymbols(string.Join(',', _settings.Symbols.ToArray())); //Utilize normalization function
        }
        protected override void SaveSettings()
        {
            SaveToUserSettings(_settings);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!_disposed)
            {
                if (disposing)
                {
                    // ✅ Dispose subscriptions first
                    _socketReconnectionSubscription?.Dispose();
                    _socketDisconnectionSubscription?.Dispose();
                    _socketMessageSubscription?.Dispose();
                    _userReconnectionSubscription?.Dispose();
                    _userDisconnectionSubscription?.Dispose();
                    _userMessageSubscription?.Dispose();

                    // ✅ Dispose WebSocket clients
                    _socketClient?.Dispose();
                    _userOrderEvents?.Dispose();

                    // ✅ Dispose timer
                    _heartbeatTimer?.Dispose();

                    // ✅ Dispose queue
                    _eventBuffers?.Dispose();
                }
                _disposed = true;
            }
        }

        // FOR UNIT TESTING PURPOSES: simulate a connection interruption + recovery, fully offline.
        // Drives the REAL reconnect teardown (ClearAsync) then reseeds via the existing InjectSnapshot
        // path. InjectSnapshot rebuilds the book directly (it does not use the _eventBuffers queue that
        // ClearAsync disposes — MarketDataTests proves InjectSnapshot works with no buffers), so no
        // buffer recreation is needed here. Lets a test assert the reconnect leaves a FRESH book, not a
        // stale one (see ReconnectionReseedTests).
        public async Task SimulateConnectionInterruption(VisualHFT.Model.OrderBook reseedSnapshot)
        {
            if (reseedSnapshot == null)
                throw new ArgumentNullException(nameof(reseedSnapshot));

            await ClearAsync();
            InjectSnapshot(reseedSnapshot, reseedSnapshot.Sequence);
            Status = ePluginStatus.STARTED;
        }
        public void InjectSnapshot(OrderBook snapshotModel, long sequence)
        {
            //1. Call snapshot: creates the local order book, but it won't add any item to it
            //2. Call deltas: it will add the items to the local order book


            var localModel = new InitialResponse(); //transform to local model
            localModel.asks = snapshotModel.Asks.Select(x => new Ask()
            {
                price = x.Price.Value,
                amount = x.Size.Value,
                timestamp = x.LocalTimeStamp.Ticks
            }).ToList();
            localModel.bids = snapshotModel.Bids.Select(x => new Bid()
            {
                price = x.Price.Value,
                amount = x.Size.Value,
                timestamp = x.LocalTimeStamp.Ticks
            }).ToList();
            _settings.DepthLevels = snapshotModel.MaxDepth; //force depth received

            var symbol = snapshotModel.Symbol;
            var orderBook = ToOrderBookModel(localModel, symbol);

            if (orderBook != null)
            {
                _localOrderBooks.AddOrUpdate(
                    symbol,
                    orderBook,
                    (key, oldValue) => orderBook
                );
            }

            //once called snapshots, we need to update the LOB
            List<List<string>> changes = new List<List<string>>();
            snapshotModel.Asks.ToList().ForEach(x =>
            {
                changes.Add(new List<string>() { "sell", x.Price.Value.ToString(CultureInfo.InvariantCulture), x.Size.Value.ToString(CultureInfo.InvariantCulture) });
            });
            snapshotModel.Bids.ToList().ForEach(x =>
            {
                changes.Add(new List<string>() { "buy", x.Price.Value.ToString(CultureInfo.InvariantCulture), x.Size.Value.ToString(CultureInfo.InvariantCulture) });
            });

            UpdateOrderBook(new MarketUpdate()
            {
                Symbol = symbol,
                Type = "l2_updates", //no need here
                Changes = changes,
            }, symbol, DateTime.Now);

            if (_localOrderBooks.TryGetValue(symbol, out var lob))
            {
                lob.Sequence = sequence;
                RaiseOnDataReceived(lob);
            }

        }
        public void InjectDeltaModel(List<DeltaBookItem> bidDeltaModel, List<DeltaBookItem> askDeltaModel)
        {
            throw new VisualHFT.Commons.Exceptions.ExceptionDeltasNotSupportedByExchange();
        }
        public List<VisualHFT.Model.Order> ExecutePrivateMessageScenario(eTestingPrivateMessageScenario scenario)
        {


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

            string jsonString = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"Gemini_jsonMessages/{_file}"));
            JsonParser parser = new JsonParser();
            //DESERIALIZE EXCHANGES MODEL
            List<UserOrderData> modelList = new List<UserOrderData>();
            var dataEvents = new List<UserOrderData>();
            var jsonArray = JArray.Parse(jsonString);
            foreach (var jsonObject in jsonArray)
            {
                JArray innerArray = JArray.Parse(jsonObject.ToString());

                string dataJsonString = innerArray[0].ToString();

                UserOrderData _data = parser.Parse<UserOrderData>(dataJsonString);

                if (_data != null)
                    modelList.Add(_data);

            }
            //END UPDATE VISUALHFT CORE


            //UPDATE VISUALHFT CORE & CREATE MODEL TO RETURN
            if (!modelList.Any())
                throw new Exception("No data was found in the json file.");

            _localUserOrders.Clear();
            foreach (var item in modelList)
            {
                UpdateUserOrderBook(item);
            }
            //END UPDATE VISUALHFT CORE


            // ✅ FIX: Return the ACTUAL orders from _localUserOrders
            // These orders have been processed by UpdateUserOrderBook and have correct calculations
            return _localUserOrders.Values.ToList();
        }
    }
}