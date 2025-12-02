# Release Notes
See details [here](#release-notes-1)

# Coming Soon
- **Ability to connect to any market data source** (equities, futures, forex and news feeds) via new data‑retriever plug‑ins.
- **Open plug‑in architecture** for third‑party developers to add analytics, data sources and more.
- **New advanced studies** on market microstructure.
- **Trading surveillance & infrastructure monitoring** tools.
- and much more...

We are open to hearing from the community to add more features. Make sure you open new Issues with suggestions.


# VisualHFT

**VisualHFT** is an open‑source desktop application for real‑time market microstructure analysis. Built in C# using WPF, it renders the limit‑order book, trades and strategy exposures from multiple venues so traders and quants can observe order‑flow dynamics and algorithmic behaviour. 

The platform emphasises a modular plug‑in architecture – new market‑connectors and analytics can be added without modifying the core.
Learn more about **VisualHFT**'s architecture [here](https://visualhft.github.io/VisualHFT/system-architecture.html)

![Limit Order Book Visualization](https://github.com/silahian/VisualHFT/blob/master/docImages/LOB_imbalances_2.gif)


## Key Capabilities

- **Real‑time market data via plug‑ins**: **VisualHFT** retrieves market data through plug‑ins. The open‑source edition ships with plug‑ins for several cryptocurrency venues: **Binance, Bitfinex, BitStamp, Coinbase, Gemini, Kraken, KuCoin** and a generic WebSocket connector. Each plug‑in normalises order‑book and trade updates and pushes them into the common data bus.
- **Real‑time market microstructure visualisation**: View the full depth of the order book (10+ levels per side), recent trades and order flow dynamics in real time. The WPF UI uses high‑performance charts to display depth, spreads and other microstructure patterns.
- **Advanced microstructure studies**: Built‑in study plug‑ins compute microstructure analytics like **VPIN** (Volume‑synchronised Probability of Informed Trading), **LOB Imbalance**, **Market Resilience** and **OTT Ratio**. Each study listens to order book/trade events and publishes computed metrics via the trigger engine.
- **Trigger engine & alerts**: A rules‑based trigger engine registers metrics from study plug‑ins and evaluates user‑defined conditions (e.g., “VPIN > threshold”). Alerts can be surfaced through the UI or used by custom plug‑ins.
- **Modular plug‑in architecture**: All connectors and studies implement an `IPlugin` interface and are loaded at runtime by the `PluginManager`. New data sources or analytics can be added by developing a DLL that implements the appropriate base class and dropping it in the plug‑ins folder.


## Getting started
1. **Prerequisites**: **VisualHFT** targets .NET 7.0. Ensure you have a recent .NET SDK and Visual Studio Community version.
2. **Clone the repository**: `git clone https://github.com/visualHFT/VisualHFT.git`
3. **Build the solution**: Open `VisualHFT.sln` in Visual Studio and build. The solution includes the core application, the commons library, the WPF UI and several plug‑ins. The plug‑ins are referenced projects and will be built automatically.
4. **Run VisualHFT**: Launch the **VisualHFT** project. On startup you will be prompted to select a provider (venue) and symbol. Choose one of the available venues (e.g., Binance or Kraken) and a supported symbol (e.g., `BTC-USD`, which is the normalized symbol) to begin streaming data.

## Features
- **Real-time market data from any source**: Add multiple market data using plugins.
- **Real-Time Market Microstructure Visualization**: Detailed view of market dynamics, including Limit Order Book movements.
- **Advanced Execution Quality Analysis**: Tools to assess and optimize trade execution and reduce slippage.
- **Interactive Charts and Graphs**: Dynamic and interactive visual representations of market data.
- **User-Centric Design**: An intuitive interface designed for ease of use, making complex data accessible.
- **Performance Metrics and Reporting**: Robust reporting tools to track and analyze trading performance metrics.
- **Real‑Time Data Bus**: Helper classes (`HelperOrderBook`, `HelperTrade`, etc.) act as a real‑time bus. Data‑retriever plug‑ins push order‑book and trade events into these helpers; the UI and study plug‑ins subscribe to them.
- **Interactive Charts**: **OxyPlot**‑based charts display depth, spread, volumes and study outputs in real time.
- **User‑Centric Design**: The interface emphasises clarity, speed and customisation. Users can configure which studies are active, adjust triggers and save layout settings.
- **Extensibility**: Developers can build their own plug‑ins for proprietary data feeds or custom analytics. See the `VisualHFT.Plugins` folder for examples of data‑retriever and study plug‑ins.

Even though some of these items do not yet appear in the open‑source code, they are part of the project’s roadmap and will be added as development continues.

## Performance Architecture

VisualHFT uses a **multicast ring buffer** architecture for its real-time data bus, providing:

| Metric | Performance |
|--------|-------------|
| Producer Latency (p50) | 50-100 nanoseconds |
| Consumer Latency (p50) | 30-50 nanoseconds |
| Throughput | 50-100M messages/second |
| GC Allocations | Zero (modern API) |

**Key Features:**
- **Lock-free design**: No blocking, no contention
- **Independent consumers**: Slow subscribers do not affect others
- **Zero-copy API**: `ImmutableOrderBook` for studies that only read data
- **Backward compatible**: Existing `Action<OrderBook>` callbacks still work

See the [Multicast Ring Buffer Architecture](docs/MulticastRingBuffer-Architecture.md) documentation for migration guides and technical details.


## About the founder
Ariel Silahian has been building high-frequency trading software for the past 10 years. Primarily using C++, for the core system, which always runs in a collocated server next to the exchange.

He's a passionate software engineer with a deep interest in the world of electronic trading. With extensive experience in the field, he has developed a keen understanding of the complexities and challenges that traders face in today's fast-paced, high-frequency trading environment.

His journey in electronic trading began with his work at a proprietary trading firm, where he was involved in developing and optimizing high-frequency trading systems. This experience gave him a firsthand look at the need for tools that provide real-time insights into trading operations, leading to the creation of **VisualHFT**.

In addition to his work in electronic trading, he has a broad background in software development, with skills in a range of programming languages and technologies. He's always eager to learn and explore new areas, and he believes in the power of open-source software to drive innovation and collaboration.

Through **VisualHFT**, we hope to contribute to the trading community and provide a valuable tool for traders and developers alike. We welcome feedback and contributions to the project and look forward to seeing how it evolves with the input of the community.


## Screenshots

![Trading Statistics](/docImages/Aspose.Words.5b849bdf-d96d-4013-ad76-8c3daba3aead.003.png)
![Depth LOB](/docImages/Aspose.Words.5b849bdf-d96d-4013-ad76-8c3daba3aead.004.png)
![Analytics](/docImages/Aspose.Words.5b849bdf-d96d-4013-ad76-8c3daba3aead.005.png)
![Charts](/docImages/Aspose.Words.5b849bdf-d96d-4013-ad76-8c3daba3aead.006.png)
![Limit Order Book](/docImages/Aspose.Words.5b849bdf-d96d-4013-ad76-8c3daba3aead.007.png)
![Stats](/docImages/Aspose.Words.5b849bdf-d96d-4013-ad76-8c3daba3aead.008.png)


## Contributing

If you are interested in reporting/fixing issues and contributing directly to the code base, please see [CONTRIBUTING.md](CONTRIBUTING.md) for more information on what we're looking for and how to get started.

> Important: We **will not accept** any changes to any of the existing input json message format. This is fixed and cannot be changed. The main reason for this is that we can break all existing installations of this system. Unless there is a “very strong” case that needs to be addressed, and all the community agrees upon that. However, we could accept having new json messages, to be parsed and processed accordingly, without breaking any of the existing ones.*


## How to contact us
For project questions use the repository’s forums or any of my social media profiles.
[Twitter](https://twitter.com/sisSoftware) | [LinkedIn](https://www.linkedin.com/in/silahian/) | Forums


# Release notes
### Mar 16 2025
**Enhancements**
- **New Plugins Added:**
  - BitStamp
  - Gemini
  - Kraken
  - KuCoin

- **Plugin Improvements:**
  - Enhanced plugin lifecycle, allowing each plugin to have its own autonomy without affecting the core system (reconnection, auto stopping, etc.).
  - Improved error handling within plugins.
  - Introduced a new module to handle all exceptions and notifications from plugins and the core system without disrupting operations.

- **Performance Improvements:**
  - Incorporated custom queues that improve performance and throughput by 40%.
  - Implemented custom object pools, enhancing memory allocation throughout the system.
  - Re-organized data structures and code for better usage, with significant improvements in performance and memory handling.
  - Optimized order book data structures for faster lookups and updates.

For detailed changes, refer to [pull request #41](https://github.com/visualHFT/VisualHFT/pull/41) and [pull request #36](https://github.com/visualHFT/VisualHFT/pull/36).

### Jun 26 2024
**Enhancements**
- **Performance Improvements:**
  - Incorporated custom queues that improve performance and throughput by 40%.
  - Implemented custom object pools, enhancing memory allocation throughout the system.
- **Limit Order Book:**
  - Re-organized data structures and code for better usage, with significant improvements in performance and memory handling.
  - Optimized order book data structures for faster lookups and updates.
- **Plugins:**
  - Improved plugin lifecycle, allowing each plugin to have its own autonomy without affecting the core system (reconnection, auto stopping, etc.).
  - Enhanced error handling within plugins.
- **Notification Center:**
  - Introduced a new module to handle all exceptions and notifications from plugins and the core system without disrupting operations.
  - Improved UI experience for notifications.
- **Code Cleanup:**
  - Removed unused third-party packages and modules.
  - Refactored code to remove unnecessary database access from the core, now handled by plugins if needed.

These updates focus on enhancing system performance, reliability, and maintainability. For detailed changes, refer to [pull request #36](https://github.com/silahian/VisualHFT/pull/36).


### Oct 27 2023
**Enhancements**
- **Plugin Architecture**: Revamped the entire plug-in architecture. It is very easy to add new plugins to increase functionality.
- **Performance**: Improved performance by 200%. Refactored events and queues.


### Oct 19 2023

**Enhancements**
- **Memory Optimization with Object Pooling**: Introduced object pooling to reduce memory allocations in ProcessBufferedTrades method by reusing Trade and OrderBook objects.
- **Optimizing Real-Time Data Processing**: Replaced Task.Delay(0) with more efficient mechanisms like ManualResetEventSlim or BlockingCollection to handle high-frequency real-time data processing with lower latency and CPU usage.
- **Data Copy Optimization**: Implemented a CopyTo method to efficiently copy data between objects, facilitating object reuse and reducing memory allocations.
- **Converting Queue to BlockingCollection**: Transitioned from using Queue<IBinanceTrade> to BlockingCollection<IBinanceTrade> for thread-safe and efficient data processing in a multi-threaded environment.
- **Efficient Data Processing with BlockingCollection**: Utilized BlockingCollection<T> methods like Take and GetConsumingEnumerable to efficiently process data from different threads, ensuring thread-safety and reduced latency in high-frequency real-time analytic systems.


### Oct 02 2023

**New Features**
- **Plugin System Integration**: Incorporated the ability to use plugins within the application, allowing for modular expansion and customization. This will allow us to incorporate more functionalities in a modular fashion. Also, it will allow for 3rd party developers to expand VisualHFT even further.
- **Sample Plugins**: Added two sample plugins that serve as connectors to **Binance** and **Bitfinex** exchanges, demonstrating the capability and flexibility of the new plugin system. With this, VisualHFT won't need to run the **demoTradingCore** project anymore.
- **Plugin Manager UI**: Introduced a user interface for managing plugins. This allows users to load, unload, start, stop, and configure plugins.
- Plugin Normalization: Implemented a symbol normalization feature to allow users to analyze data from different exchanges in a standardized format. This ensures a consistent analysis across various exchanges.
- **Dynamic Plugin Settings UI**: Enhanced the plugin system to support dynamic user interface elements for plugin settings. This allows plugins to provide their own UI for configuration.
- **Performance Optimizations**: Introduced various performance improvements, including optimized data structures and multi-threading strategies.

**Enhancements**
- Improved Error Handling: Integrated more robust error handling mechanisms, especially for plugins. Plugins can now report errors which can either be logged or displayed to the user based on their severity.
- Base Class Refinements: Enhanced the base class for plugins to provide more features out-of-the-box, making it easier for third-party developers to create plugins.
- Tooltip for Symbol Normalization: Added detailed tooltips to guide users on how to use the symbol normalization feature.
- Code Refactoring: Refactored various parts of the code to improve maintainability, readability, and performance.


### Sep 22 2023
- Architectural improvement: Rearranged classes around to improve project structure.
- Improved performance overall:
    - Gradually separating pure “model” classes from “model view” classes. This will improve the MVVM architecture and it will give a performance boost, since model are light-weight.
    - Created custom collections and cache capability
    - UI updates improved for a flawless visualization
    - Improvement in memory usage
- Preparing the architecture, to introduce Plugins: these plugins will act as independent components, letting the community create new ones and have VisualHFT easily use them.
- Added Tiles into the dashboard, with different metrics. With the ability to launch realtime charts for each of them. The following list of metrics has been added:
    - VPIN
    - LOB Imbalance
    - TTO: trade to trade ratio
    - OTT: order to trade ratio
    - Market Resilience
    - Market Resilience Bias
- Multi Venue (providers) price chart
- Updated to latest .NET Framework .NET 7.0
