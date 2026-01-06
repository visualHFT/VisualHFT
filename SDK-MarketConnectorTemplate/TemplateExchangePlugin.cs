using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;

namespace MarketConnector.Template
{
    /// <summary>
    /// Template for a market connector plugin. Implement IPlugin and derive from BasePluginDataRetriever
    /// to receive market data and push it into the VisualHFT helper classes.
    /// </summary>
    public class TemplateExchangePlugin : VisualHFT.Commons.PluginManager.BasePluginDataRetriever
    {
        #region Private Fields
        
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _receiveTask;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private int _reconnectionAttempts = 0;
        private readonly Dictionary<string, long> _lastSequences = new Dictionary<string, long>();
        private bool _disposed = false; // Hides inherited member - use new to avoid warning
        
        // Provider information
        private const int PROVIDER_ID = 999; // TODO: Change to a unique ID for your exchange
        private const string PROVIDER_NAME = "TemplateExchange";
        
        // Constants
        private const int MAX_RECONNECTION_ATTEMPTS = 5;
        private const int RECONNECTION_DELAY_MS = 1000;
        private const int BUFFER_SIZE = 4096;
        
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        
        #endregion
        public TemplateExchangePlugin()
        {
            Name = "TemplateExchange";
            Description = "Connects to TemplateExchange and streams order book and trade data.";
            Author = "Developer Name";
            Version = "0.0.1";
            Settings = new Model.PlugInSettings();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        // Required properties
        public override string Name { get; set; }
        public override string Version { get; set; }
        public override string Description { get; set; }
        public override string Author { get; set; }
        public override ISetting Settings { get; set; }
        public override Action CloseSettingWindow { get; set; }

        /// <summary>
        /// Start the plugin asynchronously. Initialize connections to the exchange API here.
        /// Use Settings values such as API keys, symbols, depth levels, etc.
        /// </summary>
        public override async Task StartAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (Status == ePluginStatus.STARTED)
                {
                    log.Warn("Plugin is already started");
                    return;
                }

                Status = ePluginStatus.STARTING;
                log.Info($"Starting {PROVIDER_NAME} plugin...");
                
                // Validate settings
                var validationErrors = ValidateSettings();
                if (validationErrors.Count > 0)
                {
                    var errorMessage = $"Invalid settings: {string.Join(", ", validationErrors)}";
                    log.Error(errorMessage);
                    throw new InvalidOperationException(errorMessage);
                }
                
                // TODO: Initialize your exchange client here
                // Example:
                // _exchangeClient = new TemplateExchangeClient(Settings.ApiKey, Settings.ApiSecret);
                // 
                // If using WebSocket directly:
                await ConnectWebSocket();
                
                // TODO: Subscribe to data feeds
                // Example:
                // await SubscribeToOrderBook();
                // await SubscribeToTrades();
                
                Status = ePluginStatus.STARTED;
                log.Info($"{PROVIDER_NAME} plugin started successfully");
            }
            catch (Exception ex)
            {
                Status = ePluginStatus.STOPPED;
                log.Error($"Failed to start {PROVIDER_NAME} plugin", ex);
                throw;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Stop the plugin asynchronously. Close connections and clean up resources.
        /// </summary>
        public override async Task StopAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (Status == ePluginStatus.STOPPED)
                {
                    log.Warn("Plugin is already stopped");
                    return;
                }

                Status = ePluginStatus.STOPPING;
                log.Info($"Stopping {PROVIDER_NAME} plugin...");
                
                // TODO: Disconnect from the exchange API and dispose of resources
                // Example:
                // await _exchangeClient?.DisconnectAsync();
                // _exchangeClient?.Dispose();
                
                await DisconnectWebSocket();
                
                Status = ePluginStatus.STOPPED;
                log.Info($"{PROVIDER_NAME} plugin stopped successfully");
            }
            catch (Exception ex)
            {
                log.Error($"Error stopping {PROVIDER_NAME} plugin", ex);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Initialize default settings for the plugin. Invoked on construction.
        /// </summary>
        protected override void InitializeDefaultSettings()
        {
            Settings = new Model.PlugInSettings
            {
                ApiKey = string.Empty,
                ApiSecret = string.Empty,
                Symbols = "BTC-USD,ETH-USD",  // comma-separated list of symbols
                DepthLevels = 20,
                AggregationLevel = VisualHFT.Enums.AggregationLevel.Ms100,
                EnableReconnection = true,
                MaxReconnectionAttempts = MAX_RECONNECTION_ATTEMPTS,
                ConnectionTimeoutMs = 5000,
                UseTestnet = false,
                Environment = "production",
                EnableDebugLogging = false
            };
        }

        /// <summary>
        /// Load settings from persistent storage.
        /// TODO: Implement settings loading logic based on your requirements.
        /// </summary>
        protected override void LoadSettings()
        {
            // TODO: Load settings from configuration file, registry, or database
            // Example:
            // var loaded = SettingsManager.Load<Model.PlugInSettings>("TemplateExchange");
            // if (loaded != null)
            //     Settings = loaded;
        }

        /// <summary>
        /// Save settings to persistent storage.
        /// TODO: Implement settings saving logic based on your requirements.
        /// </summary>
        protected override void SaveSettings()
        {
            // TODO: Save settings to configuration file, registry, or database
            // Example:
            // SettingsManager.Save(Settings, "TemplateExchange");
        }

        /// <summary>
        /// Get the UI settings control for configuration.
        /// Returns the WPF user control for plugin settings.
        /// </summary>
        public override object GetUISettings()
        {
            // Create and return the settings view with its ViewModel
            var view = new UserControls.PluginSettingsView();
            var viewModel = new ViewModels.PluginSettingsViewModel(
                Settings as Model.PlugInSettings,
                () => CloseSettingWindow?.Invoke()
            );
            view.DataContext = viewModel;
            return view;
        }
        
        #region Private Methods
        
        /// <summary>
        /// Validates the current settings and returns a list of errors.
        /// </summary>
        private List<string> ValidateSettings()
        {
            var errors = new List<string>();
            var settings = Settings as Model.PlugInSettings;
            
            if (settings == null)
            {
                errors.Add("Settings is not of type PlugInSettings");
                return errors;
            }
            
            // TODO: Add your validation logic
            // Example:
            // if (!settings.UseTestnet && string.IsNullOrWhiteSpace(settings.ApiKey))
            //     errors.Add("API Key is required for production environment");
            // 
            // if (!settings.UseTestnet && string.IsNullOrWhiteSpace(settings.ApiSecret))
            //     errors.Add("API Secret is required for production environment");
            // 
            // if (string.IsNullOrWhiteSpace(settings.Symbols))
            //     errors.Add("At least one symbol must be specified");
            
            return errors;
        }
        
        /// <summary>
        /// Connects to the exchange WebSocket.
        /// TODO: Implement based on your exchange's WebSocket API.
        /// </summary>
        private async Task ConnectWebSocket()
        {
            var settings = Settings as Model.PlugInSettings;
            var wsUrl = settings?.GetEffectiveWebSocketUrl() ?? "wss://api.example.com/ws";
            
            _webSocket = new ClientWebSocket();
            
            // TODO: Add authentication headers if needed
            // Example:
            // _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");
            
            await _webSocket.ConnectAsync(new Uri(wsUrl), _cancellationTokenSource.Token);
            
            // Start the receive loop
            _receiveTask = Task.Run(ReceiveLoop, _cancellationTokenSource.Token);
        }
        
        /// <summary>
        /// Disconnects the WebSocket and cleans up resources.
        /// </summary>
        private async Task DisconnectWebSocket()
        {
            _cancellationTokenSource?.Cancel();
            
            if (_webSocket != null)
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                _webSocket.Dispose();
                _webSocket = null;
            }
            
            // Wait for receive task to complete
            _receiveTask?.Wait(TimeSpan.FromSeconds(5));
        }
        
        /// <summary>
        /// Main message receive loop.
        /// TODO: Customize based on your exchange's message format.
        /// </summary>
        private async Task ReceiveLoop()
        {
            var buffer = new byte[BUFFER_SIZE];
            
            while (_webSocket?.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await ProcessMessage(json);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        log.Info("WebSocket closed by server");
                        await HandleDisconnection();
                        break;
                    }
                }
                catch (WebSocketException ex)
                {
                    log.Error("WebSocket error", ex);
                    await HandleDisconnection();
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation
                    break;
                }
                catch (Exception ex)
                {
                    log.Error("Error in receive loop", ex);
                }
            }
        }
        
        /// <summary>
        /// Processes a received message.
        /// TODO: Implement message processing logic.
        /// </summary>
        private async Task ProcessMessage(string json)
        {
            try
            {
                // Parse the message using the JsonParser
                var parsed = JsonParser.ParseMessage(json, PROVIDER_ID, PROVIDER_NAME);
                
                if (parsed != null)
                {
                    // Handle different message types
                    switch (parsed)
                    {
                        case OrderBook orderBook:
                            await ProcessOrderBook(orderBook);
                            break;
                        case Trade trade:
                            await ProcessTrade(trade);
                            break;
                        case Model.ErrorMessage errorMessage:
                            await ProcessError(errorMessage);
                            break;
                        case Model.SubscriptionMessage subscription:
                            await ProcessSubscription(subscription);
                            break;
                    }
                }
                else
                {
                    log.Warn($"Failed to parse message: {json}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error processing message: {json}", ex);
            }
        }
        
        /// <summary>
        /// Processes an order book message.
        /// TODO: Implement order book processing logic.
        /// </summary>
        private async Task ProcessOrderBook(OrderBook orderBook)
        {
            // TODO: Validate sequence numbers
            // Example:
            // if (!ValidateSequence(orderBook.Symbol, orderBook.Sequence))
            // {
            //     log.Warn($"Sequence gap detected for {orderBook.Symbol}");
            //     await RequestOrderBookSnapshot(orderBook.Symbol);
            //     return;
            // }
            
            // Publish the order book to VisualHFT
            RaiseOnDataReceived(orderBook);
            
            var settings = Settings as Model.PlugInSettings;
            if (settings?.EnableDebugLogging == true)
            {
                log.Debug($"Published order book for {orderBook.Symbol}");
            }
        }
        
        /// <summary>
        /// Processes a trade message.
        /// TODO: Implement trade processing logic.
        /// </summary>
        private async Task ProcessTrade(Trade trade)
        {
            // TODO: Check for duplicate trades
            // Example:
            // if (IsDuplicateTrade(trade.TradeID))
            //     return;
            
            // Publish the trade to VisualHFT
            RaiseOnDataReceived(trade);
            
            var settings = Settings as Model.PlugInSettings;
            if (settings?.EnableDebugLogging == true)
            {
                log.Debug($"Published trade for {trade.Symbol}: {trade.Price} x {trade.Size}");
            }
        }
        
        /// <summary>
        /// Processes an error message.
        /// TODO: Implement error handling logic.
        /// </summary>
        private async Task ProcessError(Model.ErrorMessage errorMessage)
        {
            log.Error($"Exchange error: {errorMessage.Code} - {errorMessage.Message}");
            
            // TODO: Handle specific error codes
            // Example:
            // switch (errorMessage.Code)
            // {
            //     case 400:
            //         // Bad request - check subscription format
            //         break;
            //     case 401:
            //         // Unauthorized - check API credentials
            //         break;
            //     case 429:
            //         // Rate limited - implement backoff
            //         break;
            // }
        }
        
        /// <summary>
        /// Processes a subscription confirmation.
        /// TODO: Implement subscription handling logic.
        /// </summary>
        private async Task ProcessSubscription(Model.SubscriptionMessage subscription)
        {
            if (subscription.Status == "success")
            {
                log.Info($"Successfully subscribed to {subscription.Channel} for {subscription.Symbol}");
            }
            else
            {
                log.Error($"Failed to subscribe to {subscription.Channel} for {subscription.Symbol}");
            }
        }
        
        /// <summary>
        /// Handles disconnection and attempts reconnection.
        /// TODO: Customize reconnection logic based on your needs.
        /// </summary>
        private async Task HandleDisconnection()
        {
            var settings = Settings as Model.PlugInSettings;
            
            if (settings?.EnableReconnection != true || _reconnectionAttempts >= settings.MaxReconnectionAttempts)
            {
                Status = ePluginStatus.STOPPED;
                log.Info("Disconnected - max reconnection attempts reached");
                return;
            }
            
            _reconnectionAttempts++;
            var delay = Math.Min(RECONNECTION_DELAY_MS * (int)Math.Pow(2, _reconnectionAttempts - 1), 30000);
            
            log.Info($"Attempting reconnection {_reconnectionAttempts}/{settings.MaxReconnectionAttempts} in {delay}ms...");
            
            await Task.Delay(delay);
            
            try
            {
                await StartAsync();
                _reconnectionAttempts = 0;
            }
            catch
            {
                await HandleDisconnection();
            }
        }
        
        /// <summary>
        /// Validates sequence numbers for order book updates.
        /// TODO: Implement based on your exchange's sequence handling.
        /// </summary>
        private bool ValidateSequence(string symbol, long sequence)
        {
            if (!_lastSequences.ContainsKey(symbol))
            {
                _lastSequences[symbol] = sequence;
                return true;
            }
            
            var lastSequence = _lastSequences[symbol];
            if (sequence <= lastSequence)
            {
                log.Warn($"Out of sequence message for {symbol}: {sequence} <= {lastSequence}");
                return false;
            }
            
            if (sequence > lastSequence + 1)
            {
                log.Warn($"Sequence gap detected for {symbol}: {sequence} > {lastSequence + 1}");
                _lastSequences[symbol] = sequence;
                return false;
            }
            
            _lastSequences[symbol] = sequence;
            return true;
        }
        
        #endregion
        
        #region IDisposable Implementation
        
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                log.Info($"Disposing {PROVIDER_NAME} plugin...");
                
                // Stop the plugin if running
                if (Status == ePluginStatus.STARTED)
                {
                    StopAsync().GetAwaiter().GetResult();
                }
                
                // Dispose resources
                _cancellationTokenSource?.Dispose();
                _connectionLock?.Dispose();
                
                _disposed = true;
            }
            
            base.Dispose(disposing);
        }
        
        #endregion
    }
}
