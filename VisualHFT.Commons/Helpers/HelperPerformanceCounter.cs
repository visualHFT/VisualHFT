using System.Diagnostics;
using System.Threading.Tasks;
using log4net;

namespace VisualHFT.Commons.Helpers
{
    public class HelperPerformanceCounter : IDisposable
    {
        private PerformanceCounter _itemsInQueueCounter;
        private PerformanceCounter _addThroughputCounter;
        private PerformanceCounter _processThroughputCounter;
        private PerformanceCounter _latencyCounter;
        private PerformanceCounter _latencyBaseCounter;

        private readonly object _lock = new object();
        private static readonly object _ctorLock = new object();
        protected static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public HelperPerformanceCounter(string category, string queueName)
        {
            string categoryName = $"VisualHFT_{category}_{queueName}";
            if (categoryName.Length > 80)
            {
                throw new ArgumentException("Category name exceeds the maximum length of 256 characters.");
            }
            lock (_ctorLock)
            {
                CreateOrUpdateCategory(categoryName, $"{category} - {queueName}");
                InitializeCounters(categoryName, queueName);
            }
        }

        private void CreateOrUpdateCategory(string categoryName, string categoryDescription)
        {
            // Windows performance counters are unavailable on macOS. Callers still run;
            // the increment methods no-op when the counters were not created.
            try
            {
                if (!PerformanceCounterCategory.Exists(categoryName))
                {
                    try
                    {
                        CounterCreationDataCollection counters = new CounterCreationDataCollection
                        {
                            new CounterCreationData("ItemsInQueue", "Number of items in the queue", PerformanceCounterType.NumberOfItems32),
                            new CounterCreationData("AddThroughput", "Add throughput of the queue", PerformanceCounterType.RateOfCountsPerSecond32),
                            new CounterCreationData("ProcessThroughput", "Process throughput of the queue", PerformanceCounterType.RateOfCountsPerSecond32),
                            new CounterCreationData("Latency", "Latency of items being processed", PerformanceCounterType.AverageTimer32),
                            new CounterCreationData("LatencyBase", "Base counter for latency", PerformanceCounterType.AverageBase)
                        };
                        PerformanceCounterCategory.Create(categoryName, categoryDescription, PerformanceCounterCategoryType.MultiInstance, counters);
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        private void InitializeCounters(string categoryName, string instanceName)
        {
            try
            {
                _itemsInQueueCounter = new PerformanceCounter(categoryName, "ItemsInQueue", instanceName, false);
                _addThroughputCounter = new PerformanceCounter(categoryName, "AddThroughput", instanceName, false);
                _processThroughputCounter = new PerformanceCounter(categoryName, "ProcessThroughput", instanceName, false);
                _latencyCounter = new PerformanceCounter(categoryName, "Latency", instanceName, false);
                _latencyBaseCounter = new PerformanceCounter(categoryName, "LatencyBase", instanceName, false);
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }



        public void QueueItemAdded()
        {
            _itemsInQueueCounter?.Increment();
            _addThroughputCounter?.Increment();
        }

        public void QueueItemRemoved()
        {
            _itemsInQueueCounter?.Decrement();
            _processThroughputCounter?.Increment();
        }

        public void UpdateLatency(long latency)
        {
            _latencyCounter?.IncrementBy(latency);
            _latencyBaseCounter?.Increment();
        }

        private void ResetCounters()
        {
            if (_itemsInQueueCounter == null)
                return;
            _itemsInQueueCounter.RawValue = 0;
            _addThroughputCounter.RawValue = 0;
            _processThroughputCounter.RawValue = 0;
            _latencyCounter.RawValue = 0;
            _latencyBaseCounter.RawValue = 0;
        }

        public void Dispose()
        {
            ResetCounters();
            _itemsInQueueCounter?.Dispose();
            _addThroughputCounter?.Dispose();
            _processThroughputCounter?.Dispose();
            _latencyCounter?.Dispose();
            _latencyBaseCounter?.Dispose();
        }
    }
}
