using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VisualHFT.Model;
using VisualHFT.Commons.Helpers;
using VisualHFT.Enums;
using MarketConnector.Template.Model;

namespace MarketConnector.Template
{
    /// <summary>
    /// JSON parser for converting exchange messages to VisualHFT common models.
    /// Customize this parser based on your exchange's specific message format.
    /// </summary>
    public class JsonParser
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Parses a raw JSON message and converts it to appropriate VisualHFT model.
        /// </summary>
        /// <param name="jsonMessage">Raw JSON message from the exchange</param>
        /// <param name="providerId">Provider ID for the data</param>
        /// <param name="providerName">Provider name for the data</param>
        /// <returns>Parsed model or null if parsing fails</returns>
        public static object ParseMessage(string jsonMessage, int providerId, string providerName)
        {
            try
            {
                // First, determine the message type by looking at the JSON structure
                var messageObject = JObject.Parse(jsonMessage);
                
                // Check for different message types based on your exchange's format
                if (IsOrderBookMessage(messageObject))
                {
                    return ParseOrderBook(jsonMessage, providerId, providerName);
                }
                else if (IsTradeMessage(messageObject))
                {
                    return ParseTrade(jsonMessage, providerId, providerName);
                }
                else if (IsErrorMessage(messageObject))
                {
                    return ParseError(jsonMessage);
                }
                else if (IsSubscriptionMessage(messageObject))
                {
                    return ParseSubscription(jsonMessage);
                }
                else
                {
                    log.Warn($"Unknown message type: {jsonMessage}");
                    return null;
                }
            }
            catch (JsonException ex)
            {
                log.Error($"Failed to parse JSON message: {jsonMessage}", ex);
                return null;
            }
            catch (Exception ex)
            {
                log.Error($"Error parsing message: {jsonMessage}", ex);
                return null;
            }
        }

        #region Message Type Detection

        /// <summary>
        /// Determines if the message is an order book update.
        /// Customize based on your exchange's message structure.
        /// </summary>
        private static bool IsOrderBookMessage(JObject message)
        {
            // Example checks - modify based on your exchange's format
            return message.ContainsKey("bids") && message.ContainsKey("asks") ||
                   message.ContainsKey("type") && message["type"]?.ToString() == "orderbook" ||
                   message.ContainsKey("channel") && message["channel"]?.ToString().Contains("orderbook") == true;
        }

        /// <summary>
        /// Determines if the message is a trade.
        /// Customize based on your exchange's message structure.
        /// </summary>
        private static bool IsTradeMessage(JObject message)
        {
            // Example checks - modify based on your exchange's format
            return message.ContainsKey("price") && message.ContainsKey("quantity") &&
                   (message.ContainsKey("side") || message.ContainsKey("type")) ||
                   message.ContainsKey("type") && message["type"]?.ToString() == "trade" ||
                   message.ContainsKey("channel") && message["channel"]?.ToString().Contains("trade") == true;
        }

        /// <summary>
        /// Determines if the message is an error.
        /// Customize based on your exchange's message structure.
        /// </summary>
        private static bool IsErrorMessage(JObject message)
        {
            // Example checks - modify based on your exchange's format
            return message.ContainsKey("error") ||
                   message.ContainsKey("code") && message.ContainsKey("message") ||
                   message.ContainsKey("type") && message["type"]?.ToString() == "error";
        }

        /// <summary>
        /// Determines if the message is a subscription confirmation.
        /// Customize based on your exchange's message structure.
        /// </summary>
        private static bool IsSubscriptionMessage(JObject message)
        {
            // Example checks - modify based on your exchange's format
            return message.ContainsKey("status") ||
                   message.ContainsKey("type") && message["type"]?.ToString() == "subscription" ||
                   message.ContainsKey("channel") && message.ContainsKey("reqid");
        }

        #endregion

        #region Order Book Parsing

        /// <summary>
        /// Parses an order book message from the exchange.
        /// </summary>
        private static OrderBook ParseOrderBook(string jsonMessage, int providerId, string providerName)
        {
            try
            {
                // Deserialize using the exchange-specific model
                var exchangeMessage = JsonConvert.DeserializeObject<OrderBookMessage>(jsonMessage);
                
                if (exchangeMessage == null)
                {
                    log.Warn("Failed to deserialize order book message");
                    return null;
                }

                // Convert to VisualHFT OrderBook model
                var orderBook = new OrderBook
                {
                    Symbol = NormalizeSymbol(exchangeMessage.Symbol),
                    Sequence = exchangeMessage.Sequence,
                    LastUpdated = DateTimeOffset.FromUnixTimeMilliseconds(exchangeMessage.Timestamp).DateTime
                };

                // Parse bids and asks
                var bids = exchangeMessage.Bids
                    .Where(b => !b.IsEmpty)
                    .Select(b => new BookItem
                    {
                        Price = (double)b.Price,
                        Size = (double)b.Quantity,
                        IsBid = true
                    })
                    .OrderByDescending(x => x.Price)
                    .ToList();

                var asks = exchangeMessage.Asks
                    .Where(a => !a.IsEmpty)
                    .Select(a => new BookItem
                    {
                        Price = (double)a.Price,
                        Size = (double)a.Quantity,
                        IsBid = false
                    })
                    .OrderBy(x => x.Price)
                    .ToList();

                orderBook.LoadData(asks, bids);

                return orderBook;
            }
            catch (Exception ex)
            {
                log.Error($"Error parsing order book: {jsonMessage}", ex);
                return null;
            }
        }

        #endregion

        #region Trade Parsing

        /// <summary>
        /// Parses a trade message from the exchange.
        /// </summary>
        private static Trade ParseTrade(string jsonMessage, int providerId, string providerName)
        {
            try
            {
                // Deserialize using the exchange-specific model
                var exchangeMessage = JsonConvert.DeserializeObject<TradeMessage>(jsonMessage);
                
                if (exchangeMessage == null)
                {
                    log.Warn("Failed to deserialize trade message");
                    return null;
                }

                // Convert to VisualHFT Trade model
                var trade = new Trade
                {
                    ProviderId = providerId,
                    ProviderName = providerName,
                    Symbol = NormalizeSymbol(exchangeMessage.Symbol),
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(exchangeMessage.Timestamp).DateTime,
                    Price = exchangeMessage.Price,
                    Size = exchangeMessage.Quantity,
                    // Convert exchange side to VisualHFT IsBuy
                    IsBuy = exchangeMessage.Side?.ToLower() == "buy"
                };

                return trade;
            }
            catch (Exception ex)
            {
                log.Error($"Error parsing trade: {jsonMessage}", ex);
                return null;
            }
        }

        #endregion

        #region Error and Subscription Parsing

        /// <summary>
        /// Parses an error message from the exchange.
        /// </summary>
        private static ErrorMessage ParseError(string jsonMessage)
        {
            try
            {
                var exchangeMessage = JsonConvert.DeserializeObject<ErrorMessage>(jsonMessage);
                
                if (exchangeMessage == null)
                    return null;

                return new ErrorMessage
                {
                    Code = exchangeMessage.Code,
                    Message = exchangeMessage.Message,
                    Details = exchangeMessage.Details,
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                log.Error($"Error parsing error message: {jsonMessage}", ex);
                return null;
            }
        }

        /// <summary>
        /// Parses a subscription confirmation message.
        /// </summary>
        private static SubscriptionMessage ParseSubscription(string jsonMessage)
        {
            try
            {
                var exchangeMessage = JsonConvert.DeserializeObject<SubscriptionMessage>(jsonMessage);
                
                if (exchangeMessage == null)
                    return null;

                return new SubscriptionMessage
                {
                    Status = exchangeMessage.Status,
                    Channel = exchangeMessage.Channel,
                    RequestId = exchangeMessage.RequestId,
                    Symbol = exchangeMessage.Symbol,
                    Timestamp = exchangeMessage.Timestamp
                };
            }
            catch (Exception ex)
            {
                log.Error($"Error parsing subscription message: {jsonMessage}", ex);
                return null;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Normalizes symbol names to a consistent format.
        /// Example: BTCUSDT -> BTC/USDT
        /// </summary>
        private static string NormalizeSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return symbol;

            // TODO: Implement symbol normalization logic
            // This is just an example - customize based on your needs
            
            // Example: Convert BTCUSDT to BTC/USDT
            if (symbol.Contains("USDT") && !symbol.Contains("/"))
            {
                return symbol.Replace("USDT", "/USDT");
            }
            
            // Example: Convert BTC-USD to BTC/USD
            if (symbol.Contains("-"))
            {
                return symbol.Replace("-", "/");
            }

            return symbol;
        }

        /// <summary>
        /// Validates that a parsed model has required data.
        /// </summary>
        private static bool ValidateModel(object model)
        {
            return model switch
            {
                OrderBook book => !string.IsNullOrEmpty(book.Symbol) && 
                                 (book.Bids?.Any() == true || book.Asks?.Any() == true),
                Trade trade => !string.IsNullOrEmpty(trade.Symbol) && 
                              trade.Price > 0 && trade.Size > 0,
                _ => true
            };
        }

        #endregion

        #region Batch Processing

        /// <summary>
        /// Parses multiple messages in batch for better performance.
        /// </summary>
        public static List<object> ParseMessages(IEnumerable<string> jsonMessages, int providerId, string providerName)
        {
            var results = new List<object>();
            
            foreach (var message in jsonMessages)
            {
                var parsed = ParseMessage(message, providerId, providerName);
                if (parsed != null)
                {
                    results.Add(parsed);
                }
            }

            return results;
        }

        #endregion
    }

    #region Custom Error Classes

    /// <summary>
    /// Represents an error message from the exchange.
    /// </summary>
    public class ErrorMessage
    {
        public int Code { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Represents a subscription confirmation message.
    /// </summary>
    public class SubscriptionMessage
    {
        public string Status { get; set; }
        public string Channel { get; set; }
        public string RequestId { get; set; }
        public string Symbol { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Represents a trade message from the exchange.
    /// </summary>
    public class TradeMessage
    {
        public string Symbol { get; set; }
        public long Timestamp { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
        public string TradeId { get; set; }
        [JsonProperty("Side")]
        public string Side { get; set; } // "buy" or "sell"
    }

    #endregion
}
