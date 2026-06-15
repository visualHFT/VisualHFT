using System.Collections.Generic;

namespace MarketConnectors.Kraken
{
    /// <summary>
    /// A lightweight per-symbol DECIMAL mirror of the order book, maintained alongside the display
    /// (double-based) <see cref="VisualHFT.Model.OrderBook"/> for the sole purpose of validating
    /// Kraken's v2 CRC32 integrity checksum.
    ///
    /// It exists because the checksum must be computed from the raw exchange decimals at their wire
    /// precision (see <see cref="KrakenChecksum"/>); the display book stores <see cref="double"/> and
    /// cannot reproduce the trailing-zero scale Kraken's algorithm requires.
    ///
    /// NOT thread-safe by design: snapshot and deltas are applied in order on the single market-data
    /// consumer thread (same discipline as the display book), so no locking is needed.
    /// </summary>
    public sealed class KrakenDecimalBook
    {
        // Asks ascending (best/lowest first); bids descending (best/highest first) — Kraken checksum order.
        // SortedList (array-backed) not SortedDictionary (red-black tree): the checksum reads the top levels by
        // index every frame, which is zero-allocation on SortedList but allocates a traversal stack per foreach
        // on SortedDictionary. Updates are mostly in-place quantity changes (no array shift).
        private readonly SortedList<decimal, decimal> _asks = new();
        private readonly SortedList<decimal, decimal> _bids =
            new(Comparer<decimal>.Create((a, b) => b.CompareTo(a)));

        // Symbol precision used to format every price/quantity for the CRC32 (reconstructs wire trailing zeros).
        private int _priceDecimals;
        private int _sizeDecimals;

        // Subscribed book depth. Kraken's WS v2 book channel requires the client to keep its local book trimmed
        // to the subscribed depth: a level that falls outside the depth window is dropped WITHOUT a qty=0 delete
        // (https://docs.kraken.com/api/docs/guides/spot-ws-book-v2/). Without this cap the ladder accumulates
        // ghost levels Kraken has discarded, which eventually re-enter the top-10 and break the checksum. A value
        // &lt;= 0 disables the cap (used by the offline test seam that never validates).
        private int _depth;

        /// <summary>Has a snapshot been installed? Until then there is nothing to checksum against.</summary>
        public bool IsSeeded { get; private set; }

        /// <summary>
        /// Replace the whole book from a snapshot (asks/bids = price -&gt; absolute quantity) and record the
        /// symbol's price/quantity precision and the subscribed <paramref name="depth"/> used to keep the book
        /// trimmed to what Kraken itself retains.
        /// </summary>
        public void Reset(
            IEnumerable<KeyValuePair<decimal, decimal>> asks,
            IEnumerable<KeyValuePair<decimal, decimal>> bids,
            int priceDecimals,
            int sizeDecimals,
            int depth)
        {
            _priceDecimals = priceDecimals;
            _sizeDecimals = sizeDecimals;
            _depth = depth;
            _asks.Clear();
            _bids.Clear();
            if (asks != null)
                foreach (var a in asks) SetLevel(_asks, a.Key, a.Value);
            if (bids != null)
                foreach (var b in bids) SetLevel(_bids, b.Key, b.Value);
            TrimToDepth();
            IsSeeded = true;
        }

        /// <summary>
        /// Apply a delta frame. Kraken's aggregated book sends the new ABSOLUTE quantity per price
        /// level; quantity 0 removes the level. Mirrors the display book's apply rules exactly so the
        /// two stay in lockstep (negative quantities and the (0,0) sentinel are skipped, not applied).
        /// </summary>
        public void ApplyDelta(
            IEnumerable<KeyValuePair<decimal, decimal>> asks,
            IEnumerable<KeyValuePair<decimal, decimal>> bids)
        {
            if (asks != null)
                foreach (var a in asks) SetLevel(_asks, a.Key, a.Value);
            if (bids != null)
                foreach (var b in bids) SetLevel(_bids, b.Key, b.Value);
            TrimToDepth();
        }

        /// <summary>Apply a single ask level (absolute quantity; 0 removes). Zero-allocation hot-path entry
        /// so the caller can mirror each delta in the same pass it updates the display book.</summary>
        public void ApplyAsk(decimal price, decimal quantity) => SetLevel(_asks, price, quantity);

        /// <summary>Apply a single bid level (absolute quantity; 0 removes). See <see cref="ApplyAsk"/>.</summary>
        public void ApplyBid(decimal price, decimal quantity) => SetLevel(_bids, price, quantity);

        /// <summary>
        /// Trim each side to the subscribed depth, evicting the worst-priced levels Kraken has dropped from its
        /// own depth window (it sends no qty=0 for them). The worst level is the LAST index on both sides (asks
        /// ascending, bids descending), so this is an O(1)-per-eviction tail removal on the array-backed list.
        /// Call once per applied frame, before computing the checksum. No-op when depth &lt;= 0 (test seam).
        /// </summary>
        public void TrimToDepth()
        {
            if (_depth <= 0)
                return;
            while (_asks.Count > _depth)
                _asks.RemoveAt(_asks.Count - 1);
            while (_bids.Count > _depth)
                _bids.RemoveAt(_bids.Count - 1);
        }

        private static void SetLevel(SortedList<decimal, decimal> side, decimal price, decimal qty)
        {
            if (qty < 0m)
                return;                          // invalid for an aggregated book — skip (matches display book)
            if (qty == 0m)
            {
                if (price != 0m)
                    side.Remove(price);          // explicit level removal
                return;
            }
            side[price] = qty;                   // set absolute quantity at this price
        }

        /// <summary>CRC32 over the top-10 asks (ascending) then top-10 bids (descending). Zero-allocation:
        /// the in-place overload iterates the sorted books directly (no top-10 list).</summary>
        public uint ComputeChecksum()
        {
            return KrakenChecksum.Compute(_asks, _bids, _priceDecimals, _sizeDecimals);
        }

        public void Clear()
        {
            _asks.Clear();
            _bids.Clear();
            IsSeeded = false;
        }
    }
}
