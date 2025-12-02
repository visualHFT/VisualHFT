using System.Collections.Concurrent;
using VisualHFT.Commons.Messaging;
using VisualHFT.Helpers;
using VisualHFT.Model;
using Xunit;

namespace VisualHFT.Commons.Tests.Messaging
{
    /// <summary>
    /// Integration tests for HelperOrderBook with the multicast ring buffer architecture.
    /// Tests backward compatibility and new functionality.
    /// </summary>
    public class HelperOrderBookIntegrationTests
    {
        #region Backward Compatibility Tests

        [Fact]
        public void Subscribe_LegacyAPI_ReceivesMessages()
        {
            // Arrange
            var helper = CreateTestHelper();
            var receivedMessages = new ConcurrentBag<OrderBook>();
            var resetEvent = new ManualResetEventSlim(false);

            Action<OrderBook> subscriber = book =>
            {
                receivedMessages.Add(book);
                if (receivedMessages.Count >= 1)
                    resetEvent.Set();
            };

            // Act
            helper.Subscribe(subscriber);
            helper.UpdateData(CreateTestOrderBook());

            // Assert - Wait for message with timeout
            Assert.True(resetEvent.Wait(TimeSpan.FromSeconds(5)), "Subscriber should receive message");
            Assert.Single(receivedMessages);
            Assert.Equal("BTCUSD", receivedMessages.First().Symbol);

            // Cleanup
            helper.Unsubscribe(subscriber);
        }

        [Fact]
        public void Unsubscribe_LegacyAPI_StopsReceivingMessages()
        {
            // Arrange
            var helper = CreateTestHelper();
            var receivedCount = 0;

            Action<OrderBook> subscriber = _ => Interlocked.Increment(ref receivedCount);

            helper.Subscribe(subscriber);
            helper.UpdateData(CreateTestOrderBook());
            Thread.Sleep(100); // Allow message to be processed

            // Act
            helper.Unsubscribe(subscriber);
            Thread.Sleep(100);
            var countAfterUnsubscribe = receivedCount;
            
            helper.UpdateData(CreateTestOrderBook());
            Thread.Sleep(100);

            // Assert - Count should not increase after unsubscribe
            Assert.Equal(countAfterUnsubscribe, receivedCount);
        }

        [Fact]
        public void UpdateData_MultipleBooks_AllProcessed()
        {
            // Arrange
            var helper = CreateTestHelper();
            var receivedMessages = new ConcurrentBag<string>();
            var resetEvent = new ManualResetEventSlim(false);

            Action<OrderBook> subscriber = book =>
            {
                receivedMessages.Add(book.Symbol);
                if (receivedMessages.Count >= 3)
                    resetEvent.Set();
            };

            var books = new[]
            {
                CreateTestOrderBook("BTCUSD"),
                CreateTestOrderBook("ETHUSD"),
                CreateTestOrderBook("LTCUSD")
            };

            // Act
            helper.Subscribe(subscriber);
            helper.UpdateData(books);

            // Assert
            Assert.True(resetEvent.Wait(TimeSpan.FromSeconds(5)), "Should receive all messages");
            Assert.Contains("BTCUSD", receivedMessages);
            Assert.Contains("ETHUSD", receivedMessages);
            Assert.Contains("LTCUSD", receivedMessages);

            // Cleanup
            helper.Unsubscribe(subscriber);
        }

        #endregion

        #region Modern API Tests

        [Fact]
        public void Subscribe_ModernAPI_ReceivesImmutableOrderBooks()
        {
            // Arrange
            var helper = CreateTestHelper();
            var receivedMessages = new ConcurrentBag<ImmutableOrderBook>();
            var resetEvent = new ManualResetEventSlim(false);

            Action<ImmutableOrderBook> subscriber = book =>
            {
                receivedMessages.Add(book);
                if (receivedMessages.Count >= 1)
                    resetEvent.Set();
            };

            // Act
            helper.Subscribe(subscriber);
            helper.UpdateData(CreateTestOrderBook());

            // Assert
            Assert.True(resetEvent.Wait(TimeSpan.FromSeconds(5)), "Subscriber should receive message");
            Assert.Single(receivedMessages);
            Assert.Equal("BTCUSD", receivedMessages.First().Symbol);

            // Cleanup
            helper.Unsubscribe(subscriber);
        }

        [Fact]
        public void Subscribe_ModernAPI_ImmutableBookHasCorrectData()
        {
            // Arrange
            var helper = CreateTestHelper();
            ImmutableOrderBook? receivedBook = null;
            var resetEvent = new ManualResetEventSlim(false);

            Action<ImmutableOrderBook> subscriber = book =>
            {
                receivedBook = book;
                resetEvent.Set();
            };

            var orderBook = CreateTestOrderBook();

            // Act
            helper.Subscribe(subscriber);
            helper.UpdateData(orderBook);

            // Assert
            Assert.True(resetEvent.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(receivedBook);
            Assert.Equal(orderBook.Symbol, receivedBook.Symbol);
            Assert.Equal(orderBook.ProviderID, receivedBook.ProviderID);
            Assert.True(receivedBook.Bids.Count > 0);
            Assert.True(receivedBook.Asks.Count > 0);

            // Cleanup
            helper.Unsubscribe(subscriber);
        }

        #endregion

        #region Dual API Tests

        [Fact]
        public void Subscribe_BothAPIs_BothReceiveMessages()
        {
            // Arrange
            var helper = CreateTestHelper();
            var legacyReceived = new ConcurrentBag<OrderBook>();
            var modernReceived = new ConcurrentBag<ImmutableOrderBook>();
            var resetEvent = new ManualResetEventSlim(false);

            Action<OrderBook> legacySubscriber = book =>
            {
                legacyReceived.Add(book);
                if (legacyReceived.Count >= 1 && modernReceived.Count >= 1)
                    resetEvent.Set();
            };

            Action<ImmutableOrderBook> modernSubscriber = book =>
            {
                modernReceived.Add(book);
                if (legacyReceived.Count >= 1 && modernReceived.Count >= 1)
                    resetEvent.Set();
            };

            // Act
            helper.Subscribe(legacySubscriber);
            helper.Subscribe(modernSubscriber);
            helper.UpdateData(CreateTestOrderBook());

            // Assert
            Assert.True(resetEvent.Wait(TimeSpan.FromSeconds(5)), "Both subscribers should receive message");
            Assert.Single(legacyReceived);
            Assert.Single(modernReceived);

            // Cleanup
            helper.Unsubscribe(legacySubscriber);
            helper.Unsubscribe(modernSubscriber);
        }

        #endregion

        #region Multiple Subscriber Tests

        [Fact]
        public void Subscribe_MultipleConsumers_AllReceiveMessages()
        {
            // Arrange
            var helper = CreateTestHelper();
            var received1 = new ConcurrentBag<string>();
            var received2 = new ConcurrentBag<string>();
            var received3 = new ConcurrentBag<string>();
            var resetEvent = new CountdownEvent(3);

            Action<OrderBook> subscriber1 = book =>
            {
                received1.Add(book.Symbol);
                if (received1.Count >= 1)
                    resetEvent.Signal();
            };

            Action<OrderBook> subscriber2 = book =>
            {
                received2.Add(book.Symbol);
                if (received2.Count >= 1)
                    resetEvent.Signal();
            };

            Action<OrderBook> subscriber3 = book =>
            {
                received3.Add(book.Symbol);
                if (received3.Count >= 1)
                    resetEvent.Signal();
            };

            // Act
            helper.Subscribe(subscriber1);
            helper.Subscribe(subscriber2);
            helper.Subscribe(subscriber3);
            helper.UpdateData(CreateTestOrderBook("BTCUSD"));

            // Assert
            Assert.True(resetEvent.Wait(TimeSpan.FromSeconds(5)), "All subscribers should receive message");
            Assert.Single(received1);
            Assert.Single(received2);
            Assert.Single(received3);

            // Cleanup
            helper.Unsubscribe(subscriber1);
            helper.Unsubscribe(subscriber2);
            helper.Unsubscribe(subscriber3);
        }

        #endregion

        #region Metrics Tests

        [Fact]
        public void GetMetrics_ReturnsValidStatistics()
        {
            // Arrange
            var helper = CreateTestHelper();
            Action<OrderBook> subscriber = _ => { };

            helper.Subscribe(subscriber);
            helper.UpdateData(CreateTestOrderBook());
            Thread.Sleep(100);

            // Act
            var metrics = helper.GetMetrics();

            // Assert
            Assert.NotNull(metrics);
            Assert.True(metrics.ProducerSequence >= 0);
            Assert.True(metrics.ActiveConsumers >= 1);

            // Cleanup
            helper.Unsubscribe(subscriber);
        }

        [Fact]
        public void TotalPublished_IncrementsOnUpdateData()
        {
            // Arrange
            var helper = CreateTestHelper();

            // Act
            var initialCount = helper.TotalPublished;
            helper.UpdateData(CreateTestOrderBook());
            helper.UpdateData(CreateTestOrderBook());
            helper.UpdateData(CreateTestOrderBook());

            // Assert
            Assert.Equal(initialCount + 3, helper.TotalPublished);
        }

        [Fact]
        public void SubscriberCounts_AreAccurate()
        {
            // Arrange
            var helper = CreateTestHelper();
            Action<OrderBook> legacySub = _ => { };
            Action<ImmutableOrderBook> modernSub = _ => { };

            // Act & Assert - Initial state
            Assert.Equal(0, helper.LegacySubscriberCount);
            Assert.Equal(0, helper.ModernSubscriberCount);
            Assert.Equal(0, helper.TotalSubscriberCount);

            // Add subscribers
            helper.Subscribe(legacySub);
            Assert.Equal(1, helper.LegacySubscriberCount);
            Assert.Equal(1, helper.TotalSubscriberCount);

            helper.Subscribe(modernSub);
            Assert.Equal(1, helper.ModernSubscriberCount);
            Assert.Equal(2, helper.TotalSubscriberCount);

            // Cleanup
            helper.Unsubscribe(legacySub);
            helper.Unsubscribe(modernSub);
        }

        #endregion

        #region Reset Tests

        [Fact]
        public void Reset_ClearsAllSubscribers()
        {
            // Arrange
            var helper = CreateTestHelper();
            Action<OrderBook> subscriber = _ => { };
            helper.Subscribe(subscriber);
            Assert.Equal(1, helper.TotalSubscriberCount);

            // Act
            helper.Reset();

            // Assert
            Assert.Equal(0, helper.TotalSubscriberCount);
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public void Subscribe_NullLegacySubscriber_ThrowsArgumentNullException()
        {
            // Arrange
            var helper = CreateTestHelper();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => helper.Subscribe((Action<OrderBook>)null!));
        }

        [Fact]
        public void Subscribe_NullModernSubscriber_ThrowsArgumentNullException()
        {
            // Arrange
            var helper = CreateTestHelper();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => helper.Subscribe((Action<ImmutableOrderBook>)null!));
        }

        #endregion

        #region Helper Methods

        private static HelperOrderBook CreateTestHelper()
        {
            // Use the singleton instance - tests should be isolated
            // Note: In production, HelperOrderBook.Instance is a singleton
            // For testing, we use the same instance but reset between tests
            var helper = HelperOrderBook.Instance;
            helper.Reset(); // Clear any existing state
            return helper;
        }

        private static OrderBook CreateTestOrderBook(string symbol = "BTCUSD")
        {
            var orderBook = new OrderBook(symbol, 2, 10);
            orderBook.ProviderID = 1;
            orderBook.ProviderName = "TestProvider";

            var bids = new[]
            {
                new BookItem { Price = 100.0, Size = 10.0, IsBid = true },
                new BookItem { Price = 99.0, Size = 20.0, IsBid = true }
            };

            var asks = new[]
            {
                new BookItem { Price = 101.0, Size = 15.0, IsBid = false },
                new BookItem { Price = 102.0, Size = 25.0, IsBid = false }
            };

            orderBook.LoadData(asks, bids);
            return orderBook;
        }

        #endregion
    }
}
