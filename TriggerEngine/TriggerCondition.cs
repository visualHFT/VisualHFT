using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisualHFT.TriggerEngine
{
    /// <summary>
    /// Describes the condition that must be met for a rule to be triggered.
    /// This includes the source plugin, the metric name, the comparison operator, the threshold value, and optionally a time window for smoothing or delay.
    /// </summary>

    public class TriggerCondition
    {
        public long ConditionID { get; set; }
        public string Plugin { get; set; }                 // e.g. "MarketMicrostructure"
        public string Metric { get; set; }                 // e.g. "LOBImbalance"
        public ConditionOperator Operator { get; set; }    // e.g. CrossesAbove, GreaterThan
        public double Threshold { get; set; }              // e.g. 0.7
        public TimeWindow? Window { get; set; }             // Optional smoothing/aggregation logic
    }
}
