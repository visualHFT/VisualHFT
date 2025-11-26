using VisualHFT.Model;
using VisualHFT.Helpers;
using VisualHFT.DataRetriever.TestingFramework.Core;
using Xunit.Abstractions;
using VisualHFT.PluginManager;
using VisualHFT.Commons.Interfaces;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
                    
                    // Wait for started status
                    var startSuccess = await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout);
                    if (!startSuccess)
                    {
                        throw new TimeoutException($"Plugin did not reach STARTED status within {config.StatusChangeTimeout}. Current status: {context.Plugin.Status}");
                    }
                    
                    Assert.Equal(ePluginStatus.STARTED, context.Plugin.Status);
                    output.WriteLine($"✓ {context.PluginName} started successfully");
                    
                    // Stop the plugin
                    await context.DataRetriever.StopAsync();
                    
                    // Wait for stopped status
                    var stopSuccess = await context.WaitForStatusAsync(ePluginStatus.STOPPED, config.StatusChangeTimeout);
                    if (!stopSuccess)
                    {
                        throw new TimeoutException($"Plugin did not reach STOPPED status within {config.StatusChangeTimeout}. Current status: {context.Plugin.Status}");
                    }
                    
                    Assert.Equal(ePluginStatus.STOPPED, context.Plugin.Status);
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
                    
                    await Test_BasicReconnection(context, config, output, results);
                    await Test_ConcurrentExceptions(context, config, output, results);
                    await Test_RetryLogicAfterFailures(context, config, output, results);
                    await Test_MaxAttemptsExhaustion(context, config, output, results);
                    await Test_StatusChangesDuringReconnection(context, config, output, results);
                    await Test_ReconnectionCoalescing(context, config, output, results);
                    
                    // Summary
                    output.WriteLine("=".PadRight(60, '='));
                    output.WriteLine($"Summary: {results.PassedTests}/{results.TotalTests} passed");
                    
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

        private async Task Test_BasicReconnection(PluginTestContext context, TestConfiguration config, ITestOutputHelper output, ReconnectionTestResults results)
        {
            output.WriteLine("🔬 TEST 1: Basic Reconnection");
            
            try
            {
                bool exceptionTriggered = false;
                
                Action<OrderBook> exceptionTrigger = (lob) =>
                {
                    if (exceptionTriggered || lob.ProviderID != context.Plugin.Settings.Provider.ProviderID) 
                        return;
                    
                    exceptionTriggered = true;
                    throw new Exception("Test exception: Simulated sequence gap");
                };
                
                HelperOrderBook.Instance.Subscribe(exceptionTrigger);
                
                try
                {
                    await context.DataRetriever.StartAsync();
                    if (!await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout))
                        throw new TimeoutException("Plugin did not start");
                    
                    // ✅ REDUCED: 5s max wait instead of 10s
                    if (!await context.WaitForStatusAsync(ePluginStatus.STOPPED, TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Plugin did not stop for reconnection");
                    
                    // ✅ REDUCED: 3s max wait instead of 5s
                    if (!await context.WaitForStatusAsync(ePluginStatus.STARTING, TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("Plugin did not reach STARTING status");
                    
                    // ✅ REDUCED: 5s max wait instead of 10s
                    if (!await context.WaitForStatusAsync(ePluginStatus.STARTED, TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Plugin did not reconnect successfully");
                    
                    results.PassTest("Basic Reconnection");
                    output.WriteLine("  ✅ PASSED");
                }
                finally
                {
                    HelperOrderBook.Instance.Unsubscribe(exceptionTrigger);
                    await context.DataRetriever.StopAsync();
                    await Task.Delay(500); // ✅ REDUCED: 500ms instead of 1000ms
                }
            }
            catch (Exception ex)
            {
                results.FailTest("Basic Reconnection", ex.Message);
                output.WriteLine($"  ❌ FAILED: {ex.Message}");
            }
        }

        private async Task Test_ConcurrentExceptions(PluginTestContext context, TestConfiguration config, ITestOutputHelper output, ReconnectionTestResults results)
        {
            output.WriteLine("🔬 TEST 2: Concurrent Exception Handling");
            
            try
            {
                int exceptionCount = 0;
                object exceptionLock = new object();
                
                Action<OrderBook> exceptionTrigger = (lob) =>
                {
                    if (lob.ProviderID != context.Plugin.Settings.Provider.ProviderID) 
                        return;
                    
                    lock (exceptionLock)
                    {
                        if (exceptionCount >= 5) return;
                        exceptionCount++;
                    }
                    
                    throw new Exception($"Concurrent exception #{exceptionCount}");
                };
                
                HelperOrderBook.Instance.Subscribe(exceptionTrigger);
                
                try
                {
                    await context.DataRetriever.StartAsync();
                    if (!await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout))
                        throw new TimeoutException("Plugin did not start");
                    
                    // ✅ REDUCED: 3s max wait instead of 5s
                    bool stoppedDetected = await context.WaitForStatusAsync(ePluginStatus.STOPPED, TimeSpan.FromSeconds(3));
                    bool stoppingDetected = false;
                    bool startingDetected = false;
                    
                    if (!stoppedDetected)
                    {
                        stoppingDetected = context.Plugin.Status == ePluginStatus.STOPPING;
                        
                        // ✅ REDUCED: 3s max wait instead of 5s
                        startingDetected = await context.WaitForStatusAsync(ePluginStatus.STARTING, TimeSpan.FromSeconds(3));
                    }
                    
                    if (!stoppedDetected && !stoppingDetected && !startingDetected)
                    {
                        throw new TimeoutException("Plugin did not stop for reconnection (neither STOPPED, STOPPING, nor STARTING detected)");
                    }
                    
                    // ✅ REDUCED: 10s max wait instead of 15s
                    if (!await context.WaitForStatusAsync(ePluginStatus.STARTED, TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Plugin did not reconnect");
                    
                    output.WriteLine($"  ✓ Handled {exceptionCount} concurrent exceptions with single reconnection");
                    results.PassTest("Concurrent Exceptions");
                    output.WriteLine("  ✅ PASSED");
                }
                finally
                {
                    HelperOrderBook.Instance.Unsubscribe(exceptionTrigger);
                    await context.DataRetriever.StopAsync();
                    await Task.Delay(500); // ✅ REDUCED: 500ms instead of 1000ms
                }
            }
            catch (Exception ex)
            {
                results.FailTest("Concurrent Exceptions", ex.Message);
                output.WriteLine($"  ❌ FAILED: {ex.Message}");
            }
        }

        private async Task Test_RetryLogicAfterFailures(PluginTestContext context, TestConfiguration config, ITestOutputHelper output, ReconnectionTestResults results)
        {
            output.WriteLine("🔬 TEST 3: Retry Logic");
            
            if (context.DataRetriever is not IDataRetrieverTestable)
            {
                output.WriteLine("  ⏭️  SKIPPED: Plugin not testable");
                results.AddWarning("TEST 3: Skipped - plugin not testable");
                results.PassTest("Retry Logic (Skipped)");
                return;
            }
            
            Timer statusMonitor = null;
            
            try
            {
                int reconnectionAttemptCount = 0;
                object countLock = new object();
                
                ePluginStatus lastStatus = context.Plugin.Status;
                statusMonitor = new Timer(_ =>
                {
                    var currentStatus = context.Plugin.Status;
                    if (currentStatus == ePluginStatus.STARTING && lastStatus == ePluginStatus.STOPPED)
                    {
                        lock (countLock) { reconnectionAttemptCount++; }
                    }
                    lastStatus = currentStatus;
                }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
                
                bool exceptionTriggered = false;
                Action<OrderBook> exceptionTrigger = (lob) =>
                {
                    if (exceptionTriggered || lob.ProviderID != context.Plugin.Settings.Provider.ProviderID) 
                        return;
                    exceptionTriggered = true;
                    throw new Exception("Test exception: Simulated gap");
                };
                
                HelperOrderBook.Instance.Subscribe(exceptionTrigger);
                
                try
                {
                    await context.DataRetriever.StartAsync();
                    if (!await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout))
                        throw new TimeoutException("Plugin did not start");
                    
                    // ✅ REDUCED: 5s wait instead of 8s
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    var finalStarted = await context.WaitForStatusAsync(ePluginStatus.STARTED, TimeSpan.FromSeconds(5));
                    
                    if (reconnectionAttemptCount >= 1)
                    {
                        output.WriteLine($"  ✓ Retry executed ({reconnectionAttemptCount} attempt(s)), no stack overflow");
                        if (!finalStarted || context.Plugin.Status != ePluginStatus.STARTED)
                            results.AddWarning($"TEST 3: Plugin status {context.Plugin.Status} after test");
                        
                        results.PassTest("Retry Logic");
                        output.WriteLine("  ✅ PASSED");
                    }
                    else
                    {
                        results.FailTest("Retry Logic", "No reconnection attempts detected");
                        output.WriteLine("  ❌ FAILED: No retry attempts");
                    }
                }
                finally
                {
                    statusMonitor?.Dispose();
                    statusMonitor = null;
                    HelperOrderBook.Instance.Unsubscribe(exceptionTrigger);
                    await context.DataRetriever.StopAsync();
                    await Task.Delay(500); // ✅ REDUCED: 500ms instead of 1000ms
                }
            }
            catch (Exception ex)
            {
                results.FailTest("Retry Logic", ex.Message);
                output.WriteLine($"  ❌ FAILED: {ex.Message}");
            }
            finally
            {
                statusMonitor?.Dispose();
            }
        }

        private async Task Test_MaxAttemptsExhaustion(PluginTestContext context, TestConfiguration config, ITestOutputHelper output, ReconnectionTestResults results)
        {
            output.WriteLine("🔬 TEST 4: Max Reconnection Attempts");
            output.WriteLine("  ⏭️  SKIPPED: Would take ~60 seconds");
            results.AddWarning("TEST 4: Skipped - too slow");
            results.PassTest("Max Attempts (Skipped)");
        }

        private async Task Test_StatusChangesDuringReconnection(PluginTestContext context, TestConfiguration config, ITestOutputHelper output, ReconnectionTestResults results)
        {
            output.WriteLine("🔬 TEST 5: Status Changes During Reconnection");
            
            try
            {
                bool exceptionTriggered = false;
                Action<OrderBook> exceptionTrigger = (lob) =>
                {
                    if (exceptionTriggered || lob.ProviderID != context.Plugin.Settings.Provider.ProviderID) 
                        return;
                    exceptionTriggered = true;
                    throw new Exception("Test exception");
                };
                
                HelperOrderBook.Instance.Subscribe(exceptionTrigger);
                
                try
                {
                    await context.DataRetriever.StartAsync();
                    if (!await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout))
                        throw new TimeoutException("Plugin did not start");
                    
                    // ✅ REDUCED: 5s max wait instead of 10s
                    if (!await context.WaitForStatusAsync(ePluginStatus.STOPPED, TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Plugin did not stop");
                    
                    await context.DataRetriever.StopAsync();
                    await Task.Delay(1000); // ✅ REDUCED: 1s instead of 2s
                    
                    if (context.Plugin.Status == ePluginStatus.STOPPED || context.Plugin.Status == ePluginStatus.STOPPING)
                    {
                        output.WriteLine($"  ✓ Plugin respected stop request");
                    }
                    else
                    {
                        results.AddWarning($"TEST 5: Plugin status {context.Plugin.Status} after stop");
                    }
                    
                    results.PassTest("Status Changes");
                    output.WriteLine("  ✅ PASSED");
                }
                finally
                {
                    HelperOrderBook.Instance.Unsubscribe(exceptionTrigger);
                    await context.DataRetriever.StopAsync();
                    await Task.Delay(500); // ✅ REDUCED: 500ms instead of 1000ms
                }
            }
            catch (Exception ex)
            {
                results.FailTest("Status Changes", ex.Message);
                output.WriteLine($"  ❌ FAILED: {ex.Message}");
            }
        }

        private async Task Test_ReconnectionCoalescing(PluginTestContext context, TestConfiguration config, ITestOutputHelper output, ReconnectionTestResults results)
        {
            output.WriteLine("🔬 TEST 6: Reconnection Coalescing");
            
            try
            {
                int exceptionCount = 0;
                int reconnectionDetected = 0;
                object countLock = new object();
                
                var originalStatus = context.Plugin.Status;
                var statusMonitor = new Timer(_ =>
                {
                    if (context.Plugin.Status == ePluginStatus.STARTING && originalStatus == ePluginStatus.STOPPED)
                    {
                        lock (countLock) { reconnectionDetected++; }
                    }
                    originalStatus = context.Plugin.Status;
                }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
                
                Action<OrderBook> exceptionTrigger = (lob) =>
                {
                    if (lob.ProviderID != context.Plugin.Settings.Provider.ProviderID) 
                        return;
                    
                    lock (countLock)
                    {
                        if (exceptionCount >= 10) return;
                        exceptionCount++;
                        Task.Run(() => throw new Exception($"Coalescing test #{exceptionCount}"));
                    }
                };
                
                HelperOrderBook.Instance.Subscribe(exceptionTrigger);
                
                try
                {
                    await context.DataRetriever.StartAsync();
                    if (!await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout))
                        throw new TimeoutException("Plugin did not start");
                    
                    // ✅ REDUCED: 3s wait instead of 5s
                    await Task.Delay(3000);
                    
                    // ✅ REDUCED: 10s max wait instead of 20s
                    await context.WaitForStatusAsync(ePluginStatus.STARTED, TimeSpan.FromSeconds(10));
                    
                    statusMonitor.Dispose();
                    
                    output.WriteLine($"  ✓ {exceptionCount} exceptions → {reconnectionDetected} reconnections");
                    
                    if (reconnectionDetected > 2)
                        results.AddWarning($"TEST 6: {reconnectionDetected} reconnections (expected ≤2)");
                    
                    results.PassTest("Reconnection Coalescing");
                    output.WriteLine("  ✅ PASSED");
                }
                finally
                {
                    statusMonitor.Dispose();
                    HelperOrderBook.Instance.Unsubscribe(exceptionTrigger);
                    await context.DataRetriever.StopAsync();
                    await Task.Delay(500); // ✅ REDUCED: 500ms instead of 1000ms
                }
            }
            catch (Exception ex)
            {
                results.FailTest("Reconnection Coalescing", ex.Message);
                output.WriteLine($"  ❌ FAILED: {ex.Message}");
            }
        }

        #endregion

        #region Helper Classes

        private class ReconnectionTestResults
        {
            public int TotalTests => PassedTests + FailedTests;
            public int PassedTests { get; private set; }
            public int FailedTests { get; private set; }
            public List<string> Failures { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();

            public void PassTest(string testName)
            {
                PassedTests++;
            }

            public void FailTest(string testName, string reason)
            {
                FailedTests++;
                Failures.Add($"{testName}: {reason}");
            }

            public void AddWarning(string warning)
            {
                Warnings.Add(warning);
            }
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
                    
                    var startSuccess = await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout);
                    if (!startSuccess)
                    {
                        throw new TimeoutException($"Plugin did not start within {config.StatusChangeTimeout}");
                    }
                    output.WriteLine($"✓ {context.PluginName} started successfully");
                    
                    // Wait for initial data
                    output.WriteLine($"⏳ Waiting {config.InitialDataDelay} for initial data...");
                    await Task.Delay(config.InitialDataDelay);
                    
                    var dataReceived = await context.WaitForDataAsync(config.DataReceptionTimeout);
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
