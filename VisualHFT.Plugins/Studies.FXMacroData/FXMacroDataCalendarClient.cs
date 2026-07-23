using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VisualHFT.Studies.FXMacroData
{
    internal sealed class FXMacroDataCalendarClient
    {
        private const string ApiBaseUrl = "https://api.fxmacrodata.com/v1/";
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;

        public FXMacroDataCalendarClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<IReadOnlyList<CalendarEvent>> GetUpcomingUsdEventsAsync(
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            var startDate = nowUtc.UtcDateTime.Date.AddDays(-1);
            var endDate = nowUtc.UtcDateTime.Date.AddDays(7);
            var requestUri = $"{ApiBaseUrl}calendar/usd?start_date={startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&end_date={endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var responseBody = await JsonSerializer.DeserializeAsync<CalendarResponse>(content, SerializerOptions, cancellationToken).ConfigureAwait(false);
            return responseBody?.Data ?? new List<CalendarEvent>();
        }
    }
}
