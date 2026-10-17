using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisualHFT.TriggerEngine.Actions
{
    public class UIAction
    {
        public string Message { get; set; }                // Message to display
        public AlertSeverity Severity { get; set; }        // Info, Warning, Error
    }
}
