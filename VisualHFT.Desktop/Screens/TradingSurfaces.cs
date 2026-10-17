using System.Globalization;
using VisualHFT.Commons.Helpers;
using VisualHFT.Enums;

namespace VisualHFT.Desktop.Screens;

public static class AggregationChoices
{
    public static IReadOnlyList<AggregationLevel> BookLevels { get; } =
        Enum.GetValues<AggregationLevel>().Where(level => level >= AggregationLevel.Ms10).ToArray();

    public static IReadOnlyList<string> Labels { get; } =
        BookLevels.Select(level => HelperCommon.GetEnumDescription(level)).ToArray();

    public static string Label(AggregationLevel level) => HelperCommon.GetEnumDescription(level);

    public static bool TryGet(string? label, out AggregationLevel level)
    {
        foreach (var candidate in BookLevels)
        {
            if (Label(candidate) == label)
            {
                level = candidate;
                return true;
            }
        }

        level = AggregationLevel.Ms100;
        return false;
    }
}

public readonly record struct RestingLevel(double Price, double Size, bool IsBid);

public sealed class PriceSample
{
    public required DateTime Time { get; init; }
    public required double Bid { get; init; }
    public required double Ask { get; init; }
    public required double Mid { get; init; }
    public required IReadOnlyList<RestingLevel> Levels { get; init; }
}

public readonly record struct RestingMark(int Index, double Price, double Size, bool IsBid);

public sealed class BookPriceFrame
{
    public IReadOnlyList<double> Bids { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Asks { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Mids { get; init; } = Array.Empty<double>();
    public IReadOnlyList<RestingMark> Marks { get; init; } = Array.Empty<RestingMark>();
}

public readonly record struct BookPriceScale(double Min, double Max)
{
    /// <summary>
    /// Fit bid, ask, and mid. Resting size off the top of book must not stretch the axis
    /// or the chart becomes two filled depth bands.
    /// </summary>
    public static BookPriceScale Of(IEnumerable<double> bids, IEnumerable<double> asks, IEnumerable<double> mids)
    {
        var quotes = bids.Concat(asks).Concat(mids).Where(price => price > 0).ToList();
        if (quotes.Count == 0)
            return new BookPriceScale(0, 1);

        var min = quotes.Min();
        var max = quotes.Max();
        var mid = quotes.Average();
        var span = max - min;
        var pad = Math.Max(span * 2, Math.Abs(mid) * 0.0005);
        if (pad <= 0)
            pad = 1;
        return new BookPriceScale(min - pad, max + pad);
    }

    public bool Contains(double price) => price >= Min && price <= Max;
}

/// <summary>
/// Keeps the first book sample in each aggregation bucket, matching the Windows chart collection.
/// </summary>
public sealed class BookPriceSeries
{
    public const int MaxPoints = 240;
    public const int LevelsPerSide = 6;
    private readonly List<PriceSample> _samples = new();
    private readonly object _gate = new();
    private AggregationLevel _level = AggregationLevel.Ms100;

    public AggregationLevel Level
    {
        get
        {
            lock (_gate)
                return _level;
        }
        set
        {
            lock (_gate)
            {
                _level = value;
                _samples.Clear();
            }
        }
    }

    public bool TryAdd(DateTime time, double bid, double ask, double mid, IReadOnlyList<RestingLevel> levels)
    {
        lock (_gate)
        {
            if (_samples.Count > 0 && _level != AggregationLevel.None)
            {
                var span = _level.ToTimeSpan();
                if (span > TimeSpan.Zero && time - _samples[^1].Time < span)
                    return false;
            }

            _samples.Add(new PriceSample
            {
                Time = time,
                Bid = bid,
                Ask = ask,
                Mid = mid,
                Levels = Limit(levels)
            });
            while (_samples.Count > MaxPoints)
                _samples.RemoveAt(0);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _samples.Clear();
    }

    public BookPriceFrame Frame()
    {
        lock (_gate)
        {
            var marks = new List<RestingMark>();
            for (var i = 0; i < _samples.Count; i++)
            {
                foreach (var level in _samples[i].Levels)
                {
                    if (level.Price > 0 && level.Size > 0)
                        marks.Add(new RestingMark(i, level.Price, level.Size, level.IsBid));
                }
            }

            return new BookPriceFrame
            {
                Bids = _samples.Select(sample => sample.Bid).ToArray(),
                Asks = _samples.Select(sample => sample.Ask).ToArray(),
                Mids = _samples.Select(sample => sample.Mid).ToArray(),
                Marks = marks
            };
        }
    }

    public static IReadOnlyList<RestingLevel> Limit(IReadOnlyList<RestingLevel> levels)
    {
        var bids = levels.Where(level => level.IsBid && level.Price > 0 && level.Size > 0)
            .OrderByDescending(level => level.Price)
            .Take(LevelsPerSide);
        var asks = levels.Where(level => !level.IsBid && level.Price > 0 && level.Size > 0)
            .OrderBy(level => level.Price)
            .Take(LevelsPerSide);
        return bids.Concat(asks).ToArray();
    }
}

public static class TopOfBook
{
    public static (string Whole, string Fraction, string Size) Split(double price, double size, int decimals)
    {
        if (price == 0)
            return ("", "", "");

        decimals = Math.Clamp(decimals, 0, 8);
        var text = price.ToString("N" + decimals, CultureInfo.InvariantCulture);
        string rest;
        string fraction;
        if (decimals > 0)
        {
            var dot = text.LastIndexOf('.');
            rest = text[..dot];
            fraction = text[(dot + 1)..];
        }
        else
        {
            var comma = text.IndexOf(',');
            rest = comma < 0 ? text : text[..comma];
            fraction = comma < 0 ? "" : text[(comma + 1)..];
        }

        return (rest, fraction, HelperCommon.GetKiloFormatter(size));
    }
}

public static class OrderBlotter
{
    public static bool Matches(eORDERSTATUS status, DateTime created, string filter, DateTime sessionDate)
    {
        if (created.Date != sessionDate.Date)
            return false;

        return filter switch
        {
            "Working" => status is eORDERSTATUS.NONE or eORDERSTATUS.SENT or eORDERSTATUS.NEW
                or eORDERSTATUS.PARTIALFILLED or eORDERSTATUS.CANCELEDSENT or eORDERSTATUS.REPLACESENT
                or eORDERSTATUS.REPLACED,
            "Filled" => status is eORDERSTATUS.FILLED or eORDERSTATUS.PARTIALFILLED,
            "Cancelled" => status is eORDERSTATUS.CANCELED or eORDERSTATUS.REJECTED,
            "All" => true,
            _ => false
        };
    }

    public static double FillPercent(double filledQuantity, double quantity) =>
        quantity <= 0 ? 0 : filledQuantity / quantity * 100;

    public static double AverageFill(double filledPrice, IReadOnlyList<(decimal Price, decimal Quantity)> executions)
    {
        if (filledPrice > 0)
            return filledPrice;
        double notional = 0;
        double quantity = 0;
        foreach (var execution in executions)
        {
            if (execution.Quantity <= 0 || execution.Price <= 0)
                continue;
            notional += (double)execution.Price * (double)execution.Quantity;
            quantity += (double)execution.Quantity;
        }

        return quantity <= 0 ? 0 : notional / quantity;
    }
}

public readonly record struct OrderSnap(
    DateTime Created,
    string Provider,
    string Symbol,
    string ClientId,
    double Quantity,
    double Price,
    string Side,
    double FilledPercent,
    double FilledQuantity,
    double AveragePrice,
    string TimeInForce,
    string Type,
    string Status,
    double BestBid,
    double BestAsk,
    eORDERSTATUS StatusCode);

public static class SessionControl
{
    public static bool TryNext(eSESSIONSTATUS current, out bool connect)
    {
        if (current is eSESSIONSTATUS.CONNECTED or eSESSIONSTATUS.CONNECTING or eSESSIONSTATUS.CONNECTED_WITH_WARNINGS)
        {
            connect = false;
            return true;
        }

        if (current is eSESSIONSTATUS.DISCONNECTED or eSESSIONSTATUS.DISCONNECTED_FAILED)
        {
            connect = true;
            return true;
        }

        connect = false;
        return false;
    }

    public static string Color(eSESSIONSTATUS status) => status switch
    {
        eSESSIONSTATUS.CONNECTED => "#3DDC84",
        eSESSIONSTATUS.CONNECTING => "#F5C16C",
        eSESSIONSTATUS.CONNECTED_WITH_WARNINGS => "#3DDC84",
        eSESSIONSTATUS.DISCONNECTED => "#FF6B6B",
        eSESSIONSTATUS.DISCONNECTED_FAILED => "#FF6B6B",
        _ => "#9AA4B2"
    };
}
