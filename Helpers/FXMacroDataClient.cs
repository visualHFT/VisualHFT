using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VisualHFT.Helpers
{
    public sealed class FXMacroDataClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string? _apiKey;

        public FXMacroDataClient(HttpClient httpClient, string? apiKey = null, string baseUrl = "https://api.fxmacrodata.com/v1")
        {
            _httpClient = httpClient;
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public Task<string> DataCatalogueAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/data_catalogue/{Normalize(currency)}", null, cancellationToken);

        public Task<string> AnnouncementsAsync(string currency, string indicator, CancellationToken cancellationToken = default) =>
            GetAsync($"/announcements/{Normalize(currency)}/{indicator}", null, cancellationToken);

        public Task<string> CalendarAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/calendar/{Normalize(currency)}", null, cancellationToken);

        public Task<string> PredictionsAsync(string currency, string indicator, CancellationToken cancellationToken = default) =>
            GetAsync($"/predictions/{Normalize(currency)}/{indicator}", null, cancellationToken);

        public Task<string> ForexAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken = default) =>
            GetAsync($"/forex/{Normalize(baseCurrency)}/{Normalize(quoteCurrency)}", null, cancellationToken);

        public Task<string> CotAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/cot/{Normalize(currency)}", null, cancellationToken);

        public Task<string> CommoditiesLatestAsync(CancellationToken cancellationToken = default) =>
            GetAsync("/commodities/latest", null, cancellationToken);

        public Task<string> CommodityAsync(string indicator, CancellationToken cancellationToken = default) =>
            GetAsync($"/commodities/{indicator}", null, cancellationToken);

        public Task<string> CurvesAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/curves/{Normalize(currency)}", null, cancellationToken);

        public Task<string> CurveProxiesAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/curve_proxies/{Normalize(currency)}", null, cancellationToken);

        public Task<string> ForwardCurvesAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/forward_curves/{Normalize(currency)}", null, cancellationToken);

        public Task<string> MarketSessionsAsync(CancellationToken cancellationToken = default) =>
            GetAsync("/market_sessions", null, cancellationToken);

        public Task<string> RiskSentimentAsync(CancellationToken cancellationToken = default) =>
            GetAsync("/risk_sentiment", null, cancellationToken);

        public Task<string> NewsAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/news/{Normalize(currency)}", null, cancellationToken);

        public Task<string> PressReleasesAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/press-releases/{Normalize(currency)}", null, cancellationToken);

        public Task<string> CentralBankersAsync(string currency, CancellationToken cancellationToken = default) =>
            GetAsync($"/central_bankers/{Normalize(currency)}", null, cancellationToken);

        private async Task<string> GetAsync(string path, IDictionary<string, string>? query, CancellationToken cancellationToken)
        {
            var parameters = query == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(query);

            if (_apiKey != null)
                parameters["api_key"] = _apiKey;

            var queryString = parameters.Count == 0
                ? string.Empty
                : "?" + string.Join("&", parameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

            using var response = await _httpClient.GetAsync(_baseUrl + path + queryString, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string Normalize(string currency) => currency.Trim().ToLowerInvariant();
    }
}
