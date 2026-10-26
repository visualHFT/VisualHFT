using System.Diagnostics;
using System.Reflection;
using VisualHFT.Commons.Model;
using VisualHFT.Model;

namespace VisualHFT.Commons.Tests;

/// <summary>
/// DEFECT B — a disposed <see cref="OrderBook"/> still throws on the mutation entry points the
/// connectors reach INLINE from the socket thread, so a book disposed by a reconnect turns the very
/// next in-flight frame into a NullReferenceException.
///
/// The read side is already guarded: <c>GetBidsSnapshot</c> (VisualHFT.Commons/Model/OrderBook.cs:155),
/// <c>GetAsksSnapshot</c> (OrderBook.cs:192) and <c>CalculateMetrics</c> (OrderBook.cs:307) all bail on
/// <c>Volatile.Read(ref _disposed)</c> and return empty rather than throw. <c>DeleteLevel(..)</c>
/// (OrderBook.cs:824) is the write-side PRECEDENT, not a defect: it already re-checks the side list for
/// null INSIDE the write lock (OrderBook.cs:834-837 and :844-847) and returns, and it did so before
/// this branch. <c>CalculateMetrics</c> does the same at OrderBook.cs:314. The remaining write paths do
/// not:
///   * <c>Clear()</c>              OrderBook.cs:630 -> InternalClear() -> <c>_data.Asks.Count()</c> (:434)
///   * <c>AddOrUpdateLevel(..)</c> OrderBook.cs:656 -> <c>_list.Count()</c> on the null side list (:676)
///   * <c>AddLevel(..)</c>         OrderBook.cs:699 -> <c>list.Count()</c> on the null side list (:717)
///   * <c>UpdateLevel(..)</c>      OrderBook.cs:776 -> <c>.Update(..)</c> on the null side list (:786)
/// while <c>OrderBookData.Dispose</c> (VisualHFT.Commons/Model/OrderBookData.cs:244-262) nulls
/// <c>_Bids</c>/<c>_Asks</c> under the write lock.
///
/// DEFECT C — the sibling sweep stopped short. Three more entry points take the SAME write lock and
/// dereference the SAME side lists with no guard of any kind, neither a <c>_disposed</c> pre-check nor
/// an in-lock re-check:
///   * <c>Reset()</c>          OrderBook.cs:649 -> InternalClear() -> <c>_data.Asks.Count()</c> (:434)
///   * <c>UpdateSnapshot(..)</c> OrderBook.cs:466 -> <c>_data.Clear()</c> (:471) -> <c>foreach (var item in _Asks)</c> (OrderBookData.cs:202)
///   * <c>LoadData(..)</c>     OrderBook.cs:320 -> <c>_data.Clear()</c> (:325) -> the same OrderBookData.cs:202
/// These are not exotic paths: <c>LoadData</c> and <c>UpdateSnapshot</c> are how a REST or websocket
/// SNAPSHOT is applied, which is precisely what a reconnect does immediately after the book map was
/// torn down, and <c>Reset()</c> is the recycle path. The fix is the one-line in-lock re-check the
/// other four now carry.
///
/// TWO defects live here, and they need two shapes of test.
///
/// (1) SEQUENTIAL — one thread, dispose then mutate. <c>Clear()</c> and <c>AddOrUpdateLevel(..)</c>
/// answer this today via their <c>Volatile.Read(ref _disposed)</c> pre-check; <c>DeleteLevel</c> answers
/// it via its in-lock null re-check. Pinned by the four <c>_OnADisposedBook_</c> tests below.
///
/// (2) THE RACE — the pre-check is check-then-act OUTSIDE the lock. A socket thread reads
/// <c>_disposed == false</c>, is descheduled, and a reconnect thread runs
/// <c>OrderBookData.Dispose</c>: flag set, write lock taken, <c>_Bids</c>/<c>_Asks</c> nulled, lock
/// released. The socket thread resumes PAST its own guard, takes the write lock and dereferences null.
/// A pre-lock flag read cannot close that window — only a null re-check INSIDE the lock can, which is
/// exactly what <c>DeleteLevel</c> and <c>CalculateMetrics</c> already do. <c>AddLevel</c> and
/// <c>UpdateLevel</c> are worse still: they carry no guard at all, so any caller that writes a
/// level directly can reach them on a book a reconnect has already disposed.
///
/// The four <c>_WhenTheDisposeLandsAfterTheGuardPassed_</c> tests reproduce that window
/// deterministically, without threads: dispose the book for real (side lists null, flag true), then put
/// the private <c>_disposed</c> flag back to <c>false</c> by reflection. That is precisely the state a
/// thread which has already passed the pre-check observes, and it is unreachable by any pre-lock flag
/// read — so only an in-lock null re-check on all four write paths turns these green.
///
/// Why it matters here: Bitfinex applies snapshot and delta frames inline on the socket thread
/// (<c>BitfinexPlugin.UpdateOrderBookSnapshot</c> -> <c>lob.Clear()</c> then <c>AddOrUpdateLevel</c>),
/// and <c>ClearAsync</c> disposes every book in <c>_localOrderBooks</c> during a reconnect. The
/// exception type and message shape produced by that window are identical to the production storm
/// measured on v0.1.11 (309 error events, 2 users) — "Unhandled error while receiving delta market
/// data for BTC/USD" with a NullReferenceException.
///
/// Contract under test: every mutation entry point behaves like the read side — a book whose side lists
/// are gone silently ignores the frame (no throw, no state change), whether the caller learned that
/// before or after it passed the guard, while a LIVE book still applies all of them.
/// </summary>
public class OrderBookDisposedGuardTests
{
    private const string Symbol = "BTC/USD";
    private const int PriceDecimalPlaces = 2;
    private const int MaxDepth = 25;

    /// <summary>Ceiling on waiting for the mutation thread to park on the write lock (normally microseconds).</summary>
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling on the mutation finishing once the lock is handed over; a miss means a deadlock.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    // ---------------------------------------------------------------------------------------------
    // (1) SEQUENTIAL — the caller can see the disposed flag before it acts.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Clear_OnADisposedBook_IsIgnored_NotAnNre()
    {
        var book = NewLoadedBook();
        book.Dispose();

        Exception? thrown = Record.Exception(() => book.Clear());

        Assert.True(thrown == null,
            "Clear() on a disposed book must be a no-op (the read side already bails on _disposed). "
            + "It threw instead, which is exactly the shape of the Bitfinex production storm: "
            + Describe(thrown));
    }

    [Fact]
    public void AddOrUpdateLevel_OnADisposedBook_IsIgnored_NotAnNre()
    {
        var book = NewLoadedBook();
        book.Dispose();

        Exception? thrown = Record.Exception(() => book.AddOrUpdateLevel(Delta(isBid: true, price: 100.5, size: 2.0)));

        Assert.True(thrown == null,
            "AddOrUpdateLevel() on a disposed book must be a no-op; a delta frame in flight when "
            + "ClearAsync disposes the book reaches this line inline on the socket thread. It threw: "
            + Describe(thrown));
    }

    [Fact]
    public void DeleteLevel_OnADisposedBook_IsIgnored_NotAnNre()
    {
        var book = NewLoadedBook();
        book.Dispose();

        Exception? thrown = Record.Exception(() => book.DeleteLevel(Delta(isBid: true, price: 100.5, size: 0.0)));

        Assert.True(thrown == null,
            "DeleteLevel() on a disposed book must be a no-op; a delta frame in flight when "
            + "ClearAsync disposes the book reaches this line inline on the socket thread. It threw: "
            + Describe(thrown));
    }

    [Fact]
    public void MutationsOnADisposedBook_LeaveItEmpty_NotPartiallyMutated()
    {
        // The guard must DROP the frame, not half-apply it: a disposed book has no side lists left,
        // so the only correct outcome is "nothing happened".
        //
        // NOTE — there is deliberately no post-state assertion here. GetBidsSnapshot/GetAsksSnapshot
        // return 0 UNCONDITIONALLY on a disposed book (OrderBook.cs:160-161, :195-196, the _disposed
        // pre-check), and _data.Bids/_data.Asks are null, so every observable is a constant: a
        // "still empty" assertion through them cannot fail whether the frames were dropped or not.
        // The one falsifiable fact about this burst is that it completes without throwing.
        var book = NewLoadedBook();
        book.Dispose();

        Exception? thrown = Record.Exception(() =>
        {
            book.Clear();
            book.AddOrUpdateLevel(Delta(isBid: true, price: 101.0, size: 5.0));
            book.AddOrUpdateLevel(Delta(isBid: false, price: 102.0, size: 5.0));
            book.DeleteLevel(Delta(isBid: true, price: 101.0, size: 0.0));
        });

        Assert.True(thrown == null,
            "A burst of frames landing on a book that a reconnect has just disposed must be dropped "
            + "silently. It threw: " + Describe(thrown));
    }

    // ---------------------------------------------------------------------------------------------
    // (2) THE RACE — the caller passed the guard BEFORE the dispose landed. The pre-lock
    //     Volatile.Read(ref _disposed) is check-then-act and cannot see this; only an in-lock null
    //     re-check of the side list can (the DeleteLevel/CalculateMetrics precedent).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Clear_WhenTheDisposeLandsAfterTheGuardPassed_IsIgnored_NotAnNre()
    {
        var book = DisposedBookWithTheRaceWindowReopened();

        Exception? thrown = Record.Exception(() => book.Clear());

        Assert.True(thrown == null,
            "Clear() (OrderBook.cs:630) read _disposed==false, then the dispose won the race and nulled "
            + "the side lists under the write lock. Clear() then took the same lock and ran "
            + "InternalClear() -> _data.Asks.Count() (OrderBook.cs:434) on a null list. The pre-lock flag "
            + "read cannot close this window; an in-lock null re-check can, as DeleteLevel already does "
            + "(OrderBook.cs:834-837). It threw: " + Describe(thrown));
    }

    [Fact]
    public void AddOrUpdateLevel_WhenTheDisposeLandsAfterTheGuardPassed_IsIgnored_NotAnNre()
    {
        var book = DisposedBookWithTheRaceWindowReopened();

        Exception? thrown = Record.Exception(() =>
            book.AddOrUpdateLevel(Delta(isBid: true, price: 100.5, size: 2.0)));

        Assert.True(thrown == null,
            "AddOrUpdateLevel() (OrderBook.cs:656) read _disposed==false, then the dispose won the race. "
            + "Inside the write lock it dereferenced the nulled side list at _list.Count() "
            + "(OrderBook.cs:676). This is the frame a Bitfinex/Coinbase socket thread is holding when "
            + "ClearAsync disposes the book mid-reconnect. It threw: " + Describe(thrown));
    }

    [Fact]
    public void AddLevel_WhenTheDisposeLandsAfterTheGuardPassed_IsIgnored_NotAnNre()
    {
        var book = DisposedBookWithTheRaceWindowReopened();

        Exception? thrown = Record.Exception(() => book.AddLevel(
            IsBid: true,
            EntryID: string.Empty,
            Price: 100.5,
            Size: 2.0,
            LocalTimeStamp: DateTime.Now,
            ServerTimeStamp: DateTime.Now));

        Assert.True(thrown == null,
            "AddLevel() has NO disposed guard at all and takes no lock of its own; it "
            + "dereferenced the nulled side list at list.Count(). A caller writing a level "
            + "directly can reach it on a book a reconnect has disposed. It threw: "
            + Describe(thrown));
    }

    [Fact]
    public void UpdateLevel_WhenTheDisposeLandsAfterTheGuardPassed_IsIgnored_NotAnNre()
    {
        var book = DisposedBookWithTheRaceWindowReopened();

        Exception? thrown = Record.Exception(() => book.UpdateLevel(
            IsBid: true,
            EntryID: string.Empty,
            Price: 100.5,
            Size: 4.0,
            LocalTimeStamp: DateTime.Now,
            ServerTimeStamp: DateTime.Now));

        Assert.True(thrown == null,
            "UpdateLevel() has NO disposed guard at all and takes no lock of its own; "
            + "it dereferenced the nulled side list at .Update(..). Reached through "
            + "a direct level update. It threw: "
            + Describe(thrown));
    }

    [Fact]
    public void Reset_WhenTheDisposeLandsAfterTheGuardPassed_IsIgnored_NotAnNre()
    {
        var book = DisposedBookWithTheRaceWindowReopened();

        Exception? thrown = Record.Exception(() => book.Reset());

        Assert.True(thrown == null,
            "Reset() (OrderBook.cs:649) has NO disposed guard at all: it takes the write lock and runs "
            + "InternalClear() -> _data.Asks.Count() (OrderBook.cs:434) straight onto the nulled side "
            + "list. Same shape as the Clear() that was fixed on this branch, same one-line in-lock "
            + "re-check needed. It threw: " + Describe(thrown));
    }

    [Fact]
    public void UpdateSnapshot_WhenTheDisposeLandsAfterTheGuardPassed_IsIgnored_NotAnNre()
    {
        var book = DisposedBookWithTheRaceWindowReopened();
        BookItem[] asks = { Level(isBid: false, price: 101.5, size: 3.0) };
        BookItem[] bids = { Level(isBid: true, price: 100.5, size: 2.0) };

        Exception? thrown = Record.Exception(() => book.UpdateSnapshot(asks, bids));

        Assert.True(thrown == null,
            "UpdateSnapshot() (OrderBook.cs:466) has NO disposed guard at all: it takes the write lock and "
            + "runs _data.Clear() (OrderBook.cs:471), which iterates the nulled _Asks at "
            + "OrderBookData.cs:202. This is the entry point a websocket SNAPSHOT frame lands on, and a "
            + "reconnect re-seeds the book from a snapshot the moment after ClearAsync disposed it. It "
            + "threw: " + Describe(thrown));
    }

    [Fact]
    public void LoadData_WhenTheDisposeLandsAfterTheGuardPassed_IsIgnored_NotAnNre()
    {
        var book = DisposedBookWithTheRaceWindowReopened();
        BookItem[] asks = { Level(isBid: false, price: 101.5, size: 3.0) };
        BookItem[] bids = { Level(isBid: true, price: 100.5, size: 2.0) };

        bool applied = true;
        Exception? thrown = Record.Exception(() => applied = book.LoadData(asks, bids));

        Assert.True(thrown == null,
            "LoadData() (OrderBook.cs:320) has NO disposed guard at all: it takes the write lock and runs "
            + "_data.Clear() (OrderBook.cs:325), which iterates the nulled _Asks at OrderBookData.cs:202. "
            + "This is the entry point a REST snapshot lands on during the reconnect that disposed the "
            + "book. It threw: " + Describe(thrown));
        // LoadData reports whether it applied the snapshot; a torn-down book applied nothing.
        Assert.False(applied, "LoadData() on a torn-down book must report that nothing was applied.");
    }

    // ---------------------------------------------------------------------------------------------
    // (3) THE RACE, DISCRIMINATED — the four tests above cannot tell an IN-LOCK null re-check from a
    //     PRE-LOCK one, because in them the side lists are already null before the call. These two
    //     can: the book is fully alive when the mutation starts, so every pre-lock guard (the flag
    //     read AND any pre-lock null check) passes, and the lists are nulled only while the mutation
    //     is parked on the write lock. Only a re-check taken INSIDE the lock survives this.
    //
    //     Written for the five entry points that actually take the lock: Clear, AddOrUpdateLevel and
    //     the three added for DEFECT C. AddLevel (OrderBook.cs:699) and UpdateLevel (OrderBook.cs:776)
    //     take no lock at all, so "in-lock" has no meaning for them until one is added; they are
    //     covered by the flag-reset tests above.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Clear_WhenTheDisposeLandsWhileItWaitsForTheWriteLock_IsIgnored_NotAnNre()
    {
        var book = NewLoadedBook();

        Exception? thrown = MutateWhileTheWriteLockIsHeld(book, b => b.Clear(), teardownLandsWhileParked: true);

        Assert.True(thrown == null,
            "Clear() (OrderBook.cs:630) was ALIVE and unguardable when it started: _disposed was false "
            + "and both side lists were populated, so every pre-lock check passes. The teardown then ran "
            + "to completion while Clear() was parked on the write lock, exactly as OrderBookData.Dispose "
            + "does (OrderBookData.cs:255-262). Clear() acquired the lock and dereferenced the nulled list "
            + "at InternalClear() -> _data.Asks.Count() (OrderBook.cs:434). Only a null re-check taken "
            + "INSIDE the write lock closes this; a stronger read of _disposed cannot. It threw: "
            + Describe(thrown));
    }

    [Fact]
    public void AddOrUpdateLevel_WhenTheDisposeLandsWhileItWaitsForTheWriteLock_IsIgnored_NotAnNre()
    {
        var book = NewLoadedBook();

        Exception? thrown = MutateWhileTheWriteLockIsHeld(
            book,
            b => b.AddOrUpdateLevel(Delta(isBid: true, price: 100.5, size: 2.0)),
            teardownLandsWhileParked: true);

        Assert.True(thrown == null,
            "AddOrUpdateLevel() (OrderBook.cs:656) was ALIVE and unguardable when it started, so every "
            + "pre-lock check passes. The teardown completed while it was parked on the write lock; it "
            + "then acquired the lock and dereferenced the nulled side list at _list.Count() "
            + "(OrderBook.cs:676). Only a null re-check taken INSIDE the write lock closes this. It threw: "
            + Describe(thrown));
    }

    [Fact]
    public void Reset_WhenTheDisposeLandsWhileItWaitsForTheWriteLock_IsIgnored_NotAnNre()
    {
        var book = NewLoadedBook();

        Exception? thrown = MutateWhileTheWriteLockIsHeld(book, b => b.Reset(), teardownLandsWhileParked: true);

        Assert.True(thrown == null,
            "Reset() (OrderBook.cs:649) was ALIVE and unguardable when it started, so every pre-lock check "
            + "passes. The teardown completed while it was parked on the write lock; it then acquired the "
            + "lock and dereferenced the nulled list at InternalClear() -> _data.Asks.Count() "
            + "(OrderBook.cs:434). Only a null re-check taken INSIDE the write lock closes this. It threw: "
            + Describe(thrown));
    }

    [Fact]
    public void UpdateSnapshot_WhenTheDisposeLandsWhileItWaitsForTheWriteLock_IsIgnored_NotAnNre()
    {
        var book = NewLoadedBook();
        BookItem[] asks = { Level(isBid: false, price: 101.5, size: 3.0) };
        BookItem[] bids = { Level(isBid: true, price: 100.5, size: 2.0) };

        Exception? thrown = MutateWhileTheWriteLockIsHeld(
            book,
            b => b.UpdateSnapshot(asks, bids),
            teardownLandsWhileParked: true);

        Assert.True(thrown == null,
            "UpdateSnapshot() (OrderBook.cs:466) was ALIVE and unguardable when it started, so every "
            + "pre-lock check passes. The teardown completed while it was parked on the write lock; it "
            + "then acquired the lock and ran _data.Clear() (OrderBook.cs:471) over the nulled _Asks "
            + "(OrderBookData.cs:202). Only a null re-check taken INSIDE the write lock closes this. It "
            + "threw: " + Describe(thrown));
    }

    [Fact]
    public void LoadData_WhenTheDisposeLandsWhileItWaitsForTheWriteLock_IsIgnored_NotAnNre()
    {
        var book = NewLoadedBook();
        BookItem[] asks = { Level(isBid: false, price: 101.5, size: 3.0) };
        BookItem[] bids = { Level(isBid: true, price: 100.5, size: 2.0) };

        Exception? thrown = MutateWhileTheWriteLockIsHeld(
            book,
            b => b.LoadData(asks, bids),
            teardownLandsWhileParked: true);

        Assert.True(thrown == null,
            "LoadData() (OrderBook.cs:320) was ALIVE and unguardable when it started, so every pre-lock "
            + "check passes. The teardown completed while it was parked on the write lock; it then "
            + "acquired the lock and ran _data.Clear() (OrderBook.cs:325) over the nulled _Asks "
            + "(OrderBookData.cs:202). Only a null re-check taken INSIDE the write lock closes this. It "
            + "threw: " + Describe(thrown));
    }

    [Fact]
    public void TheParkedOnTheLockHarness_WithNoTeardown_LetsTheMutationThroughUnharmed()
    {
        // Positive control for the two tests above: same choreography — mutation started on its own
        // thread, parked on the write lock, lock handed over — but WITHOUT the teardown. If this were
        // to fail (or if it passed while the mutation never actually ran), the NREs above would be an
        // artefact of the harness rather than of the dispose landing inside the lock.
        using var book = NewLoadedBook();

        Exception? thrown = MutateWhileTheWriteLockIsHeld(book, b => b.Clear(), teardownLandsWhileParked: false);

        Assert.True(thrown == null,
            "The harness itself broke Clear() on a book nothing disposed. The two race tests above would "
            + "then be measuring the harness, not the defect. It threw: " + Describe(thrown));
        Assert.True(book.GetTOB(true) == null,
            "Clear() never actually ran: it was handed the write lock but the bid side still has a level. "
            + "The race tests above would then prove nothing, because the mutation they time never reaches "
            + "the code under test.");
    }

    // ---------------------------------------------------------------------------------------------
    // Guards the guard.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void LiveBook_StillAppliesClearAddAndDelete()
    {
        // Guards the guard: making the disposed path a no-op must not turn the LIVE path into one.
        using var book = new OrderBook(Symbol, PriceDecimalPlaces, MaxDepth);

        book.AddOrUpdateLevel(Delta(isBid: true, price: 100.5, size: 2.0));
        book.AddOrUpdateLevel(Delta(isBid: false, price: 101.5, size: 3.0));

        BookItem? bestBid = book.GetTOB(true);
        BookItem? bestAsk = book.GetTOB(false);
        Assert.True(bestBid != null, "AddOrUpdateLevel did not reach the live book's bid side.");
        Assert.True(bestAsk != null, "AddOrUpdateLevel did not reach the live book's ask side.");
        Assert.Equal(100.5, bestBid!.Price!.Value);
        Assert.Equal(101.5, bestAsk!.Price!.Value);

        book.DeleteLevel(Delta(isBid: false, price: 101.5, size: 0.0));
        Assert.True(book.GetTOB(false) == null, "DeleteLevel did not remove the level from the live book.");

        book.Clear();
        Assert.True(book.GetTOB(true) == null, "Clear() did not empty the live book's bid side.");

        var bids = new BookItem[MaxDepth];
        Assert.Equal(0, book.GetBidsSnapshot(bids));
    }

    [Fact]
    public void LiveBook_StillAppliesAddLevelAndUpdateLevel()
    {
        // Same guard on the two entry points that get a null re-check for the first time: a fix that
        // returns unconditionally (or on the wrong condition) would silence every direct level
        // write, which is a far worse defect than the NRE it replaces.
        using var book = new OrderBook(Symbol, PriceDecimalPlaces, MaxDepth);

        book.AddLevel(
            IsBid: true,
            EntryID: string.Empty,
            Price: 100.5,
            Size: 2.0,
            LocalTimeStamp: DateTime.Now,
            ServerTimeStamp: DateTime.Now);

        BookItem? bestBid = book.GetTOB(true);
        Assert.True(bestBid != null, "AddLevel did not reach the live book's bid side.");
        Assert.Equal(100.5, bestBid!.Price!.Value);
        Assert.Equal(2.0, bestBid.Size!.Value);

        book.UpdateLevel(
            IsBid: true,
            EntryID: string.Empty,
            Price: 100.5,
            Size: 7.0,
            LocalTimeStamp: DateTime.Now,
            ServerTimeStamp: DateTime.Now);

        BookItem? updated = book.GetTOB(true);
        Assert.True(updated != null, "UpdateLevel removed the level from the live book instead of updating it.");
        Assert.Equal(7.0, updated!.Size!.Value);
    }

    [Fact]
    public void LiveBook_UpdateLevel_OnTheAskSide_TouchesOnlyTheAsk()
    {
        // The side selector was hoisted into a local for the null re-check; this pins that the
        // selector itself still picks the ASK list for IsBid == false (a mutated selector would
        // route the update to the bid side, or throw on a null IsBid).
        using var book = NewLoadedBook();

        book.UpdateLevel(
            IsBid: false,
            EntryID: string.Empty,
            Price: 101.5,
            Size: 9.0,
            LocalTimeStamp: DateTime.Now,
            ServerTimeStamp: DateTime.Now);

        BookItem? bestAsk = book.GetTOB(false);
        BookItem? bestBid = book.GetTOB(true);
        Assert.True(bestAsk != null, "UpdateLevel on the ask side removed the ask level.");
        Assert.Equal(9.0, bestAsk!.Size!.Value);
        Assert.True(bestBid != null, "UpdateLevel on the ask side must leave the bid side alone.");
        Assert.Equal(2.0, bestBid!.Size!.Value);
    }

    [Fact]
    public void LiveBook_StillAppliesReset()
    {
        // Guards the guard for DEFECT C: a null re-check that returns on the wrong condition would turn
        // the recycle path into a silent no-op, leaving stale levels in a book about to be reused.
        using var book = NewLoadedBook();

        book.Reset();

        Assert.True(book.GetTOB(true) == null, "Reset() did not empty the live book's bid side.");
        Assert.True(book.GetTOB(false) == null, "Reset() did not empty the live book's ask side.");
    }

    [Fact]
    public void LiveBook_StillAppliesUpdateSnapshot()
    {
        // Guards the guard for DEFECT C: this is the websocket snapshot path, so a fix that returns
        // early on a live book would silently stop every reconnect from re-seeding its book.
        using var book = new OrderBook(Symbol, PriceDecimalPlaces, MaxDepth);
        BookItem[] asks = { Level(isBid: false, price: 101.5, size: 3.0) };
        BookItem[] bids = { Level(isBid: true, price: 100.5, size: 2.0) };

        book.UpdateSnapshot(asks, bids);

        BookItem? bestBid = book.GetTOB(true);
        BookItem? bestAsk = book.GetTOB(false);
        Assert.True(bestBid != null, "UpdateSnapshot did not reach the live book's bid side.");
        Assert.True(bestAsk != null, "UpdateSnapshot did not reach the live book's ask side.");
        Assert.Equal(100.5, bestBid!.Price!.Value);
        Assert.Equal(2.0, bestBid.Size!.Value);
        Assert.Equal(101.5, bestAsk!.Price!.Value);
        Assert.Equal(3.0, bestAsk.Size!.Value);
    }

    [Fact]
    public void LiveBook_StillAppliesLoadData()
    {
        // Guards the guard for DEFECT C: this is the REST snapshot path. Its return value is part of the
        // contract, so a fix that bails must not report success on a book it did not load.
        using var book = new OrderBook(Symbol, PriceDecimalPlaces, MaxDepth);
        BookItem[] asks = { Level(isBid: false, price: 101.5, size: 3.0) };
        BookItem[] bids = { Level(isBid: true, price: 100.5, size: 2.0) };

        bool loaded = book.LoadData(asks, bids);

        Assert.True(loaded, "LoadData reported failure on a live book.");
        BookItem? bestBid = book.GetTOB(true);
        BookItem? bestAsk = book.GetTOB(false);
        Assert.True(bestBid != null, "LoadData did not reach the live book's bid side.");
        Assert.True(bestAsk != null, "LoadData did not reach the live book's ask side.");
        Assert.Equal(100.5, bestBid!.Price!.Value);
        Assert.Equal(2.0, bestBid.Size!.Value);
        Assert.Equal(101.5, bestAsk!.Price!.Value);
        Assert.Equal(3.0, bestAsk.Size!.Value);
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A book in the state a reconnect actually finds it in: seeded with one level per side, so the
    /// disposal has real content to tear down and the guard is not trivially satisfied by an empty book.
    /// </summary>
    private static OrderBook NewLoadedBook()
    {
        var book = new OrderBook(Symbol, PriceDecimalPlaces, MaxDepth);
        book.AddOrUpdateLevel(Delta(isBid: true, price: 100.5, size: 2.0));
        book.AddOrUpdateLevel(Delta(isBid: false, price: 101.5, size: 3.0));
        return book;
    }

    /// <summary>
    /// Reproduces the check-then-act window deterministically, with no threads and no timing.
    ///
    /// A real dispose runs first, so <c>OrderBookData._Bids</c>/<c>_Asks</c> are genuinely null
    /// (OrderBookData.cs:255-262) — that is the only state that matters to the code under test. The
    /// private <c>_disposed</c> flag is then put back to <c>false</c>, which is exactly what a thread
    /// that already evaluated <c>Volatile.Read(ref _disposed)</c> before the dispose landed is holding.
    /// No amount of flag reading can distinguish the two, which is the point: the fix must be an in-lock
    /// null re-check of the side list, not a stronger read of the flag.
    ///
    /// The book is intentionally NOT disposed again by the test — it already was.
    /// </summary>
    private static OrderBook DisposedBookWithTheRaceWindowReopened()
    {
        var book = NewLoadedBook();
        book.Dispose();

        FieldInfo? disposedFlag = typeof(OrderBook).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(disposedFlag != null,
            "OrderBook no longer has a private bool field named '_disposed' (it was at OrderBook.cs:12). "
            + "This harness simulates the race by resetting that flag; rename it and this test measures nothing.");
        Assert.Equal(typeof(bool), disposedFlag!.FieldType);
        Assert.True((bool)disposedFlag.GetValue(book)!,
            "Dispose() did not set _disposed, so the harness never reached the state it is meant to reopen.");

        disposedFlag.SetValue(book, false);
        return book;
    }

    /// <summary>
    /// Runs <paramref name="mutate"/> against a LIVE book while a simulated dispose owns the write lock,
    /// and releases the lock only after the side lists are gone. Deterministic — there is no sleep and
    /// no timing assumption: the test thread waits on <c>ReaderWriterLockSlim.WaitingWriteCount</c>, which
    /// is a fact about the lock, not a guess about the scheduler.
    ///
    /// Sequence:
    ///   1. test thread takes the book's write lock (the same <c>ReaderWriterLockSlim</c>
    ///      <c>OrderBookData.Dispose</c> takes, OrderBookData.cs:255);
    ///   2. the mutation starts on its own thread with the book fully alive — <c>_disposed</c> false,
    ///      both side lists populated — so it passes every pre-lock guard and parks on the lock;
    ///   3. once it is provably parked, the test thread nulls <c>_Bids</c>/<c>_Asks</c>, which is what
    ///      the real teardown does under this lock (OrderBookData.cs:258-259);
    ///   4. the lock is released and the mutation resumes on the far side of its own guard.
    ///
    /// Returns whatever the mutation threw, or null. A dedicated thread (not the pool) keeps this
    /// independent of thread-pool pressure from parallel test collections. With
    /// <paramref name="teardownLandsWhileParked"/> false, step 3 is skipped — that is the positive
    /// control which shows the choreography alone harms nothing.
    /// </summary>
    private static Exception? MutateWhileTheWriteLockIsHeld(
        OrderBook book,
        Action<OrderBook> mutate,
        bool teardownLandsWhileParked)
    {
        OrderBookData data = ReadBookData(book);
        var bookLock = data.Lock as ReaderWriterLockSlim;
        Assert.True(bookLock != null,
            "OrderBookData.Lock no longer exposes the ReaderWriterLockSlim (it did at OrderBookData.cs:30). "
            + "This harness needs it to prove the mutation is parked on the write lock.");

        Exception? captured = null;
        var mutator = new Thread(() =>
        {
            try
            {
                mutate(book);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
            Name = "orderbook-mutation-under-dispose"
        };

        bool mutationStarted = false;
        bool joined = false;

        bookLock!.EnterWriteLock();
        try
        {
            mutator.Start();
            mutationStarted = true;

            var waited = Stopwatch.StartNew();
            while (bookLock.WaitingWriteCount == 0 && mutator.IsAlive && waited.Elapsed < ParkTimeout)
            {
                Thread.Yield();
            }

            Assert.True(bookLock.WaitingWriteCount > 0,
                $"The mutation never asked for the write lock within {ParkTimeout.TotalSeconds:0} s "
                + $"(thread alive: {mutator.IsAlive}). It returned before reaching the lock, so this test "
                + "measured nothing: the point is to prove the guard is taken INSIDE the lock, which "
                + "requires the mutation to get that far.");

            if (teardownLandsWhileParked)
            {
                NullTheSideLists(data);
            }
        }
        finally
        {
            // The park assertion above throws BEFORE the join below on a failure. Joining here as well
            // means a failed assertion cannot leave a live thread mutating this book — and the
            // process-wide BookItemPool it borrows from — while the next test runs. Release the lock
            // first, in its own try, or the join would wait on a thread this one is blocking.
            try
            {
                bookLock.ExitWriteLock();
            }
            finally
            {
                if (mutationStarted)
                {
                    joined = mutator.Join(JoinTimeout);
                }
            }
        }

        Assert.True(joined,
            $"The mutation did not finish within {JoinTimeout.TotalSeconds:0} s of the write lock being "
            + "released. Either the guard deadlocked on the same lock it is holding, or the lock was "
            + "never handed over.");

        return captured;
    }

    /// <summary>The book's <see cref="OrderBookData"/>, whose lock and side lists this harness drives.</summary>
    private static OrderBookData ReadBookData(OrderBook book)
    {
        FieldInfo? dataField = typeof(OrderBook).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(dataField != null,
            "OrderBook no longer has a field named '_data' (it was at OrderBook.cs:15).");
        var data = dataField!.GetValue(book) as OrderBookData;
        Assert.True(data != null, "OrderBook._data was null; the book was never constructed properly.");
        return data!;
    }

    /// <summary>
    /// The only part of <c>OrderBookData.Dispose</c> the code under test can observe: the side lists go
    /// away (OrderBookData.cs:258-259). Called with the write lock already held, as the real teardown
    /// does — so this is the production teardown's effect, not a shortcut around it.
    /// </summary>
    private static void NullTheSideLists(OrderBookData data)
    {
        foreach (string name in new[] { "_Bids", "_Asks" })
        {
            FieldInfo? field = typeof(OrderBookData).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field != null,
                $"OrderBookData no longer has a private field named '{name}' (they were at "
                + "OrderBookData.cs:57-58). This harness nulls them to reproduce what Dispose does.");
            field!.SetValue(data, null);
        }
    }

    /// <summary>
    /// The snapshot shape: one book level, as <c>LoadData</c> and <c>UpdateSnapshot</c> receive it from a
    /// REST or websocket snapshot. Not pooled — both entry points copy into their own pooled item.
    /// </summary>
    private static BookItem Level(bool isBid, double price, double size)
    {
        return new BookItem
        {
            IsBid = isBid,
            EntryID = string.Empty,
            Price = price,
            Size = size,
            Symbol = Symbol,
            PriceDecimalPlaces = PriceDecimalPlaces,
            LocalTimeStamp = DateTime.Now,
            ServerTimeStamp = DateTime.Now
        };
    }

    /// <summary>The delta shape the connectors' queue consumers hand to the book, one per venue frame.</summary>
    private static DeltaBookItem Delta(bool isBid, double price, double size)
    {
        return new DeltaBookItem
        {
            IsBid = isBid,
            EntryID = string.Empty,
            Price = price,
            Size = size,
            Symbol = Symbol,
            LocalTimeStamp = DateTime.Now,
            ServerTimeStamp = DateTime.Now
        };
    }

    private static string Describe(Exception? ex)
    {
        if (ex == null)
            return "(no exception)";
        return $"{ex.GetType().FullName}: {ex.Message}{ThrowSite(ex)}";
    }

    /// <summary>
    /// The frame the exception actually came from. A test that names a specific dereference is worth
    /// nothing if it only proves that SOMETHING threw, so every failure message here carries the throw
    /// site: if it is not the line the message names, the test is measuring the wrong thing.
    /// </summary>
    private static string ThrowSite(Exception ex)
    {
        string? trace = ex.StackTrace;
        if (string.IsNullOrEmpty(trace))
            return string.Empty;

        string[] frames = trace.Split('\n');
        string? inOrderBook = frames.FirstOrDefault(f => f.Contains("VisualHFT.Model.OrderBook", StringComparison.Ordinal));
        return "\n  thrown at: " + (inOrderBook ?? frames[0]).Trim();
    }
}
