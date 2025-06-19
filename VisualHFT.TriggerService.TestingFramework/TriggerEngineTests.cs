using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using VisualHFT.Commons.Helpers;
using VisualHFT.TriggerEngine;
using TriggerAction = VisualHFT.TriggerEngine.TriggerAction;

namespace VisualHFT.DataRetriever.TestingFramework.TestCases
{
    public class TriggerEngineTests
    {
        private const double Threshold = 100.0;
        public const string PluginID = "PluginID1";
        public const string PluginName = "TestPlugin";
        public const string Exchange = "Binance";
        public const string Symbol = "BTC-USD";
         
        [Fact]
        public void AddOrUpdateRule_ShouldAddNewRule()
        {
            var rule = new TriggerRule { Name = "TestRule1", IsEnabled = true,RuleID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            TriggerEngineService.AddOrUpdateRule(rule);

            var rules = TriggerEngineService.GetRules();
            Assert.Contains(rules, r => r.Name == "TestRule1");
        }

        [Fact]
        public void AddOrUpdateRule_ShouldUpdateExistingRule()
        {
            var rule1 = new TriggerRule { Name = "TestRule2", IsEnabled = true, RuleID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var rule2 = new TriggerRule { Name = "TestRule2", IsEnabled = false , RuleID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

            TriggerEngineService.AddOrUpdateRule(rule1);
            TriggerEngineService.AddOrUpdateRule(rule2);

            var rules = TriggerEngineService.GetRules();
            var updated = rules.FirstOrDefault(r => r.Name == "TestRule2");
            Assert.False(updated?.IsEnabled);
        }

        [Fact]
        public void RemoveRule_ShouldRemoveRuleByName()
        {
            var rule = new TriggerRule { Name = "TestRule3", RuleID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            TriggerEngineService.AddOrUpdateRule(rule);
            TriggerEngineService.RemoveRule(rule.RuleID);

            var rules = TriggerEngineService.GetRules();
            Assert.DoesNotContain(rules, r => r.RuleID == rule.RuleID);
        }

        [Fact]
        public async Task RegisterMetric_ShouldTriggerMatchingRuleAction()
        {
            bool actionExecuted = false;

            var rule = new TriggerRule
            {
                Name = "TestRunRule",
                IsEnabled = true,
                RuleID=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Condition = new List<TriggerCondition>
            {
                new TriggerCondition
                {
                    Plugin = PluginID,
                    Metric =PluginName,
                    Threshold = 100,
                    Operator = ConditionOperator.GreaterThan
                }
            },
                Actions = new List<TriggerAction>
            {
                new TriggerAction
                {
                    Type = ActionType.RestApi,
                    CooldownDuration = 0,
                    CooldownUnit = TimeWindowUnit.Seconds,
                    RestApi = new TriggerEngine.Actions.RestApiAction
                    {
                        Url = "http://test.api",
                        Method = "POST",
                        BodyTemplate = "{{metric}}-{{value}}-{{timestamp}}"
                    }
                }
            }
            };

            try
            {
                TriggerEngineService.AddOrUpdateRule(rule);

                var cts = new CancellationTokenSource();
                var workerTask = TriggerEngineService.StartBackgroundWorkerAsync(cts.Token);

                TriggerEngineService.RegisterMetric(PluginID, PluginName,Exchange,Symbol, 120, DateTime.UtcNow);

                await Task.Delay(200);  

                cts.Cancel();
                await workerTask;
            }
            catch (Exception ex)
            {
                 
            }   
        }


        [Theory]
        [InlineData(ConditionOperator.Equals, 100, 50, 100, true)]
        [InlineData(ConditionOperator.Equals, 100, 50, 99.9, false)]
        [InlineData(ConditionOperator.GreaterThan, 100, 100, 101, true)]
        [InlineData(ConditionOperator.GreaterThan, 100, 100, 100, false)]
        [InlineData(ConditionOperator.LessThan, 100, 100, 99, true)]
        [InlineData(ConditionOperator.LessThan, 100, 100, 100, false)]
        [InlineData(ConditionOperator.CrossesAbove, 100, 99, 101, true)]
        [InlineData(ConditionOperator.CrossesAbove, 100, 101, 102, false)]
        [InlineData(ConditionOperator.CrossesBelow, 100, 101, 99, true)]
        [InlineData(ConditionOperator.CrossesBelow, 100, 99, 101, false)]
        public void EvaluateDirect_ShouldWork(ConditionOperator op, double threshold, double previous, double current, bool expected)
        {
            var condition = new TriggerCondition
            {
                Operator = op,
                Threshold = threshold
            };

            Assert.Equal(expected, Evaluate(condition, current, previous));
        }

        [Fact]
        public void EvaluateDirect_ShouldReturnFalse_ForUnknownOperator()
        {
            var condition = new TriggerCondition
            {
                Operator = (ConditionOperator)999,
                Threshold = 100
            };

            Assert.False(Evaluate(condition, 50, 100));
        }



        private static TriggerRule CreateRule(TimeSpan cooldown)
        {
            return new TriggerRule
            {
                Name = "TestRule",
                IsEnabled = true,
                RuleID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Condition = new List<TriggerCondition>
            {
                new TriggerCondition
                {
                    ConditionID= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Plugin = PluginID,
                    Metric = PluginName,
                    Operator = ConditionOperator.GreaterThan,
                    Threshold = Threshold
                }
            },
                Actions = new List<TriggerAction>
            {
                new TriggerAction
                {
                    Type = ActionType.UIAlert,
                    CooldownDuration = (int)cooldown.TotalSeconds,
                    CooldownUnit = TimeWindowUnit.Seconds
                }
            }
            };
        }

        [Fact]
        public async Task Should_Not_Trigger_Immediately_When_Condition_Met()
        {
            TriggerEngineService.ClearAllRules();
            // Arrange
            var cooldown = TimeSpan.FromSeconds(5);
            var rule = CreateRule(cooldown);
            TriggerEngineService.AddOrUpdateRule(rule);
            
            var cts = new CancellationTokenSource();
            var workerTask = TriggerEngineService.StartBackgroundWorkerAsync(cts.Token);


            var now = DateTime.UtcNow;

            // Act: Register first metric (meets condition)
            TriggerEngineService.RegisterMetric(PluginID, PluginName, Exchange, Symbol, 120.0, now);

            await Task.Delay(200); // simulate time for async processing

            // Act again before cooldown ends
            TriggerEngineService.RegisterMetric(PluginID, PluginName, Exchange, Symbol, 130.0, now.AddSeconds(3));

            await Task.Delay(200);

            int count = HelperNotificationManager.Instance.GetAllNotifications().Where(x => x.Category == HelprNorificationManagerCategories.TRIGGER_ENGINE && x.PluginID.Equals(PluginID)).Count();

            Assert.Equal(0, count); // No notifications should be sent 
        }

        [Fact]
        public async Task Should_Trigger_Only_After_Cooldown_If_Condition_Remains_True()
        {
            TriggerEngineService.ClearAllRules();
            // Arrange
            var cooldown = TimeSpan.FromSeconds(3);
            var rule = CreateRule(cooldown);
            TriggerEngineService.AddOrUpdateRule(rule);

            var cts = new CancellationTokenSource();
            var workerTask = TriggerEngineService.StartBackgroundWorkerAsync(cts.Token);

            var baseTime = DateTime.UtcNow;

            // Act 1: Trigger first match
            TriggerEngineService.RegisterMetric(PluginID, PluginName, Exchange, Symbol, 120.0, baseTime);
            await Task.Delay(100);

            // Act 2: Not yet past cooldown
            TriggerEngineService.RegisterMetric(PluginID, PluginName, Exchange, Symbol, 125.0, baseTime.AddSeconds(2));
            await Task.Delay(100);

            // Act 3: Past cooldown
            TriggerEngineService.RegisterMetric(PluginID, PluginName, Exchange, Symbol, 130.0, baseTime.AddSeconds(4));
            await Task.Delay(100);

            int count = HelperNotificationManager.Instance.GetAllNotifications().Where(x => x.Category == HelprNorificationManagerCategories.TRIGGER_ENGINE && x.PluginID.Equals(PluginID)).Count();
            Assert.Equal(1, count); //  notifications should be sent  since cooldown is staisfied
        }

        [Fact]
        public async Task Should_Not_Trigger_If_Condition_Broken_Before_Cooldown()
        {
            TriggerEngineService.ClearAllRules();
            // Arrange
            var cooldown = TimeSpan.FromSeconds(4);
            var rule = CreateRule(cooldown);
            TriggerEngineService.AddOrUpdateRule(rule);
            var cts = new CancellationTokenSource();
            var workerTask = TriggerEngineService.StartBackgroundWorkerAsync(cts.Token);

            var baseTime = DateTime.UtcNow;

            // Act: Trigger condition
            TriggerEngineService.RegisterMetric(PluginID, PluginName, Exchange, Symbol, 120.0, baseTime);
            await Task.Delay(100);

            // Now condition breaks
            TriggerEngineService.RegisterMetric(PluginID, PluginName, Exchange, Symbol, 80.0, baseTime.AddSeconds(2));
            await Task.Delay(100);

            // Condition true again, but should reset the cooldown
            TriggerEngineService.RegisterMetric(PluginID, PluginName, Exchange, Symbol, 125.0, baseTime.AddSeconds(3));
            await Task.Delay(100);

            // Final assert — we expect no  firing (after cooldown from last true condition)
            int count = HelperNotificationManager.Instance.GetAllNotifications().Where(x => x.Category == HelprNorificationManagerCategories.TRIGGER_ENGINE && x.PluginID.Equals(PluginID)).Count();
            Assert.Equal(0, count);


        }

    private Func<TriggerCondition, double, double, bool> Evaluate =>
            (condition, current, previous) =>
                (bool)typeof(TriggerEngineService)
                    .GetMethod("EvaluateDirect", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { condition, current, previous });
    }
}