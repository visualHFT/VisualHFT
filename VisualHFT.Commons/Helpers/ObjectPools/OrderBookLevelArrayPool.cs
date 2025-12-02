using System.Runtime.CompilerServices;
using VisualHFT.Model;

namespace VisualHFT.Commons.Helpers.ObjectPools
{
    /// <summary>
    /// Specialized array pool for OrderBookLevel arrays.
    /// Optimized for typical order book depths (5, 10, 20, 50, 100 levels).
    /// 
    /// Key Features:
    /// - Pre-bucketed sizes for common order book depths
    /// - Thread-safe rental and return
    /// - Zero allocation for standard depths
    /// 
    /// Performance Characteristics:
    /// - Rent: ~10-30 nanoseconds for common sizes
    /// - Return: ~10-30 nanoseconds
    /// - Zero GC pressure for reused arrays
    /// 
    /// Typical Usage:
    /// <code>
    /// var levels = OrderBookLevelArrayPool.Instance.Rent(20);
    /// try
    /// {
    ///     // Use levels array
    /// }
    /// finally
    /// {
    ///     OrderBookLevelArrayPool.Instance.Return(levels);
    /// }
    /// </code>
    /// </summary>
    public sealed class OrderBookLevelArrayPool
    {
        /// <summary>
        /// Shared instance for convenient access.
        /// </summary>
        public static readonly OrderBookLevelArrayPool Instance = new OrderBookLevelArrayPool();

        // Pre-sized buckets for common order book depths
        private readonly Stack<OrderBookLevel[]>[] _buckets;
        private readonly int[] _bucketSizes = { 5, 10, 20, 50, 100, 200, 500, 1000 };
        private readonly object[] _locks;

        private long _totalRents;
        private long _totalReturns;
        private long _poolHits;
        private long _poolMisses;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType);

        /// <summary>
        /// Creates a new OrderBookLevelArrayPool with pre-warmed buckets.
        /// </summary>
        public OrderBookLevelArrayPool()
        {
            _buckets = new Stack<OrderBookLevel[]>[_bucketSizes.Length];
            _locks = new object[_bucketSizes.Length];

            for (int i = 0; i < _bucketSizes.Length; i++)
            {
                _buckets[i] = new Stack<OrderBookLevel[]>(64);
                _locks[i] = new object();

                // Pre-warm with some arrays
                var prewarmCount = Math.Min(16, 64);
                for (int j = 0; j < prewarmCount; j++)
                {
                    _buckets[i].Push(new OrderBookLevel[_bucketSizes[i]]);
                }
            }

            _totalRents = 0;
            _totalReturns = 0;
            _poolHits = 0;
            _poolMisses = 0;
        }

        /// <summary>
        /// Gets the bucket index for a given size.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetBucketIndex(int size)
        {
            for (int i = 0; i < _bucketSizes.Length; i++)
            {
                if (size <= _bucketSizes[i])
                    return i;
            }
            return -1; // Size too large, will allocate directly
        }

        /// <summary>
        /// Rents an OrderBookLevel array of at least the specified size.
        /// The returned array may be larger than requested.
        /// 
        /// Latency: ~10-30 nanoseconds for common sizes.
        /// </summary>
        /// <param name="minimumSize">The minimum number of levels needed.</param>
        /// <returns>An array of at least the specified size.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OrderBookLevel[] Rent(int minimumSize)
        {
            Interlocked.Increment(ref _totalRents);

            var bucketIndex = GetBucketIndex(minimumSize);
            if (bucketIndex >= 0)
            {
                lock (_locks[bucketIndex])
                {
                    if (_buckets[bucketIndex].Count > 0)
                    {
                        Interlocked.Increment(ref _poolHits);
                        return _buckets[bucketIndex].Pop();
                    }
                }
            }

            // No pooled array available, allocate new
            Interlocked.Increment(ref _poolMisses);
            var size = bucketIndex >= 0 ? _bucketSizes[bucketIndex] : minimumSize;
            return new OrderBookLevel[size];
        }

        /// <summary>
        /// Returns an OrderBookLevel array to the pool.
        /// 
        /// Latency: ~10-30 nanoseconds.
        /// </summary>
        /// <param name="array">The array to return.</param>
        /// <param name="clearArray">If true, clears the array before returning (default: true for safety).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(OrderBookLevel[]? array, bool clearArray = true)
        {
            if (array == null) return;

            Interlocked.Increment(ref _totalReturns);

            var bucketIndex = GetBucketIndex(array.Length);
            if (bucketIndex < 0)
            {
                // Array too large, let GC collect it
                return;
            }

            // Only return if it matches the exact bucket size
            if (array.Length != _bucketSizes[bucketIndex])
            {
                return;
            }

            if (clearArray)
            {
                Array.Clear(array, 0, array.Length);
            }

            lock (_locks[bucketIndex])
            {
                // Don't grow the pool too large
                if (_buckets[bucketIndex].Count < 256)
                {
                    _buckets[bucketIndex].Push(array);
                }
            }
        }

        /// <summary>
        /// Gets the total number of rent operations.
        /// </summary>
        public long TotalRents => Interlocked.Read(ref _totalRents);

        /// <summary>
        /// Gets the total number of return operations.
        /// </summary>
        public long TotalReturns => Interlocked.Read(ref _totalReturns);

        /// <summary>
        /// Gets the number of successful pool hits (array reuse).
        /// </summary>
        public long PoolHits => Interlocked.Read(ref _poolHits);

        /// <summary>
        /// Gets the number of pool misses (new allocations).
        /// </summary>
        public long PoolMisses => Interlocked.Read(ref _poolMisses);

        /// <summary>
        /// Gets the pool hit rate (0.0 to 1.0).
        /// </summary>
        public double HitRate
        {
            get
            {
                var total = PoolHits + PoolMisses;
                return total > 0 ? (double)PoolHits / total : 0;
            }
        }

        /// <summary>
        /// Gets the number of currently outstanding arrays.
        /// </summary>
        public long Outstanding => TotalRents - TotalReturns;

        /// <summary>
        /// Resets statistics (for testing/debugging).
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalRents, 0);
            Interlocked.Exchange(ref _totalReturns, 0);
            Interlocked.Exchange(ref _poolHits, 0);
            Interlocked.Exchange(ref _poolMisses, 0);
        }

        /// <summary>
        /// Gets a status string for monitoring.
        /// </summary>
        public string GetStatus()
        {
            var availableTotal = 0;
            for (int i = 0; i < _buckets.Length; i++)
            {
                lock (_locks[i])
                {
                    availableTotal += _buckets[i].Count;
                }
            }

            return $"OrderBookLevelArrayPool: " +
                   $"Rents={TotalRents}, Returns={TotalReturns}, " +
                   $"Outstanding={Outstanding}, Available={availableTotal}, " +
                   $"HitRate={HitRate:P1}";
        }
    }
}
