using log4net.Core;
using System.Runtime.CompilerServices;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.Pools;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Studies;

namespace VisualHFT.Model
{
    public partial class OrderBook : ICloneable, IResettable, IDisposable
    {
        private bool _disposed = false; // to track whether the object has been disposed
        private OrderFlowAnalysis lobMetrics = new OrderFlowAnalysis();

        protected OrderBookData _data;
        protected static readonly log4net.ILog log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        protected static readonly CustomObjectPool<DeltaBookItem> DeltaBookItemPool =
            new CustomObjectPool<DeltaBookItem>(1_000);

        // Add counters for level changes
        // _addedLevels: New price levels + size increases at existing levels
        // _deletedLevels: Removed price levels + size decreases at existing levels
        // _updatedLevels: Currently unused (reserved for future use)
        private long _addedLevels = 0;
        private long _deletedLevels = 0;
        private long _updatedLevels = 0;
        private ulong _addedVolumeScaled = 0;
        private ulong _deletedVolumeScaled = 0;
        private ulong _updatedVolumeScaled = 0;

        // Scale cache (10^SizeDecimalPlaces)
        private int _volumeScaleDp;
        private ulong _volumeScale;

        // Properties to expose counters
        public OrderBook()
        {
            _data = new OrderBookData();
            _volumeScaleDp = _data.SizeDecimalPlaces;
            _volumeScale = ComputeScale(_volumeScaleDp);
            FilterBidAskByMaxDepth = true;
        }

        public OrderBook(string symbol, int priceDecimalPlaces, int maxDepth)
        {
            if (maxDepth <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxDepth), "maxDepth must be greater than zero.");
            _data = new OrderBookData(symbol, priceDecimalPlaces, maxDepth);
            _volumeScaleDp = _data.SizeDecimalPlaces;
            _volumeScale = ComputeScale(_volumeScaleDp);
            FilterBidAskByMaxDepth = true;
        }

        ~OrderBook()
        {
            Dispose(false);
        }

        public string Symbol
        {
            get => _data.Symbol;
            set => _data.Symbol = value;
        }

        public int MaxDepth
        {
            get => _data.MaxDepth;
            set => _data.MaxDepth = value;
        }

        public int PriceDecimalPlaces
        {
            get => _data.PriceDecimalPlaces;
            set => _data.PriceDecimalPlaces = value;
        }

        public int SizeDecimalPlaces
        {
            get => _data.SizeDecimalPlaces;
            set
            {
                _data.SizeDecimalPlaces = value;
                _volumeScaleDp = _data.SizeDecimalPlaces;
                _volumeScale = ComputeScale(_volumeScaleDp);
            }
        }

        public double SymbolMultiplier => _data.SymbolMultiplier;

        public int ProviderID
        {
            get => _data.ProviderID;
            set => _data.ProviderID = value;
        }

        public string ProviderName
        {
            get => _data.ProviderName;
            set => _data.ProviderName = value;
        }

        public eSESSIONSTATUS ProviderStatus
        {
            get => _data.ProviderStatus;
            set => _data.ProviderStatus = value;
        }

        public double MaximumCummulativeSize
        {
            get => _data.MaximumCummulativeSize;
            set => _data.MaximumCummulativeSize = value;
        }

        public CachedCollection<BookItem> Asks
        {
            get
            {
                lock (_data.Lock)
                {
                    if (_data.Asks == null)
                        return null;
                    if (MaxDepth > 0 && FilterBidAskByMaxDepth)
                        return _data.Asks.Take(MaxDepth);
                    else
                        return _data.Asks;
                }
            }
            set => _data.Asks.Update(value); //do not remove setter: it is used to auto parse json
        }

        public CachedCollection<BookItem> Bids
        {
            get
            {
                lock (_data.Lock)
                {
                    if (_data.Bids == null)
                        return null;
                    if (MaxDepth > 0 && FilterBidAskByMaxDepth)
                        return _data.Bids.Take(MaxDepth);
                    else
                        return _data.Bids;
                }
            }
            set => _data.Bids.Update(value); //do not remove setter: it is used to auto parse json
        }

        /// <summary>
        /// Creates a thread-safe snapshot of bids/asks for validation.
        /// </summary>
        public BookItem[] GetBidsSnapshot()
        {
            lock (_data.Lock)
            {
                if (_data.Bids == null)
                    return Array.Empty<BookItem>();

                if (MaxDepth > 0 && FilterBidAskByMaxDepth)
                    return _data.Bids.Take(MaxDepth).ToArray();
                else
                    return _data.Bids.ToArray();
            }
        }
        public BookItem[] GetAsksSnapshot()
        {
            lock (_data.Lock)
            {
                if (_data.Asks == null)
                    return Array.Empty<BookItem>();

                if (MaxDepth > 0 && FilterBidAskByMaxDepth)
                    return _data.Asks.Take(MaxDepth).ToArray();
                else
                    return _data.Asks.ToArray();
            }
        }

        public BookItem GetTOB(bool isBid)
        {
            lock (_data.Lock)
            {
                return _data.GetTOB(isBid);
            }
        }

        public double MidPrice
        {
            get
            {
                return _data.MidPrice;
            }
        }
        public double Spread
        {
            get
            {
                return _data.Spread;
            }
        }
        public bool FilterBidAskByMaxDepth
        {
            get
            {
                return _data.FilterBidAskByMaxDepth;
            }
            set
            {
                _data.FilterBidAskByMaxDepth = value;
            }
        }
        public void GetAddDeleteUpdate(ref CachedCollection<BookItem> inputExisting, bool matchAgainsBids)
        {
            if (inputExisting == null)
                return;
            lock (_data.Lock)
            {
                IEnumerable<BookItem> listToMatch = (matchAgainsBids ? _data.Bids : _data.Asks);
                if (listToMatch.Count() == 0)
                    return;

                if (inputExisting.Count() == 0)
                {
                    foreach (var item in listToMatch)
                    {
                        inputExisting.Add(item);
                    }

                    return;
                }

                IEnumerable<BookItem> inputNew = listToMatch;
                List<BookItem> outAdds;
                List<BookItem> outUpdates;
                List<BookItem> outRemoves;

                var existingSet = inputExisting;
                var newSet = inputNew;

                outRemoves = inputExisting.Where(e => !newSet.Contains(e)).ToList();
                outUpdates = inputNew.Where(e =>
                    existingSet.Contains(e) && e.Size != existingSet.FirstOrDefault(i => i.Equals(e)).Size).ToList();
                outAdds = inputNew.Where(e => !existingSet.Contains(e)).ToList();

                foreach (var b in outRemoves)
                    inputExisting.Remove(b);
                foreach (var b in outUpdates)
                {
                    var itemToUpd = inputExisting.Where(x => x.Price == b.Price).FirstOrDefault();
                    if (itemToUpd != null)
                    {
                        itemToUpd.Size = b.Size;
                        itemToUpd.ActiveSize = b.ActiveSize;
                        itemToUpd.CummulativeSize = b.CummulativeSize;
                        itemToUpd.LocalTimeStamp = b.LocalTimeStamp;
                        itemToUpd.ServerTimeStamp = b.ServerTimeStamp;
                    }
                }

                foreach (var b in outAdds)
                    inputExisting.Add(b);
            }
        }

        public void CalculateMetrics()
        {
            lock (_data.Lock)
            {
                lobMetrics.LoadData(_data.Asks, _data.Bids, MaxDepth);
            }
            _data.ImbalanceValue = lobMetrics.Calculate_OrderImbalance();
        }
        public bool LoadData(IEnumerable<BookItem> asks, IEnumerable<BookItem> bids)
        {
            bool ret = true;
            lock (_data.Lock)
            {
                #region Bids
                if (bids != null)
                {
                    _data.Bids.Update(bids
                        .Where(x => x != null && x.Price.HasValue && x.Size.HasValue)
                        .OrderByDescending(x => x.Price.Value)
                    );
                }
                #endregion
                #region Asks
                if (asks != null)
                {
                    _data.Asks.Update(asks
                        .Where(x => x != null && x.Price.HasValue && x.Size.HasValue)
                        .OrderBy(x => x.Price.Value)
                    );
                }
                #endregion
                _data.CalculateAccummulated();
            }
            CalculateMetrics();

            return ret;
        }

        public double GetMaxOrderSize()
        {
            double _maxOrderSize = 0;

            lock (_data.Lock)
            {
                if (_data.Bids != null)
                    _maxOrderSize = _data.Bids.Where(x => x.Size.HasValue).DefaultIfEmpty(new BookItem()).Max(x => x.Size.Value);
                if (_data.Asks != null)
                    _maxOrderSize = Math.Max(_maxOrderSize, _data.Asks.Where(x => x.Size.HasValue).DefaultIfEmpty(new BookItem()).Max(x => x.Size.Value));
            }
            return _maxOrderSize;
        }

        public Tuple<double, double> GetMinMaxSizes()
        {
            lock (_data.Lock)
            {
                return _data.GetMinMaxSizes();
            }
        }

        public virtual object Clone()
        {
            var clone = new OrderBook(_data.Symbol, _data.PriceDecimalPlaces, _data.MaxDepth);
            clone.ProviderID = _data.ProviderID;
            clone.ProviderName = _data.ProviderName;
            clone.SizeDecimalPlaces = _data.SizeDecimalPlaces;
            clone._data.ImbalanceValue = _data.ImbalanceValue;
            clone.ProviderStatus = _data.ProviderStatus;
            clone.MaxDepth = _data.MaxDepth;
            clone.LoadData(Asks, Bids);
            return clone;
        }

        public void PrintLOB(bool isBid)
        {
            lock (_data.Lock)
            {
                int _level = 0;
                foreach (var item in isBid ? _data.Bids : _data.Asks)
                {
                    Console.WriteLine($"{_level} - {item.FormattedPrice} [{item.Size}]");
                    _level++;
                }
            }
        }

        public double ImbalanceValue
        {
            get => _data.ImbalanceValue;
            set => _data.ImbalanceValue = value;
        }
        public long Sequence { get; set; }
        public DateTime? LastUpdated { get; set; }

        private void InternalClear()
        {
            int asksCount = _data.Asks.Count();
            for (int i = asksCount - 1; i >= 0; i--)
            {
                var item = _data.Asks[i];
                if (item.Price != 0)
                {
                    var itemToDelete = DeltaBookItemPool.Get();
                    itemToDelete.IsBid = false;
                    itemToDelete.Price = item.Price;
                    DeleteLevel(itemToDelete);
                    DeltaBookItemPool.Return(itemToDelete);
                }
            }

            int bidsCount = _data.Bids.Count();
            for (int i = bidsCount - 1; i >= 0; i--)
            {
                var item = _data.Bids[i];
                if (item.Price != 0)
                {
                    var itemToDelete = DeltaBookItemPool.Get();
                    itemToDelete.IsBid = true;
                    itemToDelete.Price = item.Price;
                    DeleteLevel(itemToDelete);
                    DeltaBookItemPool.Return(itemToDelete);
                }
            }


            ResetCounters();
        }

        public void UpdateSnapshot(IEnumerable<BookItem> asks, IEnumerable<BookItem> bids)
        {
            lock (_data.Lock)
            {
                // Clear existing data and return items to shared pool to avoid allocation
                _data.Clear();


                // Copy asks using shared pooled objects
                if (asks != null)
                {
                    foreach (var askItem in asks.Where(x => x != null && x.Price.HasValue && x.Size.HasValue))
                    {
                        // FIXED: Get from shared pool instead of instance pool
                        var pooledItem = BookItemPool.Get();
                        pooledItem.CopyFrom(askItem);
                        _data.Asks.Add(pooledItem);
                    }
                }

                // Copy bids using shared pooled objects
                if (bids != null)
                {
                    foreach (var bidItem in bids.Where(x => x != null && x.Price.HasValue && x.Size.HasValue))
                    {
                        // FIXED: Get from shared pool instead of instance pool
                        var pooledItem = BookItemPool.Get();
                        pooledItem.CopyFrom(bidItem);
                        _data.Bids.Add(pooledItem);
                    }
                }

                // Calculate accumulated sizes
                _data.CalculateAccummulated();
            }

            // Calculate metrics outside the lock for better performance
            CalculateMetrics();
        }

        public void Clear()
        {
            lock (_data.Lock)
            {
                InternalClear();
                _data.Clear();
            }
        }

        public void Reset()
        {
            lock (_data.Lock)
            {
                InternalClear();
                _data?.Reset();
            }
        }

        public virtual void AddOrUpdateLevel(DeltaBookItem item)
        {
            if (!item.IsBid.HasValue)
                return;


            if (item.Size.HasValue && IsZeroAtDp(item.Size.Value, this.SizeDecimalPlaces))
            {
                DeleteLevel(item);     // explicit remove, not an update to zero
                return;
            }
            eMDUpdateAction eAction = eMDUpdateAction.None;

            lock (_data.Lock)
            {
                var _list = (item.IsBid.HasValue && item.IsBid.Value ? _data.Bids : _data.Asks);
                BookItem? itemFound = null;
                var targetPrice = item.Price.Value;  // Cache the value
                var count = _list.Count();

                for (int i = 0; i < count; i++)
                {
                    if (_list[i].Price == targetPrice)  // Use cached value
                    {
                        itemFound = _list[i];
                        break;
                    }
                }



                if (itemFound == null)
                    eAction = eMDUpdateAction.New;
                else
                    eAction = eMDUpdateAction.Change;
            }

            if (eAction == eMDUpdateAction.Change)
                UpdateLevel(item);
            else
                AddLevel(item);
        }

        public virtual void AddLevel(DeltaBookItem item)
        {
            if (!item.IsBid.HasValue)
                return;
            if (item.Size.HasValue && IsZeroAtDp(item.Size.Value, this.SizeDecimalPlaces))
            {
                DeleteLevel(item);
                return;
            }
            // quantize what we store so internals never carry float dust
            item.Size = QuantizeToDp(item.Size.Value, this.SizeDecimalPlaces);

            lock (_data.Lock)
            {
                // Check if it is appropriate to add a new item to the Limit Order Book (LOB). 
                // If the item exceeds the depth scope defined by MaxDepth, it should not be added.
                // If the item is within the acceptable depth, truncate the LOB to ensure it adheres to the MaxDepth limit.
                bool willNewItemFallOut = false;

                var list = item.IsBid.Value ? _data.Bids : _data.Asks;
                var listCount = list.Count();
                if (item.IsBid.Value)
                {
                    willNewItemFallOut = listCount > this.MaxDepth && item.Price < list.Min(x => x.Price);
                }
                else
                {
                    willNewItemFallOut = listCount > this.MaxDepth && item.Price > list.Max(x => x.Price);
                }

                if (!willNewItemFallOut)
                {
                    // FIXED: Get from shared pool instead of instance pool
                    var _level = BookItemPool.Get();
                    _level.EntryID = item.EntryID;
                    _level.Price = item.Price;
                    _level.IsBid = item.IsBid.Value;
                    _level.LocalTimeStamp = item.LocalTimeStamp;
                    _level.ProviderID = _data.ProviderID;
                    _level.ServerTimeStamp = item.ServerTimeStamp;
                    _level.Size = item.Size;
                    _level.Symbol = _data.Symbol;
                    _level.PriceDecimalPlaces = this.PriceDecimalPlaces;
                    _level.SizeDecimalPlaces = this.SizeDecimalPlaces;
                    list.Add(_level);
                    listCount++;
                    Interlocked.Increment(ref _addedLevels);
                    if (_level.Size.HasValue && _level.Size.Value > 0)
                    {
                        var scaled = Scale(_level.Size.Value);
                        if (scaled > 0)
                            Interlocked.Add(ref _addedVolumeScaled, scaled);
                    }

                    //truncate last item if we exceeded the MaxDepth
                    if (listCount > MaxDepth)
                    {
                        // ALLOCATION-FREE: Direct index access
                        int startIndex = MaxDepth; // First item to remove

                        // Process excess items in reverse order (LIFO)
                        for (int i = listCount - 1; i >= startIndex; i--)
                        {
                            var itemToReturn = list[i];
                            BookItemPool.Return(itemToReturn);
                        }

                        // Truncate in one operation
                        list.TruncateItemsAfterPosition(MaxDepth - 1);
                    }
                }
            }
        }

        public virtual void UpdateLevel(DeltaBookItem item)
        {
            if (item.Size.HasValue && IsZeroAtDp(item.Size.Value, this.SizeDecimalPlaces))
            {
                DeleteLevel(item);
                return;
            }
            // quantize what we store so internals never carry float dust
            item.Size = QuantizeToDp(item.Size.Value, this.SizeDecimalPlaces);

            lock (_data.Lock)
            {
                (item.IsBid.HasValue && item.IsBid.Value ? _data.Bids : _data.Asks).Update(x => x.Price == item.Price,
                    existingItem =>
                    {
                        double oldSize = existingItem.Size ?? 0.0;
                        double newSize = item.Size ?? 0.0;

                        if (oldSize > newSize)
                        {
                            var delta = oldSize - newSize;
                            var scaled = Scale(delta);
                            if (scaled > 0) Interlocked.Add(ref _deletedVolumeScaled, scaled);
                            Interlocked.Increment(ref _deletedLevels);
                        }
                        else if (oldSize < newSize)
                        {
                            var delta = newSize - oldSize;
                            var scaled = Scale(delta);
                            if (scaled > 0) Interlocked.Add(ref _addedVolumeScaled, scaled);
                            Interlocked.Increment(ref _addedLevels);
                        }
                        else
                        {
                            Interlocked.Increment(ref _updatedLevels);
                            // (Keep _updatedVolumeScaled unused; hook here if needed.)
                        }

                        existingItem.Price = item.Price;
                        existingItem.Size = item.Size;
                        existingItem.LocalTimeStamp = item.LocalTimeStamp;
                        existingItem.ServerTimeStamp = item.ServerTimeStamp;
                    });
            }
        }

        public virtual void DeleteLevel(DeltaBookItem item)
        {
            if (string.IsNullOrEmpty(item.EntryID) && (!item.Price.HasValue || item.Price.Value == 0))
                throw new Exception("DeltaBookItem cannot be deleted since has no price or no EntryID.");
            lock (_data.Lock)
            {
                BookItem _itemToDelete = null;

                if (!string.IsNullOrEmpty(item.EntryID))
                {
                    _itemToDelete = (item.IsBid.HasValue && item.IsBid.Value ? _data.Bids : _data.Asks)
                        .FirstOrDefault(x => x.EntryID == item.EntryID);
                }
                else if (item.Price.HasValue && item.Price > 0)
                {
                    _itemToDelete = (item.IsBid.HasValue && item.IsBid.Value ? _data.Bids : _data.Asks)
                        .FirstOrDefault(x => x.Price == item.Price);
                }

                if (_itemToDelete != null)
                {
                    (item.IsBid.HasValue && item.IsBid.Value ? _data.Bids : _data.Asks).Remove(_itemToDelete);
                    // FIXED: Return to shared pool instead of instance pool
                    Interlocked.Increment(ref _deletedLevels);
                    double sz = _itemToDelete.Size ?? 0.0;
                    BookItemPool.Return(_itemToDelete);
                    if (sz > 0)
                    {
                        var scaled = Scale(sz);
                        if (scaled > 0)
                            Interlocked.Add(ref _deletedVolumeScaled, scaled);
                    }
                }
            }
        }

        public (long added, long deleted, long updated) GetCounters()
        {
            long added = Interlocked.Read(ref _addedLevels);
            long deleted = Interlocked.Read(ref _deletedLevels);
            long updated = Interlocked.Read(ref _updatedLevels);
            return (added, deleted, updated);
        }
        public (double addedVol, double deletedVol, double updatedVol) GetCountersVolume()
        {
            var a = (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref _addedVolumeScaled));
            var d = (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref _deletedVolumeScaled));
            var u = (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref _updatedVolumeScaled));
            return (Unscale(a), Unscale(d), Unscale(u));
        }
        private void ResetCounters()
        {
            Interlocked.Exchange(ref _addedLevels, 0);
            Interlocked.Exchange(ref _deletedLevels, 0);
            Interlocked.Exchange(ref _updatedLevels, 0);
            Interlocked.Exchange(ref _addedVolumeScaled, 0);
            Interlocked.Exchange(ref _deletedVolumeScaled, 0);
            Interlocked.Exchange(ref _updatedVolumeScaled, 0);
        }






        /// <summary>
        /// Computes the delta needed to transform THIS order book into <paramref name="other"/>,
        /// emitting one <see cref="DeltaBookItem"/> per changed price level (absolute size; 0 = delete).
        /// Performance: O(N+M) per side; allocation-free (uses pooled DeltaBookItem).
        /// IMPORTANT: <paramref name="onDelta"/> MUST consume the item synchronously and copy its data.
        /// This method RETURNS the pooled item immediately after invoking the callback.
        /// </summary>
        /// <param name="other">The newer snapshot for the same venue/symbol.</param>
        /// <param name="onDelta">
        /// Callback that receives one pooled DeltaBookItem per change. Do not retain the reference.
        /// Typical usage: obLocal.AddOrUpdateLevel(delta);  // which copies fields synchronously
        /// </param>
        public void ComputeDeltaAgainst(OrderBook other, Action<DeltaBookItem> onDelta)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            if (onDelta == null) throw new ArgumentNullException(nameof(onDelta));

            // Lock both books in a deterministic order to avoid deadlocks when called from multiple modules.
            var lockA = _data.Lock;
            var lockB = other._data.Lock;
            int ha = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(lockA);
            int hb = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(lockB);

            if (ha <= hb)
            {
                lock (lockA) lock (lockB) { DiffBothSides_NoAlloc_(this, other, onDelta); }
            }
            else
            {
                lock (lockB) lock (lockA) { DiffBothSides_NoAlloc_(this, other, onDelta); }
            }
        }


        private static void DiffBothSides_NoAlloc_(OrderBook oldBook, OrderBook newBook, Action<DeltaBookItem> emit)
        {
            // Access underlying storage directly to avoid MaxDepth filtering and extra wrappers.
            var oldBids = oldBook._data.Bids;
            var newBids = newBook._data.Bids;
            var oldAsks = oldBook._data.Asks;
            var newAsks = newBook._data.Asks;

            DiffSide_NoAlloc_(oldBids, newBids, /*isBid*/ true, emit);
            DiffSide_NoAlloc_(oldAsks, newAsks, /*isBid*/ false, emit);
        }

        private static void DiffSide_NoAlloc_(
            CachedCollection<BookItem> oldSide,
            CachedCollection<BookItem> newSide,
            bool isBid,
            Action<DeltaBookItem> emit)
        {
            int i = 0, j = 0;
            int oldCount = oldSide == null ? 0 : oldSide.Count();
            int newCount = newSide == null ? 0 : newSide.Count();

            // Local function to emit a pooled delta and immediately return it to the pool.
            static void Emit(bool isBidL, double price, double size, Action<DeltaBookItem> sink)
            {
                var d = DeltaBookItemPool.Get();
                d.IsBid = isBidL;
                d.Price = price;
                d.Size = size;
                d.LocalTimeStamp = DateTime.UtcNow; // caller may overwrite if needed
                d.ServerTimeStamp = d.LocalTimeStamp;
                sink(d);
                DeltaBookItemPool.Return(d); // safe because consumer must copy synchronously
            }

            // Merge walk
            while (i < oldCount && j < newCount)
            {
                var o = oldSide[i];
                var n = newSide[j];

                // Assuming exact price equality as elsewhere in the codebase (AddOrUpdateLevel uses ==).
                double op = o.Price.GetValueOrDefault();
                double np = n.Price.GetValueOrDefault();

                if (op == np)
                {
                    // Same price level: update only if size changed
                    double os = o.Size.GetValueOrDefault();
                    double ns = n.Size.GetValueOrDefault();
                    if (os != ns)
                        Emit(isBid, np, ns, emit);

                    i++; j++;
                }
                else
                {
                    // Determine ordering based on side sort (bids: desc, asks: asc)
                    bool oldComesFirst = isBid ? (op > np) : (op < np);

                    if (oldComesFirst)
                    {
                        // Level present in old but not (at this position) in new => removed (size -> 0)
                        Emit(isBid, op, 0.0, emit);
                        i++;
                    }
                    else
                    {
                        // Level present in new but not in old => added (size -> ns)
                        Emit(isBid, np, n.Size.GetValueOrDefault(), emit);
                        j++;
                    }
                }
            }

            // Any remaining old levels are deletions
            while (i < oldCount)
            {
                var o = oldSide[i++];
                double op = o.Price.GetValueOrDefault();
                if (op != 0.0) Emit(isBid, op, 0.0, emit);
            }

            // Any remaining new levels are additions
            while (j < newCount)
            {
                var n = newSide[j++];
                double np = n.Price.GetValueOrDefault();
                if (np != 0.0) Emit(isBid, np, n.Size.GetValueOrDefault(), emit);
            }
        }


        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static double QuantizeToDp(double size, int dp)
        {
            return Math.Round(size, dp, MidpointRounding.ToZero);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsZeroAtDp(double size, int dp)
        {
            // Anything that quantizes to 0 at this precision is treated as zero.
            return QuantizeToDp(size, dp) == 0.0;
        }


        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static ulong ComputeScale(int dp) =>
            dp switch
            {
                0 => 1UL,
                1 => 10UL,
                2 => 100UL,
                3 => 1_000UL,
                4 => 10_000UL,
                5 => 100_000UL,
                6 => 1_000_000UL,
                7 => 10_000_000UL,
                8 => 100_000_000UL,
                9 => 1_000_000_000UL,
                _ => (ulong)Math.Pow(10, dp)
            };

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private ulong Scale(double v)
        {
            if (v <= 0) return 0;
            return (ulong)Math.Round(v * _volumeScale, MidpointRounding.ToZero);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private double Unscale(ulong scaled) => scaled / (double)_volumeScale;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _data?.Dispose();
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

