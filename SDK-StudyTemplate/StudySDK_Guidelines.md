# Study Plugin Development Guidelines

This guide provides comprehensive instructions for developing study plugins for VisualHFT.

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Development Setup](#development-setup)
4. [Core Concepts](#core-concepts)
5. [Implementation Guide](#implementation-guide)
6. [Best Practices](#best-practices)
7. [Advanced Topics](#advanced-topics)
8. [Testing](#testing)
9. [Deployment](#deployment)

## Overview

Study plugins extend VisualHFT's analytical capabilities by processing market data to generate insights, indicators, and alerts. They differ from market connectors in that they consume data rather than produce it.

### Key Responsibilities

- **Process Market Data**: Receive and analyze order book updates
- **Perform Calculations**: Implement study-specific algorithms
- **Generate Results**: Emit calculated values through events
- **Trigger Alerts**: Notify users of significant conditions
- **Provide Configuration**: Offer UI for parameter adjustment

## Architecture

### Class Hierarchy

```
IPlugin
├── BasePluginStudy (Abstract)
    └── YourStudyPlugin
```

### Key Components

1. **BasePluginStudy**: Provides infrastructure for data handling
2. **PlugInSettings**: Configuration model
3. **PluginSettingsViewModel**: MVVM view model for settings
4. **PluginSettingsView**: WPF UI for configuration

## Development Setup

### Prerequisites

- Visual Studio 2022 or later
- .NET 8.0 SDK
- Understanding of C# and WPF
- Familiarity with financial market data

### Project Setup

1. Copy the template folder to a new location
2. Rename the project file and update namespaces
3. Add any required NuGet packages
4. Configure project references

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.17763.0</TargetFramework>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  
  <ItemGroup>
    <ProjectReference Include="..\VisualHFT.Commons\VisualHFT.Commons.csproj" />
    <ProjectReference Include="..\VisualHFT.Commons.WPF\VisualHFT.Commons.WPF.csproj" />
  </ItemGroup>
</Project>
```

## Core Concepts

### Data Flow

```
Market Data → Queue → Calculate() → OnCalculated Event → UI/Consumers
                     ↓
                 Alert Check → OnAlertTriggered Event
```

### Lifecycle Management

1. **Construction**: Initialize basic properties
2. **Settings Load**: Load persisted configuration
3. **Start**: Begin data subscription
4. **Processing**: Handle incoming data
5. **Stop**: Cease operations
6. **Dispose**: Clean up resources

### Thread Safety

- Use locks for shared state
- Avoid blocking operations in Calculate()
- Use thread-safe collections when needed

## Implementation Guide

### 1. Create the Plugin Class

```csharp
public class MyStudyPlugin : BasePluginStudy
{
    private MySettings _settings;
    
    public override string Name { get; set; } = "My Study";
    public override string Version { get; set; } = "1.0.0";
    
    protected override void Calculate(List<BookItem> data)
    {
        // Implementation here
    }
}
```

### 2. Define Settings

```csharp
public class MySettings : ISetting
{
    public string Symbol { get; set; }
    public Provider Provider { get; set; }
    public int Period { get; set; } = 14;
    public double Threshold { get; set; } = 0.5;
}
```

### 3. Implement Calculation Logic

```csharp
protected override void Calculate(List<BookItem> data)
{
    if (data == null || data.Count == 0) return;
    
    // Your algorithm
    var result = PerformCalculation(data);
    
    // Emit result
    var model = new BaseStudyModel
    {
        Timestamp = DateTime.UtcNow,
        Value = result,
        Symbol = _settings.Symbol,
        Provider = _settings.Provider.ProviderID
    };
    
    OnCalculated?.Invoke(this, model);
}
```

### 4. Handle Alerts

```csharp
private void CheckAlert(double value)
{
    if (value > _settings.Threshold)
    {
        OnAlertTriggered?.Invoke(this, (decimal)value);
    }
}
```

## Best Practices

### Performance Optimization

1. **Minimize Allocations**: Reuse objects where possible
2. **Efficient Data Structures**: Use appropriate collections
3. **Async Operations**: Use async/await for I/O operations
4. **Batch Processing**: Process multiple items when possible

### Code Organization

1. **Separate Concerns**: Keep calculation logic separate from UI
2. **Use Regions**: Organize code with #region blocks
3. **Document Methods**: Use XML documentation
4. **Follow Naming Conventions**: Use C# naming standards

### Error Handling

```csharp
protected override void Calculate(List<BookItem> data)
{
    try
    {
        // Calculation logic
    }
    catch (Exception ex)
    {
        log.Error("Calculation error", ex);
        // Continue processing or recover gracefully
    }
}
```

## Advanced Topics

### Custom Data Models

Create custom models for complex studies:

```csharp
public class MyStudyModel : BaseStudyModel
{
    public double UpperBand { get; set; }
    public double LowerBand { get; set; }
    public double Signal { get; set; }
}
```

### Historical Data Access

Access historical data for calculations:

```csharp
protected override void Calculate(List<BookItem> data)
{
    // Get historical data
    var historical = GetDataHistory(_settings.HistoryPeriod);
    
    // Combine with current data
    var allData = historical.Concat(data).ToList();
    
    // Perform calculation
}
```

### Multi-Symbol Studies

Handle multiple symbols:

```csharp
public class MultiSymbolSettings : ISetting
{
    public List<string> Symbols { get; set; } = new();
    public Provider Provider { get; set; }
}
```

### Stateful Studies

Maintain state between calculations:

```csharp
private readonly Queue<double> _priceHistory = new();
private double _previousValue = 0;

protected override void Calculate(List<BookItem> data)
{
    // Update history
    _priceHistory.Enqueue(currentPrice);
    if (_priceHistory.Count > _settings.Period)
        _priceHistory.Dequeue();
    
    // Use state in calculation
    var result = CalculateWithHistory(_priceHistory);
}
```

## Testing

### Unit Testing

```csharp
[Test]
public void TestCalculation()
{
    // Arrange
    var plugin = new MyStudyPlugin();
    var testData = CreateTestData();
    
    // Act
    plugin.Calculate(testData);
    
    // Assert
    Assert.AreEqual(expected, plugin.LastValue);
}
```

### Integration Testing

1. Test with real market data
2. Verify event emissions
3. Check alert triggering
4. Validate settings persistence

### Performance Testing

```csharp
[TestMethod]
public void BenchmarkCalculation()
{
    var stopwatch = Stopwatch.StartNew();
    
    for (int i = 0; i < 10000; i++)
    {
        plugin.Calculate(testData);
    }
    
    stopwatch.Stop();
    Assert.IsTrue(stopwatch.ElapsedMilliseconds < 1000);
}
```

## Deployment

### Build Configuration

```xml
<PropertyGroup>
    <Configuration>Release</Configuration>
    <Optimize>true</Optimize>
    <DebugType>pdbonly</DebugType>
</PropertyGroup>
```

### Installation Steps

1. Build the project
2. Copy DLL to VisualHFT/Plugins
3. Add configuration if needed
4. Restart VisualHFT
5. Verify in Plugin Manager

### Version Management

- Use semantic versioning (MAJOR.MINOR.PATCH)
- Update assembly version
- Document breaking changes
- Maintain backward compatibility

## Troubleshooting

### Common Issues

1. **Plugin Not Loading**: Check dependencies and .NET version
2. **No Data Received**: Verify provider and symbol settings
3. **Calculation Errors**: Check data validation
4. **UI Not Showing**: Verify WPF references

### Debugging Tips

1. Use log4net for logging
2. Attach debugger to VisualHFT process
3. Use Visual Studio's Diagnostic Tools
4. Check Windows Event Viewer for errors

### Performance Issues

1. Profile with Visual Studio Profiler
2. Check for memory leaks
3. Optimize hot paths
4. Consider parallel processing

## Examples

### Simple Moving Average

```csharp
private readonly Queue<double> _prices = new();

protected override void Calculate(List<BookItem> data)
{
    var midPrice = (data[0].Price + data[1].Price) / 2;
    _prices.Enqueue(midPrice);
    
    if (_prices.Count > _settings.Period)
        _prices.Dequeue();
    
    if (_prices.Count == _settings.Period)
    {
        var sma = _prices.Average();
        EmitResult(sma);
    }
}
```

### RSI Calculation

```csharp
private double _previousGain = 0;
private double _previousLoss = 0;

protected override void Calculate(List<BookItem> data)
{
    var change = GetCurrentChange(data);
    var gain = Math.Max(0, change);
    var loss = Math.Max(0, -change);
    
    var avgGain = (_previousGain * (_settings.Period - 1) + gain) / _settings.Period;
    var avgLoss = (_previousLoss * (_settings.Period - 1) + loss) / _settings.Period;
    
    var rs = avgGain / avgLoss;
    var rsi = 100 - (100 / (1 + rs));
    
    _previousGain = avgGain;
    _previousLoss = avgLoss;
    
    EmitResult(rsi);
}
```

## Resources

- [VisualHFT Documentation](../../docs/)
- [BasePluginStudy Source](../../VisualHFT.Commons/PluginManager/BasePluginStudy.cs)
- [Example Studies](../VisualHFT.Plugins/Studies.*/)
- [Market Data Models](../../VisualHFT/Model/)

## Support

For questions or issues:

1. Check existing examples
2. Review log files
3. Contact the development team
4. Create an issue in the repository
