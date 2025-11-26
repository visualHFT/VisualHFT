using System;

namespace VisualHFT.DataRetriever.TestingFramework.Core
{
    /// <summary>
    /// Configuration settings for plugin functional tests
    /// </summary>
    public class TestConfiguration
    {
        /// <summary>
        /// Timeout for plugin status changes (default: 60 seconds)
        /// </summary>
        public TimeSpan StatusChangeTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Timeout for data reception (default: 30 seconds)
        /// </summary>
        public TimeSpan DataReceptionTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Duration for data integrity monitoring (default: 20 seconds)
        /// </summary>
        public TimeSpan IntegrityTestDuration { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Interval between checks during integrity testing (default: 100ms)
        /// </summary>
        public TimeSpan IntegrityCheckInterval { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Time to wait for initial data after plugin start (default: 5 seconds)
        /// </summary>
        public TimeSpan InitialDataDelay { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Maximum time to tolerate crossed spread before considering it an error (default: 1 second)
        /// </summary>
        public TimeSpan CrossedSpreadTolerance { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Maximum depth warning threshold when no MaxDepth is set (default: 30)
        /// </summary>
        public int DepthWarningThreshold { get; set; } = 30;

        /// <summary>
        /// Whether to run tests in parallel (default: false for stability)
        /// </summary>
        public bool RunInParallel { get; set; } = false;

        /// <summary>
        /// Time to wait between test executions to avoid resource conflicts (default: 1 second)
        /// </summary>
        public TimeSpan TestExecutionDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Whether to continue testing other plugins when one fails (default: true)
        /// </summary>
        public bool ContinueOnFailure { get; set; } = true;

        /// <summary>
        /// Timeout for plugin cleanup operations (default: 10 seconds)
        /// </summary>
        public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Creates a configuration optimized for fast testing (shorter timeouts)
        /// </summary>
        public static TestConfiguration Fast()
        {
            return new TestConfiguration
            {
                StatusChangeTimeout = TimeSpan.FromSeconds(30),
                DataReceptionTimeout = TimeSpan.FromSeconds(15),
                IntegrityTestDuration = TimeSpan.FromSeconds(10),
                InitialDataDelay = TimeSpan.FromSeconds(3),
                CrossedSpreadTolerance = TimeSpan.FromMilliseconds(500),
                TestExecutionDelay = TimeSpan.FromMilliseconds(500)
            };
        }

        /// <summary>
        /// Creates a configuration optimized for ultra-fast testing (minimal timeouts for reconnection tests)
        /// /// Use this for tests that don't require real network data (e.g., reconnection logic)
        /// </summary>
        public static TestConfiguration UltraFast()
        {
            return new TestConfiguration
            {
                StatusChangeTimeout = TimeSpan.FromSeconds(15),      // ? Reduced from 60s
                DataReceptionTimeout = TimeSpan.FromSeconds(10),     // ? Reduced from 30s
                IntegrityTestDuration = TimeSpan.FromSeconds(5),     // ? Reduced from 20s
                InitialDataDelay = TimeSpan.FromSeconds(2),          // ? Reduced from 5s
                CrossedSpreadTolerance = TimeSpan.FromMilliseconds(500),
                TestExecutionDelay = TimeSpan.FromMilliseconds(100), // ? Minimal delay
                CleanupTimeout = TimeSpan.FromSeconds(5)             // ? Reduced from 10s
            };
        }

        /// <summary>
        /// Creates a configuration for thorough testing (longer timeouts)
        /// </summary>
        public static TestConfiguration Thorough()
        {
            return new TestConfiguration
            {
                StatusChangeTimeout = TimeSpan.FromSeconds(120),
                DataReceptionTimeout = TimeSpan.FromSeconds(60),
                IntegrityTestDuration = TimeSpan.FromSeconds(60),
                InitialDataDelay = TimeSpan.FromSeconds(10),
                CrossedSpreadTolerance = TimeSpan.FromSeconds(2),
                TestExecutionDelay = TimeSpan.FromSeconds(2)
            };
        }

        /// <summary>
        /// Creates default configuration suitable for most scenarios
        /// </summary>
        public static TestConfiguration Default()
        {
            return new TestConfiguration();
        }
    }
}