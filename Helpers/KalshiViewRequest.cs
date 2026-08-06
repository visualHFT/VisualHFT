using System;

namespace VisualHFT.Helpers
{
    /// <summary>
    /// Cross-window event hub for "show this Kalshi ticker in the main view".
    /// Subscribed to by vmOrderBook; fired by the Watch List / Strike Ladder /
    /// Events Browser when the user wants to inspect a specific market.
    /// </summary>
    public static class KalshiViewRequest
    {
        /// <summary>(symbol, providerId) — providerId 100 = Kalshi.</summary>
        public static event Action<string, int>? OnRequest;

        public static void Show(string symbol, int providerId = 100)
        {
            if (string.IsNullOrEmpty(symbol)) return;
            OnRequest?.Invoke(symbol, providerId);
        }
    }
}
