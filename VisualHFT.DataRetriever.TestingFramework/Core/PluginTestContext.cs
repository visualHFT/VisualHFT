using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Commons.Interfaces;
using VisualHFT.Commons.Extensions; // ✅ ADD: For ToPluginStatus extension method
using VisualHFT.DataRetriever;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Model;

namespace VisualHFT.DataRetriever.TestingFramework.Core
{
    /// <summary>
    /// Provides isolated test context for plugin testing with proper cleanup and state management
    /// </summary>
    public class PluginTestContext : IDisposable
    {
        private readonly IDataRetrieverTestable _plugin;
        private readonly IDataRetriever _dataRetriever;
        private readonly IPlugin _pluginInterface;
        private readonly object _lockObject = new object();
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly TaskCompletionSource<bool> _disposalCompletionSource;

        private OrderBook? _lastOrderBook;
        private Exception? _lastException;
        private List<Exception> _allExceptions = new List<Exception>();
        private bool _disposed = false;

        // Event-driven status tracking
        private readonly Dictionary<ePluginStatus, TaskCompletionSource<bool>> _statusWaiters = new();
        private ePluginStatus _lastObservedStatus = ePluginStatus.LOADED;

        // Event subscriptions for cleanup tracking
        private readonly List<Action> _subscriptionCleanupActions = new List<Action>();

        public string PluginName => _pluginInterface?.Name ?? _plugin?.GetType().Name ?? "Unknown";
        public IDataRetriever DataRetriever => _dataRetriever;
        public IPlugin Plugin => _pluginInterface;
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        private readonly List<StatusTransition> _transitions = new();

        private class StatusTransition
        {
            public ePluginStatus FromStatus { get; set; }
            public ePluginStatus ToStatus { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public OrderBook? LastOrderBook 
        { 
            get { lock (_lockObject) return _lastOrderBook; } 
            private set { lock (_lockObject) _lastOrderBook = value; } 
        }

        public Exception? LastException 
        { 
            get { lock (_lockObject) return _lastException; } 
            private set { lock (_lockObject) _lastException = value; } 
        }

        public List<Exception> AllExceptions 
        { 
            get { lock (_lockObject) return new List<Exception>(_allExceptions); } 
        }

        public PluginTestContext(IDataRetrieverTestable plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _dataRetriever = plugin as IDataRetriever ?? throw new ArgumentException("Plugin must implement IDataRetriever");
            _pluginInterface = plugin as IPlugin ?? throw new ArgumentException("Plugin must implement IPlugin");
            
            _cancellationTokenSource = new CancellationTokenSource();
            _disposalCompletionSource = new TaskCompletionSource<bool>();

            SetupEventSubscriptions();
        }

        private void SetupEventSubscriptions()
        {
            // Subscribe to OrderBook updates
            Action<OrderBook> orderBookHandler = (orderBook) =>
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested) return;
                
                if (orderBook?.ProviderID == _pluginInterface.Settings?.Provider?.ProviderID)
                {
                    LastOrderBook = orderBook;
                }
            };
            
            HelperOrderBook.Instance.Subscribe(orderBookHandler);
            _subscriptionCleanupActions.Add(() => HelperOrderBook.Instance.Unsubscribe(orderBookHandler));

            // ✅ FIX: Use Plugin instance comparison instead of ProviderCode comparison
            // This prevents silent event discards when Settings.Provider is null/recreated during reconnection
        EventHandler<Provider> statusChangeHandler = (sender, provider) =>
            {
                if (provider?.Plugin == _pluginInterface)
                {
                    var newStatus = provider.Status.ToPluginStatus();
                    lock (_lockObject)
                    {
                        var transition = new StatusTransition
                        {
                            FromStatus = _lastObservedStatus,
                            ToStatus = newStatus,
                            Timestamp = DateTime.UtcNow
                        };

                        _transitions.Add(transition);
                        _lastObservedStatus = newStatus;

                        // ✅ FIX: Don't auto-cleanup during tests - let Reset() handle it
                        // This prevents "Collection was modified" exceptions

                        // Complete waiters
                        if (_statusWaiters.TryGetValue(newStatus, out var tcs))
                        {
                            tcs.TrySetResult(true);
                        }
                    }
                }
            };

            HelperProvider.Instance.OnStatusChanged += statusChangeHandler;
            _subscriptionCleanupActions.Add(() => HelperProvider.Instance.OnStatusChanged -= statusChangeHandler);

            // Subscribe to error notifications
            EventHandler<ErrorNotificationEventArgs> errorHandler = (sender, e) =>
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested) return;
                
                if (e.Notification.NotificationType == HelprNorificationManagerTypes.ERROR && 
                    e.Notification.Exception != null)
                {
                    lock (_lockObject)
                    {
                        _lastException = e.Notification.Exception;
                        _allExceptions.Add(e.Notification.Exception);
                    }
                }
            };
            
            HelperNotificationManager.Instance.NotificationAdded += errorHandler;
            _subscriptionCleanupActions.Add(() => HelperNotificationManager.Instance.NotificationAdded -= errorHandler);
        }

        /// <summary>
        /// Waits for the plugin to reach the specified status within the timeout period using hybrid event+polling approach
        /// </summary>
        public async Task<bool> WaitForStatusAsync(ePluginStatus expectedStatus, TimeSpan timeout)
        {
            // ✅ FIX: Check if status WAS EVER reached (not just "is it now")
            lock (_lockObject)
            {
                // Check current state
                if (_pluginInterface.Status == expectedStatus)
                    return true;

                // ✅ NEW: Check transition history
                if (_transitions.Any(t => t.ToStatus == expectedStatus))
                    return true;  // "Plugin DID transition to this state"
            }

            // Set up waiter for future transitions
            TaskCompletionSource<bool> tcs = new();
            lock (_lockObject)
            {
                _statusWaiters[expectedStatus] = tcs;
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(timeout);

                // Wait for either:
                // 1. Event fires (status changes TO expectedStatus)
                // 2. Timeout expires
                var completedTask = await Task.WhenAny(
                    tcs.Task,
                    Task.Delay(timeout, timeoutCts.Token)
                );

                if (completedTask == tcs.Task)
                    return await tcs.Task;  // Event fired

                // Timeout - final check of history
                lock (_lockObject)
                {
                    return _transitions.Any(t => t.ToStatus == expectedStatus);
                }
            }
            finally
            {
                lock (_lockObject)
                {
                    _statusWaiters.Remove(expectedStatus);
                }
            }
        }

        /// <summary>
        /// Waits for data (OrderBook) to be received within the timeout period
        /// </summary>
        public async Task<bool> WaitForDataAsync(TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellationTokenSource.Token, timeoutCts.Token);

            try
            {
                while (!combinedCts.Token.IsCancellationRequested && LastOrderBook == null)
                {
                    await Task.Delay(100, combinedCts.Token);
                }
                return LastOrderBook != null;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Resets the test context state for reuse
        /// </summary>
        public void Reset()
        {
            lock (_lockObject)
            {
                _lastOrderBook = null;
                _lastException = null;
                _allExceptions.Clear();
                _statusWaiters.Clear();
                _transitions.Clear(); // ✅ FIX: Clear transition history between tests
                _lastObservedStatus = _pluginInterface.Status;
            }
        }

        /// <summary>
        /// Gets a snapshot of current plugin state for debugging
        /// </summary>
        public PluginTestSnapshot GetSnapshot()
        {
            return new PluginTestSnapshot
            {
                PluginName = PluginName,
                Status = _pluginInterface.Status,
                HasOrderBook = LastOrderBook != null,
                LastOrderBookTimestamp = LastOrderBook?.Sequence,
                ExceptionCount = AllExceptions.Count,
                LastException = LastException?.Message
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            _cancellationTokenSource.Cancel();

            try
            {
                // Clean up all event subscriptions
                foreach (var cleanup in _subscriptionCleanupActions)
                {
                    try
                    {
                        cleanup();
                    }
                    catch (Exception ex)
                    {
                        // Log but don't throw during disposal
                        System.Diagnostics.Debug.WriteLine($"Error during subscription cleanup: {ex.Message}");
                    }
                }

                // Stop the plugin if running
                if (_pluginInterface.Status == ePluginStatus.STARTED || 
                    _pluginInterface.Status == ePluginStatus.STARTING)
                {
                    var stopTask = _dataRetriever.StopAsync();
                    if (!stopTask.Wait(TimeSpan.FromSeconds(10)))
                    {
                        System.Diagnostics.Debug.WriteLine($"Plugin {PluginName} did not stop within timeout");
                    }
                }

                // Dispose the plugin if it implements IDisposable
                if (_dataRetriever is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            finally
            {
                lock (_lockObject)
                {
                    // Complete all pending waiters
                    foreach (var waiter in _statusWaiters.Values)
                    {
                        waiter.TrySetCanceled();
                    }
                    _statusWaiters.Clear();
                }
                
                _cancellationTokenSource.Dispose();
                _disposalCompletionSource.SetResult(true);
            }
        }
    }

    /// <summary>
    /// Snapshot of plugin state for debugging and reporting
    /// </summary>
    public class PluginTestSnapshot
    {
        public string PluginName { get; set; } = string.Empty;
        public ePluginStatus Status { get; set; }
        public bool HasOrderBook { get; set; }
        public long? LastOrderBookTimestamp { get; set; }
        public int ExceptionCount { get; set; }
        public string? LastException { get; set; }

        public override string ToString()
        {
            return $"Plugin: {PluginName}, Status: {Status}, HasData: {HasOrderBook}, " +
                   $"Exceptions: {ExceptionCount}, LastError: {LastException ?? "None"}";
        }
    }
}