using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VisualHFT.Helpers;

namespace VisualHFT.ViewModel
{
    /// <summary>One TabItem in the events browser — a category and its events.</summary>
    public sealed class KalshiCategoryGroup
    {
        public string Category { get; init; } = "";
        public ObservableCollection<KalshiEventInfo> Events { get; } = new();
        public System.ComponentModel.ICollectionView? FilteredEvents { get; set; }
        public string Header => $"{Category} ({Events.Count})";
    }

    /// <summary>
    /// Loads the whole Kalshi event catalog and exposes it grouped by category
    /// for a tabbed browser view. Read-only.
    /// </summary>
    public sealed class vmKalshiEventBrowser : INotifyPropertyChanged
    {
        public ObservableCollection<KalshiCategoryGroup> Groups { get; } = new();

        private string _search = "";
        public string Search
        {
            get => _search;
            set
            {
                _search = value ?? "";
                Notify(nameof(Search));
                foreach (var g in Groups) g.FilteredEvents?.Refresh();
            }
        }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set { _isLoading = value; Notify(nameof(IsLoading)); Notify(nameof(StatusText)); } }

        private int _totalEvents;
        public int TotalEvents { get => _totalEvents; set { _totalEvents = value; Notify(nameof(TotalEvents)); Notify(nameof(StatusText)); } }

        public string StatusText => IsLoading
            ? "Loading event catalog from Kalshi…"
            : $"{TotalEvents} open events across {Groups.Count} categories";

        public async Task RefreshAsync()
        {
            IsLoading = true;
            Groups.Clear();
            TotalEvents = 0;

            List<KalshiEventInfo> events;
            try
            {
                using var cat = KalshiEventCatalog.ForProd();
                events = await cat.FetchAllOpenAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IsLoading = false;
                MessageBox.Show($"Failed to load event catalog:\n\n{ex.Message}",
                    "Kalshi Events Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Group by API category, sort categories by count desc
            var grouped = events.GroupBy(e => string.IsNullOrEmpty(e.Category) ? "Other" : e.Category)
                                .OrderByDescending(g => g.Count())
                                .ToList();

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var g in grouped)
                {
                    var bucket = new KalshiCategoryGroup { Category = g.Key };
                    foreach (var e in g.OrderBy(x => x.EventTicker))
                        bucket.Events.Add(e);
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(bucket.Events);
                    view.Filter = obj =>
                    {
                        if (string.IsNullOrWhiteSpace(_search)) return true;
                        if (obj is not KalshiEventInfo evt) return true;
                        var s = _search.Trim();
                        return evt.EventTicker.Contains(s, StringComparison.OrdinalIgnoreCase)
                            || evt.SeriesTicker.Contains(s, StringComparison.OrdinalIgnoreCase)
                            || evt.Title.Contains(s, StringComparison.OrdinalIgnoreCase)
                            || evt.SubTitle.Contains(s, StringComparison.OrdinalIgnoreCase);
                    };
                    bucket.FilteredEvents = view;
                    Groups.Add(bucket);
                }
                TotalEvents = events.Count;
                IsLoading = false;
            });

            // Kick off the markets fetch in the background — once done, attach OI
            // per event and resort each category by liquidity desc.
            _ = Task.Run(() => LoadLiquidityAsync(events));
        }

        private async Task LoadLiquidityAsync(List<KalshiEventInfo> events)
        {
            try
            {
                using var cat = KalshiEventCatalog.ForProd();
                var liq = await cat.FetchEventLiquidityAsync().ConfigureAwait(false);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var e in events)
                    {
                        if (liq.TryGetValue(e.EventTicker, out var v))
                        {
                            e.OpenInterest = v.oi;
                            e.Volume       = v.vol;
                            e.MarketCount  = v.markets;
                        }
                    }
                    // Resort each category by OI desc, ticker asc as tiebreaker
                    foreach (var bucket in Groups)
                    {
                        var sorted = bucket.Events
                            .OrderByDescending(x => x.OpenInterest)
                            .ThenBy(x => x.EventTicker)
                            .ToList();
                        bucket.Events.Clear();
                        foreach (var e in sorted) bucket.Events.Add(e);
                    }
                });
            }
            catch (Exception ex)
            {
                // Non-fatal — events stay listed, just unsorted by liquidity
                System.Diagnostics.Debug.WriteLine($"liquidity load failed: {ex.Message}");
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
