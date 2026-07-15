using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using VisualHFT.Helpers;
using VisualHFT.Model;

namespace VisualHFT.ViewModel
{
    /// <summary>
    /// One row in the Kalshi strike-ladder grid — top-of-book + derived
    /// implied-probability fields for a single binary contract.
    /// </summary>
    public class KalshiStrikeRow : INotifyPropertyChanged
    {
        public string Ticker { get; set; } = "";
        public string EventTicker { get; set; } = "";
        public string Strike { get; set; } = "";
        public string Category { get; set; } = "";
        /// <summary>Strike value parsed numerically from the ticker (e.g. T103.99 → 103.99).</summary>
        public double StrikeNumeric { get; set; }

        private double _yesBid;
        public double YesBid
        {
            get => _yesBid;
            set { _yesBid = value; Notify(nameof(YesBid)); Notify(nameof(Spread)); Notify(nameof(MidProb)); }
        }

        private double _yesAsk;
        public double YesAsk
        {
            get => _yesAsk;
            set { _yesAsk = value; Notify(nameof(YesAsk)); Notify(nameof(Spread)); Notify(nameof(MidProb)); }
        }

        public double Spread => Math.Max(0, YesAsk - YesBid);
        public double MidProb => (YesBid > 0 && YesAsk > 0) ? Math.Round((YesBid + YesAsk) / 2.0, 1) : 0;

        // --- Monotonicity / arbitrage flags, set by the scanner ---
        private double _arbEdgeCents;
        /// <summary>Largest executable arbitrage edge (cents) involving this strike, across all pairs.
        /// Positive = free money exists. Computed by the scanner.</summary>
        public double ArbEdgeCents
        {
            get => _arbEdgeCents;
            set { _arbEdgeCents = value; Notify(nameof(ArbEdgeCents)); Notify(nameof(ArbEdgeText)); Notify(nameof(IsArbExecutable)); Notify(nameof(IsMonotonicityViolation)); }
        }

        private double _theoMonotonicityGapCents;
        /// <summary>Mid-price monotonicity gap in cents — positive means *theoretical* violation
        /// (mid of higher strike exceeds mid of lower strike). Sub-spread, not necessarily executable.</summary>
        public double TheoMonotonicityGapCents
        {
            get => _theoMonotonicityGapCents;
            set { _theoMonotonicityGapCents = value; Notify(nameof(TheoMonotonicityGapCents)); Notify(nameof(IsMonotonicityViolation)); }
        }

        public bool IsArbExecutable => ArbEdgeCents > 0.0;
        public bool IsMonotonicityViolation => IsArbExecutable || TheoMonotonicityGapCents > 0.0;
        public string ArbEdgeText => IsArbExecutable ? $"+{ArbEdgeCents:F1}¢"
                                   : TheoMonotonicityGapCents > 0 ? $"~{TheoMonotonicityGapCents:F1}¢"
                                   : "";

        private DateTime _lastUpdate;
        public DateTime LastUpdate
        {
            get => _lastUpdate;
            set { _lastUpdate = value; Notify(nameof(LastUpdate)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// View-model for the Kalshi strike-ladder window.
    /// Subscribes to every order-book update from any provider, filters to
    /// Kalshi tickers (prefix 'KX'), and groups by event ticker into rows.
    /// </summary>
    public sealed class vmKalshiStrikeLadder : INotifyPropertyChanged, IDisposable
    {
        public ObservableCollection<KalshiStrikeRow> Strikes { get; } = new();
        private readonly ConcurrentDictionary<string, KalshiStrikeRow> _byTicker = new();
        private readonly Action<OrderBook> _handler;

        public vmKalshiStrikeLadder()
        {
            _handler = OnBook;
            HelperOrderBook.Instance.Subscribe(_handler);
        }

        private void OnBook(OrderBook ob)
        {
            if (ob is null) return;
            var symbol = ob.Symbol;
            // Filter by Kalshi provider id (100) instead of KX prefix — real Kalshi
            // tickers like CONTROLH-2026, GOVPARTY*, EUEXIT, etc. don't start with KX.
            if (ob.ProviderID != 100 && !IsKalshiTicker(symbol)) return;

            // Snapshot top-of-book outside the UI thread
            var bids = ob.Bids;
            var asks = ob.Asks;
            double topBid = bids.Count() > 0 ? (bids[0].Price ?? 0) : 0;
            double topAsk = asks.Count() > 0 ? (asks[0].Price ?? 0) : 0;

            var row = _byTicker.GetOrAdd(symbol, CreateRow);

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!Strikes.Contains(row))
                {
                    InsertSorted(row);
                }
                row.YesBid = topBid;
                row.YesAsk = topAsk;
                row.LastUpdate = DateTime.Now;

                // Monotonicity / no-arb scan across this row's event group.
                ScanArbForEvent(row.EventTicker);
            });
        }

        /// <summary>
        /// Scan all strikes of one event for monotonicity violations.
        /// Two checks per adjacent pair (K_low &lt; K_high):
        ///   1. Executable arb: yes_ask(K_low) &lt; yes_bid(K_high). Free money = bid_high - ask_low cents.
        ///   2. Theoretical violation: mid(K_low) &lt; mid(K_high). Sub-spread mispricing.
        /// Each row's flags = max over all pairs touching that row.
        /// </summary>
        private void ScanArbForEvent(string eventTicker)
        {
            var group = Strikes.Where(r => r.EventTicker == eventTicker).ToList();
            if (group.Count < 2) return;

            // Reset
            foreach (var r in group) { r.ArbEdgeCents = 0; r.TheoMonotonicityGapCents = 0; }

            // Sort ascending by strike
            group.Sort((a, b) => a.StrikeNumeric.CompareTo(b.StrikeNumeric));

            for (int i = 0; i < group.Count - 1; i++)
            {
                var lo = group[i];
                var hi = group[i + 1];
                if (lo.YesBid <= 0 || hi.YesBid <= 0) continue;

                // Executable: buy K_low at ask, sell K_high at bid
                double execEdge = hi.YesBid - lo.YesAsk;
                if (execEdge > 0)
                {
                    if (execEdge > lo.ArbEdgeCents) lo.ArbEdgeCents = execEdge;
                    if (execEdge > hi.ArbEdgeCents) hi.ArbEdgeCents = execEdge;
                }

                // Theoretical: mid of K_high should be < mid of K_low
                double theoGap = hi.MidProb - lo.MidProb;
                if (theoGap > 0)
                {
                    if (theoGap > lo.TheoMonotonicityGapCents) lo.TheoMonotonicityGapCents = theoGap;
                    if (theoGap > hi.TheoMonotonicityGapCents) hi.TheoMonotonicityGapCents = theoGap;
                }
            }
        }

        private void InsertSorted(KalshiStrikeRow row)
        {
            // Insert keeping rows grouped by EventTicker, then sorted by strike text
            var idx = Strikes.ToList()
                .FindIndex(r => string.Compare(r.EventTicker + r.Strike, row.EventTicker + row.Strike, StringComparison.Ordinal) > 0);
            if (idx < 0) Strikes.Add(row);
            else Strikes.Insert(idx, row);
        }

        private static KalshiStrikeRow CreateRow(string ticker)
        {
            // KXHIGHTATL-26APR29-B82.5 → event="KXHIGHTATL-26APR29", strike="B82.5"
            int lastDash = ticker.LastIndexOf('-');
            string evt = lastDash > 0 ? ticker.Substring(0, lastDash) : ticker;
            string strk = lastDash > 0 ? ticker.Substring(lastDash + 1) : "";
            return new KalshiStrikeRow
            {
                Ticker = ticker,
                EventTicker = evt,
                Strike = strk,
                StrikeNumeric = ParseStrikeNumber(strk),
                Category = CategorizeFromTicker(ticker)
            };
        }

        /// <summary>Parse the numeric strike value out of a strike token like 'T103.99', 'B82.5', 'T63'.</summary>
        private static double ParseStrikeNumber(string strikeToken)
        {
            if (string.IsNullOrEmpty(strikeToken)) return 0;
            // Strip any leading non-digit prefix like 'T', 'B', 'L' etc.
            int i = 0;
            while (i < strikeToken.Length && !char.IsDigit(strikeToken[i]) && strikeToken[i] != '-' && strikeToken[i] != '.') i++;
            return double.TryParse(strikeToken.Substring(i), System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        // Map ticker prefix → broad market category. Easy to extend.
        private static string CategorizeFromTicker(string ticker)
        {
            string t = ticker.ToUpperInvariant();
            if (t.StartsWith("KXHIGH") || t.StartsWith("KXLOW") ||
                t.StartsWith("KXSNOW") || t.StartsWith("KXRAIN") ||
                t.StartsWith("KXTEMP")) return "Weather";
            if (t.StartsWith("KXNBA") || t.StartsWith("KXNFL") || t.StartsWith("KXMLB") ||
                t.StartsWith("KXNHL") || t.StartsWith("KXMLS") || t.StartsWith("KXUFC") ||
                t.StartsWith("KXTENNIS")) return "Sports";
            if (t.StartsWith("KXBTC") || t.StartsWith("KXETH") || t.StartsWith("KXSOL") ||
                t.StartsWith("KXCRYPTO")) return "Crypto";
            if (t.StartsWith("KXWTI") || t.StartsWith("KXBRENT") ||
                t.StartsWith("KXNGAS") || t.StartsWith("KXGOLD")) return "Commodities";
            if (t.StartsWith("KXFED") || t.StartsWith("KXRATE") ||
                t.StartsWith("KXCPI") || t.StartsWith("KXJOBS") || t.StartsWith("KXGDP")) return "Rates/Macro";
            if (t.StartsWith("KXSENATE") || t.StartsWith("KXPRES") ||
                t.StartsWith("KXHOUSE") || t.StartsWith("KXELECT")) return "Politics";
            if (t.StartsWith("KXSP500") || t.StartsWith("KXNASDAQ") ||
                t.StartsWith("KXDOW")) return "Equities";
            return "Other";
        }

        private static bool IsKalshiTicker(string s) =>
            !string.IsNullOrEmpty(s) && s.StartsWith("KX", StringComparison.OrdinalIgnoreCase);

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Dispose() => HelperOrderBook.Instance.Unsubscribe(_handler);
    }
}
