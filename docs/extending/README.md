# Extending VisualHFT

VisualHFT supports two kinds of public extensions.

| Extension | Start here | Purpose |
| --- | --- | --- |
| Market connector | [Market Connector Template](../../SDK-MarketConnectorTemplate/README.md) | Connect a venue and publish normalised market data. |
| Study | [Study Plugin Template](../../SDK-StudyTemplate/README.md) | Calculate and publish a market-microstructure metric. |

Both templates compile against the current shared plugin APIs. Follow the linked SDK guide in the matching template before changing the scaffold.

## Loading an extension

Each host discovers plugin DLLs in its own base directory. For a source build, add the extension project as a `ProjectReference` of the host that should ship it: `VisualHFT.csproj` for the WPF host, `VisualHFT.Desktop.csproj` for the Avalonia host. Rebuild that host. No manual registration is required. WPF settings views stay on the `net10.0-windows` target. The Avalonia host edits the settings model when `GetUISettings()` returns null.

See the [architecture overview](../architecture.md) for the data flow and plugin boundary.
