// RED tests (TDD) for two TriggerEngine root-cause defects surfaced by the
// Market Data Recorder gap analysis:
//
//   GAP-MDR-01 — A qualifying breach from a CLEAN engine state must fire.
//     The first-fire branch (TriggerEngineService.cs:196-203) records the
//     last-fire timestamp but leaves ExecuteActionAsync commented out and never
//     calls RaiseOnTriggerFired, so the very first breach is silently dropped;
//     a fire only happens on the second qualifying tick after cooldown. Spec
//     FR-3.3.1 / S-08 AC-S08.2 requires the first fire to fire.
//
//   GAP-MDR-14 / OD-3 — A windowed (sustained-condition) rule (Window.Duration
//     > 0) must only fire after the condition has HELD for the window.
//     ProcessMetric:185 calls EvaluateDirect only; IsConditionSatisfiedWithWindow
//     is dead code, so a windowed rule fires instantly, ignoring its window.
//
// WHY THESE TESTS EXIST AT ALL: the original suite tested the windowed logic by
// reflecting into the private IsConditionSatisfiedWithWindow (TestHelpers.cs:31),
// proving the method works in isolation while never proving it is WIRED into the
// live path. And the first-fire defect was codified as expected behavior
// (Pattern TEST-03 "First-Fire-No-Execute"), with tests primed to skip past it.
// Every assertion below therefore goes through the real RegisterMetric ->
// ProcessMetric pipeline with NO reflection into evaluation internals, so it
// fails if the behavior is not actually reachable in production.
//
// These are RED until the L2 TriggerEngine fix lands (first-fire branch fires +
// IsConditionSatisfiedWithWindow wired into ProcessMetric).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.TriggerEngine;
using VisualHFT.TriggerEngine.Actions;
using Xunit;
using TriggerActionT = VisualHFT.TriggerEngine.TriggerAction;

namespace VisualHFT.TriggerEngine.IntegrationTests;

[Collection("TriggerEngineSerial")]
public sealed class FirstFireAndWindowedConditionTests : IDisposable
{
    private const string Plugin = "TestPlugin";
    private const string Metric = "TestMetric";
    private const string Exchange = "Binance";
    private const string Symbol = "BTCUSDT";

    private readonly string _originalConfigPath;
    private readonly string _testConfigPath;
    private readonly CancellationTokenSource _workerCts = new();
    private readonly Task _workerTask;

    public FirstFireAndWindowedConditionTests()
    {
        _originalConfigPath = TriggerEngineService.TriggerEngineConfigFilePath;
        _testConfigPath = Path.Combine(
            Path.GetTempPath(),
            $"TE_FirstFireWindowed_{Guid.NewGuid():N}",
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

    // ====================================================================
    // GAP-MDR-01 — first fire from a clean state
    // ====================================================================

    /// <summary>
    /// A single GreaterThan breach against a freshly-reset engine MUST raise
    /// OnTriggerFired exactly once. RED today: the first-fire branch records the
    /// timestamp without firing, so no event is raised at all.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task FirstQualifyingBreach_FromCleanState_FiresExactlyOnce()
    {
        var captured = new List<TriggerFiredEventArgs>();
        var fired = new ManualResetEventSlim(initialState: false);
        Action<TriggerFiredEventArgs> handler = a =>
        {
            lock (captured) captured.Add(a);
            fired.Set();
        };

        SubscribeOnTriggerFired(handler);
        try
        {
            long ruleId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rule = BuildRule("FirstFire", ruleId,
                ConditionOperator.GreaterThan, threshold: 100.0, cooldownSeconds: 0);
            TriggerEngineService.AddOrUpdateRule(rule);

            // ONE breach, from clean state. No primer cycle — the spec says the
            // first fire fires.
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 150.0, DateTime.UtcNow);

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
                "GAP-MDR-01: the first qualifying breach from a clean state did not fire.");
            await Task.Delay(50);

            lock (captured)
            {
                Assert.Single(captured);
                Assert.Equal(ruleId, captured[0].RuleID);
                Assert.Equal(150.0, captured[0].Value);
            }
        }
        finally
        {
            UnsubscribeOnTriggerFired(handler);
        }
    }

    /// <summary>
    /// After the first fire fires, a second breach inside the cooldown window
    /// must be suppressed — exactly one fire total. RED today: zero fires (first
    /// fire is dropped), so Assert.Single fails. Guards against over-correcting
    /// the fix into firing on every tick.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task FirstFire_ThenSecondBreachWithinCooldown_FiresExactlyOnce()
    {
        int fireCount = 0;
        var firstFired = new ManualResetEventSlim(initialState: false);
        Action<TriggerFiredEventArgs> handler = _ =>
        {
            Interlocked.Increment(ref fireCount);
            firstFired.Set();
        };

        SubscribeOnTriggerFired(handler);
        try
        {
            long ruleId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1;
            var rule = BuildRule("FirstFireCooldown", ruleId,
                ConditionOperator.GreaterThan, threshold: 100.0, cooldownSeconds: 60);
            TriggerEngineService.AddOrUpdateRule(rule);

            var t0 = DateTime.UtcNow;
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 150.0, t0);
            Assert.True(firstFired.Wait(TimeSpan.FromSeconds(5)),
                "GAP-MDR-01: first breach did not fire.");

            // Second breach 1s later — well inside the 60s cooldown → must NOT fire.
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 160.0, t0.AddSeconds(1));
            await Task.Delay(300);

            Assert.Equal(1, Volatile.Read(ref fireCount));
        }
        finally
        {
            UnsubscribeOnTriggerFired(handler);
        }
    }

    // ====================================================================
    // GAP-MDR-14 / OD-3 — windowed (sustained) condition
    // ====================================================================

    /// <summary>
    /// A rule with Window.Duration = 3s must NOT fire while the condition has
    /// been true for less than the window, even across multiple qualifying ticks.
    /// RED today: ProcessMetric ignores Window, so the rule fires instantly
    /// (after the first-fire primer the second tick fires). This is the
    /// discriminating test — window math uses the metric timestamp, so it is
    /// deterministic without wall-clock sleeps.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WindowedRule_DoesNotFire_WhileConditionHeldBelowWindow()
    {
        int fireCount = 0;
        Action<TriggerFiredEventArgs> handler = _ => Interlocked.Increment(ref fireCount);

        SubscribeOnTriggerFired(handler);
        try
        {
            long ruleId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2;
            var rule = BuildRule("WindowedNoEarlyFire", ruleId,
                ConditionOperator.GreaterThan, threshold: 100.0, cooldownSeconds: 0,
                window: new TimeWindow { Duration = 3, Unit = TimeWindowUnit.Seconds });
            TriggerEngineService.AddOrUpdateRule(rule);

            var t0 = DateTime.UtcNow;
            // Condition continuously true, but all within the 3s window.
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 150.0, t0);
            await Task.Delay(60);
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 151.0, t0.AddSeconds(1));
            await Task.Delay(60);
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 152.0, t0.AddSeconds(2));
            await Task.Delay(300);

            Assert.Equal(0, Volatile.Read(ref fireCount));
        }
        finally
        {
            UnsubscribeOnTriggerFired(handler);
        }
    }

    /// <summary>
    /// Once the condition has held continuously beyond the window, the windowed
    /// rule fires. Companion to the test above: together they pin "fires only
    /// after sustained", not "never fires."
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WindowedRule_Fires_AfterConditionHeldBeyondWindow()
    {
        int fireCount = 0;
        var fired = new ManualResetEventSlim(initialState: false);
        Action<TriggerFiredEventArgs> handler = _ =>
        {
            Interlocked.Increment(ref fireCount);
            fired.Set();
        };

        SubscribeOnTriggerFired(handler);
        try
        {
            long ruleId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3;
            var rule = BuildRule("WindowedFiresAfter", ruleId,
                ConditionOperator.GreaterThan, threshold: 100.0, cooldownSeconds: 0,
                window: new TimeWindow { Duration = 3, Unit = TimeWindowUnit.Seconds });
            TriggerEngineService.AddOrUpdateRule(rule);

            var t0 = DateTime.UtcNow;
            // Start tracking, then a tick beyond the 3s window with the condition
            // still true → must fire.
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 150.0, t0);
            await Task.Delay(60);
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 151.0, t0.AddSeconds(2));
            await Task.Delay(60);
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 152.0, t0.AddSeconds(5));

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
                "GAP-MDR-14: windowed rule did not fire after the condition held beyond its window.");
            Assert.True(Volatile.Read(ref fireCount) >= 1);
        }
        finally
        {
            UnsubscribeOnTriggerFired(handler);
        }
    }

    /// <summary>
    /// If the condition breaks before the window elapses, the sustained timer
    /// resets — a later breach does not inherit the earlier elapsed time, so no
    /// fire occurs until the condition has again held for a full window.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WindowedRule_ResetsTracking_WhenConditionBreaksBeforeWindow()
    {
        int fireCount = 0;
        Action<TriggerFiredEventArgs> handler = _ => Interlocked.Increment(ref fireCount);

        SubscribeOnTriggerFired(handler);
        try
        {
            long ruleId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 4;
            var rule = BuildRule("WindowedReset", ruleId,
                ConditionOperator.GreaterThan, threshold: 100.0, cooldownSeconds: 0,
                window: new TimeWindow { Duration = 3, Unit = TimeWindowUnit.Seconds });
            TriggerEngineService.AddOrUpdateRule(rule);

            var t0 = DateTime.UtcNow;
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 150.0, t0);              // true, start
            await Task.Delay(60);
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 50.0, t0.AddSeconds(2)); // breaks → reset
            await Task.Delay(60);
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 151.0, t0.AddSeconds(3)); // true again, restart
            await Task.Delay(60);
            TriggerEngineService.RegisterMetric(Plugin, Metric, Exchange, Symbol, 152.0, t0.AddSeconds(5)); // only 2s since restart < 3s
            await Task.Delay(300);

            Assert.Equal(0, Volatile.Read(ref fireCount));
        }
        finally
        {
            UnsubscribeOnTriggerFired(handler);
        }
    }

    // ---------- helpers (mirror OnTriggerFiredEventTests) -----------------

    private static TriggerRule BuildRule(
        string name, long ruleId, ConditionOperator op, double threshold,
        int cooldownSeconds, TimeWindow? window = null)
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
                    Plugin = Plugin,
                    Metric = Metric,
                    Operator = op,
                    Threshold = threshold,
                    Window = window,
                }
            },
            Actions = new List<TriggerActionT>
            {
                new TriggerActionT
                {
                    ActionID = ruleId,
                    Type = ActionType.UIAlert,
                    CooldownDuration = cooldownSeconds,
                    CooldownUnit = TimeWindowUnit.Seconds,
                }
            }
        };
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
