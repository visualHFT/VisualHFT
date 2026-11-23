using System.Reflection;
using Bitfinex.Net.Enums;
using Bitfinex.Net.Interfaces.Clients;
using Bitfinex.Net.Interfaces.Clients.ExchangeApi;
using Bitfinex.Net.Objects.Models;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using MarketConnectors.Bitfinex;
using MarketConnectors.Bitfinex.Model;
using Moq;
using VisualHFT.PluginManager;
using Xunit;

namespace VisualHFT.DataRetriever.TestingFramework.TestCases;

/// <summary>
/// Reproduces a crossed Bitfinex book: a bid at 60733 stays in the book above an ask at 60720, and no
/// later update removes it. The live order book integrity test
/// (<c>PluginFunctionalTests.Test_Plugin_OrderBookIntegrityAndResilience_Async</c>) reports this as a
/// crossed spread that persists.
///
/// Mechanism: the start subscribes to the level2 channel first and fetches the REST book second.
/// Bitfinex answers the subscription with a snapshot frame, and every delta on the channel is relative
/// to THAT frame. The start used to drop the frame, because the symbol had no book entry yet (the frame
/// came during the subscribe call) or only a null placeholder (it came during the REST call), and then
/// seeded the book from the REST snapshot. While the book entry was absent or null, the queue consumer
/// also dropped every delta. The REST picture is normally taken after the subscription, so a change made
/// after that picture and before the book was stored was lost for good: a level cancelled in that gap
/// stays in the REST book, and no later delta removes it. Once the market trades through it, the book
/// is crossed.
///
/// The contract under test: whenever the websocket snapshot arrives, the book the connector keeps
/// is built from it, and a REST snapshot never replaces a book the websocket snapshot already built.
///
/// Offline by construction: the venue clients come through the CreateSocketClient/CreateRestClient seams
/// as Moq doubles; the test drives the production StartAsync and the production delta callback.
/// </summary>
public class BitfinexSnapshotSequencingTests
{
    private const string BtcWire = "tBTCUSD";
    private const string BtcNormalized = "BTC/USD";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public enum SnapshotArrives
    {
        /// <summary>Bitfinex pushes the frame before the subscribe call has returned.</summary>
        DuringTheSubscribeCall,

        /// <summary>The frame lands while the REST snapshot request is in flight.</summary>
        DuringTheRestCall,

        /// <summary>The frame lands after the REST book was loaded.</summary>
        AfterTheRestBookLoaded,
    }

    [Theory]
    [InlineData(SnapshotArrives.DuringTheSubscribeCall)]
    [InlineData(SnapshotArrives.DuringTheRestCall)]
    [InlineData(SnapshotArrives.AfterTheRestBookLoaded)]
    public async Task StartAsync_BuildsTheBookFromTheWebsocketSnapshot_SoALevelCancelledBeforeItDoesNotSurvive(SnapshotArrives when)
    {
        using var plugin = new OfflineBitfinexPlugin(when);
        try
        {
            await RunAsync(plugin.StartAsync());
            Assert.Equal(ePluginStatus.STARTED, plugin.Status);
            Assert.True(plugin.DeltaHandler != null, "StartAsync did not subscribe to the level2 channel on the mocked socket.");

            if (when == SnapshotArrives.AfterTheRestBookLoaded)
            {
                plugin.DeltaHandler!(OfflineBitfinexPlugin.WebsocketSnapshot());
            }

            // The market moves down: a new ask below the cancelled bid.
            plugin.DeltaHandler!(Delta(Ask(60720m, 0.5m)));

            VisualHFT.Model.OrderBook book = await WaitForBestAskAsync(plugin, 60720d);

            var bestBid = book.GetTOB(true);
            var bestAsk = book.GetTOB(false);
            Assert.True(bestBid?.Price == 60700d,
                $"Snapshot {when}: best bid is {bestBid?.Price} @ {bestBid?.Size}, expected 60700. "
                + "The bid at 60733 was cancelled before the websocket snapshot; it exists only in the REST "
                + "snapshot. Seeding the book from the REST snapshot and dropping the websocket one leaves it in the "
                + $"book forever, and the book is now crossed (bid {bestBid?.Price} >= ask {bestAsk?.Price}).");
            Assert.True(bestBid!.Price < bestAsk!.Price,
                $"Snapshot {when}: crossed book, bid {bestBid.Price} >= ask {bestAsk.Price}.");
        }
        finally
        {
            await RunAsync(plugin.StopAsync());
        }
    }

    // ------------------------------------------------------------------------------------------

    private static async Task RunAsync(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(ReferenceEquals(finished, task), $"The connector call did not complete within {Timeout.TotalSeconds:0} s.");
        await task;
    }

    /// <summary>Deltas are applied by the symbol's queue consumer; wait until this one has landed.</summary>
    private static async Task<VisualHFT.Model.OrderBook> WaitForBestAskAsync(BitfinexPlugin plugin, double price)
    {
        var books = (Dictionary<string, VisualHFT.Model.OrderBook>)PrivateField("_localOrderBooks").GetValue(plugin)!;
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (books.TryGetValue(BtcNormalized, out var book) && book != null && book.GetTOB(false)?.Price == price)
            {
                return book;
            }
            await Task.Delay(10);
        }
        books.TryGetValue(BtcNormalized, out var last);
        Assert.Fail($"The delta's ask {price} never reached the book within {Timeout.TotalSeconds:0} s. "
                    + $"Book present: {last != null}; best ask: {last?.GetTOB(false)?.Price}.");
        return null!;
    }

    private static FieldInfo PrivateField(string name)
    {
        FieldInfo? field = typeof(BitfinexPlugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, $"BitfinexPlugin has no private field '{name}'; the book path changed shape.");
        return field!;
    }

    /// <summary>Bitfinex encodes side in the sign of Quantity: positive is a bid.</summary>
    private static BitfinexOrderBookEntry Bid(decimal price, decimal size)
    {
        return new BitfinexOrderBookEntry { Price = price, Quantity = size, Count = 1 };
    }

    /// <summary>Bitfinex encodes side in the sign of Quantity: negative is an ask.</summary>
    private static BitfinexOrderBookEntry Ask(decimal price, decimal size)
    {
        return new BitfinexOrderBookEntry { Price = price, Quantity = -size, Count = 1 };
    }

    private static DataEvent<BitfinexOrderBookEntry[]> Delta(params BitfinexOrderBookEntry[] entries)
    {
        return new DataEvent<BitfinexOrderBookEntry[]>("Bitfinex", entries, DateTime.UtcNow, null)
        {
            Symbol = BtcWire,
            UpdateType = SocketUpdateType.Update
        };
    }

    /// <summary>
    /// BitfinexPlugin with in-memory settings, an instant reconnect backoff, and Moq venue clients whose
    /// level2 subscription and REST snapshot succeed. The websocket snapshot frame is pushed at the
    /// moment the test names.
    /// </summary>
    private sealed class OfflineBitfinexPlugin : BitfinexPlugin
    {
        private readonly SnapshotArrives _when;

        public OfflineBitfinexPlugin(SnapshotArrives when)
        {
            _when = when;
        }

        public Action<DataEvent<BitfinexOrderBookEntry[]>>? DeltaHandler { get; private set; }

        /// <summary>The channel's picture: the bid at 60733 has already been cancelled.</summary>
        public static DataEvent<BitfinexOrderBookEntry[]> WebsocketSnapshot()
        {
            return new DataEvent<BitfinexOrderBookEntry[]>(
                "Bitfinex",
                new[] { Bid(60700m, 1.5m), Bid(60690m, 2m), Ask(60740m, 0.75m), Ask(60750m, 1.2m) },
                DateTime.UtcNow,
                null)
            {
                Symbol = BtcWire,
                UpdateType = SocketUpdateType.Snapshot
            };
        }

        /// <summary>The REST picture: it still holds the bid at 60733.</summary>
        private static BitfinexOrderBook RestSnapshot()
        {
            return new BitfinexOrderBook
            {
                Bids = new[] { Bid(60733m, 0.023m), Bid(60700m, 1.5m), Bid(60690m, 2m) },
                Asks = new[] { Ask(60740m, 0.75m), Ask(60750m, 1.2m) },
            };
        }

        protected override void LoadSettings()
        {
            Settings = new PlugInSettings
            {
                ApiKey = string.Empty,
                ApiSecret = string.Empty,
                DepthLevels = 25,
                Provider = new VisualHFT.Model.Provider { ProviderID = 2, ProviderName = "Bitfinex" },
                Symbols = new List<string> { "tBTCUSD(BTC/USD)" }
            };
            ParseSymbols("tBTCUSD(BTC/USD)");
        }

        protected override void SaveSettings() { }

        protected override Task ReconnectBackoffDelayAsync(int milliseconds)
        {
            return Task.CompletedTask;
        }

        protected override IBitfinexSocketClient CreateSocketClient()
        {
            var api = new Mock<IBitfinexSocketClientExchangeApi>(MockBehavior.Loose);

            // UpdateSubscription cannot be built outside the library, so a successful result carries no
            // subscription object; the connector skips attaching handlers to null.
            api.Setup(x => x.SubscribeToOrderBookUpdatesAsync(
                    It.IsAny<string>(),
                    It.IsAny<Precision>(),
                    It.IsAny<Frequency>(),
                    It.IsAny<int>(),
                    It.IsAny<Action<DataEvent<BitfinexOrderBookEntry[]>>>(),
                    It.IsAny<Action<DataEvent<int>>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, Precision, Frequency, int, Action<DataEvent<BitfinexOrderBookEntry[]>>, Action<DataEvent<int>>?, CancellationToken>(
                    (_, _, _, _, handler, _, _) =>
                    {
                        DeltaHandler = handler;
                        if (_when == SnapshotArrives.DuringTheSubscribeCall)
                        {
                            handler(WebsocketSnapshot());
                        }
                    })
                .Returns(() => Task.FromResult(new WebSocketResult<UpdateSubscription>("Bitfinex", null, null)));
            api.Setup(x => x.SubscribeToTradeUpdatesAsync(
                    It.IsAny<string>(),
                    It.IsAny<Action<DataEvent<BitfinexTradeSimple[]>>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(new WebSocketResult<UpdateSubscription>("Bitfinex", null, null)));

            var socket = new Mock<IBitfinexSocketClient>(MockBehavior.Loose);
            socket.Setup(x => x.ExchangeApi).Returns(api.Object);
            socket.Setup(x => x.UnsubscribeAllAsync()).Returns(() => Task.CompletedTask);
            return socket.Object;
        }

        protected override IBitfinexRestClient CreateRestClient()
        {
            var data = new Mock<IBitfinexRestClientExchangeApiExchangeData>(MockBehavior.Loose);
            data.Setup(x => x.GetOrderBookAsync(
                    It.IsAny<string>(),
                    It.IsAny<Precision>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    if (_when == SnapshotArrives.DuringTheRestCall)
                    {
                        DeltaHandler!(WebsocketSnapshot());
                    }
                    return Task.FromResult(new HttpResult<BitfinexOrderBook>("Bitfinex", RestSnapshot(), null));
                });

            var exchangeApi = new Mock<IBitfinexRestClientExchangeApi>(MockBehavior.Loose);
            exchangeApi.Setup(x => x.ExchangeData).Returns(data.Object);

            var rest = new Mock<IBitfinexRestClient>(MockBehavior.Loose);
            rest.Setup(x => x.ExchangeApi).Returns(exchangeApi.Object);
            return rest.Object;
        }
    }
}
