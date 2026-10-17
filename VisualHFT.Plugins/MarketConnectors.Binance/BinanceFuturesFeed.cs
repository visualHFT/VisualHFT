using System;
using System.Collections.Generic;

namespace MarketConnectors.Binance
{
    /// <summary>
    /// USD-M perpetual book rules. Binance.com futures (fapi) are a different
    /// stream from spot: the first diff must bridge the snapshot, and every
    /// later diff must chain through the previous update id ("pu").
    /// </summary>
    public static class BinanceFuturesFeed
    {
        public enum BookStep
        {
            Drop,
            Apply,
            Gap
        }

        public static BookStep Decide(long bookSequence, long firstUpdateId, long lastUpdateId, long previousFinalUpdateId, bool synced)
        {
            if (lastUpdateId < bookSequence)
                return BookStep.Drop;

            if (!synced)
            {
                if (firstUpdateId <= bookSequence && lastUpdateId >= bookSequence)
                    return BookStep.Apply;
                return BookStep.Gap;
            }

            if (previousFinalUpdateId != bookSequence)
                return BookStep.Gap;
            return BookStep.Apply;
        }

        public static int DepthLimit(int requested)
        {
            int[] allowed = { 5, 10, 20, 50, 100, 500, 1000 };
            if (requested <= allowed[0])
                return allowed[0];
            var pick = allowed[0];
            foreach (var level in allowed)
            {
                if (level <= requested)
                    pick = level;
                else
                    break;
            }
            return pick;
        }

        public static int UpdateInterval(int requested)
        {
            if (requested <= 100)
                return 100;
            if (requested <= 500)
                return 500;
            return 1000;
        }

        public static Dictionary<string, string> Parse(IEnumerable<string> entries)
        {
            var map = new Dictionary<string, string>();
            if (entries == null)
                return map;
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                var parts = entry.Split(new[] { '(' }, StringSplitOptions.RemoveEmptyEntries);
                var symbol = parts[0].Trim();
                if (symbol.Length == 0)
                    continue;
                var normalized = parts.Length > 1 ? parts[1].Trim().TrimEnd(')') : symbol;
                map[symbol] = string.IsNullOrEmpty(normalized) ? symbol : normalized;
            }
            return map;
        }
    }
}
