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
        public string Header => $"{Category} ({Events.Count})";
    }

    /// <summary>
    /// Loads the whole Kalshi event catalog and exposes it grouped by category
    /// for a tabbed browser view. Read-only.
    /// </summary>
    public sealed class vmKalshiEventBrowser : INotifyPropertyChanged
    {
        public ObservableCollection<KalshiCategoryGroup> Groups { get; } = new();

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
                    Groups.Add(bucket);
                }
                TotalEvents = events.Count;
                IsLoading = false;
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
