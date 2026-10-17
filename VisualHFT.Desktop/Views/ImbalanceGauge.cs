using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VisualHFT.Desktop.Screens;

namespace VisualHFT.Desktop.Views;

/// <summary>
/// Horizontal LOB imbalance track: red wings, dark mid, black pointer at signed value (−1..1).
/// </summary>
public sealed class ImbalanceGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ImbalanceGauge, double>(
            nameof(Value),
            defaultValue: 0,
            coerce: (_, value) => LobGauge.Clamp(value));

    private static readonly IBrush Wing = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush Mid = new SolidColorBrush(Color.Parse("#1C2330"));
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#0E1116"));
    private static readonly IBrush Pointer = Brushes.Black;

    static ImbalanceGauge()
    {
        AffectsRender<ImbalanceGauge>(ValueProperty);
    }

    public ImbalanceGauge()
    {
        ClipToBounds = true;
        MinWidth = 40;
        MinHeight = 10;
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width < 8 || bounds.Height < 4)
            return;

        var width = bounds.Width;
        var height = bounds.Height;
        var wing = width * 0.2;
        context.FillRectangle(Track, new Rect(0, 0, width, height));
        context.FillRectangle(Wing, new Rect(0, 0, wing, height));
        context.FillRectangle(Mid, new Rect(wing, 0, width - wing * 2, height));
        context.FillRectangle(Wing, new Rect(width - wing, 0, wing, height));

        const double half = 5;
        var x = Math.Clamp(LobGauge.PointerX(Value, width), half, Math.Max(half, width - half));
        var tip = Math.Min(10, height);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, 0), true);
            ctx.LineTo(new Point(x + half, tip));
            ctx.LineTo(new Point(x - half, tip));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(Pointer, null, geometry);
    }
}
