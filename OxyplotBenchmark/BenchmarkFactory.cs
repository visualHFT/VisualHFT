using System.IO;
namespace OxyPlotBenchmark
{
    public static class BenchmarkFactory
    {
        private static readonly string _originalPath = Path.Combine(AppContext.BaseDirectory, "Original");
        private static readonly string _forkedPath = Path.Combine(AppContext.BaseDirectory, "Forked");

        public static IBenchmark CreateBenchmark(bool useForked)
        {
            var context = new OxyPlotLoadContext(useForked ? _forkedPath : _originalPath);
            return useForked ?
                new BenchmarkForkedRealTimeChart(context) :
                new BenchmarkOfficialRealTimeChart(context);
        }
    }
}