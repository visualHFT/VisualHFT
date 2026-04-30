using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.UserSettings;

namespace VisualHFT.ViewModel
{
    public enum KalshiDepthUnit
    {
        Contracts,
        Notional,
        Percent,
    }

    /// <summary>
    /// One level row in the Kalshi-style ladder (asks above, bids below).
    /// </summary>
    public class KalshiLevelRow
    {
        public bool IsAsk { get; init; }
        public double Price { get; init; }                   // cents (whole or fractional)
        public double Contracts { get; init; }
        public double CumulativeDollars { get; init; }       // running total walking away from mid
        public double BarWidth { get; set; }                  // 0..160 (px), proportional to size
        public string PriceText  => $"{Price:F0}¢";
        public string ContractsText => Contracts >= 1000
            ? $"{Contracts:N0}"
            : $"{Contracts:N2}";
        public string TotalText => FormatDollars(CumulativeDollars);

        private static string FormatDollars(double v) =>
            v >= 1_000_000 ? $"${v/1_000_000:N1}M"
          : v >= 10_000    ? $"${v/1_000:N0}K"
          : v >= 1_000     ? $"${v/1_000:N1}K"
          :                  $"${v:N2}";
    }

    /// <summary>
    /// View-model for the Kalshi ladder window. Tracks one symbol; renders the
    /// full depth ladder with cumulative dollar totals on each side.
    /// </summary>
    public sealed class vmKalshiLadder : INotifyPropertyChanged, IDisposable
    {
        // Persistence config. We store visibility + unit selection under a single
        // SettingKey, keyed by id strings so a future second knob (e.g. clip pct)
        // can join without churning the SettingKey enum again.
        private const string PREF_VISIBLE = "visible";
        private const string PREF_UNIT    = "unit";
        // Y-axis is clipped to the 90th percentile of cumulative depth so a single
        // whale level doesn't squash everything near the spread flat.
        private const double DEPTH_CLIP_PERCENTILE = 0.90;

        private readonly Action<OrderBook> _handler;
        private string _symbol = "";

        // Latest cumulative-built rows; cached so unit-mode flips can rebuild the
        // chart synchronously without waiting for the next book tick.
        private List<(double price, double contracts, double cumContracts)> _bidLevels = new();
        private List<(double price, double contracts, double cumContracts)> _askLevels = new();
        private double _midPriceValue;

        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; Notify(nameof(Symbol)); Notify(nameof(WindowTitle)); Notify(nameof(HeaderPrimary)); Notify(nameof(HeaderSecondary)); Notify(nameof(HasHeaderSecondary)); }
        }

        // Human-readable metadata, fetched async after ctor. Empty until the
        // /markets/{ticker} call returns. UI falls back to the ticker so the
        // header is never blank.
        private string _marketTitle = "";
        public string MarketTitle
        {
            get => _marketTitle;
            private set { _marketTitle = value; Notify(nameof(MarketTitle)); Notify(nameof(HeaderPrimary)); Notify(nameof(HeaderSecondary)); Notify(nameof(HasHeaderSecondary)); }
        }

        private string _marketSubtitle = "";
        public string MarketSubtitle
        {
            get => _marketSubtitle;
            private set { _marketSubtitle = value; Notify(nameof(MarketSubtitle)); Notify(nameof(HeaderPrimary)); Notify(nameof(HeaderSecondary)); Notify(nameof(HasHeaderSecondary)); }
        }

        private string _yesSubTitle = "";
        public string YesSubTitle
        {
            get => _yesSubTitle;
            private set { _yesSubTitle = value; Notify(nameof(YesSubTitle)); Notify(nameof(HeaderPrimary)); Notify(nameof(HeaderSecondary)); Notify(nameof(HasHeaderSecondary)); }
        }

        // What to show as the big header: prefer the market subtitle (most
        // distinctive — usually the strike, e.g. "Eagles win by 7+ points"),
        // then the YES outcome label, then the question, then ticker.
        public string HeaderPrimary
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_marketSubtitle)) return _marketSubtitle;
                if (!string.IsNullOrWhiteSpace(_yesSubTitle))    return _yesSubTitle;
                if (!string.IsNullOrWhiteSpace(_marketTitle))    return _marketTitle;
                return _symbol;
            }
        }

        // Secondary line: the ticker (always useful for traders) and the parent
        // question if both subtitle and title are available. Hidden until we
        // have something meaningful beyond what's already in the primary line.
        public string HeaderSecondary
        {
            get
            {
                bool primaryIsTicker = string.IsNullOrWhiteSpace(_marketSubtitle)
                                    && string.IsNullOrWhiteSpace(_yesSubTitle)
                                    && string.IsNullOrWhiteSpace(_marketTitle);
                if (primaryIsTicker) return "";
                // If the subtitle is the primary, also show the question for
                // context. Otherwise just the ticker.
                if (!string.IsNullOrWhiteSpace(_marketSubtitle) && !string.IsNullOrWhiteSpace(_marketTitle))
                    return $"{_marketTitle}  ·  {_symbol}";
                return _symbol;
            }
        }

        public bool HasHeaderSecondary => !string.IsNullOrEmpty(HeaderSecondary);

        private bool _showDepthChart;
        public bool ShowDepthChart
        {
            get => _showDepthChart;
            set
            {
                if (_showDepthChart == value) return;
                _showDepthChart = value;
                TrySaveBool(PREF_VISIBLE, value);
                Notify(nameof(ShowDepthChart));
                if (value) RebuildDepthChartModel();
            }
        }

        private KalshiDepthUnit _depthUnit = KalshiDepthUnit.Contracts;
        public KalshiDepthUnit DepthUnit
        {
            get => _depthUnit;
            set
            {
                if (_depthUnit == value) return;
                _depthUnit = value;
                TrySaveString(PREF_UNIT, value.ToString());
                Notify(nameof(DepthUnit));
                Notify(nameof(IsUnitContracts));
                Notify(nameof(IsUnitNotional));
                Notify(nameof(IsUnitPercent));
                if (_showDepthChart) RebuildDepthChartModel();
            }
        }

        public bool IsUnitContracts => _depthUnit == KalshiDepthUnit.Contracts;
        public bool IsUnitNotional  => _depthUnit == KalshiDepthUnit.Notional;
        public bool IsUnitPercent   => _depthUnit == KalshiDepthUnit.Percent;

        private PlotModel _depthChartModel;
        public PlotModel DepthChartModel
        {
            get => _depthChartModel;
            private set { _depthChartModel = value; Notify(nameof(DepthChartModel)); }
        }

        public void ToggleDepthChart() => ShowDepthChart = !ShowDepthChart;
        public void SetDepthUnit(KalshiDepthUnit u) => DepthUnit = u;

        public string WindowTitle => string.IsNullOrEmpty(_symbol)
            ? "Kalshi — Ladder"
            : $"Kalshi — {_symbol}";

        public ObservableCollection<KalshiLevelRow> Asks { get; } = new();   // shown top (descending price)
        public ObservableCollection<KalshiLevelRow> Bids { get; } = new();   // shown bottom (descending price)

        private double _bestBid; public double BestBid { get => _bestBid; set { _bestBid = value; Notify(nameof(BestBid)); Notify(nameof(BestBidText)); Notify(nameof(NoAskText)); Notify(nameof(MidPrice)); Notify(nameof(SpreadText)); } }
        private double _bestAsk; public double BestAsk { get => _bestAsk; set { _bestAsk = value; Notify(nameof(BestAsk)); Notify(nameof(BestAskText)); Notify(nameof(NoBidText)); Notify(nameof(MidPrice)); Notify(nameof(SpreadText)); } }

        public string BestBidText => BestBid > 0 ? $"{BestBid:F0}¢" : "—";
        public string BestAskText => BestAsk > 0 ? $"{BestAsk:F0}¢" : "—";
        // NO side equivalents (binary-contract identity): NO_bid = 100 - YES_ask, NO_ask = 100 - YES_bid
        public string NoBidText => BestAsk > 0 ? $"{100 - BestAsk:F0}¢" : "—";
        public string NoAskText => BestBid > 0 ? $"{100 - BestBid:F0}¢" : "—";
        public string MidPrice => (BestBid > 0 && BestAsk > 0) ? $"{(BestBid + BestAsk) / 2.0:F1}¢" : "—";
        public string SpreadText => (BestBid > 0 && BestAsk > 0) ? $"{BestAsk - BestBid:F0}¢ spread" : "";

        public vmKalshiLadder(string symbol)
        {
            Symbol = symbol;
            // Load persisted prefs before the first book tick so the chart renders
            // straight into the user's last-saved unit if they had it on.
            _showDepthChart = TryLoadBool(PREF_VISIBLE, false);
            _depthUnit      = TryLoadEnum(PREF_UNIT, KalshiDepthUnit.Contracts);
            _handler = OnBook;
            HelperOrderBook.Instance.Subscribe(_handler);
            FetchMarketInfoAsync(symbol);
        }

        private async void FetchMarketInfoAsync(string symbol)
        {
            // Best-effort metadata fetch. The header falls back to the raw ticker
            // if this fails, so we don't surface errors to the user.
            try
            {
                var info = await KalshiBrowserPoller.Instance.GetMarketInfoAsync(symbol).ConfigureAwait(true);
                MarketTitle    = info.Title;
                MarketSubtitle = info.Subtitle;
                YesSubTitle    = info.YesSubTitle;
            }
            catch { /* swallow — header just keeps the ticker */ }
        }

        private void OnBook(OrderBook ob)
        {
            if (ob is null) return;
            if (!string.Equals(ob.Symbol, _symbol, StringComparison.Ordinal)) return;

            // Snapshot off-thread
            var bidItems = ob.Bids;
            var askItems = ob.Asks;

            // Asks ordered ascending (best ask first)
            var asksList = new List<(double price, double size)>();
            for (int i = 0; i < askItems.Count(); i++)
            {
                var lvl = askItems[i];
                if (lvl.Price.HasValue && lvl.Size.HasValue && lvl.Size.Value > 0)
                    asksList.Add((lvl.Price.Value, lvl.Size.Value));
            }
            asksList.Sort((a, b) => a.price.CompareTo(b.price));

            // Bids ordered descending (best bid first)
            var bidsList = new List<(double price, double size)>();
            for (int i = 0; i < bidItems.Count(); i++)
            {
                var lvl = bidItems[i];
                if (lvl.Price.HasValue && lvl.Size.HasValue && lvl.Size.Value > 0)
                    bidsList.Add((lvl.Price.Value, lvl.Size.Value));
            }
            bidsList.Sort((a, b) => b.price.CompareTo(a.price));

            // Build cumulative-total rows
            // Asks: walk from best (lowest ask) outward, accumulating dollars (price/100 * size)
            var newAskRows = new List<KalshiLevelRow>();
            double cum = 0;
            foreach (var (p, q) in asksList)
            {
                cum += (p / 100.0) * q;
                newAskRows.Add(new KalshiLevelRow { IsAsk = true, Price = p, Contracts = q, CumulativeDollars = cum });
            }
            // For display we want highest ask at top, lowest near mid: reverse
            newAskRows.Reverse();

            var newBidRows = new List<KalshiLevelRow>();
            cum = 0;
            foreach (var (p, q) in bidsList)
            {
                cum += (p / 100.0) * q;
                newBidRows.Add(new KalshiLevelRow { IsAsk = false, Price = p, Contracts = q, CumulativeDollars = cum });
            }

            double bestB = bidsList.Count > 0 ? bidsList[0].price : 0;
            double bestA = asksList.Count > 0 ? asksList[0].price : 0;

            // Set bar widths proportional to the largest contracts size on either side.
            const double MAX_BAR_PX = 160.0;
            double maxQ = 0;
            foreach (var r in newAskRows) maxQ = Math.Max(maxQ, r.Contracts);
            foreach (var r in newBidRows) maxQ = Math.Max(maxQ, r.Contracts);
            if (maxQ > 0)
            {
                foreach (var r in newAskRows) r.BarWidth = (r.Contracts / maxQ) * MAX_BAR_PX;
                foreach (var r in newBidRows) r.BarWidth = (r.Contracts / maxQ) * MAX_BAR_PX;
            }

            // Snapshot for the depth-chart builder (price asc on each side, with
            // running cumulative contracts walking outward from mid).
            var bidChart = new List<(double, double, double)>(bidsList.Count);
            double cumBids = 0;
            foreach (var (p, q) in bidsList) { cumBids += q; bidChart.Add((p, q, cumBids)); }
            var askChart = new List<(double, double, double)>(asksList.Count);
            double cumAsks = 0;
            foreach (var (p, q) in asksList) { cumAsks += q; askChart.Add((p, q, cumAsks)); }
            double midForChart = (bestB > 0 && bestA > 0) ? (bestB + bestA) / 2.0 : 0;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Asks.Clear();
                foreach (var r in newAskRows) Asks.Add(r);
                Bids.Clear();
                foreach (var r in newBidRows) Bids.Add(r);
                BestBid = bestB;
                BestAsk = bestA;

                _bidLevels = bidChart;
                _askLevels = askChart;
                _midPriceValue = midForChart;
                if (_showDepthChart) RebuildDepthChartModel();
            });
        }

        private void RebuildDepthChartModel()
        {
            // Keep this resilient: if there is no book yet, render a blank model
            // rather than null so the PlotView clears prior data.
            var model = new PlotModel
            {
                PlotAreaBorderThickness = new OxyThickness(0, 0, 0, 1),
                PlotAreaBorderColor = OxyColor.FromArgb(255, 0x44, 0x44, 0x44),
                Background = OxyColor.FromArgb(255, 0x1A, 0x1A, 0x1A),
                TextColor = OxyColor.FromArgb(255, 0xCC, 0xCC, 0xCC),
                PlotMargins = new OxyThickness(46, 6, 46, 26),
            };

            string yTitle = DepthUnit switch
            {
                KalshiDepthUnit.Notional => "cumulative size ($)",
                KalshiDepthUnit.Percent  => "cumulative size (%)",
                _                        => "cumulative size (contracts)",
            };

            // Convert raw contracts to the requested unit. Total = sum of both
            // sides' deepest cumulative; that's what "% of total book" hangs on.
            double totalContracts = (_bidLevels.Count > 0 ? _bidLevels[^1].cumContracts : 0)
                                  + (_askLevels.Count > 0 ? _askLevels[^1].cumContracts : 0);

            double Convert(double cumContracts, double price)
            {
                return DepthUnit switch
                {
                    // Notional in dollars: cumulative contracts at that price-level
                    // would cost (price/100)*size to lift. We approximate with the
                    // level price; users mainly want order-of-magnitude here, not
                    // a tick-perfect reconstruction.
                    KalshiDepthUnit.Notional => cumContracts * (price / 100.0),
                    KalshiDepthUnit.Percent  => totalContracts > 0 ? (cumContracts / totalContracts) * 100.0 : 0,
                    _                        => cumContracts,
                };
            }

            // Bid step: walk inward (worst → best) so the curve climbs toward mid.
            // Discrete book → StairStepSeries, never smoothed.
            var bidSeries = new StairStepSeries
            {
                Color = OxyColor.FromArgb(255, 0x66, 0xBB, 0x6A),
                StrokeThickness = 1.6,
                LineStyle = LineStyle.Solid,
                VerticalStrokeThickness = 1.0,
            };
            // Bids ordered ascending price: we already have them descending in
            // _bidLevels (best first). Reverse for a left-to-right ascending walk.
            for (int i = _bidLevels.Count - 1; i >= 0; i--)
            {
                var (p, _, cum) = _bidLevels[i];
                bidSeries.Points.Add(new DataPoint(p, Convert(cum, p)));
            }

            var askSeries = new StairStepSeries
            {
                Color = OxyColor.FromArgb(255, 0xEF, 0x53, 0x50),
                StrokeThickness = 1.6,
                LineStyle = LineStyle.Solid,
                VerticalStrokeThickness = 1.0,
            };
            // Asks ordered ascending price already (best first, increasing).
            foreach (var (p, _, cum) in _askLevels)
                askSeries.Points.Add(new DataPoint(p, Convert(cum, p)));

            model.Series.Add(bidSeries);
            model.Series.Add(askSeries);

            // Compute a 90th-percentile clip on visible cumulative values so a
            // single whale far from the spread doesn't dominate the y range.
            // If everything fits, no overflow indicator. If clipped, draw a
            // small "▲ N% clipped" badge at the clip line.
            var allYs = new List<double>(bidSeries.Points.Count + askSeries.Points.Count);
            foreach (var pt in bidSeries.Points) if (!double.IsNaN(pt.Y)) allYs.Add(pt.Y);
            foreach (var pt in askSeries.Points) if (!double.IsNaN(pt.Y)) allYs.Add(pt.Y);

            double yMax = 1.0;
            int overflowCount = 0;
            if (allYs.Count > 0)
            {
                allYs.Sort();
                double rawMax = allYs[^1];
                int idx = (int)Math.Floor(DEPTH_CLIP_PERCENTILE * (allYs.Count - 1));
                double clip = allYs[idx];
                // Add a little headroom so the curve isn't crammed against the top.
                yMax = clip > 0 ? clip * 1.10 : Math.Max(rawMax, 1.0);
                if (yMax <= 0) yMax = Math.Max(rawMax, 1.0);
                if (rawMax > yMax)
                {
                    foreach (var y in allYs) if (y > yMax) overflowCount++;
                }
            }

            // X axis: price in cents. Anchored to the visible level range so the
            // chart matches the ladder, with a tiny margin either side.
            double xMin = 0, xMax = 100;
            if (_bidLevels.Count > 0 || _askLevels.Count > 0)
            {
                xMin = _bidLevels.Count > 0 ? _bidLevels[^1].price : (_askLevels.Count > 0 ? _askLevels[0].price : 0);
                xMax = _askLevels.Count > 0 ? _askLevels[^1].price : (_bidLevels.Count > 0 ? _bidLevels[0].price : 100);
                double span = Math.Max(1.0, xMax - xMin);
                xMin = Math.Max(0,   xMin - span * 0.04);
                xMax = Math.Min(100, xMax + span * 0.04);
            }

            var xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Minimum = xMin,
                Maximum = xMax,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromArgb(40, 0xFF, 0xFF, 0xFF),
                AxislineColor = OxyColor.FromArgb(180, 0x88, 0x88, 0x88),
                TextColor = OxyColor.FromArgb(255, 0xAA, 0xAA, 0xAA),
                TitleColor = OxyColor.FromArgb(255, 0x99, 0x99, 0x99),
                Title = "price (¢)",
                FontSize = 10,
                TitleFontSize = 10,
            };

            // Y axis mirrored: matching ticks on left and right edges.
            var yAxisLeft = new LinearAxis
            {
                Key = "yLeft",
                Position = AxisPosition.Left,
                Minimum = 0,
                Maximum = yMax,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromArgb(40, 0xFF, 0xFF, 0xFF),
                AxislineColor = OxyColor.FromArgb(180, 0x88, 0x88, 0x88),
                TextColor = OxyColor.FromArgb(255, 0xAA, 0xAA, 0xAA),
                TitleColor = OxyColor.FromArgb(255, 0x99, 0x99, 0x99),
                Title = yTitle,
                FontSize = 10,
                TitleFontSize = 10,
                StringFormat = DepthUnit == KalshiDepthUnit.Percent ? "0.#" : "0.##",
            };
            var yAxisRight = new LinearAxis
            {
                Key = "yRight",
                Position = AxisPosition.Right,
                Minimum = 0,
                Maximum = yMax,
                MajorGridlineStyle = LineStyle.None,
                AxislineColor = OxyColor.FromArgb(180, 0x88, 0x88, 0x88),
                TextColor = OxyColor.FromArgb(255, 0xAA, 0xAA, 0xAA),
                FontSize = 10,
                StringFormat = DepthUnit == KalshiDepthUnit.Percent ? "0.#" : "0.##",
            };

            model.Axes.Add(xAxis);
            model.Axes.Add(yAxisLeft);
            model.Axes.Add(yAxisRight);

            // Mid divider so it's obvious where bids end and asks begin.
            if (_midPriceValue > 0 && _midPriceValue >= xMin && _midPriceValue <= xMax)
            {
                model.Annotations.Add(new LineAnnotation
                {
                    Type = LineAnnotationType.Vertical,
                    X = _midPriceValue,
                    Color = OxyColor.FromArgb(160, 0xFF, 0xD5, 0x4F),
                    LineStyle = LineStyle.Dash,
                    StrokeThickness = 1,
                });
            }

            // Overflow indicator: small marker at the clip line and a label so it
            // is visually obvious that part of the curve is hidden.
            if (overflowCount > 0)
            {
                model.Annotations.Add(new LineAnnotation
                {
                    Type = LineAnnotationType.Horizontal,
                    Y = yMax,
                    Color = OxyColor.FromArgb(180, 0xFF, 0xA7, 0x26),
                    LineStyle = LineStyle.Dot,
                    StrokeThickness = 1,
                });
                model.Annotations.Add(new TextAnnotation
                {
                    Text = $"▲ {overflowCount} level(s) clipped at p{(int)(DEPTH_CLIP_PERCENTILE * 100)}",
                    TextPosition = new DataPoint(xMax, yMax),
                    TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Right,
                    TextVerticalAlignment = OxyPlot.VerticalAlignment.Bottom,
                    TextColor = OxyColor.FromArgb(220, 0xFF, 0xA7, 0x26),
                    Stroke = OxyColors.Transparent,
                    FontSize = 10,
                });
            }

            DepthChartModel = model;
        }

        private static bool TryLoadBool(string id, bool dflt)
        {
            try
            {
                var v = SettingsManager.Instance.GetSetting<string>(SettingKey.KALSHI_LADDER_DEPTH_CHART, id);
                if (v is null) return dflt;
                return bool.TryParse(v, out var b) ? b : dflt;
            }
            catch { return dflt; }
        }

        private static T TryLoadEnum<T>(string id, T dflt) where T : struct, Enum
        {
            try
            {
                var v = SettingsManager.Instance.GetSetting<string>(SettingKey.KALSHI_LADDER_DEPTH_CHART, id);
                if (v is null) return dflt;
                return Enum.TryParse<T>(v, ignoreCase: true, out var parsed) ? parsed : dflt;
            }
            catch { return dflt; }
        }

        private static void TrySaveBool(string id, bool v)
        {
            try { SettingsManager.Instance.SetSetting(SettingKey.KALSHI_LADDER_DEPTH_CHART, id, v.ToString()); }
            catch { /* best-effort: in-memory state still works */ }
        }

        private static void TrySaveString(string id, string v)
        {
            try { SettingsManager.Instance.SetSetting(SettingKey.KALSHI_LADDER_DEPTH_CHART, id, v); }
            catch { /* best-effort: in-memory state still works */ }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public void Dispose() => HelperOrderBook.Instance.Unsubscribe(_handler);
    }
}
