using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using VisualHFT.Helpers;
using VisualHFT.Model;

namespace VisualHFT.Helpers
{
    /// <summary>
    /// Singleton poller that watches a *dynamic* set of Kalshi tickers and pushes
    /// their order books into the same bus the plugin uses
    /// (<c>HelperOrderBook.Instance.UpdateData</c>). Used by the Events Browser
    /// when you double-click an event to "watch" it without editing the plugin's
    /// static ticker list.
    ///
    /// Hits prod (richer book). Same Kalshi provider ID/name as the plugin so
    /// new tickers appear under the existing 'Kalshi' provider in VisualHFT's
    /// Provider/Symbol dropdown automatically.
    /// </summary>
    public sealed class KalshiBrowserPoller : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(KalshiBrowserPoller));

        private const string ProdBase = "https://api.elections.kalshi.com";
        // Filled from KALSHI_PROD_KEY_ID at construction; empty if not configured.
        private readonly string _keyId;

        // Match the plugin so the Provider/Symbol dropdown groups everything together
        public const int KalshiProviderId = 100;
        public const string KalshiProviderName = "Kalshi";

        private static readonly Lazy<KalshiBrowserPoller> _instance =
            new(() => new KalshiBrowserPoller());
        public static KalshiBrowserPoller Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, OrderBook> _books = new();
        // Tracks the most-recently-seen trade_id per ticker so we only push new trades.
        private readonly ConcurrentDictionary<string, HashSet<string>> _seenTradeIds = new();
        private string? _focusedTicker;   // Trade tape only fires for the actively viewed ticker
        private readonly HttpClient _http;
        private readonly RSA _rsa;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private bool _disposed;

        // 1Hz baseline. Scaled up automatically when many tickers are watched
        // so total req/s stays within Kalshi's basic-tier ceiling (~10/s read).
        // See ComputeLoopDelayMs().
        private const int PollMsBase = 1000;
        private const int TargetReqPerSec = 8;   // headroom under the 10/s nominal cap

        private int ComputeLoopDelayMs()
        {
            // Each loop iteration polls every watched ticker once + (optionally) one
            // trade-tape call. So req/s ≈ (n_books + 1) * (1000 / loopMs).
            // Keep that under TargetReqPerSec.
            int n = _books.Count + (string.IsNullOrEmpty(_focusedTicker) ? 0 : 1);
            if (n <= 0) return PollMsBase;
            int needed = (int)Math.Ceiling((double)n * 1000.0 / TargetReqPerSec);
            return Math.Max(PollMsBase, needed);
        }

        private KalshiBrowserPoller()
        {
            _http = new HttpClient { BaseAddress = new Uri(ProdBase) };
            if (KalshiCredentials.TryLoadProd(out var keyId, out var rsa, out var error))
            {
                _keyId = keyId;
                _rsa = rsa;
            }
            else
            {
                _keyId = "";
                _rsa = RSA.Create();
                log.Warn($"BrowserPoller: prod credentials not configured — polling will fail. {error}");
            }

            // When the user picks a ticker (Watch List, Strike Ladder, Browser),
            // remember it so the trade-tape poll fires for that ticker.
            KalshiViewRequest.OnRequest += (sym, _) => _focusedTicker = sym;

            _loop = Task.Run(LoopAsync);
            log.Info("KalshiBrowserPoller started");
        }

        public IReadOnlyCollection<string> WatchedTickers => _books.Keys.ToArray();

        public void Watch(IEnumerable<string> tickers)
        {
            int registeredCount = 0;
            foreach (var t in tickers)
            {
                // Kalshi has plenty of non-KX tickers (CONTROLH, GOVPARTY*, EUEXIT, …).
                // Just require non-empty and reasonably ticker-like.
                if (string.IsNullOrWhiteSpace(t) || t.Length < 3) continue;
                _books.TryAdd(t, new OrderBook(t, priceDecimalPlaces: 0, maxDepth: 50)
                {
                    ProviderID = KalshiProviderId,
                    ProviderName = KalshiProviderName
                });
                // Always re-register with HelperSymbol — it dedupes internally and
                // raises OnCollectionChanged only on the first add. Calling it
                // unconditionally keeps the dropdown in sync even if the user
                // double-clicks the same event twice or restarts a session.
                try
                {
                    bool wasNew = !HelperSymbol.Instance.Contains(t);
                    HelperSymbol.Instance.UpdateData(t);
                    if (wasNew) registeredCount++;
                }
                catch (Exception ex) { log.Warn($"HelperSymbol register {t}: {ex.Message}"); }
            }
            log.Info($"Watch: +{registeredCount} new symbol(s); total watched = {_books.Count}");
        }

        public void Unwatch(string ticker) => _books.TryRemove(ticker, out _);
        public void UnwatchAll() => _books.Clear();

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                foreach (var kv in _books.ToArray())
                {
                    try { await PollOnceAsync(kv.Key, kv.Value); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { log.Warn($"poll {kv.Key}: {ex.Message}"); }
                }
                // Trade tape: fetch recent trades for the currently focused ticker
                // (the one in the main Provider/Symbol view). Avoids polling trades
                // for all 100+ watched tickers and blowing past the rate limit.
                var focus = _focusedTicker;
                if (!string.IsNullOrEmpty(focus))
                {
                    try { await PollTradesAsync(focus); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { log.Warn($"trades {focus}: {ex.Message}"); }
                }

                int delay = ComputeLoopDelayMs();
                try { await Task.Delay(delay, _cts.Token); }
                catch { return; }
            }
        }

        private async Task PollTradesAsync(string ticker)
        {
            var path = "/trade-api/v2/markets/trades";
            using var req = BuildRequest(HttpMethod.Get, path, $"?ticker={Uri.EscapeDataString(ticker)}&limit=20");
            using var resp = await _http.SendAsync(req, _cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("trades", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            var seen = _seenTradeIds.GetOrAdd(ticker, _ => new HashSet<string>());
            // Iterate oldest -> newest by reversing (Kalshi returns newest first)
            var pending = new List<Trade>();
            foreach (var t in arr.EnumerateArray())
            {
                var tradeId = t.TryGetProperty("trade_id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(tradeId)) continue;
                lock (seen) { if (!seen.Add(tradeId)) continue; }

                double yesPrice = 0;
                if (t.TryGetProperty("yes_price_dollars", out var ypEl)
                    && double.TryParse(ypEl.GetString(), System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out var yp))
                    yesPrice = yp * 100.0;

                double count = 0;
                if (t.TryGetProperty("count_fp", out var cEl)
                    && double.TryParse(cEl.GetString(), System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out var cv))
                    count = cv;

                bool isBuy = t.TryGetProperty("taker_side", out var sEl)
                          && string.Equals(sEl.GetString(), "yes", StringComparison.OrdinalIgnoreCase);

                DateTime ts = DateTime.UtcNow;
                if (t.TryGetProperty("created_time", out var ctEl)
                    && DateTimeOffset.TryParse(ctEl.GetString(), out var dto))
                    ts = dto.LocalDateTime;

                pending.Add(new Trade
                {
                    Symbol = ticker,
                    ProviderId = KalshiProviderId,
                    ProviderName = KalshiProviderName,
                    Price = (decimal)yesPrice,
                    Size = (decimal)count,
                    IsBuy = isBuy,
                    Timestamp = ts,
                });
            }
            // pending is newest-first; reverse so the tape grows in time-order
            pending.Reverse();
            foreach (var trade in pending)
            {
                try { HelperTrade.Instance.UpdateData(trade); }
                catch (Exception ex) { log.Warn($"HelperTrade.UpdateData {ticker}: {ex.Message}"); }
            }

            // Cap memory: keep at most last 200 trade-ids per ticker
            lock (seen)
            {
                if (seen.Count > 400)
                {
                    seen.Clear();
                    foreach (var t in arr.EnumerateArray())
                    {
                        var id = t.TryGetProperty("trade_id", out var idEl) ? idEl.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(id)) seen.Add(id);
                    }
                }
            }
        }

        private async Task PollOnceAsync(string ticker, OrderBook book)
        {
            var path = $"/trade-api/v2/markets/{ticker}/orderbook";
            using var req = BuildRequest(HttpMethod.Get, path, $"?depth=50");
            using var resp = await _http.SendAsync(req, _cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var bids = new List<BookItem>();
            var asks = new List<BookItem>();
            var now = DateTime.UtcNow;

            if (doc.RootElement.TryGetProperty("orderbook_fp", out var ob))
            {
                if (ob.TryGetProperty("yes_dollars", out var yes) && yes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lvl in yes.EnumerateArray())
                    {
                        if (lvl.GetArrayLength() < 2) continue;
                        if (!double.TryParse(lvl[0].GetString(), out var p)) continue;
                        if (!double.TryParse(lvl[1].GetString(), out var q)) continue;
                        bids.Add(new BookItem
                        {
                            Symbol = ticker, ProviderID = KalshiProviderId, IsBid = true,
                            Price = Math.Round(p * 100.0, 0), Size = q,
                            EntryID = $"y{p:F4}", LayerName = "MM",
                            LocalTimeStamp = now, ServerTimeStamp = now
                        });
                    }
                }
                if (ob.TryGetProperty("no_dollars", out var no) && no.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lvl in no.EnumerateArray())
                    {
                        if (lvl.GetArrayLength() < 2) continue;
                        if (!double.TryParse(lvl[0].GetString(), out var pNo)) continue;
                        if (!double.TryParse(lvl[1].GetString(), out var q)) continue;
                        asks.Add(new BookItem
                        {
                            Symbol = ticker, ProviderID = KalshiProviderId, IsBid = false,
                            Price = Math.Round((1.0 - pNo) * 100.0, 0), Size = q,
                            EntryID = $"n{pNo:F4}", LayerName = "MM",
                            LocalTimeStamp = now, ServerTimeStamp = now
                        });
                    }
                }
            }

            bids.Sort((a, b) => (b.Price ?? 0).CompareTo(a.Price ?? 0));
            asks.Sort((a, b) => (a.Price ?? 0).CompareTo(b.Price ?? 0));
            book.LoadData(asks, bids);

            try { HelperOrderBook.Instance.UpdateData(book); }
            catch (Exception ex) { log.Warn($"UpdateData {ticker}: {ex.Message}"); }
        }

        /// <summary>
        /// Human-readable metadata for a single Kalshi market. Filled lazily by
        /// <see cref="GetMarketInfoAsync"/> from <c>/markets/{ticker}</c>. All
        /// fields default to empty so callers can render fallbacks safely.
        /// </summary>
        public sealed record KalshiMarketInfo(string Title, string Subtitle, string YesSubTitle);

        // Process-wide cache: ladders typically reopen the same handful of
        // tickers, and these strings don't change for the life of a market.
        private static readonly ConcurrentDictionary<string, KalshiMarketInfo> _marketInfoCache = new();

        /// <summary>
        /// Resolve a ticker's title / subtitle / yes-side label. Returns a
        /// best-effort result (empty fields on auth/network failure) — never
        /// throws — so the caller can fall back to the raw ticker.
        /// </summary>
        public async Task<KalshiMarketInfo> GetMarketInfoAsync(string ticker, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ticker)) return new KalshiMarketInfo("", "", "");
            if (_marketInfoCache.TryGetValue(ticker, out var cached)) return cached;

            var path = $"/trade-api/v2/markets/{ticker}";
            try
            {
                using var req = BuildRequest(HttpMethod.Get, path, "");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    log.Warn($"GetMarketInfo {ticker}: {(int)resp.StatusCode}");
                    return new KalshiMarketInfo("", "", "");
                }
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("market", out var m))
                    return new KalshiMarketInfo("", "", "");

                string title    = m.TryGetProperty("title",         out var t) ? t.GetString() ?? "" : "";
                string subtitle = m.TryGetProperty("subtitle",      out var s) ? s.GetString() ?? "" : "";
                string yesSub   = m.TryGetProperty("yes_sub_title", out var y) ? y.GetString() ?? "" : "";

                var info = new KalshiMarketInfo(title, subtitle, yesSub);
                _marketInfoCache[ticker] = info;
                return info;
            }
            catch (OperationCanceledException) { return new KalshiMarketInfo("", "", ""); }
            catch (Exception ex)
            {
                log.Warn($"GetMarketInfo {ticker}: {ex.Message}");
                return new KalshiMarketInfo("", "", "");
            }
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string pathToSign, string queryString)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var msg = Encoding.UTF8.GetBytes(ts + method.Method + pathToSign);
            var sig = Convert.ToBase64String(_rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            var req = new HttpRequestMessage(method, pathToSign + queryString);
            req.Headers.Add("KALSHI-ACCESS-KEY", _keyId);
            req.Headers.Add("KALSHI-ACCESS-SIGNATURE", sig);
            req.Headers.Add("KALSHI-ACCESS-TIMESTAMP", ts);
            req.Headers.Add("Accept", "application/json");
            return req;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _cts.Cancel();
            _http.Dispose();
            _rsa.Dispose();
            _disposed = true;
        }
    }
}
