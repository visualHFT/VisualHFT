namespace VisualHFT.Desktop.Screens;

/// <summary>
/// Avalonia hex encoding for the positions and orders blotter.
/// Matches Windows ucPositions meaning (P/L row tint, side, status chip, cancel
/// affordance, fill bar width) without WPF brushes or DataTriggers.
/// </summary>
public static class BlotterPaint
{
    public const string Profit = "#15803d";
    public const string Loss = "#b91c1c";
    public const string Transparent = "#00000000";
    public const string Buy = "#1d4ed8";
    public const string Partial = "#a3e635";
    public const string Working = "#6b7280";
    public const string Neutral = "#e5e7eb";

    public static string PositionFill(double plTot)
    {
        if (double.IsNaN(plTot) || plTot == 0)
            return Transparent;
        return plTot > 0 ? Profit : Loss;
    }

    public static string SideFill(string? side) => Normalize(side) switch
    {
        "BUY" => Buy,
        "SELL" => Loss,
        _ => Transparent
    };

    public static string StatusFill(string? status) => Normalize(status) switch
    {
        "FILLED" => Profit,
        "CANCELED" or "REJECTED" => Loss,
        "PARTIALFILLED" => Partial,
        "SENT" or "NEW" or "REPLACED" or "CANCELEDSENT" or "REPLACESENT" => Working,
        _ => Neutral
    };

    /// <summary>Map a 0–100 fill percent onto a bar width, clamped.</summary>
    public static double FillWidth(double percent, double maxWidth)
    {
        if (double.IsNaN(percent) || double.IsNaN(maxWidth) || maxWidth <= 0)
            return 0;
        return Math.Clamp(percent, 0, 100) / 100.0 * maxWidth;
    }

    public static bool CanCancel(string? status) => Normalize(status) is "REPLACED" or "SENT" or "NEW" or "PARTIALFILLED";

    public static void Validate()
    {
        if (PositionFill(1) != Profit || PositionFill(-0.01) != Loss)
            throw new InvalidOperationException("Position P/L should tint green above zero and red below.");
        if (PositionFill(0) != Transparent || PositionFill(double.NaN) != Transparent || PositionFill(-0.0) != Transparent)
            throw new InvalidOperationException("Flat or NaN P/L should stay transparent.");

        if (SideFill("Buy") != Buy || SideFill("SELL") != Loss || SideFill("None") != Transparent || SideFill(null) != Transparent)
            throw new InvalidOperationException("Side colors should map Buy/Sell and ignore the rest.");

        if (StatusFill("FILLED") != Profit)
            throw new InvalidOperationException("FILLED should be green.");
        if (StatusFill("CANCELED") != Loss || StatusFill("rejected") != Loss)
            throw new InvalidOperationException("CANCELED and REJECTED should be red.");
        if (StatusFill("PARTIALFILLED") != Partial)
            throw new InvalidOperationException("PARTIALFILLED should be lime.");
        foreach (var working in new[] { "SENT", "NEW", "REPLACED", "CANCELEDSENT", "REPLACESENT" })
        {
            if (StatusFill(working) != Working)
                throw new InvalidOperationException(working + " should use the working gray.");
        }
        if (StatusFill("NONE") != Neutral || StatusFill("LASTLOOK_HOLDING") != Neutral || StatusFill(null) != Neutral)
            throw new InvalidOperationException("Unknown status should use the white-ish chip.");

        if (FillWidth(0, 200) != 0 || Math.Abs(FillWidth(25, 200) - 50) > 1e-9 || FillWidth(100, 80) != 80)
            throw new InvalidOperationException("Fill width should scale 0–100 onto maxWidth.");
        if (FillWidth(-10, 100) != 0 || FillWidth(150, 40) != 40 || FillWidth(double.NaN, 10) != 0 || FillWidth(50, 0) != 0)
            throw new InvalidOperationException("Fill width should clamp percent and reject empty max width.");

        foreach (var status in new[] { "REPLACED", "SENT", "NEW", "PARTIALFILLED" })
        {
            if (!CanCancel(status))
                throw new InvalidOperationException(status + " should offer cancel.");
        }
        foreach (var status in new[] { "FILLED", "CANCELED", "REJECTED", "CANCELEDSENT", "REPLACESENT", "NONE", "" })
        {
            if (CanCancel(status))
                throw new InvalidOperationException(status + " should not offer cancel.");
        }
        if (CanCancel(null))
            throw new InvalidOperationException("A missing status should not offer cancel.");
    }

    private static string Normalize(string? value) => (value ?? "").Trim().ToUpperInvariant();
}
