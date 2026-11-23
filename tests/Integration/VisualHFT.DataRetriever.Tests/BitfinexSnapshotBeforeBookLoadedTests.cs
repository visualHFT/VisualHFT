using System.Collections.Concurrent;
using System.Reflection;
using Bitfinex.Net.Objects.Models;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using MarketConnectors.Bitfinex;
using MarketConnectors.Bitfinex.Model;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.PluginManager;
using VisualHFT.PluginManager;
using Xunit;

namespace VisualHFT.DataRetriever.TestingFramework.TestCases;

/// <summary>
/// Covers the snapshot frame Bitfinex pushes on the level2 channel right after the subscription is
/// accepted, when the symbol's book slot is still empty (null).
///
/// The start registers the null slot in <c>InternalStartAsync</c> before it subscribes. A snapshot
/// frame that finds the null slot must build the book from that frame. It must not dereference the
/// null value: a <see cref="NullReferenceException"/> in <c>UpdateOrderBookSnapshot</c> would reach the
/// callback's catch, which logs an ERROR and asks the shared reconnection engine to reconnect, which
/// re-subscribes and reopens the very same window.
///
/// The contract under test: a snapshot frame that arrives while the book is still the null slot raises
/// no ERROR log and no reconnect request, and builds the book (the channel's deltas follow that frame;
/// <see cref="BitfinexSnapshotSequencingTests"/> shows why dropping it leaves a crossed book), while a
/// snapshot for a loaded book is still applied.
///
/// Offline by construction: the plugin is never started (no socket, no REST call); the book map is
/// put into the state InternalStartAsync leaves it in before it subscribes, and the production callback
/// is invoked by reflection. Settings come from memory, not settings.json.
/// </summary>
public class BitfinexSnapshotBeforeBookLoadedTests
{
    private const string BtcWire = "tBTCUSD";
    private const string BtcNormalized = "BTC/USD";
    private const string ReconnectPrefix = "Trying to reconnect";

    [Fact]
    public void SnapshotWhileBookIsNullSlot_SeedsTheBook_NotLoggedAsError_NotEscalatedToReconnect()
    {
        using var plugin = new OfflineBitfinexPlugin();
        InjectBook(plugin, BtcNormalized, null);

        using var reconnectRequested = new ManualResetEventSlim(false);
        var reconnectReasons = new ConcurrentBag<string>();
        EventHandler<ErrorNotificationEventArgs> onNotification = (_, e) =>
        {
            if (e.Notification.Message != null && e.Notification.Message.StartsWith(ReconnectPrefix, StringComparison.Ordinal))
            {
                reconnectReasons.Add(e.Notification.Message);
                reconnectRequested.Set();
            }
        };
        HelperNotificationManager.Instance.NotificationAdded += onNotification;
        try
        {
            // Deterministic observable: the callback's catch runs LogException synchronously.
            IReadOnlyList<string> errors = CaptureErrorLog(() =>
                Deliver(plugin, SnapshotFrame(Bid(100.5m, 2m)), BtcNormalized));

            Assert.True(errors.Count == 0,
                "A snapshot arriving before the REST book is loaded must not raise an error; instead it was logged as an ERROR:\n"
                + string.Join("\n", errors));

            var seeded = ReadBook(plugin, BtcNormalized);
            Assert.True(seeded != null, "A snapshot arriving before the REST book is loaded must build the book; the slot is still empty.");
            Assert.Equal(100.5, seeded!.GetTOB(true)?.Price);
            Assert.Equal(2d, seeded.GetTOB(true)?.Size);

            // The reconnect request is dispatched via Task.Run; give it a bounded window to show up.
            bool escalated = reconnectRequested.Wait(TimeSpan.FromMilliseconds(750));
            Assert.False(escalated,
                "A snapshot arriving before the REST book is loaded must NOT be escalated to HandleConnectionLost "
                + "(the reconnect re-subscribes and reopens the same window); got: " + string.Join(" | ", reconnectReasons));
        }
        finally
        {
            HelperNotificationManager.Instance.NotificationAdded -= onNotification;
            // A failure here escalates into the reconnection engine's retry loop. Park the plugin so
            // that loop stops at its next status check instead of logging into a later test's capture.
            plugin.Status = ePluginStatus.STOPPED_FAILED;
        }
    }

    [Fact]
    public void SnapshotForLoadedBook_IsApplied()
    {
        // Seeding the book from the frame that finds the null slot must not change how a later
        // snapshot frame is applied to a book that is already loaded.
        using var plugin = new OfflineBitfinexPlugin();
        var book = new VisualHFT.Model.OrderBook(BtcNormalized, 2, 25);
        InjectBook(plugin, BtcNormalized, book);

        Deliver(plugin, SnapshotFrame(Bid(100.5m, 2m), Ask(101.5m, 3m)), BtcNormalized);

        var bestBid = book.GetTOB(true);
        var bestAsk = book.GetTOB(false);
        Assert.True(bestBid != null, "The snapshot's bid never reached the loaded book.");
        Assert.True(bestAsk != null, "The snapshot's ask never reached the loaded book.");
        Assert.Equal(100.5, bestBid!.Price!.Value);
        Assert.Equal(101.5, bestAsk!.Price!.Value);
        Assert.Equal(2d, bestBid.Size!.Value);
        Assert.Equal(3d, bestAsk.Size!.Value);
        Assert.Equal(101.0, book.MidPrice);
    }

    [Fact]
    public void SnapshotForUnknownSymbol_IsDroppedSilently()
    {
        // The map is empty, as it is between ClearAsync and the next start, which adds the slot in
        // InternalStartAsync before subscribing: the symbol's KEY is absent, so the TryGetValue guard
        // drops the frame.
        using var plugin = new OfflineBitfinexPlugin();

        using var reconnectRequested = new ManualResetEventSlim(false);
        var reconnectReasons = new ConcurrentBag<string>();
        EventHandler<ErrorNotificationEventArgs> onNotification = (_, e) =>
        {
            if (e.Notification.Message != null && e.Notification.Message.StartsWith(ReconnectPrefix, StringComparison.Ordinal))
            {
                reconnectReasons.Add(e.Notification.Message);
                reconnectRequested.Set();
            }
        };
        HelperNotificationManager.Instance.NotificationAdded += onNotification;
        try
        {
            IReadOnlyList<string> errors = CaptureErrorLog(() =>
                Deliver(plugin, SnapshotFrame(Bid(100.5m, 2m)), BtcNormalized));

            Assert.True(errors.Count == 0,
                "A snapshot for a symbol with no book entry must be dropped silently; instead it was logged as an ERROR:\n"
                + string.Join("\n", errors));

            bool escalated = reconnectRequested.Wait(TimeSpan.FromMilliseconds(750));
            Assert.False(escalated,
                "A snapshot for a symbol with no book entry must NOT be escalated to HandleConnectionLost; got: "
                + string.Join(" | ", reconnectReasons));
        }
        finally
        {
            HelperNotificationManager.Instance.NotificationAdded -= onNotification;
            plugin.Status = ePluginStatus.STOPPED_FAILED;
        }
    }

    // ------------------------------------------------------------------------------------------

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

    /// <summary>
    /// The frame Bitfinex pushes immediately after a level2 subscription is accepted, which can arrive
    /// before or during InitializeSnapshotsAsync's REST call.
    /// </summary>
    private static DataEvent<BitfinexOrderBookEntry[]> SnapshotFrame(params BitfinexOrderBookEntry[] entries)
    {
        return new DataEvent<BitfinexOrderBookEntry[]>("Bitfinex", entries, DateTime.UtcNow, null)
        {
            Symbol = BtcWire,
            UpdateType = SocketUpdateType.Snapshot
        };
    }

    /// <summary>
    /// Puts a book, or the NULL slot InternalStartAsync registers before it subscribes, into the
    /// plugin's private per-symbol book map. This reflection only SETS UP state; the behaviour
    /// under test runs through the production delta callback.
    /// </summary>
    private static void InjectBook(BitfinexPlugin plugin, string normalizedSymbol, VisualHFT.Model.OrderBook? book)
    {
        FieldInfo? field = typeof(BitfinexPlugin).GetField("_localOrderBooks", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, "BitfinexPlugin._localOrderBooks not found: the per-symbol book map was renamed; update this test.");
        var map = (Dictionary<string, VisualHFT.Model.OrderBook>)field!.GetValue(plugin)!;
        map.Add(normalizedSymbol, book!);
    }

    /// <summary>
    /// Invokes the connector's level2 callback, <c>OnDeltaReceived</c>, the method the websocket
    /// subscription calls for every frame. It is internal, so the test reaches it by reflection.
    /// </summary>
    private static void Deliver(BitfinexPlugin plugin, DataEvent<BitfinexOrderBookEntry[]> frame, string normalizedSymbol)
    {
        MethodInfo? method = typeof(BitfinexPlugin).GetMethod("OnDeltaReceived", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(method != null, "BitfinexPlugin.OnDeltaReceived not found: the level2 callback was renamed; update this test.");
        method!.Invoke(plugin, new object[] { frame, normalizedSymbol });
    }

    private static VisualHFT.Model.OrderBook? ReadBook(BitfinexPlugin plugin, string normalizedSymbol)
    {
        FieldInfo? field = typeof(BitfinexPlugin).GetField("_localOrderBooks", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, "BitfinexPlugin._localOrderBooks not found: the per-symbol book map was renamed; update this test.");
        var map = (Dictionary<string, VisualHFT.Model.OrderBook>)field!.GetValue(plugin)!;
        return map.TryGetValue(normalizedSymbol, out var book) ? book : null;
    }

    /// <summary>
    /// Captures ERROR-and-above events from the log4net repositories the connector's loggers write to
    /// (BasePluginDataRetriever.LogException logs the callback's error).
    /// </summary>
    private static IReadOnlyList<string> CaptureErrorLog(Action act)
    {
        var repositories = new[]
            {
                log4net.LogManager.GetRepository(typeof(BasePluginDataRetriever).Assembly),
                log4net.LogManager.GetRepository(typeof(BitfinexPlugin).Assembly)
            }
            .Distinct()
            .Cast<log4net.Repository.Hierarchy.Hierarchy>()
            .ToList();

        var appender = new log4net.Appender.MemoryAppender { Name = "bitfinex-snapshot-before-book-loaded-capture" };
        appender.ActivateOptions();
        var previousLevels = new List<log4net.Core.Level>();
        // Configured is process-global state on a SHARED repository: forcing it true and leaving it true
        // makes every later test in this assembly run against a repository this capture reconfigured.
        // Restore it with the level and the appender.
        var previousConfigured = new List<bool>();
        foreach (var h in repositories)
        {
            previousLevels.Add(h.Root.Level);
            previousConfigured.Add(h.Configured);
            h.Root.AddAppender(appender);
            h.Root.Level = log4net.Core.Level.All;
            h.Configured = true;
        }
        try
        {
            act();
        }
        finally
        {
            for (int i = 0; i < repositories.Count; i++)
            {
                repositories[i].Root.RemoveAppender(appender);
                repositories[i].Root.Level = previousLevels[i];
                repositories[i].Configured = previousConfigured[i];
            }
        }

        return appender.GetEvents()
            .Where(e => e.Level >= log4net.Core.Level.Error)
            .Select(e => $"{e.Level.Name} {e.RenderedMessage}" + (e.ExceptionObject != null ? " :: " + e.ExceptionObject.GetType().Name : string.Empty))
            .ToList();
    }

    /// <summary>
    /// BitfinexPlugin with in-memory settings (no settings.json read/write) and the shipped default
    /// symbol map. Never started, so the book map holds exactly what the test injects.
    /// </summary>
    private sealed class OfflineBitfinexPlugin : BitfinexPlugin
    {
        protected override void LoadSettings()
        {
            Settings = new PlugInSettings
            {
                ApiKey = string.Empty,
                ApiSecret = string.Empty,
                DepthLevels = 25,
                Provider = new VisualHFT.Model.Provider { ProviderID = 2, ProviderName = "Bitfinex" },
                Symbols = new List<string> { "tBTCUSD(BTC/USD)", "tETHUSD(ETH/USD)" }
            };
            ParseSymbols("tBTCUSD(BTC/USD),tETHUSD(ETH/USD)");
        }

        protected override void SaveSettings() { }
    }
}
