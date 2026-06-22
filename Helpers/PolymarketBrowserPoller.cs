using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace VisualHFT.Helpers
{
    /// <summary>
    /// Catalog fetcher for the Polymarket events browser. Mirrors
    /// <see cref="KalshiEventCatalog"/> so the existing UI / grouping logic in
    /// <c>vmKalshiEventBrowser</c> can render Polymarket events without caring
    /// which venue produced them.
    ///
    /// Hits Polymarket's public Gamma API (no auth required):
    ///   https://gamma-api.polymarket.com/events?active=true&closed=false&...
    /// </summary>
    public static class PolymarketBrowserPoller
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PolymarketBrowserPoller));

        private const string GammaUrl =
            "https://gamma-api.polymarket.com/events?active=true&closed=false&limit=500&order=volume24hr&ascending=false";

        // One static HttpClient is the recommended pattern for long-lived hosts;
        // it also keeps the connection pool warm between Refresh clicks.
        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Process-wide cache so reopening the browser is instant and we don't
        // re-hammer Gamma. Cleared by the user's Refresh button (see InvalidateCache).
        private static readonly object _cacheLock = new();
        private static List<KalshiEventInfo>? _cachedEvents;
        private static DateTime _cachedAt;

        public static void InvalidateCache()
        {
            lock (_cacheLock) { _cachedEvents = null; }
        }

        /// <summary>
        /// Fetch every active, non-closed Polymarket event (single page; the
        /// Gamma API caps at 500 which covers the entire live universe today).
        /// </summary>
        public static async Task<List<KalshiEventInfo>> FetchAllOpenAsync(CancellationToken ct = default)
        {
            // Serve from cache if it's fresh.
            lock (_cacheLock)
            {
                if (_cachedEvents != null && (DateTime.UtcNow - _cachedAt).TotalMinutes < 5)
                {
                    log.Info($"polymarket catalog: serving {_cachedEvents.Count} from cache");
                    return new List<KalshiEventInfo>(_cachedEvents);
                }
            }

            string body;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, GammaUrl);
                req.Headers.Add("Accept", "application/json");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    log.Warn($"polymarket catalog: HTTP {(int)resp.StatusCode}");
                    return new List<KalshiEventInfo>();
                }
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Warn($"polymarket catalog fetch failed: {ex.Message}");
                throw;
            }

            var all = new List<KalshiEventInfo>();
            using var doc = JsonDocument.Parse(body);

            // Gamma returns a bare array of event objects.
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                log.Warn("polymarket catalog: unexpected response shape (expected array)");
                return all;
            }

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var slug = StringOrEmpty(e, "slug");
                if (string.IsNullOrEmpty(slug)) continue;

                var title = StringOrEmpty(e, "title");

                // Sub-title: first market's question, if any.
                string subTitle = "";
                string yesToken = "";
                int marketCount = 0;
                if (e.TryGetProperty("markets", out var markets) && markets.ValueKind == JsonValueKind.Array)
                {
                    marketCount = markets.GetArrayLength();
                    bool firstMarket = true;
                    foreach (var m in markets.EnumerateArray())
                    {
                        if (firstMarket)
                        {
                            subTitle = StringOrEmpty(m, "question");
                            yesToken = FirstClobTokenId(m);
                            firstMarket = false;
                            if (!string.IsNullOrEmpty(yesToken)) break;
                        }
                    }
                }

                // Category: first tag's label (or "Other" if no tags). Also used
                // for the SeriesTicker column so the existing UI has something
                // sensible to render in the per-row "Series" cell.
                string category = "Other";
                if (e.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0)
                {
                    var firstTag = tags[0];
                    var label = StringOrEmpty(firstTag, "label");
                    if (!string.IsNullOrEmpty(label)) category = label;
                }

                double liquidity = NumberOrZero(e, "liquidity");
                double vol24     = NumberOrZero(e, "volume24hr");

                all.Add(new KalshiEventInfo
                {
                    EventTicker        = slug,
                    SeriesTicker       = category == "Other" ? "" : category,
                    Title              = title,
                    SubTitle           = subTitle,
                    Category           = category,
                    MutuallyExclusive  = false,
                    LastUpdated        = "",
                    PolymarketYesToken = yesToken,
                    OpenInterest       = liquidity,
                    Volume             = vol24,
                    MarketCount        = marketCount,
                });
            }

            log.Info($"polymarket catalog: {all.Count} events");

            // Cache anything non-trivial.
            if (all.Count > 0)
            {
                lock (_cacheLock) { _cachedEvents = new List<KalshiEventInfo>(all); _cachedAt = DateTime.UtcNow; }
            }
            return all;
        }

        // --- helpers ------------------------------------------------------------

        private static string StringOrEmpty(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => ""
            };
        }

        /// <summary>
        /// Polymarket sometimes returns numeric fields as strings (e.g. "1234.5").
        /// Handle both shapes defensively.
        /// </summary>
        private static double NumberOrZero(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var v)) return 0;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    return v.TryGetDouble(out var d) ? d : 0;
                case JsonValueKind.String:
                    var s = v.GetString();
                    return double.TryParse(s, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// clobTokenIds can be either a JSON array (["yes","no"]) or a stringified
        /// JSON array ("[\"yes\",\"no\"]"). Return the first element or "".
        /// </summary>
        private static string FirstClobTokenId(JsonElement market)
        {
            if (!market.TryGetProperty("clobTokenIds", out var v)) return "";
            if (v.ValueKind == JsonValueKind.Array)
            {
                if (v.GetArrayLength() == 0) return "";
                var first = v[0];
                return first.ValueKind == JsonValueKind.String ? first.GetString() ?? "" : first.GetRawText();
            }
            if (v.ValueKind == JsonValueKind.String)
            {
                var raw = v.GetString();
                if (string.IsNullOrEmpty(raw)) return "";
                try
                {
                    using var inner = JsonDocument.Parse(raw);
                    if (inner.RootElement.ValueKind == JsonValueKind.Array && inner.RootElement.GetArrayLength() > 0)
                    {
                        var first = inner.RootElement[0];
                        return first.ValueKind == JsonValueKind.String ? first.GetString() ?? "" : first.GetRawText();
                    }
                }
                catch { /* fall through */ }
            }
            return "";
        }
    }
}
