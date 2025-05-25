using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Wpf;
using System;
using System.Diagnostics;
using System.IO;

namespace OxyPlotBenchmark
{
    public static class BenchmarkOfficialRealTimeChart
    {
        static int _maxChartPoints = 0;
        public static BenchmarkResult Run(int iterations, int maxChartPoints)
        {
            _maxChartPoints = maxChartPoints;
            var model = new PlotModel { Title = "Official RealTimePricePlotModel", DefaultFontSize = 8 };
            ConfigureAxes(model);

            var lineMid = CreateLineSeries("MidPrice", OxyColors.Gray, 2);
            var lineAsk = CreateLineSeries("Ask", OxyColors.Red, 6);
            var lineBid = CreateLineSeries("Bid", OxyColors.Green, 6);
            var scatterAsks = CreateScatterSeries("ScatterAsks", "RedColorAxis", OxyColors.DarkRed);
            var scatterBids = CreateScatterSeries("ScatterBids", "GreenColorAxis", OxyColors.DarkGreen);

            model.Series.Add(scatterBids);
            model.Series.Add(scatterAsks);
            model.Series.Add(lineMid);
            model.Series.Add(lineAsk);
            model.Series.Add(lineBid);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long memBefore = GC.GetTotalMemory(true);

            var sw = Stopwatch.StartNew();
            for (int t = 0; t < iterations; t++)
            {
                double ts = DateTime.UtcNow.ToOADate();
                
                lineMid.Points.Add(new DataPoint(ts, 100));
                lineAsk.Points.Add(new DataPoint(ts, 101));
                lineBid.Points.Add(new DataPoint(ts, 99));

                for (int i = 0; i < 100; i++) //add 100 points on each scatter
                {
                    scatterAsks.Points.Add(new ScatterPoint(ts, 101 + i * 0.01, 4, i));
                    scatterBids.Points.Add(new ScatterPoint(ts, 99 - i * 0.01, 4, i));
                }
                RemoveOldPoints(lineMid, lineAsk, lineBid, scatterAsks, scatterBids);
                model.InvalidatePlot(true);
            }
            sw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long memAfter = GC.GetTotalMemory(true);

            return new BenchmarkResult
            {
                Engine = "Official RealTimePricePlotModel",
                PointsPerFrame = maxChartPoints,
                Iterations = iterations,
                TotalTimeMs = sw.ElapsedMilliseconds,
                AverageFrameMs = sw.Elapsed.TotalMilliseconds / iterations,
                MemoryAllocatedKB = (memAfter - memBefore) / 1024
            };
        }

        private static void ConfigureAxes(PlotModel model)
        {
            model.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, IsAxisVisible = false });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left });
            model.Axes.Add(new LinearColorAxis { Position = AxisPosition.Right, Key = "RedColorAxis", IsAxisVisible = false, Minimum = 1, Maximum = 100 });
            model.Axes.Add(new LinearColorAxis { Position = AxisPosition.Right, Key = "GreenColorAxis", IsAxisVisible = false, Minimum = 1, Maximum = 100 });
        }

        private static LineSeries CreateLineSeries(string title, OxyColor color, int thickness) =>
            new LineSeries { Title = title, Color = color, StrokeThickness = thickness, MarkerType = MarkerType.None, EdgeRenderingMode = EdgeRenderingMode.PreferSpeed };

        private static ScatterSeries CreateScatterSeries(string title, string colorAxisKey, OxyColor strokeColor) =>
            new ScatterSeries
            {
                Title = title,
                ColorAxisKey = colorAxisKey,
                MarkerType = MarkerType.Circle,
                MarkerStroke = strokeColor,
                MarkerStrokeThickness = 0,
                MarkerSize = 10,
                RenderInLegend = false,
                Selectable = false,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                BinSize = 10
            };

        private static void RemoveOldPoints(LineSeries mid, LineSeries ask, LineSeries bid, ScatterSeries bids, ScatterSeries asks)
        {
            double xCut = mid.Points[0].X;
            if (mid.Points.Count > _maxChartPoints) mid.Points.RemoveAt(0);
            if (ask.Points.Count > _maxChartPoints) ask.Points.RemoveAt(0);
            if (bid.Points.Count > _maxChartPoints) bid.Points.RemoveAt(0);

            bids.Points.RemoveAll(p => p.X == xCut);
            asks.Points.RemoveAll(p => p.X == xCut);
        }
    }
}
