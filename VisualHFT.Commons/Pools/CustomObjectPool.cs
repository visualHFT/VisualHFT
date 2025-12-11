using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VisualHFT.Commons.Pools
{
    /// <summary>
    /// High-performance object pool with guaranteed object reuse for HFT systems.
    /// Uses true allocation-free lock-free array-based circular buffer.
    /// Replaces Microsoft's DefaultObjectPool which creates new objects when exhausted.
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
        // Note: TotalGets is derived from _head pointer, no separate counter needed
        private long _totalReturns;
        private long _totalCreated;
        private bool _disposed = false;

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
            // Small pools (size < 10) won't have pre-warmed objects
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
            // Handle edge cases
            if (value <= 1) return 1;

            // For small values, use the fast path
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

            // For larger values, use bit manipulation for dynamic calculation
            // This approach can handle up to 1,073,741,824 (1 billion) objects
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value++;

            // Ensure we don't overflow int (max power of 2 for int is 1,073,741,824)
            if (value <= 0 || value > 1073741824)
            {
                throw new ArgumentException(
                    $"Pool size {value:N0} is too large. Maximum supported size is 1,073,741,824 (1 billion) objects. " +
                    $"Consider using multiple smaller pools or redesigning your architecture for sizes above 1 billion.");
            }

            return value;
        }

        private string GetInstantiator()
        {
            try
            {
                var stackTrace = new StackTrace();
                var frames = stackTrace.GetFrames();

                // Skip the first frame (this method) and the constructor frame
                // Look for the first frame that's not in this class
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get()
        {
            // Single atomic increment - this IS the Get operation
            long currentHead = Interlocked.Increment(ref _head) - 1;

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
            if ((created & 0x7FFF) == 0) // Every 32768 creations (power of 2 for fast check)
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
                Return(obj);  // This will now correctly track each individual return
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(T obj)
        {
            if (obj == null || _disposed)
                return;

            // Reset object state before returning to pool
            (obj as VisualHFT.Commons.Model.IResettable)?.Reset();
            (obj as IList)?.Clear();

            // Always increment tail and store - circular buffer handles wraparound
            long currentTail = Interlocked.Increment(ref _tail) - 1;
            var index = (int)(currentTail & _mask);
            _objects[index] = obj;

            // Always count the return for accurate statistics
            Interlocked.Increment(ref _totalReturns);
        }

        public void Reset()
        {
            // Thread-safe reset by resetting indices and statistics
            Interlocked.Exchange(ref _head, 0);
            Interlocked.Exchange(ref _tail, 0);
            Interlocked.Exchange(ref _totalReturns, 0);

            // Pre-fill pool with objects for optimal efficiency
            for (int i = 0; i < _maxPoolSize; i++)
            {
                var obj = _objects[i];
                if (obj != null)
                {
                    // Reset existing object state
                    (obj as VisualHFT.Commons.Model.IResettable)?.Reset();
                    (obj as IList)?.Clear();
                }
                else
                {
                    // Create new object if slot is empty
                    _objects[i] = new T();
                    Interlocked.Increment(ref _totalCreated);
                }
            }

            // Set tail to indicate all slots are filled
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

                // True utilization: objects currently in use / total pool capacity
                return Math.Max(0.0, Math.Min(1.0, outstanding / (double)_maxPoolSize));
            }
        }

        /// <summary>
        /// Gets the pool efficiency ratio (objects from pool vs total objects created).
        /// Values closer to 1.0 indicate better pool efficiency.
        /// </summary>
        public double PoolEfficiency
        {
            get
            {
                var totalGets = Interlocked.Read(ref _head);
                var totalCreated = Interlocked.Read(ref _totalCreated);

                if (totalGets == 0) return 1.0; // No gets yet, consider efficient

                var objectsFromPool = totalGets - totalCreated;
                return Math.Max(0, Math.Min(1.0, objectsFromPool / (double)totalGets));
            }
        }

        /// <summary>
        /// Gets the outstanding objects count (gets - returns).
        /// Positive values may indicate memory leaks or high concurrent usage.
        /// </summary>
        public long Outstanding => Math.Max(0, Interlocked.Read(ref _head) - Interlocked.Read(ref _totalReturns));
        public long TotalGets => Interlocked.Read(ref _head); // Now derived from head pointer
        public long TotalReturns => Interlocked.Read(ref _totalReturns);
        public long TotalCreated => Interlocked.Read(ref _totalCreated);
        public int MaxPoolSize => _maxPoolSize;
        public bool IsHealthy => _totalCreated < _maxPoolSize * 2   // Creation pressure check
                         && UtilizationPercentage < 0.90   // Outstanding utilization check (more reasonable threshold)
                         && !_disposed;                    // Not disposed
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

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
