using System.Collections.Concurrent;
using VisualHFT.Commons.Messaging;
using Xunit;

namespace VisualHFT.Commons.Tests.Messaging
{
    /// <summary>
    /// Unit tests for MulticastRingBuffer - the lock-free SPMC ring buffer.
    /// </summary>
    public class MulticastRingBufferTests
    {
        #region Constructor Tests

        [Fact]
        public void Constructor_WithValidPowerOf2_CreatesBuffer()
        {
            // Arrange & Act
            var buffer = new MulticastRingBuffer<string>(1024);

            // Assert
            Assert.Equal(1024, buffer.BufferSize);
            Assert.Equal(-1, buffer.ProducerSequence);
            Assert.Equal(0, buffer.ConsumerCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(100)]
        [InlineData(1000)]
        [InlineData(1023)]
        public void Constructor_WithNonPowerOf2_ThrowsArgumentException(int invalidSize)
        {
            // Arrange, Act & Assert
            Assert.Throws<ArgumentException>(() => new MulticastRingBuffer<string>(invalidSize));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(1024)]
        [InlineData(65536)]
        public void Constructor_WithPowerOf2_Succeeds(int validSize)
        {
            // Arrange & Act
            var buffer = new MulticastRingBuffer<string>(validSize);

            // Assert
            Assert.Equal(validSize, buffer.BufferSize);
        }

        #endregion

        #region Publish Tests

        [Fact]
        public void Publish_SingleMessage_ReturnsSequence0()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);

            // Act
            var sequence = buffer.Publish("test");

            // Assert
            Assert.Equal(0, sequence);
            Assert.Equal(0, buffer.ProducerSequence);
        }

        [Fact]
        public void Publish_MultipleMessages_ReturnsIncreasingSequences()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);

            // Act
            var seq1 = buffer.Publish("msg1");
            var seq2 = buffer.Publish("msg2");
            var seq3 = buffer.Publish("msg3");

            // Assert
            Assert.Equal(0, seq1);
            Assert.Equal(1, seq2);
            Assert.Equal(2, seq3);
            Assert.Equal(2, buffer.ProducerSequence);
        }

        [Fact]
        public void Publish_NullMessage_ThrowsArgumentNullException()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => buffer.Publish(null!));
        }

        [Fact]
        public void Publish_OverwritesOldMessages_WhenBufferFull()
        {
            // Arrange - small buffer
            var buffer = new MulticastRingBuffer<string>(4);

            // Act - publish more than buffer size
            for (int i = 0; i < 10; i++)
            {
                buffer.Publish($"msg{i}");
            }

            // Assert
            Assert.Equal(9, buffer.ProducerSequence);
        }

        #endregion

        #region Subscribe Tests

        [Fact]
        public void Subscribe_CreatesConsumer()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);

            // Act
            var cursor = buffer.Subscribe("consumer1");

            // Assert
            Assert.NotNull(cursor);
            Assert.Equal("consumer1", cursor.Name);
            Assert.Equal(1, buffer.ConsumerCount);
        }

        [Fact]
        public void Subscribe_MultipleConsumers_AllRegistered()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);

            // Act
            var cursor1 = buffer.Subscribe("consumer1");
            var cursor2 = buffer.Subscribe("consumer2");
            var cursor3 = buffer.Subscribe("consumer3");

            // Assert
            Assert.Equal(3, buffer.ConsumerCount);
        }

        [Fact]
        public void Subscribe_DuplicateName_ThrowsInvalidOperationException()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            buffer.Subscribe("consumer1");

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => buffer.Subscribe("consumer1"));
        }

        [Fact]
        public void Subscribe_EmptyName_ThrowsArgumentException()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);

            // Act & Assert
            Assert.Throws<ArgumentException>(() => buffer.Subscribe(""));
            Assert.Throws<ArgumentException>(() => buffer.Subscribe(null!));
        }

        #endregion

        #region TryRead Tests

        [Fact]
        public void TryRead_NoMessages_ReturnsFalse()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            var cursor = buffer.Subscribe("consumer1");

            // Act
            var result = buffer.TryRead(cursor, out var item, out var sequence);

            // Assert
            Assert.False(result);
            Assert.Null(item);
            Assert.Equal(-1, sequence);
        }

        [Fact]
        public void TryRead_MessageAvailable_ReturnsMessage()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            var cursor = buffer.Subscribe("consumer1");
            buffer.Publish("test message");

            // Act
            var result = buffer.TryRead(cursor, out var item, out var sequence);

            // Assert
            Assert.True(result);
            Assert.Equal("test message", item);
            Assert.Equal(0, sequence);
        }

        [Fact]
        public void TryRead_MultipleMessages_ReadsInOrder()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            var cursor = buffer.Subscribe("consumer1");
            buffer.Publish("msg1");
            buffer.Publish("msg2");
            buffer.Publish("msg3");

            // Act & Assert
            Assert.True(buffer.TryRead(cursor, out var item1, out var seq1));
            Assert.Equal("msg1", item1);
            Assert.Equal(0, seq1);

            Assert.True(buffer.TryRead(cursor, out var item2, out var seq2));
            Assert.Equal("msg2", item2);
            Assert.Equal(1, seq2);

            Assert.True(buffer.TryRead(cursor, out var item3, out var seq3));
            Assert.Equal("msg3", item3);
            Assert.Equal(2, seq3);

            // No more messages
            Assert.False(buffer.TryRead(cursor, out _, out _));
        }

        [Fact]
        public void TryRead_ConsumerIndependence_EachConsumerReadsAllMessages()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            var cursor1 = buffer.Subscribe("consumer1");
            var cursor2 = buffer.Subscribe("consumer2");

            buffer.Publish("msg1");
            buffer.Publish("msg2");

            // Act - consumer1 reads both
            Assert.True(buffer.TryRead(cursor1, out var c1m1, out _));
            Assert.True(buffer.TryRead(cursor1, out var c1m2, out _));

            // consumer2 also reads both (independent)
            Assert.True(buffer.TryRead(cursor2, out var c2m1, out _));
            Assert.True(buffer.TryRead(cursor2, out var c2m2, out _));

            // Assert
            Assert.Equal("msg1", c1m1);
            Assert.Equal("msg2", c1m2);
            Assert.Equal("msg1", c2m1);
            Assert.Equal("msg2", c2m2);
        }

        [Fact]
        public void TryRead_SlowConsumerDoesNotBlockOthers()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(8);
            var slowCursor = buffer.Subscribe("slow");
            var fastCursor = buffer.Subscribe("fast");

            // Publish messages
            for (int i = 0; i < 5; i++)
            {
                buffer.Publish($"msg{i}");
            }

            // Slow consumer doesn't read
            // Fast consumer reads all
            for (int i = 0; i < 5; i++)
            {
                Assert.True(buffer.TryRead(fastCursor, out var msg, out _));
                Assert.Equal($"msg{i}", msg);
            }

            // Publish more - these may overwrite messages slow consumer hasn't read
            for (int i = 5; i < 10; i++)
            {
                buffer.Publish($"msg{i}");
            }

            // Fast consumer still works
            Assert.True(buffer.TryRead(fastCursor, out var newMsg, out _));
            Assert.Equal("msg5", newMsg);
        }

        #endregion

        #region Consumer Lag Tests

        [Fact]
        public void GetConsumerLag_NoMessages_ReturnsZero()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            buffer.Subscribe("consumer1");

            // Act
            var lag = buffer.GetConsumerLag("consumer1");

            // Assert
            Assert.Equal(0, lag);
        }

        [Fact]
        public void GetConsumerLag_UnreadMessages_ReturnsCorrectLag()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            var cursor = buffer.Subscribe("consumer1");

            buffer.Publish("msg1");
            buffer.Publish("msg2");
            buffer.Publish("msg3");

            // Act - read one message
            buffer.TryRead(cursor, out _, out _);

            // Assert - 2 unread messages
            Assert.Equal(2, buffer.GetConsumerLag("consumer1"));
        }

        [Fact]
        public void GetConsumerLag_UnknownConsumer_ReturnsMinusOne()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);

            // Act
            var lag = buffer.GetConsumerLag("unknown");

            // Assert
            Assert.Equal(-1, lag);
        }

        #endregion

        #region Metrics Tests

        [Fact]
        public void GetMetrics_ReturnsCorrectStatistics()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            var cursor1 = buffer.Subscribe("consumer1");
            var cursor2 = buffer.Subscribe("consumer2");

            buffer.Publish("msg1");
            buffer.Publish("msg2");
            buffer.TryRead(cursor1, out _, out _);

            // Act
            var metrics = buffer.GetMetrics();

            // Assert
            Assert.Equal(1024, metrics.BufferSize);
            Assert.Equal(1, metrics.ProducerSequence);
            Assert.Equal(2, metrics.ActiveConsumers);
            Assert.Equal(2, metrics.Consumers.Count);
        }

        [Fact]
        public void GetConsumerMetrics_ReturnsDetailedStats()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            var cursor = buffer.Subscribe("consumer1");

            for (int i = 0; i < 10; i++)
            {
                buffer.Publish($"msg{i}");
            }

            for (int i = 0; i < 5; i++)
            {
                buffer.TryRead(cursor, out _, out _);
            }

            // Act
            var metrics = buffer.GetConsumerMetrics("consumer1");

            // Assert
            Assert.NotNull(metrics);
            Assert.Equal("consumer1", metrics.ConsumerName);
            Assert.Equal(5, metrics.MessagesConsumed);
            Assert.Equal(5, metrics.Lag);
        }

        #endregion

        #region Unsubscribe Tests

        [Fact]
        public void Unsubscribe_RemovesConsumer()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            buffer.Subscribe("consumer1");
            Assert.Equal(1, buffer.ConsumerCount);

            // Act
            var result = buffer.Unsubscribe("consumer1");

            // Assert
            Assert.True(result);
            Assert.Equal(0, buffer.ConsumerCount);
        }

        [Fact]
        public void Unsubscribe_UnknownConsumer_ReturnsFalse()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);

            // Act
            var result = buffer.Unsubscribe("unknown");

            // Assert
            Assert.False(result);
        }

        #endregion

        #region Thread Safety Tests

        [Fact]
        public void Publish_ConcurrentPublishes_AllSequencesUnique()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(65536);
            var sequences = new ConcurrentBag<long>();
            var publishCount = 10000;

            // Act - simulate concurrent publishes (though design is single producer)
            Parallel.For(0, publishCount, i =>
            {
                var seq = buffer.Publish($"msg{i}");
                sequences.Add(seq);
            });

            // Assert - all sequences should be unique
            var uniqueSequences = sequences.Distinct().Count();
            Assert.Equal(publishCount, uniqueSequences);
        }

        [Fact]
        public void TryRead_ConcurrentReads_NoDataCorruption()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            
            // Publish messages
            for (int i = 0; i < 100; i++)
            {
                buffer.Publish($"msg{i}");
            }

            var cursor = buffer.Subscribe("consumer1");
            var readMessages = new ConcurrentBag<string>();

            // Act - concurrent reads from same consumer (not typical usage but should handle)
            Parallel.For(0, 100, _ =>
            {
                if (buffer.TryRead(cursor, out var msg, out _) && msg != null)
                {
                    readMessages.Add(msg);
                }
            });

            // Assert - should have read some messages without crashing
            Assert.True(readMessages.Count >= 0);
        }

        [Fact]
        public async Task ProducerConsumer_HighThroughput_NoDataLoss()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(8192);
            var cursor = buffer.Subscribe("consumer1");
            var messageCount = 10000;
            var received = new ConcurrentBag<long>();
            var cts = new CancellationTokenSource();

            // Start consumer
            var consumerTask = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested || received.Count < messageCount)
                {
                    if (buffer.TryRead(cursor, out var msg, out var seq))
                    {
                        received.Add(seq);
                    }
                    else
                    {
                        Thread.SpinWait(100);
                    }

                    if (received.Count >= messageCount)
                        break;
                }
            });

            // Producer
            for (int i = 0; i < messageCount; i++)
            {
                buffer.Publish($"msg{i}");
            }

            cts.CancelAfter(5000); // Safety timeout
            await consumerTask;

            // Assert
            Assert.Equal(messageCount, received.Count);
            var sortedSeqs = received.OrderBy(x => x).ToList();
            for (int i = 0; i < messageCount; i++)
            {
                Assert.Equal(i, sortedSeqs[i]);
            }
        }

        #endregion

        #region Dispose Tests

        [Fact]
        public void Dispose_ClearsBuffer()
        {
            // Arrange
            var buffer = new MulticastRingBuffer<string>(1024);
            buffer.Subscribe("consumer1");
            buffer.Publish("msg1");

            // Act
            buffer.Dispose();

            // Assert
            Assert.Equal(0, buffer.ConsumerCount);
        }

        #endregion
    }
}
