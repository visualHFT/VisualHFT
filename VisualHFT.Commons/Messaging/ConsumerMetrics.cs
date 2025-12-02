namespace VisualHFT.Commons.Messaging
{
    /// <summary>
    /// Consumer metrics for monitoring ring buffer consumer health and performance.
    /// Used to track individual consumer lag, throughput, and potential issues.
    /// </summary>
    public class ConsumerMetrics
    {
        /// <summary>
        /// Unique name identifying this consumer.
        /// </summary>
        public string ConsumerName { get; init; } = string.Empty;

        /// <summary>
        /// Current sequence number being read by this consumer.
        /// </summary>
        public long CurrentSequence { get; init; }

        /// <summary>
        /// Latest sequence number published by the producer.
        /// </summary>
        public long ProducerSequence { get; init; }

        /// <summary>
        /// Number of messages this consumer is behind the producer.
        /// High lag indicates a slow consumer that may miss messages.
        /// </summary>
        public long Lag => ProducerSequence - CurrentSequence;

        /// <summary>
        /// Percentage of buffer capacity being used by this consumer's lag.
        /// Values approaching 100% indicate the consumer is at risk of being overwritten.
        /// </summary>
        public double LagPercentage { get; init; }

        /// <summary>
        /// Total number of messages successfully consumed by this consumer.
        /// </summary>
        public long MessagesConsumed { get; init; }

        /// <summary>
        /// Number of messages that were overwritten before this consumer could read them.
        /// Non-zero values indicate the consumer is too slow for the message rate.
        /// </summary>
        public long MessagesLost { get; init; }

        /// <summary>
        /// Indicates if this consumer is healthy (lag is within acceptable bounds).
        /// </summary>
        public bool IsHealthy => LagPercentage < 50.0;

        /// <summary>
        /// Indicates if this consumer is in critical state (about to lose messages).
        /// </summary>
        public bool IsCritical => LagPercentage >= 90.0;

        /// <summary>
        /// Timestamp when these metrics were captured.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        public override string ToString()
        {
            return $"Consumer '{ConsumerName}': Seq={CurrentSequence}, Lag={Lag} ({LagPercentage:F1}%), " +
                   $"Consumed={MessagesConsumed}, Lost={MessagesLost}, " +
                   $"Status={(IsCritical ? "CRITICAL" : IsHealthy ? "Healthy" : "Warning")}";
        }
    }

    /// <summary>
    /// Aggregated metrics for the entire ring buffer.
    /// Provides a comprehensive view of producer and all consumer states.
    /// </summary>
    public class RingBufferMetrics
    {
        /// <summary>
        /// Size of the ring buffer (must be power of 2).
        /// </summary>
        public int BufferSize { get; init; }

        /// <summary>
        /// Current producer sequence number (total messages published).
        /// </summary>
        public long ProducerSequence { get; init; }

        /// <summary>
        /// Total number of messages published since buffer creation.
        /// </summary>
        public long TotalMessagesPublished { get; init; }

        /// <summary>
        /// Total number of complete buffer wraps (overwrites).
        /// </summary>
        public long BufferWraps => TotalMessagesPublished / BufferSize;

        /// <summary>
        /// Number of active consumers subscribed to this buffer.
        /// </summary>
        public int ActiveConsumers { get; init; }

        /// <summary>
        /// Metrics for each individual consumer.
        /// </summary>
        public IReadOnlyList<ConsumerMetrics> Consumers { get; init; } = Array.Empty<ConsumerMetrics>();

        /// <summary>
        /// The slowest consumer (highest lag).
        /// </summary>
        public ConsumerMetrics? SlowestConsumer => Consumers.MaxBy(c => c.Lag);

        /// <summary>
        /// Overall buffer health status.
        /// </summary>
        public bool IsHealthy => Consumers.Count == 0 || Consumers.All(c => c.IsHealthy);

        /// <summary>
        /// Indicates if any consumer is in critical state.
        /// </summary>
        public bool HasCriticalConsumers => Consumers.Any(c => c.IsCritical);

        /// <summary>
        /// Timestamp when these metrics were captured.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        public override string ToString()
        {
            var status = HasCriticalConsumers ? "CRITICAL" : IsHealthy ? "Healthy" : "Warning";
            return $"RingBuffer[{BufferSize}]: ProducerSeq={ProducerSequence}, Consumers={ActiveConsumers}, " +
                   $"Wraps={BufferWraps}, Status={status}";
        }
    }
}
