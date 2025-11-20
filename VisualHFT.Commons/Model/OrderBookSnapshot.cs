using VisualHFT.Commons.Pools;
using VisualHFT.Helpers;
using VisualHFT.Model;

namespace VisualHFT.Commons.Model
{
    public class OrderBookSnapshot : IResettable
    {
        private List<BookItem> _asks;
        private List<BookItem> _bids;
        private string _symbol;
        private int _priceDecimalPlaces;
        private int _sizeDecimalPlaces;
        private int _providerId;
        private string _providerName;
        private int _maxDepth;
        private double _imbalanceValue;
        private DateTime _lastUpdated;

        public List<BookItem> Asks
        {
            get => _asks;
            private set => _asks = value;
        }

        public List<BookItem> Bids
        {
            get => _bids;
            private set => _bids = value;
        }

        public string Symbol
        {
            get => _symbol;
            set => _symbol = value;
        }

        public int PriceDecimalPlaces
        {
            get => _priceDecimalPlaces;
            set => _priceDecimalPlaces = value;
        }

        public int SizeDecimalPlaces
        {
            get => _sizeDecimalPlaces;
            set => _sizeDecimalPlaces = value;
        }

        public double SymbolMultiplier => Math.Pow(10, PriceDecimalPlaces);

        public int ProviderID
        {
            get => _providerId;
            set => _providerId = value;
        }

        public string ProviderName
        {
            get => _providerName;
            set => _providerName = value;
        }

        public int MaxDepth
        {
            get => _maxDepth;
            set => _maxDepth = value;
        }

        public double ImbalanceValue
        {
            get => _imbalanceValue;
            set => _imbalanceValue = value;
        }

        public DateTime LastUpdated
        {
            get => _lastUpdated;
            set => _lastUpdated = value;
        }

        // Constructor creates new subcollections.
        public OrderBookSnapshot()
        {
            Bids = new List<BookItem>();
            Asks = new List<BookItem>();
        }

        // Update the snapshot from the master OrderBook.
        public void UpdateFrom(OrderBook master)
        {
            if (master == null)
                throw new ArgumentNullException(nameof(master));

            this.Symbol = master.Symbol;
            this.ProviderID = master.ProviderID;
            this.ProviderName = master.ProviderName;
            this.PriceDecimalPlaces = master.PriceDecimalPlaces;
            this.SizeDecimalPlaces = master.SizeDecimalPlaces;
            this.MaxDepth = master.MaxDepth;
            this.ImbalanceValue = master.ImbalanceValue;
            if (master.LastUpdated.HasValue)
                this.LastUpdated = master.LastUpdated.Value;
            else
                this.LastUpdated = HelperTimeProvider.Now;

            CopyBookItems(master.Asks, _asks);
            CopyBookItems(master.Bids, _bids);
        }

        private void CopyBookItems(CachedCollection<BookItem> from, List<BookItem> to)
        {
            if (from == null)
            {
                return;
            }

            ClearBookItems(to); //reset before copying

            foreach (var bookItem in from)
            {
                if (bookItem == null) continue; // skip null items

                // CHANGED: Use shared pool instead of instance pool
                var _item = BookItemPool.Get();
                _item.CopyFrom(bookItem);
                to.Add(_item);
            }
        }

        // Reset the snapshot to a clean state - properly implements IResettable
        public void Reset()
        {
            ClearBookItems(_asks);
            ClearBookItems(_bids);

            // Reset all properties to default values
            _symbol = null;
            _priceDecimalPlaces = 0;
            _sizeDecimalPlaces = 0;
            _providerId = 0;
            _providerName = null;
            _maxDepth = 0;
            _imbalanceValue = 0;
            _lastUpdated = DateTime.MinValue;
        }
        private void ClearBookItems(List<BookItem> list)
        {
            if (list == null) return;

            // CHANGED: Return all valid items to the shared pool before clearing
            foreach (var item in list)
            {
                BookItemPool.Return(item);
            }

            list.Clear();
        }


        public BookItem GetTOB(bool isBid)
        {
            if (isBid)
            {
                return _bids?.Count > 0 ? _bids[0] : null;
            }
            else
            {
                return _asks?.Count > 0 ? _asks[0] : null;
            }
        }

        public double MidPrice
        {
            get
            {
                var _bidTOP = GetTOB(true);
                var _askTOP = GetTOB(false);
                if (_bidTOP?.Price.HasValue == true && _askTOP?.Price.HasValue == true)
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
                if (_bidTOP?.Price.HasValue == true && _askTOP?.Price.HasValue == true)
                {
                    return _askTOP.Price.Value - _bidTOP.Price.Value;
                }
                return 0;
            }
        }

        public Tuple<double, double> GetMinMaxSizes()
        {
            if (Asks == null || Bids == null || Asks.Count == 0 || Bids.Count == 0)
                return new Tuple<double, double>(0, 0);

            double minVal = double.MaxValue;
            double maxVal = double.MinValue;
            bool hasValidValue = false;

            foreach (var o in _bids)
            {
                if (o?.Size.HasValue == true && o.Size.Value > 0)
                {
                    minVal = Math.Min(minVal, o.Size.Value);
                    maxVal = Math.Max(maxVal, o.Size.Value);
                    hasValidValue = true;
                }
            }

            foreach (var o in _asks)
            {
                if (o?.Size.HasValue == true && o.Size.Value > 0)
                {
                    minVal = Math.Min(minVal, o.Size.Value);
                    maxVal = Math.Max(maxVal, o.Size.Value);
                    hasValidValue = true;
                }
            }

            return hasValidValue ? Tuple.Create(minVal, maxVal) : new Tuple<double, double>(0, 0);
        }

    }
}
