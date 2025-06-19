using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VisualHFT.TriggerEngine.Actions;

namespace VisualHFT.TriggerEngine
{
    /// <summary>
    /// Defines a single action to execute when a rule's condition is met.
    /// The type determines the implementation used (e.g., REST API call).
    /// </summary>
    public class TriggerAction
    {
        public long ActionID { get; set; }
        public ActionType Type { get; set; } = ActionType.RestApi;
        public RestApiAction? RestApi { get; set; }         // Only required if Type == RestApi
                                                            // Future: Add more actions (e.g., UIAlertAction, PluginCommandAction)


        //Cooldown Logic
        //Each action has its own cooldown(CooldownDuration and CooldownUnit).//
        //The cooldown ensures that an action is not triggered too frequently, based on the duration and unit specified in the action(e.g., once per hour, once per day, etc.).

        public int CooldownDuration { get; set; } = 0;
        public TimeWindowUnit CooldownUnit { get; set; } = TimeWindowUnit.Seconds;
    }

}