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
    /// <summary>One row in the Watch List window — top of book for a watched ticker.</summary>
    public sealed class WatchListRow : INotifyPropertyChanged
    {
        public string Ticker { get; init; } = "";
        public string EventTicker { get; init; } = "";

        private double _yesBid;
        public double YesBid { get => _yesBid; set { _yesBid = value; Notify(nameof(YesBid)); Notify(nameof(YesBidText)); Notify(nameof(SpreadText)); Notify(nameof(MidText)); } }

        private double _yesAsk;
        public double YesAsk { get => _yesAsk; set { _yesAsk = value; Notify(nameof(YesAsk)); Notify(nameof(YesAskText)); Notify(nameof(SpreadText)); Notify(nameof(MidText)); } }

        public string YesBidText => YesBid > 0 ? $"{YesBid:F0}¢" : "—";
        public string YesAskText => YesAsk > 0 ? $"{YesAsk:F0}¢" : "—";
        public string MidText    => (YesBid > 0 && YesAsk > 0) ? $"{(YesBid + YesAsk) / 2:F1}¢" : "—";
        public string SpreadText => (YesBid > 0 && YesAsk > 0) ? $"{YesAsk - YesBid:F0}¢" : "—";

        private DateTime _last;
        public DateTime LastUpdate { get => _last; set { _last = value; Notify(nameof(LastUpdate)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// View-model for the Watch List window. Backs the KalshiBrowserPoller's
    /// dynamic ticker set with a UI for inspect / add / remove. Subscribes to
    /// HelperOrderBook so prices stay live. Supports text search across
    /// ticker + event ticker.
    /// </summary>
    public sealed class vmKalshiWatchList : INotifyPropertyChanged, IDisposable
    {
        public ObservableCollection<WatchListRow> Rows { get; } = new();
        public System.ComponentModel.ICollectionView FilteredRows { get; }
        private readonly ConcurrentDictionary<string, WatchListRow> _byTicker = new();
        private readonly Action<OrderBook> _handler;

        private string _search = "";
        public string Search
        {
            get => _search;
            set
            {
                _search = value ?? "";
                Notify(nameof(Search));
                FilteredRows.Refresh();
            }
        }

        public vmKalshiWatchList()
        {
            _handler = OnBook;
            HelperOrderBook.Instance.Subscribe(_handler);

            FilteredRows = System.Windows.Data.CollectionViewSource.GetDefaultView(Rows);
            FilteredRows.Filter = obj =>
            {
                if (string.IsNullOrWhiteSpace(_search)) return true;
                if (obj is not WatchListRow r) return true;
                var s = _search.Trim();
                return r.Ticker.Contains(s, StringComparison.OrdinalIgnoreCase)
                    || r.EventTicker.Contains(s, StringComparison.OrdinalIgnoreCase);
            };

            // Seed with anything currently watched
            foreach (var t in KalshiBrowserPoller.Instance.WatchedTickers)
                EnsureRow(t);
        }

        private void OnBook(OrderBook ob)
        {
            if (ob is null || ob.ProviderID != KalshiBrowserPoller.KalshiProviderId) return;
            // Only show tickers we actually opted into watching
            if (!KalshiBrowserPoller.Instance.WatchedTickers.Contains(ob.Symbol)) return;

            var bids = ob.Bids;
            var asks = ob.Asks;
            double topBid = bids.Count() > 0 ? (bids[0].Price ?? 0) : 0;
            double topAsk = asks.Count() > 0 ? (asks[0].Price ?? 0) : 0;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var row = EnsureRow(ob.Symbol);
                row.YesBid = topBid;
                row.YesAsk = topAsk;
                row.LastUpdate = DateTime.Now;
            });
        }

        private WatchListRow EnsureRow(string ticker)
        {
            return _byTicker.GetOrAdd(ticker, t =>
            {
                var row = new WatchListRow
                {
                    Ticker = t,
                    EventTicker = t.Contains('-') ? t.Substring(0, t.LastIndexOf('-')) : t
                };
                Application.Current?.Dispatcher.Invoke(() => Rows.Add(row));
                return row;
            });
        }

        /// <summary>Add a single ticker and start polling it.</summary>
        public void AddManual(string ticker)
        {
            ticker = (ticker ?? "").Trim();
            if (ticker.Length < 3) return;
            KalshiBrowserPoller.Instance.Watch(new[] { ticker });
            EnsureRow(ticker);
        }

        public void Remove(WatchListRow row)
        {
            if (row is null) return;
            KalshiBrowserPoller.Instance.Unwatch(row.Ticker);
            _byTicker.TryRemove(row.Ticker, out _);
            Application.Current?.Dispatcher.Invoke(() => Rows.Remove(row));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        public void Dispose() => HelperOrderBook.Instance.Unsubscribe(_handler);
    }
}
