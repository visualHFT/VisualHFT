using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace VisualHFT.Helpers
{
    /// <summary>
    /// Coordinates all UI update participants into a single frame-budgeted timer,
    /// preventing timer alignment stacking that causes UI freezes.
    ///
    /// Instead of N independent DispatcherTimers (one per UIUpdater) firing independently
    /// and potentially aligning on the same frame, FrameCoordinator uses a single timer
    /// and round-robins participants within a per-frame time budget.
    /// </summary>
    public sealed class FrameCoordinator : IDisposable
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly Lazy<FrameCoordinator> _instance = new Lazy<FrameCoordinator>(() => new FrameCoordinator());

        /// <summary>
        /// Singleton instance. Created lazily on first access.
        /// </summary>
        public static FrameCoordinator Instance => _instance.Value;

        private readonly DispatcherTimer _frameTimer;
        private readonly List<FrameParticipant> _participants = new List<FrameParticipant>();
        private readonly Stopwatch _frameStopwatch = new Stopwatch();
        private int _roundRobinIndex;
        private bool _disposed;

        /// <summary>
        /// Maximum time budget per frame in milliseconds.
        /// Participants that exceed this budget are deferred to the next frame.
        /// Default: 12ms (leaves ~4ms headroom for a 60fps target of 16.67ms).
        /// </summary>
        public double FrameBudgetMs { get; set; } = 12.0;

        /// <summary>
        /// The frame timer interval in milliseconds.
        /// Default: 16 (~60fps).
        /// </summary>
        public double FrameIntervalMs
        {
            get => _frameTimer.Interval.TotalMilliseconds;
            set => _frameTimer.Interval = TimeSpan.FromMilliseconds(value);
        }

        // Diagnostics
        public int ParticipantCount => _participants.Count;
        public int LastFrameParticipantsRun { get; private set; }
        public double LastFrameBudgetUsedMs { get; private set; }
        public int SkippedCount { get; private set; }

        private FrameCoordinator()
        {
            _frameTimer = new DispatcherTimer(DispatcherPriority.Input);
            _frameTimer.Interval = TimeSpan.FromMilliseconds(16);
            _frameTimer.Tick += OnFrameTick;
        }

        /// <summary>
        /// Registers a participant with the coordinator.
        /// </summary>
        /// <param name="callback">The action to invoke on the UI thread.</param>
        /// <param name="intervalMs">Minimum interval between invocations in milliseconds.</param>
        /// <returns>A FrameParticipant handle that can be used to unregister.</returns>
        public FrameParticipant Register(Action callback, double intervalMs)
        {
            var participant = new FrameParticipant(callback, intervalMs);
            _participants.Add(participant);

            // Auto-start the frame timer when first participant registers
            if (_participants.Count == 1)
            {
                _frameTimer.Start();
            }

            return participant;
        }

        /// <summary>
        /// Unregisters a participant from the coordinator.
        /// </summary>
        public void Unregister(FrameParticipant participant)
        {
            if (participant == null) return;

            _participants.Remove(participant);

            // Auto-stop when no participants
            if (_participants.Count == 0)
            {
                _frameTimer.Stop();
            }
        }

        private void OnFrameTick(object? sender, EventArgs e)
        {
            if (_participants.Count == 0) return;

            _frameStopwatch.Restart();
            int participantsRun = 0;
            int skipped = 0;
            long nowTicks = Stopwatch.GetTimestamp();
            double tickFrequency = Stopwatch.Frequency / 1000.0; // ticks per ms

            // Round-robin through participants starting from where we left off
            int count = _participants.Count;
            for (int i = 0; i < count; i++)
            {
                int index = (_roundRobinIndex + i) % count;
                var participant = _participants[index];

                if (!participant.IsActive) continue;

                // Check if enough time has elapsed since last execution
                double elapsedMs = (nowTicks - participant.LastExecutedTick) / tickFrequency;
                if (elapsedMs < participant.IntervalMs)
                {
                    continue; // Not time yet for this participant
                }

                // Check frame budget
                if (_frameStopwatch.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                {
                    skipped++;
                    continue; // Budget exhausted, defer to next frame
                }

                // Execute participant
                try
                {
                    participant.Callback();
                    participant.LastExecutedTick = Stopwatch.GetTimestamp();
                    participantsRun++;
                }
                catch (Exception ex)
                {
                    log.Error($"FrameCoordinator participant error", ex);
                }
            }

            // Advance round-robin starting point for next frame
            _roundRobinIndex = (_roundRobinIndex + 1) % Math.Max(1, count);

            // Update diagnostics
            LastFrameParticipantsRun = participantsRun;
            LastFrameBudgetUsedMs = _frameStopwatch.Elapsed.TotalMilliseconds;
            SkippedCount = skipped;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _frameTimer.Stop();
            _frameTimer.Tick -= OnFrameTick;
            _participants.Clear();
        }
    }

    /// <summary>
    /// Represents a registered participant in the FrameCoordinator.
    /// </summary>
    public class FrameParticipant
    {
        internal Action Callback { get; }
        public double IntervalMs { get; internal set; }
        internal long LastExecutedTick { get; set; }
        public bool IsActive { get; set; } = true;

        internal FrameParticipant(Action callback, double intervalMs)
        {
            Callback = callback ?? throw new ArgumentNullException(nameof(callback));
            IntervalMs = Math.Max(1, intervalMs);
            LastExecutedTick = 0; // Will execute on first opportunity
        }
    }
}
