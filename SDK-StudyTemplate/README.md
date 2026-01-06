# Study Plugin Template

This directory contains a comprehensive template for building study plugins for **VisualHFT**. The template includes all necessary components, best practices, and documentation to help developers create robust study plugins from scratch.

## Quick Start

1. **Copy the template** - Duplicate this folder and rename it to your study name
2. **Update namespace** - Replace `VisualHFT.Studies.Template` with your study name (e.g., `VisualHFT.Studies.MyStudy`)
3. **Implement calculation logic** - Follow the TODO comments in `TemplateStudyPlugin.cs`
4. **Customize settings** - Update `PlugInSettings.cs` to match your study's configuration needs
5. **Modify the UI** - Adjust `PluginSettingsView.xaml` for your specific settings
6. **Build and test** - Compile and place the DLL in VisualHFT's plugins folder

## Documentation

- **[StudySDK_Guidelines.md](StudySDK_Guidelines.md)** - Comprehensive development guide for study plugins

## Template Structure

```
SDK-StudyTemplate/
├── TemplateStudyPlugin.cs           # Main plugin class derived from BasePluginStudy
├── Study.Template.csproj           # Project configuration
├── ViewModels/                     # MVVM ViewModels
│   ├── PluginSettingsViewModel.cs  # Settings UI logic with validation
│   └── TemplateStudyViewModel.cs   # Optional: Custom UI view model
├── Model/                          # Data models
│   └── PlugInSettings.cs           # Configuration settings for the study
├── UserControls/                   # WPF UI
│   ├── PluginSettingsView.xaml     # Settings UI definition
│   ├── PluginSettingsView.xaml.cs  # Code-behind
│   ├── TemplateStudyView.xaml      # Optional: Custom visualization UI
│   └── TemplateStudyView.xaml.cs   # Optional: Custom visualization code-behind
├── StudySDK_Guidelines.md          # Development guide
└── README.md                       # This file
```

## Key Features Included

✅ **Complete Study Architecture** - Full implementation with proper inheritance from `BasePluginStudy`  
✅ **Market Data Processing** - Built-in hooks for processing order book data  
✅ **Calculation Framework** - Structure for implementing custom study calculations  
✅ **Alert System** - Built-in alert triggering with configurable thresholds  
✅ **MVVM Settings UI** - WPF user control with validation and data binding  
✅ **Optional Custom UI** - Template for creating custom visualizations (charts, indicators)  
✅ **Thread-Safe Operations** - Proper locking mechanisms for concurrent access  
✅ **Error Handling** - Comprehensive logging and error management  
✅ **Resource Management** - Proper disposal pattern implementation  

## Understanding Study Plugins

Study plugins in VisualHFT are designed to analyze market data and generate insights, indicators, or alerts. Unlike market connectors that fetch data, study plugins:

1. **Receive market data** through the base class infrastructure
2. **Perform calculations** on the received data
3. **Emit results** through the `OnCalculated` event
4. **Trigger alerts** when conditions are met through `OnAlertTriggered`
5. **Provide configuration** through a settings UI

## Core Concepts

### BasePluginStudy

The `BasePluginStudy` class provides:

- **Data Subscription**: Automatic subscription to market data based on settings
- **Queue Management**: Thread-safe queue for incoming market data
- **Calculation Hooks**: Override `Calculate()` method to implement your logic
- **Event Infrastructure**: Built-in events for results and alerts
- **Settings Management**: Load/save/initialize settings framework

### Study Lifecycle

1. **Initialization**: Constructor runs, settings are loaded
2. **Start**: `StartAsync()` called, data subscription begins
3. **Processing**: Market data triggers `Calculate()` method
4. **Results**: `OnCalculated` event emits study results
5. **Alerts**: `OnAlertTriggered` event fires on alert conditions
6. **Stop**: `StopAsync()` called, subscription ends
7. **Dispose**: Resources cleaned up

## Implementation Steps

### 1. Define Your Study Logic

```csharp
protected override void Calculate(List<BookItem> data)
{
    // Your calculation logic here
    // Example: Moving average, volatility, correlation, etc.
    
    var result = YourCalculation(data);
    
    // Emit result
    var studyModel = new BaseStudyModel
    {
        Timestamp = DateTime.UtcNow,
        Value = result,
        Symbol = _settings.Symbol,
        Provider = _settings.Provider.ProviderID
    };
    
    OnCalculated?.Invoke(this, studyModel);
}
```

### 2. Configure Settings

Edit `PlugInSettings.cs` to add your study-specific parameters:

```csharp
public class PlugInSettings : ISetting
{
    // Standard settings
    public string Symbol { get; set; }
    public Provider Provider { get; set; }
    public AggregationLevel AggregationLevel { get; set; }
    
    // Your custom settings
    public int LookbackPeriod { get; set; } = 20;
    public double Threshold { get; set; } = 0.5;
    public bool EnableSmoothing { get; set; } = true;
}
```

### 3. Update the UI

Modify `PluginSettingsView.xaml` to include controls for your settings:

```xml
<!-- Add your custom controls -->
<Label Content="Lookback Period"/>
<TextBox Text="{Binding LookbackPeriod, UpdateSourceTrigger=PropertyChanged}" />

<Label Content="Threshold"/>
<TextBox Text="{Binding Threshold, UpdateSourceTrigger=PropertyChanged}" />
```

### 4. (Optional) Create Custom Visualization

If your study needs custom visualization:

```csharp
// In your plugin class
private TemplateStudyView _customView;

public override void Initialize()
{
    _customView = new TemplateStudyView();
    // Register with VisualHFT framework
}

// Update the view when calculations complete
private void UpdateVisualization(double value)
{
    _customView?.UpdateValue(value, DateTime.UtcNow);
}
```

### 5. Implement Alert Logic

```csharp
private void CheckAlertCondition(double value)
{
    if (value > _settings.Threshold)
    {
        OnAlertTriggered?.Invoke(this, (decimal)value);
    }
}
```

## Best Practices

1. **Thread Safety**: Always use locks when accessing shared state
2. **Performance**: Minimize allocations in the `Calculate()` method
3. **Validation**: Validate all settings before starting
4. **Logging**: Log important events and errors
5. **Disposal**: Properly dispose of resources in `Dispose()`

## Common Study Types

- **Technical Indicators**: RSI, MACD, Bollinger Bands
- **Statistical Analysis**: Volatility, Correlation, Regression
- **Order Book Analysis**: Imbalance, Depth, Spread Analysis
- **Volume Analysis**: VWAP, Volume Profile, Flow Analysis
- **Custom Algorithms**: Proprietary calculations and signals

## Testing Your Plugin

1. **Unit Tests**: Test calculation logic with sample data
2. **Integration Tests**: Test with live market data
3. **UI Tests**: Verify settings validation and persistence
4. **Performance Tests**: Monitor CPU/memory usage

## Deployment

1. Build the project in Release mode
2. Copy the DLL to VisualHFT's Plugins folder
3. Restart VisualHFT
4. Configure your study in the Settings dialog
5. Add to a dashboard to see results

## Support

- Check the [VisualHFT documentation](../../docs/) for more details
- Review existing study plugins in `VisualHFT.Plugins/Studies.*` for examples
- Use the debug log to troubleshoot issues
