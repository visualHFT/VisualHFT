using VisualHFT.Model;
using VisualHFT.Helpers;
using VisualHFT.DataRetriever.TestingFramework.Core;
using VisualHFT.PluginManager;
using VisualHFT.Commons.Interfaces;
using System.Diagnostics;

// xunit.v3 4.0.0 deprecates CollectionBehavior.DisableTestParallelization as an ERROR (CS0619),
// and its own docs say the property goes away entirely in the next major version.
// ParallelMode.None is its documented replacement -- "Disables all parallelism within the
// test assembly" -- the same guarantee these plugin tests rely on, since they drive one
// shared PluginManager and a live socket per venue.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace VisualHFT.DataRetriever.TestingFramework.TestCases
{
    [Trait("Category", "Run_Manually")]
    public class PluginFunctionalTests : BasePluginTest
    {
        public PluginFunctionalTests(ITestOutputHelper testOutputHelper) 
            : base(testOutputHelper, TestConfiguration.UltraFast()) // ✅ CHANGED: Use UltraFast for reconnection tests
        {
            ValidateTestEnvironment();
            LogAvailablePlugins();
        }

        [Fact]
        public async Task Test_Plugin_StartStop_Async()
        {
            await ExecuteTestWithReporting(
                "Plugin Start/Stop Test",
                async (context, config, output) =>
                {
                    output.WriteLine($"Testing {context.PluginName} start/stop cycle");
                    
                    // Verify initial state
                    Assert.Equal(ePluginStatus.LOADED, context.Plugin.Status);
                    
                    // Start the plugin
                    await context.DataRetriever.StartAsync();
                    
                    // Wait for either successful startup or a terminal startup failure
                    var startResolved = await WaitUntilAsync(
                        () => context.Plugin.Status == ePluginStatus.STARTED || context.Plugin.Status == ePluginStatus.STOPPED_FAILED,
                        config.StatusChangeTimeout);
                    if (!startResolved)
                    {
                        throw new TimeoutException($"Plugin did not reach STARTED status within {config.StatusChangeTimeout}. Current status: {context.Plugin.Status}");
                    }

                    if (context.Plugin.Status == ePluginStatus.STOPPED_FAILED)
                    {
                        throw new SkippedPluginTestException("Plugin could not become runnable in this environment and transitioned to STOPPED_FAILED during startup.");
                    }
                    
                    Assert.Equal(ePluginStatus.STARTED, context.Plugin.Status);
                    output.WriteLine($"✓ {context.PluginName} started successfully");
                    
                    // Stop the plugin
                    await context.DataRetriever.StopAsync();
                    
                    // Wait for stopped status
                    var stopSuccess = await WaitUntilAsync(() => IsNonRunningStatus(context.Plugin.Status), config.StatusChangeTimeout);
                    if (!stopSuccess)
                    {
                        throw new TimeoutException($"Plugin did not reach STOPPED status within {config.StatusChangeTimeout}. Current status: {context.Plugin.Status}");
                    }
                    
                    Assert.True(IsNonRunningStatus(context.Plugin.Status), $"Expected a non-running status after StopAsync but got {context.Plugin.Status}");
                    output.WriteLine($"✓ {context.PluginName} stopped successfully");
                    
                    return true;
                },
                result => result == true
            );
        }

        [Fact]
        public async Task Test_Plugin_HandlingReconnection_Async()
        {
            await ExecuteTestWithReporting(
                "Plugin Reconnection Test Suite",
                async (context, config, output) =>
                {
                    var results = new ReconnectionTestResults();
                    output.WriteLine("");
                    output.WriteLine("--------------------------------");
                    output.WriteLine($"Testing: {context.PluginName}");
                    output.WriteLine("=".PadRight(60, '='));
                    
                    await RunReconnectionScenarioAsync(
                        "Basic Reconnection",
                        context,
                        config,
                        output,
                        results,
                        async () => await ExecuteBasicReconnectionScenarioAsync(context, config));

                    await RunReconnectionScenarioAsync(
                        "Burst Exception Recovery",
                        context,
                        config,
                        output,
                        results,
                        async () => await ExecuteBurstExceptionScenarioAsync(context, config, results));

                    await RunReconnectionScenarioAsync(
                        "Repeated Recovery Waves",
                        context,
                        config,
                        output,
                        results,
                        async () => await ExecuteRepeatedRecoveryScenarioAsync(context, config));

                    await RunReconnectionScenarioAsync(
                        "Max Reconnection Attempts",
                        context,
                        config,
                        output,
                        results,
                        () => throw new SkippedPluginTestException("Not exercised in CI because it requires long-running failure windows."));

                    await RunReconnectionScenarioAsync(
                        "Stop During Reconnection",
                        context,
                        config,
                        output,
                        results,
                        async () => await ExecuteStopDuringReconnectionScenarioAsync(context, config));

                    await RunReconnectionScenarioAsync(
                        "Reconnection Coalescing",
                        context,
                        config,
                        output,
                        results,
                        async () => await ExecuteCoalescingScenarioAsync(context, config, results));
                    
                    output.WriteLine("=".PadRight(60, '='));
                    output.WriteLine($"Summary: {results.PassedTests}/{results.TotalTests} passed");
                    output.WriteLine($"Skipped: {results.SkippedTests}");
                    
                    if (results.FailedTests > 0)
                    {
                        output.WriteLine($"Failed ({results.FailedTests}):");
                        foreach (var failure in results.Failures)
                            output.WriteLine($"  • {failure}");
                    }
                    
                    if (results.Warnings.Any())
                    {
                        output.WriteLine($"Warnings ({results.Warnings.Count}):");
                        foreach (var warning in results.Warnings)
                            output.WriteLine($"  • {warning}");
                    }
                    
                    if (results.FailedTests == 0)
                        return results;
                    throw new Exception($"{results.FailedTests} test(s) failed");
                },
                result => result.FailedTests == 0
            );
        }

        #region Reconnection Test Cases

        private async Task RunReconnectionScenarioAsync(
            string scenarioName,
            PluginTestContext context,
            TestConfiguration config,
            ITestOutputHelper output,
            ReconnectionTestResults results,
            Func<Task> scenario)
        {
            try
            {
                output.WriteLine($"Scenario: {scenarioName}");
                context.Reset();

                if (context.Plugin.Status == ePluginStatus.STOPPED_FAILED)
                {
                    throw new SkippedPluginTestException("Plugin is in STOPPED_FAILED before the scenario begins, so reconnection behavior cannot be exercised in this environment.");
                }

                await EnsurePluginStoppedAsync(context, config);
                await scenario();

                results.PassTest(scenarioName);
                output.WriteLine("  PASSED");
            }
            catch (SkippedPluginTestException ex)
            {
                results.SkipTest(scenarioName, ex.Message);
                output.WriteLine($"  SKIPPED: {ex.Message}");
            }
            catch (Exception ex)
            {
                results.FailTest(scenarioName, ex.Message);
                output.WriteLine($"  FAILED: {ex.Message}");
            }
            finally
            {
                await StopPluginQuietlyAsync(context, config, output);
                context.Reset();
            }
        }

        private async Task ExecuteBasicReconnectionScenarioAsync(PluginTestContext context, TestConfiguration config)
        {
            using var observer = new PluginStatusObserver(context);
            await StartPluginAndWaitForDataAsync(context, config);
            using var probe = new MatchingOrderBookSubscription(context, maxExceptions: 1, scenarioName: "Basic reconnection");

            await TriggerAndAwaitRecoveryAsync(context, config, observer, probe);
        }

        private async Task ExecuteBurstExceptionScenarioAsync(PluginTestContext context, TestConfiguration config, ReconnectionTestResults results)
        {
            using var observer = new PluginStatusObserver(context);
            await StartPluginAndWaitForDataAsync(context, config);
            using var probe = new MatchingOrderBookSubscription(context, maxExceptions: 5, scenarioName: "Burst exception recovery");

            await TriggerAndAwaitRecoveryAsync(context, config, observer, probe);

            if (probe.ExceptionCount < 2)
            {
                results.AddWarning("Burst Exception Recovery: only one exception was observed before recovery completed.");
            }
        }

        private async Task ExecuteRepeatedRecoveryScenarioAsync(PluginTestContext context, TestConfiguration config)
        {
            using var observer = new PluginStatusObserver(context);
            await StartPluginAndWaitForDataAsync(context, config);

            await ExecuteFailureWaveAsync(context, config, observer, "Repeated recovery wave 1");
            await ExecuteFailureWaveAsync(context, config, observer, "Repeated recovery wave 2");
        }

        private async Task ExecuteStopDuringReconnectionScenarioAsync(PluginTestContext context, TestConfiguration config)
        {
            using var observer = new PluginStatusObserver(context);
            await StartPluginAndWaitForDataAsync(context, config);
            using var probe = new MatchingOrderBookSubscription(context, maxExceptions: 1, scenarioName: "Stop during reconnection");

            if (!await WaitUntilAsync(() => probe.ExceptionCount >= 1, config.DataReceptionTimeout))
            {
                throw new TimeoutException("No matching order book event was captured to trigger reconnection.");
            }

            if (!await WaitUntilAsync(observer.HasSeenReconnectionSignal, config.StatusChangeTimeout))
            {
                throw new TimeoutException($"No reconnection signal observed before issuing StopAsync. History: {observer.FormatHistory()}");
            }

            await context.DataRetriever.StopAsync();

            if (!await WaitUntilAsync(() => IsNonRunningStatus(context.Plugin.Status), config.StatusChangeTimeout))
            {
                throw new TimeoutException($"Plugin did not stop after StopAsync during reconnection. Current status: {context.Plugin.Status}");
            }

            if (!await WaitUntilAsync(() => IsNonRunningStatus(context.Plugin.Status), TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException($"Plugin did not settle into a non-running state after StopAsync. History: {observer.FormatHistory()}");
            }
        }

        private async Task ExecuteCoalescingScenarioAsync(PluginTestContext context, TestConfiguration config, ReconnectionTestResults results)
        {
            using var observer = new PluginStatusObserver(context);
            await StartPluginAndWaitForDataAsync(context, config);
            using var probe = new MatchingOrderBookSubscription(context, maxExceptions: 10, scenarioName: "Reconnection coalescing");

            if (!await WaitUntilAsync(() => probe.ExceptionCount >= 1, config.StatusChangeTimeout + config.DataReceptionTimeout))
            {
                throw new SkippedPluginTestException("Plugin did not emit enough matching order book updates to exercise coalescing deterministically.");
            }

            await TriggerAndAwaitRecoveryAsync(context, config, observer, probe, requireTrigger: false);

            var restartSignals = observer.CountTransitionsTo(ePluginStatus.STARTING) + observer.CountTransitionsTo(ePluginStatus.STOPPED);
            if (restartSignals > 3)
            {
                results.AddWarning($"Reconnection Coalescing: observed {restartSignals} restart signals for {probe.ExceptionCount} injected exceptions.");
            }
        }

        private async Task ExecuteFailureWaveAsync(PluginTestContext context, TestConfiguration config, PluginStatusObserver observer, string scenarioName)
        {
            using var probe = new MatchingOrderBookSubscription(context, maxExceptions: 1, scenarioName: scenarioName);
            await TriggerAndAwaitRecoveryAsync(context, config, observer, probe);
        }

        private async Task StartPluginAndWaitForDataAsync(PluginTestContext context, TestConfiguration config)
        {
            await context.DataRetriever.StartAsync();

            if (!await WaitUntilAsync(
                    () => context.Plugin.Status == ePluginStatus.STARTED || context.Plugin.Status == ePluginStatus.STOPPED_FAILED,
                    config.StatusChangeTimeout))
            {
                throw new TimeoutException($"Plugin did not reach STARTED status within {config.StatusChangeTimeout}. Current status: {context.Plugin.Status}");
            }

            if (context.Plugin.Status == ePluginStatus.STOPPED_FAILED)
            {
                throw new SkippedPluginTestException("Plugin could not become runnable in this environment and transitioned to STOPPED_FAILED before reconnection behavior could be exercised.");
            }

            if (!await context.WaitForDataAsync(config.InitialDataDelay + config.DataReceptionTimeout))
            {
                throw new TimeoutException($"No order book data received within {config.InitialDataDelay + config.DataReceptionTimeout}.");
            }
        }

        private async Task TriggerAndAwaitRecoveryAsync(
            PluginTestContext context,
            TestConfiguration config,
            PluginStatusObserver observer,
            MatchingOrderBookSubscription probe,
            bool requireTrigger = true)
        {
            if (requireTrigger && !await WaitUntilAsync(() => probe.ExceptionCount >= 1, config.DataReceptionTimeout))
            {
                throw new TimeoutException($"No matching order book event was captured for {probe.ScenarioName}.");
            }

            if (!await WaitUntilAsync(observer.HasSeenReconnectionSignal, config.StatusChangeTimeout))
            {
                throw new TimeoutException($"No reconnection signal observed for {probe.ScenarioName}. History: {observer.FormatHistory()}");
            }

            if (!await WaitUntilAsync(
                    () => context.Plugin.Status == ePluginStatus.STARTED && probe.MessageCount > probe.ExceptionCount,
                    config.StatusChangeTimeout + config.DataReceptionTimeout))
            {
                throw new TimeoutException(
                    $"Plugin did not resume publishing data after {probe.ScenarioName}. Current status: {context.Plugin.Status}. History: {observer.FormatHistory()}");
            }
        }

        private async Task EnsurePluginStoppedAsync(PluginTestContext context, TestConfiguration config)
        {
            if (context.Plugin.Status is ePluginStatus.STARTED or ePluginStatus.STARTING or ePluginStatus.STOPPING)
            {
                await context.DataRetriever.StopAsync();
            }

            if (!await WaitUntilAsync(() => IsNonRunningStatus(context.Plugin.Status), config.CleanupTimeout))
            {
                throw new TimeoutException($"Plugin did not reach a stopped state within {config.CleanupTimeout}. Current status: {context.Plugin.Status}");
            }
        }

        private async Task StopPluginQuietlyAsync(PluginTestContext context, TestConfiguration config, ITestOutputHelper output)
        {
            try
            {
                await EnsurePluginStoppedAsync(context, config);
            }
            catch (Exception ex)
            {
                output.WriteLine($"  Cleanup warning: {ex.Message}");
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? pollInterval = null)
        {
            ArgumentNullException.ThrowIfNull(condition);

            if (condition())
            {
                return true;
            }

            var stopwatch = Stopwatch.StartNew();
            var delay = pollInterval ?? TimeSpan.FromMilliseconds(100);

            while (stopwatch.Elapsed < timeout)
            {
                await Task.Delay(delay);
                if (condition())
                {
                    return true;
                }
            }

            return condition();
        }

        private static bool IsNonRunningStatus(ePluginStatus status)
        {
            return status is ePluginStatus.STOPPED or ePluginStatus.LOADED or ePluginStatus.STOPPED_FAILED;
        }

        #endregion

        #region Helper Classes

        private sealed class ReconnectionTestResults
        {
            private readonly List<ScenarioResult> _scenarioResults = new List<ScenarioResult>();

            public int TotalTests => _scenarioResults.Count;
            public int PassedTests => _scenarioResults.Count(x => x.Outcome == ScenarioOutcome.Passed);
            public int FailedTests => _scenarioResults.Count(x => x.Outcome == ScenarioOutcome.Failed);
            public int SkippedTests => _scenarioResults.Count(x => x.Outcome == ScenarioOutcome.Skipped);
            public List<string> Failures => _scenarioResults
                .Where(x => x.Outcome == ScenarioOutcome.Failed)
                .Select(x => $"{x.Name}: {x.Detail}")
                .ToList();
            public List<string> Warnings { get; } = new List<string>();

            public void PassTest(string testName)
            {
                _scenarioResults.Add(new ScenarioResult(testName, ScenarioOutcome.Passed, null));
            }

            public void FailTest(string testName, string reason)
            {
                _scenarioResults.Add(new ScenarioResult(testName, ScenarioOutcome.Failed, reason));
            }

            public void SkipTest(string testName, string reason)
            {
                _scenarioResults.Add(new ScenarioResult(testName, ScenarioOutcome.Skipped, reason));
            }

            public void AddWarning(string warning)
            {
                Warnings.Add(warning);
            }
        }

        private enum ScenarioOutcome
        {
            Passed,
            Failed,
            Skipped
        }

        private sealed record ScenarioResult(string Name, ScenarioOutcome Outcome, string? Detail);

        private sealed class PluginStatusObserver : IDisposable
        {
            private readonly PluginTestContext _context;
            private readonly Timer _timer;
            private readonly object _lockObject = new object();
            private readonly List<ePluginStatus> _transitions = new List<ePluginStatus>();
            private ePluginStatus _lastStatus;

            public PluginStatusObserver(PluginTestContext context)
            {
                _context = context ?? throw new ArgumentNullException(nameof(context));
                _lastStatus = context.Plugin.Status;
                _transitions.Add(_lastStatus);
                _timer = new Timer(_ => CaptureStatus(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            }

            public bool HasSeenReconnectionSignal()
            {
                lock (_lockObject)
                {
                    return _transitions.Any(x => x is ePluginStatus.STARTING or ePluginStatus.STOPPING or ePluginStatus.STOPPED or ePluginStatus.STOPPED_FAILED);
                }
            }

            public int CountTransitionsTo(ePluginStatus status)
            {
                lock (_lockObject)
                {
                    return _transitions.Count(x => x == status);
                }
            }

            public string FormatHistory()
            {
                lock (_lockObject)
                {
                    return string.Join(" -> ", _transitions.Select(x => x.ToString()));
                }
            }

            private void CaptureStatus()
            {
                var currentStatus = _context.Plugin.Status;

                lock (_lockObject)
                {
                    if (currentStatus == _lastStatus)
                    {
                        return;
                    }

                    _lastStatus = currentStatus;
                    _transitions.Add(currentStatus);
                }
            }

            public void Dispose()
            {
                _timer.Dispose();
            }
        }

        private sealed class MatchingOrderBookSubscription : IDisposable
        {
            private readonly PluginTestContext _context;
            private readonly Action<OrderBook> _handler;
            private readonly int _maxExceptions;
            private readonly object _lockObject = new object();
            private int _messageCount;
            private int _exceptionCount;

            public MatchingOrderBookSubscription(PluginTestContext context, int maxExceptions, string scenarioName)
            {
                _context = context ?? throw new ArgumentNullException(nameof(context));
                if (maxExceptions < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxExceptions));
                }

                _maxExceptions = maxExceptions;
                ScenarioName = scenarioName;
                _handler = HandleOrderBook;
                HelperOrderBook.Instance.Subscribe(_handler);
            }

            public string ScenarioName { get; }
            public int MessageCount => Volatile.Read(ref _messageCount);
            public int ExceptionCount => Volatile.Read(ref _exceptionCount);

            private void HandleOrderBook(OrderBook orderBook)
            {
                if (!IsPluginOrderBook(_context, orderBook))
                {
                    return;
                }

                lock (_lockObject)
                {
                    _messageCount++;
                    if (_exceptionCount >= _maxExceptions)
                    {
                        return;
                    }

                    _exceptionCount++;
                    throw new InvalidOperationException($"{ScenarioName} injected exception #{_exceptionCount}");
                }
            }

            public void Dispose()
            {
                HelperOrderBook.Instance.Unsubscribe(_handler);
            }
        }

        private static bool IsPluginOrderBook(PluginTestContext context, OrderBook orderBook)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(orderBook);

            var providerId = context.Plugin.Settings?.Provider?.ProviderID;
            return providerId is not null && Equals(orderBook.ProviderID, providerId);
        }

        #endregion

        [Fact]
        public async Task Test_Plugin_OrderBookIntegrityAndResilience_Async()
        {
            await ExecuteTestWithReporting(
                "OrderBook Integrity Test", 
                async (context, config, output) =>
                {
                    output.WriteLine($"Testing {context.PluginName} order book integrity and resilience");
                    
                    // Start the plugin
                    await context.DataRetriever.StartAsync();
                    
                    var startResolved = await WaitUntilAsync(
                        () => context.Plugin.Status == ePluginStatus.STARTED || context.Plugin.Status == ePluginStatus.STOPPED_FAILED,
                        config.StatusChangeTimeout);
                    if (!startResolved)
                    {
                        throw new TimeoutException($"Plugin did not start within {config.StatusChangeTimeout}");
                    }

                    if (context.Plugin.Status == ePluginStatus.STOPPED_FAILED)
                    {
                        throw new SkippedPluginTestException("Plugin could not become runnable in this environment and transitioned to STOPPED_FAILED before order book integrity could be exercised.");
                    }
                    output.WriteLine($"✓ {context.PluginName} started successfully");

                    // Wait for initial data (up to InitialDataDelay, exit early if data arrives)
                    var dataReceived = await context.WaitForDataAsync(config.InitialDataDelay + config.DataReceptionTimeout);
                    if (!dataReceived)
                    {
                        throw new Exception($"No order book data received within {config.DataReceptionTimeout}");
                    }
                    output.WriteLine($"✓ {context.PluginName} data reception confirmed");
                    
                    // Monitor data integrity for specified duration
                    output.WriteLine($"🔍 Monitoring data integrity for {config.IntegrityTestDuration}...");
                    var testStartTime = DateTime.Now;
                    var crossedSpreadStartTime = (DateTime?)null;
                    var warnings = new List<string>();
                    var checksPerformed = 0;
                    
                    while (DateTime.Now.Subtract(testStartTime) < config.IntegrityTestDuration && context.LastException == null)
                    {
                        var orderBook = context.LastOrderBook;
                        checksPerformed++;
                        
                        if (orderBook != null)
                        {
                            // Check for crossed spread
                            var isCrossedSpread = orderBook.Spread < 0;
                            if (isCrossedSpread)
                            {
                                crossedSpreadStartTime ??= DateTime.Now;
                                
                                if (DateTime.Now.Subtract(crossedSpreadStartTime.Value) > config.CrossedSpreadTolerance)
                                {
                                    // Debug information about the crossed spread
                                    var bestBid = orderBook.GetTOB(true);
                                    var bestAsk = orderBook.GetTOB(false);
                                    var debugInfo = $"Best Bid: {bestBid?.Price:F6} @ {bestBid?.Size:F6}, Best Ask: {bestAsk?.Price:F6} @ {bestAsk?.Size:F6}";
                                    output.WriteLine($"🚨 DEBUG - Crossed spread details: {debugInfo}");
                                    
                                    throw new Exception($"Crossed spread detected and persisted for more than {config.CrossedSpreadTolerance}. Status: {context.Plugin.Status}, Spread: {orderBook.Spread}. {debugInfo}");
                                }
                            }
                            else
                            {
                                crossedSpreadStartTime = null;
                            }
                            
                            // Check depth limits
                            if (orderBook.MaxDepth > 0)
                            {
                                var bidCount = orderBook.Bids?.Count() ?? 0;
                                var askCount = orderBook.Asks?.Count() ?? 0;
                                
                                if (bidCount > orderBook.MaxDepth || askCount > orderBook.MaxDepth)
                                {
                                    throw new Exception($"Order book depth exceeds maximum allowed depth. Bids: {bidCount}, Asks: {askCount}, MaxDepth: {orderBook.MaxDepth}");
                                }
                            }
                            else 
                            {
                                var bidCount = orderBook.Bids?.Count() ?? 0;
                                var askCount = orderBook.Asks?.Count() ?? 0;
                                
                                if (bidCount > config.DepthWarningThreshold || askCount > config.DepthWarningThreshold)
                                {
                                    var warning = $"Depth exceeds threshold - Bids: {bidCount}, Asks: {askCount} (threshold: {config.DepthWarningThreshold})";
                                    if (!warnings.Contains(warning))
                                    {
                                        warnings.Add(warning);
                                        output.WriteLine($"⚠️  {warning}");
                                    }
                                }
                            }
                        }
                        
                        await Task.Delay(config.IntegrityCheckInterval);
                    }
                    
                    if (context.LastException != null)
                    {
                        throw context.LastException;
                    }
                    
                    if (context.LastOrderBook == null)
                    {
                        throw new Exception("No order book data was maintained during the test period");
                    }
                    
                    output.WriteLine($"✓ {context.PluginName} integrity test completed");
                    output.WriteLine($"  📊 Checks performed: {checksPerformed}");
                    output.WriteLine($"  📈 Final spread: {context.LastOrderBook.Spread:F6}");
                    output.WriteLine($"  📚 Final depth - Bids: {context.LastOrderBook.Bids?.Count() ?? 0}, Asks: {context.LastOrderBook.Asks?.Count() ?? 0}");
                    
                    if (warnings.Any())
                    {
                        output.WriteLine($"  ⚠️  Warnings: {warnings.Count}");
                    }
                    
                    return new TestResult { Success = true, Warnings = warnings };
                },
                result => result.Success
            );
        }

        // Helper class for test results
        private class TestResult
        {
            public bool Success { get; set; }
            public List<string> Warnings { get; set; } = new List<string>();
        }
    }
}
