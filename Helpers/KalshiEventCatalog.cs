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
    /// <summary>One row in the catalog — what /events returns per event.</summary>
    public sealed class KalshiEventInfo
    {
        public string EventTicker { get; init; } = "";
        public string SeriesTicker { get; init; } = "";
        public string Title { get; init; } = "";
        public string SubTitle { get; init; } = "";
        public string Category { get; init; } = "Other";
        public bool MutuallyExclusive { get; init; }
        public string LastUpdated { get; init; } = "";
    }

    /// <summary>
    /// Fetches the full Kalshi event catalog with pagination, then groups by
    /// the API's <c>category</c> field. Read-only, uses prod URL because it has
    /// the richest universe (the polling plugin runs separately on demo).
    /// </summary>
    public sealed class KalshiEventCatalog : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(KalshiEventCatalog));

        private const string ProdBase = "https://api.elections.kalshi.com";
        private const string ProdKeyId = "68975a46-1202-4c8a-b71b-3e5fc0817a17";
        private const string ProdPemPath =
            @"C:\Users\paulo\Desktop\Repositories\kalshi-data\secrets\kalshi_private_key.pem";

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
            if (!File.Exists(ProdPemPath))
                throw new FileNotFoundException($"Prod PEM not found at {ProdPemPath}");
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(ProdPemPath));
            return new KalshiEventCatalog(ProdBase, ProdKeyId, rsa);
        }

        /// <summary>
        /// Fetch every open event, paging until the server returns no cursor.
        /// Capped at <paramref name="maxPages"/> as a safety belt.
        /// </summary>
        public async Task<List<KalshiEventInfo>> FetchAllOpenAsync(int maxPages = 50)
        {
            var all = new List<KalshiEventInfo>();
            string cursor = "";
            int pages = 0;
            while (pages < maxPages)
            {
                var qs = $"/trade-api/v2/events?status=open&limit=200" +
                         (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                using var req = BuildRequest(HttpMethod.Get, "/trade-api/v2/events");
                using var get = new HttpRequestMessage(HttpMethod.Get, qs);
                CopyAuth(req, get);

                using var resp = await _http.SendAsync(get).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    log.Warn($"events page {pages}: {(int)resp.StatusCode} {body[..Math.Min(160, body.Length)]}");
                    break;
                }

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
            return all;
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

        public void Dispose()
        {
            if (_disposed) return;
            _http.Dispose();
            _rsa.Dispose();
            _disposed = true;
        }
    }
}
