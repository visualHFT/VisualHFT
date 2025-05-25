using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace OxyPlotBenchmark
{
    public static class BenchmarkForkedRealTimeChart
    {
        static int _maxChartPoints = 0;

        public static BenchmarkResult Run(int iterations, int maxChartPoints)
        {
            _maxChartPoints = maxChartPoints;

            string projectConfig = "";
#if DEBUG
            projectConfig = "Debug";
#else
            projectConfig = "Release";
#endif
            // Load forked assemblies
            string oxyPlotCoreDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "oxyplot", "Source", "OxyPlot", "bin", projectConfig, "net8.0-windows8.0"));
            var asmCore = Assembly.LoadFrom(Path.Combine(oxyPlotCoreDir, "OxyPlot.dll"));

            string oxyPlotWpfDir = Path.Combine("..", "..", "..", "..", "..", "oxyplot", "Source", "OxyPlot.Wpf", "bin", projectConfig, "net8.0-windows8.0");
            var asmWpf = Assembly.LoadFrom(Path.Combine(oxyPlotWpfDir, "OxyPlot.Wpf.dll"));

            // Types
            var plotModelType = asmCore.GetType("OxyPlot.PlotModel");
            var lineSeriesType = asmCore.GetType("OxyPlot.Series.LineSeries");
            var scatterSeriesType = asmCore.GetType("OxyPlot.Series.ScatterSeries");
            var scatterPointType = asmCore.GetType("OxyPlot.Series.ScatterPoint");
            var markerTypeEnum = asmCore.GetType("OxyPlot.MarkerType");
            var colorAxisType = asmCore.GetType("OxyPlot.Axes.LinearColorAxis");
            var linearAxisType = asmCore.GetType("OxyPlot.Axes.LinearAxis");
            var dateTimeAxisType = asmCore.GetType("OxyPlot.Axes.DateTimeAxis");

            var oxyColors = asmCore.GetType("OxyPlot.OxyColors");
            var exporterType = asmWpf.GetType("OxyPlot.Wpf.PngExporter");
            var exportMethod = exporterType.GetMethod("Export", new[] { plotModelType, typeof(string), typeof(int), typeof(int) });

            var colorRed = oxyColors?.GetProperty("Red")?.GetValue(null);
            var colorGreen = oxyColors?.GetProperty("Green")?.GetValue(null);
            var colorGray = oxyColors?.GetProperty("Gray")?.GetValue(null);
            var colorDarkRed = oxyColors?.GetField("DarkRed")?.GetValue(null);
            var colorDarkGreen = oxyColors?.GetField("DarkGreen")?.GetValue(null);

            dynamic model = Activator.CreateInstance(plotModelType);
            model.Title = "Forked RealTimePricePlotModel";
            model.DefaultFontSize = 8;

            // Add axes
            dynamic redColorAxis = Activator.CreateInstance(colorAxisType);
            redColorAxis.Key = "RedColorAxis";
            redColorAxis.Minimum = 1;
            redColorAxis.Maximum = 100;
            redColorAxis.IsAxisVisible = false;
            model.Axes.Add(redColorAxis);

            dynamic greenColorAxis = Activator.CreateInstance(colorAxisType);
            greenColorAxis.Key = "GreenColorAxis";
            greenColorAxis.Minimum = 1;
            greenColorAxis.Maximum = 100;
            greenColorAxis.IsAxisVisible = false;
            model.Axes.Add(greenColorAxis);

            model.Axes.Add((OxyPlot.Axes.Axis)Activator.CreateInstance(dateTimeAxisType));
            model.Axes.Add((OxyPlot.Axes.Axis)Activator.CreateInstance(linearAxisType));

            // Create series
            dynamic midLine = Activator.CreateInstance(lineSeriesType);
            midLine.Title = "MidPrice";
            midLine.StrokeThickness = 2;
            //midLine.Color = colorGray;

            dynamic askLine = Activator.CreateInstance(lineSeriesType);
            askLine.Title = "Ask";
            askLine.StrokeThickness = 6;
            //askLine.Color = colorRed;

            dynamic bidLine = Activator.CreateInstance(lineSeriesType);
            bidLine.Title = "Bid";
            bidLine.StrokeThickness = 6;
            //bidLine.Color = colorGreen;

            dynamic scatterAsks = Activator.CreateInstance(scatterSeriesType);
            scatterAsks.Title = "ScatterAsks";
            scatterAsks.ColorAxisKey = "RedColorAxis";
            scatterAsks.MarkerType = (OxyPlot.MarkerType)Enum.Parse(markerTypeEnum!, "Circle");
            scatterAsks.MarkerStroke = (OxyPlot.OxyColor)colorDarkRed;
            scatterAsks.MarkerStrokeThickness = 0;
            scatterAsks.MarkerSize = 10;
            scatterAsks.RenderInLegend = false;
            scatterAsks.Selectable = false;
            scatterAsks.EdgeRenderingMode = OxyPlot.EdgeRenderingMode.PreferSpeed;
            scatterAsks.BinSize = 10;

            dynamic scatterBids = Activator.CreateInstance(scatterSeriesType);
            scatterBids.Title = "ScatterBids";
            scatterBids.ColorAxisKey = "GreenColorAxis";
            scatterBids.MarkerType = (OxyPlot.MarkerType)Enum.Parse(markerTypeEnum!, "Circle");
            scatterBids.MarkerStroke = (OxyPlot.OxyColor)colorDarkGreen;
            scatterBids.MarkerStrokeThickness = 0;
            scatterBids.MarkerSize = 10;
            scatterBids.RenderInLegend = false;
            scatterBids.Selectable = false;
            scatterBids.EdgeRenderingMode = OxyPlot.EdgeRenderingMode.PreferSpeed;
            scatterBids.BinSize = 10;

            model.Series.Add(scatterBids);
            model.Series.Add(scatterAsks);
            model.Series.Add(midLine);
            model.Series.Add(askLine);
            model.Series.Add(bidLine);

            // Start benchmark
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long memBefore = GC.GetTotalMemory(true);

            var sw = Stopwatch.StartNew();
            
            
            for (int t = 0; t < iterations; t++)
            {
                double ts = DateTime.UtcNow.ToOADate();
                midLine.Points.Add(CreateDataPoint(ts, 100));
                askLine.Points.Add(CreateDataPoint(ts, 101));
                bidLine.Points.Add(CreateDataPoint(ts, 99));

                for (int i = 0; i < 100; i++) //add 100 points on each scatter
                {
                    scatterAsks.Points.Add(CreateScatterPoint(ts, 101 + i * 0.01, 4, i));
                    scatterBids.Points.Add(CreateScatterPoint(ts, 99 - i * 0.01, 4, i));
                }
                RemoveOldPoints(midLine, askLine, bidLine, scatterAsks, scatterBids);
                model.InvalidatePlot(true);
            }


            sw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long memAfter = GC.GetTotalMemory(true);

            return new BenchmarkResult
            {
                Engine = "Forked RealTimePricePlotModel",
                PointsPerFrame = maxChartPoints,
                Iterations = iterations,
                TotalTimeMs = sw.ElapsedMilliseconds,
                AverageFrameMs = sw.Elapsed.TotalMilliseconds / iterations,
                MemoryAllocatedKB = (memAfter - memBefore) / 1024
            };

            dynamic CreateDataPoint(double x, double y) =>
                Activator.CreateInstance(asmCore.GetType("OxyPlot.DataPoint"), x, y);

            dynamic CreateScatterPoint(double x, double y, double size, double value) =>
                Activator.CreateInstance(scatterPointType!, x, y, size, value, null);
        }

        private static void RemoveOldPoints(dynamic mid, dynamic ask, dynamic bid, dynamic bids, dynamic asks)
        {
            double xCut = mid.Points[0].X;

            if (mid.Points.Count > _maxChartPoints) mid.Points.RemoveAt(0);
            if (ask.Points.Count > _maxChartPoints) ask.Points.RemoveAt(0);
            if (bid.Points.Count > _maxChartPoints) bid.Points.RemoveAt(0);


            bids.Points.RemoveAll((Predicate<dynamic>)(p => p.X == xCut));
            asks.Points.RemoveAll((Predicate<dynamic>)(p => p.X == xCut));
        }
    }
}
