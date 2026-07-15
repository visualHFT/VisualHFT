using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using log4net;

namespace VisualHFT.Helpers
{
    /// <summary>One row in the catalog — what /events returns per event,
    /// plus liquidity aggregates filled in later from /markets.</summary>
    public sealed class KalshiEventInfo : System.ComponentModel.INotifyPropertyChanged
    {
        public string EventTicker { get; init; } = "";
        public string SeriesTicker { get; init; } = "";
        public string Title { get; init; } = "";
        public string SubTitle { get; init; } = "";
        public string Category { get; init; } = "Other";
        public bool MutuallyExclusive { get; init; }
        public string LastUpdated { get; init; } = "";

        // Polymarket-only: the first market's YES clobTokenId. Empty for Kalshi rows.
        // Used by "Watch + Load Chart" to route Polymarket events to providerId 11
        // (the Polymarket plugin) instead of the Kalshi path.
        public string PolymarketYesToken { get; init; } = "";

        // Filled in by FetchAllMarketsAsync after events load — aggregated over the event's markets.
        private double _oi;
        public double OpenInterest
        {
            get => _oi;
            set { _oi = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(OpenInterest))); PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(OpenInterestText))); }
        }
        public string OpenInterestText => Format(OpenInterest);

        private double _volume;
        public double Volume
        {
            get => _volume;
            set { _volume = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Volume))); PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(VolumeText))); }
        }
        public string VolumeText => Format(Volume);

        private static string Format(double v) =>
            v <= 0           ? ""
          : v >= 1_000_000   ? $"{v/1_000_000:F1}M"
          : v >= 1_000       ? $"{v/1_000:F1}K"
          : $"{v:F0}";

        private int _markets;
        public int MarketCount
        {
            get => _markets;
            set { _markets = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MarketCount))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Fetches the full Kalshi event catalog with pagination, then groups by
    /// the API's <c>category</c> field. Read-only, uses prod URL because it has
    /// the richest universe (the polling plugin runs separately on demo).
    /// </summary>
    public sealed class KalshiEventCatalog : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(KalshiEventCatalog));

        private readonly HttpClient _http;
        private readonly RSA _rsa;
        private readonly string _keyId;
        private bool _disposed;

        private KalshiEventCatalog(string baseUrl, string keyId, RSA rsa)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _rsa = rsa;
            _keyId = keyId;
        }

        public static KalshiEventCatalog ForProd()
        {
            var pemPath = KalshiCredentials.ProdPemPath;
            if (!File.Exists(pemPath))
                throw new FileNotFoundException($"Prod PEM not found at {pemPath}");
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(pemPath));
            return new KalshiEventCatalog(KalshiCredentials.ProdBase, KalshiCredentials.ProdKeyId, rsa);
        }

        // Process-wide cache + lock so reopening the browser is instant and we don't
        // re-hammer Kalshi. Cleared by the user's Refresh button.
        private static readonly object _cacheLock = new();
        private static List<KalshiEventInfo>? _cachedEvents;
        private static DateTime _cachedAt;

        public static void InvalidateCache()
        {
            lock (_cacheLock) { _cachedEvents = null; }
        }

        /// <summary>
        /// Fetch every open event, paging until the server returns no cursor.
        /// Throttled (200ms between pages) and resilient to 429 (exponential
        /// backoff up to 5 retries per page). Cached process-wide for ~5 min.
        /// </summary>
        public async Task<List<KalshiEventInfo>> FetchAllOpenAsync(int maxPages = 50)
        {
            // Serve from cache if it's fresh.
            lock (_cacheLock)
            {
                if (_cachedEvents != null && (DateTime.UtcNow - _cachedAt).TotalMinutes < 5)
                {
                    log.Info($"event catalog: serving {_cachedEvents.Count} from cache");
                    return new List<KalshiEventInfo>(_cachedEvents);
                }
            }

            var all = new List<KalshiEventInfo>();
            string cursor = "";
            int pages = 0;
            while (pages < maxPages)
            {
                // Throttle BEFORE every request after the first to stay well under
                // Kalshi's basic-tier limit (~10 req/s). 200ms = 5 req/s ceiling.
                if (pages > 0) await Task.Delay(200).ConfigureAwait(false);

                var (ok, body) = await FetchPageWithBackoffAsync(cursor).ConfigureAwait(false);
                if (!ok) { log.Warn($"page {pages}: giving up after retries"); break; }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("events", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        all.Add(new KalshiEventInfo
                        {
                            EventTicker = e.TryGetProperty("event_ticker",  out var v0) ? v0.GetString() ?? "" : "",
                            SeriesTicker= e.TryGetProperty("series_ticker", out var v1) ? v1.GetString() ?? "" : "",
                            Title       = e.TryGetProperty("title",         out var v2) ? v2.GetString() ?? "" : "",
                            SubTitle    = e.TryGetProperty("sub_title",     out var v3) ? v3.GetString() ?? "" : "",
                            Category    = e.TryGetProperty("category",      out var v4) ? v4.GetString() ?? "Other" : "Other",
                            MutuallyExclusive = e.TryGetProperty("mutually_exclusive", out var v5) && v5.ValueKind == JsonValueKind.True,
                            LastUpdated = e.TryGetProperty("last_updated_ts", out var v6) ? v6.GetString() ?? "" : "",
                        });
                    }
                }
                cursor = doc.RootElement.TryGetProperty("cursor", out var cu) ? cu.GetString() ?? "" : "";
                pages++;
                if (string.IsNullOrEmpty(cursor)) break;
            }
            log.Info($"event catalog: {all.Count} events across {pages} page(s)");

            // Cache only on a complete-ish fetch (>=5 pages or empty cursor).
            if (all.Count > 200)
            {
                lock (_cacheLock) { _cachedEvents = new List<KalshiEventInfo>(all); _cachedAt = DateTime.UtcNow; }
            }
            return all;
        }

        private async Task<(bool ok, string body)> FetchPageWithBackoffAsync(string cursor)
        {
            int retryDelayMs = 1000;
            const int maxRetries = 5;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var qs = "/trade-api/v2/events?status=open&limit=200" +
                         (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                using var req = BuildRequest(HttpMethod.Get, "/trade-api/v2/events");
                using var get = new HttpRequestMessage(HttpMethod.Get, qs);
                CopyAuth(req, get);

                using var resp = await _http.SendAsync(get).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return (true, body);
                if ((int)resp.StatusCode == 429)
                {
                    log.Warn($"429 on attempt {attempt + 1} — backing off {retryDelayMs}ms");
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                    retryDelayMs = Math.Min(retryDelayMs * 2, 30_000);
                    continue;
                }
                log.Warn($"page failed: {(int)resp.StatusCode} {body[..Math.Min(160, body.Length)]}");
                return (false, body);
            }
            return (false, "");
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string path)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var msg = Encoding.UTF8.GetBytes(ts + method.Method + path);
            var sig = Convert.ToBase64String(_rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            var req = new HttpRequestMessage(method, path);
            req.Headers.Add("KALSHI-ACCESS-KEY", _keyId);
            req.Headers.Add("KALSHI-ACCESS-SIGNATURE", sig);
            req.Headers.Add("KALSHI-ACCESS-TIMESTAMP", ts);
            req.Headers.Add("Accept", "application/json");
            return req;
        }

        private static void CopyAuth(HttpRequestMessage from, HttpRequestMessage to)
        {
            foreach (var h in from.Headers) to.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        /// <summary>
        /// Fetch the list of market tickers (strikes) inside one event.
        /// </summary>
        public async Task<List<string>> GetEventMarketsAsync(string eventTicker)
        {
            var basePath = $"/trade-api/v2/events/{eventTicker}";
            var qs = "?with_nested_markets=true";
            using var req = BuildRequest(HttpMethod.Get, basePath);
            using var get = new HttpRequestMessage(HttpMethod.Get, basePath + qs);
            CopyAuth(req, get);

            using var resp = await _http.SendAsync(get).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                log.Warn($"GetEventMarkets {eventTicker}: {(int)resp.StatusCode}");
                return new List<string>();
            }
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("event", out var ev))
                return new List<string>();
            if (!ev.TryGetProperty("markets", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<string>();
            var tickers = new List<string>();
            foreach (var m in arr.EnumerateArray())
            {
                if (m.TryGetProperty("ticker", out var t))
                {
                    var s = t.GetString();
                    if (!string.IsNullOrEmpty(s)) tickers.Add(s);
                }
            }
            return tickers;
        }

        /// <summary>
        /// Fetch every active market and aggregate open_interest_fp per event.
        /// Used by the events browser to sort categories by liquidity.
        /// Throttled + retry-on-429 like FetchAllOpenAsync.
        /// </summary>
        public async Task<Dictionary<string, (double oi, double vol, int markets)>> FetchEventLiquidityAsync(int maxPages = 200)
        {
            var byEvent = new Dictionary<string, (double oi, double vol, int markets)>(StringComparer.Ordinal);
            string cursor = "";
            int pages = 0;
            while (pages < maxPages)
            {
                if (pages > 0) await Task.Delay(200).ConfigureAwait(false);

                // status=open is the valid value (Kalshi 400s on 'active'). 'open'
                // covers active markets — closed/settled markets contribute no live OI.
                var qs = "/trade-api/v2/markets?status=open&limit=200" +
                         (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                using var req = BuildRequest(HttpMethod.Get, "/trade-api/v2/markets");
                using var get = new HttpRequestMessage(HttpMethod.Get, qs);
                CopyAuth(req, get);

                using var resp = await _http.SendAsync(get).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if ((int)resp.StatusCode == 429)
                {
                    log.Warn($"markets page {pages}: 429 — backing off 2s");
                    await Task.Delay(2000).ConfigureAwait(false);
                    continue; // retry same cursor
                }
                if (!resp.IsSuccessStatusCode)
                {
                    log.Warn($"markets page {pages}: {(int)resp.StatusCode}");
                    break;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("markets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        var ev = m.TryGetProperty("event_ticker", out var et) ? et.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(ev)) continue;
                        double oi = 0, vol = 0;
                        if (m.TryGetProperty("open_interest_fp", out var oiEl))
                        {
                            var s = oiEl.GetString();
                            if (!string.IsNullOrEmpty(s) && double.TryParse(s, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var p)) oi = p;
                        }
                        if (m.TryGetProperty("volume_fp", out var volEl))
                        {
                            var s = volEl.GetString();
                            if (!string.IsNullOrEmpty(s) && double.TryParse(s, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var p)) vol = p;
                        }
                        (double oi, double vol, int markets) cur = byEvent.TryGetValue(ev, out var v) ? v : (0.0, 0.0, 0);
                        byEvent[ev] = (cur.oi + oi, cur.vol + vol, cur.markets + 1);
                    }
                }
                cursor = doc.RootElement.TryGetProperty("cursor", out var cu) ? cu.GetString() ?? "" : "";
                pages++;
                if (string.IsNullOrEmpty(cursor)) break;
            }
            log.Info($"event liquidity: {byEvent.Count} events covered across {pages} markets-page(s)");
            return byEvent;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _http.Dispose();
            _rsa.Dispose();
            _disposed = true;
        }
    }
}
