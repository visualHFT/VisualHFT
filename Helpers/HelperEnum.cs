using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VisualHFT.TriggerEngine;

namespace VisualHFT.Helpers
{
    public static class EnumHelper
    {
        public static string GetDisplayName(Enum value)
        {
            // Provide mapping here instead of using DescriptionAttribute
            return value switch
            {
                ActionType.RestApi => "WebHook URL",
                ActionType.UIAlert=> "Notify In-App",
                _ => value.ToString()
            };
        }

        public static IEnumerable<KeyValuePair<Enum, string>> GetAllDisplayValues<T>() where T : Enum
        {
            return Enum.GetValues(typeof(T)).Cast<Enum>()
                .Select(e => new KeyValuePair<Enum, string>(e, GetDisplayName(e)));
        }
    }
}
