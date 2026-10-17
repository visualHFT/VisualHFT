using System.Globalization;

namespace VisualHFT.Desktop.Screens;

/// <summary>
/// Live-session metrics from the equity path, tape, and open positions.
/// Windows AnalyticReports scores closed Position signals; Mac has a running
/// equity series, so these ratios use successive equity differences (not percent
/// returns and not closed-trade PnL).
/// </summary>
public sealed record SessionAnalytics
{
    public int TradeCount { get; init; }
    public double Volume { get; init; }
    public int BuyCount { get; init; }
    public int SellCount { get; init; }
    public double AverageSize { get; init; }
    public double Realized { get; init; }
    public double Open { get; init; }
    public double Net { get; init; }
    public double MaxDrawdown { get; init; }

    /// <summary>
    /// sqrt(n) × mean(returns) / population stdev(returns), n = equity steps.
    /// NaN when n &lt; 2. Zero when stdev is 0.
    /// </summary>
    public double Sharpe { get; init; }

    /// <summary>
    /// Gross positive returns / abs(gross negative returns). NaN when there are no losses.
    /// </summary>
    public double ProfitFactor { get; init; }

    /// <summary>
    /// Strictly positive returns / nonzero returns. Flat steps are excluded so a quiet
    /// book does not inflate the hit rate. NaN when every step is flat or there are no steps.
    /// </summary>
    public double WinRate { get; init; }

    /// <summary>Mean equity-step return. NaN when there are no steps.</summary>
    public double Expectancy { get; init; }

    public static SessionAnalytics From(
        IReadOnlyList<double>? equity,
        IReadOnlyList<TradePrint>? trades,
        IReadOnlyList<PositionSnap>? positions)
    {
        equity ??= Array.Empty<double>();
        trades ??= Array.Empty<TradePrint>();
        positions ??= Array.Empty<PositionSnap>();

        var totals = SessionReport.Compute(trades, positions, equity);
        var returns = Diffs(equity);
        var mean = Mean(returns);
        return new SessionAnalytics
        {
            TradeCount = totals.TradeCount,
            Volume = totals.Volume,
            BuyCount = totals.BuyCount,
            SellCount = totals.SellCount,
            AverageSize = totals.AverageSize,
            Realized = totals.Realized,
            Open = totals.Open,
            Net = totals.Net,
            MaxDrawdown = totals.MaxDrawdown,
            Sharpe = SharpeOf(returns, mean),
            ProfitFactor = ProfitFactorOf(returns),
            WinRate = WinRateOf(returns),
            Expectancy = mean
        };
    }

    /// <summary>
    /// InvariantCulture multi-line desk report. Non-finite ratios render as "—".
    /// </summary>
    public string Text()
    {
        return
            $"Trades {TradeCount}   Volume {Num(Volume)}   Avg size {Num(AverageSize)}\n" +
            $"Buys {BuyCount}   Sells {SellCount}\n" +
            $"Realized {Num(Realized)}   Open {Num(Open)}   Net {Num(Net)}\n" +
            $"Sharpe {Num(Sharpe)}   PF {Num(ProfitFactor)}   WinRate {Rate(WinRate)}\n" +
            $"Expectancy {Num(Expectancy)}   Drawdown {Num(MaxDrawdown)}";
    }

    public static void Validate()
    {
        var trades = new[]
        {
            new TradePrint("BTC/USD", "Binance", 100, 2, true, DateTime.UtcNow),
            new TradePrint("BTC/USD", "Binance", 101, 1, false, DateTime.UtcNow)
        };
        var positions = new[] { new PositionSnap("BTC/USD", 1, 12.5, -2) };
        var session = From(new[] { 0d, 10d, 8d, 15d, 4d }, trades, positions);
        if (session.TradeCount != 2 || session.Volume != 3 || session.BuyCount != 1 || session.SellCount != 1)
            throw new InvalidOperationException("Trade totals are wrong.");
        if (Math.Abs(session.AverageSize - 1.5) > 1e-9 || session.Realized != 12.5 || session.Open != -2 || session.Net != 1)
            throw new InvalidOperationException("Position totals are wrong.");
        if (Math.Abs(session.MaxDrawdown - 11) > 1e-9)
            throw new InvalidOperationException("Drawdown should be 11 (peak 15 to 4).");
        if (Math.Abs(session.Expectancy - 1) > 1e-9)
            throw new InvalidOperationException("Expectancy should be the mean equity step.");
        if (Math.Abs(session.ProfitFactor - 17.0 / 13.0) > 1e-9)
            throw new InvalidOperationException("Profit factor should be 17/13.");
        if (Math.Abs(session.WinRate - 0.5) > 1e-9)
            throw new InvalidOperationException("Win rate should count two up steps of four nonzero steps.");
        if (Math.Abs(session.Sharpe - 2.0 / Math.Sqrt(67.5)) > 1e-9)
            throw new InvalidOperationException("Sharpe did not match sqrt(n)×mean/population stdev.");

        var tiny = From(new[] { 0d, 1d, 3d }, Array.Empty<TradePrint>(), Array.Empty<PositionSnap>());
        if (Math.Abs(tiny.Sharpe - 3.0 * Math.Sqrt(2.0)) > 1e-9)
            throw new InvalidOperationException("Sharpe on [0,1,3] should be 3√2.");
        if (!double.IsNaN(tiny.ProfitFactor))
            throw new InvalidOperationException("A session with no losses has no profit factor.");
        if (Math.Abs(tiny.WinRate - 1) > 1e-9 || Math.Abs(tiny.Expectancy - 1.5) > 1e-9)
            throw new InvalidOperationException("All-positive steps should win every nonzero bar.");

        var flat = From(new[] { 5d, 5d, 5d }, null, null);
        if (flat.Sharpe != 0 || !double.IsNaN(flat.WinRate) || flat.Expectancy != 0)
            throw new InvalidOperationException("A flat equity path should report Sharpe 0 and no win rate.");

        var oneLoss = From(new[] { 10d, 5d, 0d }, Array.Empty<TradePrint>(), Array.Empty<PositionSnap>());
        if (oneLoss.ProfitFactor != 0 || oneLoss.WinRate != 0 || oneLoss.Sharpe != 0)
            throw new InvalidOperationException("All-loss equal steps should have PF 0, win rate 0, Sharpe 0.");

        var empty = From(Array.Empty<double>(), Array.Empty<TradePrint>(), Array.Empty<PositionSnap>());
        if (empty.TradeCount != 0 || empty.MaxDrawdown != 0)
            throw new InvalidOperationException("Empty equity should not throw and drawdown should be 0.");
        if (!double.IsNaN(empty.Sharpe) || !double.IsNaN(empty.ProfitFactor) || !double.IsNaN(empty.WinRate) || !double.IsNaN(empty.Expectancy))
            throw new InvalidOperationException("Empty equity should leave ratios undefined.");

        var single = From(new[] { 5d }, Array.Empty<TradePrint>(), Array.Empty<PositionSnap>());
        if (!double.IsNaN(single.Sharpe) || single.MaxDrawdown != 0)
            throw new InvalidOperationException("A single equity point is not enough for Sharpe.");
        var pair = From(new[] { 0d, 10d }, Array.Empty<TradePrint>(), Array.Empty<PositionSnap>());
        if (!double.IsNaN(pair.Sharpe) || Math.Abs(pair.Expectancy - 10) > 1e-9)
            throw new InvalidOperationException("One equity step is not enough for Sharpe.");

        var text = empty.Text();
        if (!text.Contains("Sharpe", StringComparison.Ordinal) || !text.Contains("PF", StringComparison.Ordinal)
            || !text.Contains("WinRate", StringComparison.Ordinal) || !text.Contains("Drawdown", StringComparison.Ordinal)
            || !text.Contains("Expectancy", StringComparison.Ordinal) || !text.Contains("—", StringComparison.Ordinal))
            throw new InvalidOperationException("Text() should name the ratios and use — for NaN.");
        if (session.Text().Contains("NaN", StringComparison.Ordinal) || session.Text().Contains(','))
            throw new InvalidOperationException("Text() should stay on InvariantCulture without NaN literals.");
    }

    private static double[] Diffs(IReadOnlyList<double> equity)
    {
        if (equity.Count < 2)
            return Array.Empty<double>();
        var returns = new double[equity.Count - 1];
        for (var i = 1; i < equity.Count; i++)
            returns[i - 1] = equity[i] - equity[i - 1];
        return returns;
    }

    private static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return double.NaN;
        var sum = 0d;
        foreach (var value in values)
            sum += value;
        return sum / values.Count;
    }

    private static double SharpeOf(IReadOnlyList<double> returns, double mean)
    {
        if (returns.Count < 2)
            return double.NaN;
        var stdev = 0d;
        foreach (var value in returns)
        {
            var delta = value - mean;
            stdev += delta * delta;
        }
        stdev = Math.Sqrt(stdev / returns.Count);
        if (stdev == 0)
            return 0;
        return Math.Sqrt(returns.Count) * mean / stdev;
    }

    private static double ProfitFactorOf(IReadOnlyList<double> returns)
    {
        var profit = 0d;
        var loss = 0d;
        foreach (var value in returns)
        {
            if (value > 0)
                profit += value;
            else if (value < 0)
                loss += value;
        }
        return loss == 0 ? double.NaN : profit / Math.Abs(loss);
    }

    private static double WinRateOf(IReadOnlyList<double> returns)
    {
        var wins = 0;
        var played = 0;
        foreach (var value in returns)
        {
            if (value == 0)
                continue;
            played++;
            if (value > 0)
                wins++;
        }
        return played == 0 ? double.NaN : (double)wins / played;
    }

    private static string Num(double value) =>
        double.IsNaN(value) ? "—" : value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Rate(double value) =>
        double.IsNaN(value) ? "—" : (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
