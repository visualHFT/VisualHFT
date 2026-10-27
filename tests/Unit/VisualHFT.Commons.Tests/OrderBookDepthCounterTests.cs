using System;
using System.Linq;
using VisualHFT.Model;
using Xunit;

namespace VisualHFT.Commons.Tests
{
    /// <summary>
    /// The added-levels counter and the added volume count only levels that stay in the book.
    ///
    /// A side at full depth (MaxDepth levels) that receives a worse price adds the level and cuts it
    /// at once, so the book a reader sees does not change. The order-to-trade study reads these
    /// counters and tells users that only price levels inside the configured depth are counted; a
    /// level that is cut on arrival is therefore not counted. A better price that
    /// pushes the last level out is counted as added, and the level it pushes out is not counted as
    /// deleted. A book with MaxDepth 0 keeps today's counting.
    /// </summary>
    public class OrderBookDepthCounterTests
    {
        private static readonly DateTime _time = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        private static string Fmt(OrderBook book, bool isBid) =>
            string.Join(";", (isBid ? book.Bids : book.Asks).Select(x => $"{x.Price}:{x.Size}"));

        // Three levels per side at MaxDepth 3: bids 100, 99, 98 and asks 101, 102, 103, size 1 each.
        private static OrderBook FullBook(bool isBid)
        {
            var book = new OrderBook("DEPTH_COUNTER", 2, 3) { SizeDecimalPlaces = 0 };
            foreach (var p in isBid ? new[] { 100, 99, 98 } : new[] { 101, 102, 103 })
                book.AddOrUpdateLevel(isBid, null, p, 1, _time, _time);
            return book;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WorsePriceAtFullDepth_LeavesTheBookAndTheCountersUnchanged(bool isBid)
        {
            var book = FullBook(isBid);
            string sideBefore = Fmt(book, isBid);
            var countersBefore = book.GetCounters();
            var volumeBefore = book.GetCountersVolume();
            Assert.Equal(3, countersBefore.added);
            Assert.Equal(3, volumeBefore.addedVol);

            book.AddOrUpdateLevel(isBid, null, isBid ? 97 : 104, 4, _time, _time);

            Assert.Equal(sideBefore, Fmt(book, isBid));
            Assert.Equal(countersBefore, book.GetCounters());
            Assert.Equal(volumeBefore, book.GetCountersVolume());
            book.Dispose();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BetterPriceAtFullDepth_IsCountedAsAdded_AndThePushedOutLevelIsNotCountedAsDeleted(bool isBid)
        {
            var book = FullBook(isBid);

            book.AddOrUpdateLevel(isBid, null, isBid ? 101 : 100, 4, _time, _time);

            Assert.Equal(isBid ? "101:4;100:1;99:1" : "100:4;101:1;102:1", Fmt(book, isBid));
            Assert.Equal((4L, 0L, 0L), book.GetCounters());
            Assert.Equal(7, book.GetCountersVolume().addedVol);
            Assert.Equal(0, book.GetCountersVolume().deletedVol);
            book.Dispose();
        }

        [Fact]
        public void WorsePriceOnASideBelowFullDepth_IsCountedAsAdded()
        {
            var book = new OrderBook("DEPTH_COUNTER_ROOM", 2, 3) { SizeDecimalPlaces = 0 };
            book.AddOrUpdateLevel(true, null, 100, 1, _time, _time);

            book.AddOrUpdateLevel(true, null, 90, 2, _time, _time);

            Assert.Equal("100:1;90:2", Fmt(book, true));
            Assert.Equal(2, book.GetCounters().added);
            Assert.Equal(3, book.GetCountersVolume().addedVol);
            book.Dispose();
        }

        [Fact]
        public void MaxDepthZero_KeepsCountingTheAdd()
        {
            // A book from the default constructor has MaxDepth 0, and an add leaves its side empty;
            // its counting is unchanged.
            var book = new OrderBook { SizeDecimalPlaces = 0 };

            book.AddOrUpdateLevel(true, null, 100, 1, _time, _time);

            Assert.Equal(0, book.MaxDepth);
            Assert.Equal(1, book.GetCounters().added);
            book.Dispose();
        }
    }
}
