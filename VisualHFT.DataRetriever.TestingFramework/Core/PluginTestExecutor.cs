using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VisualHFT.Commons.Interfaces;
using VisualHFT.PluginManager;

namespace VisualHFT.DataRetriever.TestingFramework.Core
{
    /// <summary>
    /// Manages the execution of plugin tests with proper isolation and resource management
    /// </summary>
    public class PluginTestExecutor : IDisposable
    {
        private readonly ITestOutputHelper _testOutputHelper;
        private readonly TestConfiguration _configuration;
        private readonly List<IDataRetrieverTestable> _plugins;
        private readonly List<PluginTestContext> _activeContexts = new List<PluginTestContext>();
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        public PluginTestExecutor(ITestOutputHelper testOutputHelper, TestConfiguration? configuration = null)
        {
            _testOutputHelper = testOutputHelper ?? throw new ArgumentNullException(nameof(testOutputHelper));
            _configuration = configuration ?? TestConfiguration.Default();
            _plugins = AssemblyLoader.LoadDataRetrievers();
            
            _testOutputHelper.WriteLine($"Loaded {_plugins.Count} plugins for testing");
            _testOutputHelper.WriteLine($"Test configuration: Status timeout={_configuration.StatusChangeTimeout}, Data timeout={_configuration.DataReceptionTimeout}");
        }

        /// <summary>
        /// Executes a test function against all loaded plugins
        /// </summary>
        public async Task<List<ErrorReporting>> ExecuteTestAsync<T>(
            string testName,
            Func<PluginTestContext, TestConfiguration, ITestOutputHelper, Task<T>> testFunction,
            Func<T, bool>? successPredicate = null)
        {
            var errors = new List<ErrorReporting>();
            _testOutputHelper.WriteLine($"\n=== Starting {testName} ===");
            _testOutputHelper.WriteLine($"Testing {_plugins.Count} plugins...\n");

            var testTasks = new List<Task>();
            
            foreach (var plugin in _plugins)
            {
                if (_configuration.RunInParallel)
                {
                    testTasks.Add(ExecuteSinglePluginTestAsync(plugin, testName, testFunction, successPredicate, errors));
                }
                else
                {
                    await ExecuteSinglePluginTestAsync(plugin, testName, testFunction, successPredicate, errors);
                    
                    // Add delay between tests to prevent resource conflicts
                    if (_configuration.TestExecutionDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_configuration.TestExecutionDelay);
                    }
                }
            }

            if (_configuration.RunInParallel && testTasks.Any())
            {
                await Task.WhenAll(testTasks);
            }

            _testOutputHelper.WriteLine($"\n=== {testName} Complete ===");
            _testOutputHelper.WriteLine($"Plugins tested: {_plugins.Count}");
            _testOutputHelper.WriteLine($"Errors: {errors.Count(e => e.MessageType == ErrorMessageTypes.ERROR)}");
            _testOutputHelper.WriteLine($"Warnings: {errors.Count(e => e.MessageType == ErrorMessageTypes.WARNING)}");

            return errors;
        }

        private async Task ExecuteSinglePluginTestAsync<T>(
            IDataRetrieverTestable plugin,
            string testName,
            Func<PluginTestContext, TestConfiguration, ITestOutputHelper, Task<T>> testFunction,
            Func<T, bool>? successPredicate,
            List<ErrorReporting> errors)
        {
            PluginTestContext? context = null;
            var pluginName = plugin.GetType().Name;
            
            try
            {
                _testOutputHelper.WriteLine($"[{testName}] Starting test for {pluginName}");
                
                context = new PluginTestContext(plugin);
                lock (_lockObject)
                {
                    _activeContexts.Add(context);
                }

                var result = await testFunction(context, _configuration, _testOutputHelper);
                
                // Check success predicate if provided
                var success = successPredicate?.Invoke(result) ?? true;
                
                if (success)
                {
                    _testOutputHelper.WriteLine($"[{testName}] ? {pluginName} - PASSED");
                }
                else
                {
                    var error = $"Test completed but success predicate failed. Result: {result}";
                    errors.Add(new ErrorReporting 
                    { 
                        PluginName = pluginName, 
                        Message = error, 
                        MessageType = ErrorMessageTypes.ERROR 
                    });
                    _testOutputHelper.WriteLine($"[{testName}] ? {pluginName} - FAILED: {error}");
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"Test execution failed: {ex.Message}";
                errors.Add(new ErrorReporting 
                { 
                    PluginName = pluginName, 
                    Message = errorMessage, 
                    MessageType = ErrorMessageTypes.ERROR 
                });
                _testOutputHelper.WriteLine($"[{testName}] ? {pluginName} - ERROR: {errorMessage}");
                
                if (!_configuration.ContinueOnFailure)
                {
                    throw;
                }
            }
            finally
            {
                if (context != null)
                {
                    try
                    {
                        context.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _testOutputHelper.WriteLine($"[{testName}] Warning: Error disposing context for {pluginName}: {ex.Message}");
                    }
                    finally
                    {
                        lock (_lockObject)
                        {
                            _activeContexts.Remove(context);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the number of loaded plugins available for testing
        /// </summary>
        public int PluginCount => _plugins.Count;

        /// <summary>
        /// Gets the names of all loaded plugins
        /// </summary>
        public IEnumerable<string> PluginNames => _plugins.Select(p => p.GetType().Name);

        /// <summary>
        /// Validates the test environment and reports any issues
        /// </summary>
        public List<ErrorReporting> ValidateTestEnvironment()
        {
            var errors = new List<ErrorReporting>();

            if (_plugins.Count == 0)
            {
                errors.Add(new ErrorReporting
                {
                    PluginName = "TestEnvironment",
                    Message = "No plugins loaded for testing",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }

            // Check for duplicate plugin names
            var duplicateNames = _plugins
                .GroupBy(p => p.GetType().Name)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var name in duplicateNames)
            {
                errors.Add(new ErrorReporting
                {
                    PluginName = "TestEnvironment",
                    Message = $"Duplicate plugin name found: {name}",
                    MessageType = ErrorMessageTypes.WARNING
                });
            }

            return errors;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Dispose all active contexts
            lock (_lockObject)
            {
                foreach (var context in _activeContexts.ToList())
                {
                    try
                    {
                        context.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _testOutputHelper?.WriteLine($"Error disposing context: {ex.Message}");
                    }
                }
                _activeContexts.Clear();
            }
        }
    }
}