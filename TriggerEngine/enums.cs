﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisualHFT.TriggerEngine
{
    public enum ConditionOperator
    {
        Equals,
        GreaterThan,
        LessThan,
        CrossesAbove,
        CrossesBelow
    }
    public enum WindowUnit
    {
        Seconds,
        Milliseconds,
        Ticks
    }
    public enum ActionType
    {
        UIAlert,
        RestApi
        // Future: UI, LogFile, PluginCallback, Webhook, StrategyControl, etc.
    }
    public enum AlertSeverity
    {
        Info,
        Warning,
        Error
    }
}
