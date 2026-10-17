using VisualHFT.Desktop.Hosting;
using VisualHFT.Model;

namespace VisualHFT.Desktop.Tests;

public class MarketIngressTests
{
    [Fact]
    public void AddBook_CopiesLevels_AndDoesNotKeepTheMutableBook()
    {
        var ingress = new MarketIngress();
        ingress.SetFocus("SOLUSDT", "SOL/USD", "Binance");
        var book = new OrderBook("SOLUSDT", 2, 8) { ProviderName = "Binance" };
        book.AddOrUpdateLevel(true, "bid", 100, 2, DateTime.UtcNow, DateTime.UtcNow);
        book.AddOrUpdateLevel(false, "ask", 101, 3, DateTime.UtcNow, DateTime.UtcNow);

        ingress.AddBook(book);
        book.Symbol = "ETHUSDT";
        book.AddOrUpdateLevel(true, "bid", 50, 9, DateTime.UtcNow, DateTime.UtcNow);

        var batch = ingress.Drain();
        var snapshot = Assert.Single(batch.Books);
        Assert.Equal("SOLUSDT", snapshot.Symbol);
        Assert.Equal("Binance", snapshot.Provider);
        Assert.Contains(snapshot.Bids, level => level.Price == 100 && level.Size == 2);
        Assert.DoesNotContain(snapshot.Bids, level => level.Price == 50);
    }

    [Fact]
    public void AddBook_WithoutFocus_DropsTheUpdate()
    {
        var ingress = new MarketIngress();
        var book = new OrderBook("SOLUSDT", 2, 8) { ProviderName = "Binance" };
        ingress.AddBook(book);
        var batch = ingress.Drain();
        Assert.Empty(batch.Books);
        Assert.False(batch.HasWork);
    }

    [Fact]
    public void AddTrade_KeepsTheCopiedPrint_AfterTheTradeChanges()
    {
        var ingress = new MarketIngress();
        ingress.SetFocus("SOLUSDT", "SOL/USD", "Binance");
        var trade = new Trade { Symbol = "SOLUSDT", ProviderName = "Binance", Price = 12.5m, Size = 3m, IsBuy = true, Timestamp = DateTime.UtcNow };

        ingress.AddTrade(trade);
        trade.Price = 99m;
        trade.Symbol = "ETHUSDT";

        var batch = ingress.Drain();
        var print = Assert.Single(batch.Trades);
        Assert.Equal(12.5, print.Price);
        Assert.Equal("SOLUSDT", print.Symbol);
        Assert.NotNull(batch.LatestTrade);
        Assert.Equal(12.5, batch.LatestTrade.Value.Price);
    }

    [Fact]
    public void AddTrade_DropsTheOldestPrint_OnceTheCapIsReached()
    {
        var ingress = new MarketIngress();
        ingress.SetFocus("SOLUSDT", "SOL/USD", "Binance");
        for (var i = 0; i < MarketIngress.MaxTrades + 5; i++)
        {
            ingress.AddTrade(new Trade
            {
                Symbol = "SOLUSDT",
                ProviderName = "Binance",
                Price = i,
                Size = 1,
                Timestamp = DateTime.UtcNow
            });
        }

        var batch = ingress.Drain();
        Assert.Equal(MarketIngress.MaxTrades, batch.Trades.Count);
        Assert.Equal(5, batch.Trades[0].Price);
        Assert.Equal(MarketIngress.MaxTrades + 4, batch.Trades[^1].Price);
    }

    [Fact]
    public void AddBook_NullAndEmptySymbol_DoNotThrow()
    {
        var ingress = new MarketIngress();
        ingress.SetFocus("SOLUSDT", "SOL/USD", "Binance");
        ingress.Drain();
        ingress.AddBook(null);
        ingress.AddBook(new OrderBook());
        ingress.AddTrade(null);
        Assert.False(ingress.Drain().HasWork);
    }
}
