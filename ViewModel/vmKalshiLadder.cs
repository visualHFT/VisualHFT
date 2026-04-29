using System;
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
        private readonly Action<OrderBook> _handler;
        private string _symbol = "";

        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; Notify(nameof(Symbol)); Notify(nameof(WindowTitle)); }
        }

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
            _handler = OnBook;
            HelperOrderBook.Instance.Subscribe(_handler);
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

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Asks.Clear();
                foreach (var r in newAskRows) Asks.Add(r);
                Bids.Clear();
                foreach (var r in newBidRows) Bids.Add(r);
                BestBid = bestB;
                BestAsk = bestA;
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public void Dispose() => HelperOrderBook.Instance.Unsubscribe(_handler);
    }
}
