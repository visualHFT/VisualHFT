using System;
using System.Collections;
using System.IO;
using System.Reflection;
using VisualHFT.Commons.Helpers;
using VisualHFT.TriggerEngine;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// Every test in this project drives the static TriggerEngineService, so one
// test class must not reset or reconfigure the engine while another is running.
[assembly: Parallelization(Mode = ParallelMode.None)]
[assembly: VisualHFT.DataRetriever.TestingFramework.TestCases.ResetTriggerEngineStateAroundTriggerEngineTests]

namespace VisualHFT.DataRetriever.TestingFramework.TestCases
{
    /// <summary>
    /// TriggerEngineTests counts the notifications the engine raises, and its tests share one plugin and one
    /// rule name. The engine keeps the rules, the last observation of each metric and each action's last fire
    /// time in static state that outlives a test, and the notification store is a singleton. Without a reset,
    /// the cooldown or the notifications left by an earlier test decide what a later test counts. Before and
    /// after every test of that class this clears that state, and it points the rule file at a temporary
    /// path so the tests never overwrite the rules a user has saved.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class ResetTriggerEngineStateAroundTriggerEngineTestsAttribute : BeforeAfterTestAttribute
    {
        private const string TargetClass = "VisualHFT.DataRetriever.TestingFramework.TestCases.TriggerEngineTests";
        private static readonly string[] StateFields = { "LastMetricValues", "ConditionStartTimes", "ActionLastFiredTimes" };

        private static string? _savedConfigPath;
        private static string? _tempConfigDirectory;

        public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        {
            if (!IsTargeted(methodUnderTest))
            {
                return;
            }

            _savedConfigPath = TriggerEngineService.TriggerEngineConfigFilePath;
            _tempConfigDirectory = Path.Combine(Path.GetTempPath(), $"TE_TriggerEngineTests_{Guid.NewGuid():N}");
            TriggerEngineService.TriggerEngineConfigFilePath = Path.Combine(_tempConfigDirectory, "TriggerEngineConfig.json");
            ResetState();
        }

        public override void After(MethodInfo methodUnderTest, IXunitTest test)
        {
            if (!IsTargeted(methodUnderTest))
            {
                return;
            }

            ResetState();
            if (_savedConfigPath is not null)
            {
                TriggerEngineService.TriggerEngineConfigFilePath = _savedConfigPath;
            }
            if (_tempConfigDirectory is not null && Directory.Exists(_tempConfigDirectory))
            {
                Directory.Delete(_tempConfigDirectory, recursive: true);
            }
        }

        private static bool IsTargeted(MethodInfo methodUnderTest) => methodUnderTest.DeclaringType?.FullName == TargetClass;

        private static void ResetState()
        {
            TriggerEngineService.ClearAllRules();
            foreach (var fieldName in StateFields)
            {
                var field = typeof(TriggerEngineService).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(field);
                ((IDictionary)field!.GetValue(null)!).Clear();
            }
            HelperNotificationManager.Instance.ClearNotifications();
        }
    }
}
