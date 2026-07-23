using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.Studies.FXMacroData.Model;
using VisualHFT.Studies.FXMacroData.UserControls;
using VisualHFT.Studies.FXMacroData.ViewModel;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies.FXMacroData
{
    public class FXMacroDataMacroEventRiskStudy : BasePluginStudy
    {
        private static readonly HttpClient HttpClient = new HttpClient();
        private readonly FXMacroDataCalendarClient _calendarClient = new FXMacroDataCalendarClient(HttpClient);
        private MacroRiskSettings _settings = null!;
        private CancellationTokenSource? _refreshCancellation;
        private Task? _refreshTask;
        private DateTimeOffset? _lastAlertedRelease;

        public override event EventHandler<decimal> OnAlertTriggered = delegate { };
        public override bool EmitsMetric => true;

        public override string Name { get; set; } = "FXMacroData Macro Event Risk";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Emits a 1 while a confirmed, top-tier USD macroeconomic release is within the configured risk window and 0 otherwise.";
        public override string Author { get; set; } = "FXMacroData";
        public override ISetting Settings { get => _settings; set => _settings = (MacroRiskSettings)value; }
        public override Action CloseSettingWindow { get; set; } = null!;
        public override string TileTitle { get; set; } = "Macro risk";
        public override string TileToolTip { get; set; } = "<b>Macro event risk</b><br/>Uses the public FXMacroData USD calendar. The metric is 1 during the configured window around a confirmed, top-tier release and 0 at other times. No API key is used.";

        public override async Task StartAsync()
        {
            await base.StartAsync().ConfigureAwait(false);

            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = new CancellationTokenSource();
            _lastAlertedRelease = null;
            Status = ePluginStatus.STARTED;
            _refreshTask = RefreshLoopAsync(_refreshCancellation.Token);
            log.Info($"{Name} Plugin has successfully started.");
        }

        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            var cancellation = _refreshCancellation;
            var refreshTask = _refreshTask;
            _refreshCancellation = null;
            _refreshTask = null;
            cancellation?.Cancel();

            if (refreshTask != null)
            {
                try
                {
                    await refreshTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the study is stopped.
                }
            }

            cancellation?.Dispose();
            await base.StopAsync().ConfigureAwait(false);
        }

        private async Task RefreshLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PublishError(ex);
                }

                var refreshMinutes = Math.Clamp(_settings.RefreshIntervalMinutes, 1, 60);
                await Task.Delay(TimeSpan.FromMinutes(refreshMinutes), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RefreshAsync(CancellationToken cancellationToken)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var events = await _calendarClient.GetUpcomingUsdEventsAsync(nowUtc, cancellationToken).ConfigureAwait(false);
            var snapshot = MacroEventRiskEvaluator.Evaluate(
                events,
                nowUtc,
                _settings.MinutesBeforeRelease,
                _settings.MinutesAfterRelease);

            var activeEvent = snapshot.ActiveEvent;
            var tooltip = activeEvent == null
                ? BuildInactiveTooltip(snapshot.NextEvent)
                : $"Macro risk active: {activeEvent.Name} ({activeEvent.AnnouncementDatetimeUtc:yyyy-MM-dd HH:mm} UTC).";

            AddCalculation(new BaseStudyModel
            {
                Value = snapshot.IsActive ? 1m : 0m,
                Format = "N0",
                Timestamp = nowUtc.UtcDateTime,
                ValueColor = snapshot.IsActive ? "Red" : "Green",
                Tooltip = tooltip,
                Tag = activeEvent?.Release ?? string.Empty,
                IsIndependentMetric = true
            });

            if (snapshot.IsActive && activeEvent?.AnnouncementDatetimeUtc is DateTimeOffset announcementTime && announcementTime != _lastAlertedRelease)
            {
                _lastAlertedRelease = announcementTime;
                OnAlertTriggered.Invoke(this, 1m);
            }
        }

        private static string BuildInactiveTooltip(CalendarEvent? nextEvent)
        {
            return nextEvent == null
                ? "No confirmed, top-tier USD releases are in the current calendar window."
                : $"No macro risk window is active. Next release: {nextEvent.Name} ({nextEvent.AnnouncementDatetimeUtc:yyyy-MM-dd HH:mm} UTC).";
        }

        private void PublishError(Exception exception)
        {
            var message = $"FXMacroData calendar refresh failed: {exception.Message}";
            log.Error(message, exception);
            AddCalculation(new BaseStudyModel
            {
                Value = 0m,
                Format = "N0",
                Timestamp = DateTime.UtcNow,
                ValueColor = "Red",
                Tooltip = message,
                HasError = true,
                IsIndependentMetric = true
            });
        }

        protected override void onDataAggregation(List<BaseStudyModel> dataCollection, BaseStudyModel newItem, int lastItemAggregationCount)
        {
            dataCollection[^1].copyFrom(newItem);
            base.onDataAggregation(dataCollection, newItem, lastItemAggregationCount);
        }

        protected override void LoadSettings()
        {
            var savedSettings = LoadFromUserSettings<MacroRiskSettings>();
            if (savedSettings == null)
            {
                InitializeDefaultSettings();
                return;
            }

            _settings = savedSettings;
            _settings.MinutesBeforeRelease = Math.Max(0, _settings.MinutesBeforeRelease);
            _settings.MinutesAfterRelease = Math.Max(0, _settings.MinutesAfterRelease);
            _settings.RefreshIntervalMinutes = Math.Clamp(_settings.RefreshIntervalMinutes, 1, 60);
        }

        protected override void SaveSettings()
        {
            SaveToUserSettings(_settings);
        }

        protected override void InitializeDefaultSettings()
        {
            _settings = new MacroRiskSettings();
            SaveSettings();
        }

        public override object GetUISettings()
        {
            var view = new PluginSettingsView();
            var viewModel = new PluginSettingsViewModel(CloseSettingWindow)
            {
                MinutesBeforeRelease = _settings.MinutesBeforeRelease,
                MinutesAfterRelease = _settings.MinutesAfterRelease,
                RefreshIntervalMinutes = _settings.RefreshIntervalMinutes
            };

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.MinutesBeforeRelease = viewModel.MinutesBeforeRelease;
                _settings.MinutesAfterRelease = viewModel.MinutesAfterRelease;
                _settings.RefreshIntervalMinutes = viewModel.RefreshIntervalMinutes;
                SaveSettings();
                Task.Run(() => HandleRestart($"{Name} is reloading settings.", forceStartRegardlessStatus: true));
            };
            view.DataContext = viewModel;
            return view;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _refreshCancellation?.Cancel();
                _refreshCancellation?.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
