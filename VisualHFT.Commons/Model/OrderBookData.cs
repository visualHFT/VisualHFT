using System.Runtime.CompilerServices;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.Pools;
using VisualHFT.Enums;
using VisualHFT.Helpers;

namespace VisualHFT.Model
{
    public class OrderBookData : IResettable, IDisposable
    {
        // Zero-allocation RAII lock releasers (structs avoid heap allocation on every lock operation):
        public readonly struct ReadLockReleaser : IDisposable
        {
            private readonly ReaderWriterLockSlim _lock;
            public ReadLockReleaser(ReaderWriterLockSlim rwLock) => _lock = rwLock;
            public void Dispose() => _lock.ExitReadLock();
        }

        public readonly struct WriteLockReleaser : IDisposable
        {
            private readonly ReaderWriterLockSlim _lock;
            public WriteLockReleaser(ReaderWriterLockSlim rwLock) => _lock = rwLock;
            public void Dispose() => _lock.ExitWriteLock();
        }
        private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        /// <summary>
        /// Exposes the internal lock for advanced scenarios requiring coordinated locking across multiple OrderBooks.
        /// Use with caution - prefer EnterReadLock/EnterWriteLock for normal operations.
        /// </summary>
        public object Lock => _rwLock;

        /// <summary>
        /// Enters a read lock. Use in 'using' statement for automatic cleanup.
        /// Returns concrete struct type to avoid heap allocation.
        /// </summary>
        public ReadLockReleaser EnterReadLock()
        {
            _rwLock.EnterReadLock();
            return new ReadLockReleaser(_rwLock);
        }
        /// <summary>
        /// Enters a write lock. Use in 'using' statement for automatic cleanup.
        /// Returns concrete struct type to avoid heap allocation.
        /// </summary>
        public WriteLockReleaser EnterWriteLock()
        {
            _rwLock.EnterWriteLock();
            return new WriteLockReleaser(_rwLock);
        }






        private bool _disposed = false; // to track whether the object has been disposed
        private CachedCollection<BookItem> _Asks;
        private CachedCollection<BookItem> _Bids;
        public CachedCollection<BookItem> Asks
        {
            get
            {
                return _Asks;
            }
        }
        public CachedCollection<BookItem> Bids
        {
            get
            {
                return _Bids;
            }
        }
        public string Symbol { get; set; }
        public int PriceDecimalPlaces { get; set; }
        public int SizeDecimalPlaces { get; set; }
        public double SymbolMultiplier => Math.Pow(10, PriceDecimalPlaces);
        public int ProviderID { get; set; }
        public string ProviderName { get; set; }
        public eSESSIONSTATUS ProviderStatus { get; set; }
        public int MaxDepth { get; set; }
        public double MaximumCummulativeSize { get; set; }
        public double ImbalanceValue { get; set; }
        /// <summary>
        /// Gets Top of Book (best bid or ask).
        /// OPTIMIZATION: Use indexed access [0] instead of FirstOrDefault() to avoid LINQ iterator allocation.
        /// </summary>
        public BookItem GetTOB(bool isBid)
        {
            if (isBid)
                return (Bids != null && Bids.Count() > 0) ? Bids[0] : null;
            else
                return (Asks != null && Asks.Count() > 0) ? Asks[0] : null;
        }
        public double MidPrice
        {
            get
            {
                var _bidTOP = GetTOB(true);
                var _askTOP = GetTOB(false);
                if (_bidTOP != null && _bidTOP.Price.HasValue && _askTOP != null && _askTOP.Price.HasValue)
                {
                    return (_bidTOP.Price.Value + _askTOP.Price.Value) / 2.0;
                }
                return 0;
            }
        }
        public double Spread
        {
            get
            {
                var _bidTOP = GetTOB(true);
                var _askTOP = GetTOB(false);
                if (_bidTOP != null && _bidTOP.Price.HasValue && _askTOP != null && _askTOP.Price.HasValue)
                {
                    return _askTOP.Price.Value - _bidTOP.Price.Value;
                }
                return 0;
            }
        }
        public bool FilterBidAskByMaxDepth { get; set; }

        public OrderBookData()
        {
            _Bids = new CachedCollection<BookItem>((x, y) => y.Price.GetValueOrDefault().CompareTo(x.Price.GetValueOrDefault()));
            _Asks = new CachedCollection<BookItem>((x, y) => x.Price.GetValueOrDefault().CompareTo(y.Price.GetValueOrDefault()));
        }

        public OrderBookData(string symbol, int priceDecimalPlaces, int maxDepth)
        {
            _Bids = new CachedCollection<BookItem>((x, y) => y.Price.GetValueOrDefault().CompareTo(x.Price.GetValueOrDefault()), maxDepth);
            _Asks = new CachedCollection<BookItem>((x, y) => x.Price.GetValueOrDefault().CompareTo(y.Price.GetValueOrDefault()), maxDepth);

            MaxDepth = maxDepth;
            Symbol = symbol;
            PriceDecimalPlaces = priceDecimalPlaces;
        }
        public void CalculateAccummulated()
        {
            double cumSize = 0;
            foreach (var o in Bids)
            {
                cumSize += o.Size.Value;
                o.CummulativeSize = cumSize;
            }

            MaximumCummulativeSize = cumSize;

            cumSize = 0;
            foreach (var o in Asks)
            {
                cumSize += o.Size.Value;
                o.CummulativeSize = cumSize;
            }
            MaximumCummulativeSize = Math.Max(MaximumCummulativeSize, cumSize);
        }
        public (double min, double max) GetMinMaxSizes()
        {
            double minVal = 0;
            double maxVal = 0;
            if (Asks == null || Bids == null
                             || Asks.Count() == 0
                             || Bids.Count() == 0)
                return (0, 0);

            int bidsCount = Bids.Count();
            for (int i = 0; i < bidsCount; i++)
            {
                var size = Bids[i].Size;
                if (size.HasValue)
                {
                    minVal = Math.Min(minVal, size.Value);
                    maxVal = Math.Max(maxVal, size.Value);
                }
            }
            int asksCount = Asks.Count();
            for (int i = 0; i < asksCount; i++)
            {
                var size = Asks[i].Size;
                if (size.HasValue)
                {
                    minVal = Math.Min(minVal, size.Value);
                    maxVal = Math.Max(maxVal, size.Value);
                }
            }

            return (minVal, maxVal);
        }

        public void Reset()
        {
            Clear();
            Symbol = "";
            PriceDecimalPlaces = 0;
            SizeDecimalPlaces = 0;
            ProviderID = 0;
            ProviderName = "";
            MaxDepth = 0;
        }
        public void Clear()
        {
            // Return all items to the shared pool, then clear
            foreach (var item in _Asks)
            {
                BookItemPool.Return(item);
            }
            _Asks.Clear();

            foreach (var item in _Bids)
            {
                BookItemPool.Return(item);
            }
            _Bids.Clear();
        }


        /// <summary>
        /// Returns a read-only span of asks WITHOUT allocating.
        /// MUST be called while holding Lock.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<BookItem> GetAsksSpan(int maxCount = int.MaxValue)
        {
            // Caller MUST hold Lock!
            if (_Asks == null)
                return ReadOnlySpan<BookItem>.Empty;

            return _Asks.AsSpan(maxCount);
        }

        /// <summary>
        /// Returns a read-only span of bids WITHOUT allocating.
        /// MUST be called while holding Lock.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<BookItem> GetBidsSpan(int maxCount = int.MaxValue)
        {
            // Caller MUST hold Lock!
            if (_Bids == null)
                return ReadOnlySpan<BookItem>.Empty;

            return _Bids.AsSpan(maxCount);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Clear();  // This returns all BookItems to the pool

                    _rwLock?.Dispose();
                    _Bids?.Clear();
                    _Asks?.Clear();
                    _Bids = null;
                    _Asks = null;
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