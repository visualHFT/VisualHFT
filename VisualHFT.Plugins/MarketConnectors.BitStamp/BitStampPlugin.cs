using BitStamp.Net.Clients;
using BitStamp.Net.Models;
using MarketConnectors.BitStamp;
using MarketConnectors.BitStamp.Model;
using MarketConnectors.BitStamp.UserControls;
using MarketConnectors.BitStamp.ViewModel;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.PluginManager;
using VisualHFT.DataRetriever.DataParsers;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
using Websocket.Client;
using OrderBook = VisualHFT.Model.OrderBook;
using Trade = VisualHFT.Model.Trade;

namespace MarketConnectors.Gemini
{
    public class BitStampPlugin : BasePluginDataRetriever
    {
        private new bool _disposed = false;
        private readonly SemaphoreSlim _startStopLock = new SemaphoreSlim(1, 1); // ✅ FIX: Add synchronization
        private bool isReconnecting = false; // ✅ FIX: Add reconnection flag

        private Timer _heartbeatTimer;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly ConcurrentDictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks
            = new ConcurrentDictionary<string, VisualHFT.Model.OrderBook>();

        private IDisposable? _reconnectionSubscription;
        private IDisposable? _disconnectionSubscription;
        private IDisposable? _messageSubscription;

        private PlugInSettings? _settings;
        public override string Name { get; set; } = "BitStamp";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Connects to BitStamp.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action? CloseSettingWindow { get; set; }

        private IDataParser _parser;
        JsonSerializerSettings? _parser_settings = null;
        WebsocketClient? _ws;

        BitStampHttpClient bitstampHttpClient;

        public BitStampPlugin()
        {
            _parser = new JsonParser();
            _parser_settings = new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new CustomDateConverter() },
                DateParseHandling = DateParseHandling.None,
                DateFormatString = "yyyy.MM.dd-HH.mm.ss.ffffff"
            };
            _heartbeatTimer = new Timer(CheckConnectionStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            bitstampHttpClient = new BitStampHttpClient();

            // ✅ FIX: Set reconnection action
            SetReconnectionAction(InternalStartAsync);
        }

        ~BitStampPlugin()
        {
            Dispose(false);
        }

        public override async Task StartAsync()
        {
            await base.StartAsync();

            try
            {
                await InternalStartAsync();
            }
            catch (Exception ex)
            {
                var _error = ex.Message;
                LogException(ex, _error);
                await HandleConnectionLost(_error, ex);
            }
        }

        // ✅ FIX: Add InternalStartAsync for reconnection
        public async Task InternalStartAsync()
        {
            // ✅ FIX: Acquire lock to prevent concurrent start/stop
            await _startStopLock.WaitAsync();
            try
            {
                await ClearAsync();

                await InitializeSnapshotAsync();

                var symbols = GetAllNonNormalizedSymbols();
                List<string> channelsToSubscribe = new List<string>();

                foreach (var symbol in symbols)
                {
                    channelsToSubscribe.Add("diff_order_book_" + symbol);
                    channelsToSubscribe.Add("live_trades_" + symbol);
                }

                var url = new Uri(_settings.WebSocketHostName);
                _ws = new WebsocketClient(url);
                _ws.ReconnectTimeout = TimeSpan.FromSeconds(30);

                _reconnectionSubscription = _ws.ReconnectionHappened.Subscribe(info =>
                {
                    if (info.Type == ReconnectionType.Error)
                    {
                        RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED_FAILED));
                        Status = ePluginStatus.STOPPED_FAILED;
                    }
                    else if (info.Type == ReconnectionType.Initial)
                    {
                        foreach (var symbol in GetAllNonNormalizedSymbols())
                        {
                            var normalizedSymbol = GetNormalizedSymbol(symbol);
                            _localOrderBooks.TryAdd(normalizedSymbol, null);
                        }
                    }
                });

                // ✅ FIX: Add reconnection logic
                _disconnectionSubscription = _ws.DisconnectionHappened.Subscribe(disconnected =>
                {
                    Status = ePluginStatus.STOPPED;
                    if (isReconnecting)
                        return;

                    if (disconnected.CloseStatus == WebSocketCloseStatus.NormalClosure)
                    {
                        RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));
                    }
                    else
                    {
                        var _error = $"Will reconnect. Unhandled error while receiving market data.";
                        LogException(disconnected?.Exception, _error);
                        if (!isReconnecting)
                        {
                            isReconnecting = true;
                            Task.Run(async () =>
                            {
                                await HandleConnectionLost(disconnected.CloseStatusDescription, disconnected.Exception);
                                isReconnecting = false;
                            });
                        }
                    }
                });

                // ✅ FIX: Add reconnection on exception
                _messageSubscription = _ws.MessageReceived.Subscribe(async msg =>
                {
                    try
                    {
                        string data = msg.ToString();
                        HandleMessage(data, DateTime.Now);
                        RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                    }
                    catch (Exception ex)
                    {
                        var _error = $"Will reconnect. Unhandled error in HandleMessage.";
                        LogException(ex, _error);
                        if (!isReconnecting)
                        {
                            isReconnecting = true;
                            await HandleConnectionLost(_error, ex);
                            isReconnecting = false;
                        }
                    }
                });

                await _ws.Start();
                log.Info($"Plugin has successfully started.");
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));

                foreach (var tosubscribe in channelsToSubscribe)
                {
                    BitStampSubscriptions bitStampSubscriptions = new BitStampSubscriptions();
                    bitStampSubscriptions.data = new Data();
                    bitStampSubscriptions.data.channel = tosubscribe;
                    string jsonToSubscribe = JsonConvert.SerializeObject(bitStampSubscriptions);
                    _ws.Send(jsonToSubscribe);
                    await Task.Delay(1000);
                }

                Status = ePluginStatus.STARTED;
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        public async Task InitializeSnapshotAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);

                _localOrderBooks.TryAdd(normalizedSymbol, null);

                var response = await bitstampHttpClient.InitializeSnapshotAsync(symbol);
                if (response != null)
                {
                    var orderBook = ToOrderBookModel(response, normalizedSymbol);
                    _localOrderBooks[normalizedSymbol] = orderBook;
                }
            }
        }

        private async Task ClearAsync()
        {
            _reconnectionSubscription?.Dispose();
            _disconnectionSubscription?.Dispose();
            _messageSubscription?.Dispose();

            // ✅ FIX: Dispose timer safely
            var timer = Interlocked.Exchange(ref _heartbeatTimer, null);
            timer?.Dispose();

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

        public override async Task StopAsync()
        {
            // ✅ FIX: Acquire lock
            await _startStopLock.WaitAsync();
            try
            {
                Status = ePluginStatus.STOPPING;
                log.Info($"{this.Name} is stopping.");

                await ClearAsync();

                if (_ws != null && _ws.IsRunning)
                {
                    try
                    {
                        await _ws.Stop(WebSocketCloseStatus.NormalClosure, "Manual Closing");
                    }
                    catch (ObjectDisposedException)
                    {
                        log.Debug("WebSocket already disposed, ignoring stop request");
                    }
                }

                try
                {
                    _ws?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    log.Debug("WebSocket already disposed, ignoring disposal");
                }

                RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));

                await base.StopAsync();
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        private VisualHFT.Model.OrderBook ToOrderBookModel(InitialResponse data, string symbol)
        {
            var identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.asks.Select(x => double.Parse(x[0])));

            var lob = new VisualHFT.Model.OrderBook(symbol, identifiedPriceDecimalPlaces, _settings.DepthLevels);
            lob.ProviderID = _settings.Provider.ProviderID;
            lob.ProviderName = _settings.Provider.ProviderName;
            lob.SizeDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.asks.Select(x => double.Parse(x[1])));

            return lob;
        }

        private void HandleMessage(string marketData, DateTime serverTime)
        {
            string message = marketData;
            dynamic data = JsonConvert.DeserializeObject<dynamic>(message);

            if (data.@event == "data")
            {
                BitStampOrderBook type = JsonConvert.DeserializeObject<BitStampOrderBook>(message);

                if (type.@event.Equals("data", StringComparison.OrdinalIgnoreCase))
                {
                    string symbol = string.Empty;
                    if (type.channel.Split('_').Length > 3)
                    {
                        symbol = GetNormalizedSymbol(type.channel.Split('_')[3]);
                    }
                    else if (type.channel.Split('_').Length == 3)
                    {
                        symbol = GetNormalizedSymbol(type.channel.Split('_')[2]);
                    }

                    if (!_localOrderBooks.TryGetValue(symbol, out var local_lob))
                        return;

                    if (local_lob == null)
                    {
                        log.Warn($"OrderBook for {symbol} is null, skipping update");
                        return;
                    }

                    serverTime = DateTimeOffset.FromUnixTimeMilliseconds(type.data.microtimestamp / 1000).LocalDateTime;
                    var now = DateTime.Now;

                    foreach (var item in type.data.bids)
                    {
                        var _price = double.Parse(item[0]);
                        var _size = double.Parse(item[1]);

                        if (_size != 0)
                        {
                            local_lob.AddOrUpdateLevel(new DeltaBookItem()
                            {
                                MDUpdateAction = eMDUpdateAction.None,
                                Price = _price,
                                Size = _size,
                                IsBid = true,
                                LocalTimeStamp = now,
                                ServerTimeStamp = serverTime,
                                Symbol = symbol
                            });
                        }
                        else
                        {
                            local_lob.DeleteLevel(new DeltaBookItem()
                            {
                                MDUpdateAction = eMDUpdateAction.Delete,
                                Price = _price,
                                IsBid = true,
                                LocalTimeStamp = now,
                                ServerTimeStamp = serverTime,
                                Symbol = symbol
                            });
                        }
                    }

                    foreach (var item in type.data.asks)
                    {
                        var _price = double.Parse(item[0]);
                        var _size = double.Parse(item[1]);

                        if (_size != 0)
                        {
                            local_lob.AddOrUpdateLevel(new DeltaBookItem()
                            {
                                MDUpdateAction = eMDUpdateAction.None,
                                Price = _price,
                                Size = _size,
                                IsBid = false,
                                LocalTimeStamp = now,
                                ServerTimeStamp = serverTime,
                                Symbol = symbol
                            });
                        }
                        else
                        {
                            local_lob.DeleteLevel(new DeltaBookItem()
                            {
                                MDUpdateAction = eMDUpdateAction.Delete,
                                Price = _price,
                                IsBid = false,
                                LocalTimeStamp = now,
                                ServerTimeStamp = serverTime,
                                Symbol = symbol
                            });
                        }
                    }

                    local_lob.LastUpdated = serverTime;
                    RaiseOnDataReceived(local_lob);
                }
            }
            else if (data.@event == "heartbeat")
            {
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
            }
            else if (data.@event == "trade")
            {
                BitStampTrade type = JsonConvert.DeserializeObject<BitStampTrade>(message);
                if (type != null && type.data != null)
                {
                    string symbol = string.Empty;
                    serverTime = DateTimeOffset.FromUnixTimeMilliseconds(type.data.microtimestamp / 1000).LocalDateTime;

                    if (type.channel.Split('_').Length > 3)
                    {
                        symbol = GetNormalizedSymbol(type.channel.Split('_')[3]);
                    }
                    else if (type.channel.Split('_').Length == 3)
                    {
                        symbol = GetNormalizedSymbol(type.channel.Split('_')[2]);
                    }

                    Trade trade = new Trade();
                    trade.Timestamp = serverTime;
                    trade.Price = type.data.price;
                    trade.Size = type.data.amount;
                    trade.Symbol = symbol;
                    trade.ProviderId = _settings.Provider.ProviderID;
                    trade.ProviderName = _settings.Provider.ProviderName;
                    trade.IsBuy = type.data.type == 0;

                    RaiseOnDataReceived(trade);
                }
            }
        }

        private void CheckConnectionStatus(object state)
        {
            // ✅ FIX: Safe null check
            var ws = _ws;
            bool isConnected = ws != null && ws.IsRunning;

            if (isConnected)
            {
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
            }
            else
            {
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));
            }
        }

        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.ApiSecret = _settings.ApiSecret;
            viewModel.ApiKey = _settings.ApiKey;
            viewModel.APIPassPhrase = _settings.APIPassPhrase;
            viewModel.ProviderId = _settings.Provider.ProviderID;
            viewModel.ProviderName = _settings.Provider.ProviderName;
            viewModel.Symbols = _settings.Symbols;

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.ApiSecret = viewModel.ApiSecret;
                _settings.ApiKey = viewModel.ApiKey;
                _settings.APIPassPhrase = viewModel.APIPassPhrase;
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = viewModel.ProviderId, ProviderName = viewModel.ProviderName };
                _settings.Symbols = viewModel.Symbols;

                SaveSettings();
                ParseSymbols(string.Join(',', _settings.Symbols.ToArray()));

                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTING));
                Status = ePluginStatus.STARTING;

                // ✅ FIX: Proper async handling
                isReconnecting = true;
                Task.Run(async () =>
                {
                    await HandleConnectionLost($"{this.Name} is starting (from reloading settings).", null, true);
                    isReconnecting = false;
                });
            };

            view.DataContext = viewModel;
            return view;
        }

        protected override void InitializeDefaultSettings()
        {
            _settings = new PlugInSettings()
            {
                ApiKey = "",
                ApiSecret = "",
                HostName = "https://www.bitstamp.net/api/v2/",
                WebSocketHostName = "wss://ws.bitstamp.net",
                DepthLevels = 10,
                Provider = new VisualHFT.Model.Provider() { ProviderID = 6, ProviderName = "BitStamp" },
                Symbols = new List<string>() { "btcusd(BTC/USD)", "ethusd(ETH/USD)" }
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
            if (_settings.Provider == null)
            {
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = 6, ProviderName = "BitStamp" };
            }
            ParseSymbols(string.Join(',', _settings.Symbols.ToArray()));
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
                    _reconnectionSubscription?.Dispose();
                    _disconnectionSubscription?.Dispose();
                    _messageSubscription?.Dispose();

                    _ws?.Dispose();
                    _heartbeatTimer?.Dispose();

                    // ✅ FIX: Dispose semaphore
                    _startStopLock?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}