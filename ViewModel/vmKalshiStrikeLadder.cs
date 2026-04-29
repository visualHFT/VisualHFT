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
            if (!IsKalshiTicker(symbol)) return;

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
            });
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
                Category = CategorizeFromTicker(ticker)
            };
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
