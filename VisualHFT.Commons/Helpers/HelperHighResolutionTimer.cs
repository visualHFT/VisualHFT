using System.Diagnostics;


namespace VisualHFT.Commons.Helpers
{
    public static class HelperHighResolutionTimer
    {
        // Stopwatch uses QueryPerformanceCounter on Windows and mach_absolute_time on macOS,
        // so the same busy-wait math stays valid on both platforms.
        private static long _FREQUENCY = 0;

        public static void DelayNanoseconds(long nanosecondsToWait, Func<double> getPlaybackFactor, Func<bool> isPaused, Action<long> updateUI, CancellationTokenSource cancellationTokenSource)
        {
            if (nanosecondsToWait < 0) return;

            long startTicks = Stopwatch.GetTimestamp();
            long frequency = Stopwatch.Frequency;
            double tickDurationInNanoseconds = 1_000_000_000.0 / frequency; // Converts ticks to nanoseconds

            long lastTicks = startTicks;
            long elapsedTicks = 0;

            while (true)
            {
                long currentTicks = Stopwatch.GetTimestamp();
                if (cancellationTokenSource != null && cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                if (isPaused != null && isPaused())
                    break;

                double playbackFactor = getPlaybackFactor();
                long diffLastStep = (long)((currentTicks - lastTicks) * playbackFactor);
                elapsedTicks += diffLastStep;

                if (elapsedTicks * tickDurationInNanoseconds >= nanosecondsToWait)
                    break;

                updateUI?.Invoke((long)(elapsedTicks * tickDurationInNanoseconds)); // Send nanoseconds to UI

                lastTicks = currentTicks;
            }
        }
        public static void DelayNanoseconds(long nanosecondsToWait, Func<double> getPlaybackFactor)
        {
            DelayNanoseconds(nanosecondsToWait, getPlaybackFactor, null, null, null);
        }



        public static void DelayMicroseconds(long microsecondsToWait, Func<double> getPlaybackFactor, Func<bool> isPaused, Action<long> updateUI, CancellationTokenSource cancellationTokenSource)
        {
            /*DelayMicroseconds(microsecondsToWait, getPlaybackFactor);
            return;*/

            if (microsecondsToWait < 0) return;
            if (_FREQUENCY == 0)
                _FREQUENCY = Stopwatch.Frequency;


            long startTicks = Stopwatch.GetTimestamp();

            double tickDurationInMicroseconds = _FREQUENCY / 1_000_000.0; // Converts ticks to microseconds

            long lastTicks = startTicks;
            long elapsedTicks = 0;

            while (true)
            {
                long currentTicks = Stopwatch.GetTimestamp();
                if (cancellationTokenSource != null && cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                if (isPaused != null && isPaused())
                    break;

                double playbackFactor = getPlaybackFactor();
                long diffLastStep = (long)((currentTicks - lastTicks) * playbackFactor);
                elapsedTicks += diffLastStep;

                if (elapsedTicks / tickDurationInMicroseconds >= microsecondsToWait)
                    break;

                updateUI?.Invoke((long)(elapsedTicks)); // Send microseconds to UI

                lastTicks = currentTicks;
            }
        }

        public static void DelayMilliseconds(long millisecondsToWait, Func<double> getPlaybackFactor, Func<bool> isPaused, Action<long> updateUI, CancellationTokenSource cancellationTokenSource)
        {
            if (millisecondsToWait < 0)
            {
                return;
            }

            // Convert milliseconds to microseconds for consistency with DelayMicroseconds
            long microsecondsToWait = millisecondsToWait * 1000;

            DelayMicroseconds(microsecondsToWait, getPlaybackFactor, isPaused, updateUI, cancellationTokenSource);
        }


        private static Stopwatch stopwatch = new Stopwatch();
        public static async Task DelayWithControl(
            TimeSpan originalDelay,
            Func<bool> isPaused,
            Func<double> getPlaybackFactor,
            Action<long> updateTimeLineUI,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            double playbackFactor = getPlaybackFactor(); // Initial playback factor

            while (stopwatch.Elapsed.TotalMilliseconds < originalDelay.TotalMilliseconds / playbackFactor)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (!isPaused())
                {
                    // Calculate the time to wait, adjusting the sleep time dynamically
                    var remaining = originalDelay - TimeSpan.FromMilliseconds(stopwatch.Elapsed.TotalMilliseconds * playbackFactor);
                    var sleepTime = TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, 1));

                    if (remaining.TotalMilliseconds > 1)
                        Thread.Sleep(sleepTime);  // Sleep to reduce CPU usage when a significant delay remains
                    else
                        Thread.Yield();  // Yield for very short remaining times

                    // Update playback factor in case it changes over time
                    playbackFactor = getPlaybackFactor();

                    // Update the UI with elapsed ticks or milliseconds
                    updateTimeLineUI((long)(stopwatch.Elapsed.Ticks * playbackFactor));
                }
                else
                {
                    // Pause handling: yield to avoid tight loops and keep checking the pause status
                    Thread.Yield();
                }
            }
        }


    }
}
