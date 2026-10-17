# Getting started

## Prerequisites

- Windows
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio with .NET desktop development tools
- The VisualHFT OxyPlot fork cloned beside this repository

## Clone the repositories

`VisualHFT.csproj` references the VisualHFT OxyPlot fork by a sibling path. Clone both repositories into the same parent directory.

```powershell
git clone https://github.com/visualHFT/oxyplot.git
git clone https://github.com/visualHFT/VisualHFT.git
```

The resulting folders should look like this:

```text
parent-folder/
├── oxyplot/
└── VisualHFT/
```

## Build and run

1. Open `VisualHFT/VisualHFT.sln` in Visual Studio.
2. Build the solution.
3. Set `VisualHFT` as the startup project.
4. Press <kbd>F5</kbd>.
5. In the Dashboard, select an available provider and a normalised symbol such as `BTC/USD`.

## macOS

The WPF host stays Windows-only. On macOS, the Avalonia host builds against the same `VisualHFT.Commons` contracts and the shared `VisualHFT.Runtime` plugin loader. It does not use the OxyPlot fork.

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
dotnet build VisualHFT.Mac.slnx
dotnet run --project VisualHFT.Desktop/VisualHFT.Desktop.csproj
```

The process starts with no ticker selected. Choose an exchange and symbol. Book, tape, and study tiles follow that selection. Market-data callbacks only copy the fields they need; the window paints those copies on a timer.

## Next steps

- Review the [architecture overview](architecture.md).
- Build a [market connector or study plugin](extending/README.md).
- Use [troubleshooting](troubleshooting.md) if the application does not build or a provider does not stream data.
