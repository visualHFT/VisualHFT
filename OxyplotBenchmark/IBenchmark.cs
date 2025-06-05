namespace OxyPlotBenchmark
{
    public interface IBenchmark
    {
        BenchmarkResult Run(int iterations, int maxChartPoints);
    }
}