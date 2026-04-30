using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using VisualHFT.Helpers;
using VisualHFT.Model;

namespace VisualHFT.ViewModel
{
    /// <summary>
    /// One bin in the implied probability mass function.
    /// </summary>
    public class PMFBin
    {
        public string Range { get; init; } = "";
        public double ProbPercent { get; init; }              // 0..100
        public double BarWidth { get; init; }                 // px, proportional to prob
        public bool IsPeak { get; init; }
        public bool IsLeftTail { get; init; }
        public bool IsRightTail { get; init; }
        public string ProbText => $"{ProbPercent:F1}%";
    }

    /// <summary>
    /// Implied PMF derived from the live strike ladder of one event.
    /// Subscribes to every order-book update, filters by event-ticker prefix,
    /// rebuilds the PMF when the prices change.
    ///
    ///   p_i = P(X > K_{i-1}) - P(X > K_i)   for i = 1..N (interior bins)
    ///   p_0 = 1 - P(X > K_0)                (left tail)
    ///   p_N = P(X > K_N)                    (right tail)
    /// </summary>
    public sealed class vmKalshiPMF : INotifyPropertyChanged, IDisposable
    {
        private readonly Action<OrderBook> _handler;
        private readonly ConcurrentDictionary<string, (double midProb, double strike)> _strikes = new();

        public string EventTicker { get; }
        public string WindowTitle => $"Kalshi — Implied PMF — {EventTicker}";
        public ObservableCollection<PMFBin> Bins { get; } = new();

        private int _binsCount;
        public int BinsCount { get => _binsCount; set { _binsCount = value; Notify(nameof(BinsCount)); } }

        private double _medianStrike;
        public double MedianStrike { get => _medianStrike; set { _medianStrike = value; Notify(nameof(MedianStrike)); Notify(nameof(MedianText)); } }
        public string MedianText => MedianStrike > 0 ? $"~{MedianStrike:F2}" : "—";

        private DateTime _lastUpdate;
        public DateTime LastUpdate { get => _lastUpdate; set { _lastUpdate = value; Notify(nameof(LastUpdate)); } }

        public vmKalshiPMF(string eventTicker)
        {
            EventTicker = eventTicker;
            _handler = OnBook;
            HelperOrderBook.Instance.Subscribe(_handler);
        }

        private void OnBook(OrderBook ob)
        {
            if (ob is null) return;
            var symbol = ob.Symbol ?? "";
            if (!symbol.StartsWith(EventTicker, StringComparison.Ordinal)) return;

            var bids = ob.Bids;
            var asks = ob.Asks;
            double topBid = bids.Count() > 0 ? (bids[0].Price ?? 0) : 0;
            double topAsk = asks.Count() > 0 ? (asks[0].Price ?? 0) : 0;
            if (topBid <= 0 || topAsk <= 0) return;

            double mid = (topBid + topAsk) / 2.0;             // cents
            double midProb = mid / 100.0;                       // 0..1

            // Parse strike from the ticker tail (T103.99 / B82.5 / T63)
            int lastDash = symbol.LastIndexOf('-');
            string token = lastDash > 0 ? symbol.Substring(lastDash + 1) : "";
            if (string.IsNullOrEmpty(token)) return;
            double strike = ParseStrikeNumber(token);
            if (strike <= 0) return;

            _strikes[symbol] = (midProb, strike);
            Application.Current?.Dispatcher.BeginInvoke(Recompute);
        }

        private void Recompute()
        {
            // Sort strikes ascending. Filter out malformed.
            var sorted = _strikes.Values.Where(v => v.midProb > 0 && v.midProb < 1)
                                        .OrderBy(v => v.strike).ToList();
            if (sorted.Count < 2)
            {
                Bins.Clear();
                BinsCount = 0;
                return;
            }

            // Build bins
            var newBins = new List<PMFBin>();

            // Left tail: P(X < K_0) = 1 - P(X > K_0)
            double leftTail = 1.0 - sorted[0].midProb;
            newBins.Add(new PMFBin
            {
                Range = $"< {sorted[0].strike:F2}",
                ProbPercent = leftTail * 100,
                IsLeftTail = true
            });

            // Interior bins: P(K_{i-1} < X < K_i) = P(>K_{i-1}) - P(>K_i)
            for (int i = 1; i < sorted.Count; i++)
            {
                double p = sorted[i - 1].midProb - sorted[i].midProb;
                if (p < 0) p = 0; // monotonicity violation — clamp; the arb scanner already flagged it
                newBins.Add(new PMFBin
                {
                    Range = $"{sorted[i - 1].strike:F2}–{sorted[i].strike:F2}",
                    ProbPercent = p * 100
                });
            }

            // Right tail: P(X > K_N)
            double rightTail = sorted[^1].midProb;
            newBins.Add(new PMFBin
            {
                Range = $"> {sorted[^1].strike:F2}",
                ProbPercent = rightTail * 100,
                IsRightTail = true
            });

            // Compute bar widths + peak flag
            const double MAX_BAR_PX = 320.0;
            double maxP = newBins.Max(b => b.ProbPercent);
            for (int i = 0; i < newBins.Count; i++)
            {
                var b = newBins[i];
                newBins[i] = new PMFBin
                {
                    Range = b.Range,
                    ProbPercent = b.ProbPercent,
                    BarWidth = maxP > 0 ? (b.ProbPercent / maxP) * MAX_BAR_PX : 0,
                    IsPeak = b.ProbPercent == maxP && !b.IsLeftTail && !b.IsRightTail,
                    IsLeftTail = b.IsLeftTail,
                    IsRightTail = b.IsRightTail
                };
            }

            // Median strike: walk cumulative until hitting 50%
            double cum = 0;
            double median = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                double binProb = (i == 0) ? leftTail
                                          : (sorted[i - 1].midProb - sorted[i].midProb);
                if (cum + binProb >= 0.5)
                {
                    // Interpolate within this bin
                    double need = 0.5 - cum;
                    double frac = binProb > 0 ? need / binProb : 0;
                    double lo = (i == 0) ? sorted[0].strike - (sorted[1].strike - sorted[0].strike) : sorted[i - 1].strike;
                    double hi = (i == 0) ? sorted[0].strike : sorted[i].strike;
                    median = lo + frac * (hi - lo);
                    break;
                }
                cum += binProb;
            }

            Bins.Clear();
            foreach (var b in newBins) Bins.Add(b);
            BinsCount = newBins.Count;
            MedianStrike = median;
            LastUpdate = DateTime.Now;
        }

        private static double ParseStrikeNumber(string token)
        {
            int i = 0;
            while (i < token.Length && !char.IsDigit(token[i]) && token[i] != '-' && token[i] != '.') i++;
            return double.TryParse(token.Substring(i), System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        public void Dispose() => HelperOrderBook.Instance.Unsubscribe(_handler);
    }
}
