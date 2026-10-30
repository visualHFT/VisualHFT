using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
using Xunit;

namespace VisualHFT.DataRetriever.TestingFramework.TestCases
{
    /// <summary>
    /// Deterministic, offline tests for the SHARED reconnection engine in
    /// BasePluginDataRetriever.HandleConnectionLost (attempt cap, exponential-backoff orchestration,
    /// dedup/coalescing, terminal STOPPED_FAILED). Today this logic is only exercised by the live
    /// PluginFunctionalTests via a flaky hack (throwing inside the global order-book callback). Here it
    /// is driven against a minimal in-memory fake with the wall-clock backoff overridden to complete
    /// instantly — so the same engine is proven without network or multi-second waits.
    ///
    /// The second block of tests covers the engine's POST-START DECISION: what the retry loop must
    /// conclude from the status a connector's StartAsync leaves behind. A connector reports a failed
    /// start by handing off to HandleConnectionLost from inside its own catch (CoinbasePlugin's
    /// StartAsync does), the hand-off is swallowed by the re-entry guard at the top of
    /// HandleConnectionLost because the engine's own loop already owns the flag, and StartAsync then
    /// returns normally with Status still STARTING. The loop must read that as a failed attempt, never
    /// stamp STARTED over it: a half-started plugin must not read as green.
    ///
    /// The third block covers three rules of that post-start outcome, each named DEFECT 1/2/3 on its
    /// test: an external stop (a caller that stops the connector) is not retried as a failure, a forced
    /// restart does not override the connector's own terminal STOPPED_FAILED verdict, and the engine runs
    /// the connector's internal start ONCE per attempt (inside StartAsync, never itself as well).
    ///
    /// NOTE: this covers the engine ORCHESTRATION. The per-connector "reconnect re-seeds the book"
    /// assertion is covered separately (SimulateConnectionInterruption on each real connector), because
    /// a real connector's StartAsync re-opens the live socket and cannot complete a reconnect offline.
    /// </summary>
    public class ReconnectionEngineTests
    {
        // Mirrors the private const in BasePluginDataRetriever; asserted explicitly so a change to the
        // cap is a deliberate, visible test update.
        private const int MaxReconnectionAttempts = 5;

        /// <summary>
        /// The five ways a connector's StartAsync actually ends in this codebase. The engine has to tell
        /// them apart — only one of them is a successful reconnection.
        /// </summary>
        private enum StartOutcome
        {
            /// <summary>Clean start: Status = STARTED (every healthy connector, e.g. at the end of CoinbasePlugin's StartAsync).</summary>
            Started,

            /// <summary>
            /// The venue work threw and the connector's own catch handed the failure to the engine
            /// (CoinbasePlugin's StartAsync catch does this). Status is left where base.StartAsync() put it: STARTING.
            /// </summary>
            FailLikeRealConnector,

            /// <summary>
            /// The plugin deliberately declined to start and stayed idle: Status = LOADED
            /// (a plugin with nothing configured to run, for example).
            /// </summary>
            LeaveLoaded,

            /// <summary>The start concluded it had failed terminally: Status = STOPPED_FAILED.</summary>
            StoppedFailed,

            /// <summary>
            /// Somebody stopped the plugin while it was starting: Status = STOPPED. The case covered: a
            /// caller stops a plugin that is still STARTING by calling StopAsync() and then setting the
            /// status to STOPPED.
            /// </summary>
            LeaveStopped
        }

        [Fact]
        public async Task Reconnect_WhenActionSucceeds_TransitionsToStarted()
        {
            var fake = new ReconnectEngineFake();

            await fake.TriggerReconnect(force: true);

            Assert.Equal(ePluginStatus.STARTED, fake.Status);
            Assert.True(fake.ReconnectActionCallCount >= 1, "reconnect action should run at least once");
            Assert.True(fake.StartAsyncCallCount >= 1, "StartAsync should be invoked on a successful attempt");
        }

        [Fact]
        public async Task Reconnect_WhenEveryAttemptFails_ExhaustsCapThenStoppedFailed()
        {
            var fake = new ReconnectEngineFake { ShouldFail = true };

            await fake.TriggerReconnect(force: true);

            Assert.Equal(ePluginStatus.STOPPED_FAILED, fake.Status);
            // The engine must retry exactly up to the cap before giving up — no infinite loop, no early give-up.
            Assert.Equal(MaxReconnectionAttempts, fake.ReconnectActionCallCount);
            // DEFECT 3: the engine does not invoke the registered action itself. With the fake faithful to
            // real connectors (StartAsync awaits the internal start, as BinancePlugin's StartAsync does), one
            // attempt is one StartAsync that fails from the inside, so both counts must equal the cap.
            Assert.Equal(MaxReconnectionAttempts, fake.StartAsyncCallCount);
        }

        [Fact]
        public async Task Reconnect_ConcurrentCalls_AreCoalescedIntoOne()
        {
            var fake = new ReconnectEngineFake();
            var actionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Hold the first reconnection in-flight so its dedup flag is set when the second call arrives.
            // The gate lives in the reconnect action, which the faithful fake reaches from INSIDE
            // StartAsync, so it fires while HandleConnectionLost owns the dedup flag.
            fake.GateBeforeAction = async () =>
            {
                actionEntered.TrySetResult();
                await release.Task;
            };

            var first = fake.TriggerReconnect(force: true);
            await actionEntered.Task;                       // first reconnection is now inside the engine

            await fake.TriggerReconnect(force: true);       // must coalesce (return immediately), not start a 2nd

            release.SetResult();
            await first;

            // Only the first reconnection ran; the concurrent one was dropped by the atomic dedup flag.
            // Counted on StartAsync (one call == one attempt) rather than on the action, because the number
            // of action calls per attempt is exactly what DEFECT 3 is about and is pinned by its own test.
            Assert.Equal(1, fake.StartAsyncCallCount);
            Assert.Equal(ePluginStatus.STARTED, fake.Status);
        }

        /// <summary>
        /// THE ENGINE HOLE. A real connector signals a failed start by calling HandleConnectionLost from
        /// its own catch block; inside the engine's retry loop that call is a no-op (the re-entry guard at
        /// the top of HandleConnectionLost already owns the flag), so StartAsync returns normally with
        /// Status = STARTING. That is a FAILED attempt: the loop must retry it, up to the cap, and end in
        /// STOPPED_FAILED — never stamp STARTED over a plugin that never started.
        /// </summary>
        [Fact]
        public async Task Reconnect_WhenStartAsyncFailsLikeARealConnector_IsRetriedUpToCapThenStoppedFailed()
        {
            var fake = new ReconnectEngineFake { DefaultStartOutcome = StartOutcome.FailLikeRealConnector };

            await fake.TriggerReconnect(force: true);

            Assert.Equal(ePluginStatus.STOPPED_FAILED, fake.Status);
            Assert.Equal(MaxReconnectionAttempts, fake.StartAsyncCallCount);
            // One attempt runs the connector's internal start exactly once, so the two counts match
            // (DEFECT 3: the engine does not invoke the action itself as well).
            Assert.Equal(MaxReconnectionAttempts, fake.ReconnectActionCallCount);
            // A plugin that never started must never have been announced as CONNECTED.
            Assert.Equal(0, fake.ConnectedNotificationCount);
        }

        /// <summary>
        /// The recovery path the hole also hides: two failed starts followed by a clean one is a
        /// successful reconnection on the THIRD attempt, not on the first.
        /// </summary>
        [Fact]
        public async Task Reconnect_WhenStartAsyncFailsTwiceThenSucceeds_EndsStartedAfterThreeAttempts()
        {
            var fake = new ReconnectEngineFake();
            fake.EnqueueStartOutcomes(
                StartOutcome.FailLikeRealConnector,
                StartOutcome.FailLikeRealConnector,
                StartOutcome.Started);

            await fake.TriggerReconnect(force: true);

            Assert.Equal(ePluginStatus.STARTED, fake.Status);
            Assert.Equal(3, fake.StartAsyncCallCount);
            // Same one-internal-start-per-attempt rule as above (DEFECT 3): 3, not 6.
            Assert.Equal(3, fake.ReconnectActionCallCount);
            // CONNECTED is announced once — on the attempt that actually started, not on the failed ones.
            Assert.Equal(1, fake.ConnectedNotificationCount);
        }

        /// <summary>
        /// A plugin may decline to start on purpose and stay LOADED (nothing configured to run, so it
        /// is deliberately NOT presented as a connected feed). Retrying that is
        /// pointless and overwriting its status is a lie: the loop must stop and leave LOADED alone.
        /// </summary>
        [Fact]
        public async Task Reconnect_WhenStartAsyncLeavesLoaded_StopsWithoutMarkingStartedAndRaisesNoConnected()
        {
            var fake = new ReconnectEngineFake { DefaultStartOutcome = StartOutcome.LeaveLoaded };

            await fake.TriggerReconnect(force: true);

            Assert.Equal(ePluginStatus.LOADED, fake.Status);
            Assert.Equal(1, fake.StartAsyncCallCount);
            Assert.DoesNotContain(eSESSIONSTATUS.CONNECTED, fake.ProviderStatuses);
        }

        /// <summary>
        /// Regression guard for the one post-start branch the engine already gets right: an unforced
        /// reconnection whose start reports STOPPED_FAILED gives up immediately. Expected GREEN before
        /// the fix as well as after — it pins the behaviour the fix must not break.
        /// </summary>
        [Fact]
        public async Task Reconnect_WhenStartAsyncReportsStoppedFailed_GivesUpWithoutForce()
        {
            var fake = new ReconnectEngineFake { DefaultStartOutcome = StartOutcome.StoppedFailed };
            // The unforced entry guard in HandleConnectionLost skips reconnection outright
            // when Status is already STOPPED_FAILED/STOPPING, so start from a running plugin.
            fake.Status = ePluginStatus.STARTED;

            // Giving up is NOT a failed attempt: the loop must exit without routing the STOPPED_FAILED
            // result through its failure path (which logs "Reconnection failed. Attempt N" and notifies the UI).
            var failedAttemptNotifications = await CaptureFailedAttemptNotificationsAsync(
                fake.Name, () => fake.TriggerReconnect(force: false));

            Assert.Equal(ePluginStatus.STOPPED_FAILED, fake.Status);
            Assert.Equal(1, fake.StartAsyncCallCount);
            Assert.Equal(0, fake.ConnectedNotificationCount);
            Assert.True(failedAttemptNotifications.Count == 0,
                "an unforced STOPPED_FAILED result must exit the loop directly, not be counted as a failed attempt: " + string.Join(" | ", failedAttemptNotifications));
            // The UI shows the LAST provider status announced. Giving up must announce the terminal
            // failure, otherwise the provider keeps whatever was announced last (a venue library can
            // announce CONNECTED shortly before the connector gives up): a green status on a dead feed.
            Assert.Equal(eSESSIONSTATUS.DISCONNECTED_FAILED, fake.ProviderStatuses.Last());
        }

        /// <summary>
        /// The give-up exit resets the attempt counter. Without the reset, every settings-dialog reload
        /// (a forced reconnect) on a connector that keeps reporting STOPPED_FAILED leaves the count
        /// standing; after five such reloads the retry loop's while-condition is false on entry and the
        /// sixth reload runs no start at all — silently. Six forced reloads must run six starts.
        /// </summary>
        [Fact]
        public async Task Reconnect_RepeatedForcedReloads_OnAConnectorThatKeepsGivingUp_EachRunTheStart()
        {
            var fake = new ReconnectEngineFake { DefaultStartOutcome = StartOutcome.StoppedFailed };

            for (int reload = 0; reload < MaxReconnectionAttempts + 1; reload++)
            {
                await fake.TriggerReconnect(force: true);
            }

            Assert.Equal(MaxReconnectionAttempts + 1, fake.StartAsyncCallCount);
            Assert.Equal(ePluginStatus.STOPPED_FAILED, fake.Status);
        }

        /// <summary>
        /// The retry loop's backoff must go through the ReconnectBackoffDelayAsync seam that exists for
        /// exactly this purpose. If the loop awaited Task.Delay directly, the seam override would be dead
        /// and a full-cap engine test would sleep for ~62s of real wall clock (2+4+8+16+32s plus jitter).
        /// </summary>
        [Fact]
        public async Task Reconnect_BackoffGoesThroughTheOverridableSeam_OnEveryAttempt()
        {
            var fake = new ReconnectEngineFake { ShouldFail = true };

            var stopwatch = Stopwatch.StartNew();
            await fake.TriggerReconnect(force: true);
            stopwatch.Stop();

            // Structural, not wall-clock: every attempt's delay must be the seam call. (A bypassed seam
            // would show as a missing call here; the hang guard below only catches a real-clock backoff.)
            Assert.Equal(MaxReconnectionAttempts, fake.BackoffDelayCallCount);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"the {MaxReconnectionAttempts}-attempt cap under an overridden backoff took {stopwatch.Elapsed.TotalSeconds:F1}s — a real-clock backoff leaked in");
            Assert.Equal(ePluginStatus.STOPPED_FAILED, fake.Status);
        }

        /// <summary>
        /// DEFECT 1 — an external stop must not be retried as if it were a failed connection attempt.
        /// Mid-reconnect, a caller stops a plugin that is still STARTING: it calls the retriever's
        /// StopAsync() and then sets Status = STOPPED. If the retry loop read STOPPED as "the attempt
        /// did not reach STARTED", it would throw and retry to the cap: ~62s of real backoff
        /// (2+4+8+16+32s), an ERROR notification per attempt, and a plugin stopped on purpose left in
        /// STOPPED_FAILED, which the unforced entry guard then refuses to restart.
        ///
        /// Rule: after the retried StartAsync, only STARTING (the status base.StartAsync() stamps, and
        /// the one a connector's own failure hand-off leaves behind) is a failed attempt. STOPPED or
        /// STOPPING means somebody stopped the plugin: exit the loop, retry nothing, mark nothing,
        /// notify nothing.
        /// </summary>
        [Fact]
        public async Task Reconnect_WhenStartAsyncLeavesStopped_ExitsWithoutRetryingOrMarking()
        {
            var fake = new ReconnectEngineFake { DefaultStartOutcome = StartOutcome.LeaveStopped };

            var failedAttemptNotifications = await CaptureFailedAttemptNotificationsAsync(
                fake.Name, () => fake.TriggerReconnect(force: true));

            Assert.Equal(ePluginStatus.STOPPED, fake.Status);
            Assert.Equal(1, fake.StartAsyncCallCount);
            // Nothing started, so nothing may be announced as connected.
            Assert.Equal(0, fake.ConnectedNotificationCount);
            Assert.True(failedAttemptNotifications.Count == 0,
                "a plugin stopped externally must not be counted as a failed reconnection attempt: " + string.Join(" | ", failedAttemptNotifications));
            // The exit must leave the tile consistent with the status it is honouring (base.StartAsync
            // announced CONNECTING moments earlier; a stopped plugin must read as disconnected).
            Assert.Equal(eSESSIONSTATUS.DISCONNECTED, fake.ProviderStatuses.Last());
        }

        /// <summary>
        /// DEFECT 2 — a forced restart must not override the connector's own terminal verdict.
        /// If the post-start STOPPED_FAILED exit were gated on !forceStartRegardlessStatus, a forced
        /// start that reported STOPPED_FAILED would fall through to the failure throw and be retried 5
        /// times. Binance and Gemini set STOPPED_FAILED deliberately and WITHOUT retry on a
        /// [CantConnectError], and every settings-dialog reload enters the engine with force:true.
        ///
        /// Rule: force only bypasses the ENTRY guard in HandleConnectionLost, so a previously failed
        /// plugin can be restarted at all. STOPPED_FAILED reported *by the start itself* is the connector
        /// giving up: honour it immediately, forced or not.
        /// </summary>
        [Fact]
        public async Task Reconnect_ForcedRestart_WhenStartReportsStoppedFailed_GivesUpImmediately()
        {
            var fake = new ReconnectEngineFake { DefaultStartOutcome = StartOutcome.StoppedFailed };

            var failedAttemptNotifications = await CaptureFailedAttemptNotificationsAsync(
                fake.Name, () => fake.TriggerReconnect(force: true));

            Assert.Equal(ePluginStatus.STOPPED_FAILED, fake.Status);
            Assert.Equal(1, fake.StartAsyncCallCount);
            Assert.Equal(0, fake.ConnectedNotificationCount);
            Assert.True(failedAttemptNotifications.Count == 0,
                "a start that reported STOPPED_FAILED must end the loop even when forced, not be retried to the cap: " + string.Join(" | ", failedAttemptNotifications));
            // Same terminal announcement as the unforced give-up: the tile must not keep a stale status.
            Assert.Equal(eSESSIONSTATUS.DISCONNECTED_FAILED, fake.ProviderStatuses.Last());
        }

        /// <summary>
        /// DEFECT 3 — the engine must not run the connector's internal start TWICE per attempt.
        /// Every connector (Binance, Bitfinex, BitStamp, Coinbase, Gemini, Kraken, KuCoin) registers its
        /// internal start with SetReconnectionAction and also awaits that same method from inside its
        /// own StartAsync. If the loop invoked the registered action itself and then called StartAsync(),
        /// one reconnection attempt would fire two full subscribe bursts milliseconds apart against the
        /// venue, and the SECOND one's status would decide the attempt.
        ///
        /// Rule: one attempt = one StartAsync() call. SetReconnectionAction stays as a registration API,
        /// but the engine does not invoke the action itself — the connector's own start is what performs
        /// the internal start.
        /// </summary>
        [Fact]
        public async Task Reconnect_RunsTheConnectorStartOncePerAttempt_NeverTheActionSeparately()
        {
            var fake = new ReconnectEngineFake { DefaultStartOutcome = StartOutcome.Started };

            await fake.TriggerReconnect(force: true);

            Assert.Equal(ePluginStatus.STARTED, fake.Status);
            Assert.Equal(1, fake.StartAsyncCallCount);
            // One: the engine does not invoke the action on top of the one inside StartAsync.
            Assert.Equal(1, fake.ReconnectActionCallCount);
        }

        /// <summary>
        /// A connector the user stopped must stay stopped. The sequence on a real connector: the stop
        /// closes the socket, the close is reported as a server close while the status still reads as
        /// running, HandleConnectionLost starts a reconnection and waits out its backoff, and the stop then
        /// lands (STOPPED). If the loop checked only STOPPED_FAILED after the backoff, it would run its own
        /// StopAsync/StartAsync and revive the connector a few seconds after the user stopped it.
        ///
        /// Rule: a StopAsync from outside the engine during the backoff means stopped on purpose: exit
        /// without a restart, announcing the status like the other non-success exits.
        /// </summary>
        [Fact]
        public async Task Reconnect_StoppedFromOutsideDuringTheBackoff_DoesNotRestart()
        {
            var fake = new ReconnectEngineFake { Status = ePluginStatus.STARTED };
            fake.DuringBackoff = () => fake.StopAsync();

            var failedAttemptNotifications = await CaptureFailedAttemptNotificationsAsync(
                fake.Name, () => fake.TriggerReconnect(force: false));

            Assert.Equal(1, fake.BackoffDelayCallCount);
            Assert.Equal(0, fake.StartAsyncCallCount);
            Assert.Equal(ePluginStatus.STOPPED, fake.Status);
            Assert.Equal(0, fake.ConnectedNotificationCount);
            Assert.True(failedAttemptNotifications.Count == 0,
                "a stop during the backoff is not a failed attempt: " + string.Join(" | ", failedAttemptNotifications));
            Assert.Equal(eSESSIONSTATUS.DISCONNECTED, fake.ProviderStatuses.Last());

            // The connector is stopped, not broken: the next start (and a later reconnection) works as before.
            fake.DuringBackoff = null;
            await fake.StartAsync();
            Assert.Equal(ePluginStatus.STARTED, fake.Status);
            fake.StartAsyncCallCount = 0;
            await fake.TriggerReconnect(force: false);
            Assert.Equal(1, fake.StartAsyncCallCount);
            Assert.Equal(ePluginStatus.STARTED, fake.Status);
        }

        /// <summary>
        /// A stop that lost a race to a start leaves the stop flag set on a running connector (the start
        /// finished after the stop and stamped STARTED). The flag alone must not refuse the next genuine
        /// drop: only a connector that is still stopped or stopping is left alone.
        /// </summary>
        [Fact]
        public async Task Reconnect_StaleStopFlagOnARunningConnector_StillReconnects()
        {
            var fake = new ReconnectEngineFake { Status = ePluginStatus.STARTED };

            // The user's stop runs, then the start it raced finishes and the connector is running again.
            await fake.StopAsync();
            fake.Status = ePluginStatus.STARTED;

            await fake.TriggerReconnect(force: false);

            Assert.Equal(1, fake.BackoffDelayCallCount);
            Assert.Equal(1, fake.StartAsyncCallCount);
            Assert.Equal(ePluginStatus.STARTED, fake.Status);
            Assert.Equal(eSESSIONSTATUS.CONNECTED, fake.ProviderStatuses.Last());
        }

        /// <summary>
        /// Guards the shape of the fix above. A connector may set STOPPED itself when its socket drops and
        /// then call HandleConnectionLost (BitStampPlugin's DisconnectionHappened handler does), so the
        /// status alone cannot mean "the user stopped it": that reconnection must still run.
        /// </summary>
        [Fact]
        public async Task Reconnect_ConnectorThatMarksItselfStoppedOnASocketDrop_StillReconnects()
        {
            var fake = new ReconnectEngineFake { Status = ePluginStatus.STARTED };
            fake.DuringBackoff = () =>
            {
                fake.Status = ePluginStatus.STOPPED;
                return Task.CompletedTask;
            };

            await fake.TriggerReconnect(force: false);

            Assert.Equal(1, fake.StartAsyncCallCount);
            Assert.Equal(ePluginStatus.STARTED, fake.Status);
        }

        /// <summary>
        /// A forced restart (the settings dialog's reload) still runs after an earlier user stop: force only
        /// bypasses the "stopped" guards, as it does for STOPPED_FAILED.
        /// </summary>
        [Fact]
        public async Task Reconnect_ForcedAfterAUserStop_StillRestarts()
        {
            var fake = new ReconnectEngineFake { Status = ePluginStatus.STARTED };
            await fake.StopAsync();

            await fake.TriggerReconnect(force: true);

            Assert.Equal(1, fake.StartAsyncCallCount);
            Assert.Equal(ePluginStatus.STARTED, fake.Status);
        }

        /// <summary>
        /// Captures the engine's failed-attempt notifications — "Reconnection failed. Attempt N of M",
        /// raised through LogException(..., NotifyToUI: true) in the retry loop's catch —
        /// for the duration of one reconnection, so a test can prove an outcome was NOT counted as a
        /// failed attempt. HelperNotificationManager raises NotificationAdded synchronously, so
        /// everything the run notified is captured by the
        /// time the awaited reconnection returns.
        /// </summary>
        private static async Task<IReadOnlyList<string>> CaptureFailedAttemptNotificationsAsync(string pluginName, Func<Task> reconnection)
        {
            var captured = new ConcurrentBag<string>();
            EventHandler<ErrorNotificationEventArgs> onNotification = (_, e) =>
            {
                var message = e.Notification.Message;
                if (message != null
                    && message.Contains(pluginName, StringComparison.Ordinal)
                    && message.Contains("Reconnection failed", StringComparison.Ordinal))
                {
                    captured.Add(message);
                }
            };

            HelperNotificationManager.Instance.NotificationAdded += onNotification;
            try
            {
                await reconnection();
            }
            finally
            {
                HelperNotificationManager.Instance.NotificationAdded -= onNotification;
            }

            return captured.ToList();
        }

        // ----------------------------------------------------------------------------------------
        // Minimal concrete BasePluginDataRetriever for driving the base-class reconnection engine.
        // It implements the abstract surface with no-ops and controls the reconnect outcome in-memory.
        // ----------------------------------------------------------------------------------------
        private sealed class ReconnectEngineFake : BasePluginDataRetriever
        {
            // Field initializer runs BEFORE the base constructor, so LoadSettings() (called from the
            // base ctor) can already see it.
            private readonly Provider _provider = new Provider { ProviderID = 4242, ProviderName = "RECONNECT_FAKE" };
            private readonly Queue<StartOutcome> _startOutcomes = new Queue<StartOutcome>();
            private readonly ConcurrentQueue<eSESSIONSTATUS> _providerStatuses = new ConcurrentQueue<eSESSIONSTATUS>();

            public bool ShouldFail { get; set; }
            public int ReconnectActionCallCount;
            public int StartAsyncCallCount;
            public int BackoffDelayCallCount;
            public Func<Task> GateBeforeAction;

            /// <summary>Runs inside the backoff wait, as anything else on the app's threads could (tests set it).</summary>
            public Func<Task> DuringBackoff;

            /// <summary>How StartAsync ends once the per-attempt queue is exhausted.</summary>
            public StartOutcome DefaultStartOutcome { get; set; } = StartOutcome.Started;

            /// <summary>Provider statuses announced through the base class, in order.</summary>
            public IReadOnlyList<eSESSIONSTATUS> ProviderStatuses => _providerStatuses.ToList();

            /// <summary>How many CONNECTED announcements the ENGINE made (see the note in StartAsync).</summary>
            public int ConnectedNotificationCount => _providerStatuses.Count(status => status == eSESSIONSTATUS.CONNECTED);

            public ReconnectEngineFake()
            {
                // Registered exactly as every connector registers its own InternalStartAsync
                // (BinancePlugin, CoinbasePlugin, KrakenPlugin, ...).
                SetReconnectionAction(ReconnectActionAsync);
            }

            /// <summary>Queues per-attempt StartAsync outcomes, consumed in order.</summary>
            public void EnqueueStartOutcomes(params StartOutcome[] outcomes)
            {
                foreach (var outcome in outcomes)
                {
                    _startOutcomes.Enqueue(outcome);
                }
            }

            private StartOutcome NextStartOutcome()
            {
                lock (_startOutcomes)
                {
                    return _startOutcomes.Count > 0 ? _startOutcomes.Dequeue() : DefaultStartOutcome;
                }
            }

            /// <summary>
            /// The connector's internal start: the subscribe burst that opens the venue socket. It is both
            /// the method registered with SetReconnectionAction AND the one the connector's own StartAsync
            /// awaits — one object, two call sites in production, which is why counting its invocations
            /// measures how many subscribe bursts one reconnection attempt actually fires.
            /// </summary>
            private async Task ReconnectActionAsync()
            {
                Interlocked.Increment(ref ReconnectActionCallCount);
                if (GateBeforeAction != null)
                {
                    await GateBeforeAction();
                }
                if (ShouldFail)
                {
                    throw new InvalidOperationException("Synthetic offline reconnect failure.");
                }
            }

            public Task TriggerReconnect(bool force) => HandleConnectionLost("test interruption", null, force);

            // Make the retry loop's backoff instant so the engine orchestration is deterministic and fast.
            protected override Task ReconnectBackoffDelayAsync(int milliseconds)
            {
                Interlocked.Increment(ref BackoffDelayCallCount);
                return DuringBackoff?.Invoke() ?? Task.CompletedTask;
            }

            /// <summary>
            /// Same shape as every real connector: base first (which raises CONNECTING and sets STARTING),
            /// then the connector's OWN internal start inside a try/catch, then one of the five endings.
            /// The status this leaves behind is the only thing the engine can read, so the fake must set
            /// it exactly like production does.
            ///
            /// The internal start here is the very method registered with SetReconnectionAction — that is
            /// how every real connector is wired (e.g. BinancePlugin registers InternalStartAsync and
            /// awaits it from StartAsync), and reproducing it is what would make a separate engine invoke
            /// of the same action visible as a double subscribe burst.
            /// </summary>
            public override async Task StartAsync()
            {
                Interlocked.Increment(ref StartAsyncCallCount);
                await base.StartAsync();

                var outcome = NextStartOutcome();
                switch (outcome)
                {
                    case StartOutcome.Started:
                    case StartOutcome.FailLikeRealConnector:
                        try
                        {
                            await ReconnectActionAsync();
                            if (outcome == StartOutcome.FailLikeRealConnector)
                            {
                                await FailingVenueStartAsync();
                            }

                            // Real connectors also announce CONNECTED here (CoinbasePlugin does). The
                            // fake deliberately does not, so ConnectedNotificationCount counts only what
                            // the ENGINE announced — otherwise the production duplicate would mask the
                            // engine's own.
                            Status = ePluginStatus.STARTED;
                        }
                        catch (Exception ex)
                        {
                            // As CoinbasePlugin's StartAsync catch does: log, then hand the failure to the engine.
                            // Inside the engine's own loop that hand-off is swallowed, and Status stays STARTING.
                            var error = ex.Message;
                            LogException(ex, error);
                            await HandleConnectionLost(error, ex);
                        }
                        break;

                    case StartOutcome.LeaveLoaded:
                        Status = ePluginStatus.LOADED;
                        break;

                    case StartOutcome.StoppedFailed:
                        Status = ePluginStatus.STOPPED_FAILED;
                        break;

                    case StartOutcome.LeaveStopped:
                        // A caller stopping a plugin that is still STARTING: it calls the retriever's
                        // StopAsync() and then sets the status. No further internal start work runs.
                        await StopAsync();
                        Status = ePluginStatus.STOPPED;
                        break;
                }
            }

            private static Task FailingVenueStartAsync()
            {
                throw new InvalidOperationException("Synthetic start failure (venue unreachable).");
            }

            /// <summary>
            /// BasePluginDataRetriever exposes no data-received event (the one in
            /// RaiseOnDataReceived(DataEventArgs) is commented out), so this protected virtual
            /// raise method IS the observation seam for provider status notifications.
            /// </summary>
            protected override void RaiseOnDataReceived(Provider heartBeatModel, bool overrideDisabiityFromOtherDataRetrievers = false)
            {
                if (heartBeatModel != null)
                {
                    _providerStatuses.Enqueue(heartBeatModel.Status);
                }
                base.RaiseOnDataReceived(heartBeatModel, overrideDisabiityFromOtherDataRetrievers);
            }

            // Faithful to production: the engine's StopAsync() step leaves STOPPED (base sets it) before
            // the retried StartAsync stamps STARTING again.
            public override Task StopAsync() => base.StopAsync();

            public override string Name { get; set; } = "ReconnectEngineFake";
            public override string Version { get; set; } = "1.0";
            public override string Description { get; set; } = "test fake";
            public override string Author { get; set; } = "test";
            public override ISetting Settings { get; set; }
            public override Action CloseSettingWindow { get; set; }

            protected override void LoadSettings() => Settings = new FakeSetting(_provider);
            protected override void SaveSettings() { }
            protected override void InitializeDefaultSettings() { }
            public override object GetUISettings() => null;
        }

        private sealed class FakeSetting : ISetting
        {
            public FakeSetting(Provider provider) => Provider = provider;
            public string Symbol { get; set; } = "TESTUSD";
            public Provider Provider { get; set; }
            public AggregationLevel AggregationLevel { get; set; }
        }
    }
}
