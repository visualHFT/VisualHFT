using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using VisualHFT.Helpers;

public class HelperCustomQueue<T> : IDisposable
{
    private readonly Channel<T> _channel;
    private readonly ChannelWriter<T> _writer;
    private readonly ChannelReader<T> _reader;
    private readonly string _queueName;
    private readonly Action<T> _actionOnRead;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _token;
    private Task _taskConsumer;
    private bool _isPaused;
    private bool _isRunning;
    private bool _disposed;
    private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
    private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    // Depth thresholds for unbounded queue health monitoring
    private readonly int _healthyThreshold;
    private readonly int _warningThreshold;
    private readonly bool _isBounded;

    #region Ultra-Lean Performance Monitoring

    private readonly bool _monitorHealth = false; // Set to true to enable performance monitoring

    // Separate counters into different cache lines to prevent false sharing
    // Each cache line is 64 bytes, we pad with 56 bytes after each 8-byte counter

    // Producer counters (cache line 1)
    private long _totalMessagesAdded = 0;
    private long _pad1a, _pad2a, _pad3a, _pad4a, _pad5a, _pad6a, _pad7a;

    // Consumer counters (cache line 2) - only track when monitoring enabled
    private long _totalMessagesProcessed = 0;
    private long _totalProcessingTimeTicks = 0;
    private long _currentDepth = 0;
    private long _pad1b, _pad2b, _pad3b, _pad4b, _pad5b;
    // Sliding window for rate calculation (lock-free)
    private long _lastReportTicks = DateTime.UtcNow.Ticks;
    private long _lastReportAdded = 0;
    private long _lastReportProcessed = 0;

    private long _lastReportTimestamp = Stopwatch.GetTimestamp();
    private static readonly long REPORT_INTERVAL_TICKS = Stopwatch.Frequency * 5; // 5 seconds

    // Cached timestamp for monitoring to avoid Stopwatch overhead
    private long _cachedTimestamp = 0;


    // Public read-only properties for monitoring
    public string QueueName => _queueName;
    public int Count => CurrentQueueDepth;   // Implement the IQuantifiable interface
    public long TotalMessagesAdded => Volatile.Read(ref _totalMessagesAdded);
    public long TotalMessagesProcessed => Volatile.Read(ref _totalMessagesProcessed);
    public int CurrentQueueDepth => _disposed ? 0 : (int)Volatile.Read(ref _currentDepth);
    public bool IsBounded => _isBounded;
    public int HealthyThreshold => _healthyThreshold;
    public int WarningThreshold => _warningThreshold;
    public double AverageProcessingTimeMicroseconds
    {
        get
        {
            var processed = Volatile.Read(ref _totalMessagesProcessed);
            var totalTime = Volatile.Read(ref _totalProcessingTimeTicks);
            return processed > 0 ? (totalTime / (double)processed) / 10.0 : 0; // Convert ticks to microseconds
        }
    }

    #endregion

    public HelperCustomQueue(string queueName, Action<T> actionOnRead, Action<Exception> onError = null, bool monitorHealth = false, int healthyThreshold = 1000, int warningThreshold = 10000)
    {
        _queueName = queueName;
        _actionOnRead = actionOnRead;
        _onError = onError;

        // Create unbounded channel with optimal settings for HFT
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,                     // Optimize for single consumer
            SingleWriter = false,                    // Multiple producers
            AllowSynchronousContinuations = false    // Prevent thread hopping
        };
        _channel = Channel.CreateUnbounded<T>(options);
        _writer = _channel.Writer;
        _reader = _channel.Reader;

        _isBounded = false;  // This is an unbounded queue
        _healthyThreshold = healthyThreshold;
        _warningThreshold = warningThreshold;
        _cts = new CancellationTokenSource();
        _token = _cts.Token;
        _monitorHealth = monitorHealth;
        if (_queueName == null)
            _queueName = GetInstantiator();


        Start();
    }
    private static string GetInstantiator()
    {
        try
        {
            var stackTrace = new StackTrace();
            var frames = stackTrace.GetFrames();

            // Skip the first frame (this method) and the constructor frame
            // Look for the first frame that's not in this class
            for (int i = 2; i < frames.Length; i++)
            {
                var method = frames[i].GetMethod();
                if (method?.DeclaringType != null && method.DeclaringType != typeof(HelperCustomQueue<T>))
                {
                    var declaringType = method.DeclaringType;
                    return $"{declaringType.Namespace}.{declaringType.Name}";
                }
            }

            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    public void Add(T item)
    {
        if (Volatile.Read(ref _disposed) || _reader.Completion.IsCompleted)
            return;
        if (item == null)
            return;

        // Only increment counters if write succeeds
        if (_writer.TryWrite(item)) // Lock-free, non-blocking write
        {
            if (_monitorHealth)
                Interlocked.Increment(ref _totalMessagesAdded); // Ultra-fast atomic increment

            if (_monitorHealth)
            {
                var depth = Interlocked.Increment(ref _currentDepth);
                // No need to signal - Channel handles this internally
            }
        }

        // Periodic reporting (minimal overhead check)
        if (_monitorHealth)
            CheckAndReportPerformance();
    }

    // Change field declarations (no need to change to volatile keyword)
    // Instead, use Volatile.Read/Write at access points

    public void PauseConsumer()
    {
        if (Volatile.Read(ref _disposed))
            return;
        Volatile.Write(ref _isPaused, true);
        _pauseEvent.Reset();
    }

    public void ResumeConsumer()
    {
        if (Volatile.Read(ref _disposed))
            return;
        Volatile.Write(ref _isPaused, false);
        _pauseEvent.Set();
    }

    public void Stop()
    {
        Volatile.Write(ref _isRunning, false);

        // Release paused consumer first to avoid blocking
        _pauseEvent.Set();

        try
        {
            _cts.Cancel(); // Will wake up WaitToReadAsync
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        try
        {
            _writer.Complete(); // Signal no more items will be written
        }
        catch (ChannelClosedException)
        {
            // Already completed
        }
    }

    public void Clear()
    {
        if (_disposed)
            return;

        // Drain all items without processing them
        while (_reader.TryRead(out _))
        {
            if (_monitorHealth)
                Interlocked.Decrement(ref _currentDepth); // Track depth
        }
    }

    private void Start()
    {
        _isRunning = true;
        _taskConsumer = Task.Run(RunConsumer);
    }

    private async Task RunConsumer()
    {
        var startTimestamp = 0L;

        try
        {
            while (Volatile.Read(ref _isRunning) && !Volatile.Read(ref _disposed))
            {
                // Check if paused - wait for resume signal (already on background thread)
                if (Volatile.Read(ref _isPaused))
                {
                    try
                    {
                        _pauseEvent.Wait(_token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when stopping - exit gracefully
                        break;
                    }
                    continue;
                }


                // Wait for items to be available - Channel's native async waiting
                // Should handle the cancellation gracefully
                try
                {
                    if (!await _reader.WaitToReadAsync(_token))
                        break;
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping - exit gracefully
                    break;
                }
                catch (ChannelClosedException)
                {
                    // Channel was closed - exit gracefully
                    break;
                }
                catch (Exception)
                {
                    break;
                }

                // Process all available items in batch
                int processedCount = 0;
                while (_reader.TryRead(out var item))
                {
                    if (_monitorHealth)
                        Interlocked.Decrement(ref _currentDepth); // Track depth

                    // ✅ FIX: Check pause BEFORE processing, but item is already taken
                    // If paused mid-batch, we need to process this item then stop
                    if (Volatile.Read(ref _disposed))
                        break;

                    // ✅ FIX: If paused, put item back or process it - choosing to process
                    // since we already took it (alternative: use Peek if available)
                    if (Volatile.Read(ref _isPaused))
                    {
                        // ✅ FIX: Only re-add if channel is still accepting writes
                        if (!_reader.Completion.IsCompleted)
                        {
                            try
                            {
                                if (!_writer.TryWrite(item))
                                {
                                    // TryWrite failed - process item to prevent loss
                                    try
                                    {
                                        _actionOnRead(item);
                                    }
                                    catch (Exception ex)
                                    {
                                        _onError?.Invoke(ex);
                                    }
                                }
                                else
                                {
                                    if (_monitorHealth)
                                        Interlocked.Increment(ref _currentDepth); // Track depth
                                }
                            }
                            catch (ChannelClosedException)
                            {
                                // Channel was closed between check and write - process item instead
                                try
                                {
                                    _actionOnRead(item);
                                }
                                catch (Exception ex)
                                {
                                    _onError?.Invoke(ex);
                                }
                            }
                        }
                        else
                        {
                            // Channel is complete, process the item we already took
                            try
                            {
                                _actionOnRead(item);
                            }
                            catch (Exception ex)
                            {
                                _onError?.Invoke(ex);
                            }
                        }
                        break;
                    }

                    try
                    {
                        if (_monitorHealth)
                        {
                            startTimestamp = Stopwatch.GetTimestamp();
                        }
                        _actionOnRead(item);
                        if (_monitorHealth)
                        {
                            var elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
                            Interlocked.Increment(ref _totalMessagesProcessed);
                            Interlocked.Add(ref _totalProcessingTimeTicks, elapsedTicks);
                        }
                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        _onError?.Invoke(ex);
                    }
                } // End of item processing loop

                // No need for manual waiting - WaitToReadAsync handles this
            } // End of main while loop
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _onError?.Invoke(ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckAndReportPerformance()
    {
        var currentTimestamp = Stopwatch.GetTimestamp();
        var lastReport = Volatile.Read(ref _lastReportTimestamp);

        if (currentTimestamp - lastReport > REPORT_INTERVAL_TICKS)
        {
            if (Interlocked.CompareExchange(ref _lastReportTimestamp, currentTimestamp, lastReport) == lastReport)
            {
                ReportPerformanceMetrics(currentTimestamp, lastReport);
            }
        }
    }
    private void ReportPerformanceMetrics(long currentTicks, long lastReportTicks)
    {
        var currentAdded = Volatile.Read(ref _totalMessagesAdded);
        var currentProcessed = Volatile.Read(ref _totalMessagesProcessed);
        var intervalSeconds = (currentTicks - lastReportTicks) / (double)TimeSpan.TicksPerSecond;

        var addedInInterval = currentAdded - _lastReportAdded;
        var processedInInterval = currentProcessed - _lastReportProcessed;

        var addRate = addedInInterval / intervalSeconds;
        var processRate = processedInInterval / intervalSeconds;
        var queueDepth = CurrentQueueDepth;
        var avgProcessingTime = AverageProcessingTimeMicroseconds;

        // Ultra-compact performance report
        if (queueDepth > 1000)
            log.WarnFormat("[{0}] Add: {1:F0}/s | Process: {2:F0}/s | Queue: {3:N0} | Avg: {4:F1}μs", _queueName, addRate, processRate, queueDepth, avgProcessingTime);

        // Update for next interval
        _lastReportAdded = currentAdded;
        _lastReportProcessed = currentProcessed;
    }
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();

        // Wait for consumer task to complete before disposing primitives
        try
        {
            _taskConsumer?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Task faulted - ignore during dispose
        }

        _pauseEvent.Dispose();
        _cts.Dispose();
    }
}
