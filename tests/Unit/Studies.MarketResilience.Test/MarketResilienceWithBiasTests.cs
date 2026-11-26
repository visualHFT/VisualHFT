using VisualHFT;
using VisualHFT.Model;
using Studies.MarketResilience.Model;
using VisualHFT.Commons.Model;
using VisualHFT.Studies.MarketResilience.Model;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Studies.MarketResilience.Test")]

namespace Studies.MarketResilience.Tests
{
    /// <summary>
    /// Tests for MarketResilienceWithBias class focusing on directional bias detection
    /// after depth depletion/recovery events.
    ///
    /// The rules these tests assert:
    ///   * only the DEPLETED side counts. A depleted side that regains 90% of what it lost closes
    ///     the event as a recovery; the other side growing is a price move, not resilience;
    ///   * an event whose depleted side never comes back inside the window is scored as a
    ///     non-recovery (depth 0 at its full weight), and the direction points away from the side
    ///     that failed to redeploy: a bid that never came back is Bearish, an ask is Bullish;
    ///   * the first scored recovery of a session seeds the recovery history and publishes
    ///     nothing, so every test that reads a score first completes at least one recovery.
    ///
    /// Every recovery duration is driven through the shared time provider, so the numbers are
    /// exact rather than whatever the machine happened to measure.
    /// </summary>
    public class MarketResilienceWithBiasTests
    {
        private static readonly DateTime ClockStart = new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local);

        private PlugInSettings _settings;

        public MarketResilienceWithBiasTests()
        {
            _settings = new PlugInSettings()
            {
                MaxShockMsTimeout = 500,
            };
        }

        /// <summary>Pins the shared time provider; disposing restores wall time.</summary>
        private sealed class FixedClock : IDisposable
        {
            public FixedClock() => HelperTimeProvider.SetFixedTime(ClockStart);
            public void Advance(long milliseconds) => HelperTimeProvider.IncrementByMilliseconds(milliseconds);
            public void Dispose() => HelperTimeProvider.ResetToSystemTime();
        }

        private OrderBookSnapshot BuildLOB((decimal px, double sz)[] asks, (decimal px, double sz)[] bids)
        {
            var ob = new OrderBook();

            var askItems = asks.Select(a => new BookItem
            {
                Price = (double)a.px,
                Size = a.sz,
                IsBid = false,
                LocalTimeStamp = HelperTimeProvider.Now,
                ServerTimeStamp = HelperTimeProvider.Now
            }).ToArray();

            var bidItems = bids.Select(b => new BookItem
            {
                Price = (double)b.px,
                Size = b.sz,
                IsBid = true,
                LocalTimeStamp = HelperTimeProvider.Now,
                ServerTimeStamp = HelperTimeProvider.Now
            }).ToArray();

            ob.LoadData(askItems, bidItems);
            var snapshot = new OrderBookSnapshot();
            snapshot.UpdateFrom(ob);
            return snapshot;
        }

        /// <summary>
        /// Trains the depth, spread and trade baselines. Every print is the same size, so the
        /// trade-size dispersion is zero: any larger print anchors a shock, and the trade-severity
        /// component is omitted from every score in the tests that use this warm-up.
        /// </summary>
        private void WarmUp(MarketResilienceWithBias calc, int frames = 300)
        {
            WarmUp(calc, frames, _ => 100m);
        }

        private void WarmUp(MarketResilienceWithBias calc, int frames, Func<int, decimal> printSize)
        {
            var random = new Random(42);
            for (int i = 0; i < frames; i++)
            {
                var lob = BuildLOB(
                    asks: new[] {
                        (100.50m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                         Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),
                        (100.51m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                         Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),
                        (100.52m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                         Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    },
                    bids: new[] {
                        (100.49m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                         Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),
                        (100.48m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                         Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),
                        (100.47m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                         Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    }
                );

                calc.OnOrderBookUpdate(lob);
                calc.OnTrade(new Trade
                {
                    Size = printSize(i),
                    Price = 100.49m,
                    Timestamp = HelperTimeProvider.Now
                });
            }
        }

        // ---------- books shared by the scenarios ----------

        private OrderBookSnapshot FullBook() => BuildLOB(
            asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
            bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) });

        private OrderBookSnapshot BidDepletedBook() => BuildLOB(
            asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
            bids: new[] { (100.40m, 50.0), (100.39m, 30.0) });

        private OrderBookSnapshot AskDepletedBook() => BuildLOB(
            asks: new[] { (100.70m, 20.0), (100.71m, 15.0) },
            bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) });

        private OrderBookSnapshot BothDepletedBook() => BuildLOB(
            asks: new[] { (100.60m, 20.0) },
            bids: new[] { (100.40m, 20.0) });

        private static Trade LargePrint(decimal price) => new Trade { Size = 5000, Price = price, Timestamp = HelperTimeProvider.Now };

        /// <summary>
        /// One complete depletion-and-recovery cycle: the large print, the depleted book, the
        /// recovery <paramref name="recoveryMs"/> later. The first such cycle on a fresh
        /// calculator seeds the recovery history; the ones after it publish a score.
        /// </summary>
        private void CompleteCycle(MarketResilienceWithBias calc, FixedClock clock, OrderBookSnapshot depleted, long recoveryMs, decimal printPrice = 100.49m)
        {
            calc.OnTrade(LargePrint(printPrice));
            calc.OnOrderBookUpdate(depleted);
            clock.Advance(recoveryMs);
            calc.OnOrderBookUpdate(FullBook());
        }

        /// <summary>
        /// Test 1: BID depletion that never redeploys = Bearish bias.
        ///
        /// The ask improving while the bid is still gone does NOT close the event: the untouched
        /// side's growth is a price move, not resilience. The event closes when its window runs
        /// out, scored as a non-recovery, and the direction points away from the bid that failed.
        ///
        /// Defects this catches: crediting the opposite side's growth as the recovery (the score
        /// would move at the ask frame); discarding a timed-out event instead of scoring it (the
        /// score would stay at the seeded 0.44 and never arm the bias); attributing the direction
        /// to the side that grew rather than the side that failed (Bullish instead of Bearish).
        /// </summary>
        [Fact]
        public void BidDepletionAskRecovery_ShouldDetectBearishBias()
        {
            using var clock = new FixedClock();
            var _settings = new PlugInSettings() { MaxShockMsTimeout = 600 };
            var calc = new MarketResilienceWithBias(_settings);
            WarmUp(calc);

            // Seed the recovery history with fast (100 ms) same-side recoveries. The first cycle
            // only seeds; the rest publish a middling score that leaves the bias unarmed.
            for (int i = 0; i < 5; i++)
                CompleteCycle(calc, clock, BidDepletedBook(), recoveryMs: 100);

            var seededScore = calc.CurrentMRScore;
            Assert.NotEqual(1m, seededScore);
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);
            // No verdict yet: the Neutral above is the default, not a reading, so the tile shows none.
            Assert.False(calc.HasBiasVerdict);

            // The event under test: the bid is taken out and never comes back.
            calc.OnTrade(LargePrint(100.49m));
            calc.OnOrderBookUpdate(BidDepletedBook());

            clock.Advance(450);

            // ASK side improves while the BID is still weak. That is not a recovery of anything.
            var askImproved = BuildLOB(
                asks: new[] { (100.48m, 120.0), (100.49m, 120.0), (100.50m, 120.0) },
                bids: new[] { (100.40m, 50.0), (100.39m, 30.0) }
            );
            calc.OnOrderBookUpdate(askImproved);

            Assert.Equal(seededScore, calc.CurrentMRScore);
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);

            // The window (600 ms) runs out with the bid still gone.
            clock.Advance(151);
            calc.OnOrderBookUpdate(askImproved);

            Assert.NotEqual(seededScore, calc.CurrentMRScore);
            Assert.True(calc.CurrentMRScore <= 0.30m,
                $"MR score {calc.CurrentMRScore} should be ≤ 0.30");
            Assert.Equal(eMarketBias.Bearish, calc.CurrentMarketBias);
            Assert.True(calc.HasBiasVerdict);
        }

        /// <summary>
        /// Test 2: ASK depletion that never redeploys = Bullish bias. Mirror of test 1.
        ///
        /// Defects this catches: the same three as test 1, on the ask side.
        /// </summary>
        [Fact]
        public void AskDepletionBidRecovery_ShouldDetectBullishBias()
        {
            using var clock = new FixedClock();
            var _settings = new PlugInSettings() { MaxShockMsTimeout = 600 };
            var calc = new MarketResilienceWithBias(_settings);
            WarmUp(calc);

            for (int i = 0; i < 5; i++)
                CompleteCycle(calc, clock, AskDepletedBook(), recoveryMs: 100, printPrice: 100.50m);

            var seededScore = calc.CurrentMRScore;
            Assert.NotEqual(1m, seededScore);
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);

            calc.OnTrade(LargePrint(100.50m));
            calc.OnOrderBookUpdate(AskDepletedBook());

            clock.Advance(450);

            // BID side improves while the ASK is still weak.
            var bidImproved = BuildLOB(
                asks: new[] { (100.70m, 20.0), (100.71m, 15.0) },
                bids: new[] { (100.50m, 120.0), (100.49m, 120.0), (100.48m, 120.0) }
            );
            calc.OnOrderBookUpdate(bidImproved);

            Assert.Equal(seededScore, calc.CurrentMRScore);
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);

            clock.Advance(151);
            calc.OnOrderBookUpdate(bidImproved);

            Assert.NotEqual(seededScore, calc.CurrentMRScore);
            Assert.True(calc.CurrentMRScore <= 0.30m,
                $"MR score {calc.CurrentMRScore} should be ≤ 0.30");
            Assert.Equal(eMarketBias.Bullish, calc.CurrentMarketBias);
        }

        /// <summary>
        /// Test 3: BID depletion → BID recovery (same side) = Neutral bias, even when the score
        /// is poor enough to arm the bias. A slow recovery is still a recovery: the depleted
        /// side came back, so there is no side to point away from.
        ///
        /// Defects this catches: a direction attributed from the depleted side alone (this would
        /// read Bearish); a same-side refill not being credited as the recovery (the event would
        /// stay open and the score would stay at the seeded value, above 0.30).
        /// </summary>
        [Fact]
        public void SameSideRecovery_ShouldDetectNeutralBias()
        {
            using var clock = new FixedClock();
            var calc = new MarketResilienceWithBias(_settings);
            WarmUp(calc);

            // Fast (50 ms) recoveries set the reference, so the 450 ms one below scores poorly.
            for (int i = 0; i < 5; i++)
                CompleteCycle(calc, clock, BidDepletedBook(), recoveryMs: 50);

            var seededScore = calc.CurrentMRScore;
            Assert.NotEqual(1m, seededScore);

            // The event under test: the bid is taken out and comes back, slowly.
            CompleteCycle(calc, clock, BidDepletedBook(), recoveryMs: 450);

            Assert.NotEqual(seededScore, calc.CurrentMRScore);
            Assert.True(calc.CurrentMRScore <= 0.30m,
                $"MR score {calc.CurrentMRScore} should be ≤ 0.30 so the bias is armed");
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);
        }

        /// <summary>
        /// Test 4: BOTH sides depleted → only the BID comes back = Bullish bias. The event stays
        /// open until every depleted side has redeployed or the window closes; here the ask never
        /// does, so the event closes on the window and the direction points away from the ask.
        ///
        /// Defects this catches: closing the event when the first of two depleted sides returns;
        /// forgetting which side did return when the window closes (the direction would be
        /// Neutral, as if both had failed); a swapped direction (Bearish).
        /// </summary>
        [Fact]
        public void BothSidesDepleted_BidRecoversFirst_ShouldDetectBullishBias()
        {
            using var clock = new FixedClock();
            var _settings = new PlugInSettings() { MaxShockMsTimeout = 600 };
            var calc = new MarketResilienceWithBias(_settings);
            WarmUp(calc);

            for (int i = 0; i < 5; i++)
                CompleteCycle(calc, clock, BothDepletedBook(), recoveryMs: 100);

            var seededScore = calc.CurrentMRScore;
            Assert.NotEqual(1m, seededScore);
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);

            calc.OnTrade(LargePrint(100.49m));
            calc.OnOrderBookUpdate(BothDepletedBook());

            clock.Advance(450);

            // BID side comes back; ASK side is still gone. One of two is not a recovery.
            var bidBack = BuildLOB(
                asks: new[] { (100.60m, 20.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );
            calc.OnOrderBookUpdate(bidBack);

            Assert.Equal(seededScore, calc.CurrentMRScore);
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);

            clock.Advance(151);
            calc.OnOrderBookUpdate(bidBack);

            Assert.NotEqual(seededScore, calc.CurrentMRScore);
            Assert.True(calc.CurrentMRScore <= 0.30m,
                $"MR score {calc.CurrentMRScore} should be ≤ 0.30");
            Assert.Equal(eMarketBias.Bullish, calc.CurrentMarketBias);
        }

        /// <summary>
        /// Test 5: a high MR score (≥ 0.5) silences the arrow. The bias is armed by a failed
        /// event first, so the silence is observable as a change: Bearish → Neutral.
        ///
        /// Defects this catches: a fast, complete recovery not scoring as high resilience (the
        /// recovery-time ratio inverted); the arrow keeping its direction once the book has
        /// proven resilient.
        /// </summary>
        [Fact]
        public void HighMRScore_ShouldSkipBiasCalculation()
        {
            using var clock = new FixedClock();
            var _settings = new PlugInSettings() { MaxShockMsTimeout = 600 };
            var calc = new MarketResilienceWithBias(_settings);
            WarmUp(calc);

            // A slow (400 ms) recovery seeds the reference. It publishes nothing.
            CompleteCycle(calc, clock, BidDepletedBook(), recoveryMs: 400);
            Assert.Equal(1m, calc.CurrentMRScore);

            // Arm the bias: the bid is taken out and never comes back inside the window.
            calc.OnTrade(LargePrint(100.49m));
            calc.OnOrderBookUpdate(BidDepletedBook());
            clock.Advance(601);
            calc.OnOrderBookUpdate(BidDepletedBook());

            Assert.True(calc.CurrentMRScore <= 0.30m,
                $"MR score {calc.CurrentMRScore} should be ≤ 0.30 to arm the bias");
            Assert.Equal(eMarketBias.Bearish, calc.CurrentMarketBias);

            // A quiet frame so the next depletion is a new edge.
            calc.OnOrderBookUpdate(FullBook());

            // Very fast (30 ms) same-side recovery against the 400 ms reference → high score.
            CompleteCycle(calc, clock, BidDepletedBook(), recoveryMs: 30);

            Assert.True(calc.CurrentMRScore >= 0.50m,
                $"MR score {calc.CurrentMRScore} should be ≥ 0.50 after a fast, complete recovery");
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);
        }

        /// <summary>
        /// Test 6: Hysteresis — the arrow arms at MR ≤ 0.30, stays armed through the middle zone
        /// (0.30 &lt; MR &lt; 0.50), disarms at MR ≥ 0.50, and stays silent in the middle zone once
        /// disarmed.
        ///
        /// An event with a failed side carries depth 0 at weight 0.50, so a middle-zone score
        /// needs the other components near their best. This test warms up with prints of varying
        /// size (80 / 100 / 120: mean 100, dispersion 16.33) so trade severity participates; a
        /// 133-share print is just large enough to anchor (z ≈ 2.02 → severity 0.66) and a
        /// 5,000-share print scores severity 0. The spread-recovery reference is seeded once with
        /// a 400 ms return, so a return on the very next frame scores 1.0. Every failed event
        /// widens the spread to 0.03 (magnitude ≈ 0.33) and returns it on the next frame.
        ///
        ///   failed side, 5,000 print ... (0 + 0.10·1.0 + 0.50·0 + 0.10·0.33) / 1.00 ≈ 0.13 → arms
        ///   failed side,   133 print ... (0.30·0.66 + 0.10·1.0 + 0 + 0.10·0.33) / 1.00 ≈ 0.33 → middle
        ///   30 ms recovery vs 400 ms .. (0.30·0.66 + 0.50·0.93) / 0.80 ≈ 0.83 → disarms
        ///
        /// Defects this catches: no direction on a poor score (phase 1); the direction not being
        /// spoken in the middle zone while armed (phase 2 would keep the stale Bearish); the
        /// arrow not clearing at ≥ 0.50 (phase 3); the latch not disarming, so a middle-zone
        /// failed side speaks when it should stay silent (phase 4 would read Bullish).
        /// </summary>
        [Fact]
        public void HysteresisStateMachine_ShouldArmDisarmCorrectly()
        {
            using var clock = new FixedClock();
            var _settings = new PlugInSettings() { MaxShockMsTimeout = 500 };
            var calc = new MarketResilienceWithBias(_settings);
            WarmUp(calc, 300, i => 80m + 20m * (i % 3));

            Trade Print(decimal size) => new Trade { Size = size, Price = 100.49m, Timestamp = HelperTimeProvider.Now };

            // Books whose depleted side sits at its usual prices, thinned to 5 a level, with the
            // OTHER side moved three ticks away so the spread widens to 0.03; and the same book
            // with the other side back at half a tick, so the spread has returned.
            var bidGoneSpreadWide = BuildLOB(
                asks: new[] { (100.52m, 100.0), (100.53m, 100.0), (100.54m, 100.0) },
                bids: new[] { (100.49m, 5.0), (100.48m, 5.0), (100.47m, 5.0) });
            var bidGoneSpreadBack = BuildLOB(
                asks: new[] { (100.495m, 100.0), (100.505m, 100.0), (100.515m, 100.0) },
                bids: new[] { (100.49m, 5.0), (100.48m, 5.0), (100.47m, 5.0) });
            var askGoneSpreadWide = BuildLOB(
                asks: new[] { (100.50m, 5.0), (100.51m, 5.0), (100.52m, 5.0) },
                bids: new[] { (100.47m, 100.0), (100.46m, 100.0), (100.45m, 100.0) });
            var askGoneSpreadBack = BuildLOB(
                asks: new[] { (100.50m, 5.0), (100.51m, 5.0), (100.52m, 5.0) },
                bids: new[] { (100.495m, 100.0), (100.485m, 100.0), (100.475m, 100.0) });

            // Full depth on both sides, spread 0.03 / 0.005: a spread event with no depletion.
            var wideNoDepletion = BuildLOB(
                asks: new[] { (100.52m, 100.0), (100.53m, 100.0), (100.54m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) });
            var tightNoDepletion = BuildLOB(
                asks: new[] { (100.495m, 100.0), (100.505m, 100.0), (100.515m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) });

            var bidThinned = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 5.0), (100.48m, 5.0), (100.47m, 5.0) });

            void FailedEvent(decimal printSize, OrderBookSnapshot wide, OrderBookSnapshot back)
            {
                calc.OnTrade(Print(printSize));
                calc.OnOrderBookUpdate(wide);
                calc.OnOrderBookUpdate(back);
                clock.Advance(501);
                calc.OnOrderBookUpdate(back);   // the window has run out; the side never came back
                calc.OnOrderBookUpdate(FullBook());
                clock.Advance(1000);
            }

            // ═══════════════════════════════════════════════════════════════
            // PHASE 0: seed the spread-recovery reference (400 ms). Publishes nothing.
            // ═══════════════════════════════════════════════════════════════
            calc.OnTrade(Print(5000m));
            calc.OnOrderBookUpdate(wideNoDepletion);
            clock.Advance(400);
            calc.OnOrderBookUpdate(tightNoDepletion);
            calc.OnOrderBookUpdate(FullBook());
            clock.Advance(1000);
            Assert.Equal(1m, calc.CurrentMRScore);

            // ═══════════════════════════════════════════════════════════════
            // PHASE 1: ARM (MR ≤ 0.30) — the bid fails on a severe print.
            // ═══════════════════════════════════════════════════════════════
            FailedEvent(5000m, bidGoneSpreadWide, bidGoneSpreadBack);

            Assert.True(calc.CurrentMRScore <= 0.30m,
                $"Phase 1: MR score {calc.CurrentMRScore} should be ≤ 0.30 to arm hysteresis");
            Assert.Equal(eMarketBias.Bearish, calc.CurrentMarketBias);

            // ═══════════════════════════════════════════════════════════════
            // PHASE 2: STAY ARMED in the middle zone — the ask fails on a mild print.
            // ═══════════════════════════════════════════════════════════════
            FailedEvent(133m, askGoneSpreadWide, askGoneSpreadBack);

            Assert.InRange(calc.CurrentMRScore, 0.3000001m, 0.4999999m);
            Assert.Equal(eMarketBias.Bullish, calc.CurrentMarketBias);

            // ═══════════════════════════════════════════════════════════════
            // PHASE 3: DISARM (MR ≥ 0.50) — a fast, complete recovery.
            // ═══════════════════════════════════════════════════════════════

            // Seed the depth-recovery reference with a slow (400 ms) recovery. Publishes nothing.
            var beforeSeed = calc.CurrentMRScore;
            calc.OnTrade(Print(5000m));
            calc.OnOrderBookUpdate(bidThinned);
            clock.Advance(400);
            calc.OnOrderBookUpdate(FullBook());
            Assert.Equal(beforeSeed, calc.CurrentMRScore);
            Assert.Equal(eMarketBias.Bullish, calc.CurrentMarketBias);
            clock.Advance(1000);

            calc.OnTrade(Print(133m));
            calc.OnOrderBookUpdate(bidThinned);
            clock.Advance(30);
            calc.OnOrderBookUpdate(FullBook());
            clock.Advance(1000);

            Assert.True(calc.CurrentMRScore >= 0.50m,
                $"Phase 3: MR score {calc.CurrentMRScore} should be ≥ 0.50 to disarm hysteresis");
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);

            // ═══════════════════════════════════════════════════════════════
            // PHASE 4: STAY DISARMED in the middle zone — the same failed-ask event as phase 2
            // must now be spoken as nothing at all.
            // ═══════════════════════════════════════════════════════════════
            FailedEvent(133m, askGoneSpreadWide, askGoneSpreadBack);

            Assert.InRange(calc.CurrentMRScore, 0.3000001m, 0.4999999m);
            Assert.Equal(eMarketBias.Neutral, calc.CurrentMarketBias);
        }
    }
}
