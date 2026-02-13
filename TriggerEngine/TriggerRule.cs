using System.Collections.Generic;

namespace VisualHFT.TriggerEngine
{
    /// <summary>
    /// Represents a full trigger rule that defines a condition ("WHEN") and a list of actions ("THEN") to be executed when the condition is met.
    /// Each rule is uniquely identified and can be enabled or disabled individually.
    /// </summary>
    public class TriggerRule
    {
        public string Name { get; set; }                   // Friendly name for UI
        public List<TriggerCondition> Condition { get; set; } = new List<TriggerCondition>();  // "WHEN"
        public List<TriggerAction> Actions { get; set; }        // "THEN"
        public bool IsEnabled { get; set; } = true;
        public long RuleID { get; set; }
    } 
}
