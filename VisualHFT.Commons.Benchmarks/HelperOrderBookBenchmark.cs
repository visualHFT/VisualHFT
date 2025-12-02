using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using VisualHFT.Commons.Messaging;
using VisualHFT.Model;

namespace VisualHFT.Benchmarks
{
    /// <summary>
    /// Performance benchmarks for the multicast ring buffer architecture.
    /// 
    /// Run benchmarks:
    ///   dotnet run -c Release -- --filter "*"
    /// 
    /// Expected results:
    /// - Publish latency: 50-100 nanoseconds
    /// - Read latency: 30-50 nanoseconds
    /// - Throughput: 50-100M messages/second
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
    public class MulticastRingBufferBenchmark
    {
        private MulticastRingBuffer<ImmutableOrderBook>? _buffer;
        private ImmutableOrderBook? _testBook;
        private ConsumerCursor? _cursor;
        private OrderBook? _mutableBook;

        [GlobalSetup]
        public void Setup()
        {
            _buffer = new MulticastRingBuffer<ImmutableOrderBook>(65536);
            _mutableBook = CreateTestOrderBook();
            _testBook = ImmutableOrderBook.CreateSnapshot(_mutableBook, 0);
            _cursor = _buffer.Subscribe("BenchmarkConsumer");

            // Pre-warm
            for (int i = 0; i < 1000; i++)
            {
                _buffer.Publish(_testBook);
                _buffer.TryRead(_cursor, out _, out _);
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _buffer?.Dispose();
            _mutableBook?.Dispose();
        }

        /// <summary>
        /// Benchmark: Raw publish latency (no consumer).
        /// Target: 50-100 nanoseconds
        /// </summary>
        [Benchmark(Description = "Publish (no consumer)")]
        public long Publish()
        {
            return _buffer!.Publish(_testBook!);
        }

        /// <summary>
        /// Benchmark: Snapshot creation latency.
        /// Target: 100-200 nanoseconds
        /// </summary>
        [Benchmark(Description = "CreateSnapshot")]
        public ImmutableOrderBook CreateSnapshot()
        {
            return ImmutableOrderBook.CreateSnapshot(_mutableBook!, 1);
        }

        /// <summary>
        /// Benchmark: Publish + Read roundtrip.
        /// Target: 80-150 nanoseconds total
        /// </summary>
        [Benchmark(Description = "Publish + Read")]
        public bool PublishAndRead()
        {
            _buffer!.Publish(_testBook!);
            return _buffer.TryRead(_cursor!, out _, out _);
        }

        /// <summary>
        /// Benchmark: Full producer path (snapshot + publish).
        /// This represents the real-world producer cost.
        /// Target: 150-300 nanoseconds
        /// </summary>
        [Benchmark(Description = "Full Producer Path")]
        public long FullProducerPath()
        {
            var snapshot = ImmutableOrderBook.CreateSnapshot(_mutableBook!, 1);
            return _buffer!.Publish(snapshot);
        }

        /// <summary>
        /// Benchmark: ToMutable conversion (allocation).
        /// This shows the cost of legacy API support.
        /// Target: 1-10 microseconds
        /// </summary>
        [Benchmark(Description = "ToMutable (allocation)")]
        public OrderBook ToMutable()
        {
            var result = _testBook!.ToMutable();
            result.Dispose();
            return result;
        }

        private static OrderBook CreateTestOrderBook()
        {
            var orderBook = new OrderBook("BTCUSD", 2, 20);
            orderBook.ProviderID = 1;
            orderBook.ProviderName = "TestProvider";

            var bids = new BookItem[20];
            var asks = new BookItem[20];

            for (int i = 0; i < 20; i++)
            {
                bids[i] = new BookItem
                {
                    Price = 100.0 - i * 0.1,
                    Size = 10.0 + i,
                    IsBid = true,
                    ServerTimeStamp = DateTime.UtcNow,
                    LocalTimeStamp = DateTime.UtcNow
                };

                asks[i] = new BookItem
                {
                    Price = 100.1 + i * 0.1,
                    Size = 10.0 + i,
                    IsBid = false,
                    ServerTimeStamp = DateTime.UtcNow,
                    LocalTimeStamp = DateTime.UtcNow
                };
            }

            orderBook.LoadData(asks, bids);
            return orderBook;
        }
    }

    /// <summary>
    /// Throughput benchmarks to measure messages per second.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
    public class ThroughputBenchmark
    {
        private MulticastRingBuffer<ImmutableOrderBook>? _buffer;
        private ImmutableOrderBook? _testBook;
        private OrderBook? _mutableBook;

        private const int MessageCount = 1_000_000;

        [GlobalSetup]
        public void Setup()
        {
            _buffer = new MulticastRingBuffer<ImmutableOrderBook>(1048576); // 1M buffer
            _mutableBook = CreateTestOrderBook();
            _testBook = ImmutableOrderBook.CreateSnapshot(_mutableBook, 0);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _buffer?.Dispose();
            _mutableBook?.Dispose();
        }

        /// <summary>
        /// Benchmark: Publish 1 million messages.
        /// Target: 10-20 milliseconds (50-100M msg/sec)
        /// </summary>
        [Benchmark(Description = "1M Publishes")]
        public long PublishOneMillion()
        {
            long lastSeq = 0;
            for (int i = 0; i < MessageCount; i++)
            {
                lastSeq = _buffer!.Publish(_testBook!);
            }
            return lastSeq;
        }

        /// <summary>
        /// Benchmark: Full path for 1 million messages (snapshot + publish).
        /// </summary>
        [Benchmark(Description = "1M Full Paths")]
        public long FullPathOneMillion()
        {
            long lastSeq = 0;
            for (int i = 0; i < MessageCount; i++)
            {
                var snapshot = ImmutableOrderBook.CreateSnapshot(_mutableBook!, i);
                lastSeq = _buffer!.Publish(snapshot);
            }
            return lastSeq;
        }

        private static OrderBook CreateTestOrderBook()
        {
            var orderBook = new OrderBook("BTCUSD", 2, 10);
            orderBook.ProviderID = 1;
            orderBook.ProviderName = "TestProvider";

            var bids = new BookItem[10];
            var asks = new BookItem[10];

            for (int i = 0; i < 10; i++)
            {
                bids[i] = new BookItem { Price = 100.0 - i * 0.1, Size = 10.0 + i, IsBid = true };
                asks[i] = new BookItem { Price = 100.1 + i * 0.1, Size = 10.0 + i, IsBid = false };
            }

            orderBook.LoadData(asks, bids);
            return orderBook;
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("VisualHFT Multicast Ring Buffer Benchmarks");
            Console.WriteLine("==========================================");
            Console.WriteLine();
            Console.WriteLine("Running latency benchmarks...");
            BenchmarkRunner.Run<MulticastRingBufferBenchmark>();

            Console.WriteLine();
            Console.WriteLine("Running throughput benchmarks...");
            BenchmarkRunner.Run<ThroughputBenchmark>();
        }
    }
}
