using System;
using System.Threading;
using VisualHFT.Helpers;
using VisualHFT.Model;
using Xunit;

namespace VisualHFT.Commons.Tests
{
    /// <summary>
    /// Order book dispatch allocates nothing while no subscriber throws.
    ///
    /// <c>DispatchToSubscribers</c> runs on the market connector's producer thread for every book
    /// update. Its fault handler logs on a background task through a lambda that captures the
    /// subscriber and the exception; when that lambda sits in the loop's catch block, the compiler
    /// allocates the captured variables at the top of every loop pass, faulting or not (32 to 40 bytes
    /// per subscriber per update). The fault path is kept in its own method so the happy path is
    /// allocation-free.
    ///
    /// Each fact uses a private instance created by reflection, not the process-wide singleton, so
    /// no subscriber from another test can run (and allocate) on this thread.
    /// </summary>
    public class HelperOrderBookDispatchAllocationTests
    {
        private static int _calls;

        private static HelperOrderBook NewDispatcher() =>
            (HelperOrderBook)Activator.CreateInstance(typeof(HelperOrderBook), nonPublic: true)!;

        private static void CountingSubscriber(OrderBook book) => Interlocked.Increment(ref _calls);

        [Fact]
        public void UpdateData_WithSubscribersThatDoNotThrow_AllocatesNothing()
        {
            var dispatcher = NewDispatcher();
            Action<OrderBook> subscriber = CountingSubscriber;
            for (int i = 0; i < 4; i++)
                dispatcher.Subscribe(subscriber);
            var book = new OrderBook("DISPATCH_ALLOC", 2, 5);
            dispatcher.UpdateData(book); // warm up: first-call JIT and static initialisation

            int callsBefore = Volatile.Read(ref _calls);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
                dispatcher.UpdateData(book);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(4_000, Volatile.Read(ref _calls) - callsBefore);
            Assert.True(allocated == 0, $"1,000 dispatches to 4 subscribers allocated {allocated} bytes");
            book.Dispose();
        }

        [Fact]
        public void UpdateData_WhenASubscriberThrows_StillReportsItOnOnException_AndReachesTheNextSubscriber()
        {
            var dispatcher = NewDispatcher();
            var faulted = new ManualResetEventSlim();
            object? reportedContext = null;
            int nextCalls = 0;
            Action<OrderBook> thrower = _ => throw new InvalidOperationException("subscriber fault");
            dispatcher.OnException += e =>
            {
                reportedContext = e.Context;
                faulted.Set();
            };
            dispatcher.Subscribe(thrower);
            dispatcher.Subscribe(_ => Interlocked.Increment(ref nextCalls));
            var book = new OrderBook("DISPATCH_FAULT", 2, 5);

            dispatcher.UpdateData(book);

            Assert.Equal(1, Volatile.Read(ref nextCalls));
            Assert.True(faulted.Wait(TimeSpan.FromSeconds(10)), "the fault was not reported on OnException");
            Assert.Same(thrower.Target, reportedContext);
            book.Dispose();
        }
    }
}
