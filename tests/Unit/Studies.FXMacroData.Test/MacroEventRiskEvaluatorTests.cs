using System;
using System.Net;
using System.Net.Http;
using System.Text;
using VisualHFT.Studies.FXMacroData;

namespace Studies.FXMacroData.Test
{
    public class MacroEventRiskEvaluatorTests
    {
        [Fact]
        public void Evaluate_ActivatesInsideConfiguredPreReleaseWindow()
        {
            var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
            var release = CreateEvent(now.AddMinutes(20));

            var result = MacroEventRiskEvaluator.Evaluate(new[] { release }, now, 30, 15);

            Assert.True(result.IsActive, "A confirmed top-tier release inside the pre-release window must activate risk.");
            Assert.Same(release, result.ActiveEvent);
        }

        [Fact]
        public void Evaluate_StaysActiveInsideConfiguredPostReleaseWindow()
        {
            var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
            var release = CreateEvent(now.AddMinutes(-10));

            var result = MacroEventRiskEvaluator.Evaluate(new[] { release }, now, 30, 15);

            Assert.True(result.IsActive, "A release inside the configured post-release window must keep risk active.");
        }

        [Fact]
        public void Evaluate_IgnoresUnconfirmedAndNonTopTierRows()
        {
            var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
            var unconfirmed = CreateEvent(now.AddMinutes(10));
            unconfirmed.ReleaseDateConfirmed = false;
            var lowerTier = CreateEvent(now.AddMinutes(15));
            lowerTier.TopTierForCurrency = false;

            var result = MacroEventRiskEvaluator.Evaluate(new[] { unconfirmed, lowerTier }, now, 30, 15);

            Assert.False(result.IsActive, "Unconfirmed and non-top-tier rows must not activate the macro risk metric.");
            Assert.Null(result.ActiveEvent);
        }

        [Fact]
        public void Evaluate_ReportsTheNextEligibleReleaseOutsideTheRiskWindow()
        {
            var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
            var release = CreateEvent(now.AddHours(2));

            var result = MacroEventRiskEvaluator.Evaluate(new[] { release }, now, 30, 15);

            Assert.False(result.IsActive, "A release outside the configured window must not activate risk.");
            Assert.Same(release, result.NextEvent);
        }

        [Fact]
        public async Task CalendarClient_UsesThePublicUsdRouteAndParsesTypedEvents()
        {
            var handler = new StubHttpMessageHandler("""
                {
                  "data": [
                    {
                      "name": "Employment Situation",
                      "release": "BLS Employment Situation",
                      "announcement_datetime_utc": "2026-07-22T12:30:00+00:00",
                      "release_date_confirmed": true,
                      "top_tier_for_currency": true
                    }
                  ]
                }
                """);
            using var httpClient = new HttpClient(handler);
            var client = new FXMacroDataCalendarClient(httpClient);

            var events = await client.GetUpcomingUsdEventsAsync(
                new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero),
                CancellationToken.None);

            var request = Assert.IsType<HttpRequestMessage>(handler.Request);
            Assert.Equal("https://api.fxmacrodata.com/v1/calendar/usd?start_date=2026-07-21&end_date=2026-07-29", request.RequestUri?.ToString());
            Assert.DoesNotContain(request.Headers, header => header.Key.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase));
            Assert.Null(request.Headers.Authorization);
            var item = Assert.Single(events);
            Assert.Equal("Employment Situation", item.Name);
            Assert.Equal(new DateTimeOffset(2026, 7, 22, 12, 30, 0, TimeSpan.Zero), item.AnnouncementDatetimeUtc);
            Assert.True(item.ReleaseDateConfirmed);
            Assert.True(item.TopTierForCurrency);
        }

        private static CalendarEvent CreateEvent(DateTimeOffset announcementDatetimeUtc)
        {
            return new CalendarEvent
            {
                Name = "Employment Situation",
                Release = "BLS Employment Situation",
                AnnouncementDatetimeUtc = announcementDatetimeUtc,
                ReleaseDateConfirmed = true,
                TopTierForCurrency = true
            };
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly string _responseBody;

            public StubHttpMessageHandler(string responseBody)
            {
                _responseBody = responseBody;
            }

            public HttpRequestMessage? Request { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Request = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
