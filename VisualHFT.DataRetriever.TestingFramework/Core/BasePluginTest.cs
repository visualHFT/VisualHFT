using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace VisualHFT.DataRetriever.TestingFramework.Core
{
    /// <summary>
    /// Base class for plugin functional tests providing common infrastructure
    /// </summary>
    public abstract class BasePluginTest : IDisposable
    {
        protected readonly ITestOutputHelper TestOutputHelper;
        protected readonly PluginTestExecutor TestExecutor;
        protected readonly TestConfiguration Configuration;
        private bool _disposed = false;

        protected BasePluginTest(ITestOutputHelper testOutputHelper, TestConfiguration? configuration = null)
        {
            TestOutputHelper = testOutputHelper ?? throw new ArgumentNullException(nameof(testOutputHelper));
            Configuration = configuration ?? TestConfiguration.Default();
            TestExecutor = new PluginTestExecutor(testOutputHelper, Configuration);
            
            // Log test initialization
            TestOutputHelper.WriteLine($"Initialized {GetType().Name} with {TestExecutor.PluginCount} plugins");
            TestOutputHelper.WriteLine($"Configuration: {Configuration.GetType().Name}");
        }

        /// <summary>
        /// Executes a test and handles error reporting with performance measurement
        /// </summary>
        protected async Task ExecuteTestWithReporting<T>(
            string testName,
            Func<PluginTestContext, TestConfiguration, ITestOutputHelper, Task<T>> testFunction,
            Func<T, bool>? successPredicate = null)
        {
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                TestOutputHelper.WriteLine($"\n?? Starting {testName}");
                TestOutputHelper.WriteLine($"? Test started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                
                var errors = await TestExecutor.ExecuteTestAsync(testName, testFunction, successPredicate);
                
                stopwatch.Stop();
                
                var successfulPlugins = TestExecutor.PluginCount - errors.Count;
                var summary = TestResultFormatter.CreateTestSummary(
                    testName, 
                    TestExecutor.PluginCount, 
                    successfulPlugins, 
                    errors, 
                    stopwatch.Elapsed);
                
                TestOutputHelper.WriteLine(summary);
                
                if (errors.Any())
                {
                    var errorReport = TestResultFormatter.FormatErrorReport(errors, testName);
                    TestOutputHelper.WriteLine(errorReport);
                    
                    if (errors.Any(e => e.MessageType == ErrorMessageTypes.ERROR))
                    {
                        Assert.Fail($"{testName} failed with {errors.Count(e => e.MessageType == ErrorMessageTypes.ERROR)} errors. See output for details.");
                    }
                }
                else
                {
                    TestOutputHelper.WriteLine($"? {testName} completed successfully!");
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                TestOutputHelper.WriteLine($"? {testName} failed with exception after {stopwatch.Elapsed:mm\\:ss\\.fff}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Validates that plugins are available for testing
        /// </summary>
        protected void ValidateTestEnvironment()
        {
            var environmentErrors = TestExecutor.ValidateTestEnvironment();
            
            if (environmentErrors.Any(e => e.MessageType == ErrorMessageTypes.ERROR))
            {
                var errorMessages = string.Join(", ", 
                    environmentErrors
                        .Where(e => e.MessageType == ErrorMessageTypes.ERROR)
                        .Select(e => e.Message));
                        
                throw new InvalidOperationException($"Test environment validation failed: {errorMessages}");
            }
            
            if (environmentErrors.Any(e => e.MessageType == ErrorMessageTypes.WARNING))
            {
                var warningMessages = string.Join(", ", 
                    environmentErrors
                        .Where(e => e.MessageType == ErrorMessageTypes.WARNING)
                        .Select(e => e.Message));
                        
                TestOutputHelper.WriteLine($"??  Test environment warnings: {warningMessages}");
            }
        }

        /// <summary>
        /// Gets information about available plugins for testing
        /// </summary>
        protected void LogAvailablePlugins()
        {
            TestOutputHelper.WriteLine($"Available plugins for testing ({TestExecutor.PluginCount}):");
            foreach (var pluginName in TestExecutor.PluginNames)
            {
                TestOutputHelper.WriteLine($"  • {pluginName}");
            }
            TestOutputHelper.WriteLine($""); // Empty line
        }

        public virtual void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            TestExecutor?.Dispose();
        }
    }
}