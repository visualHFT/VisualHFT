using System;
using Studies.MarketResilience.Model;
using VisualHFT;
using VisualHFT.Commons.Model;
using VisualHFT.Model;
using VisualHFT.Studies.MarketResilience.Model;
using Xunit;

namespace Studies.MarketResilience.Tests
{
    /// <summary>
    /// A crossed or locked book (best bid at or above best ask: a pre-open, a halt, or a feed out of sync) has no spread or
    /// mid a recovery can be measured against, so it adds no spread sample and moves no mid. A print with no
    /// taker (an auction cross, flagged "no-aggressor") never anchors a shock and never enters the trade-size statistics.
    /// </summary>
    public class MarketResilienceCrossedBookTests : IDisposable
    {
        private static readonly DateTime ClockStart = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Local);

        private sealed class Probe : MarketResilienceCalculator
        {
            public Probe(PlugInSettings settings) : base(settings) { }
            public int SpreadSamples => recentSpreads.Count;
            public bool HasShockTrade => ShockTrade != null;
        }

        public MarketResilienceCrossedBookTests() => HelperTimeProvider.SetFixedTime(ClockStart);

        public void Dispose() => HelperTimeProvider.ResetToSystemTime();

        private static OrderBookSnapshot Book(decimal bid, decimal ask)
        {
            var ob = new OrderBook();
            ob.LoadData(new[] { new BookItem { Price = (double)ask, Size = 100, IsBid = false } },
                        new[] { new BookItem { Price = (double)bid, Size = 100, IsBid = true } });
            var snapshot = new OrderBookSnapshot();
            snapshot.UpdateFrom(ob);
            return snapshot;
        }

        private static Probe Warmed()
        {
            var calc = new Probe(new PlugInSettings { MaxShockMsTimeout = 500 });
            for (int i = 0; i < 60; i++)
            {
                HelperTimeProvider.IncrementByMilliseconds(100);
                calc.OnOrderBookUpdate(Book(100.25m, 100.50m));
                calc.OnTrade(new Trade { Price = 100.50m, Size = 5, IsBuy = true });
            }

            return calc;
        }

        [Theory]
        [InlineData(101.25, 100.50)]   // crossed: bid 101.25 over offer 100.50
        [InlineData(100.50, 100.50)]   // locked
        public void ACrossedOrLockedBook_AddsNoSpreadSample_AndMovesNoMid(double bid, double ask)
        {
            Probe calc = Warmed();
            int samples = calc.SpreadSamples;

            HelperTimeProvider.IncrementByMilliseconds(100);
            calc.OnOrderBookUpdate(Book((decimal)bid, (decimal)ask));

            Assert.Equal(samples, calc.SpreadSamples);
            Assert.Equal(100.375m, calc.MidMarketPrice);
        }

        [Fact]
        public void AnAuctionPrint_FlaggedNoAggressor_NeverAnchorsAShock()
        {
            Probe calc = Warmed();

            HelperTimeProvider.IncrementByMilliseconds(100);
            calc.OnTrade(new Trade { Price = 100.75m, Size = 2500, IsBuy = null, Flags = "no-aggressor" });

            Assert.False(calc.HasShockTrade);
        }

        [Fact]
        public void ALargeUnflaggedPrint_StillAnchorsAShock()
        {
            Probe calc = Warmed();

            HelperTimeProvider.IncrementByMilliseconds(100);
            calc.OnTrade(new Trade { Price = 100.50m, Size = 2500, IsBuy = false });

            Assert.True(calc.HasShockTrade);
        }
    }
}
