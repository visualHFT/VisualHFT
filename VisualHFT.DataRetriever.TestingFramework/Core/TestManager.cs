using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VisualHFT.Commons.Interfaces;
using VisualHFT.PluginManager;
using VisualHFT.Model;
using VisualHFT.Commons.Helpers;
using VisualHFT.Helpers;


namespace VisualHFT.DataRetriever.TestingFramework.Core
{
    /// <summary>
    /// Manages test execution and provides programmatic access to plugin tests
    /// </summary>
    public class TestManager : IDisposable
    {
        private readonly List<IDataRetrieverTestable> _marketConnectors;
        private readonly ScenarioBuilder _scenarioBuilder;
        private readonly TestConfiguration _configuration;
        private bool _disposed = false;

        public TestManager(TestConfiguration? configuration = null)
        {
            _marketConnectors = AssemblyLoader.LoadDataRetrievers();
            _scenarioBuilder = new ScenarioBuilder();
            _configuration = configuration ?? TestConfiguration.Default();
        }

        /// <summary>
        /// Gets the number of loaded plugins available for testing
        /// </summary>
        public int PluginCount => _marketConnectors.Count;

        /// <summary>
        /// Gets the names of all loaded plugins
        /// </summary>
        public IEnumerable<string> PluginNames => _marketConnectors.Select(p => p.GetType().Name);

        /// <summary>
        /// Runs all available tests programmatically
        /// </summary>
        public async Task<TestManagerResult> RunAllTestsAsync(IProgress<string>? progress = null)
        {
            if (_marketConnectors.Count == 0)
            {
                return new TestManagerResult
                {
                    Success = false,
                    Message = "No exchange connectors found for testing",
                    Errors = new List<ErrorReporting>
                    {
                        new ErrorReporting 
                        { 
                            PluginName = "TestEnvironment", 
                            Message = "No plugins loaded", 
                            MessageType = ErrorMessageTypes.ERROR 
                        }
                    }
                };
            }

            var result = new TestManagerResult();
            var allErrors = new List<ErrorReporting>();

            try
            {
                progress?.Report("Starting plugin functional tests...");

                // Create a mock test output helper for programmatic execution
                var outputHelper = new MockTestOutputHelper();
                using var executor = new PluginTestExecutor(outputHelper, _configuration);

                // Run Start/Stop test
                progress?.Report("Running Start/Stop tests...");
                var startStopErrors = await RunStartStopTestAsync(executor, outputHelper);
                allErrors.AddRange(startStopErrors);

                // Run Reconnection test
                progress?.Report("Running Reconnection tests...");
                var reconnectionErrors = await RunReconnectionTestAsync(executor, outputHelper);
                allErrors.AddRange(reconnectionErrors);

                // Run Integrity test
                progress?.Report("Running OrderBook Integrity tests...");
                var integrityErrors = await RunIntegrityTestAsync(executor, outputHelper);
                allErrors.AddRange(integrityErrors);

                result.Success = !allErrors.Any(e => e.MessageType == ErrorMessageTypes.ERROR);
                result.Errors = allErrors;
                result.TestedPluginCount = _marketConnectors.Count;
                result.TestOutput = outputHelper.GetOutput();

                var errorCount = allErrors.Count(e => e.MessageType == ErrorMessageTypes.ERROR);
                var warningCount = allErrors.Count(e => e.MessageType == ErrorMessageTypes.WARNING);

                result.Message = result.Success 
                    ? $"All tests completed successfully. {warningCount} warnings detected."
                    : $"Tests completed with {errorCount} errors and {warningCount} warnings.";

                progress?.Report("Tests completed.");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Test execution failed: {ex.Message}";
                allErrors.Add(new ErrorReporting
                {
                    PluginName = "TestManager",
                    Message = ex.Message,
                    MessageType = ErrorMessageTypes.ERROR
                });
                result.Errors = allErrors;
            }

            return result;
        }

        private async Task<List<ErrorReporting>> RunStartStopTestAsync(PluginTestExecutor executor, MockTestOutputHelper outputHelper)
        {
            return await executor.ExecuteTestAsync(
                "StartStop Test",
                async (context, config, output) =>
                {
                    // Verify initial state
                    if (context.Plugin.Status != ePluginStatus.LOADED)
                        throw new InvalidOperationException($"Expected LOADED status, got {context.Plugin.Status}");

                    // Start the plugin
                    await context.DataRetriever.StartAsync();
                    var startSuccess = await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout);
                    if (!startSuccess)
                        throw new TimeoutException($"Plugin did not start within {config.StatusChangeTimeout}");

                    // Stop the plugin
                    await context.DataRetriever.StopAsync();
                    var stopSuccess = await context.WaitForStatusAsync(ePluginStatus.STOPPED, config.StatusChangeTimeout);
                    if (!stopSuccess)
                        throw new TimeoutException($"Plugin did not stop within {config.StatusChangeTimeout}");

                    return true;
                }
            );
        }

        private async Task<List<ErrorReporting>> RunReconnectionTestAsync(PluginTestExecutor executor, MockTestOutputHelper outputHelper)
        {
            return await executor.ExecuteTestAsync(
                "Reconnection Test",
                async (context, config, output) =>
                {
                    bool exceptionTriggered = false;
                    Action<OrderBook> exceptionTrigger = (lob) =>
                    {
                        if (exceptionTriggered || lob.ProviderID != context.Plugin.Settings.Provider.ProviderID)
                            return;
                        exceptionTriggered = true;
                        throw new Exception("Test exception for reconnection");
                    };

                    HelperOrderBook.Instance.Subscribe(exceptionTrigger);

                    try
                    {
                        await context.DataRetriever.StartAsync();
                        var startSuccess = await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout);
                        if (!startSuccess)
                            throw new TimeoutException("Initial start failed");

                        // Wait for reconnection sequence
                        var stoppedSuccess = await context.WaitForStatusAsync(ePluginStatus.STOPPED, config.StatusChangeTimeout);
                        var startingSuccess = await context.WaitForStatusAsync(ePluginStatus.STARTING, config.StatusChangeTimeout);
                        var restartedSuccess = await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout);

                        if (!stoppedSuccess || !startingSuccess || !restartedSuccess)
                            throw new Exception("Reconnection sequence failed");

                        return true;
                    }
                    finally
                    {
                        HelperOrderBook.Instance.Unsubscribe(exceptionTrigger);
                    }
                }
            );
        }

        private async Task<List<ErrorReporting>> RunIntegrityTestAsync(PluginTestExecutor executor, MockTestOutputHelper outputHelper)
        {
            return await executor.ExecuteTestAsync(
                "Integrity Test",
                async (context, config, output) =>
                {
                    await context.DataRetriever.StartAsync();
                    var startSuccess = await context.WaitForStatusAsync(ePluginStatus.STARTED, config.StatusChangeTimeout);
                    if (!startSuccess)
                        throw new TimeoutException("Plugin did not start");

                    await Task.Delay(config.InitialDataDelay);
                    var dataReceived = await context.WaitForDataAsync(config.DataReceptionTimeout);
                    if (!dataReceived)
                        throw new Exception("No data received within timeout");

                    // Monitor for a shorter period in programmatic tests
                    var monitoringDuration = TimeSpan.FromSeconds(10);
                    var testStartTime = DateTime.Now;

                    while (DateTime.Now.Subtract(testStartTime) < monitoringDuration && context.LastException == null)
                    {
                        var orderBook = context.LastOrderBook;
                        if (orderBook != null)
                        {
                            if (orderBook.Spread < 0)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1));
                                if (orderBook.Spread < 0)
                                    throw new Exception("Persistent crossed spread detected");
                            }
                        }
                        await Task.Delay(config.IntegrityCheckInterval);
                    }

                    if (context.LastException != null)
                        throw context.LastException;

                    return true;
                }
            );
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Cleanup if needed
        }
    }

    /// <summary>
    /// Results from programmatic test execution
    /// </summary>
    public class TestManagerResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<ErrorReporting> Errors { get; set; } = new List<ErrorReporting>();
        public int TestedPluginCount { get; set; }
        public string TestOutput { get; set; } = string.Empty;
    }

    /// <summary>
    /// Mock implementation of ITestOutputHelper for programmatic test execution
    /// </summary>
    public class MockTestOutputHelper : ITestOutputHelper
    {
        private readonly StringBuilder _output = new StringBuilder();

        public string Output => throw new NotImplementedException();

        public void WriteLine(string message)
        {
            _output.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }

        public void WriteLine(string format, params object[] args)
        {
            WriteLine(string.Format(format, args));
        }

        public string GetOutput() => _output.ToString();

        public void Write(string message)
        {
            throw new NotImplementedException();
        }

        public void Write(string format, params object[] args)
        {
            throw new NotImplementedException();
        }
    }
}
