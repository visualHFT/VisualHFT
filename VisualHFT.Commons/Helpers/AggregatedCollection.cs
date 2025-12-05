using System.Collections;
using System.Collections.ObjectModel;
using VisualHFT.Enums;


/*
 * The AggregatedCollection<T> class is designed to maintain a running window list of items with a specified maximum capacity.
 * It ensures that as new items are added, the oldest items are removed once the capacity is reached, effectively implementing a Last-In-First-Out (LIFO) collection.
 * Additionally, it supports aggregation of items based on a specified aggregation level (e.g., 10 milliseconds, 20 milliseconds).
 * Each item in the collection represents a bucket corresponding to the chosen aggregation level, aggregating data within the same bucket based on the provided dateSelector and aggregator functions.
 *
 */
namespace VisualHFT.Helpers
{
    public class AggregatedCollection<T> : IDisposable, IEnumerable<T> where T : new()
    {
        private bool _disposed = false; // to track whether the object has been disposed
        private TimeSpan _aggregationSpan;
        private TimeSpan _dynamicAggregationSpan;
        private AggregationLevel _level;
        //private readonly CachedCollection<T> _aggregatedData;
        private readonly List<T> _aggregatedData;

        private readonly Func<T, DateTime> _dateSelector;
        private readonly Action<List<T>, T, int> _aggregator;

        private readonly object _lockObject = new object();
        private int _maxPoints = 0; // Maximum number of points
        private DateTime lastItemDate = DateTime.MinValue;
        private int _ItemsUpdatedCount = 0;

        //AUTOMATED Aggregation
        private const int WINDOW_SIZE = 10; // Number of items to consider for frequency calculation

        public event EventHandler<T> OnRemoving;
        public event EventHandler<int> OnRemoved;
        public event EventHandler<T> OnAdded;

        /// <summary>
        /// Initializes a new instance of the <see cref="AggregatedCollection"/> class.
        /// </summary>
        /// <param name="level">The level.</param>
        /// <param name="maxItems">The max items.</param>
        /// <param name="dateSelector">The date selector.</param>
        /// <param name="onDataAggregationAction">The aggregator.</param>
        public AggregatedCollection(AggregationLevel level, int maxItems, Func<T, DateTime> dateSelector, Action<List<T>, T, int> onDataAggregationAction)
        {
            _aggregatedData = new List<T>(maxItems);

            _maxPoints = maxItems;
            _level = level;
            _aggregationSpan = level.ToTimeSpan();
            _dynamicAggregationSpan = _aggregationSpan; // Initialize with the same value
            _dateSelector = dateSelector;
            _aggregator = onDataAggregationAction;
        }
        ~AggregatedCollection()
        {
            Dispose(false);
        }


        public bool ForceAddSkippingAggregation(T item)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AggregatedCollection<T>));
            }
            bool retValue = false;
            lock (_lockObject)
            {
                _ItemsUpdatedCount = 0; //reset on add new
                _aggregatedData.Add(item);
                OnAdded?.Invoke(this, item);
                if (_aggregatedData.Count() > _maxPoints)
                {
                    var itemToRemove = _aggregatedData[0];
                    // Remove the item from the collection
                    OnRemoving?.Invoke(this, itemToRemove);
                    _aggregatedData.Remove(itemToRemove);
                    // Trigger any remove events or perform additional logic as required
                    OnRemoved?.Invoke(this, 0);
                }
                retValue = true;
            }
            return retValue;
        }
        public bool Add(T item)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AggregatedCollection<T>));
            }

            bool retValue = false;

            lock (_lockObject)
            {
                if (_aggregationSpan == TimeSpan.Zero) // no aggregation
                {
                    _aggregatedData.Add(item);
                    OnAdded?.Invoke(this, item);

                    if (_aggregatedData.Count() > _maxPoints)
                    {
                        // Remove the item from the collection
                        var itemToRemove = _aggregatedData[0];

                        OnRemoving?.Invoke(this, itemToRemove);

                        _aggregatedData.Remove(itemToRemove);

                        // Trigger any remove events or perform additional logic as required
                        OnRemoved?.Invoke(this, 0);
                    }
                    retValue = true;
                }
                else
                {
                    bool _readyToAdd = true;
                    T lastItem = _aggregatedData.LastOrDefault();
                    if (lastItem != null)
                    {
                        _readyToAdd = Math.Abs(_dateSelector(item).Ticks - _dateSelector(lastItem).Ticks) >= _level.ToTimeSpan().Ticks;
                    }

                    // Check the last item in the list
                    if (!_readyToAdd && lastItem != null)
                    {
                        _ItemsUpdatedCount++;
                        _aggregator(_aggregatedData, item, _ItemsUpdatedCount);
                        retValue = false;
                    }
                    else if (_readyToAdd)
                    {
                        _ItemsUpdatedCount = 0; //reset on add new
                        _aggregatedData.Add(item);
                        OnAdded?.Invoke(this, item);

                        if (_aggregatedData.Count() > _maxPoints)
                        {
                            var itemToRemove = _aggregatedData[0];
                            // Remove the item from the collection
                            OnRemoving?.Invoke(this, itemToRemove);

                            _aggregatedData.Remove(itemToRemove);

                            // Trigger any remove events or perform additional logic as required
                            OnRemoved?.Invoke(this, 0);
                        }
                        retValue = true;
                    }
                }
            }
            return retValue;
        }

        public void Clear()
        {
            lock (_lockObject)
                _aggregatedData.Clear();
            OnRemoved?.Invoke(this, -1); //-1 indicates that we are clearing the list
        }
        public int Count()
        {
            lock (_lockObject)
            {
                return _aggregatedData.Count();
            }
        }
        public bool Any()
        {
            lock (_lockObject)
            {
                return _aggregatedData.Count > 0;
            }
        }
        public IEnumerable<T> ToList()
        {
            lock (_lockObject)
            {
                return _aggregatedData.ToList();
            }
        }

        public T this[int index]
        {
            get
            {
                lock (_lockObject)
                {
                    return _aggregatedData[index];
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerator<T> GetEnumerator()
        {
            lock (_lockObject)
            {
                return _aggregatedData.ToList().GetEnumerator(); // Avoid direct enumeration on the locked collection
            }
        }


        public IEnumerable<TResult> Select<TResult>(Func<T, TResult> selector)
        {
            lock (_lockObject)
            {
                return _aggregatedData.Select(selector);
            }
        }
        public IEnumerable<TResult> SelectMany<TResult>(Func<T, IEnumerable<TResult>> selector)
        {
            lock (_lockObject)
            {
                return _aggregatedData.SelectMany(selector);
            }
        }
        public ReadOnlyCollection<T> AsReadOnly()
        {
            lock (_lockObject)
            {
                return _aggregatedData.AsReadOnly();
            }
        }
        public T LastOrDefault()
        {
            lock (_lockObject)
                return _aggregatedData.LastOrDefault();

        }
        public T FirstOrDefault()
        {
            lock (_lockObject)
                return _aggregatedData.FirstOrDefault();
        }
        public IEnumerable<T> Where(Func<T, bool> predicate)
        {
            lock (_lockObject)
                return _aggregatedData.Where(predicate);
        }
        public decimal Min(Func<T, decimal> selector)
        {
            lock (_lockObject)
                return _aggregatedData.DefaultIfEmpty(new T()).Min(selector);
        }
        public double Min(Func<T, double> selector)
        {
            lock (_lockObject)
                return _aggregatedData.DefaultIfEmpty(new T()).Min(selector);
        }
        public decimal Max(Func<T, decimal> selector)
        {
            lock (_lockObject)
                return _aggregatedData.DefaultIfEmpty(new T()).Max(selector);
        }
        public double Max(Func<T, double> selector)
        {
            lock (_lockObject)
                return _aggregatedData.DefaultIfEmpty(new T()).Max(selector);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _aggregatedData?.Clear();
                }
                _disposed = true;
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

    }
}
