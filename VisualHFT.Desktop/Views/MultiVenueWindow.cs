using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VisualHFT;
using VisualHFT.Desktop.Screens;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;

namespace VisualHFT.Desktop.Views;

public sealed class MultiVenueWindow : Window
{
    private static readonly AggregationLevel[] AllLevels = Enum.GetValues<AggregationLevel>();

    private readonly VenueMidTracker _tracker = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly ComboBox _symbols;
    private readonly ComboBox _aggregation;
    private readonly SeriesChart _chart;
    private readonly DispatcherTimer _timer;
    private readonly Action<OrderBook> _onBook;
    private readonly string _native;
    private readonly string _normalized;
    private readonly string _venue;
    private bool _refreshing;

    public MultiVenueWindow(IEnumerable<string> knownSymbols, string native, string normalized, string venue)
    {
        Title = "Venue prices";
        Width = 900;
        Height = 500;
        Background = new SolidColorBrush(Color.Parse("#0E1116"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _native = native;
        _normalized = normalized;
        _venue = venue;
        if (!string.IsNullOrWhiteSpace(native))
            _known.Add(native);
        if (!string.IsNullOrWhiteSpace(normalized))
            _known.Add(normalized);
        foreach (var symbol in knownSymbols)
        {
            if (!string.IsNullOrWhiteSpace(symbol)
                && (string.IsNullOrEmpty(native) || TickerSearch.SameInstrument(symbol, native, normalized)))
                _known.Add(symbol);
        }

        _tracker.SetAggregation(AggregationLevel.Ms100);
        _onBook = OnBook;

        _symbols = new ComboBox
        {
            Width = 150,
            ItemsSource = MergeSymbols()
        };
        if (_symbols.ItemsSource is IList<string> { Count: > 0 } start)
        {
            _symbols.SelectedItem = start[0];
            _tracker.SetSymbol(start[0]);
        }
        _symbols.SelectionChanged += (_, _) =>
        {
            if (_refreshing)
                return;
            _tracker.SetSymbol(_symbols.SelectedItem as string);
        };

        _aggregation = new ComboBox
        {
            Width = 200,
            ItemsSource = AllLevels.Select(AggregationChoices.Label).ToArray(),
            SelectedItem = AggregationChoices.Label(AggregationLevel.Ms100)
        };
        _aggregation.SelectionChanged += (_, _) =>
        {
            if (_aggregation.SelectedItem is string label && TryLevel(label, out var level))
                _tracker.SetAggregation(level);
        };

        _chart = new SeriesChart { Margin = new Avalonia.Thickness(12) };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Avalonia.Thickness(12, 10, 12, 0),
            [DockPanel.DockProperty] = Dock.Top,
            Children =
            {
                Label("Symbol"),
                _symbols,
                Label("Aggregation"),
                _aggregation
            }
        };

        Content = new DockPanel
        {
            Children = { toolbar, _chart }
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => Paint();
        Opened += (_, _) =>
        {
            HelperOrderBook.Instance.Subscribe(_onBook);
            Paint();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            HelperOrderBook.Instance.Unsubscribe(_onBook);
            _timer.Stop();
        };
    }

    private void OnBook(OrderBook book)
    {
        if (book == null)
            return;
        if (!string.IsNullOrEmpty(_native)
            && !TickerSearch.SameInstrument(book.Symbol, _native, _normalized))
            return;
        if (!string.IsNullOrEmpty(_venue)
            && !string.Equals(book.ProviderName, _venue, StringComparison.OrdinalIgnoreCase))
            return;
        var providerId = book.ProviderID;
        var providerName = book.ProviderName ?? "";
        var symbol = book.Symbol ?? "";
        var mid = book.MidPrice;
        _tracker.Add(providerId, providerName, symbol, mid, HelperTimeProvider.Now);
    }

    private void Paint()
    {
        var selected = _symbols.SelectedItem as string;
        var items = MergeSymbols();
        if (!Same(items, _symbols.ItemsSource as IEnumerable<string>))
        {
            _refreshing = true;
            try
            {
                _symbols.ItemsSource = items;
                if (selected != null && items.Contains(selected, StringComparer.Ordinal))
                    _symbols.SelectedItem = selected;
                else if (items.Count > 0 && _symbols.SelectedItem == null)
                {
                    _symbols.SelectedItem = items[0];
                    _tracker.SetSymbol(items[0]);
                }
            }
            finally
            {
                _refreshing = false;
            }
        }

        _chart.Series = _tracker.Series();
    }

    private List<string> MergeSymbols()
    {
        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in _known.Concat(_tracker.Symbols))
        {
            if (string.IsNullOrWhiteSpace(symbol) || !seen.Add(symbol))
                continue;
            items.Add(symbol);
        }
        return items;
    }

    private static bool Same(IReadOnlyList<string> left, IEnumerable<string>? right)
    {
        if (right == null)
            return false;
        var index = 0;
        foreach (var item in right)
        {
            if (index >= left.Count || left[index] != item)
                return false;
            index++;
        }
        return index == left.Count;
    }

    private static bool TryLevel(string? label, out AggregationLevel level)
    {
        foreach (var candidate in AllLevels)
        {
            if (AggregationChoices.Label(candidate) == label)
            {
                level = candidate;
                return true;
            }
        }

        level = AggregationLevel.Ms100;
        return false;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse("#9AA4B2"))
    };
}
