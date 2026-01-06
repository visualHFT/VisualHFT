using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MarketConnector.Template.Model
{
    /// <summary>
    /// Template classes for exchange-specific message models.
    /// These models represent the raw JSON structure returned by the exchange
    /// and should be converted to VisualHFT common models before publishing.
    /// </summary>

    /// <summary>
    /// Base message wrapper for all WebSocket messages from the exchange.
    /// Customize this based on your exchange's message format.
    /// </summary>
    public class BaseMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

        [JsonProperty("symbol")]
        public string Symbol { get; set; }
    }

    /// <summary>
    /// Order book update message from the exchange.
    /// Modify the properties to match your exchange's order book format.
    /// </summary>
    public class OrderBookMessage : BaseMessage
    {
        [JsonProperty("bids")]
        public List<PriceLevel> Bids { get; set; } = new List<PriceLevel>();

        [JsonProperty("asks")]
        public List<PriceLevel> Asks { get; set; } = new List<PriceLevel>();

        [JsonProperty("sequence")]
        public long Sequence { get; set; }

        /// <summary>
        /// Indicates if this is a partial snapshot or differential update.
        /// Some exchanges use 'true' for snapshots, others use specific message types.
        /// </summary>
        [JsonProperty("isSnapshot")]
        public bool IsSnapshot { get; set; }
    }

    /// <summary>
    /// Trade message from the exchange.
    /// Customize to match your exchange's trade data format.
    /// </summary>
    public class TradeMessage : BaseMessage
    {
        [JsonProperty("id")]
        public string TradeId { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; } // "buy" or "sell"

        [JsonProperty("timestamp")]
        public new long Timestamp { get; set; }
    }

    /// <summary>
    /// Represents a price level in the order book.
    /// Most exchanges return price levels as [price, quantity] arrays.
    /// </summary>
    public class PriceLevel
    {
        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// Constructor for array-based price levels (common in many exchanges).
        /// Example: ["123.45", "1.23"] where array[0] is price and array[1] is quantity.
        /// </summary>
        public PriceLevel(decimal[] array)
        {
            if (array != null && array.Length >= 2)
            {
                Price = array[0];
                Quantity = array[1];
            }
        }

        /// <summary>
        /// Default constructor.
        /// </summary>
        public PriceLevel() { }

        /// <summary>
        /// Constructor with explicit values.
        /// </summary>
        public PriceLevel(decimal price, decimal quantity)
        {
            Price = price;
            Quantity = quantity;
        }

        /// <summary>
        /// Returns true if the price level has no quantity (should be removed from order book).
        /// </summary>
        public bool IsEmpty => Quantity <= 0;
    }

    /// <summary>
    /// Error message from the exchange.
    /// Customize based on your exchange's error format.
    /// </summary>
    public class ErrorMessage : BaseMessage
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }
    }

    /// <summary>
    /// Subscription confirmation message.
    /// Some exchanges send confirmation when subscription is successful.
    /// </summary>
    public class SubscriptionMessage : BaseMessage
    {
        [JsonProperty("status")]
        public string Status { get; set; } // "success", "error", etc.

        [JsonProperty("channel")]
        public string Channel { get; set; } // "orderbook", "trades", etc.

        [JsonProperty("reqid")]
        public string RequestId { get; set; }
    }

    /// <summary>
    /// Custom JsonConverter for handling price levels that may come as arrays or objects.
    /// This is useful for exchanges that send price levels as [price, quantity] arrays.
    /// </summary>
    public class PriceLevelConverter : JsonConverter<PriceLevel>
    {
        public override void WriteJson(JsonWriter writer, PriceLevel value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartArray();
            writer.WriteValue(value.Price);
            writer.WriteValue(value.Quantity);
            writer.WriteEndArray();
        }

        public override PriceLevel ReadJson(JsonReader reader, Type objectType, PriceLevel existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            if (reader.TokenType == JsonToken.StartArray)
            {
                var array = serializer.Deserialize<decimal[]>(reader);
                return new PriceLevel(array);
            }
            else if (reader.TokenType == JsonToken.StartObject)
            {
                var obj = serializer.Deserialize<Dictionary<string, decimal>>(reader);
                return new PriceLevel(obj.GetValueOrDefault("price", 0), obj.GetValueOrDefault("quantity", 0));
            }

            throw new JsonSerializationException("Unexpected token for PriceLevel");
        }
    }
}
