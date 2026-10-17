using System.Globalization;
using VisualHFT.Commons.Helpers;
using VisualHFT.Enums;

namespace VisualHFT.Desktop.Screens;

public readonly record struct QuoteParts(string Rest, string NextTwo, string LastDecimal, string Size);

/// <summary>
/// Splits a top-of-book quote the way Windows BookItemPriceSplit.SetNumber does,
/// then peels the last fraction digit when the book shows three or more decimals.
/// </summary>
public static class QuoteFace
{
    public static QuoteParts Split(double price, double size, int decimals)
    {
        if (price == 0 || !double.IsFinite(price))
            return new QuoteParts("", "", "", "");

        decimals = Math.Clamp(decimals, 0, 8);
        var text = price.ToString("N" + decimals, CultureInfo.InvariantCulture);

        string rest;
        string fraction;
        if (decimals > 0)
        {
            var dot = text.LastIndexOf('.');
            if (dot < 0)
            {
                rest = text;
                fraction = new string('0', decimals);
            }
            else
            {
                rest = text[..dot];
                fraction = text[(dot + 1)..].PadRight(decimals, '0');
                if (fraction.Length > decimals)
                    fraction = fraction[..decimals];
            }
        }
        else
        {
            rest = text;
            fraction = "";
        }

        string nextTwo;
        string lastDecimal;
        if (fraction.Length >= 3)
        {
            lastDecimal = fraction[^1].ToString(CultureInfo.InvariantCulture);
            nextTwo = fraction[..^1];
        }
        else
        {
            lastDecimal = "";
            nextTwo = fraction;
        }

        return new QuoteParts(rest, nextTwo, lastDecimal, HelperCommon.GetKiloFormatter(size));
    }
}

public static class LobGauge
{
    public static double Clamp(double imbalance) =>
        double.IsFinite(imbalance) ? Math.Clamp(imbalance, -1, 1) : 0;

    /// <summary>
    /// Map signed imbalance (−1..1) onto a horizontal track (0..width). Zero sits at mid-track.
    /// </summary>
    public static double PointerX(double imbalance, double width)
    {
        if (width <= 0)
            return 0;
        return (Clamp(imbalance) + 1) / 2 * width;
    }
}

/// <summary>
/// Keeps the first spread sample in each aggregation bucket, matching BookPriceSeries.
/// </summary>
public sealed class SpreadTracker
{
    public const int MaxPoints = 240;
    private readonly List<(DateTime Time, double Spread)> _samples = new();
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

    public bool TryAdd(DateTime time, double spread)
    {
        lock (_gate)
        {
            if (_samples.Count > 0 && _level != AggregationLevel.None)
            {
                var span = _level.ToTimeSpan();
                if (span > TimeSpan.Zero && time - _samples[^1].Time < span)
                    return false;
            }

            _samples.Add((time, spread));
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

    public IReadOnlyList<double> Values()
    {
        lock (_gate)
            return _samples.Select(sample => sample.Spread).ToArray();
    }
}

public enum BookLayout
{
    SideBySide,
    Mirror,
    Ladder
}

public static class BookLayoutCycle
{
    public static BookLayout Next(BookLayout current) => current switch
    {
        BookLayout.SideBySide => BookLayout.Mirror,
        BookLayout.Mirror => BookLayout.Ladder,
        BookLayout.Ladder => BookLayout.SideBySide,
        _ => BookLayout.SideBySide
    };
}

public static class MarketChrome
{
    public static void Validate()
    {
        var quote = QuoteFace.Split(1234.5678, 25000, 4);
        if (quote.Rest != "1,234")
            throw new InvalidOperationException("Quote rest should group the integer as 1,234, not " + quote.Rest + ".");
        if (quote.NextTwo != "567" || quote.LastDecimal != "8")
            throw new InvalidOperationException("Four-decimal quotes should peel the last tick: got " + quote.NextTwo + " / " + quote.LastDecimal + ".");
        if (quote.Size != HelperCommon.GetKiloFormatter(25000) || !quote.Size.Contains('k', StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Quote size should use the kilo formatter, not " + quote.Size + ".");

        var empty = QuoteFace.Split(0, 10, 4);
        if (empty.Rest != "" || empty.NextTwo != "" || empty.LastDecimal != "" || empty.Size != "")
            throw new InvalidOperationException("A zero price must clear every quote part.");

        var two = QuoteFace.Split(1234.56, 1, 2);
        if (two.Rest != "1,234" || two.NextTwo != "56" || two.LastDecimal != "")
            throw new InvalidOperationException("One- and two-decimal quotes should leave LastDecimal empty.");

        if (LobGauge.Clamp(1.7) != 1 || LobGauge.Clamp(-4) != -1)
            throw new InvalidOperationException("Imbalance must clamp to the closed interval −1..1.");
        if (Math.Abs(LobGauge.PointerX(0, 200) - 100) > 1e-9)
            throw new InvalidOperationException("A balanced book should park the gauge pointer at width/2.");
        if (Math.Abs(LobGauge.PointerX(-1, 200)) > 1e-9 || Math.Abs(LobGauge.PointerX(1, 200) - 200) > 1e-9)
            throw new InvalidOperationException("Gauge extremes should map onto 0 and width.");

        var spread = new SpreadTracker { Level = AggregationLevel.Ms100 };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (!spread.TryAdd(t0, 0.10))
            throw new InvalidOperationException("The first spread sample should be kept.");
        if (spread.TryAdd(t0.AddMilliseconds(40), 0.11))
            throw new InvalidOperationException("A spread inside the 100ms bucket should be dropped.");
        if (!spread.TryAdd(t0.AddMilliseconds(100), 0.12))
            throw new InvalidOperationException("A spread on the next bucket should be kept.");
        var values = spread.Values();
        if (values.Count != 2 || values[0] != 0.10 || values[1] != 0.12)
            throw new InvalidOperationException("Spread tracker did not keep the first sample in each bucket.");
        for (var i = 0; i < SpreadTracker.MaxPoints + 10; i++)
            spread.TryAdd(t0.AddMilliseconds(200 + i * 100), 1 + i);
        if (spread.Values().Count != SpreadTracker.MaxPoints)
            throw new InvalidOperationException("Spread tracker exceeded " + SpreadTracker.MaxPoints + " points.");

        if (BookLayoutCycle.Next(BookLayout.SideBySide) != BookLayout.Mirror
            || BookLayoutCycle.Next(BookLayout.Mirror) != BookLayout.Ladder
            || BookLayoutCycle.Next(BookLayout.Ladder) != BookLayout.SideBySide)
            throw new InvalidOperationException("Book layout must cycle SideBySide → Mirror → Ladder → SideBySide.");
    }
}
