using System.Globalization;
using Avalonia.Media;
using VisualHFT.Desktop.ViewModels;
using VisualHFT.Enums;

namespace VisualHFT.Desktop.Screens;

public sealed class VenueMidTracker
{
    public const int MaxPoints = 1300;

    private readonly object _gate = new();
    private readonly Dictionary<int, VenueSeries> _venues = new();
    private readonly List<string> _symbols = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private string? _symbol;
    private AggregationLevel _level = AggregationLevel.Ms100;

    public IReadOnlyList<string> Symbols
    {
        get
        {
            lock (_gate)
                return _symbols.ToArray();
        }
    }

    public void SetSymbol(string? symbol)
    {
        lock (_gate)
        {
            if (_symbol == symbol)
                return;
            _symbol = symbol;
            _venues.Clear();
        }
    }

    public void SetAggregation(AggregationLevel level)
    {
        lock (_gate)
        {
            if (_level == level)
                return;
            _level = level;
            _venues.Clear();
        }
    }

    public void Add(int providerId, string providerName, string symbol, double mid, DateTime time)
    {
        if (string.IsNullOrEmpty(symbol))
            return;

        lock (_gate)
        {
            if (_seen.Add(symbol))
                _symbols.Add(symbol);

            if (string.IsNullOrEmpty(_symbol) || _symbol != symbol)
                return;

            var name = string.IsNullOrWhiteSpace(providerName)
                ? providerId.ToString(CultureInfo.InvariantCulture)
                : providerName;

            if (!_venues.TryGetValue(providerId, out var venue))
            {
                venue = new VenueSeries
                {
                    Name = name,
                    Color = ColorFor(providerId)
                };
                _venues.Add(providerId, venue);
            }
            else if (!string.IsNullOrWhiteSpace(providerName))
            {
                venue.Name = name;
            }

            if (venue.Values.Count > 0 && AggregatedAway(venue.LastTime, time, _level))
                return;

            venue.Values.Add(mid);
            venue.LastTime = time;
            venue.LastValue = mid;
            Trim(venue.Values);

            foreach (var other in _venues.Values)
            {
                if (ReferenceEquals(other, venue) || other.Values.Count == 0 || other.LastTime == time)
                    continue;
                other.Values.Add(other.LastValue);
                other.LastTime = time;
                Trim(other.Values);
            }
        }
    }

    public IReadOnlyList<ChartSeries> Series()
    {
        lock (_gate)
        {
            return _venues.Values.Select(venue => new ChartSeries
            {
                Name = venue.Name,
                Values = venue.Values.ToArray(),
                Color = venue.Color
            }).ToArray();
        }
    }

    public static void Validate()
    {
        WrongSymbolIgnored();
        VenuesStayAligned();
        Ms100DropsIntraBucket();
        ClearOnSymbolChange();
        MaxPointsTrim();
    }

    private static bool AggregatedAway(DateTime last, DateTime time, AggregationLevel level)
    {
        if (level == AggregationLevel.None)
            return false;
        var span = level.ToTimeSpan();
        return span > TimeSpan.Zero && time - last < span;
    }

    private static void Trim(List<double> values)
    {
        while (values.Count > MaxPoints)
            values.RemoveAt(0);
    }

    private static Color ColorFor(int providerId)
    {
        var colors = Palette.ToArray();
        var rng = new Random(providerId);
        for (var i = colors.Length - 1; i >= 0; i--)
        {
            var swap = rng.Next(i + 1);
            (colors[i], colors[swap]) = (colors[swap], colors[i]);
        }
        return colors[0];
    }

    private static void WrongSymbolIgnored()
    {
        var tracker = new VenueMidTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        tracker.SetSymbol("BTC-USDT");
        tracker.Add(1, "Binance", "ETH-USDT", 100, t0);
        if (tracker.Series().Count != 0)
            throw new InvalidOperationException("Ticks for a different symbol must be ignored.");
        if (tracker.Symbols.Count != 1 || tracker.Symbols[0] != "ETH-USDT")
            throw new InvalidOperationException("Ignored symbols must still be recorded as seen.");
    }

    private static void VenuesStayAligned()
    {
        var tracker = new VenueMidTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        tracker.SetSymbol("BTC-USDT");
        tracker.SetAggregation(AggregationLevel.Ms100);
        tracker.Add(1, "A", "BTC-USDT", 100, t0);
        tracker.Add(2, "B", "BTC-USDT", 200, t0);
        tracker.Add(1, "A", "BTC-USDT", 101, t0.AddSeconds(1));
        tracker.Add(2, "B", "BTC-USDT", 201, t0.AddSeconds(2));
        var series = tracker.Series();
        if (series.Count != 2)
            throw new InvalidOperationException("Both venues should have a series.");
        if (series[0].Values.Count != series[1].Values.Count)
            throw new InvalidOperationException("Venues must stay the same length after an A-tick then a B-tick.");
        if (series[0].Values.Count != 3)
            throw new InvalidOperationException("Aligned venues should each hold three points.");
        if (ColorFor(1) != series[0].Color || ColorFor(2) != series[1].Color)
            throw new InvalidOperationException("Provider colors must be deterministic.");
    }

    private static void Ms100DropsIntraBucket()
    {
        var tracker = new VenueMidTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        tracker.SetSymbol("BTC-USDT");
        tracker.SetAggregation(AggregationLevel.Ms100);
        tracker.Add(1, "A", "BTC-USDT", 100, t0);
        tracker.Add(2, "B", "BTC-USDT", 200, t0);
        tracker.Add(1, "A", "BTC-USDT", 150, t0.AddMilliseconds(50));
        var series = tracker.Series();
        if (series.Count != 2 || series[0].Values.Count != 1 || series[1].Values.Count != 1)
            throw new InvalidOperationException("An Ms100 intra-bucket sample must be dropped without extending other series.");
        if (series[0].Values[0] != 100)
            throw new InvalidOperationException("The first sample in an Ms100 bucket must be kept.");
    }

    private static void ClearOnSymbolChange()
    {
        var tracker = new VenueMidTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        tracker.SetSymbol("BTC-USDT");
        tracker.Add(1, "A", "BTC-USDT", 100, t0);
        tracker.SetSymbol("ETH-USDT");
        if (tracker.Series().Count != 0)
            throw new InvalidOperationException("Changing symbol must clear every series.");
        tracker.SetSymbol("BTC-USDT");
        tracker.Add(1, "A", "BTC-USDT", 100, t0);
        tracker.SetAggregation(AggregationLevel.S1);
        if (tracker.Series().Count != 0)
            throw new InvalidOperationException("Changing aggregation must clear every series.");
    }

    private static void MaxPointsTrim()
    {
        var tracker = new VenueMidTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        tracker.SetSymbol("BTC-USDT");
        tracker.SetAggregation(AggregationLevel.None);
        for (var i = 0; i <= MaxPoints; i++)
            tracker.Add(1, "A", "BTC-USDT", 100 + i, t0.AddMilliseconds(i));
        var series = tracker.Series();
        if (series.Count != 1 || series[0].Values.Count != MaxPoints)
            throw new InvalidOperationException("A venue series must trim to 1300 points.");
        if (series[0].Values[0] != 101 || series[0].Values[^1] != 100 + MaxPoints)
            throw new InvalidOperationException("Trim must drop the oldest mid and keep the newest.");
    }

    private static readonly Color[] Palette =
    {
        Color.Parse("#61AFFE"),
        Color.Parse("#3DDC84"),
        Color.Parse("#FF6B6B"),
        Color.Parse("#F5C16C"),
        Color.Parse("#C084FC"),
        Color.Parse("#22D3EE"),
        Color.Parse("#FB7185"),
        Color.Parse("#A3E635"),
        Color.Parse("#F472B6"),
        Color.Parse("#38BDF8"),
        Color.Parse("#FBBF24"),
        Color.Parse("#4ADE80"),
        Color.Parse("#E879F9"),
        Color.Parse("#F97316"),
        Color.Parse("#2DD4BF"),
        Color.Parse("#818CF8"),
        Color.Parse("#F43F5E"),
        Color.Parse("#14B8A6"),
        Color.Parse("#EAB308"),
        Color.Parse("#A78BFA")
    };

    private sealed class VenueSeries
    {
        public required string Name { get; set; }
        public required Color Color { get; init; }
        public List<double> Values { get; } = new();
        public DateTime LastTime { get; set; }
        public double LastValue { get; set; }
    }
}
