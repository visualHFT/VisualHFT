# Sample Messages

This directory contains sample JSON messages that represent the typical data structure you might receive from an exchange WebSocket API. These samples are provided to help you understand the expected message format and to test your parser implementation.

## Files Description

### OrderBookSnapshot.json
- Represents a full order book snapshot
- Contains complete bid and ask sides with multiple price levels
- Used for initial order book state

### OrderBookUpdate.json
- Represents differential updates to the order book
- Contains only changed price levels
- Price levels with quantity 0.0000 should be removed from the order book

### Trade.json
- Represents a single trade execution
- Contains price, quantity, trade ID, and side (buy/sell)

### Error.json
- Represents an error message from the exchange
- Contains error code, message, and optional details

### Subscription.json
- Represents a subscription confirmation
- Sent after successfully subscribing to a data channel

## Usage

These sample files can be used to:

1. **Test your JsonParser implementation** - Load these files and verify they parse correctly
2. **Understand message structure** - Use as reference when implementing your parser
3. **Debug parsing issues** - Compare with actual messages from your exchange
4. **Unit testing** - Include in automated tests for your plugin

## Customization

You should modify these samples to match your specific exchange's message format. Common variations include:

- Different field names (e.g., "price" vs "rate", "quantity" vs "size")
- Array vs object format for price levels
- Timestamp formats (Unix ms, ISO 8601, etc.)
- Additional fields specific to your exchange

## Testing

To test your parser with these samples:

```csharp
// Example test code
var json = File.ReadAllText("OrderBookSnapshot.json");
var result = JsonParser.ParseMessage(json, 1, "TemplateExchange");

if (result is OrderBook orderBook)
{
    Console.WriteLine($"Parsed order book for {orderBook.Symbol}");
    Console.WriteLine($"Bids: {orderBook.Bids.Count}, Asks: {orderBook.Asks.Count}");
}
```
