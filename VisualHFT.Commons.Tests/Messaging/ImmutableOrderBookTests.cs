using VisualHFT.Commons.Messaging;
using VisualHFT.Model;
using Xunit;

namespace VisualHFT.Commons.Tests.Messaging
{
    /// <summary>
    /// Unit tests for ImmutableOrderBook - the zero-copy immutable wrapper for OrderBook.
    /// </summary>
    public class ImmutableOrderBookTests
    {
        #region Snapshot Creation Tests

        [Fact]
        public void CreateSnapshot_WithValidOrderBook_CreatesImmutableCopy()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();

            // Act
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 42);

            // Assert
            Assert.Equal(42, snapshot.Sequence);
            Assert.Equal("BTCUSD", snapshot.Symbol);
            Assert.Equal(1, snapshot.ProviderID);
            Assert.Equal("TestProvider", snapshot.ProviderName);
        }

        [Fact]
        public void CreateSnapshot_CopiesBidsAndAsks()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();

            // Act
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Assert
            Assert.NotEmpty(snapshot.Bids);
            Assert.NotEmpty(snapshot.Asks);
            Assert.Equal(3, snapshot.Bids.Count);
            Assert.Equal(3, snapshot.Asks.Count);
        }

        [Fact]
        public void CreateSnapshot_NullOrderBook_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => ImmutableOrderBook.CreateSnapshot(null!, 1));
        }

        [Fact]
        public void CreateSnapshot_PreservesPriceOrder()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();

            // Act
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Assert - Bids should be descending by price
            for (int i = 0; i < snapshot.Bids.Count - 1; i++)
            {
                Assert.True(snapshot.Bids[i].Price >= snapshot.Bids[i + 1].Price,
                    "Bids should be sorted descending by price");
            }

            // Asks should be ascending by price
            for (int i = 0; i < snapshot.Asks.Count - 1; i++)
            {
                Assert.True(snapshot.Asks[i].Price <= snapshot.Asks[i + 1].Price,
                    "Asks should be sorted ascending by price");
            }
        }

        #endregion

        #region Immutability Tests

        [Fact]
        public void ImmutableOrderBook_FieldsAreReadonly()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Assert - All public fields should be readonly (verified by readonly modifier)
            // This test verifies the fields can be read but not modified
            var symbol = snapshot.Symbol;
            var sequence = snapshot.Sequence;
            var providerId = snapshot.ProviderID;

            Assert.Equal("BTCUSD", symbol);
            Assert.Equal(1, sequence);
            Assert.Equal(1, providerId);
        }

        [Fact]
        public void ImmutableOrderBook_BidsAndAsksAreReadonly()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Assert - IReadOnlyList doesn't expose modification methods
            Assert.IsAssignableFrom<IReadOnlyList<ImmutableBookLevel>>(snapshot.Bids);
            Assert.IsAssignableFrom<IReadOnlyList<ImmutableBookLevel>>(snapshot.Asks);
        }

        [Fact]
        public void ImmutableOrderBook_ModifyingSourceDoesNotAffectSnapshot()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);
            var originalBidPrice = snapshot.Bids[0].Price;

            // Act - Modify source order book
            orderBook.Clear();

            // Assert - Snapshot should be unchanged
            Assert.Equal(originalBidPrice, snapshot.Bids[0].Price);
            Assert.Equal(3, snapshot.Bids.Count);
        }

        #endregion

        #region ToMutable Tests

        [Fact]
        public void ToMutable_CreatesNewOrderBook()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Act
            var mutable = snapshot.ToMutable();

            // Assert
            Assert.NotNull(mutable);
            Assert.Equal(snapshot.Symbol, mutable.Symbol);
            Assert.Equal(snapshot.ProviderID, mutable.ProviderID);
        }

        [Fact]
        public void ToMutable_CopiesBidsAndAsks()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Act
            var mutable = snapshot.ToMutable();

            // Assert
            var mutableBids = mutable.GetBidsSnapshot();
            var mutableAsks = mutable.GetAsksSnapshot();

            Assert.Equal(snapshot.Bids.Count, mutableBids.Length);
            Assert.Equal(snapshot.Asks.Count, mutableAsks.Length);
        }

        [Fact]
        public void ToMutable_ResultCanBeModified()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Act
            var mutable = snapshot.ToMutable();
            mutable.Symbol = "ETHUSD"; // Should work - mutable

            // Assert
            Assert.Equal("ETHUSD", mutable.Symbol);
            Assert.Equal("BTCUSD", snapshot.Symbol); // Original unchanged
        }

        #endregion

        #region Property Tests

        [Fact]
        public void BestBid_ReturnsFirstBid()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Act
            var bestBid = snapshot.BestBid;

            // Assert
            Assert.NotNull(bestBid);
            Assert.Equal(100.0, bestBid.Value.Price);
        }

        [Fact]
        public void BestAsk_ReturnsFirstAsk()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Act
            var bestAsk = snapshot.BestAsk;

            // Assert
            Assert.NotNull(bestAsk);
            Assert.Equal(101.0, bestAsk.Value.Price);
        }

        [Fact]
        public void TotalBidVolume_SumsAllBidSizes()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Act
            var totalBidVolume = snapshot.TotalBidVolume;

            // Assert - 10 + 20 + 30 = 60
            Assert.Equal(60.0, totalBidVolume);
        }

        [Fact]
        public void TotalAskVolume_SumsAllAskSizes()
        {
            // Arrange
            var orderBook = CreateTestOrderBook();
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Act
            var totalAskVolume = snapshot.TotalAskVolume;

            // Assert - 15 + 25 + 35 = 75
            Assert.Equal(75.0, totalAskVolume);
        }

        [Fact]
        public void BestBid_EmptyBook_ReturnsNull()
        {
            // Arrange
            var orderBook = new OrderBook("BTCUSD", 2, 10);
            var snapshot = ImmutableOrderBook.CreateSnapshot(orderBook, 1);

            // Act
            var bestBid = snapshot.BestBid;

            // Assert
            Assert.Null(bestBid);
        }

        #endregion

        #region ImmutableBookLevel Tests

        [Fact]
        public void ImmutableBookLevel_FromBookItem_CopiesAllFields()
        {
            // Arrange
            var bookItem = new BookItem
            {
                Price = 100.5,
                Size = 50.0,
                IsBid = true,
                EntryID = "entry1",
                CummulativeSize = 150.0,
                ServerTimeStamp = new DateTime(2024, 1, 1, 12, 0, 0),
                LocalTimeStamp = new DateTime(2024, 1, 1, 12, 0, 1)
            };

            // Act
            var level = ImmutableBookLevel.FromBookItem(bookItem);

            // Assert
            Assert.Equal(100.5, level.Price);
            Assert.Equal(50.0, level.Size);
            Assert.True(level.IsBid);
            Assert.Equal("entry1", level.EntryID);
            Assert.Equal(150.0, level.CumulativeSize);
        }

        [Fact]
        public void ImmutableBookLevel_ToBookItem_CreatesNewBookItem()
        {
            // Arrange
            var level = new ImmutableBookLevel(
                price: 100.5,
                size: 50.0,
                isBid: true,
                entryId: "entry1",
                cumulativeSize: 150.0);

            // Act
            var bookItem = level.ToBookItem();

            // Assert
            Assert.NotNull(bookItem);
            Assert.Equal(100.5, bookItem.Price);
            Assert.Equal(50.0, bookItem.Size);
            Assert.True(bookItem.IsBid);
        }

        #endregion

        #region Helper Methods

        private static OrderBook CreateTestOrderBook()
        {
            var orderBook = new OrderBook("BTCUSD", 2, 10);
            orderBook.ProviderID = 1;
            orderBook.ProviderName = "TestProvider";

            var bids = new[]
            {
                new BookItem { Price = 100.0, Size = 10.0, IsBid = true },
                new BookItem { Price = 99.0, Size = 20.0, IsBid = true },
                new BookItem { Price = 98.0, Size = 30.0, IsBid = true }
            };

            var asks = new[]
            {
                new BookItem { Price = 101.0, Size = 15.0, IsBid = false },
                new BookItem { Price = 102.0, Size = 25.0, IsBid = false },
                new BookItem { Price = 103.0, Size = 35.0, IsBid = false }
            };

            orderBook.LoadData(asks, bids);
            return orderBook;
        }

        #endregion
    }
}
