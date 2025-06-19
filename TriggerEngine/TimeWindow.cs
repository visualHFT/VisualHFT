using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisualHFT.TriggerEngine
{
    /// <summary>
    /// Optional time-based window for smoothing or sustained condition detection.
    /// For example, "slippage > 0.5 for more than 3 seconds".
    /// </summary>
    public class TimeWindow
    {
        public int Duration { get; set; }                  // e.g. 3
        public TimeWindowUnit Unit { get; set; }               // e.g. Seconds, Ticks
    }
}
