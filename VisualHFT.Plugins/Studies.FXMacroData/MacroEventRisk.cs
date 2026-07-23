#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace VisualHFT.Studies.FXMacroData
{
    public sealed class CalendarResponse
    {
        [JsonPropertyName("data")]
        public List<CalendarEvent> Data { get; set; } = new List<CalendarEvent>();
    }

    public sealed class CalendarEvent
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("release")]
        public string Release { get; set; } = string.Empty;

        [JsonPropertyName("announcement_datetime_utc")]
        public DateTimeOffset? AnnouncementDatetimeUtc { get; set; }

        [JsonPropertyName("release_date_confirmed")]
        public bool ReleaseDateConfirmed { get; set; }

        [JsonPropertyName("top_tier_for_currency")]
        public bool TopTierForCurrency { get; set; }
    }

    public sealed record MacroRiskSnapshot(
        bool IsActive,
        CalendarEvent? ActiveEvent,
        CalendarEvent? NextEvent);

    public static class MacroEventRiskEvaluator
    {
        public static MacroRiskSnapshot Evaluate(
            IEnumerable<CalendarEvent> events,
            DateTimeOffset nowUtc,
            int minutesBeforeRelease,
            int minutesAfterRelease)
        {
            if (minutesBeforeRelease < 0)
                throw new ArgumentOutOfRangeException(nameof(minutesBeforeRelease));
            if (minutesAfterRelease < 0)
                throw new ArgumentOutOfRangeException(nameof(minutesAfterRelease));

            var eligible = events
                .Where(x => x.ReleaseDateConfirmed && x.TopTierForCurrency && x.AnnouncementDatetimeUtc.HasValue)
                .OrderBy(x => x.AnnouncementDatetimeUtc)
                .ToList();

            var active = eligible
                .Where(x => x.AnnouncementDatetimeUtc >= nowUtc.AddMinutes(-minutesAfterRelease) &&
                            x.AnnouncementDatetimeUtc <= nowUtc.AddMinutes(minutesBeforeRelease))
                .OrderBy(x => Math.Abs((x.AnnouncementDatetimeUtc!.Value - nowUtc).Ticks))
                .FirstOrDefault();

            var next = eligible
                .Where(x => x.AnnouncementDatetimeUtc > nowUtc)
                .FirstOrDefault();

            return new MacroRiskSnapshot(active != null, active, next);
        }
    }
}
