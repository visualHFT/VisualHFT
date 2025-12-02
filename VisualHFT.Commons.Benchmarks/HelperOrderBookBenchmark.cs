using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using VisualHFT.Commons.Messaging;
using VisualHFT.Model;

namespace VisualHFT.Benchmarks
{
    /// <summary>
    /// Comparison benchmark: Old (Legacy) vs New (Ring Buffer) implementation.
    /// This class compares the synchronous lock-based dispatch with the new lock-free ring buffer.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
    public class LegacyVsNewComparisonBenchmark
    {
        private OrderBook? _testBook;
        
        // Legacy approach simulation
        private List<Action<OrderBook>> _legacySubscribers = new List<Action<OrderBook>>();
        private readonly object _legacyLock = new object();
        
        // New ring buffer approach
        private MulticastRingBuffer<ImmutableOrderBook>? _buffer;
        private ConsumerCursor? _cursor;
        private ImmutableOrderBook? _immutableBook;

        private int _receivedCount;

        [GlobalSetup]
        public void Setup()
        {
            _testBook = CreateTestOrderBook();
            _immutableBook = ImmutableOrderBook.CreateSnapshot(_testBook, 0);
            
            // Setup legacy subscriber
            _legacySubscribers.Add(book => { _receivedCount++; });
            
            // Setup new ring buffer
            _buffer = new MulticastRingBuffer<ImmutableOrderBook>(65536);
            _cursor = _buffer.Subscribe("BenchmarkConsumer");

            // Pre-warm
            for (int i = 0; i < 1000; i++)
            {
                LegacyDispatch(_testBook);
                _buffer.Publish(_immutableBook);
                _buffer.TryRead(_cursor, out _, out _);
            }
            _receivedCount = 0;
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _buffer?.Dispose();
            _testBook?.Dispose();
        }

        /// <summary>
        /// Legacy dispatch: Lock + foreach + synchronous callback.
        /// This is how the OLD HelperOrderBook worked.
        /// </summary>
        private void LegacyDispatch(OrderBook book)
        {
            lock (_legacyLock)
            {
                foreach (var subscriber in _legacySubscribers)
                {
                    subscriber(book);
                }
            }
        }

        /// <summary>
        /// OLD WAY: Synchronous dispatch with lock.
        /// Blocks while iterating subscribers.
        /// </summary>
        [Benchmark(Description = "OLD: Lock + Dispatch (1 subscriber)")]
        public void OldWay_SingleSubscriber()
        {
            LegacyDispatch(_testBook!);
        }

        /// <summary>
        /// NEW WAY: Lock-free ring buffer publish.
        /// Never blocks, O(1) operation.
        /// </summary>
        [Benchmark(Description = "NEW: Ring Buffer Publish")]
        public long NewWay_RingBufferPublish()
        {
            return _buffer!.Publish(_immutableBook!);
        }

        /// <summary>
        /// NEW WAY: Full roundtrip (publish + read).
        /// Still lock-free.
        /// </summary>
        [Benchmark(Description = "NEW: Publish + Read")]
        public bool NewWay_PublishAndRead()
        {
            _buffer!.Publish(_immutableBook!);
            return _buffer.TryRead(_cursor!, out _, out _);
        }

        /// <summary>
        /// NEW WAY: Full path including snapshot creation.
        /// Represents real-world producer cost.
        /// </summary>
        [Benchmark(Description = "NEW: CreateSnapshot + Publish")]
        public long NewWay_FullPath()
        {
            var snapshot = ImmutableOrderBook.CreateSnapshot(_testBook!, 1);
            return _buffer!.Publish(snapshot);
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
    /// Throughput comparison: Old vs New for 1 million messages.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
    public class ThroughputComparisonBenchmark
    {
        private OrderBook? _testBook;
        
        // Legacy approach
        private List<Action<OrderBook>> _legacySubscribers = new List<Action<OrderBook>>();
        private readonly object _legacyLock = new object();
        
        // New ring buffer
        private MulticastRingBuffer<ImmutableOrderBook>? _buffer;
        private ImmutableOrderBook? _immutableBook;

        private int _receivedCount;
        private const int MessageCount = 1_000_000;

        [GlobalSetup]
        public void Setup()
        {
            _testBook = CreateTestOrderBook();
            _immutableBook = ImmutableOrderBook.CreateSnapshot(_testBook, 0);
            
            // Setup legacy subscriber
            _legacySubscribers.Add(book => { _receivedCount++; });
            
            // Setup new ring buffer
            _buffer = new MulticastRingBuffer<ImmutableOrderBook>(1048576);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _buffer?.Dispose();
            _testBook?.Dispose();
        }

        private void LegacyDispatch(OrderBook book)
        {
            lock (_legacyLock)
            {
                foreach (var subscriber in _legacySubscribers)
                {
                    subscriber(book);
                }
            }
        }

        /// <summary>
        /// OLD WAY: 1 million synchronous dispatches with lock.
        /// </summary>
        [Benchmark(Description = "OLD: 1M Lock+Dispatch")]
        public int OldWay_OneMillionDispatches()
        {
            _receivedCount = 0;
            for (int i = 0; i < MessageCount; i++)
            {
                LegacyDispatch(_testBook!);
            }
            return _receivedCount;
        }

        /// <summary>
        /// NEW WAY: 1 million lock-free publishes.
        /// </summary>
        [Benchmark(Description = "NEW: 1M Ring Buffer Publishes")]
        public long NewWay_OneMillionPublishes()
        {
            long lastSeq = 0;
            for (int i = 0; i < MessageCount; i++)
            {
                lastSeq = _buffer!.Publish(_immutableBook!);
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
            
            Console.WriteLine("=== OLD vs NEW Comparison ===");
            Console.WriteLine("Running comparison benchmarks (Legacy Lock-based vs New Ring Buffer)...");
            BenchmarkRunner.Run<LegacyVsNewComparisonBenchmark>();

            Console.WriteLine();
            Console.WriteLine("=== Throughput Comparison: 1M Messages ===");
            BenchmarkRunner.Run<ThroughputComparisonBenchmark>();
            
            Console.WriteLine();
            Console.WriteLine("=== Detailed New Implementation Benchmarks ===");
            Console.WriteLine("Running latency benchmarks...");
            BenchmarkRunner.Run<MulticastRingBufferBenchmark>();

            Console.WriteLine();
            Console.WriteLine("Running throughput benchmarks...");
            BenchmarkRunner.Run<ThroughputBenchmark>();
        }
    }
}
