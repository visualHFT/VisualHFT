using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using VisualHFT.Commons.Model;

namespace VisualHFT.Commons.Pools
{
    /// <summary>
    /// High-performance object pool with guaranteed object reuse for HFT systems.
    /// Uses true allocation-free lock-free array-based circular buffer.
    /// Replaces Microsoft's DefaultObjectPool which creates new objects when exhausted.
    /// 
    /// TRIMMING BEHAVIOR:
    /// - Automatic background trimming when utilization is low for sustained period
    /// - Timer is lazy-initialized on first Get() call (zero startup overhead)
    /// - Trimming runs on ThreadPool, never blocks hot path
    /// - Configurable thresholds via constants
    /// </summary>
    public class CustomObjectPool<T> : IDisposable where T : class, new()
    {
        private readonly T[] _objects;  // Pre-allocated array for true zero allocation
        private readonly int _maxPoolSize;
        private readonly int _mask; // For fast modulo operation (requires power of 2 size)
        private readonly string _instantiator; // Track who created this pool

        // Lock-free indices - using long for atomic operations
        private long _head;  // Points to next item to take
        private long _tail;  // Points to next position to put item

        // Statistics
        private long _totalReturns;
        private long _totalCreated;
        private volatile bool _disposed = false;

        // Trimming configuration
        private const int TRIM_CHECK_INTERVAL_MS = 5 * 60 * 1000; // 5 minutes
        private const double LOW_UTILIZATION_THRESHOLD = 0.10; // 10%
        private const int CONSECUTIVE_LOW_CHECKS_REQUIRED = 3; // 15 minutes sustained low

        // Adaptive trim state
        private Timer _trimTimer;
        private int _timerInitialized = 0;
        private int _consecutiveLowUtilizationChecks = 0;
        private long _peakObjectsInUse = 0;  // Track peak usage for adaptive trimming
        private long _lastTrimTimestamp = 0; // Prevent rapid trim cycles

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public CustomObjectPool(int maxPoolSize = 100)
        {
            // Ensure power of 2 for fast modulo operation
            var actualSize = GetNextPowerOfTwo(maxPoolSize);
            _maxPoolSize = actualSize;
            _mask = actualSize - 1;
            _objects = new T[actualSize];
            _head = 0;
            _tail = 0;

            // Capture the instantiator information from the stack trace
            _instantiator = GetInstantiator();

            // Pre-warm the pool with initial objects based on original logic
            var prewarmCount = Math.Min(maxPoolSize / 10, 100);
            for (int i = 0; i < prewarmCount; i++)
            {
                var obj = new T();
                _objects[i] = obj;
                Interlocked.Increment(ref _totalCreated);
            }

            // Set tail to indicate how many objects are available
            _tail = prewarmCount;
        }

        private static int GetNextPowerOfTwo(int value)
        {
            if (value <= 1) return 1;

            if (value <= 1024)
            {
                if (value <= 2) return 2;
                if (value <= 4) return 4;
                if (value <= 8) return 8;
                if (value <= 16) return 16;
                if (value <= 32) return 32;
                if (value <= 64) return 64;
                if (value <= 128) return 128;
                if (value <= 256) return 256;
                if (value <= 512) return 512;
                return 1024;
            }

            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value++;

            if (value <= 0 || value > 1073741824)
            {
                throw new ArgumentException(
                    $"Pool size {value:N0} is too large. Maximum supported size is 1,073,741,824 (1 billion) objects.");
            }

            return value;
        }

        private string GetInstantiator()
        {
            try
            {
                var stackTrace = new StackTrace();
                var frames = stackTrace.GetFrames();

                for (int i = 2; i < frames.Length; i++)
                {
                    var method = frames[i].GetMethod();
                    if (method?.DeclaringType != null && method.DeclaringType != typeof(CustomObjectPool<T>))
                    {
                        var declaringType = method.DeclaringType;
                        return $"{declaringType.Namespace}.{declaringType.Name}";
                    }
                }

                return "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // LAZY TIMER INITIALIZATION - Called once on first Get()
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Ensures the trim timer is initialized. Uses lock-free initialization.
        /// Only allocates the timer once, on first actual use of the pool.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureTrimTimerInitialized()
        {
            // Fast path: already initialized
            if (Volatile.Read(ref _timerInitialized) == 1)
                return;

            // Slow path: try to initialize (only one thread wins)
            if (Interlocked.CompareExchange(ref _timerInitialized, 1, 0) == 0)
            {
                // We won the race - create the timer
                // Timer runs on ThreadPool, never blocks calling thread
                _trimTimer = new Timer(
                    TrimTimerCallback,
                    null,
                    TRIM_CHECK_INTERVAL_MS,  // Initial delay (don't check immediately)
                    TRIM_CHECK_INTERVAL_MS); // Subsequent interval
            }
        }

        /// <summary>
        /// Timer callback - runs on ThreadPool thread.
        /// Checks utilization and trims if sustained low usage detected.
        /// </summary>
        private void TrimTimerCallback(object state)
        {
            if (_disposed) return;

            try
            {
                double utilization = UtilizationPercentage;

                if (utilization < LOW_UTILIZATION_THRESHOLD)
                {
                    int checks = Interlocked.Increment(ref _consecutiveLowUtilizationChecks);

                    if (checks >= CONSECUTIVE_LOW_CHECKS_REQUIRED)
                    {
                        TrimExcessInternal();
                        Interlocked.Exchange(ref _consecutiveLowUtilizationChecks, 0);
                    }
                }
                else
                {
                    // Utilization increased - reset counter
                    Interlocked.Exchange(ref _consecutiveLowUtilizationChecks, 0);
                }
            }
            catch (Exception ex)
            {
                // Never let timer callback crash
                log.Warn($"CustomObjectPool<{typeof(T).Name}> trim check failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // HOT PATH - MINIMAL OVERHEAD (single volatile read for timer check)
        // ═══════════════════════════════════════════════════════════════════

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get()
        {
            // Lazy timer initialization - single volatile read on hot path
            // After first call, this becomes a single memory read (~1ns)
            EnsureTrimTimerInitialized();

            // Single atomic increment - this IS the Get operation
            long currentHead = Interlocked.Increment(ref _head) - 1;

            // Track peak usage for adaptive trimming (cheap - just a compare)
            long currentPeak = Volatile.Read(ref _peakObjectsInUse);
            if (currentHead > currentPeak)
            {
                Interlocked.CompareExchange(ref _peakObjectsInUse, currentHead, currentPeak);
            }

            // Get object from array using fast modulo (bitwise AND with mask)
            var index = (int)(currentHead & _mask);

            // ALWAYS try to get from array first - handles post-exhaustion recovery
            var item = Interlocked.Exchange(ref _objects[index], null);

            // If we got an object, return it
            if (item != null)
            {
                return item;
            }

            // Slot was empty - create new object
            long created = Interlocked.Increment(ref _totalCreated);

            // Log sparingly to avoid overhead
            if ((created & 0x7FFF) == 0)
            {
                var typeName = typeof(T).Name;
                string message = $"CustomObjectPool<{typeName}> exhausted - created {created} total objects. Consider increasing pool size. Instantiated by: {_instantiator}";
                log.Warn(message);
            }

            return new T();
        }

        public void Return(IEnumerable<T> listObjs)
        {
            if (listObjs == null) return;

            foreach (var obj in listObjs)
            {
                Return(obj);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(T obj)
        {
            if (obj == null || _disposed) return;

            (obj as IResettable)?.Reset();
            (obj as IList)?.Clear();

            // Only store if slot is empty - prevents overwrites
            long currentTail = Interlocked.Increment(ref _tail) - 1;
            var index = (int)(currentTail & _mask);

            // Compare-exchange: only store if slot was null
            var existing = Interlocked.CompareExchange(ref _objects[index], obj, null);
            if (existing != null)
            {
                // Slot was occupied - object couldn't be returned
                (obj as IDisposable)?.Dispose();
            }

            Interlocked.Increment(ref _totalReturns);
        }

        // ═══════════════════════════════════════════════════════════════════
        // TRIMMING IMPLEMENTATION - SAFE FOR CIRCULAR BUFFER
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Internal trim implementation with ADAPTIVE threshold.
        /// Only trims down to peak observed usage (with buffer), not to a fixed percentage.
        /// Prevents trim-expand oscillation by respecting actual workload demands.
        /// </summary>
        private void TrimExcessInternal()
        {
            if (_disposed) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            long lastTrim = Volatile.Read(ref _lastTrimTimestamp);

            // Cooldown: Don't trim if we trimmed recently and then had to expand
            // This detects the oscillation pattern and backs off
            const long MIN_TRIM_INTERVAL_TICKS = TimeSpan.TicksPerMinute * 30; // 30 minutes minimum between trims
            if (nowTicks - lastTrim < MIN_TRIM_INTERVAL_TICKS)
            {
                // Check if we've been creating objects since last trim (indicates we trimmed too aggressively)
                long currentHead = Interlocked.Read(ref _head);
                long peakUsage = Volatile.Read(ref _peakObjectsInUse);

                if (currentHead > peakUsage)
                {
                    // Workload is growing - update peak and skip this trim
                    Interlocked.Exchange(ref _peakObjectsInUse, currentHead);
                    log.Debug($"CustomObjectPool<{typeof(T).Name}> skipping trim - workload growing. Peak updated to {currentHead}. Instantiated by: {_instantiator}");
                    return;
                }
            }

            // Count actual non-null objects in the array
            int actualObjectCount = 0;
            for (int i = 0; i < _maxPoolSize; i++)
            {
                if (Volatile.Read(ref _objects[i]) != null)
                    actualObjectCount++;
            }

            // ADAPTIVE TARGET: Keep at least the peak observed usage + 20% buffer
            // This prevents trimming below what the workload actually needs
            long peakObjects = Volatile.Read(ref _peakObjectsInUse);
            int adaptiveMinimum = (int)(peakObjects * 0.5); // Keep 50% of peak usage
            int fixedMinimum = Math.Max(10, (int)(_maxPoolSize * 0.10)); // Original 10% minimum
            int targetCount = Math.Max(adaptiveMinimum, fixedMinimum);

            if (actualObjectCount <= targetCount)
            {
                log.Debug($"CustomObjectPool<{typeof(T).Name}> no trim needed. Current: {actualObjectCount}, Target minimum: {targetCount}. Instantiated by: {_instantiator}");
                return;
            }

            int objectsToRemove = Math.Min(actualObjectCount - targetCount, _maxPoolSize / 4); // Cap at 25% removal (was 50%)

            if (objectsToRemove < 100) // Don't bother trimming tiny amounts
                return;

            int removed = 0;

            log.Info($"CustomObjectPool<{typeof(T).Name}> trimming up to {objectsToRemove} excess objects (current: {actualObjectCount}, adaptive target: {targetCount}). Instantiated by: {_instantiator}");

            for (int i = 0; i < _maxPoolSize && removed < objectsToRemove; i++)
            {
                var item = Interlocked.Exchange(ref _objects[i], null);
                if (item != null)
                {
                    (item as IDisposable)?.Dispose();
                    removed++;
                }
            }

            // Update trim timestamp
            Interlocked.Exchange(ref _lastTrimTimestamp, nowTicks);

            log.Info($"CustomObjectPool<{typeof(T).Name}> trimmed {removed} objects. Instantiated by: {_instantiator}");

            // Suggest GC for trimmed objects (non-blocking)
            GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
        }

        /// <summary>
        /// Manually triggers a trim operation. Safe to call anytime.
        /// Use during known low-activity periods (e.g., market close).
        /// </summary>
        public void TrimExcess()
        {
            TrimExcessInternal();
        }

        // ═══════════════════════════════════════════════════════════════════
        // DIAGNOSTIC PROPERTIES & RESET
        // ═══════════════════════════════════════════════════════════════════

        public void Reset()
        {
            Interlocked.Exchange(ref _head, 0);
            Interlocked.Exchange(ref _tail, 0);
            Interlocked.Exchange(ref _totalReturns, 0);
            Interlocked.Exchange(ref _consecutiveLowUtilizationChecks, 0);

            for (int i = 0; i < _maxPoolSize; i++)
            {
                var obj = _objects[i];
                if (obj != null)
                {
                    (obj as IResettable)?.Reset();
                    (obj as IList)?.Clear();
                }
                else
                {
                    _objects[i] = new T();
                    Interlocked.Increment(ref _totalCreated);
                }
            }

            Interlocked.Exchange(ref _tail, _maxPoolSize);
        }

        public int AvailableObjects
        {
            get
            {
                var tail = Interlocked.Read(ref _tail);
                var head = Interlocked.Read(ref _head);
                return Math.Max(0, (int)(tail - head));
            }
        }

        public double UtilizationPercentage
        {
            get
            {
                if (_maxPoolSize == 0) return 0;

                var totalGets = Interlocked.Read(ref _head);
                var totalReturns = Interlocked.Read(ref _totalReturns);
                var outstanding = Math.Max(0, totalGets - totalReturns);

                return Math.Max(0.0, Math.Min(1.0, outstanding / (double)_maxPoolSize));
            }
        }

        public double PoolEfficiency
        {
            get
            {
                var totalGets = Interlocked.Read(ref _head);
                var totalCreated = Interlocked.Read(ref _totalCreated);

                if (totalGets == 0) return 1.0;

                var objectsFromPool = totalGets - totalCreated;
                return Math.Max(0, Math.Min(1.0, objectsFromPool / (double)totalGets));
            }
        }

        public long Outstanding => Math.Max(0, Interlocked.Read(ref _head) - Interlocked.Read(ref _totalReturns));
        public long TotalGets => Interlocked.Read(ref _head);
        public long TotalReturns => Interlocked.Read(ref _totalReturns);
        public long TotalCreated => Interlocked.Read(ref _totalCreated);
        public int MaxPoolSize => _maxPoolSize;

        public bool IsHealthy => _totalCreated < _maxPoolSize * 2
                                 && UtilizationPercentage < 0.90
                                 && !_disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Dispose timer first
            try
            {
                _trimTimer?.Dispose();
            }
            catch { /* Ignore timer disposal errors */ }

            // Dispose all objects in the pool
            for (int i = 0; i < _maxPoolSize; i++)
            {
                var obj = _objects[i];
                (obj as IDisposable)?.Dispose();
                _objects[i] = null;
            }
        }
    }
}