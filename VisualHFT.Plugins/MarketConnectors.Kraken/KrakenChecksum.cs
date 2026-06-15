using System;
using System.Collections.Generic;
using System.Globalization;

namespace MarketConnectors.Kraken
{
    /// <summary>
    /// Computes the Kraken WebSocket v2 "book" channel CRC32 integrity checksum.
    ///
    /// Algorithm (per https://docs.kraken.com/api/docs/guides/spot-ws-book-v2/):
    ///   - Iterate the top 10 ASKS (ascending, best/lowest ask first), then the top 10 BIDS
    ///     (descending, best/highest bid first). Fewer than 10 levels per side is allowed.
    ///   - For each level append the formatted price then the formatted quantity.
    ///   - Formatting per value: render at the symbol's wire precision (trailing zeros KEPT to that
    ///     precision), remove the decimal point, then remove LEADING zeros only.
    ///   - Concatenate every asks token followed by every bids token and CRC32 the ASCII string.
    ///   - The CRC32 (poly 0xEDB88320, init 0xFFFFFFFF, final XOR) is taken as an unsigned 32-bit int.
    ///
    /// PRECISION: callers pass the symbol's price/quantity decimal places explicitly. The value is rendered
    /// with the fixed-point ("F{decimals}") format, which RECONSTRUCTS the trailing zeros the checksum depends
    /// on — required because the connector library deserializes prices/quantities as <see cref="decimal"/>
    /// values that may have lost their wire trailing-zero scale (a checksum from the decimal's own scale then
    /// fails to match). When the scale is already intact and the precision is correct, this is a no-op, so it
    /// can never make a correct checksum wrong. Inputs MUST still be the raw exchange decimals (never doubles).
    ///
    /// ZERO-ALLOCATION: this runs once per frame on the market-data hot path when validation is enabled, so it
    /// never materializes the concatenated string. It streams each value's ASCII bytes directly into a running
    /// CRC32, formatting into a stack buffer via <see cref="decimal.TryFormat"/>. The 256-entry CRC table is
    /// built once at type initialization. The <see cref="SortedList{TKey,TValue}"/> overload reads the book by
    /// index (<c>GetKeyAtIndex</c>/<c>GetValueAtIndex</c>) so nothing is allocated — unlike a SortedDictionary,
    /// whose red-black-tree enumerator heap-allocates a traversal stack per <c>foreach</c>.
    /// </summary>
    public static class KrakenChecksum
    {
        public const int ChecksumLevels = 10;

        // Standard CRC32 (IEEE 802.3) table — identical construction to Crc32Calculator, kept local so this
        // file is a self-contained unit. Built once; reads are lock-free and allocation-free.
        private static readonly uint[] _crc32Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            const uint polynomial = 0xEDB88320;
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) == 1 ? (crc >> 1) ^ polynomial : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        /// <summary>
        /// List-based overload (the correctness oracle exercised by the official-vector tests).
        /// </summary>
        /// <param name="asksAscending">Asks ordered best (lowest) price first.</param>
        /// <param name="bidsDescending">Bids ordered best (highest) price first.</param>
        /// <param name="priceDecimals">Symbol price precision (decimal places) used to format every price.</param>
        /// <param name="sizeDecimals">Symbol quantity precision (decimal places) used to format every quantity.</param>
        public static uint Compute(
            IReadOnlyList<KeyValuePair<decimal, decimal>> asksAscending,
            IReadOnlyList<KeyValuePair<decimal, decimal>> bidsDescending,
            int priceDecimals,
            int sizeDecimals,
            int levels = ChecksumLevels)
        {
            if (asksAscending == null) throw new ArgumentNullException(nameof(asksAscending));
            if (bidsDescending == null) throw new ArgumentNullException(nameof(bidsDescending));
            if (priceDecimals < 0) priceDecimals = 0;
            if (sizeDecimals < 0) sizeDecimals = 0;

            uint crc = 0xFFFFFFFF;
            int na = Math.Min(levels, asksAscending.Count);
            for (int i = 0; i < na; i++)
            {
                var kv = asksAscending[i];
                crc = AppendToken(crc, kv.Key, priceDecimals);
                crc = AppendToken(crc, kv.Value, sizeDecimals);
            }
            int nb = Math.Min(levels, bidsDescending.Count);
            for (int i = 0; i < nb; i++)
            {
                var kv = bidsDescending[i];
                crc = AppendToken(crc, kv.Key, priceDecimals);
                crc = AppendToken(crc, kv.Value, sizeDecimals);
            }
            return ~crc;
        }

        /// <summary>
        /// Hot-path overload: reads the live sorted books by index (no enumerator, no top-10 list) and streams
        /// every byte into the CRC. Zero heap allocation. The lists must be ordered best-first per side (asks
        /// ascending, bids descending), which their comparers guarantee, so index 0..levels-1 is the top.
        /// </summary>
        public static uint Compute(
            SortedList<decimal, decimal> asksAscending,
            SortedList<decimal, decimal> bidsDescending,
            int priceDecimals,
            int sizeDecimals,
            int levels = ChecksumLevels)
        {
            if (asksAscending == null) throw new ArgumentNullException(nameof(asksAscending));
            if (bidsDescending == null) throw new ArgumentNullException(nameof(bidsDescending));
            if (priceDecimals < 0) priceDecimals = 0;
            if (sizeDecimals < 0) sizeDecimals = 0;

            uint crc = 0xFFFFFFFF;
            crc = AppendSide(crc, asksAscending, priceDecimals, sizeDecimals, levels);
            crc = AppendSide(crc, bidsDescending, priceDecimals, sizeDecimals, levels);
            return ~crc;
        }

        private static uint AppendSide(
            uint crc, SortedList<decimal, decimal> side, int priceDecimals, int sizeDecimals, int levels)
        {
            int n = Math.Min(levels, side.Count);
            for (int i = 0; i < n; i++)
            {
                crc = AppendToken(crc, side.GetKeyAtIndex(i), priceDecimals);   // price
                crc = AppendToken(crc, side.GetValueAtIndex(i), sizeDecimals);  // quantity
            }
            return crc;
        }

        /// <summary>
        /// Renders the value at the given fixed precision (trailing zeros present) into a stack buffer, strips
        /// the decimal point and leading zeros, then folds the remaining ASCII bytes into the running CRC.
        /// InvariantCulture guarantees a '.' separator and NO group separators. No heap allocation.
        /// </summary>
        private static uint AppendToken(uint crc, decimal value, int decimals)
        {
            Span<char> format = stackalloc char[3];
            format[0] = 'F';
            int fmtLen;
            if (decimals < 10)
            {
                format[1] = (char)('0' + decimals);
                fmtLen = 2;
            }
            else
            {
                format[1] = (char)('0' + decimals / 10);
                format[2] = (char)('0' + decimals % 10);
                fmtLen = 3;
            }

            Span<char> buffer = stackalloc char[64]; // ample for any price/size at realistic precision
            if (value.TryFormat(buffer, out int written, format.Slice(0, fmtLen), CultureInfo.InvariantCulture))
                return AppendChars(crc, buffer.Slice(0, written));

            // Defensive fallback (never expected for live prices/sizes): widen via ToString.
            string s = value.ToString(new string(format.Slice(0, fmtLen)), CultureInfo.InvariantCulture);
            return AppendChars(crc, s.AsSpan());
        }

        private static uint AppendChars(uint crc, ReadOnlySpan<char> s)
        {
            bool seenNonZero = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '.')
                    continue;
                if (!seenNonZero && c == '0')
                    continue;
                seenNonZero = true;
                crc = _crc32Table[(crc ^ (byte)c) & 0xFF] ^ (crc >> 8);
            }
            if (!seenNonZero) // degenerate all-zero value (not expected for live levels) — emit a single '0'
                crc = _crc32Table[(crc ^ (byte)'0') & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
