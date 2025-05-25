namespace OxyPlotBenchmark
{
    public class BenchmarkResult
    {
        public string Engine { get; set; }
        public int PointsPerFrame { get; set; }
        public int Iterations { get; set; }
        public double AverageFrameMs { get; set; }
        public long TotalTimeMs { get; set; }
        public long MemoryAllocatedKB { get; set; }

        public override string ToString()
        {
            return
$@"=== {Engine} ===
Iterations:          {Iterations}
Points per frame:    {PointsPerFrame}
Total Time (ms):     {TotalTimeMs}
Avg. per frame (ms): {AverageFrameMs:F2}
Memory Allocated:    {MemoryAllocatedKB:N0} KB
";
        }
    }
}
