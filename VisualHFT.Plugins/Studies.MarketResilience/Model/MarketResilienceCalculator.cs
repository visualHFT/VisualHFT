using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
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

        private int MAX_SHOCK_MS_TIME_OUT = 800;                    // Max time in ms to wait for shock events (trade, spread, depth) to happen.
        private bool disposed = false;
        private decimal? _lastMidPrice = 0;
        protected decimal? _lastBidPrice;
        protected decimal? _lastAskPrice;
        protected decimal? _bidAtHit;
        protected decimal? _askAtHit;
        protected readonly object _syncLock = new object();

        protected RollingWindow<decimal> recentSpreads = new RollingWindow<decimal>(500);
        private RollingWindow<decimal> recentTradeSizes = new RollingWindow<decimal>(500);
        private RollingWindow<double> spreadRecoveryTimes = new RollingWindow<double>(500);
        private RollingWindow<double> depletionRecoveryTimes = new RollingWindow<double>(500);
        private PlugInSettings settings;


        // ---------- STATE (you already asked for this name) ----------
        protected OrderBookSnapshot? _previousLOB = null;
        protected eLOBSIDE _lastReportedDepletion = eLOBSIDE.NONE;


        // ---------- CONFIG (tweak if you like) ----------
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

        private struct ActiveDepthEvent
        {
            public long T0Ticks;
            public long TmaxTicks;

            public eLOBSIDE DepletedSide;      // which side triggered depletion
            public double SBase;               // spread baseline at t0 (for normalization)

            // Baselines (at t0) and troughs (worst since t0) for each side
            public double DBaseBid, DBaseAsk;  // immediacy depth baseline per side
            public double DTroughBid, DTroughAsk;
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

        protected TimestampedValue? ShockTrade { get; set; }      // holds the shock trade
        protected TimestampedValue? ShockSpread { get; set; }     // holds the shock spread
        protected TimestampedValue? ReturnedSpread { get; set; }  // holds the last spread value, which will be used to calculate the MR score when gets back to normal values.
        protected TimestampedDepth? ShockDepth { get; set; }     // holds the depleted depth
        protected TimestampedDepth? RecoveredDepth { get; set; }     // recovered from the depltion

        protected bool? InitialHitHappenedAtBid { get; set; }      // holds the information about the shock trade happened at bid or ask

        public decimal CurrentMRScore { get; private set; } = 1m; // stable MR value by default
        public eMarketBias CurrentMarketBias { get; private set; } = eMarketBias.Neutral;
        public decimal MidMarketPrice => _lastMidPrice ?? 0;

        public void OnTrade(Trade trade)
        {
            lock (_syncLock)
            {
                if (ShockTrade == null
                    && IsLargeTrade(trade.Size))
                {
                    ShockTrade = new TimestampedValue
                    {
                        Timestamp = HelperTimeProvider.Now,
                        Value = trade.Size
                    };
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
                    recentTradeSizes.Add(trade.Size);
                }
                CheckAndCalculateIfShock();
            }
        }
        public void OnOrderBookUpdate(OrderBookSnapshot orderBook)
        {
            lock (_syncLock)
            {
                if (orderBook.Asks == null || orderBook.Bids == null
                    || orderBook.Asks.Length == 0 || orderBook.Bids.Length == 0)
                    return;

                // ═══════════════════════════════════════════════════════════════
                // CHECK TRADE SHOCK TIMEOUT FIRST
                // ═══════════════════════════════════════════════════════════════
                // ✅ FIX: Check trade timeout BEFORE processing any new shocks
                // This prevents accepting new shocks when the trade anchor is missing
                if (ShockTrade != null &&
                    HelperTimeProvider.Now.Subtract(ShockTrade.Timestamp).TotalMilliseconds > MAX_SHOCK_MS_TIME_OUT)
                {
                    // Trade anchor has expired - clear ALL shock states
                    ShockTrade = null;

                    // Also clear any partial shock states that were waiting for the trade
                    _bidAtHit = null;
                    _askAtHit = null;
                    ShockSpread = null;
                    ReturnedSpread = null;
                    _activeDepth = null;
                    ShockDepth = null;
                    RecoveredDepth = null;

                    // Don't process new shocks in this update
                    // Wait for a new trade shock to anchor the next calculation
                    recentSpreads.Add((decimal)orderBook.Spread);
                    _lastMidPrice = (decimal?)orderBook.MidPrice;
                    _lastBidPrice = (decimal?)orderBook.Bids[0]?.Price;
                    _lastAskPrice = (decimal?)orderBook.Asks[0]?.Price;
                    return;
                }

                // ═══════════════════════════════════════════════════════════════
                // SPREAD WIDENING/RETURN TRACKING
                // ═══════════════════════════════════════════════════════════════
                var currentSpread = (decimal)orderBook.Spread;

                if (ShockSpread == null && IsLargeWideningSpread(currentSpread))
                {
                    // NEW SHOCK: Only accept if we have a trade anchor
                    if (ShockTrade != null)
                    {
                        ShockSpread ??= new TimestampedValue();
                        ShockSpread.Timestamp = HelperTimeProvider.Now;
                        ShockSpread.Value = currentSpread;

                        _bidAtHit = _lastBidPrice;
                        _askAtHit = _lastAskPrice;
                    }
                    // else: No trade anchor - ignore spread shock
                }
                else if (ShockSpread != null && ReturnedSpread == null)
                {
                    // ✅ Check timeout BEFORE setting recovery
                    if (HelperTimeProvider.Now.Subtract(ShockSpread.Timestamp).TotalMilliseconds > MAX_SHOCK_MS_TIME_OUT)
                    {
                        // Timeout expired - clear state
                        _bidAtHit = null;
                        _askAtHit = null;
                        ShockSpread = null;
                    }
                    else
                    {
                        // Within timeout - check recovery
                        var hasSpreadReturned = HasSpreadReturnedToMean(currentSpread);
                        if (hasSpreadReturned)
                        {
                            ReturnedSpread ??= new TimestampedValue();
                            ReturnedSpread.Value = currentSpread;
                            ReturnedSpread.Timestamp = HelperTimeProvider.Now;
                        }
                    }
                }
                else if (ShockSpread != null && ReturnedSpread != null)
                {
                    // Monitor timeout for completed recovery
                    if (HelperTimeProvider.Now.Subtract(ShockSpread.Timestamp).TotalMilliseconds > MAX_SHOCK_MS_TIME_OUT ||
                        Math.Abs(ReturnedSpread.Timestamp.Subtract(ShockSpread.Timestamp).TotalMilliseconds) > MAX_SHOCK_MS_TIME_OUT)
                    {
                        _bidAtHit = null;
                        _askAtHit = null;
                        ShockSpread = null;
                        ReturnedSpread = null;
                    }
                }

                recentSpreads.Add(currentSpread);
                _lastMidPrice = (decimal?)orderBook.MidPrice;
                _lastBidPrice = (decimal?)orderBook.Bids[0]?.Price;
                _lastAskPrice = (decimal?)orderBook.Asks[0]?.Price;

                // ═══════════════════════════════════════════════════════════════
                // DEPTH DEPLETION/RECOVERY TRACKING
                // ═══════════════════════════════════════════════════════════════
                var depletedState = IsLOBDepleted(orderBook);

                if (ShockDepth == null && depletedState != eLOBSIDE.NONE)
                {
                    // NEW SHOCK: Only accept if we have a trade anchor
                    if (ShockTrade != null)
                    {
                        ShockDepth ??= new TimestampedDepth();
                        ShockDepth.Timestamp = HelperTimeProvider.Now;
                        ShockDepth.Value = depletedState;

                        ActivateDepthEvent(orderBook, ShockDepth.Value);
                    }
                    // else: No trade anchor - ignore depth shock
                }
                else if (ShockDepth != null && RecoveredDepth == null)
                {
                    // ✅ Check timeout BEFORE setting recovery
                    if (HelperTimeProvider.Now.Subtract(ShockDepth.Timestamp).TotalMilliseconds > MAX_SHOCK_MS_TIME_OUT)
                    {
                        // Timeout expired - clear state
                        _activeDepth = null;
                        ShockDepth = null;
                    }
                    else
                    {
                        // Within timeout - check recovery
                        var recoveredState = IsLOBRecovered(orderBook);
                        if (recoveredState != eLOBSIDE.NONE)
                        {
                            RecoveredDepth ??= new TimestampedDepth();
                            RecoveredDepth.Timestamp = HelperTimeProvider.Now;
                            RecoveredDepth.Value = recoveredState;
                        }
                    }
                }
                else if (ShockDepth != null && RecoveredDepth != null)
                {
                    // Monitor timeout for completed recovery
                    if (HelperTimeProvider.Now.Subtract(ShockDepth.Timestamp).TotalMilliseconds > MAX_SHOCK_MS_TIME_OUT ||
                        Math.Abs(RecoveredDepth.Timestamp.Subtract(ShockDepth.Timestamp).TotalMilliseconds) > MAX_SHOCK_MS_TIME_OUT)
                    {
                        _activeDepth = null;
                        ShockDepth = null;
                        RecoveredDepth = null;
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // TRIGGER MR CALCULATION IF SHOCKS COMPLETED
                // ═══════════════════════════════════════════════════════════════
                CheckAndCalculateIfShock();
            }
        }
        private void CheckAndCalculateIfShock()
        {
            // ═══════════════════════════════════════════════════════════════
            // TRIGGER MR CALCULATION WHEN ANY SHOCK HAS RECOVERED
            // ═══════════════════════════════════════════════════════════════
            // Philosophy: 
            //   - Don't wait for all shocks (will miss 95% of events)
            //   - Calculate whenever we have evidence of stress + recovery
            //   - Weighted scoring handles missing components gracefully
            // ═══════════════════════════════════════════════════════════════

            int completedShocks = 0;

            // Count spread recovery
            if (ShockSpread != null && ReturnedSpread != null)
                completedShocks++;

            // Count depth recovery
            if (ShockDepth != null && RecoveredDepth != null &&
                ShockDepth.Value != eLOBSIDE.NONE && RecoveredDepth.Value != eLOBSIDE.NONE)
                completedShocks++;

            // Trigger if we have at least one completed shock
            // (Spread OR depth must have recovered)
            if (completedShocks >= 1)
            {
                TriggerMRCalculation();
                Reset();
            }
        }


        private bool IsLargeTrade(decimal tradeSize)
        {
            decimal avgSize = recentTradeSizes.Average();
            decimal stdSize = recentTradeSizes.StandardDeviation();
            if (recentTradeSizes.Count < 3) return false; //not enough data
            return tradeSize > avgSize + SHOCK_THRESHOLD_SIGMA * stdSize;
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
            // WEIGHTED RESILIENCE CALCULATION WITH PARTIAL EVIDENCE
            // ═══════════════════════════════════════════════════════════════
            // Key changes from current implementation:
            // 1. Only process components that actually have data
            // 2. Adjust total weight dynamically based on available evidence
            // 3. Don't pollute historical data with zeros
            // 4. Rebalanced weights (spread + depth = 70%, magnitude = 30%)
            // ═══════════════════════════════════════════════════════════════

            double totalWeight = 0.0;
            double weightedScore = 0.0;

            // ───────────────────────────────────────────────────────────────
            // COMPONENT 0: TRADE SHOCK SEVERITY (30% weight)
            // ───────────────────────────────────────────────────────────────
            const double W_TRADE = 0.3;
            if (ShockTrade != null && recentTradeSizes.Any())
            {
                decimal avgSize = recentTradeSizes.Average();
                decimal stdSize = recentTradeSizes.StandardDeviation();

                if (stdSize > 0)
                {
                    // Z-score of trade size (how many std devs above mean)
                    double tradeZ = (double)((ShockTrade.Value - avgSize) / stdSize);

                    // Convert to resilience score (0..1)
                    // z=3 → score=0.5, z=6 → score=0
                    double tradeScore = Math.Max(0, 1.0 - (tradeZ / 6.0));

                    weightedScore += W_TRADE * tradeScore;
                    totalWeight += W_TRADE;
                }
            }

            // ───────────────────────────────────────────────────────────────
            // COMPONENT 1: SPREAD RECOVERY (10% weight)
            // ───────────────────────────────────────────────────────────────
            const double W_SPREAD = 0.1;

            if (ShockSpread != null && ReturnedSpread != null)
            {
                double spreadRecoveryDurationMs = Math.Abs((ReturnedSpread.Timestamp - ShockSpread.Timestamp).TotalMilliseconds);
                double avgSpreadHistoricalRecoveryMs = spreadRecoveryTimes.Any()
                    ? spreadRecoveryTimes.Average()
                    : spreadRecoveryDurationMs;

                double spreadRecoveryScore = avgSpreadHistoricalRecoveryMs /
                    (avgSpreadHistoricalRecoveryMs + spreadRecoveryDurationMs);
                spreadRecoveryScore = Math.Max(0, Math.Min(1, spreadRecoveryScore));

                weightedScore += W_SPREAD * spreadRecoveryScore;
                totalWeight += W_SPREAD;

                spreadRecoveryTimes.Add(spreadRecoveryDurationMs);  // ✅ Only add real data
            }

            // ───────────────────────────────────────────────────────────────
            // COMPONENT 2: DEPTH RECOVERY (50% weight)
            // ───────────────────────────────────────────────────────────────
            const double W_DEPTH = 0.5;

            if (ShockDepth != null && RecoveredDepth != null)
            {
                double depletionRecoveryDurationMs = Math.Abs((RecoveredDepth.Timestamp - ShockDepth.Timestamp).TotalMilliseconds);
                double avgDepletionHistoricalRecoveryMs = depletionRecoveryTimes.Any()
                    ? depletionRecoveryTimes.Average()
                    : depletionRecoveryDurationMs;

                double depletionRecoveryScore = avgDepletionHistoricalRecoveryMs /
                    (avgDepletionHistoricalRecoveryMs + depletionRecoveryDurationMs);
                depletionRecoveryScore = Math.Max(0, Math.Min(1, depletionRecoveryScore));

                weightedScore += W_DEPTH * depletionRecoveryScore;
                totalWeight += W_DEPTH;

                depletionRecoveryTimes.Add(depletionRecoveryDurationMs);  // ✅ Only add real data
            }

            // ───────────────────────────────────────────────────────────────
            // COMPONENT 3: SPREAD SHOCK MAGNITUDE (10% weight)
            // ───────────────────────────────────────────────────────────────
            const double W_MAGNITUDE = 0.10;

            if (ShockSpread != null)
            {
                decimal avgHistoricalSpread = recentSpreads.Any()
                    ? recentSpreads.Average()
                    : ShockSpread.Value;

                double magnitudeRatio = (double)(ShockSpread.Value / Math.Max(avgHistoricalSpread, 0.0001m));
                double magnitudeScore = 1.0 / magnitudeRatio;
                magnitudeScore = Math.Max(0, Math.Min(1, magnitudeScore));

                weightedScore += W_MAGNITUDE * magnitudeScore;
                totalWeight += W_MAGNITUDE;
            }

            // ───────────────────────────────────────────────────────────────
            // FINAL SCORE NORMALIZATION
            // ───────────────────────────────────────────────────────────────
            // ✅ KEY CHANGE: Normalize by actual total weight
            // This ensures score is always in [0, 1] regardless of missing components

            if (totalWeight > 0)
            {
                CurrentMRScore = (decimal)(weightedScore / totalWeight);
            }
            else
            {
                // Fallback: no evidence = baseline resilience
                CurrentMRScore = 1.0m;
            }

            // ───────────────────────────────────────────────────────────────
            // MARKET BIAS DETERMINATION
            // ───────────────────────────────────────────────────────────────
            CurrentMarketBias = CalculateMRBias() ?? CurrentMarketBias;
        }

        //DEPLETION FUNCTIONALITY USAGE:
        /*
            * Call `IsLOBDepleted(lob)` on **every** book update.

              * If it returns `NONE`, do nothing.
              * If it returns `BID`, `ASK`, or `BOTH` **and** there’s no active depth event, call `ActivateDepthEvent(lob, side)` once to start tracking recovery.
              * If it returns a side **while an event is already active**, choose your policy: ignore (recommended) or end/restart the event.

            * After an event is activated, call `IsLOBRecovered(lob)` on **every** book update.

              * It will return `NONE` until either the **recovery target is reached** (same or opposite side) **or** the **timeout** hits.
              * On that tick it returns `BID`/`ASK`/`BOTH` and clears the active event (edge-triggered). Resume watching for new depletion afterward.

            * Warm-up: allow the quantile baselines to collect enough samples before acting (the implementation already guards with `WARMUP_MIN_SAMPLES`).

            * Multiple-side cases: if `IsLOBDepleted` returns `BOTH`, pass `BOTH` into `ActivateDepthEvent`. `IsLOBRecovered` can likewise return `BOTH` if both sides meet the criterion together (or on timeout with equal recovery fractions).

            That’s it—your 3-step loop (detect → activate → recover) is the intended flow.
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
            double spreadNow = lob.Spread > 0 ? lob.Spread : ((_previousLOB?.Spread).GetValueOrDefault(0));
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

            // ✅ FIX: Declare variables ONCE at method scope, get estimates early
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
                _previousLOB = lob;
                return eLOBSIDE.NONE;
            }

            // 4) Use TRUE MAD instead of P90 approximation
            // (bidMed and askMed already declared above)
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

            // ✅ EDGE-TRIGGER LOGIC: Only report NEW depletions
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

            // 6) Advance previous snapshot and return
            _previousLOB = lob;
            return newDepletion;
        }
        internal void ActivateDepthEvent(in OrderBookSnapshot lob, eLOBSIDE side)
        {
            // Baselines at t0: use current robust medians if available, else current values
            double spreadBase = _samplesSpread >= WARMUP_MIN_SAMPLES
                ? _qSpreadMed.Estimate
                : Math.Max(lob.Spread, 1.0);

            double dBidNow = ImmediacyDepthBid(lob, spreadBase);
            double dAskNow = ImmediacyDepthAsk(lob, spreadBase);

            var nowTicks = Stopwatch.GetTimestamp();
            var tMaxTicks = nowTicks + MsToTicks(MAX_SHOCK_MS_TIME_OUT); // you can scale by shock magnitude later

            _activeDepth = new ActiveDepthEvent
            {
                T0Ticks = nowTicks,
                TmaxTicks = tMaxTicks,
                DepletedSide = side,
                SBase = spreadBase,
                DBaseBid = (_samplesDepth >= WARMUP_MIN_SAMPLES ? _qBidDMed.Estimate : dBidNow),
                DBaseAsk = (_samplesDepth >= WARMUP_MIN_SAMPLES ? _qAskDMed.Estimate : dAskNow),
                DTroughBid = dBidNow,  // initialize troughs at current, will update downward
                DTroughAsk = dAskNow
            };
        }
        internal eLOBSIDE IsLOBRecovered(in OrderBookSnapshot lob)
        {
            // No active event → nothing to recover
            if (_activeDepth == null)
            {
                _previousLOB = lob;
                return eLOBSIDE.NONE;
            }

            var ev = _activeDepth.Value;

            // Normalize distances by spread baseline captured at t0
            double spreadBase = ev.SBase > EPS ? ev.SBase : Math.Max(lob.Spread, 1.0);

            // Current immediacy-weighted depth (both sides)
            double dBidNow = ImmediacyDepthBid(lob, spreadBase);
            double dAskNow = ImmediacyDepthAsk(lob, spreadBase);

            // Update troughs (worst observed since t0)
            if (dBidNow < ev.DTroughBid) ev.DTroughBid = dBidNow;
            if (dAskNow < ev.DTroughAsk) ev.DTroughAsk = dAskNow;

            // Compute recovery fractions (0..1), side by side
            // For immediacy-depth, higher is better; recovery is how much we've climbed from trough toward baseline.
            double denomBid = Math.Max(ev.DBaseBid - ev.DTroughBid, EPS);
            double denomAsk = Math.Max(ev.DBaseAsk - ev.DTroughAsk, EPS);

            double dRecBid = Clamp01((dBidNow - ev.DTroughBid) / denomBid);
            double dRecAsk = Clamp01((dAskNow - ev.DTroughAsk) / denomAsk);

            // Early recover conditions (edge-triggered):
            // - If depleted side reaches RECOVERY_TARGET → done (resilient).
            // - Else if opposite side reaches RECOVERY_TARGET first → report opposite (control transferred).
            eLOBSIDE recovered = eLOBSIDE.NONE;

            bool bidWasDepleted = (ev.DepletedSide & eLOBSIDE.BID) != 0;
            bool askWasDepleted = (ev.DepletedSide & eLOBSIDE.ASK) != 0;

            // Check same-side first (resilient), then opposite
            if (bidWasDepleted && dRecBid >= RECOVERY_TARGET) recovered |= eLOBSIDE.BID;
            if (askWasDepleted && dRecAsk >= RECOVERY_TARGET) recovered |= eLOBSIDE.ASK;

            // If none of the depleted sides hit target, allow opposite-side dominance to count
            if (recovered == eLOBSIDE.NONE)
            {
                if (!bidWasDepleted && dRecBid >= RECOVERY_TARGET) recovered |= eLOBSIDE.BID;
                if (!askWasDepleted && dRecAsk >= RECOVERY_TARGET) recovered |= eLOBSIDE.ASK;
            }

            // Timeout?
            var nowTicks = Stopwatch.GetTimestamp();
            bool timedOut = nowTicks >= ev.TmaxTicks;

            if (recovered != eLOBSIDE.NONE || timedOut)
            {
                // Finalize: decide which side(s) to report on this tick
                if (recovered == eLOBSIDE.NONE && timedOut)
                {
                    // On timeout, report whichever side has the highest recovery fraction.
                    // This lets downstream logic classify bias even without hitting target.
                    recovered = (dRecBid > dRecAsk)
                        ? eLOBSIDE.BID
                        : (dRecAsk > dRecBid ? eLOBSIDE.ASK : eLOBSIDE.BOTH);
                }

                // Clear active event and advance snapshot
                _activeDepth = null;
                _previousLOB = lob;
                return recovered;           // edge-triggered: non-NONE only on finalize/threshold-cross
            }

            // Still recovering; keep the updated troughs and continue
            _activeDepth = ev;
            _previousLOB = lob;
            return eLOBSIDE.NONE;
        }





        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long MsToTicks(int ms)
        {
            double freq = Stopwatch.Frequency;
            return (long)(ms * (freq / 1000.0));
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

            double best = lob.Bids[0].Price.Value;
            double acc = 0.0;

            var levels = lob.Bids; // assume best-first ordering
            int n = levels.Length;
            for (int i = 0; i < n; i++)
            {
                double d = (best - levels[i].Price.Value) / spreadBase; // ≥ 0
                double w = InvSquareWeight(d);
                acc += levels[i].Size.Value * w;
            }
            return acc;
        }

        private static double ImmediacyDepthAsk(in OrderBookSnapshot lob, double spreadBase)
        {
            if (spreadBase <= EPS) spreadBase = Math.Max(lob.Spread, 1.0);
            if (lob.Asks.Length == 0) return 0; // empty side → zero immediacy

            double best = lob.Asks[0].Price.Value;
            double acc = 0.0;

            var levels = lob.Asks;
            int n = levels.Length;
            for (int i = 0; i < n; i++)
            {
                double d = (levels[i].Price.Value - best) / spreadBase; // ≥ 0
                double w = InvSquareWeight(d);
                acc += levels[i].Size.Value * w;
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
                ShockTrade = null;
                ShockDepth = null;
                RecoveredDepth = null;
                InitialHitHappenedAtBid = null;
                _lastMidPrice = null;
                _lastBidPrice = null;
                _lastAskPrice = null;
                _bidAtHit = null;
                _askAtHit = null;
                _activeDepth= null;
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

            if (disposing)
            {


            }
            disposed = true;
        }

        ~MarketResilienceCalculator()
        {
            Dispose(false);
        }
    }
}
