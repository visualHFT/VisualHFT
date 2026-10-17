using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VisualHFT.Commons.Helpers;

namespace VisualHFT.TriggerEngine
{

    // IsReplay marks an observation that EvaluateAllRulesAgainstLatestMetrics
    // re-presented after a rule-config change, as opposed to a live tick from
    // RegisterMetric. A replay is a RE-presentation of an observation the engine
    // has already seen, so it must never re-fire an action that already fired for
    // it (see ProcessMetric). Defaulted so every existing construction site and
    // the record's value-equality semantics are unchanged.
    public record MetricEvent(string Plugin, string Metric, string Exchange, string Symbol, double Value, DateTime Timestamp, bool IsReplay = false);


    /// <summary>
    /// Core service responsible for managing trigger rules, evaluating metric updates in real time,
    /// and executing defined actions when rule conditions are met.
    /// Acts as the central entry point for all plugin metric registrations.
    /// </summary>
    public static class TriggerEngineService
    {
        private static readonly log4net.ILog log =
            log4net.LogManager.GetLogger(typeof(TriggerEngineService));

        public static string TriggerEngineConfigFileName = "TriggerEngineConfig.json";
        public static string TriggerEngineConfigFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualHFT",
            TriggerEngineConfigFileName);

        private static readonly List<TriggerRule> lstRule = new();
        private static readonly object ruleLock = new();

        // The OBSERVATION timestamp is stored alongside the value so a config-change
        // replay can re-present the original observation faithfully instead of
        // fabricating DateTime.UtcNow — a fabricated timestamp silently advances
        // sustained-condition windows that no market data ever satisfied.
        private static readonly ConcurrentDictionary<string, (double Value, DateTime Timestamp)> LastMetricValues = new();
        private static readonly ConcurrentDictionary<string, DateTime> ConditionStartTimes = new();
        private static readonly ConcurrentDictionary<string, DateTime> ActionLastFiredTimes = new();
        // Per rule+condition: last evaluation of THAT condition's own series.
        // Multiple conditions on one rule are AND — a missing/false entry means
        // the conjunction is not met. Updated only when that condition's plugin ticks.
        // Keyed by the condition's POSITION on the rule, never by ConditionID:
        // the rule dialog stamps every condition of a new rule in the same
        // UtcNow millisecond, so saved configs carry duplicate ConditionIDs and
        // an ID-based key collapses distinct conditions into one shared slot.
        private static readonly ConcurrentDictionary<string, bool> ConditionCurrentlyMet = new();

        private static readonly Channel<MetricEvent> MetricChannel = Channel.CreateUnbounded<MetricEvent>();

        // Additive fan-in event for any subscriber that needs to react to rule
        // fires. Purely additive: zero behavioural change for non-subscribers.
        // The raise site lives at the bottom of ProcessMetric (after a fire
        // passes the cooldown gate). Subscribers are invoked individually so a
        // throwing handler never breaks others.
        public static event Action<TriggerFiredEventArgs>? OnTriggerFired;


        /// <summary>   
        /// Registers a new incoming metric value from any plugin.
        /// This method is called by plugins whenever a tracked metric is updated.
        /// </summary>
        /// <param name="pluginID">Name of the plugin emitting the metric.</param>
        /// <param name="pluginName">Metric identifier.</param>
        /// <param name="value">Numeric value of the metric.</param>
        /// <param name="timestamp">Timestamp of the value.</param>
        public static void RegisterMetric(string pluginID, string pluginName, string exchange, string symbol, double value, DateTime timestamp)
        {
            // 1. Store value in memory (e.g., rolling buffer)
            // 2. Find active rules matching this plugin + metric
            // 3. Evaluate each rule
            // 4. If condition is met, execute all associated actions 

            _ = MetricChannel.Writer.WriteAsync(new MetricEvent(pluginID, pluginName, exchange, symbol, value, timestamp));
        }

        public static void AddOrUpdateRule(TriggerRule rule)
        {
            lock (ruleLock)
            {
                var existing = lstRule.Find(r => r.RuleID == rule.RuleID);
                if (existing != null) lstRule.Remove(existing);
                lstRule.Add(rule);

                string directoryPath = Path.GetDirectoryName(TriggerEngineConfigFilePath);
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
                string json = JsonConvert.SerializeObject(lstRule, Formatting.Indented);
                File.WriteAllText(TriggerEngineConfigFilePath, json);

            }
            ClearConditionStateForRule(rule.RuleID);
            LoadAllRules();
            Task.Run(() => EvaluateAllRulesAgainstLatestMetrics());
        }

        public static void RemoveRule(long RuleID)
        {
            lock (ruleLock)
            {
                var rule = lstRule.FirstOrDefault(x => x.RuleID == RuleID);
                if (rule != null)
                {
                    lstRule.Remove(rule);
                    string json = JsonConvert.SerializeObject(lstRule, Formatting.Indented);
                    File.WriteAllText(TriggerEngineConfigFilePath, json);

                }
            }
            ClearConditionStateForRule(RuleID);
            LoadAllRules();
            Task.Run(() => EvaluateAllRulesAgainstLatestMetrics());
        }

        // Condition state is keyed by position on the rule, so an edit that
        // reorders/replaces conditions would otherwise let a stale slot stand
        // in for a condition that has never been evaluated. The config-change
        // replay re-establishes fresh state from LastMetricValues.
        private static void ClearConditionStateForRule(long ruleId)
        {
            string prefix = $"{ruleId}|";
            foreach (var key in ConditionCurrentlyMet.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    ConditionCurrentlyMet.TryRemove(key, out _);
            }
            foreach (var key in ConditionStartTimes.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    ConditionStartTimes.TryRemove(key, out _);
            }
        }
        public static void ClearAllRules()
        {
            lock (ruleLock)
            {
                lstRule.Clear();
            }
        }
        public static List<TriggerRule> GetRules()
        {
            lock (ruleLock)
            {
                return lstRule.ToList();
            }
        }
        public static void StopRule(string name)
        {
            lock (ruleLock)
            {
                TriggerRule? rule = lstRule.FirstOrDefault(x => x.Name == name);
                if (rule != null)
                {
                    rule.IsEnabled = false;

                }
            }
        }
        public static void StartRule(string name)
        {
            lock (ruleLock)
            {
                var rule = lstRule.FirstOrDefault(x => x.Name == name);
                if (rule != null)
                {
                    rule.IsEnabled = true;
                }
            }
        }
        public static void LoadAllRules()
        {
            lstRule.Clear();
            string directoryPath = Path.GetDirectoryName(TriggerEngineConfigFilePath);
            string filePath = Path.Combine(directoryPath, TriggerEngineConfigFileName);
            if (!File.Exists(filePath))
                return;

            string ruleJSON = File.ReadAllText(filePath);

            var rules = JsonConvert.DeserializeObject<List<TriggerRule>>(ruleJSON);
            lstRule.AddRange(rules);
        }
        public static async Task StartBackgroundWorkerAsync(CancellationToken token)
        {
            while (await MetricChannel.Reader.WaitToReadAsync(token))
            {
                while (MetricChannel.Reader.TryRead(out var metricEvent))
                {
                    ProcessMetric(metricEvent);
                }
            }
        }

        private static void ProcessMetric(MetricEvent e)
        {
            string metricKey = $"{e.Plugin}.{e.Metric}.{e.Exchange}.{e.Symbol}";
            var previous = LastMetricValues.TryGetValue(metricKey, out var lastObservation)
                ? lastObservation.Value
                : double.NaN;
            LastMetricValues[metricKey] = (e.Value, e.Timestamp);

            var ruleSnapshot = GetRules();

            foreach (var rule in ruleSnapshot)
            {
                if (!rule.IsEnabled) continue;
                if (rule.Condition == null || rule.Condition.Count == 0) continue;

                bool tickMatchesRule = false;
                for (int i = 0; i < rule.Condition.Count; i++)
                {
                    if (ConditionMatchesTick(rule.Condition[i], e))
                    {
                        tickMatchesRule = true;
                        break;
                    }
                }
                if (!tickMatchesRule)
                    continue;

                // Update satisfaction only for conditions that this tick belongs to.
                // Sibling conditions keep the last result from their own series.
                TriggerCondition? triggeringCondition = null;
                for (int i = 0; i < rule.Condition.Count; i++)
                {
                    var condition = rule.Condition[i];
                    if (!ConditionMatchesTick(condition, e))
                        continue;

                    if (triggeringCondition == null)
                        triggeringCondition = condition;

                    // A rule with a sustained Window must only
                    // count as met once the condition has HELD for the full window.
                    // Previously ProcessMetric called EvaluateDirect unconditionally
                    // and IsConditionSatisfiedWithWindow was dead code, so windowed
                    // rules fired instantly, ignoring their window. Key the window
                    // tracking per rule+condition+symbol so independent symbols and
                    // rules do not share a start time.
                    bool isConditionMet;
                    if (condition.Window != null && condition.Window.Duration > 0)
                    {
                        string conditionKey = $"{rule.RuleID}|{i}|{metricKey}";
                        isConditionMet = IsConditionSatisfiedWithWindow(
                            condition, e.Value, previous, e.Timestamp, conditionKey);
                    }
                    else
                    {
                        isConditionMet = EvaluateDirect(condition, e.Value, previous);
                    }

                    ConditionCurrentlyMet[ConditionStateKey(rule.RuleID, i)] = isConditionMet;
                }

                // AND: every condition on the rule must currently be true.
                // Never-seen (no last tick) is false.
                bool allMet = true;
                for (int i = 0; i < rule.Condition.Count; i++)
                {
                    if (!ConditionCurrentlyMet.TryGetValue(
                            ConditionStateKey(rule.RuleID, i), out var met)
                        || !met)
                    {
                        allMet = false;
                        break;
                    }
                }
                if (!allMet)
                    continue;

                triggeringCondition ??= rule.Condition[0];

                for (int j = 0; j < rule.Actions.Count; j++)
                {
                    var action = rule.Actions[j];
                    string actionKey = $"{rule.Name}|{j}";

                    var cooldown = GetCooldownSpan(action.CooldownDuration, action.CooldownUnit);

                    if (!ActionLastFiredTimes.TryGetValue(actionKey, out var lastFireTime))
                    {
                        // The FIRST qualifying fire from a clean state must
                        // fire: the first breach is treated like any other.
                        // Previously this branch
                        // only recorded the timestamp (ExecuteActionAsync and the
                        // OnTriggerFired raise were commented out), so the first
                        // breach was silently dropped and a fire only happened on
                        // the second qualifying tick after cooldown.
                        ActionLastFiredTimes[actionKey] = e.Timestamp;
                        _ = ExecuteActionAsync(rule.Name, triggeringCondition, action, e.Plugin, e.Metric, e.Exchange, e.Symbol, e.Value, e.Timestamp);
                        RaiseOnTriggerFired(rule, triggeringCondition, e);
                    }
                    else
                    {
                        // A replay is a RE-presentation of an observation this action
                        // already fired on — not new market data. Letting it through
                        // means every rule-config edit re-fires every rule currently
                        // in breach whose cooldown has elapsed, raising phantom alerts
                        // off stale values. Replays may only fire an action that has
                        // NEVER fired (the first-fire branch above), which is exactly
                        // the "evaluate a newly added rule against standing state"
                        // behaviour the replay exists for. Live ticks are unaffected.
                        if (e.IsReplay)
                            continue;

                        if ((e.Timestamp - lastFireTime) >= cooldown)
                        {
                            // Cooldown passed, fire again
                            ActionLastFiredTimes[actionKey] = e.Timestamp;
                            _ = ExecuteActionAsync(rule.Name, triggeringCondition, action, e.Plugin, e.Metric, e.Exchange, e.Symbol, e.Value, e.Timestamp);

                            // Raise OnTriggerFired AFTER the cooldown passes
                            // (matches the fire semantics of ExecuteActionAsync).
                            // Per-handler try/catch keeps a misbehaving subscriber
                            // from poisoning the rest of the invocation list.
                            RaiseOnTriggerFired(rule, triggeringCondition, e);
                        }
                        // else: cooldown not passed, do nothing
                    }
                }
            }
        }

        private static string ConditionStateKey(long ruleId, int conditionIndex)
        {
            return $"{ruleId}|{conditionIndex}";
        }

        // A condition binds to its plugin AND its metric. A null/empty Metric is
        // a legacy wildcard: configs saved before the rule dialog captured a
        // metric name carry Metric = null and keep matching any metric the
        // plugin emits (today each plugin instance emits one metric series).
        private static bool ConditionMatchesTick(TriggerCondition condition, MetricEvent e)
        {
            if (condition.Plugin != e.Plugin)
                return false;
            return string.IsNullOrEmpty(condition.Metric) || condition.Metric == e.Metric;
        }

        // Per-subscriber fan-out for OnTriggerFired. Iterating GetInvocationList
        // is required so a throwing subscriber does not abort the multicast — the
        // default `event(args)` form short-circuits on the first thrown exception.
        private static void RaiseOnTriggerFired(TriggerRule rule, TriggerCondition condition, MetricEvent e)
        {
            var snapshot = OnTriggerFired;
            if (snapshot is null) return;

            var args = new TriggerFiredEventArgs(
                RuleID: rule.RuleID,
                RuleName: rule.Name,
                Plugin: condition.Plugin,
                Metric: condition.Metric,
                Exchange: e.Exchange,
                Symbol: e.Symbol,
                Value: e.Value,
                Threshold: condition.Threshold,
                Operator: condition.Operator,
                Timestamp: e.Timestamp);

            var handlers = snapshot.GetInvocationList();
            for (int i = 0; i < handlers.Length; i++)
            {
                try
                {
                    ((Action<TriggerFiredEventArgs>)handlers[i])(args);
                }
                catch (Exception ex)
                {
                    log.Warn("OnTriggerFired subscriber threw an exception", ex);
                }
            }
        }

        private static bool EvaluateDirect(TriggerCondition condition, double current, double previous)
        {
            return condition.Operator switch
            {
                ConditionOperator.Equals => current == condition.Threshold,
                ConditionOperator.GreaterThan => current > condition.Threshold,
                ConditionOperator.LessThan => current < condition.Threshold,
                ConditionOperator.CrossesAbove => previous < condition.Threshold && current >= condition.Threshold,
                ConditionOperator.CrossesBelow => previous > condition.Threshold && current <= condition.Threshold,
                _ => false
            };
        }

        private static bool IsConditionSatisfiedWithWindow(TriggerCondition condition, double current, double previous, DateTime timestamp, string conditionKey)
        {
            bool isNowTrue = EvaluateDirect(condition, current, previous);
            TimeSpan requiredWindow = GetTimeSpan(condition.Window);

            if (!isNowTrue)
            {
                ConditionStartTimes.TryRemove(conditionKey, out _);
                return false;
            }

            if (!ConditionStartTimes.TryGetValue(conditionKey, out var start))
            {
                ConditionStartTimes[conditionKey] = timestamp;
                return false;
            }

            return (timestamp - start) > requiredWindow;
        }

        private static TimeSpan GetTimeSpan(TimeWindow window)
        {
            return window.Unit switch
            {
                TimeWindowUnit.Seconds => TimeSpan.FromSeconds(window.Duration),
                TimeWindowUnit.Milliseconds => TimeSpan.FromMilliseconds(window.Duration),
                TimeWindowUnit.Ticks => TimeSpan.FromTicks(window.Duration),
                _ => TimeSpan.Zero
            };
        }

        private static TimeSpan GetCooldownSpan(int duration, TimeWindowUnit unit)
        {
            return unit switch
            {
                TimeWindowUnit.Seconds => TimeSpan.FromSeconds(duration),
                TimeWindowUnit.Minutes => TimeSpan.FromMinutes(duration),
                TimeWindowUnit.Hours => TimeSpan.FromHours(duration),
                TimeWindowUnit.Days => TimeSpan.FromDays(duration),
                _ => TimeSpan.Zero
            };
        }

        private static Task ExecuteActionAsync(string ruleName, TriggerCondition condition, TriggerAction action, string plugin, string metric, string exchange, string symbol, double value, DateTime timestamp)
        {
            if (action.Type == ActionType.RestApi && action.RestApi != null)
            {
                var body = action.RestApi.BodyTemplate
                    .Replace("{{rulename}}", ruleName)
                    // {{plugin}} historically receives the METRIC name (the display name does
                    // not exist at fire time); kept as-is so existing saved templates keep
                    // their current output. {{metric}} is the documented, correctly-named
                    // placeholder for the same value.
                    .Replace("{{plugin}}", metric)
                    .Replace("{{metric}}", metric)
                    .Replace("{{condition}}", condition.Operator.ToString())
                    .Replace("{{threshold}}", condition.Threshold.ToString())
                    .Replace("{{value}}", value.ToString())
                    .Replace("{{timestamp}}", timestamp.ToString("o"));

                _ = action.RestApi.ExecuteAsync(body); // Fire and forget

            }

            if (action.Type == ActionType.UIAlert)
            {
                string formattedMessage = $"{exchange} - {symbol}";
                HelperNotificationManager.Instance.AddNotification(ruleName, formattedMessage, HelprNorificationManagerTypes.TRIGGER_ACTION,
                    HelprNorificationManagerCategories.TRIGGER_ENGINE, null, condition.Plugin);
            }
            return Task.CompletedTask;
        }

        private static void EvaluateAllRulesAgainstLatestMetrics()
        {
            Task.Run(() =>
            {
                var latestMetrics = LastMetricValues.ToArray(); // Snapshot current metrics

                foreach (var kvp in latestMetrics)
                {
                    var parts = kvp.Key.Split('.');
                    if (parts.Length != 4)
                        continue;

                    var plugin = parts[0];
                    var metric = parts[1];
                    var exchange = parts[2];
                    var symbol = parts[3];

                    // Re-present the ORIGINAL observation (value + timestamp), flagged as
                    // a replay. Stamping DateTime.UtcNow here fabricated an observation
                    // that no market data produced, which both re-fired already-fired
                    // actions and advanced sustained-condition windows on wall-clock alone.
                    var metricEvent = new MetricEvent(
                        plugin, metric, exchange, symbol,
                        kvp.Value.Value, kvp.Value.Timestamp, IsReplay: true);
                    _ = MetricChannel.Writer.WriteAsync(metricEvent);
                }
            });
        }


    }

}
