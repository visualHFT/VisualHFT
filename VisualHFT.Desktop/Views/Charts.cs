using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VisualHFT.Desktop.Screens;
using VisualHFT.Desktop.ViewModels;

namespace VisualHFT.Desktop.Views;

public sealed class DepthChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<DepthPoint>?> BidsProperty =
        AvaloniaProperty.Register<DepthChart, IReadOnlyList<DepthPoint>?>(nameof(Bids));

    public static readonly StyledProperty<IReadOnlyList<DepthPoint>?> AsksProperty =
        AvaloniaProperty.Register<DepthChart, IReadOnlyList<DepthPoint>?>(nameof(Asks));

    static DepthChart()
    {
        AffectsRender<DepthChart>(BidsProperty, AsksProperty);
    }

    public IReadOnlyList<DepthPoint>? Bids
    {
        get => GetValue(BidsProperty);
        set => SetValue(BidsProperty, value);
    }

    public IReadOnlyList<DepthPoint>? Asks
    {
        get => GetValue(AsksProperty);
        set => SetValue(AsksProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width < 8 || bounds.Height < 8)
            return;

        var bids = Bids;
        var asks = Asks;
        if ((bids == null || bids.Count < 2) && (asks == null || asks.Count < 2))
            return;

        var all = new List<DepthPoint>();
        if (bids != null) all.AddRange(bids);
        if (asks != null) all.AddRange(asks);
        if (all.Count < 2)
            return;

        var minPrice = all.Min(p => p.Price);
        var maxPrice = all.Max(p => p.Price);
        var maxCum = all.Max(p => p.Cumulative);
        if (maxPrice <= minPrice || maxCum <= 0)
            return;

        DrawSide(context, bids, minPrice, maxPrice, maxCum, Color.Parse("#3DDC84"));
        DrawSide(context, asks, minPrice, maxPrice, maxCum, Color.Parse("#FF6B6B"));
    }

    private void DrawSide(DrawingContext context, IReadOnlyList<DepthPoint>? points, double minPrice, double maxPrice, double maxCum, Color color)
    {
        if (points == null || points.Count < 2)
            return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var first = Map(points[0], minPrice, maxPrice, maxCum);
            ctx.BeginFigure(first, true);
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(Map(points[i], minPrice, maxPrice, maxCum));
            ctx.LineTo(new Point(Map(points[^1], minPrice, maxPrice, maxCum).X, Bounds.Height));
            ctx.LineTo(new Point(first.X, Bounds.Height));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(color, 0.35), new Pen(new SolidColorBrush(color), 1.5), geometry);
    }

    private Point Map(DepthPoint point, double minPrice, double maxPrice, double maxCum)
    {
        var x = (point.Price - minPrice) / (maxPrice - minPrice) * Bounds.Width;
        var y = Bounds.Height - (point.Cumulative / maxCum * (Bounds.Height - 4));
        return new Point(x, y);
    }
}

public sealed class SeriesChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<SeriesChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<bool> ScaleEachSeriesProperty =
        AvaloniaProperty.Register<SeriesChart, bool>(nameof(ScaleEachSeries));

    static SeriesChart()
    {
        AffectsRender<SeriesChart>(SeriesProperty, ScaleEachSeriesProperty);
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>
    /// Study value and market mid live on different ranges. Each series then uses its own axis,
    /// matching the Windows chart's separate Y axes.
    /// </summary>
    public bool ScaleEachSeries
    {
        get => GetValue(ScaleEachSeriesProperty);
        set => SetValue(ScaleEachSeriesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var series = Series;
        if (series == null || series.Count == 0 || Bounds.Width < 8 || Bounds.Height < 8)
            return;

        var shared = series.SelectMany(s => s.Values).ToList();
        if (shared.Count < 2)
            return;
        var sharedMin = shared.Min();
        var sharedMax = shared.Max();
        if (Math.Abs(sharedMax - sharedMin) < double.Epsilon)
        {
            sharedMin -= 1;
            sharedMax += 1;
        }

        var legendY = 4.0;
        foreach (var item in series)
        {
            if (item.Values.Count < 2)
                continue;
            var min = sharedMin;
            var max = sharedMax;
            if (item.AxisMin is double fixedMin && item.AxisMax is double fixedMax && fixedMax > fixedMin)
            {
                min = fixedMin;
                max = fixedMax;
            }
            else if (ScaleEachSeries)
            {
                min = item.Values.Min();
                max = item.Values.Max();
                if (Math.Abs(max - min) < double.Epsilon)
                {
                    min -= 1;
                    max += 1;
                }
            }

            if (item.ZeroLine && min < 0 && max > 0)
            {
                var zeroY = Bounds.Height - ((0 - min) / (max - min) * (Bounds.Height - 18));
                context.DrawLine(
                    new Pen(new SolidColorBrush(Color.Parse("#3A4250")), 1) { DashStyle = DashStyle.Dash },
                    new Point(0, zeroY),
                    new Point(Bounds.Width, zeroY));
            }

            var brush = new SolidColorBrush(item.Color);
            var pen = new Pen(brush, 1.6);
            Point? previous = null;
            for (var i = 0; i < item.Values.Count; i++)
            {
                var x = i / (double)(item.Values.Count - 1) * Bounds.Width;
                var y = Bounds.Height - ((item.Values[i] - min) / (max - min) * (Bounds.Height - 18));
                var point = new Point(x, y);
                if (previous is Point last)
                    context.DrawLine(pen, last, point);
                previous = point;
            }

            var label = new FormattedText(item.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
            context.DrawText(label, new Point(8, legendY));
            legendY += 14;
        }
    }
}

public sealed class BookPriceChart : Control
{
    public static readonly StyledProperty<BookPriceFrame?> FrameProperty =
        AvaloniaProperty.Register<BookPriceChart, BookPriceFrame?>(nameof(Frame));

    static BookPriceChart()
    {
        AffectsRender<BookPriceChart>(FrameProperty);
    }

    public BookPriceFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var frame = Frame;
        if (frame == null || frame.Mids.Count == 0 || Bounds.Width < 8 || Bounds.Height < 8)
            return;

        var scale = BookPriceScale.Of(frame.Bids, frame.Asks, frame.Mids);
        var min = scale.Min;
        var max = scale.Max;
        if (max <= min)
            return;

        DrawSpread(context, frame.Bids, frame.Asks, min, max);
        var visible = frame.Marks.Where(mark => mark.Size > 0 && scale.Contains(mark.Price)).ToList();
        var maxSize = visible.Count == 0 ? 1 : visible.Max(mark => mark.Size);
        foreach (var mark in visible)
        {
            var radius = 1.2 + 2.2 * Math.Sqrt(mark.Size / maxSize);
            var brush = new SolidColorBrush(mark.IsBid ? Color.Parse("#3DDC84") : Color.Parse("#FF6B6B"), 0.45);
            context.DrawEllipse(brush, null, new Point(X(mark.Index, frame.Mids.Count), Y(mark.Price, min, max)), radius, radius);
        }

        DrawLine(context, frame.Bids, min, max, Color.Parse("#3DDC84"), 1.8);
        DrawLine(context, frame.Asks, min, max, Color.Parse("#FF6B6B"), 1.8);
        DrawLine(context, frame.Mids, min, max, Color.Parse("#E6EDF7"), 1.2);

        DrawLegend(context, "Bid", Color.Parse("#3DDC84"), 4);
        DrawLegend(context, "Ask", Color.Parse("#FF6B6B"), 56);
        DrawLegend(context, "Mid", Color.Parse("#E6EDF7"), 108);
        DrawLegend(context, "Resting", Color.Parse("#F5C16C"), 156);
    }

    private void DrawSpread(DrawingContext context, IReadOnlyList<double> bids, IReadOnlyList<double> asks, double min, double max)
    {
        var count = Math.Min(bids.Count, asks.Count);
        if (count < 2)
            return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var started = false;
            for (var i = 0; i < count; i++)
            {
                if (bids[i] <= 0 || asks[i] <= 0)
                    continue;
                var point = new Point(X(i, count), Y(asks[i], min, max));
                if (!started)
                {
                    ctx.BeginFigure(point, true);
                    started = true;
                }
                else
                    ctx.LineTo(point);
            }

            for (var i = count - 1; i >= 0; i--)
            {
                if (bids[i] <= 0 || asks[i] <= 0)
                    continue;
                ctx.LineTo(new Point(X(i, count), Y(bids[i], min, max)));
            }

            if (started)
                ctx.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#2A3140"), 0.9), null, geometry);
    }

    private void DrawLine(DrawingContext context, IReadOnlyList<double> values, double min, double max, Color color, double thickness)
    {
        if (values.Count < 2)
            return;
        var pen = new Pen(new SolidColorBrush(color), thickness);
        Point? previous = null;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] <= 0)
            {
                previous = null;
                continue;
            }

            var point = new Point(X(i, values.Count), Y(values[i], min, max));
            if (previous is Point last)
                context.DrawLine(pen, last, point);
            previous = point;
        }
    }

    private void DrawLegend(DrawingContext context, string name, Color color, double x)
    {
        var text = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, new SolidColorBrush(color));
        context.DrawText(text, new Point(x, 2));
    }

    private double X(int index, int count) =>
        count <= 1 ? Bounds.Width / 2 : index / (double)(count - 1) * Bounds.Width;

    private double Y(double price, double min, double max) =>
        Bounds.Height - ((price - min) / (max - min) * (Bounds.Height - 18));
}
