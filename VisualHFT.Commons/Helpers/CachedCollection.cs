using System.Collections;
using System.Runtime.CompilerServices;

namespace VisualHFT.Helpers
{
    public class CachedCollection<T> : IDisposable, IEnumerable<T> where T : class, new()
    {
        private readonly object _lock = new object();
        private List<T> _internalList;
        private List<T> _cachedReadOnlyCollection;
        private CachedCollection<T> _takeList;
        private Comparison<T> _comparison;

        public CachedCollection(IEnumerable<T> initialData = null)
        {
            _internalList = initialData?.ToList() ?? new List<T>();
        }
        public CachedCollection(Comparison<T> comparison = null, int listSize = 0)
        {
            if (listSize > 0)
                _internalList = new List<T>(listSize);
            else
                _internalList = new List<T>();
            _comparison = comparison;
        }

        public CachedCollection(IEnumerable<T> initialData = null, Comparison<T> comparison = null)
        {
            _internalList = initialData?.ToList() ?? new List<T>();
            _comparison = comparison;
            if (_comparison != null)
            {
                _internalList.Sort(_comparison);
            }
        }

        public void Update(IEnumerable<T> newData)
        {
            lock (_lock)
            {
                _internalList = new List<T>(newData);
                Sort();
                _cachedReadOnlyCollection = null; // Invalidate the cache
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _internalList.Clear();
                _cachedReadOnlyCollection = null; // Invalidate the cache
            }
        }
        public int Count()
        {
            lock (_lock)
            {
                if (_cachedReadOnlyCollection != null)
                    return _cachedReadOnlyCollection.Count;
                else
                    return _internalList.Count;
            }
        }
        public void Add(T item)
        {
            lock (_lock)
            {
                _internalList.Add(item);
                Sort();
                _cachedReadOnlyCollection = null; // Invalidate the cache
            }
        }
        /// <summary>
        /// Adds an item without triggering a sort. Call Sort() after bulk adds.
        /// </summary>
        public void AddUnsorted(T item)
        {
            lock (_lock)
            {
                _internalList.Add(item);
                _cachedReadOnlyCollection = null;
            }
        }
        public bool Remove(T item)
        {
            lock (_lock)
            {
                var result = _internalList.Remove(item);
                if (result)
                {
                    _cachedReadOnlyCollection = null; // Invalidate the cache
                }
                return result;
            }
        }
        public bool RemoveAll(Predicate<T> predicate)
        {
            return Remove(predicate);
        }
        public bool Remove(Predicate<T> predicate)
        {
            lock (_lock)
            {
                bool removed = false;
                for (int i = _internalList.Count - 1; i >= 0; i--)
                {
                    if (predicate(_internalList[i]))
                    {
                        var item = _internalList[i];
                        _internalList.RemoveAt(i);
                        removed = true;
                    }
                }
                if (removed)
                {
                    _cachedReadOnlyCollection = null; // Invalidate the cache
                }
                return removed;
            }
        }

        public T FirstOrDefault()
        {
            lock (_lock)
            {
                var source = _cachedReadOnlyCollection ?? _internalList;
                return source.Count > 0 ? source[0] : default;
            }
        }
        public T FirstOrDefault(Func<T, bool> predicate)
        {
            lock (_lock)
            {
                var source = _cachedReadOnlyCollection ?? _internalList;
                for (int i = 0; i < source.Count; i++)
                {
                    if (predicate(source[i]))
                        return source[i];
                }
                return default;
            }
        }
        public CachedCollection<T> Take(int count)
        {
            lock (_lock)
            {
                if (count <= 0)
                {
                    return null;
                }

                if (_takeList == null)
                    _takeList = new CachedCollection<T>(_comparison);

                _takeList.Clear();
                if (_cachedReadOnlyCollection != null)
                {
                    for (int i = 0; i < Math.Min(count, _cachedReadOnlyCollection.Count); i++)
                    {
                        _takeList.AddUnsorted(_cachedReadOnlyCollection[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < Math.Min(count, _internalList.Count); i++)
                    {
                        _takeList.AddUnsorted(_internalList[i]);
                    }
                }

                return _takeList;
            }
        }
        public IEnumerator<T> GetEnumerator()
        {
            lock (_lock)
            {
                if (_cachedReadOnlyCollection != null)
                    return _cachedReadOnlyCollection.GetEnumerator();
                else
                    return _internalList.GetEnumerator(); // Create a copy to ensure thread safety during enumeration
            }
        }

        public List<T> ToList()
        {
            lock (_lock)
            {
                if (_cachedReadOnlyCollection != null)
                    return _cachedReadOnlyCollection.ToList();
                else
                    return _internalList.ToList();
            }
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }


        private class TakeEnumerable : IEnumerable<T>
        {
            private readonly List<T> _source;
            private readonly int _count;

            public TakeEnumerable(List<T> source, int count)
            {
                _source = source;
                _count = count;
            }

            public IEnumerator<T> GetEnumerator()
            {
                for (int i = 0; i < _count && i < _source.Count; i++)
                {
                    yield return _source[i];
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
        public override bool Equals(object? obj)
        {
            if (obj == null)
                return false;
            var coll = (obj as CachedCollection<T>);
            if (coll == null)
                return false;

            for (int i = 0; i < coll.Count(); i++)
            {
                if (!_internalList[i].Equals(coll[i]))
                    return false;
            }


            return true;
        }

        public T this[int index]
        {
            get
            {
                lock (_lock)
                {
                    if (_cachedReadOnlyCollection != null)
                    {
                        return _cachedReadOnlyCollection[index];
                    }
                    else
                    {
                        return _internalList[index];
                    }
                }
            }

        }
        public bool Update(Func<T, bool> predicate, Action<T> actionUpdate)
        {
            lock (_lock)
            {
                T itemFound = default;
                for (int i = 0; i < _internalList.Count; i++)
                {
                    if (predicate(_internalList[i]))
                    {
                        itemFound = _internalList[i];
                        break;
                    }
                }
                if (itemFound != null)
                {
                    //execute actionUpdate
                    actionUpdate(itemFound);
                    Sort();
                    InvalidateCache();
                    return true;
                }

                return false;
            }
        }
        public long IndexOf(T element)
        {
            lock (_lock)
            {
                if (_cachedReadOnlyCollection != null)
                    return _cachedReadOnlyCollection.IndexOf(element);
                else
                    return _internalList.IndexOf(element);
            }
        }
        public void InvalidateCache()
        {
            _cachedReadOnlyCollection = null; // Invalidate the cache
        }
        public void Sort()
        {
            lock (_lock)
            {
                if (_comparison != null)
                {
                    _internalList.Sort(_comparison);
                    InvalidateCache();
                }
            }
        }
        public void Dispose()
        {
            _internalList.Clear();
            _cachedReadOnlyCollection?.Clear();
            _takeList.Dispose();
        }

        public void TruncateItemsAfterPosition(int v)
        {
            lock (_lock)
            {
                // If v is negative, clear the entire list.
                if (v < 0)
                {
                    Clear();
                    return;
                }

                // If v is within the bounds of the list, remove items after position v.
                if (v < _internalList.Count - 1)
                {
                    _internalList.RemoveRange(v + 1, _internalList.Count - (v + 1));
                    InvalidateCache();
                }
                // If v is greater than or equal to the last index, nothing to truncate.
            }
        }


        /// <summary>
        /// Returns a read-only span view of the internal list WITHOUT allocating.
        /// Uses CollectionsMarshal for zero-copy access to List's internal array.
        /// WARNING: Span is only valid while the lock is held!
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T> AsSpan(int maxCount = int.MaxValue)
        {
            // ⚠️ CALLER MUST HOLD _lock!
            // This method does NOT lock internally to avoid nested locks

            if (_internalList == null || _internalList.Count == 0)
                return ReadOnlySpan<T>.Empty;

            // Use CollectionsMarshal to access List's internal array (ZERO COPY!)
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_internalList);

            int count = Math.Min(span.Length, maxCount);
            return span.Slice(0, count);
        }
    }
}
