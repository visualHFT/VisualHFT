using System;
using System.Runtime.CompilerServices;
using System.Threading;
using VisualHFT;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.Pools;
using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.Studies.MarketResilience.Model;

namespace Studies.MarketResilience.Model
{
    public enum eMarketBias
    {
        Neutral,
        Bullish,
        Bearish
    }
    public class MarketResilienceCalculator : IDisposable
    {
        private const decimal SHOCK_THRESHOLD_SIGMA = 2m;           // 2-sigma outlier detection

        private int MAX_SHOCK_MS_TIME_OUT = 800;                    // Recovery window (ms) for each shock component, measured from the frame that started it.
        private bool disposed = false;
        private decimal? _lastMidPrice = 0;
        protected decimal? _lastBidPrice;
        protected decimal? _lastAskPrice;
        protected readonly object _syncLock = new object();

        protected RollingWindow<decimal> recentSpreads = new RollingWindow<decimal>(500);
        private RollingWindow<decimal> recentTradeSizes = new RollingWindow<decimal>(500);
        private RollingWindow<double> spreadRecoveryTimes = new RollingWindow<double>(500);
        private RollingWindow<double> depletionRecoveryTimes = new RollingWindow<double>(500);

        // Running sums for O(1) trade-size statistics (maintained via AddWithEviction)
        private decimal _tradeSizeSum;
        private decimal _tradeSizeSumSq;
        // Cached threshold components for lock-free IsLargeTrade (double = atomic reads on x64)
        private double _cachedTradeAvg;
        private double _cachedTradeStdDev;
        private PlugInSettings settings;


        // ---------- STATE ----------
        // Spread carried over from the previous update. IsLOBDepleted falls back to it when the book
        // it is given is locked or crossed and reports a non-positive spread. OnOrderBookUpdate returns
        // before that on such a book, so only a direct caller of IsLOBDepleted reaches the fallback.
        // Per-event baselines live in _activeDepth, not here.
        protected double _previousSpread = 0;
        protected eLOBSIDE _lastReportedDepletion = eLOBSIDE.NONE;


        // ---------- CONFIG ----------
        private const double EPS = 1e-9;
        private const int WARMUP_MIN_SAMPLES = 200;      // avoid cold-start noise
        private const double Z_K_DEPTH = 3.0;               // robust z-score threshold

        // ---------- ROLLING BASELINES (P² quantiles for robustness, O(1) space) ----------
        private readonly P2Quantile _qSpreadMed = new P2Quantile(0.5);
        private readonly P2Quantile _qBidDMed = new P2Quantile(0.5); // median immediacy depth (bid)
        private readonly P2Quantile _qAskDMed = new P2Quantile(0.5);
        private readonly P2Quantile _qBidDDevMed = new P2Quantile(0.5); // MAD for bid (median of |depth - median_depth|)
        private readonly P2Quantile _qAskDDevMed = new P2Quantile(0.5); // MAD for ask

        private int _samplesSpread = 0;
        private int _samplesDepth = 0;

        // ----- ACTIVE DEPTH EVENT STATE -----
        private ActiveDepthEvent? _activeDepth = null;

        // The touch of the PREVIOUS book update, carried forward so the frame that detects a
        // depletion can still see the book as it stood before it. Depth that comes back only at a
        // worse price is not a recovery, and the price it has to come back to is the last one
        // quoted while the side was still whole.
        private decimal? _prevBidPrice;
        private decimal? _prevAskPrice;

        private struct ActiveDepthEvent
        {
            public eLOBSIDE DepletedSide;      // which side(s) triggered depletion
            public eLOBSIDE RecoveredSides;    // depleted side(s) that have regained the target so far
            public double SBase;               // spread baseline at t0 (for normalization)

            // Baselines (at t0) and troughs (worst since t0) for each side
            public double DBaseBid, DBaseAsk;  // immediacy depth baseline per side
            public double DTroughBid, DTroughAsk;

            // The touch on the frame before the depletion was detected: the price each side has to
            // come back to. Null when no earlier frame was seen.
            public decimal? AnchorBid, AnchorAsk;
        }

        // ----- CONFIG -----
        private const double RECOVERY_TARGET = 0.90;            // 90% recovery ends the event early


        public MarketResilienceCalculator(PlugInSettings settings)
        {
            this.settings = settings;
            MAX_SHOCK_MS_TIME_OUT = settings.MaxShockMsTimeout ?? MAX_SHOCK_MS_TIME_OUT;
        }

        protected class TimestampedValue
        {
            public DateTime Timestamp { get; set; }
            public decimal Value { get; set; }
        }
        protected class TimestampedDepth
        {
            public DateTime Timestamp { get; set; }
            public eLOBSIDE Value { get; set; }
        }

        // An event is anchored by one large print. While the print is younger than the window it
        // lets a spread widening and a depth depletion start; once either has started, the print
        // stays attached until the event is scored, because trade severity reads it.
        protected TimestampedValue? ShockTrade { get; set; }

        protected TimestampedValue? ShockSpread { get; set; }     // spread widening: start stamp and shock spread
        protected TimestampedValue? ReturnedSpread { get; set; }  // set only when the spread returned inside its window
        protected bool SpreadWindowClosed { get; private set; }   // the window elapsed before the spread returned

        protected TimestampedDepth? ShockDepth { get; set; }      // depletion: start stamp and depleted side(s)
        protected TimestampedDepth? RecoveredDepth { get; set; }  // set only when every depleted side regained the target inside its window
        protected bool DepthWindowClosed { get; private set; }    // the window elapsed before every depleted side regained the target
        protected eLOBSIDE DepthSidesRecovered { get; private set; } // the depleted side(s) that regained the target in time

        protected bool? InitialHitHappenedAtBid { get; set; }      // whether the anchoring print hit the bid side

        public decimal CurrentMRScore { get; private set; } = 1m; // stable MR value by default
        public eMarketBias CurrentMarketBias { get; private set; } = eMarketBias.Neutral;

        // False until a scored event gives a bias: before that CurrentMarketBias is the default Neutral, not a reading.
        public bool HasBiasVerdict { get; private set; }
        public decimal MidMarketPrice => _lastMidPrice ?? 0;

        /// <summary>Book samples the depletion detector needs before it reports anything.</summary>
        public static int WarmUpSamplesRequired => WARMUP_MIN_SAMPLES;

        /// <summary>True once the depth baselines hold enough book samples to detect a depletion.</summary>
        public bool IsBaselineWarm => Volatile.Read(ref _samplesDepth) >= WARMUP_MIN_SAMPLES;

        /// <summary>Book samples consumed so far, capped at the number required.</summary>
        public int WarmUpProgress => Math.Min(Volatile.Read(ref _samplesDepth), WARMUP_MIN_SAMPLES);

        /// <summary>True while the latest book was crossed or locked: the score is the last one computed, held, not a reading.</summary>
        public bool IsLastBookCrossed => Volatile.Read(ref _lastBookCrossed);
        private bool _lastBookCrossed;

        /// <summary>True once at least one recovery has been measured, so a later one has something to be compared to.</summary>
        public bool HasRecoveryReference
        {
            get
            {
                lock (_syncLock)
                {
                    return depletionRecoveryTimes.Count > 0 || spreadRecoveryTimes.Count > 0;
                }
            }
        }

        /// <summary>
        /// Thread-safe snapshot of calculator output values.
        /// Reads all output fields under _syncLock to prevent torn reads.
        /// </summary>
        public (decimal mrScore, eMarketBias bias, decimal midPrice) GetOutputSnapshot()
        {
            lock (_syncLock)
            {
                return (CurrentMRScore, CurrentMarketBias, _lastMidPrice ?? 0);
            }
        }

        public void OnTrade(Trade trade)
        {
            // A print with no taker (an auction cross) is not a market order hitting the book: it anchors no shock and stays out
            // of the trade-size statistics.
            if (!trade.IsBuy.HasValue && HasNoAggressorToken(trade.Flags))
                return;

            lock (_syncLock)
            {
                var now = HelperTimeProvider.Now;
                ReleaseExpiredAnchor(now);

                // The threshold check is O(1) against cached running statistics, so it is taken
                // under the lock the very next statement needs anyway. Reading it outside meant the
                // mean and the dispersion could come from two different updates.
                bool isLarge = IsLargeTrade(trade.Size);

                if (ShockTrade == null && isLarge)
                {
                    ShockTrade = new TimestampedValue { Timestamp = now, Value = trade.Size };
                    //find out if the shock trade happened closer to bid or ask
                    if (_lastBidPrice.HasValue &&
                        _lastAskPrice.HasValue) //if we have latest bid/ask price we can infer where the trade happened
                    {
                        decimal midPrice = (_lastBidPrice.Value + _lastAskPrice.Value) / 2;
                        InitialHitHappenedAtBid = trade.Price <= midPrice;
                    }
                    else
                        InitialHitHappenedAtBid = false;
                }
                else
                {
                    if (recentTradeSizes.AddWithEviction(trade.Size, out decimal evicted))
                    {
                        _tradeSizeSum -= evicted;
                        _tradeSizeSumSq -= evicted * evicted;
                    }
                    _tradeSizeSum += trade.Size;
                    _tradeSizeSumSq += trade.Size * trade.Size;
                    UpdateCachedTradeStats();
                }
            }
        }
        // Whole tokens in Trade.Flags, a comma- or space-separated list. Span scan, no allocation.
        private const string NoAggressorToken = "no-aggressor";

        private static bool HasNoAggressorToken(string flags)
        {
            if (string.IsNullOrEmpty(flags))
                return false;

            ReadOnlySpan<char> rest = flags;
            while (!rest.IsEmpty)
            {
                int end = rest.IndexOfAny(',', ' ');
                ReadOnlySpan<char> token = end < 0 ? rest : rest.Slice(0, end);
                if (token.SequenceEqual(NoAggressorToken))
                    return true;

                rest = end < 0 ? ReadOnlySpan<char>.Empty : rest.Slice(end + 1);
            }

            return false;
        }

        public void OnOrderBookUpdate(OrderBookSnapshot orderBook)
        {
            lock (_syncLock)
            {
                // A crossed or locked book (a pre-open, a halt, or a feed out of sync) has no spread or mid a recovery can be
                // measured against.
                if (orderBook.Asks == null || orderBook.Bids == null
                    || orderBook.Asks.Length == 0 || orderBook.Bids.Length == 0)
                    return;
                if (orderBook.Spread <= 0)
                {
                    Volatile.Write(ref _lastBookCrossed, true);
                    return;
                }
                Volatile.Write(ref _lastBookCrossed, false);

                var now = HelperTimeProvider.Now;
                ReleaseExpiredAnchor(now);

                // The print is the attribution gate: a widening or a depletion is a trade event only
                // while a large print is anchored and no older than the window. Once a component
                // has started, the print's age no longer matters; each component runs its own window
                // from the frame that started it.
                bool anchored = ShockTrade != null && !HasElapsed(ShockTrade.Timestamp, now);

                // ═══════════════════════════════════════════════════════════════
                // SPREAD WIDENING/RETURN TRACKING
                // ═══════════════════════════════════════════════════════════════
                var currentSpread = (decimal)orderBook.Spread;

                if (ShockSpread == null)
                {
                    if (anchored && IsLargeWideningSpread(currentSpread))
                        ShockSpread = new TimestampedValue { Timestamp = now, Value = currentSpread };
                }
                else if (ReturnedSpread == null && !SpreadWindowClosed)
                {
                    // A frame after the deadline closes the window; it is never credited as a return.
                    if (HasElapsed(ShockSpread.Timestamp, now))
                        SpreadWindowClosed = true;
                    else if (HasSpreadReturnedToMean(currentSpread))
                        ReturnedSpread = new TimestampedValue { Value = currentSpread, Timestamp = now };
                }

                recentSpreads.Add(currentSpread);
                _lastMidPrice = (decimal?)orderBook.MidPrice;

                // Carry the outgoing touch forward before it is overwritten. The depletion below is
                // detected on the frame that already shows the damage, so the price the side has to
                // come back to is the one quoted on the frame before it.
                _prevBidPrice = _lastBidPrice;
                _prevAskPrice = _lastAskPrice;
                _lastBidPrice = (decimal?)orderBook.Bids[0]?.Price;
                _lastAskPrice = (decimal?)orderBook.Asks[0]?.Price;

                // ═══════════════════════════════════════════════════════════════
                // DEPTH DEPLETION/RECOVERY TRACKING
                // ═══════════════════════════════════════════════════════════════
                var depletedState = IsLOBDepleted(orderBook);

                if (ShockDepth == null)
                {
                    if (anchored && depletedState != eLOBSIDE.NONE)
                    {
                        ShockDepth = new TimestampedDepth { Timestamp = now, Value = depletedState };
                        ActivateDepthEvent(orderBook, depletedState);
                    }
                }
                else if (RecoveredDepth == null && !DepthWindowClosed)
                {
                    if (HasElapsed(ShockDepth.Timestamp, now))
                    {
                        // The window closed before every depleted side came back. Whatever this late
                        // frame shows is not credited; the sides that did make it in time are kept
                        // so the direction can be attributed.
                        DepthWindowClosed = true;
                        DepthSidesRecovered = _activeDepth.HasValue ? _activeDepth.Value.RecoveredSides : eLOBSIDE.NONE;
                        _activeDepth = null;
                    }
                    else if (IsLOBRecovered(orderBook) != eLOBSIDE.NONE)
                    {
                        RecoveredDepth = new TimestampedDepth { Timestamp = now, Value = ShockDepth.Value };
                        DepthSidesRecovered = ShockDepth.Value;
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // SCORE THE EVENT ONCE EVERY STARTED COMPONENT HAS CONCLUDED
                // ═══════════════════════════════════════════════════════════════
                CheckAndCalculateIfShock();
            }
        }

        /// <summary>
        /// An anchor older than the window that started nothing is released, so the next large
        /// print can anchor. An anchor with a live component is kept until the event is scored.
        /// </summary>
        private void ReleaseExpiredAnchor(DateTime now)
        {
            if (ShockTrade != null && ShockSpread == null && ShockDepth == null && HasElapsed(ShockTrade.Timestamp, now))
            {
                ShockTrade = null;
                InitialHitHappenedAtBid = null;
            }
        }

        private bool HasElapsed(DateTime start, DateTime now)
        {
            return now.Subtract(start).TotalMilliseconds > MAX_SHOCK_MS_TIME_OUT;
        }

        private void CheckAndCalculateIfShock()
        {
            // The event is scored once, when every component that started has either recovered or
            // run out its window. A component that recovered early does not score the event on its
            // own: a depth failure still in progress would otherwise be dropped unscored.
            bool spreadStarted = ShockSpread != null;
            bool depthStarted = ShockDepth != null;
            if (!spreadStarted && !depthStarted)
                return;

            bool spreadConcluded = !spreadStarted || ReturnedSpread != null || SpreadWindowClosed;
            bool depthConcluded = !depthStarted || RecoveredDepth != null || DepthWindowClosed;

            if (spreadConcluded && depthConcluded)
            {
                TriggerMRCalculation();
                Reset();
            }
        }


        private bool IsLargeTrade(decimal tradeSize)
        {
            // O(1) check using cached running statistics (safe to call outside lock)
            return recentTradeSizes.Count >= 3
                && (double)tradeSize > _cachedTradeAvg + (double)SHOCK_THRESHOLD_SIGMA * _cachedTradeStdDev;
        }

        private void UpdateCachedTradeStats()
        {
            int count = recentTradeSizes.Count;
            if (count < 3)
            {
                _cachedTradeAvg = 0;
                _cachedTradeStdDev = 0;
                return;
            }
            decimal avg = _tradeSizeSum / count;
            decimal variance = (_tradeSizeSumSq / count) - avg * avg;
            _cachedTradeAvg = (double)avg;
            // This subtraction of two nearly equal quantities can leave a tiny negative residue on a
            // near-constant window, so a non-positive variance is reported as zero dispersion. The
            // O(1) form is kept here because it only feeds a threshold heuristic and runs on every
            // trade; the score itself recomputes dispersion the two-pass way, once per shock.
            _cachedTradeStdDev = variance > 0 ? Math.Sqrt((double)variance) : 0;
        }

        private bool IsLargeWideningSpread(decimal spreadValue)
        {
            decimal avgSize = recentSpreads.Average();
            decimal stdSize = recentSpreads.StandardDeviation();
            if (recentSpreads.Count < 3) return false; //not enough data
            return spreadValue > avgSize + SHOCK_THRESHOLD_SIGMA * stdSize;
        }

        private bool HasSpreadReturnedToMean(decimal spreadValue)
        {
            decimal avgSize = recentSpreads.Average();
            return spreadValue < avgSize;
        }

        private void TriggerMRCalculation()
        {
            // ═══════════════════════════════════════════════════════════════
            // WEIGHTED RESILIENCE CALCULATION
            // ═══════════════════════════════════════════════════════════════
            // Four components: trade severity 30%, spread recovery 10%, depth recovery 50%, spread
            // magnitude 10%. A component with no usable evidence is omitted and the rest are
            // reweighted. A component whose window closed without a recovery is NOT omitted: it
            // scores 0 at its full weight, and its duration is not written to the history.
            // ═══════════════════════════════════════════════════════════════

            double totalWeight = 0.0;
            double weightedScore = 0.0;

            // A recovery component with an empty history has nothing to be compared to. It seeds
            // the history and is omitted. If no recovery component contributed at all, nothing is
            // published: trade severity and magnitude are never published on their own.
            bool hasRecoveryOutcome = false;

            // ───────────────────────────────────────────────────────────────
            // COMPONENT 0: TRADE SHOCK SEVERITY (30% weight)
            // ───────────────────────────────────────────────────────────────
            const double W_TRADE = 0.3;

            // Dispersion must be measurable RELATIVE to the mean for a z-score to mean anything.
            // The factor is dimensionless on purpose: it carries no unit, no tick size and no lot
            // size, so it reads the same for a fraction of a coin and for a hundred shares.
            const decimal REL_EPS = 1e-6m;

            if (ShockTrade != null && recentTradeSizes.Count > 0)
            {
                // Dispersion from squared deviations rather than from a sum of squares minus the
                // mean squared: deviations cannot cancel, so the result is never negative and is
                // exactly zero on a constant window. The mean comes from the running sum, which is
                // an exact decimal total of the window, so ONE pass over the window is enough.
                // The deviations are accumulated in double because this sits on the market-data
                // callback and decimal arithmetic over a 500-item window costs more than the rest
                // of the callback put together; squaring a deviation removes the cancellation that
                // made the precision matter in the first place. Runs once per completed shock,
                // never per trade.
                int count = recentTradeSizes.Count;
                decimal avgSize = _tradeSizeSum / count;
                double mean = (double)avgSize;
                double sumSquaredDeviations = 0.0;
                foreach (decimal size in recentTradeSizes.Items)
                {
                    double deviation = (double)size - mean;
                    sumSquaredDeviations += deviation * deviation;
                }
                decimal stdSize = (decimal)Math.Sqrt(sumSquaredDeviations / count);

                // No scale to measure against, or a dispersion too small relative to that scale, and
                // the z-score carries no information about the shock print. Leave the component out
                // of the weighting entirely rather than publish a fabricated value at 30% weight.
                if (avgSize > 0 && stdSize >= REL_EPS * avgSize)
                {
                    // Z-score of trade size (how many std devs above mean)
                    double tradeZ = (double)((ShockTrade.Value - avgSize) / stdSize);

                    // Convert to resilience score (0..1)
                    // z=3 → score=0.5, z=6 → score=0
                    double tradeScore = Math.Clamp(1.0 - (tradeZ / 6.0), 0.0, 1.0);

                    weightedScore += W_TRADE * tradeScore;
                    totalWeight += W_TRADE;
                }
            }

            // ───────────────────────────────────────────────────────────────
            // COMPONENT 1: SPREAD RECOVERY (10% weight)
            // ───────────────────────────────────────────────────────────────
            const double W_SPREAD = 0.1;

            if (ShockSpread != null)
            {
                if (ReturnedSpread != null)
                {
                    double spreadRecoveryDurationMs = Math.Abs((ReturnedSpread.Timestamp - ShockSpread.Timestamp).TotalMilliseconds);

                    if (spreadRecoveryTimes.Count > 0)
                    {
                        double avgSpreadHistoricalRecoveryMs = spreadRecoveryTimes.Average();
                        double spreadRecoveryScore = Math.Clamp(
                            avgSpreadHistoricalRecoveryMs / (avgSpreadHistoricalRecoveryMs + spreadRecoveryDurationMs), 0.0, 1.0);

                        weightedScore += W_SPREAD * spreadRecoveryScore;
                        totalWeight += W_SPREAD;
                        hasRecoveryOutcome = true;
                    }

                    // Only a measured recovery joins the history. A zero sample would pull the
                    // historical baseline down, and that baseline is the numerator above, so every
                    // later genuine recovery would score lower for the rest of the session.
                    if (spreadRecoveryDurationMs > 0.0)
                        spreadRecoveryTimes.Add(spreadRecoveryDurationMs);
                }
                else
                {
                    // The spread never came back inside the window.
                    totalWeight += W_SPREAD;
                    hasRecoveryOutcome = true;
                }
            }

            // ───────────────────────────────────────────────────────────────
            // COMPONENT 2: DEPTH RECOVERY (50% weight)
            // ───────────────────────────────────────────────────────────────
            const double W_DEPTH = 0.5;

            if (ShockDepth != null)
            {
                if (RecoveredDepth != null)
                {
                    double depletionRecoveryDurationMs = Math.Abs((RecoveredDepth.Timestamp - ShockDepth.Timestamp).TotalMilliseconds);

                    if (depletionRecoveryTimes.Count > 0)
                    {
                        double avgDepletionHistoricalRecoveryMs = depletionRecoveryTimes.Average();
                        double depletionRecoveryScore = Math.Clamp(
                            avgDepletionHistoricalRecoveryMs / (avgDepletionHistoricalRecoveryMs + depletionRecoveryDurationMs), 0.0, 1.0);

                        weightedScore += W_DEPTH * depletionRecoveryScore;
                        totalWeight += W_DEPTH;
                        hasRecoveryOutcome = true;
                    }

                    if (depletionRecoveryDurationMs > 0.0)
                        depletionRecoveryTimes.Add(depletionRecoveryDurationMs);
                }
                else
                {
                    // The depleted side never regained the target inside the window. This is the
                    // worst reading the book can give and it counts at full weight.
                    totalWeight += W_DEPTH;
                    hasRecoveryOutcome = true;
                }
            }

            // ───────────────────────────────────────────────────────────────
            // COMPONENT 3: SPREAD SHOCK MAGNITUDE (10% weight)
            // ───────────────────────────────────────────────────────────────
            const double W_MAGNITUDE = 0.10;

            if (ShockSpread != null && recentSpreads.Count > 0)
            {
                // Ratio of the usual spread to the shock spread. There is no absolute floor on the
                // spread: prices are quoted at whatever scale the instrument uses, and a fixed
                // constant would decide the component on a pair quoting at 1e-8.
                decimal avgHistoricalSpread = recentSpreads.Average();
                if (avgHistoricalSpread > 0 && ShockSpread.Value > 0)
                {
                    double magnitudeRatio = (double)(ShockSpread.Value / avgHistoricalSpread);
                    double magnitudeScore = 1.0 / magnitudeRatio;
                    magnitudeScore = Math.Max(0, Math.Min(1, magnitudeScore));

                    weightedScore += W_MAGNITUDE * magnitudeScore;
                    totalWeight += W_MAGNITUDE;
                }
            }

            // ───────────────────────────────────────────────────────────────
            // FINAL SCORE NORMALIZATION
            // ───────────────────────────────────────────────────────────────
            // The published score is the weighted average over the components that actually had
            // usable evidence. The finiteness check is what keeps the cast safe, because a
            // non-finite quotient survives a clamp untouched and then throws on conversion.
            bool publish = hasRecoveryOutcome && totalWeight > 0;
            if (publish)
            {
                double normalizedScore = weightedScore / totalWeight;
                if (double.IsFinite(normalizedScore))
                    CurrentMRScore = (decimal)Math.Clamp(normalizedScore, 0.0, 1.0);
                else
                    publish = false;
            }

            // ───────────────────────────────────────────────────────────────
            // MARKET BIAS DETERMINATION
            // ───────────────────────────────────────────────────────────────
            // The bias step runs at the end of every scoring pass, whether or not the pass
            // publishes: it is the one hook a subclass has to observe that an event was evaluated.
            // Its result is committed only alongside a published score, so a withheld event leaves
            // both outputs exactly as they were.
            eMarketBias? bias = CalculateMRBias();
            if (publish && bias.HasValue)
            {
                CurrentMarketBias = bias.Value;
                HasBiasVerdict = true;
            }
        }

        //DEPLETION FUNCTIONALITY USAGE:
        /*
            * Call `IsLOBDepleted(lob)` on **every** book update.

              * If it returns `NONE`, do nothing.
              * If it returns `BID`, `ASK`, or `BOTH` **and** there’s no active depth event, call `ActivateDepthEvent(lob, side)` once to start tracking recovery.
              * If it returns a side **while an event is already active**, it is ignored.

            * After an event is activated, call `IsLOBRecovered(lob)` on **every** in-window book update.

              * It returns `NONE` until every depleted side has regained the recovery target.
              * On that tick it returns the depleted side(s) and clears the active event (edge-triggered).
              * Only the depleted side(s) are evaluated. The untouched side is not a recovery.
              * The window is the caller's: a frame after the deadline closes the event without calling it.

            * Warm-up: allow the quantile baselines to collect enough samples before acting (the implementation already guards with `WARMUP_MIN_SAMPLES`).

            * Multiple-side cases: if `IsLOBDepleted` returns `BOTH`, pass `BOTH` into `ActivateDepthEvent`. `IsLOBRecovered` returns `BOTH` only once both sides have met the criterion.
         */
        internal eLOBSIDE IsLOBDepleted(in OrderBookSnapshot lob)
        {
            /*
                Notes & rationale

                Why immediacy-weighted depth?
                It’s invariant to rank churn. If inner levels vanish and outer size bubbles up, raw sums can look unchanged; the immediacy metric will drop because deeper size carries lower weight.

                Why robust z instead of fixed thresholds?
                median/MAD adapts per venue and regime with zero setup. Z_K_DEPTH = 3 is a sensible default; you can even adapt K online if you want.

                Warm-up guard:
                Prevents noisy triggers during the first few hundred updates or in ultra-thin starts.

                Spread normalization:
                Distances in spread units make it market-agnostic. If baseline isn’t ready, we fallback to current spread (guarded by EPS).

                Edge-triggered behavior:
                It returns a non-NONE only when a new depletion crosses the line on this call. This keeps downstream code simple (no extra debouncing).

                No allocations:
                Pure loops, P² quantiles keep constant space, no LINQ.             */


            // 1) Update SPREAD baseline first (used to normalize distances)
            double spreadNow = lob.Spread > 0 ? lob.Spread : _previousSpread;
            if (spreadNow > 0)
            {
                _qSpreadMed.Observe(spreadNow);
                _samplesSpread++;
            }
            double spreadBase = _samplesSpread >= WARMUP_MIN_SAMPLES
                ? _qSpreadMed.Estimate
                : (spreadNow > 0 ? spreadNow : 1.0);

            // 2) Compute current immediacy-weighted depth per side
            double dBidNow = ImmediacyDepthBid(lob, spreadBase);
            double dAskNow = ImmediacyDepthAsk(lob, spreadBase);

            // 3) Update depth baselines (medians for center)
            _qBidDMed.Observe(dBidNow);
            _qAskDMed.Observe(dAskNow);

            double bidMed = _qBidDMed.Estimate;
            double askMed = _qAskDMed.Estimate;

            // Track absolute deviations for TRUE MAD (only after P² initializes at n=5)
            if (_samplesDepth >= 5)
            {
                _qBidDDevMed.Observe(Math.Abs(dBidNow - bidMed));
                _qAskDDevMed.Observe(Math.Abs(dAskNow - askMed));
            }

            _samplesDepth++;

            // If we don't have enough samples yet, just advance state and exit
            if (_samplesDepth < WARMUP_MIN_SAMPLES)
            {
                _previousSpread = lob.Spread;
                return eLOBSIDE.NONE;
            }

            // 4) Use TRUE MAD instead of P90 approximation
            double bidMAD = Math.Max(_qBidDDevMed.Estimate, EPS); // TRUE MAD
            double bidZDrop = (bidMed - dBidNow) / bidMAD;

            double askMAD = Math.Max(_qAskDDevMed.Estimate, EPS); // TRUE MAD
            double askZDrop = (askMed - dAskNow) / askMAD;

            // 5) Decide sides depleted this tick (edge-triggered)
            eLOBSIDE depleted = eLOBSIDE.NONE;

            if (bidZDrop >= Z_K_DEPTH && dBidNow < bidMed) // ensure it's actually below baseline
            {
                depleted |= eLOBSIDE.BID;
            }
            if (askZDrop >= Z_K_DEPTH && dAskNow < askMed)
            {
                depleted |= eLOBSIDE.ASK;
            }

            // EDGE-TRIGGER LOGIC: Only report NEW depletions
            eLOBSIDE newDepletion = depleted & ~_lastReportedDepletion;

            // Update last reported state
            if (depleted == eLOBSIDE.NONE)
            {
                // Depletion cleared - reset tracking
                _lastReportedDepletion = eLOBSIDE.NONE;
            }
            else if (newDepletion != eLOBSIDE.NONE)
            {
                // New depletion detected - mark as reported
                _lastReportedDepletion = depleted;
            }

            // 6) Carry this book's spread forward for the locked-book fallback in step 1, which only a direct
            //    caller reaches: OnOrderBookUpdate skips a locked or crossed book before calling this.
            _previousSpread = lob.Spread;
            return newDepletion;
        }
        internal void ActivateDepthEvent(in OrderBookSnapshot lob, eLOBSIDE side)
        {
            // Baselines at t0: use current robust medians if available, else current values.
            // The cold-start fallback matches IsLOBDepleted's: this book's own spread when it has
            // one, and only then the unit of last resort. Flooring at 1.0 regardless measured the
            // event on a different scale than the detector that admitted it, and on an instrument
            // quoting below 1.0 that unit is larger than the whole price.
            double spreadBase = _samplesSpread >= WARMUP_MIN_SAMPLES
                ? _qSpreadMed.Estimate
                : (lob.Spread > 0 ? lob.Spread : 1.0);

            double dBidNow = ImmediacyDepthBid(lob, spreadBase);
            double dAskNow = ImmediacyDepthAsk(lob, spreadBase);

            _activeDepth = new ActiveDepthEvent
            {
                DepletedSide = side,
                RecoveredSides = eLOBSIDE.NONE,
                SBase = spreadBase,
                DBaseBid = (_samplesDepth >= WARMUP_MIN_SAMPLES ? _qBidDMed.Estimate : dBidNow),
                DBaseAsk = (_samplesDepth >= WARMUP_MIN_SAMPLES ? _qAskDMed.Estimate : dAskNow),
                DTroughBid = dBidNow,  // initialize troughs at current, will update downward
                DTroughAsk = dAskNow,
                AnchorBid = _prevBidPrice,
                AnchorAsk = _prevAskPrice
            };
        }

        /// <summary>
        /// Advances the active depth event with one in-window frame. Returns the depleted side(s)
        /// on the tick every one of them has climbed back to the recovery target from its trough,
        /// clearing the event; NONE otherwise. Only the depleted side(s) are measured: the other
        /// side's baseline and trough coincide at activation, so any change there would read as a
        /// recovery of nothing.
        /// </summary>
        internal eLOBSIDE IsLOBRecovered(in OrderBookSnapshot lob)
        {
            // No active event → nothing to recover
            if (_activeDepth == null)
                return eLOBSIDE.NONE;

            var ev = _activeDepth.Value;

            // Normalize distances by spread baseline captured at t0
            double spreadBase = ev.SBase > EPS ? ev.SBase : Math.Max(lob.Spread, 1.0);

            if ((ev.DepletedSide & eLOBSIDE.BID) != 0)
            {
                double dBidNow = ImmediacyDepthBid(lob, spreadBase);
                if (dBidNow < ev.DTroughBid) ev.DTroughBid = dBidNow;

                // Recovery is how far the side has climbed from its trough toward its baseline,
                // and it only counts if it happened at a price the pre-event book would recognise.
                double denomBid = Math.Max(ev.DBaseBid - ev.DTroughBid, EPS);
                if (Clamp01((dBidNow - ev.DTroughBid) / denomBid) >= RECOVERY_TARGET
                    && IsTouchWithinAnchor(BestPrice(lob.Bids), ev.AnchorBid, ev.SBase, higherIsBetter: true))
                    ev.RecoveredSides |= eLOBSIDE.BID;
            }

            if ((ev.DepletedSide & eLOBSIDE.ASK) != 0)
            {
                double dAskNow = ImmediacyDepthAsk(lob, spreadBase);
                if (dAskNow < ev.DTroughAsk) ev.DTroughAsk = dAskNow;

                double denomAsk = Math.Max(ev.DBaseAsk - ev.DTroughAsk, EPS);
                if (Clamp01((dAskNow - ev.DTroughAsk) / denomAsk) >= RECOVERY_TARGET
                    && IsTouchWithinAnchor(BestPrice(lob.Asks), ev.AnchorAsk, ev.SBase, higherIsBetter: false))
                    ev.RecoveredSides |= eLOBSIDE.ASK;
            }

            if (ev.RecoveredSides == ev.DepletedSide)
            {
                _activeDepth = null;
                return ev.DepletedSide;     // edge-triggered: non-NONE only on the tick the event completes
            }

            // Still recovering; keep the updated troughs and continue
            _activeDepth = ev;
            return eLOBSIDE.NONE;
        }


        /// <summary>Best price of a side, or null when the side is empty or unpriced.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double? BestPrice(ReadOnlySpan<BookItem> levels)
        {
            if (levels.Length == 0 || levels[0] == null)
                return null;
            return levels[0].Price;
        }

        /// <summary>
        /// Whether a side's touch has come back to the price it was quoted at before the event.
        /// Size returning at a worse price is not resilience: the depth is there, the price that was
        /// quoted is gone. The literature measures recovery at the pre-event best, and the arrow
        /// reads a side that failed to redeploy as the direction the market is likely to take.
        ///
        /// The tolerance is one spread baseline, inclusive, taken from the event itself. It is the
        /// instrument's own typical distance, so it carries no tick size and no price scale: routine
        /// requoting inside the spread still counts as a recovery, and a touch that moved further
        /// than the instrument's own spread does not.
        ///
        /// Two cases skip the test and credit the recovery: no anchor (the print arrived before any
        /// book) and no measurable spread baseline. Both mean the comparison cannot be made, and
        /// staying silent is the safe direction for a tile that only speaks about failures.
        /// </summary>
        private static bool IsTouchWithinAnchor(double? touch, decimal? anchor, double tolerance, bool higherIsBetter)
        {
            if (anchor == null || tolerance <= EPS)
                return true;
            if (touch == null)
                return false;

            double limit = (double)anchor.Value;
            return higherIsBetter
                ? touch.Value >= limit - tolerance
                : touch.Value <= limit + tolerance;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double InvSquareWeight(double d) // w = 1 / (1 + d)^2
        {
            var x = 1.0 + (d < 0 ? 0 : d);
            return 1.0 / (x * x);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);


        // Immediacy-weighted depth for one side using distances in SPREAD units.
        // Uses top-N that exist in the snapshot; rank churn can't fake recovery.
        private static double ImmediacyDepthBid(in OrderBookSnapshot lob, double spreadBase)
        {
            if (spreadBase <= EPS) spreadBase = Math.Max(lob.Spread, 1.0); // guard
            if (lob.Bids.Length == 0) return 0; // empty side → zero immediacy

            var levels = lob.Bids; // assume best-first ordering

            // CRITICAL: Null check for pooled arrays - first level must exist
            if (levels[0] == null || !levels[0].Price.HasValue) return 0;

            double best = levels[0].Price.Value;
            double acc = 0.0;

            int n = levels.Length;
            for (int i = 0; i < n; i++)
            {
                var level = levels[i];

                // CRITICAL: Null check before property access (pooled arrays can contain nulls)
                if (level == null || !level.Price.HasValue || !level.Size.HasValue)
                    continue;

                double d = (best - level.Price.Value) / spreadBase; // ≥ 0
                double w = InvSquareWeight(d);
                acc += level.Size.Value * w;
            }
            return acc;
        }

        private static double ImmediacyDepthAsk(in OrderBookSnapshot lob, double spreadBase)
        {
            if (spreadBase <= EPS) spreadBase = Math.Max(lob.Spread, 1.0);
            if (lob.Asks.Length == 0) return 0; // empty side → zero immediacy

            var levels = lob.Asks;

            // CRITICAL: Null check for pooled arrays - first level must exist
            if (levels[0] == null || !levels[0].Price.HasValue) return 0;

            double best = levels[0].Price.Value;
            double acc = 0.0;

            int n = levels.Length;
            for (int i = 0; i < n; i++)
            {
                var level = levels[i];

                // CRITICAL: Null check before property access (pooled arrays can contain nulls)
                if (level == null || !level.Price.HasValue || !level.Size.HasValue)
                    continue;

                double d = (level.Price.Value - best) / spreadBase; // ≥ 0
                double w = InvSquareWeight(d);
                acc += level.Size.Value * w;
            }
            return acc;
        }



        protected virtual eMarketBias? CalculateMRBias()
        {
            return null;
        }

        private void Reset()
        {
            lock (_syncLock)
            {
                ShockSpread = null;
                ReturnedSpread = null;
                SpreadWindowClosed = false;
                ShockTrade = null;
                ShockDepth = null;
                RecoveredDepth = null;
                DepthWindowClosed = false;
                DepthSidesRecovered = eLOBSIDE.NONE;
                InitialHitHappenedAtBid = null;
                _activeDepth = null;
                // NOTE: _lastMidPrice / _lastBidPrice / _lastAskPrice are intentionally kept. They
                // cache the most recently observed book, not event state. Clearing them here made
                // every scored event publish a mid price of zero, and left the next trade with no
                // bid/ask to locate itself against.
                // NOTE: _previousSpread intentionally kept — IsLOBDepleted's locked-book spread fallback
                // reads it across cycles (only for a direct caller: OnOrderBookUpdate skips such a book).
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            // This calculator holds no unmanaged or poolable resources. It carries forward a
            // spread as a plain number and keeps per-event state in value types, so there is
            // nothing to release here. The method stays so subclasses and callers keep a
            // disposal contract.
            disposed = true;
        }

        ~MarketResilienceCalculator()
        {
            Dispose(false);
        }
    }
}
