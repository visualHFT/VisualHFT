// Tests for the replay that AddOrUpdateRule and RemoveRule perform after a
// rule change.
//
// A rule change re-presents the latest stored observation of every metric
// (LastMetricValues) so the rules are evaluated against standing state. That
// snapshot must be taken when the rule changes. It used to be taken later, on
// the thread pool, so it reflected whatever was stored at some unknown later
// moment: after newer metrics arrived, or after a caller had reset the engine
// state, in which case the replay was lost or carried a reading from a context
// it no longer belonged to.
//
// Each test resets the stored metrics immediately after the rule change returns.
// The replay is only seen if it was queued before the call returned.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.TriggerEngine;
using Xunit;
using TriggerActionT = VisualHFT.TriggerEngine.TriggerAction;

namespace VisualHFT.TriggerEngine.IntegrationTests;

[Collection("TriggerEngineSerial")]
public sealed class RuleChangeReplayTests : IDisposable
{
    private const string Plugin = "ReplaySnapshotPlugin";
    private const string Metric = "TestMetric";
    private const string Exchange = "Binance";
    private const string Symbol = "BTCUSDT";

    private readonly string _originalConfigPath;
    private readonly string _testConfigPath;
    private readonly CancellationTokenSource _workerCts = new();
    private readonly Task _workerTask;

    public RuleChangeReplayTests()
    {
        _originalConfigPath = TriggerEngineService.TriggerEngineConfigFilePath;
        _testConfigPath = Path.Combine(
            Path.GetTempPath(),
            $"TE_RuleChangeReplay_{Guid.NewGuid():N}",
            "TriggerEngineConfig.json");
        TriggerEngineService.TriggerEngineConfigFilePath = _testConfigPath;

        ResetEngineState();
        ResetOnTriggerFiredField();
        _workerTask = TriggerEngineService.StartBackgroundWorkerAsync(_workerCts.Token);
    }

    public void Dispose()
    {
        try
        {
            ResetOnTriggerFiredField();
            _workerCts.Cancel();
            try { _workerTask.Wait(2_000); } catch { /* drained */ }
            ResetEngineState();
            TriggerEngineService.TriggerEngineConfigFilePath = _originalConfigPath;
            var dir = Path.GetDirectoryName(_testConfigPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// The replay must be queued by the time AddOrUpdateRule returns, so a
    /// reset of the stored metrics right after the call cannot erase it. The
    /// new rule then fires on the ORIGINAL observation (value and timestamp).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void AddOrUpdateRule_QueuesReplayBeforeReturning()
    {
        string metricKey = $"{Plugin}.{Metric}.{Exchange}.{Symbol}";
        var observedAt = DateTime.UtcNow.AddMinutes(-1);

        // Store a standing observation through the real pipeline. No rule exists
        // yet, so nothing fires.
        TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 150.0, observedAt);
        Assert.True(SpinWait.SpinUntil(() => LastMetricValuesContains(metricKey), TimeSpan.FromSeconds(5)),
            "The standing observation was never stored.");

        long ruleId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var captured = new List<TriggerFiredEventArgs>();
        var fired = new ManualResetEventSlim(initialState: false);
        Action<TriggerFiredEventArgs> handler = a =>
        {
            if (a.RuleID != ruleId) return;
            lock (captured) captured.Add(a);
            fired.Set();
        };

        SubscribeOnTriggerFired(handler);
        try
        {
            var rule = BuildRule($"ReplaySnapshot_{Guid.NewGuid():N}", ruleId,
                ConditionOperator.GreaterThan, threshold: 100.0);
            TriggerEngineService.AddOrUpdateRule(rule);

            // A caller resets the engine state right after the rule change.
            ClearStaticDictionary("LastMetricValues");

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                "The rule-change replay was not queued before AddOrUpdateRule returned.");

            lock (captured)
            {
                Assert.Single(captured);
                Assert.Equal(150.0, captured[0].Value);
                Assert.Equal(observedAt, captured[0].Timestamp);
            }
        }
        finally
        {
            UnsubscribeOnTriggerFired(handler);
        }
    }

    /// <summary>
    /// RemoveRule replays the stored metrics too, so the remaining rules are
    /// evaluated again. That replay must also be queued by the time RemoveRule
    /// returns: a reset of the stored metrics right after the call must not
    /// erase it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void RemoveRule_QueuesReplayBeforeReturning()
    {
        string metricKey = $"{Plugin}.{Metric}.{Exchange}.{Symbol}";
        var observedAt = DateTime.UtcNow.AddMinutes(-1);

        // The rule to remove watches another plugin. It is added before any
        // observation is stored, so its own replay carries nothing.
        long unrelatedRuleId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        TriggerEngineService.AddOrUpdateRule(BuildRule($"ReplayUnrelated_{Guid.NewGuid():N}", unrelatedRuleId,
            ConditionOperator.GreaterThan, threshold: 100.0, plugin: "ReplayUnrelatedPlugin"));

        TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 150.0, observedAt);
        Assert.True(SpinWait.SpinUntil(() => LastMetricValuesContains(metricKey), TimeSpan.FromSeconds(5)),
            "The standing observation was never stored.");

        long ruleId = unrelatedRuleId + 1;
        var captured = new List<TriggerFiredEventArgs>();
        var fired = new SemaphoreSlim(0);
        Action<TriggerFiredEventArgs> handler = a =>
        {
            if (a.RuleID != ruleId) return;
            lock (captured) captured.Add(a);
            fired.Release();
        };

        SubscribeOnTriggerFired(handler);
        try
        {
            // The watching rule fires once on the replay of its own addition.
            TriggerEngineService.AddOrUpdateRule(BuildRule($"ReplayWatcher_{Guid.NewGuid():N}", ruleId,
                ConditionOperator.GreaterThan, threshold: 100.0));
            Assert.True(fired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                "The added rule did not fire on the standing observation.");

            // Forget that it fired, so the next replay may fire it again.
            ClearStaticDictionary("ActionLastFiredTimes");

            TriggerEngineService.RemoveRule(unrelatedRuleId);

            // A caller resets the engine state right after the rule change.
            ClearStaticDictionary("LastMetricValues");

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                "The rule-change replay was not queued before RemoveRule returned.");

            lock (captured)
            {
                Assert.Equal(2, captured.Count);
                Assert.Equal(150.0, captured[1].Value);
                Assert.Equal(observedAt, captured[1].Timestamp);
            }
        }
        finally
        {
            UnsubscribeOnTriggerFired(handler);
        }
    }

    // ---------- helpers (mirror FirstFireAndWindowedConditionTests) -------

    private static TriggerRule BuildRule(
        string name, long ruleId, ConditionOperator op, double threshold, string plugin = Plugin)
    {
        return new TriggerRule
        {
            Name = name,
            RuleID = ruleId,
            IsEnabled = true,
            Condition = new List<TriggerCondition>
            {
                new TriggerCondition
                {
                    ConditionID = ruleId,
                    Plugin = plugin,
                    Metric = Metric,
                    Operator = op,
                    Threshold = threshold,
                }
            },
            Actions = new List<TriggerActionT>
            {
                new TriggerActionT
                {
                    ActionID = ruleId,
                    Type = ActionType.UIAlert,
                    CooldownDuration = 0,
                    CooldownUnit = TimeWindowUnit.Seconds,
                }
            }
        };
    }

    private static bool LastMetricValuesContains(string metricKey)
    {
        var field = typeof(TriggerEngineService)
            .GetField("LastMetricValues", BindingFlags.Static | BindingFlags.NonPublic);
        var dict = field?.GetValue(null);
        var containsKey = dict?.GetType().GetMethod("ContainsKey");
        return containsKey is not null && (bool)containsKey.Invoke(dict, new object[] { metricKey })!;
    }

    private static void SubscribeOnTriggerFired(Action<TriggerFiredEventArgs> handler)
    {
        var addMethod = typeof(TriggerEngineService)
            .GetMethod("add_OnTriggerFired",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (addMethod is null)
            throw new InvalidOperationException("TriggerEngineService.add_OnTriggerFired not found.");
        addMethod.Invoke(null, new object[] { handler });
    }

    private static void UnsubscribeOnTriggerFired(Action<TriggerFiredEventArgs> handler)
    {
        var removeMethod = typeof(TriggerEngineService)
            .GetMethod("remove_OnTriggerFired",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        removeMethod?.Invoke(null, new object[] { handler });
    }

    private static void ResetOnTriggerFiredField()
    {
        var field = typeof(TriggerEngineService)
            .GetField("OnTriggerFired",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        field?.SetValue(null, null);
    }

    private static void ResetEngineState()
    {
        TriggerEngineService.ClearAllRules();
        ClearStaticDictionary("LastMetricValues");
        ClearStaticDictionary("ConditionStartTimes");
        ClearStaticDictionary("ActionLastFiredTimes");
    }

    private static void ClearStaticDictionary(string fieldName)
    {
        var field = typeof(TriggerEngineService)
            .GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        if (field is null) return;
        var dict = field.GetValue(null);
        var clearMethod = dict?.GetType().GetMethod("Clear");
        clearMethod?.Invoke(dict, null);
    }
}
