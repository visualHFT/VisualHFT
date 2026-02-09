using VisualHFT.Commons.Model;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Pools;
namespace VisualHFT.Model
{
    public partial class BookItem : IEquatable<BookItem>, IEqualityComparer<BookItem>, IResettable, ICopiable<BookItem>, IDisposable, IBasicBookItem
    {

        private string _Symbol;
        private int _ProviderID;
        private string _EntryID;
        private string _LayerName;
        private DateTime _LocalTimeStamp;
        private double? _Price;
        private DateTime _ServerTimeStamp;
        private double? _Size;
        private bool _IsBid;
        private double? _ActiveSize;
        private double? _CummulativeSize;
        public BookItem()
        {
        }


        public bool Equals(BookItem other)
        {
            if (other == null)
                return false;
            if (IsBid != other.IsBid)
                return false;
            if (EntryID != other.EntryID)
                return false;
            if (Price != other.Price)
                return false;
            if (Size != other.Size)
                return false;
            return true;

        }

        public bool Equals(BookItem x, BookItem y)
        {
            return x.Price == y.Price;
        }

        public int GetHashCode(BookItem obj)
        {
            return obj.Price.GetHashCode();
        }

        public void Reset()
        {
            Symbol = "";
            ProviderID = 0;
            EntryID = "";
            LayerName = "";
            LocalTimeStamp = DateTime.MinValue;
            ServerTimeStamp = DateTime.MinValue;
            Price = 0;
            Size = 0;
            IsBid = false;
            PriceDecimalPlaces = 0;
            SizeDecimalPlaces = 0;
            ActiveSize = null;
            CummulativeSize = 0;
        }

        public void CopyFrom(BookItem bookItem)
        {
            if (bookItem == null) throw new ArgumentNullException();
            Symbol = bookItem.Symbol;
            ProviderID = bookItem.ProviderID;
            EntryID = bookItem.EntryID;
            LayerName = bookItem.LayerName;
            LocalTimeStamp = bookItem.LocalTimeStamp;
            ServerTimeStamp = bookItem.ServerTimeStamp;
            Price = bookItem.Price;
            Size = bookItem.Size;
            IsBid = bookItem.IsBid;
            PriceDecimalPlaces = bookItem.PriceDecimalPlaces;
            SizeDecimalPlaces = bookItem.SizeDecimalPlaces;
            ActiveSize = bookItem.ActiveSize;
            CummulativeSize = bookItem.CummulativeSize;

        }

        /// <summary>
        /// Optimized copy for snapshot operations - copies only essential per-level fields.
        /// Skips Symbol, ProviderID, EntryID, LayerName, timestamps, and decimal places
        /// which are identical across all levels in an order book.
        /// ~2.5x faster than full CopyFrom().
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void CopyEssentialsFrom(BookItem source)
        {
            // Only copy fields that vary per price level
            _Price = source._Price;
            _Size = source._Size;
            _IsBid = source._IsBid;
            _ActiveSize = source._ActiveSize;
            _CummulativeSize = source._CummulativeSize;
        }

        public int PriceDecimalPlaces { get; set; }
        public int SizeDecimalPlaces { get; set; }


        public string Symbol
        {
            get => _Symbol;
            set => _Symbol = value;
        }

        public int ProviderID
        {
            get => _ProviderID;
            set => _ProviderID = value;
        }

        public string EntryID
        {
            get => _EntryID;
            set => _EntryID = value;
        }

        public string LayerName
        {
            get => _LayerName;
            set => _LayerName = value;
        }

        public DateTime LocalTimeStamp
        {
            get => _LocalTimeStamp;
            set => _LocalTimeStamp = value;
        }

        public double? Price
        {
            get => _Price;
            set => _Price = value;
        }

        public DateTime ServerTimeStamp
        {
            get => _ServerTimeStamp;
            set => _ServerTimeStamp = value;
        }

        public double? Size
        {
            get => _Size;
            set => _Size = value;
        }

        public bool IsBid
        {
            get => _IsBid;
            set => _IsBid = value;
        }
        public string FormattedPrice => this.Price.HasValue ? this.Price.Value.ToString("N" + PriceDecimalPlaces) : "";
        public string FormattedSize => this.Size.HasValue ? HelperCommon.GetKiloFormatter(this.Size.Value, SizeDecimalPlaces) : "";
        public string FormattedActiveSize => this.ActiveSize.HasValue ? HelperCommon.GetKiloFormatter(this.ActiveSize.Value) : "";

        public double? CummulativeSize
        {
            get => _CummulativeSize;
            set => _CummulativeSize = value;
        }

        public double? ActiveSize
        {
            get => _ActiveSize;
            set => _ActiveSize = value;
        }


        public virtual void Dispose()
        {

        }
    }
}
