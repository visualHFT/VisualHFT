# Market Connector Template

This directory contains a comprehensive template for building market‑data connectors for **VisualHFT**. The template includes all necessary components, best practices, and documentation to help developers create robust plugins from scratch.

## Quick Start

1. **Copy the template** - Duplicate this folder and rename it to your exchange name
2. **Update namespace** - Replace `MarketConnector.Template` with your exchange name (e.g., `MarketConnector.MyExchange`)
3. **Add exchange client** - Install your exchange's NuGet package in the .csproj file
4. **Implement connection logic** - Follow the TODO comments in `TemplateExchangePlugin.cs`
5. **Customize message parsing** - Update `JsonParser.cs` to match your exchange's format
6. **Build and test** - Compile and place the DLL in VisualHFT's plugins folder

## Documentation

- **[MarketConnectorSDK_Guidelines.md](MarketConnectorSDK_Guidelines.md)** - Comprehensive development guide
- **[SampleMessages/README.md](SampleMessages/README.md)** - Example message formats for testing

## Template Structure

```
SDK-MarketConnectorTemplate/
├── TemplateExchangePlugin.cs        # Main plugin class in root (matches Binance, Kraken pattern)
├── JsonParser.cs                    # Message parsing logic
├── MarketConnector.Template.csproj  # Project configuration
├── ViewModels/                      # MVVM ViewModels
│   └── PluginSettingsViewModel.cs   # Settings UI logic with validation
├── Model/                          # Data models
│   ├── PlugInSettings.cs           # Configuration settings (matches Binance, Kraken pattern)
│   └── ExchangeMessages.cs         # Exchange-specific message models
├── UserControls/                   # WPF UI
│   ├── PluginSettingsView.xaml     # Settings UI definition
│   └── PluginSettingsView.xaml.cs  # Code-behind
├── SampleMessages/                 # Test data
│   ├── OrderBookSnapshot.json      # Sample order book
│   ├── OrderBookUpdate.json        # Sample updates
│   ├── Trade.json                  # Sample trade
│   ├── Error.json                  # Sample error
│   ├── Subscription.json           # Sample subscription
│   └── README.md                   # Usage guide
├── MarketConnectorSDK_Guidelines.md # Development guide
└── README.md                       # This file
```

## Key Features Included

✅ **Complete Plugin Architecture** - Full implementation with proper inheritance from `BasePluginDataRetriever`  
✅ **WebSocket Connection Handling** - Robust connection management with reconnection logic  
✅ **Message Parsing Framework** - Flexible JSON parser with error handling  
✅ **MVVM Settings UI** - WPF user control with validation and data binding  
✅ **Comprehensive Documentation** - Detailed guidelines and examples  
✅ **Sample Test Data** - JSON message samples for development and testing  
✅ **Error Handling & Logging** - Production-ready error management  
✅ **Resource Management** - Proper disposal and cleanup patterns  

## Development Checklist

- [ ] Update namespace from `MarketConnector.Template` to your exchange name
- [ ] Add your exchange's NuGet package to `.csproj`
- [ ] Implement WebSocket connection in `StartAsync()`
- [ ] Update message models in `Model/ExchangeMessages.cs`
- [ ] Customize parsing logic in `JsonParser.cs`
- [ ] Add exchange-specific validation in settings
- [ ] Test with sample messages in `SampleMessages/`
- [ ] Update provider ID and metadata
- [ ] Add unit tests for parser logic
- [ ] Test with exchange's testnet/sandbox

## Common Customizations

### Authentication
```csharp
// Add auth headers in ConnectWebSocket()
_webSocket.Options.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");
```

### Custom Message Types
```csharp
// Add new message types in JsonParser.cs
private bool IsCustomMessage(JObject message)
{
    return message.ContainsKey("yourCustomField");
}
```

### Additional Settings
```csharp
// Extend PlugInSettings.cs
[Description("Your custom setting")]
public string CustomSetting { get; set; }
```

## Getting Help

1. Read the **[MarketConnectorSDK_Guidelines.md](MarketConnectorSDK_Guidelines.md)** for detailed instructions
2. Check existing connectors (Binance, Kraken) for reference implementations
3. Use the sample messages to test your parser
4. Enable debug logging for troubleshooting

## Contributing

To improve this template:
1. Fork the repository
2. Make your changes
3. Add documentation for new features
4. Submit a pull request

---

**Happy coding!** 🚀