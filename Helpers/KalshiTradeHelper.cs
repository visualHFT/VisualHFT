using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using log4net;

namespace VisualHFT.Helpers
{
    /// <summary>
    /// Minimal self-contained Kalshi trading client for the in-app order panel.
    /// Hard-coded to demo only. Read-only viewing stays on the plugin's prod path.
    ///
    /// Mirrors the plugin's KalshiSigner (RSA-PSS-SHA256). Replicated here to
    /// avoid VisualHFT.csproj depending on the plugin assembly at compile time.
    /// </summary>
    public sealed class KalshiTradeHelper : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(KalshiTradeHelper));

        private const string DemoBase = "https://demo-api.kalshi.co";

        public const int MAX_COUNT = 5;

        private readonly HttpClient _http;
        private readonly RSA _rsa;
        private readonly string _keyId;
        private bool _disposed;

        private KalshiTradeHelper(string baseUrl, string keyId, RSA rsa)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _rsa = rsa;
            _keyId = keyId;
        }

        public static KalshiTradeHelper ForDemo()
        {
            var (keyId, rsa) = KalshiCredentials.LoadDemo();
            return new KalshiTradeHelper(DemoBase, keyId, rsa);
        }

        public sealed class OrderResult
        {
            public bool Success { get; init; }
            public string OrderId { get; init; } = "";
            public string Status { get; init; } = "";
            public string Error { get; init; } = "";
        }

        public async Task<OrderResult> PlaceLimitYesBuyAsync(string ticker, int yesCents, int count)
            => await PlaceLimitAsync(ticker, side: "yes", action: "buy", priceCents: yesCents, count: count);
        public async Task<OrderResult> PlaceLimitYesSellAsync(string ticker, int yesCents, int count)
            => await PlaceLimitAsync(ticker, side: "yes", action: "sell", priceCents: yesCents, count: count);
        public async Task<OrderResult> PlaceLimitNoBuyAsync(string ticker, int noCents, int count)
            => await PlaceLimitAsync(ticker, side: "no", action: "buy", priceCents: noCents, count: count);
        public async Task<OrderResult> PlaceLimitNoSellAsync(string ticker, int noCents, int count)
            => await PlaceLimitAsync(ticker, side: "no", action: "sell", priceCents: noCents, count: count);

        public async Task<OrderResult> PlaceLimitAsync(string ticker, string side, string action, int priceCents, int count)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(ticker) || !ticker.StartsWith("KX", StringComparison.OrdinalIgnoreCase))
                return new() { Error = "ticker must start with 'KX'" };
            if (count < 1 || count > MAX_COUNT)
                return new() { Error = $"count must be 1..{MAX_COUNT}" };
            if (priceCents < 1 || priceCents > 99)
                return new() { Error = "price must be 1..99 cents" };

            var path = "/trade-api/v2/portfolio/orders";
            var clientOrderId = Guid.NewGuid().ToString();
            var payload = new JsonObject
            {
                ["ticker"] = ticker,
                ["client_order_id"] = clientOrderId,
                ["type"] = "limit",
                ["action"] = action,
                ["side"] = side,
                ["count"] = count
            };
            if (side == "yes") payload["yes_price"] = priceCents;
            else                payload["no_price"] = priceCents;

            using var req = BuildRequest(HttpMethod.Post, path);
            req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            log.Info($"PLACE  {ticker}  {side} {action} {count}@{priceCents}c  cid={clientOrderId}");
            try
            {
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var trim = body.Length > 240 ? body.Substring(0, 240) : body;
                    log.Warn($"order rejected: {(int)resp.StatusCode} {trim}");
                    return new() { Error = $"{(int)resp.StatusCode}: {trim}" };
                }
                using var doc = JsonDocument.Parse(body);
                var orderEl = doc.RootElement.GetProperty("order");
                string id = orderEl.TryGetProperty("order_id", out var i) ? i.GetString() ?? "" : "";
                string st = orderEl.TryGetProperty("status",   out var s) ? s.GetString() ?? "" : "";
                log.Info($"order placed: id={id} status={st}");
                return new() { Success = true, OrderId = id, Status = st };
            }
            catch (Exception ex)
            {
                log.Error("placement failed", ex);
                return new() { Error = ex.Message };
            }
        }

        public async Task<bool> CancelAsync(string orderId)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(orderId)) return false;
            var path = $"/trade-api/v2/portfolio/orders/{orderId}";
            using var req = BuildRequest(HttpMethod.Delete, path);
            log.Info($"CANCEL {orderId}");
            try
            {
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { log.Error("cancel failed", ex); return false; }
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string path)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var msg = Encoding.UTF8.GetBytes(ts + method.Method + path);
            var sig = Convert.ToBase64String(
                _rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            var req = new HttpRequestMessage(method, path);
            req.Headers.Add("KALSHI-ACCESS-KEY", _keyId);
            req.Headers.Add("KALSHI-ACCESS-SIGNATURE", sig);
            req.Headers.Add("KALSHI-ACCESS-TIMESTAMP", ts);
            req.Headers.Add("Accept", "application/json");
            return req;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(KalshiTradeHelper));
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
