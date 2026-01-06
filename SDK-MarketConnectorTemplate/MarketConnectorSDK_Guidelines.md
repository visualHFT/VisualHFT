# VisualHFT Market Connector SDK Guidelines

This document provides comprehensive guidelines for developing market data connector plugins for VisualHFT.

## Table of Contents

1. [Overview](#overview)
2. [Plugin Architecture](#plugin-architecture)
3. [Development Workflow](#development-workflow)
4. [Key Components](#key-components)
5. [Best Practices](#best-practices)
6. [Common Patterns](#common-patterns)
7. [Testing and Debugging](#testing-and-debugging)
8. [Deployment](#deployment)
9. [Troubleshooting](#troubleshooting)

## Overview

VisualHFT uses a plugin architecture to support multiple market data connectors. Each connector is implemented as a .NET class library that:

- Connects to an exchange's WebSocket/REST API
- Parses exchange-specific message formats
- Converts data to VisualHFT common models
- Provides a settings UI for configuration
- Handles connection lifecycle and error recovery

### Plugin Lifecycle

1. **Discovery** - VisualHFT scans the plugins folder for compatible DLLs
2. **Initialization** - Plugin instance is created and default settings loaded
3. **Configuration** - User configures settings through the UI
4. **Connection** - Plugin connects to the exchange API
5. **Data Flow** - Market data is streamed to VisualHFT
6. **Disconnection** - Plugin gracefully shuts down

## Plugin Architecture

### Core Classes

```csharp
// Main plugin class - inherits from BasePluginDataRetriever
public class YourExchangePlugin : BasePluginDataRetriever
{
    // Required properties
    public override string Name { get; set; }
    public override string Version { get; set; }
    public override string Description { get; set; }
    public override string Author { get; set; }
    public override ISetting Settings { get; set; }
    
    // Core methods
    public override async Task StartAsync()
    public override async Task StopAsync()
}

// Settings class - implements ISetting
public class YourExchangeSettings : ISetting
{
    // Configuration properties with [Description] attributes
}

// ViewModel for settings UI
public class PluginSettingsViewModel : INotifyPropertyChanged, IDataErrorInfo
{
    // Validation, commands, and UI logic
}
```

### Required Interfaces

- **IPlugin** - Basic plugin identification and metadata
- **IDataRetriever** - Data retrieval functionality
- **ISetting** - Configuration management
- **INotifyPropertyChanged** - UI data binding support

## Development Workflow

### 1. Setup

1. Copy the SDK template to a new folder
2. Rename the project and namespace to match your exchange
3. Update the .csproj file with your exchange's client library

### 2. Implement Connection Logic

```csharp
public override async Task StartAsync()
{
    Status = ePluginStatus.STARTING;
    
    try
    {
        // Create WebSocket client
        _webSocket = new ClientWebSocket();
        
        // Connect to exchange endpoint
        await _webSocket.ConnectAsync(new Uri(Settings.GetWebSocketUrl()), CancellationToken.None);
        
        // Send subscription messages
        await SubscribeToOrderBook();
        await SubscribeToTrades();
        
        // Start receive loop
        _ = Task.Run(ReceiveLoop);
        
        Status = ePluginStatus.STARTED;
        RaiseOnProviderStatusChanged(new ProviderStatus(ePluginStatus.STARTED, "Connected"));
    }
    catch (Exception ex)
    {
        Status = ePluginStatus.STOPPED;
        log.Error("Failed to start plugin", ex);
        throw;
    }
}
```

### 3. Parse Messages

```csharp
private async Task ReceiveLoop()
{
    var buffer = new byte[4096];
    
    while (_webSocket.State == WebSocketState.Open)
    {
        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        
        if (result.MessageType == WebSocketMessageType.Text)
        {
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var parsed = JsonParser.ParseMessage(json, ProviderId, Name);
            
            if (parsed != null)
            {
                RaiseOnDataReceived(parsed);
            }
        }
    }
}
```

### 4. Handle Settings

```csharp
protected override void InitializeDefaultSettings()
{
    Settings = new YourExchangeSettings
    {
        ApiKey = string.Empty,
        ApiSecret = string.Empty,
        Symbols = "BTC-USD,ETH-USD",
        DepthLevels = 20
    };
}
```

## Key Components

### JsonParser

Responsible for converting exchange-specific JSON to VisualHFT models:

- **OrderBook** - Order book snapshots and updates
- **Trade** - Individual trade executions
- **ErrorMessage** - Exchange error messages
- **SubscriptionMessage** - Subscription confirmations

### Settings UI

WPF user control that allows users to configure:
- API credentials
- Symbol subscriptions
- Connection parameters
- Advanced options

### Error Handling

Implement robust error handling for:
- Connection failures
- Authentication errors
- Data parsing errors
- Rate limiting
- Exchange maintenance

## Best Practices

### Performance

1. **Use object pooling** for frequently allocated objects
2. **Minimize allocations** in hot paths (use structs, Span<T>)
3. **Batch operations** where possible
4. **Use async/await** correctly to avoid blocking
5. **Implement backpressure** handling for high-volume data

### Reliability

1. **Implement reconnection logic** with exponential backoff
2. **Validate all data** before publishing
3. **Handle sequence gaps** in order book updates
4. **Log errors** with sufficient context
5. **Provide graceful degradation** when partial data is available

### Security

1. **Never hardcode credentials** - use settings only
2. **Secure API keys** in transit (use WSS)
3. **Validate user inputs** to prevent injection
4. **Use least privilege** for API permissions
5. **Consider rate limiting** to avoid API bans

### Code Quality

1. **Follow C# naming conventions**
2. **Add XML documentation** for public APIs
3. **Use meaningful variable names**
4. **Keep methods small and focused**
5. **Write unit tests** for critical logic

## Common Patterns

### Symbol Normalization

Convert exchange-specific symbols to a standard format:

```csharp
private string NormalizeSymbol(string exchangeSymbol)
{
    // Remove exchange prefixes/suffixes
    var normalized = exchangeSymbol.Replace("t", "").Replace("f", "");
    
    // Add separator if missing
    if (!normalized.Contains("/") && !normalized.Contains("-"))
    {
        // Common patterns
        if (normalized.EndsWith("USDT"))
            return normalized.Replace("USDT", "/USDT");
        if (normalized.EndsWith("USD"))
            return normalized.Replace("USD", "/USD");
        if (normalized.EndsWith("BTC"))
            return normalized.Replace("BTC", "/BTC");
    }
    
    return normalized;
}
```

### Reconnection Logic

```csharp
private async Task HandleDisconnection()
{
    if (_reconnectionAttempts >= MaxReconnectionAttempts)
    {
        Status = ePluginStatus.STOPPED;
        RaiseOnProviderStatusChanged(new ProviderStatus(ePluginStatus.STOPPED, "Max reconnection attempts reached"));
        return;
    }
    
    _reconnectionAttempts++;
    var delay = Math.Min(1000 * Math.Pow(2, _reconnectionAttempts), 30000);
    
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
```

### Rate Limiting

```csharp
private readonly SemaphoreSlim _rateLimiter = new SemaphoreSlim(10, 10); // 10 requests per second

private async Task SendWithRateLimit(string message)
{
    await _rateLimiter.WaitAsync();
    try
    {
        await _webSocket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
    }
    finally
    {
        _ = Task.Delay(100).ContinueWith(_ => _rateLimiter.Release());
    }
}
```

## Testing and Debugging

### Unit Testing

Test your parser with sample messages:

```csharp
[Test]
public void ParseOrderBookSnapshot_ReturnsCorrectModel()
{
    var json = File.ReadAllText("SampleMessages/OrderBookSnapshot.json");
    var result = JsonParser.ParseMessage(json, 1, "TestExchange");
    
    Assert.IsInstanceOf<OrderBook>(result);
    var orderBook = (OrderBook)result;
    Assert.AreEqual("BTC/USD", orderBook.Symbol);
    Assert.IsTrue(orderBook.Bids.Count > 0);
    Assert.IsTrue(orderBook.Asks.Count > 0);
}
```

### Integration Testing

1. **Use testnet/sandbox** environments
2. **Test with real API keys** (separate from production)
3. **Verify data accuracy** against exchange UI
4. **Test error scenarios** (invalid symbols, network issues)

### Debugging Tips

1. **Enable debug logging** in settings
2. **Log raw messages** before parsing
3. **Use Visual Studio debugger** with breakpoints
4. **Monitor memory usage** for leaks
5. **Check WebSocket state** regularly

## Deployment

### Build Configuration

```xml
<PropertyGroup>
    <TargetFramework>net8.0-windows8.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
</PropertyGroup>
```

### Distribution

1. **Build in Release mode**
2. **Copy DLL to plugins folder**
3. **Include any dependencies** (NuGet packages)
4. **Test loading** in VisualHFT

### Versioning

Use semantic versioning (MAJOR.MINOR.PATCH):
- **MAJOR** - Breaking changes
- **MINOR** - New features
- **PATCH** - Bug fixes

## Troubleshooting

### Common Issues

**Plugin not discovered**
- Check .NET version compatibility
- Verify DLL is in plugins folder
- Ensure VisualHFT.Commons is referenced

**Connection fails**
- Verify API credentials
- Check firewall/proxy settings
- Validate endpoint URLs

**Data not appearing**
- Check symbol format
- Verify subscription messages
- Look for parsing errors in logs

**Performance issues**
- Profile memory allocations
- Check for blocking calls
- Optimize hot paths

### Getting Help

1. **Check the logs** in VisualHFT log folder
2. **Review sample implementations** (Binance, Kraken)
3. **Search existing issues** on GitHub
4. **Create a detailed bug report** with:
   - Exchange name
   - Plugin version
   - Steps to reproduce
   - Log files
   - Sample messages

## Additional Resources

- [VisualHFT Documentation](https://docs.visualhft.com)
- [Exchange API Documentation] (your exchange's docs)
- [WebSocket Best Practices](https://tools.ietf.org/html/rfc6455)
- [C# Async Programming](https://docs.microsoft.com/en-us/dotnet/csharp/async)

---

Remember: This is a living document. Update it as you discover new patterns or best practices while developing your connector!
