using System.Buffers;
using System.Runtime.CompilerServices;

namespace VisualHFT.Commons.Helpers.ObjectPools
{
    /// <summary>
    /// High-performance generic array pool using .NET's ArrayPool.
    /// Provides zero-allocation array rentals for high-frequency scenarios.
    /// 
    /// Key Features:
    /// - Uses .NET's optimized ArrayPool.Shared
    /// - Thread-safe array rental and return
    /// - Automatic array clearing on return (optional)
    /// - Usage statistics for monitoring
    /// 
    /// Performance Characteristics:
    /// - Rent: ~10-50 nanoseconds
    /// - Return: ~10-50 nanoseconds
    /// - Zero GC pressure for reused arrays
    /// 
    /// Usage Pattern:
    /// <code>
    /// // Rent an array
    /// var pool = GenericArrayPool&lt;int&gt;.Instance;
    /// var array = pool.Rent(100);
    /// 
    /// try
    /// {
    ///     // Use the array (may be larger than requested)
    ///     for (int i = 0; i &lt; 100; i++)
    ///         array[i] = i;
    /// }
    /// finally
    /// {
    ///     // Always return the array
    ///     pool.Return(array);
    /// }
    /// </code>
    /// </summary>
    /// <typeparam name="T">The type of array elements.</typeparam>
    public sealed class GenericArrayPool<T>
    {
        /// <summary>
        /// Shared instance for convenient access.
        /// </summary>
        public static readonly GenericArrayPool<T> Instance = new GenericArrayPool<T>();

        private readonly ArrayPool<T> _pool;
        private long _totalRents;
        private long _totalReturns;
        private long _totalBytesRented;

        /// <summary>
        /// Creates a new GenericArrayPool using the shared ArrayPool.
        /// </summary>
        public GenericArrayPool()
        {
            _pool = ArrayPool<T>.Shared;
            _totalRents = 0;
            _totalReturns = 0;
            _totalBytesRented = 0;
        }

        /// <summary>
        /// Creates a new GenericArrayPool with a custom maximum array length.
        /// </summary>
        /// <param name="maxArrayLength">Maximum array length to pool.</param>
        public GenericArrayPool(int maxArrayLength)
        {
            _pool = ArrayPool<T>.Create(maxArrayLength: maxArrayLength, maxArraysPerBucket: 50);
            _totalRents = 0;
            _totalReturns = 0;
            _totalBytesRented = 0;
        }

        /// <summary>
        /// Rents an array of at least the specified minimum length.
        /// The returned array may be larger than requested.
        /// 
        /// Latency: ~10-50 nanoseconds.
        /// </summary>
        /// <param name="minimumLength">The minimum length of the array.</param>
        /// <returns>An array of at least the specified length.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref _totalRents);

            var array = _pool.Rent(minimumLength);

            var elementSize = Unsafe.SizeOf<T>();
            Interlocked.Add(ref _totalBytesRented, array.Length * elementSize);

            return array;
        }

        /// <summary>
        /// Returns an array to the pool.
        /// 
        /// Latency: ~10-50 nanoseconds.
        /// </summary>
        /// <param name="array">The array to return.</param>
        /// <param name="clearArray">If true, clears the array before returning (default: false for performance).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(T[] array, bool clearArray = false)
        {
            if (array == null) return;

            Interlocked.Increment(ref _totalReturns);
            _pool.Return(array, clearArray);
        }

        /// <summary>
        /// Returns an array to the pool and sets the reference to null.
        /// This is a convenience method to prevent accidental reuse after return.
        /// </summary>
        /// <param name="array">Reference to the array to return.</param>
        /// <param name="clearArray">If true, clears the array before returning.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReturnAndNull(ref T[]? array, bool clearArray = false)
        {
            if (array == null) return;

            Interlocked.Increment(ref _totalReturns);
            _pool.Return(array, clearArray);
            array = null;
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
        /// Gets the total bytes rented (cumulative).
        /// </summary>
        public long TotalBytesRented => Interlocked.Read(ref _totalBytesRented);

        /// <summary>
        /// Gets the number of currently outstanding arrays (rented but not returned).
        /// </summary>
        public long Outstanding => TotalRents - TotalReturns;

        /// <summary>
        /// Resets statistics (for testing/debugging).
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalRents, 0);
            Interlocked.Exchange(ref _totalReturns, 0);
            Interlocked.Exchange(ref _totalBytesRented, 0);
        }

        /// <summary>
        /// Gets a status string for monitoring.
        /// </summary>
        public string GetStatus()
        {
            return $"GenericArrayPool<{typeof(T).Name}>: " +
                   $"Rents={TotalRents}, Returns={TotalReturns}, " +
                   $"Outstanding={Outstanding}, TotalBytes={TotalBytesRented:N0}";
        }
    }
}
