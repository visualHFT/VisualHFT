using System.Collections.Immutable;
using VisualHFT.Model;

namespace VisualHFT.Helpers
{


    public sealed class HelperOrderBook : IOrderBookHelper
    {

        private ImmutableArray<Action<OrderBook>> _subscribers = ImmutableArray<Action<OrderBook>>.Empty;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly HelperOrderBook instance = new HelperOrderBook();

        public event Action<VisualHFT.Commons.Model.ErrorEventArgs> OnException;

        public static HelperOrderBook Instance => instance;


        private HelperOrderBook()
        {

        }
        ~HelperOrderBook()
        { }



        /// <summary>
        /// Subscribes the.Limit Order Book realtime stream.
        /// Note:
        ///     - must be very careful not to block this call, and make sure to TRANSFORM the object into its minimal need to update the UI.
        ///     - the UI update must be handled in another thread, without using the object coming from this subscription (must be decoupled).
        ///     
        /// </summary>
        /// <param name="subscriber">The subscriber.</param>
        public void Subscribe(Action<OrderBook> subscriber)
        {
            // No lock needed - ImmutableInterlocked is thread-safe
            ImmutableInterlocked.Update(ref _subscribers, subs => subs.Add(subscriber));
        }
        public void Unsubscribe(Action<OrderBook> subscriber)
        {
            ImmutableInterlocked.Update(ref _subscribers, subs => subs.Remove(subscriber));
        }
        public void Reset()
        {
            ImmutableInterlocked.InterlockedExchange(ref _subscribers, ImmutableArray<Action<OrderBook>>.Empty);
        }

        private void DispatchToSubscribers(OrderBook book)
        {
            var subscribers = _subscribers;
            foreach (var subscriber in subscribers)
            {
                try
                {
                    subscriber(book);
                }
                catch (Exception ex)
                {
                    // This runs on the market connector's producer thread, at a call site that
                    // does not guard itself. An unhandled exception on a non-UI thread
                    // terminates the process, and an escaping exception would also unwind this
                    // loop, so one faulting subscriber would starve every subscriber after it
                    // of order-book data.
                    //
                    // A study plugin throwing is a normal operating condition on a hot path, not
                    // grounds for killing the host. Each subscriber is isolated and dispatch
                    // continues to the next. Isolating must not mean hiding a data outage, so the
                    // fault is logged and published on OnException, carrying the subscriber that
                    // raised it so a listener can tell whose fault it was.
                    ReportSubscriberFault(ex, subscriber);
                    // deliberately NO rethrow — continue to the next subscriber.
                }
            }
        }

        // Kept out of the dispatch loop: a lambda inside the loop's catch block makes the compiler
        // allocate its captured variables at the top of every loop pass, faulting or not (32 to 40
        // bytes per subscriber per book update). Here they are allocated only when a subscriber
        // throws. subscriber.Target is read inside the task, off the producer thread.
        private void ReportSubscriberFault(Exception ex, Action<OrderBook> subscriber)
        {
            Task.Run(() =>
            {
                log.Error(ex);
                OnException?.Invoke(new VisualHFT.Commons.Model.ErrorEventArgs(ex, subscriber.Target));
            });
        }

        public void UpdateData(OrderBook data)
        {
            DispatchToSubscribers(data);
        }
        public void UpdateData(IEnumerable<OrderBook> data)
        {
            foreach (var e in data)
            {
                DispatchToSubscribers(e);
            }
        }
    }
}
